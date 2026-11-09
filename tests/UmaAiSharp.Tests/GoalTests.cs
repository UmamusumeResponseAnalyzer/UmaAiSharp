using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Tests;

public sealed class GoalTests
{
    static readonly MarginalCurve Linear = new([new(0, 1m)]);
    static readonly AttributeValues Caps = new(2000, 2000, 2000, 2000, 2000);

    static GoalDefinition PowerGoal() => new(
        [new(TrainingAttribute.Stamina, AttributeComparison.GreaterThan, 1201),
         new(TrainingAttribute.Wisdom, AttributeComparison.GreaterThan, 1201)],
        [new([new(new AttributeUtility(TrainingAttribute.Power, Linear), 1m)])]);

    static TerminalMetrics Result(int stamina, int wisdom, int power = 1500, int speed = 1800, int? overflow = 0)
        => new(new(speed, stamina, power, 1000, wisdom), Caps, 500,
            overflow is { } known ? new(known, 0, 0, 0, 0) : null, null);

    [Fact]
    public void MarginalCurveAccumulatesInsteadOfDiscountingEarlierPoints()
    {
        var curve = new MarginalCurve([new(0, 10), new(800, 10), new(1200, 1)]);
        Assert.Equal(8000m, curve.Evaluate(800));
        Assert.Equal(8009.9775m, curve.Evaluate(801));
        Assert.Equal(1m, curve.Evaluate(1201) - curve.Evaluate(1200));
        Assert.Equal(0m, curve.Evaluate(0));
    }

    [Fact]
    public void FractionalSlopesDoNotRoundBeforeSummingTheSegment()
    {
        var curve = new MarginalCurve([new(0, 0), new(3, 1)]);
        Assert.Equal(1m, curve.Evaluate(2));
        Assert.Equal(2m, curve.Evaluate(3));
        Assert.Equal(3m, curve.Evaluate(4));
    }

    [Theory]
    [InlineData(1201, false)]
    [InlineData(1202, true)]
    public void StrictThresholdUsesActualIntegerBoundary(int stamina, bool expected)
        => Assert.Equal(expected, GoalEvaluator.Evaluate(PowerGoal(), Result(stamina, 1300)).IsFeasible);

    [Fact]
    public void JointFeasibilityUsesTheSameTrajectory()
    {
        var goal = PowerGoal();
        var paired = GoalEvaluator.Summarize(goal, [Result(1300, 1300), Result(1100, 1100)]);
        var separated = GoalEvaluator.Summarize(goal, [Result(1300, 1100), Result(1100, 1300)]);
        Assert.Equal(0.5m, paired.SuccessRate);
        Assert.Equal(0, separated.SuccessCount);
        Assert.Null(separated.ConditionalUtilities);
    }

    [Fact]
    public void SuccessRatePrecedesConditionalUtility()
    {
        var goal = PowerGoal();
        var reliable = GoalEvaluator.Summarize(goal, [Result(1300, 1300, 1000), Result(1300, 1300, 1000)]);
        var powerful = GoalEvaluator.Summarize(goal, [Result(1300, 1300, 1900), Result(1100, 1100, 1900)]);
        Assert.True(GoalEvaluator.Compare(reliable, powerful) > 0);
        Assert.Equal(1900m, powerful.ConditionalUtilities!.Value[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrioritiesAndWeightsHaveDifferentMeanings(bool noSuccess)
    {
        var speed = new WeightedMetric(new AttributeDeficit(TrainingAttribute.Speed), -1m);
        var power = new WeightedMetric(new AttributeUtility(TrainingAttribute.Power, Linear), 1m);
        var strict = new GoalDefinition(noSuccess ? [new(TrainingAttribute.Stamina, AttributeComparison.AtLeast, 1800)] : [], [new([speed]), new([power])]);
        var combined = new GoalDefinition(strict.Constraints, [new([speed, power])]);
        var fast = Result(1300, 1300, power: 1000, speed: 1900);
        var strong = Result(1300, 1300, power: 1500, speed: 1800);
        Assert.True(GoalEvaluator.Compare(GoalEvaluator.Summarize(strict, [fast]), GoalEvaluator.Summarize(strict, [strong])) > 0);
        Assert.True(GoalEvaluator.Compare(GoalEvaluator.Summarize(combined, [fast]), GoalEvaluator.Summarize(combined, [strong])) < 0);
    }

    [Fact]
    public void OverflowDistinguishesOtherwiseIdenticalTerminalStates()
    {
        var goal = new GoalDefinition([], [new([new(new AttributeOverflow(TrainingAttribute.Speed), -1)])]);
        var efficient = GoalEvaluator.Summarize(goal, [Result(1300, 1300, overflow: 5)]);
        var wasteful = GoalEvaluator.Summarize(goal, [Result(1300, 1300, overflow: 50)]);
        Assert.True(GoalEvaluator.Compare(efficient, wasteful) > 0);
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Evaluate(goal, Result(1300, 1300, overflow: null)));
    }

    [Fact]
    public void EvaluatesEachOutcomeBeforeAveraging()
    {
        var curve = new MarginalCurve([new(0, 10), new(800, 10), new(1200, 1)]);
        var goal = new GoalDefinition([], [new([new(new AttributeUtility(TrainingAttribute.Stamina, curve), 1)])]);
        var summary = GoalEvaluator.Summarize(goal, [Result(700, 1300), Result(1300, 1300)]);
        Assert.Equal((curve.Evaluate(700) + curve.Evaluate(1300)) / 2, summary.ConditionalUtilities!.Value[0]);
        Assert.NotEqual(curve.Evaluate(1000), summary.ConditionalUtilities.Value[0]);
    }

    [Fact]
    public void ZeroSuccessMinimizesMeanConstraintViolationBeforeUnconditionalUtility()
    {
        var goal = PowerGoal();
        var first = GoalEvaluator.Summarize(goal, [Result(1000, 1000, 1900)]);
        var second = GoalEvaluator.Summarize(goal, [Result(1100, 1100, 1200)]);
        Assert.Null(first.ConditionalUtilities);
        Assert.Equal(404m, first.MeanConstraintViolation);
        Assert.Equal(204m, second.MeanConstraintViolation);
        Assert.Equal(1900m, first.UnconditionalUtilities[0]);
        Assert.True(GoalEvaluator.Compare(first, second) < 0);
        var uneven = GoalEvaluator.Summarize(goal, [Result(1201, 1201), Result(1000, 1000)]);
        var even = GoalEvaluator.Summarize(goal, [Result(1102, 1102), Result(1102, 1102)]);
        Assert.Equal(203m, uneven.MeanConstraintViolation);
        Assert.Equal(200m, even.MeanConstraintViolation);
        Assert.True(GoalEvaluator.Compare(even, uneven) > 0);
        Assert.True(GoalEvaluator.Compare(second, GoalEvaluator.Summarize(goal, [Result(1300, 800)])) > 0);
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Summarize(goal, []));
    }

    [Theory]
    [InlineData(AttributeComparison.GreaterThan, 1201, 1201, 1)]
    [InlineData(AttributeComparison.GreaterThan, 1202, 1201, 0)]
    [InlineData(AttributeComparison.AtLeast, 1201, 1201, 0)]
    [InlineData(AttributeComparison.AtLeast, 1200, 1201, 1)]
    [InlineData(AttributeComparison.LessThan, 1201, 1201, 1)]
    [InlineData(AttributeComparison.LessThan, 1200, 1201, 0)]
    [InlineData(AttributeComparison.AtMost, 1201, 1201, 0)]
    [InlineData(AttributeComparison.AtMost, 1202, 1201, 1)]
    [InlineData(AttributeComparison.Equal, 1200, 1201, 1)]
    [InlineData(AttributeComparison.Equal, 1202, 1201, 1)]
    public void ViolationUsesTheNearestLegalInteger(AttributeComparison comparison, int value, int threshold, int expected)
    {
        var goal = new GoalDefinition([new(TrainingAttribute.Stamina, comparison, threshold)], []);
        var summary = GoalEvaluator.Summarize(goal, [Result(value, 1300)]);
        Assert.Equal(expected, summary.MeanConstraintViolation);
        Assert.Equal(expected == 0 ? 1 : 0, summary.SuccessCount);
    }

    [Theory]
    [InlineData(1, 0.95)]
    [InlineData(65, 0.0450422581230137)]
    [InlineData(100, 0.02951304960703993)]
    public void ZeroSuccessReportsExactOneSidedBoundWithoutChangingThePointEstimate(int samples, double expectedUpper)
    {
        var summary = GoalEvaluator.Summarize(PowerGoal(), Enumerable.Repeat(Result(1000, 1000), samples));
        Assert.Equal(0m, summary.SuccessRate);
        Assert.Null(summary.ConditionalUtilities);
        Assert.Equal(expectedUpper, summary.ZeroSuccessProbabilityUpperBound95!.Value, 12);
        Assert.Null(GoalEvaluator.Summarize(PowerGoal(), [Result(1300, 1300)]).ZeroSuccessProbabilityUpperBound95);
    }

    [Fact]
    public void GoalRoundTripPreservesMeaningAndIdentity()
    {
        var goal = PowerGoal();
        var restored = JsonSerializer.Deserialize<GoalDefinition>(JsonSerializer.Serialize(goal, GoalJson.Options), GoalJson.Options)!;
        Assert.Equal(goal.Identity, restored.Identity);
        Assert.Equal<decimal>(GoalEvaluator.Evaluate(goal, Result(1300, 1300)).Utilities,
            GoalEvaluator.Evaluate(restored, Result(1300, 1300)).Utilities);
    }

    [Fact]
    public void DoesNotCompareDifferentGoalsOrInventAttributeRatings()
    {
        var powerGoal = PowerGoal();
        var ratingGoal = new GoalDefinition([], [new([new(new AttributeRating(), 1), new(new GainedSkillPoints(), 2)])]);
        var known = Result(1300, 1300) with { VerifiedAttributeRating = 12000 };
        Assert.Equal(13000m, GoalEvaluator.Evaluate(ratingGoal, known).Utilities[0]);
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Evaluate(ratingGoal, Result(1300, 1300)));
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Compare(
            GoalEvaluator.Summarize(powerGoal, [known]), GoalEvaluator.Summarize(ratingGoal, [known])));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"speed\":0,\"stamina\":0,\"power\":0,\"guts\":0}")]
    [InlineData("{\"speed\":0,\"stamina\":0,\"power\":0,\"guts\":0,\"wisdom\":0,\"speed\":1}")]
    [InlineData("{\"speed\":0,\"stamina\":0,\"power\":0,\"guts\":0,\"wisdom\":0,\"wizdom\":1}")]
    public void JsonAttributesNeverFillMissingOrAmbiguousValues(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AttributeValues>(json, GoalJson.Options));

    [Fact]
    public void JsonGoalDoesNotDefaultMissingWeightsToZero()
    {
        const string json = """
            {"constraints":[],"priorities":[{"terms":[{"metric":{"kind":"deficit","attribute":"Speed"}}]}]}
            """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GoalDefinition>(json, GoalJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TerminalMetrics>("{}", GoalJson.Options));
    }

    [Theory]
    [InlineData("{\"points\":[{}]}")]
    [InlineData("{\"points\":[{\"value\":0}]}")]
    [InlineData("{\"points\":[{\"weight\":1}]}")]
    [InlineData("{\"points\":[{\"value\":0,\"weight\":1},{\"value\":801}]}")]
    public void JsonCurveRequiresBothCoordinates(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MarginalCurve>(json, GoalJson.Options));

    [Fact]
    public void CompiledIntegerTablesPreserveDecimalFormulaAndPriorityOrder()
    {
        var curve = new MarginalCurve([new(0, -0.7m), new(3, 1.13m), new(1201, 0.003m)]);
        var goal = new GoalDefinition([], [new([
            new(new AttributeUtility(TrainingAttribute.Speed, curve), 1.000000000000000000000000001m),
            new(new AttributeDeficit(TrainingAttribute.Speed), -0.31m)]),
            new([new(new AttributeUtility(TrainingAttribute.Stamina, curve), -0.17m)])]);
        var compiled = GoalEvaluator.Compile(goal, Caps);
        var values = new decimal[compiled.PriorityCount];
        for (var value = 0; value <= 2000; value++)
        {
            var result = Result(value, 1300, speed: value);
            Assert.True(compiled.Evaluate(result, values));
            Assert.Equal(GoalEvaluator.Evaluate(goal, result).Utilities.ToArray(), values);
        }
        var observations = new[] { Result(700, 1300), Result(1300, 1300), Result(1999, 1300) };
        var baseline = GoalEvaluator.Summarize(goal, observations);
        var actual = GoalEvaluator.Summarize(compiled, observations);
        Assert.Equal(baseline.SuccessCount, actual.SuccessCount);
        Assert.True(baseline.ConditionalUtilities!.Value.SequenceEqual(actual.ConditionalUtilities!.Value));
        Assert.True(compiled.SharedMemoryBytes > 2000 * 16);
        Assert.Throws<ArgumentException>(() => compiled.Evaluate(Result(2001, 1300) with { Caps = new(3000, 3000, 3000, 3000, 3000) }, values));
    }

    [Fact]
    public void BatchGoalEvaluationRejectsStoppedAndRenumberedSamples()
    {
        var goal = GoalEvaluator.Compile(PowerGoal(), Caps);
        var result = Result(1300, 1300);
        var provenance = new SimulationProvenance("test", 1, SimulationRandom.Algorithm, SimulationRandom.SeedDerivation);
        var slots = new[]
        {
            new TrajectoryResult<TerminalMetrics>(TrajectoryStatus.Completed, 0, default, result, null, default),
            new TrajectoryResult<TerminalMetrics>(TrajectoryStatus.Stopped, 1, default, null!, new(SimulationStopReason.Cancelled, 0), default)
        };
        var batch = new SimulationBatch<TerminalMetrics>(provenance, slots, default);
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Summarize(goal, batch));
        slots[1] = slots[0] with { SampleIndex = 2 };
        Assert.Throws<ArgumentException>(() => GoalEvaluator.Summarize(goal, batch));
        slots[1] = slots[0] with { SampleIndex = 1 };
        Assert.Equal(2, GoalEvaluator.Summarize(goal, batch).SampleCount);
    }
}
