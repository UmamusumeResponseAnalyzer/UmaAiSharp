using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace UmaAiSharp.Core.Goals;

public sealed record GoalSample(bool IsFeasible, ImmutableArray<decimal> Utilities);
public sealed record GoalSummary(
    string GoalIdentity,
    int SampleCount,
    int SuccessCount,
    ImmutableArray<decimal>? ConditionalUtilities,
    decimal MeanConstraintViolation,
    ImmutableArray<decimal> UnconditionalUtilities,
    decimal? MeanSuccessfulAttributeRating = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int FailedCareerSampleCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? MeanSuccessfulFutureSkillRating = null)
{
    public decimal SuccessRate => (decimal)SuccessCount / SampleCount;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MeanSuccessfulTotalRating => MeanSuccessfulAttributeRating + MeanSuccessfulFutureSkillRating;
    // Exact one-sided binomial bound: P(0 successes | p) = (1-p)^n = 0.05.
    public double? ZeroSuccessProbabilityUpperBound95 => SuccessCount == 0 ? 1 - Math.Pow(0.05, 1.0 / SampleCount) : null;
}

public static class GoalEvaluator
{
    public static CompiledGoal Compile(GoalDefinition goal, AttributeValues maximumAttributes)
        => new(goal, maximumAttributes);

    public static GoalSample Evaluate(GoalDefinition goal, TerminalMetrics result)
    {
        var values = new decimal[goal.Compiled.PriorityCount];
        var feasible = goal.Compiled.Evaluate(result, values);
        return new(feasible, feasible ? values.ToImmutableArray() : []);
    }

    public static GoalSummary Summarize(GoalDefinition goal, IEnumerable<TerminalMetrics> results)
        => Summarize(goal.Compiled, results);

    public static GoalSummary Summarize(CompiledGoal goal, IEnumerable<TerminalMetrics> results)
    {
        var count = 0;
        var successes = 0;
        var failedCareer = 0;
        var totals = new decimal[goal.PriorityCount];
        var unconditionalTotals = new decimal[goal.PriorityCount];
        var violationTotal = 0m;
        long? ratingTotal = 0;
        long? skillRatingTotal = 0;
        foreach (var result in results)
        {
            if (goal.Accumulate(result, totals, unconditionalTotals, ref violationTotal))
            {
                successes = checked(successes + 1);
                ratingTotal += result.VerifiedAttributeRating;
                skillRatingTotal += result.EstimatedFutureSkillRating;
            }
            if (result.FailedCareerGoalId.HasValue) failedCareer++;
            count = checked(count + 1);
        }
        return SummarizeTotals(goal, count, successes, totals, unconditionalTotals, violationTotal, ratingTotal,
            failedCareer, skillRatingTotal);
    }

    public static GoalSummary Summarize(CompiledGoal goal, SimulationBatch<TerminalMetrics> batch)
    {
        // No stopped slot is silently removed from the requested fixed workload.
        for (var index = 0; index < batch.Results.Length; index++)
            if (batch.Results[index].Status != TrajectoryStatus.Completed || batch.Results[index].SampleIndex != index)
                throw new ArgumentException("目标评价要求全部固定样本完成且保留原始编号，不能筛除停止样本。", nameof(batch));
        var successes = 0;
        var failedCareer = 0;
        var totals = new decimal[goal.PriorityCount];
        var unconditionalTotals = new decimal[goal.PriorityCount];
        var violationTotal = 0m;
        long? ratingTotal = 0;
        long? skillRatingTotal = 0;
        foreach (ref readonly var sample in batch.Results.AsSpan())
        {
            if (goal.Accumulate(sample.Outcome, totals, unconditionalTotals, ref violationTotal))
            {
                successes = checked(successes + 1);
                ratingTotal += sample.Outcome.VerifiedAttributeRating;
                skillRatingTotal += sample.Outcome.EstimatedFutureSkillRating;
            }
            if (sample.Outcome.FailedCareerGoalId.HasValue) failedCareer++;
        }
        return SummarizeTotals(goal, batch.Results.Length, successes, totals, unconditionalTotals, violationTotal,
            ratingTotal, failedCareer, skillRatingTotal);
    }

    static GoalSummary SummarizeTotals(CompiledGoal goal, int count, int successes, decimal[] totals,
        decimal[] unconditionalTotals, decimal violationTotal, long? ratingTotal, int failedCareer, long? skillRatingTotal)
    {
        if (count == 0) throw new ArgumentException("没有完整轨迹，无法形成达标率和条件效用。");
        for (var index = 0; index < totals.Length; index++)
        {
            if (successes != 0) totals[index] /= successes;
            unconditionalTotals[index] /= count;
        }
        return new(goal.Identity, count, successes, successes == 0 ? null : totals.ToImmutableArray(),
            violationTotal / count, unconditionalTotals.ToImmutableArray(), successes == 0 ? null : ratingTotal / (decimal)successes,
            failedCareer, successes == 0 ? null : skillRatingTotal / (decimal)successes);
    }

    public static int Compare(GoalSummary left, GoalSummary right, bool rankByTotalRating = false)
    {
        if (left.GoalIdentity != right.GoalIdentity)
            throw new ArgumentException("不同目标的结果不能直接比较。");
        var feasibility = ((long)left.SuccessCount * right.SampleCount)
            .CompareTo((long)right.SuccessCount * left.SampleCount);
        if (feasibility != 0) return feasibility;
        if (rankByTotalRating && left.SuccessCount != 0)
        {
            if (left.MeanSuccessfulTotalRating is not { } leftRating || right.MeanSuccessfulTotalRating is not { } rightRating)
                throw new ArgumentException("达标轨迹缺少终局五维评分或新增技能评分。");
            return leftRating.CompareTo(rightRating);
        }
        if (left.SuccessCount == 0)
        {
            var career = ((long)right.FailedCareerSampleCount * left.SampleCount)
                .CompareTo((long)left.FailedCareerSampleCount * right.SampleCount);
            if (career != 0) return career;
            var proximity = right.MeanConstraintViolation.CompareTo(left.MeanConstraintViolation);
            if (proximity != 0) return proximity;
        }
        var leftValues = left.SuccessCount == 0 ? left.UnconditionalUtilities : left.ConditionalUtilities!.Value;
        var rightValues = right.SuccessCount == 0 ? right.UnconditionalUtilities : right.ConditionalUtilities!.Value;
        for (var i = 0; i < leftValues.Length; i++)
        {
            var comparison = leftValues[i].CompareTo(rightValues[i]);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }
}
