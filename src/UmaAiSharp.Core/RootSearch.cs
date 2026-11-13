using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Core;

public readonly record struct RootCandidate<TAction>(int Id, TAction Action) where TAction : notnull;
public sealed record CandidateEvaluation<TAction>(RootCandidate<TAction> Candidate,
    SimulationBatch<TerminalMetrics> Batch, GoalSummary? Summary) where TAction : notnull;
public enum SearchStatus { Completed, Stopped, Cancelled }
public sealed record RootSearchResult<TAction>(DecisionId Decision, SearchStatus Status,
    int? RecommendedCandidateId, int[] Ranking, CandidateEvaluation<TAction>[] Candidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FirstStepPromotedCandidateId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SampleLimitPerCandidate = null) where TAction : notnull;

public static class RootSearch
{
    const int MinimumPairedSamples = 32;
    const int PairedSampleLimit = 4096;

    // Candidates share sample seeds; their identity and enumeration order do not change future draws.
    public static RootSearchResult<TAction> Evaluate<TState, TDecision, TAction, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TerminalMetrics> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> continuation, in TState root,
        ReadOnlySpan<RootCandidate<TAction>> candidates, CompiledGoal goal,
        ulong seed, int samplesPerCandidate, int workers, CancellationToken cancellationToken = default,
        long? ownedActionPayloadBytes = null, bool rankByTotalRating = false,
        Func<TState, TAction, TAction, bool>? hasFirstStepAdvantage = null)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerCandidate);
        if (workers is < 1 or > ExecutionMemoryPlan.WorkerCapacity)
            throw new ArgumentOutOfRangeException(nameof(workers), "worker数量必须为1..32。");
        if (candidates.IsEmpty) throw new ArgumentException("搜索至少需要一个候选行动。", nameof(candidates));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TAction>() && ownedActionPayloadBytes is null)
            throw new ArgumentException("引用类型候选行动必须声明其不可变负载容量。", nameof(ownedActionPayloadBytes));
        if (ownedActionPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(ownedActionPayloadBytes));
        var inspection = scenario.Inspect(root, 0);
        if (inspection.Kind != InspectionKind.Decision)
            throw new ArgumentException("搜索起点必须处于待选择的决策。", nameof(root));
        var actions = candidates.ToArray();
        var identities = new HashSet<int>();
        foreach (var candidate in actions)
        {
            if (candidate.Id < 0 || !identities.Add(candidate.Id))
                throw new ArgumentException("候选编号必须非负且唯一。", nameof(candidates));
            if (scenario.ValidateAction(root, candidate.Action) is { } invalid)
                throw new ArgumentException($"候选{candidate.Id}校验失败：{invalid.Reason}，代码{invalid.Code}，位置{invalid.Position}。", nameof(candidates));
        }
        var paired = rankByTotalRating && hasFirstStepAdvantage is not null && actions.Length > 1;
        var sampleLimit = paired ? Math.Max(samplesPerCandidate, PairedSampleLimit) : samplesPerCandidate;
        var resultSize = Unsafe.SizeOf<TrajectoryResult<TerminalMetrics>>();
        var retainedResultBytes = checked((long)actions.Length * sampleLimit * (resultSize + 128L));
        // Extending a candidate holds its previous result array while copying the prefix.
        if (sampleLimit > samplesPerCandidate) retainedResultBytes += (long)sampleLimit * resultSize;
        var sharedBytes = checked(goal.SharedMemoryBytes + (ownedActionPayloadBytes ?? 0) + 128L
            + actions.Length * (64L + Unsafe.SizeOf<RootCandidate<TAction>>()) + retainedResultBytes
            + (paired ? actions.Length * (long)actions.Length * 32 : 0));
        var initialPolicy = new RootPolicy<TDecision, TAction, TCursor>(continuation, inspection.Decision.Id, actions[0].Action, sharedBytes);
        var (memory, workspaces) = SimulationRunner.CreateBatchWorkspaces(scenario, initialPolicy, root, sampleLimit, workers,
            samplesPerWorkspace: 1);
        var results = new CandidateEvaluation<TAction>[actions.Length];
        var active = Enumerable.Range(0, actions.Length).ToList();
        var retired = new List<int>();
        var firstStep = new HashSet<(int Preferred, int Other)>();
        if (paired)
            for (var i = 0; i < actions.Length; i++)
                for (var j = 0; j < actions.Length; j++)
                    if (i != j && hasFirstStepAdvantage!(root, actions[i].Action, actions[j].Action))
                        firstStep.Add((i, j));
        Comparison<int> compare = (left, right) =>
        {
            var comparison = GoalEvaluator.Compare(results[right].Summary!, results[left].Summary!, rankByTotalRating);
            return comparison != 0 ? comparison : results[left].Candidate.Id.CompareTo(results[right].Candidate.Id);
        };
        var samples = samplesPerCandidate;
        var status = SearchStatus.Completed;
        while (true)
        {
            foreach (var index in active)
            {
                var policy = new RootPolicy<TDecision, TAction, TCursor>(continuation, inspection.Decision.Id, actions[index].Action, sharedBytes);
                var batch = SimulationRunner.RunBatch(scenario, policy, root, seed, samples, memory, workspaces,
                    cancellationToken, results[index]?.Batch.Results);
                GoalSummary? summary = null;
                if (Array.TrueForAll(batch.Results, sample => sample.Status == TrajectoryStatus.Completed))
                {
                    summary = GoalEvaluator.Summarize(goal, batch);
                    if (rankByTotalRating && summary.SuccessCount != 0 && summary.MeanSuccessfulTotalRating is null)
                        throw new InvalidDataException($"候选{actions[index].Id}的达标轨迹缺少终局五维评分或新增技能评分。");
                }
                else if (Array.Exists(batch.Results, sample => sample.Stop?.Reason == SimulationStopReason.Cancelled))
                    status = SearchStatus.Cancelled;
                else if (status != SearchStatus.Cancelled)
                    status = SearchStatus.Stopped;
                results[index] = new(actions[index], batch, summary);
            }
            if (status != SearchStatus.Completed)
                return new(inspection.Decision.Id, status, null, [], results, SampleLimitPerCandidate: paired ? sampleLimit : null);

            active.Sort(compare);
            if (samples == sampleLimit || active.Count == 1) break;
            // Keep every candidate through the last unresolved comparison with a contender.
            var keep = 1;
            for (var i = 0; i < keep; i++)
            {
                for (var j = active.Count - 1; j >= keep; j--)
                {
                    if (PairedLeadExceedsNoise(results[active[i]], results[active[j]], goal,
                        firstStep.Contains((active[j], active[i])))) continue;
                    keep = j + 1;
                    break;
                }
            }
            if (keep < active.Count)
            {
                retired.InsertRange(0, active.GetRange(keep, active.Count - keep));
                active.RemoveRange(keep, active.Count - keep);
            }
            if (active.Count == 1) break;
            samples = (int)Math.Min(sampleLimit, 4L * Math.Max(MinimumPairedSamples, samples));
        }

        var ranking = new List<int>(actions.Length);
        int? firstStepPromotedCandidateId = null;
        if (paired && active.Count > 1)
        {
            var count = active.Count;
            var blocked = new List<int>?[count];
            var blockers = new int[count];
            for (var i = 0; i < count; i++)
            {
                var leader = results[active[i]];
                if (leader.Summary!.SuccessCount != samples) break;
                for (var j = i + 1; j < count; j++)
                {
                    var preferred = results[active[j]];
                    if (preferred.Summary!.SuccessCount != samples) break;
                    if (!firstStep.Contains((active[j], active[i]))
                        || HasReplicatedRatingLead(leader.Batch.Results, preferred.Batch.Results)) continue;
                    (blocked[j] ??= []).Add(i);
                    blockers[i]++;
                }
            }
            // Resolve challenges to the sampled leader before considering unrelated candidates.
            var challengesLeader = new bool[count];
            challengesLeader[0] = true;
            for (var i = 1; i < count; i++)
                if (blocked[i] is { } opponents)
                    foreach (var opponent in opponents)
                        if (challengesLeader[opponent])
                        {
                            challengesLeader[i] = true;
                            break;
                        }
            var winner = 0;
            while (blockers[winner] != 0 || !challengesLeader[winner]) winner++;
            // Preserve goal order for the remaining candidates wherever protection permits it.
            for (var i = 0; i < count; i++)
            {
                var next = i == 0 ? winner : Array.IndexOf(blockers, 0);
                ranking.Add(active[next]);
                blockers[next] = -1;
                if (blocked[next] is not { } released) continue;
                foreach (var j in released) blockers[j]--;
                firstStepPromotedCandidateId ??= actions[active[next]].Id;
            }
        }
        else ranking.AddRange(active);
        ranking.AddRange(retired);
        return new(inspection.Decision.Id, status, results[ranking[0]].Candidate.Id,
            [.. ranking.Select(index => results[index].Candidate.Id)], results, firstStepPromotedCandidateId,
            paired ? sampleLimit : null);
    }

    static bool HasReplicatedRatingLead(ReadOnlySpan<TrajectoryResult<TerminalMetrics>> leader,
        ReadOnlySpan<TrajectoryResult<TerminalMetrics>> preferred)
    {
        if (leader.Length < MinimumPairedSamples) return false;
        var split = leader.Length / 2;
        return Confirmed(leader[..split], preferred[..split]) && Confirmed(leader[split..], preferred[split..]);

        static bool Confirmed(ReadOnlySpan<TrajectoryResult<TerminalMetrics>> a, ReadOnlySpan<TrajectoryResult<TerminalMetrics>> b)
        {
            var stats = new PairedDifference();
            for (var i = 0; i < a.Length; i++)
                stats.Add(TotalRating(a[i].Outcome) - TotalRating(b[i].Outcome));
            return stats.HasLead;
        }
    }

    static bool PairedLeadExceedsNoise<TAction>(CandidateEvaluation<TAction> leader, CandidateEvaluation<TAction> other,
        CompiledGoal goal, bool protectFirstStep)
        where TAction : notnull
    {
        var left = leader.Batch.Results;
        var right = other.Batch.Results;
        var count = left.Length;
        if (count < MinimumPairedSamples) return false;
        var allFeasible = leader.Summary!.SuccessCount == count && other.Summary!.SuccessCount == count;
        if (allFeasible && protectFirstStep) return HasReplicatedRatingLead(left, right);
        if (!allFeasible && leader.Summary.SuccessCount <= other.Summary!.SuccessCount) return false;
        var stats = new PairedDifference();
        for (var i = 0; i < count; i++)
        {
            var a = left[i].Outcome;
            var b = right[i].Outcome;
            stats.Add(allFeasible ? TotalRating(a) - TotalRating(b)
                : (goal.IsFeasible(a) ? 1 : 0) - (goal.IsFeasible(b) ? 1 : 0));
        }
        return stats.HasLead;
    }

    static long TotalRating(TerminalMetrics outcome)
        => (long)outcome.VerifiedAttributeRating!.Value + outcome.EstimatedFutureSkillRating!.Value;

    struct PairedDifference
    {
        int count;
        double mean;
        double squared;

        public void Add(long difference)
        {
            var delta = difference - mean;
            mean += delta / ++count;
            squared += delta * (difference - mean);
        }

        public readonly bool HasLead => mean > 3 * Math.Sqrt(squared / (count * (count - 1.0)));
    }

    struct RootCursor<TCursor>
    {
        public bool Selected;
        public TCursor Continuation;
    }

    sealed class RootPolicy<TDecision, TAction, TCursor>(ITrajectoryPolicy<TDecision, TAction, TCursor> continuation,
        DecisionId decision, TAction action, long searchBytes) : ITrajectoryPolicy<TDecision, TAction, RootCursor<TCursor>>
        where TDecision : notnull where TAction : notnull
    {
        public long SharedMemoryBytes => checked(continuation.SharedMemoryBytes + searchBytes);
        public RootCursor<TCursor> CreateCursor() => new() { Continuation = continuation.CreateCursor() };
        public long GetCursorBytes(in RootCursor<TCursor> cursor) => continuation.GetCursorBytes(cursor.Continuation);
        public void ResetCursor(ref RootCursor<TCursor> cursor, int sampleIndex)
        {
            cursor.Selected = false;
            continuation.ResetCursor(ref cursor.Continuation, sampleIndex);
        }
        public void CopyCursor(in RootCursor<TCursor> source, ref RootCursor<TCursor> destination)
        {
            destination.Selected = source.Selected;
            continuation.CopyCursor(source.Continuation, ref destination.Continuation);
        }
        public PolicySelection<TAction> ChooseAction(in DecisionPoint<TDecision> current,
            ref RootCursor<TCursor> cursor, ref SimulationRandom random)
        {
            if (cursor.Selected) return continuation.ChooseAction(current, ref cursor.Continuation, ref random);
            if (current.Id != decision) throw new InvalidOperationException("搜索副本的根决策身份发生变化。");
            cursor.Selected = true;
            return PolicySelection<TAction>.Selected(action);
        }
        public SimulationStop? ValidateCompletion(in RootCursor<TCursor> cursor) => continuation.ValidateCompletion(cursor.Continuation);
    }
}
