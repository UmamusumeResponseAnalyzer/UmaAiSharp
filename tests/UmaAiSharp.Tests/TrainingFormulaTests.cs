using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingFormulaTests
{
    [Theory]
    [InlineData(528, 35, 20, 2, 26)]
    [InlineData(536, 47, 0, -2, 7)]
    [InlineData(520, 0, 0, 0, 99)]
    public void GeneratesObservedDisplayRatesWithBiasAfterCeiling(int coefficient, int vital, int reduction, int bias, int expected)
        => Assert.Equal(expected, TrainingFailureRate.Calculate(coefficient, vital, [reduction], bias));

    [Fact]
    public void ResolvesKnownStatusEffectsAndRejectsAnUnknownEffect()
    {
        Assert.Equal(-2, TrainingFailureRate.StatusBias([10, 26]));
        Assert.Equal(2, TrainingFailureRate.StatusBias([12, 26]));
        Assert.Equal(-2, TrainingFailureRate.StatusBias([13, 26]));
        Assert.Equal(-4, TrainingFailureRate.StatusBias([11, 3]));
        Assert.Equal(0, TrainingFailureRate.StatusBias([3, 5, 27]));
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureRate.StatusBias([999]));
    }

    [Fact]
    public void FriendshipEligibilityIsDeterministicAndDistinctFromBondConditionalUniqueEffects()
    {
        var card = new TrainingSupport(30260, 50, 1, 101, 79, [new(1, 30)], [new(101, 80, 3, 2, 0, 0)]);

        var lowBond = TrainingSupportEffects.Resolve(card, 101, 1, 0);
        var eligible = TrainingSupportEffects.Resolve(card with { Bond = 80 }, 101, 1, 0);
        var otherTraining = TrainingSupportEffects.Resolve(card with { Bond = 80 }, 105, 1, 0);

        Assert.False(lowBond.FriendshipEligible);
        Assert.Equal(0, lowBond.BaseBonuses.Speed);
        Assert.True(eligible.FriendshipEligible);
        Assert.Equal(2, eligible.BaseBonuses.Speed);
        Assert.False(otherTraining.FriendshipEligible);
        Assert.Equal(2, otherTraining.BaseBonuses.Speed);
        Assert.Throws<UnknownMechanismException>(() => TrainingSupportEffects.Resolve(
            card with { UniqueEffects = [new(999, 1, 0, 0, 0, 0)] }, 101, 1, 0));
    }

    [Fact]
    public void QuantizesRealIntegerBoundaryWhilePreservingTheUnquantizedExactRational()
    {
        // packets-0002/4450: the raw product is 22.99968; refit-14 produces the observed 23.
        var input = new TrainingFormulaInput(105, 2, 5, 18196, 2,
            new(606, 422, 454, 225, 341, 34, 819), new(1859, 1700, 1708, 1618, 1309, 108),
            new(20, 10, 0, 0, 0, 0, 0),
            [new(30107, 50, 1, 101, 100, [new(1, 25), new(2, 30), new(3, 1), new(5, 1)], [new(111, 8, 5, 0, 0, 0)]),
                new(30257, 50, 6, 0, 100, [new(2, 30), new(4, 1), new(6, 1), new(8, 10), new(27, 20), new(28, 20)], [new(122, 19, 60, 0, 0, 0)])], 20, null);

        var prediction = TrainingFormulaCandidate.GenerateOrdinary(input);
        var exact = prediction.RawGains.Single(value => value.TargetType == 2);

        Assert.Equal(23, prediction.Common.Stamina);
        Assert.Equal("71874", exact.Numerator);
        Assert.Equal("3125", exact.Denominator);
        Assert.Equal(-16, prediction.Common.Vital);
        var numeric = TrainingNumericKernel.GenerateOrdinary(input);
        Assert.Equal((prediction.Common, prediction.Scenario), (numeric.Common, numeric.Scenario));
        var compiled = TrainingNumericKernel.GenerateOrdinary(input, new(input.Supports));
        Assert.Equal(numeric, compiled);
    }

    [Theory]
    [InlineData(1190, 1800, 11, 6)]
    [InlineData(1200, 1800, 6, 6)]
    [InlineData(1189, 1200, 11, 0)]
    public void SplitsEachPreviewLayerAt1200BeforeApplyingTheAttributeLimit(int speed, int cap, int common, int scenario)
    {
        var input = new TrainingFormulaInput(101, 1, 3, 0, 0,
            new(speed, 100, 1200, 100, 100, 50, 0), new(cap, 1800, 1800, 1800, 1800, 100),
            new(0, 0, 0, 0, 0, 0, 0), [], 100, null);

        var prediction = TrainingFormulaCandidate.GenerateOrdinary(input);

        Assert.Equal(common, prediction.Common.Speed);
        Assert.Equal(scenario, prediction.Scenario.Speed);
        Assert.Equal(1, prediction.Common.Power);
        Assert.Equal(1, prediction.Scenario.Power);
        var numeric = TrainingNumericKernel.GenerateOrdinary(input);
        Assert.Equal((prediction.Common, prediction.Scenario), (numeric.Common, numeric.Scenario));
    }

}
