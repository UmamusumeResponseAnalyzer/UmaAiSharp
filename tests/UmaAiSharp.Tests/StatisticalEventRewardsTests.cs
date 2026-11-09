using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class StatisticalEventRewardsTests
{
    [Fact]
    public void SourceBonusesAreRemovedBeforeAnotherCardsBonusesAreApplied()
    {
        var source = new TrainingSupport(30257, 50, 6, 0, 90, [new(25, 60), new(26, 30)], []);
        var target = source with { CardId = 10128, Level = 40, Effects = [new(25, 30), new(26, 20)] };
        var from = EventEffectScaling.FromSupport(source);
        var to = EventEffectScaling.FromSupport(target);
        Assert.Equal(20, from.InferUncappedBase(10, 32));
        Assert.Equal(10, from.InferUncappedBase(30, 13));
        Assert.Equal(26, to.Apply(10, 20));
        Assert.Equal(12, to.Apply(30, 10));
        Assert.Equal(-10, to.Apply(10, -10));
        Assert.Equal(1, to.Apply(20, 1));
        Assert.Null(from.InferUncappedBase(10, 31));
        for (var bonus = 0; bonus <= 200; bonus++)
        {
            var scaling = new EventEffectScaling(bonus, bonus);
            for (var basis = 0; basis <= 200; basis++)
            {
                var observed = scaling.Apply(1, basis);
                Assert.Equal(basis, scaling.InferUncappedBase(1, observed));
            }
        }
    }

    [Fact]
    public void UnknownCardUsesOnlyMatchingFamilyAndProgressWithoutTheInputRun()
    {
        var family = new EventRewardFamily(1, 101, 10002, 6, 1, 2, 1);
        var samples = ImmutableArray.Create(
            new EventRewardSample("first", 100, family, [new(1, 1, 10, 0)], []),
            new EventRewardSample("second", 101, family, [new(1, 1, 20, 0)], []),
            new EventRewardSample("input", 999, family, [new(1, 1, 1000, 0)], []),
            new EventRewardSample("third", 102, family with { Progress = 3 }, [new(1, 1, 999, 0)], []));
        var model = new StatisticalEventRewards(samples);
        var estimate = model.Estimate(999, family, "input");
        Assert.True(estimate.UsesFamilyEstimate);
        Assert.Equal(2, estimate.SampleCount);
        Assert.Equal(2, estimate.RunCount);
        var compiled = new CompiledStatisticalEventRewards(estimate, default);
        var baseline = Draw(compiled, 123);
        Parallel.For(0, 32, _ =>
        {
            var actual = Draw(compiled, 123);
            Assert.Equal(baseline.Values, actual.Values);
            Assert.Equal(baseline.Random, actual.Random);
        });
        Assert.Contains(10, baseline.Values);
        Assert.Contains(20, baseline.Values);
        Assert.All(baseline.Values, value => Assert.True(value is 10 or 20));
        var exact = model.Estimate(100, family, "input");
        Assert.False(exact.UsesFamilyEstimate);
        var random = new SimulationRandom(123);
        var old = random;
        Assert.Equal(10, new CompiledStatisticalEventRewards(exact, default).Sample(ref random).Resources.Effects[0].Value1);
        Assert.Equal(old, random);
    }

    [Fact]
    public void UnexplainedAtomsRemainSeparateFromTheRescaledResourceComponent()
    {
        var family = new EventRewardFamily(2, 0, 21174, 1, 0, 0, 1);
        var model = new StatisticalEventRewards([new("source", 809049004, family,
            [new(1, 10, 20, 0), new(1, 30, 10, 0)], [new(11, 9049, 0, 0), new(9, 26, 0, 0)])]);
        var sample = new CompiledStatisticalEventRewards(model.Estimate(809049004, family, "input"), new(30, 20));
        var random = new SimulationRandom(1);
        var result = sample.Sample(ref random);
        Assert.Equal([new DeclaredEventEffect(1, 10, 26, 0), new(1, 30, 12, 0)], result.Resources.Effects.ToArray());
        Assert.Equal([new DeclaredEventEffect(11, 9049, 0, 0), new(9, 26, 0, 0)], result.RemainingEffects.ToArray());
    }

    static (int[] Values, ulong Random) Draw(CompiledStatisticalEventRewards compiled, ulong seed)
    {
        var random = new SimulationRandom(seed);
        var result = new int[65];
        for (var i = 0; i < result.Length; i++) result[i] = compiled.Sample(ref random).Resources.Effects[0].Value1;
        return (result, random.State);
    }
}
