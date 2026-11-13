using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerPlacementTests
{
    static PlacementInput Input() => new(PlacementMemberKind.Training, 0, false, 0,
        0, 0, 0, 0, 0, 0, 0, false, 5, []);

    [Fact]
    public void CompiledProbabilitiesUseResearchCountsAndSeparateHintFixedTerm()
    {
        var ordinary = new PioneerPlacementDistribution(Input());
        Assert.Equal(new ulong[] { 1999, 1999, 1999, 1999, 1999, 1142 }, ordinary.Weights);
        Assert.Equal(3UL, ordinary.HintNumerator);
        Assert.Equal(40UL, ordinary.HintDenominator);
        var boosted = new PioneerPlacementDistribution(Input() with
        {
            SpecialtyPercent = 50, UniqueSpecialtyPercent = 20, FacilitySpecialtyPercent = 80,
            HintPercent = 40, UniqueHintPercent = 20, CheckpointHintPercent = 15, FacilityHintPercent = 20,
            TemporaryEffects = [new(4, 20), new(4, 60), new(3, 120)]
        });
        Assert.Equal(6.2208m, (decimal)boosted.Weights[0] / boosted.Weights[1]);
        Assert.Equal(0.14715m, (decimal)boosted.HintNumerator / boosted.HintDenominator);
        var expired = new PioneerPlacementDistribution(Input() with { TemporaryEffects = [new(3, 120), new(5, 60)] });
        Assert.Equal(ordinary.Weights.ToArray(), expired.Weights.ToArray());
    }

    [Fact]
    public void AppearanceAndGuaranteedHintConsumeOnlyTheirApplicableRandomDraws()
    {
        var distribution = new PioneerPlacementDistribution(Input() with { GuaranteedHint = true });
        var random = new SimulationRandom(987);
        Assert.Equal(new SupportPlacement(-1, false), distribution.Sample(false, ref random));
        Assert.Equal(987UL, random.State);
        var expected = random;
        expected.NextUInt64(distribution.TotalWeight);
        var value = distribution.Sample(true, ref random);
        Assert.Equal(value.TrainingIndex >= 0, value.HasHint);
        Assert.Equal(expected.State, random.State);
        var friend = new PioneerPlacementDistribution(Input() with { Kind = PlacementMemberKind.Tucker, SpecialtyIndex = -1 });
        Assert.Equal(0UL, friend.HintNumerator);
        Assert.Equal(1UL, friend.HintDenominator);
        Assert.Equal(friend.Weights[0], friend.Weights[4]);
    }

}
