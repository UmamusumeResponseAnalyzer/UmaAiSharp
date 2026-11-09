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

    [Theory]
    [InlineData(false, 0, 571, 3998)]
    [InlineData(false, 10, 3547, 28099)]
    [InlineData(true, 10, 357, 4811)]
    public void PlacementSamplingMatchesCompiledMassAndRecomputesExactly(bool camp, int sea, int absent, int other)
    {
        var distribution = new PioneerPlacementDistribution(Input() with { Camp = camp, SeaHousePercent = sea });
        Assert.Equal((decimal)(400 * absent) / other, (decimal)distribution.Weights[5] * 100 / distribution.Weights[1]);
        var random = new SimulationRandom(123);
        var repeated = random;
        var counts = new int[6];
        var hints = 0;
        const int samples = 100_000;
        for (var sample = 0; sample < samples; sample++)
        {
            var value = distribution.Sample(true, ref random);
            Assert.Equal(value, distribution.Sample(true, ref repeated));
            counts[value.TrainingIndex == -1 ? 5 : value.TrainingIndex]++;
            if (value.HasHint) hints++;
        }
        Assert.Equal(random.State, repeated.State);
        for (var index = 0; index < 6; index++)
        {
            var probability = (double)distribution.Weights[index] / distribution.TotalWeight;
            Assert.InRange(Math.Abs(counts[index] - samples * probability), 0, 6 * Math.Sqrt(samples * probability * (1 - probability)));
        }
        var present = samples - counts[5];
        Assert.InRange(Math.Abs(hints - present * 0.075), 0, 6 * Math.Sqrt(present * 0.075 * 0.925));
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

    [Fact]
    public void WideRandomDrawUsesTheSameRejectionSequenceAsExistingIntegerDraws()
    {
        foreach (var bound in new[] { 1, 2, 127, int.MaxValue })
        {
            var narrow = new SimulationRandom(42);
            var wide = narrow;
            for (var index = 0; index < 100; index++) Assert.Equal((ulong)narrow.NextInt(bound), wide.NextUInt64((ulong)bound));
            Assert.Equal(narrow.State, wide.State);
        }
        var random = new SimulationRandom(0);
        var reference = random;
        const ulong large = (1UL << 63) + 1;
        var threshold = unchecked(0UL - large) % large;
        for (var index = 0; index < 100; index++)
        {
            var value = reference.NextUInt64();
            while (value < threshold) value = reference.NextUInt64();
            Assert.Equal(value % large, random.NextUInt64(large));
        }
        Assert.Equal(reference.State, random.State);
    }
}
