using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingNumericKernelTests
{
    static readonly int[] Commands = [101, 105, 102, 103, 106];
    static readonly ImmutableArray<TrainingSupport> Definitions =
    [
        new(30101, 50, 1, 101, 0, [new(1, 25), new(2, 30), new(3, 1), new(8, 10), new(28, 20)], [new(111, 8, 3, 0, 0, 0)]),
        new(30102, 50, 2, 105, 0, [new(1, 20), new(4, 2), new(8, 4)], [new(104, 1000, 20, 0, 0, 0), new(8, 4, 0, 0, 0, 0)]),
        new(30103, 50, 3, 102, 0, [new(1, 30), new(5, 2), new(27, 20)], [new(101, 80, 1, 10, 3, 2)]),
        new(30104, 50, 4, 103, 0, [new(1, 20), new(6, 2)], [new(101, 90, 2, 20, 8, 12)]),
        new(30105, 50, 5, 106, 0, [new(1, 25), new(7, 2), new(30, 1), new(31, 5)], [new(111, 30, 1, 0, 0, 0)]),
        new(30257, 50, 6, 0, 0, [new(2, 30), new(4, 1), new(8, 10), new(19, 60), new(28, 20)], [new(105, 0, 0, 0, 0, 0), new(122, 19, 60, 0, 0, 0)])
    ];

    [Fact]
    public void AllThreeModesMatchTheReferenceAcrossDynamicInputs()
    {
        var random = new Random(20261108);
        var deck = new CompiledTrainingDeck(Definitions);
        for (var sample = 0; sample < 300; sample++)
        {
            var command = Commands[sample % 5];
            var level = sample / 5 % 5 + 1;
            var supports = Definitions.Where(_ => random.Next(3) != 0)
                .Select(card => card with { Bond = new[] { 0, 79, 80, 89, 90, 100 }[random.Next(6)] }).ToImmutableArray();
            var current = (sample % 4) switch { 0 => 1189, 1 => 1200, 2 => 1799, _ => 100 };
            var environment = new TrainingEnvironment(sample / 25 % 5 + 1, sample * 719,
                new(current, current, current, current, current, 50, 300), new(1800, 1800, 1800, 1800, 1800, 100),
                new(20, 10, 0, 0, 0, 0, 0), sample % 7 == 0 ? new(command) : null);
            var ordinary = new TrainingFormulaInput(command, level, environment.Motivation, environment.Fans, sample % 4,
                environment.Current, environment.Limits, environment.GrowthPercent, supports, sample % 3 * 10, environment.GoldShipBoost);
            Equal(TrainingFormulaCandidate.GenerateOrdinary(ordinary), TrainingNumericKernel.GenerateOrdinary(ordinary));
            Equal(TrainingFormulaCandidate.GenerateOrdinary(ordinary), TrainingNumericKernel.GenerateOrdinary(ordinary, deck));

            var facilities = Facilities();
            var camp = new CampTrainingInput(3601 + sample % 5, 5, sample % 4, environment with { GoldShipBoost = null },
                supports, TrainingBaseTable.GetOrdinary(command, 5), facilities);
            Equal(TrainingFormulaCandidate.GenerateCamp(camp), TrainingNumericKernel.GenerateCamp(camp));
            Equal(TrainingFormulaCandidate.GenerateCamp(camp), TrainingNumericKernel.GenerateCamp(camp, deck));

            var assignments = supports.GroupBy(card => 101 + (card.Position + sample) % 6)
                .Select(group => new IslandTrainingAssignment(group.Key, group.ToImmutableArray(), sample % 3)).ToImmutableArray();
            var island = new IslandTrainingInput(environment, [1, level, 2, 3, level], new(8, 6, 4, 4, 10, 0, 7), facilities, assignments);
            Equal(TrainingFormulaCandidate.GenerateIsland(island), TrainingNumericKernel.GenerateIsland(island));
            Equal(TrainingFormulaCandidate.GenerateIsland(island), TrainingNumericKernel.GenerateIsland(island, deck));
        }
    }

    [Fact]
    public void AttributeLimitsRetainBothLayersInDisplayUnitsAcrossAllTrainingModes()
    {
        foreach (var (before, limit, uncapped, actual, overflow) in new[]
        {
            (1195, 1200, 8, 5, 6), (1199, 1200, 6, 1, 8), (1200, 1200, 6, 0, 9),
            (1200, 1201, 6, 1, 8), (1201, 1800, 6, 6, 0), (1799, 1800, 6, 1, 8), (1800, 1800, 6, 0, 9)
        })
        {
            var environment = Environment() with { Current = Environment().Current with { Speed = before }, Limits = Environment().Limits with { Speed = limit } };
            var ordinary = new TrainingFormulaInput(101, 1, 3, 0, 0, environment.Current, environment.Limits, new(), [], 50, null);
            var facilities = EmptyFacilities().SetItem(0, new(101, 10110, 1, [new(2, 2, 50, 3601), new(2, 0, 50, 0)]));
            var camp = new CampTrainingInput(3601, 5, 0, environment, [], new(12, 0, 0, 0, 0, -20, 6), facilities);
            var island = new IslandTrainingInput(environment, [1, 1, 1, 1, 1], new(12, 0, 0, 0, 0, 0, 6), facilities, []);
            foreach (var result in new[] { TrainingNumericKernel.GenerateOrdinary(ordinary), TrainingNumericKernel.GenerateCamp(camp), TrainingNumericKernel.GenerateIsland(island) })
            {
                Assert.Equal(uncapped, result.UncappedCommon.Speed);
                Assert.Equal(actual, result.Common.Speed);
                Assert.Equal(3, result.UncappedScenario.Speed);
                Assert.Equal(overflow, result.Overflow.Speed);
                Assert.Equal(result.UncappedCommon.SkillPoints, result.Common.SkillPoints);
                Assert.Equal(result.UncappedScenario.SkillPoints, result.Scenario.SkillPoints);
            }
        }
    }

    [Fact]
    public void IslandKeepsEveryStaticAndDynamicAtomSeparateBeforeTruncating()
    {
        var card = new TrainingSupport(1, 50, 1, 101, 100, [new(8, 4)], [new(101, 80, 8, 4, 0, 0)]);
        var input = new IslandTrainingInput(Environment(), [1, 1, 1, 1, 1], new(98, 0, 0, 0, 0, 0, 0),
            EmptyFacilities(), [new(106, [card], 0)]);
        Assert.Equal(98, TrainingNumericKernel.GenerateIsland(input, new([card])).Common.Speed);
        Equal(TrainingFormulaCandidate.GenerateIsland(input), TrainingNumericKernel.GenerateIsland(input, new([card])));
        var merged = card with { Effects = [new(8, 8)], UniqueEffects = [] };
        var mergedInput = input with { Assignments = [new(106, [merged], 0)] };
        Assert.Equal(99, TrainingNumericKernel.GenerateIsland(mergedInput, new([merged])).Common.Speed);
    }

    [Fact]
    public void CompiledDeckRetainsChangingConditionsAndRejectsChangedDefinitions()
    {
        var deck = new CompiledTrainingDeck(Definitions);
        var input = Ordinary(Definitions);
        foreach (var bond in new[] { 0, 79, 80, 90, 100 })
        {
            var updated = input with { Supports = [.. Definitions.Select(card => card with { Bond = bond })], Fans = bond * 1000 };
            Equal(TrainingFormulaCandidate.GenerateOrdinary(updated), TrainingNumericKernel.GenerateOrdinary(updated, deck));
        }
        var changed = Definitions.SetItem(0, Definitions[0] with { Effects = [new(8, 99)] });
        Assert.Throws<ArgumentException>(() => TrainingNumericKernel.GenerateOrdinary(input with { Supports = changed }, deck));
        Assert.Throws<ArgumentException>(() => TrainingNumericKernel.GenerateOrdinary(input with { Supports = [Definitions[0] with { Position = 9 }] }, deck));
        Assert.Throws<ArgumentException>(() => new CompiledTrainingDeck([Definitions[0], Definitions[0]]));
        var copied = Definitions.Select(card => card with { Effects = [.. card.Effects], UniqueEffects = [.. card.UniqueEffects] }).ToImmutableArray();
        Equal(TrainingFormulaCandidate.GenerateOrdinary(input with { Supports = copied }), TrainingNumericKernel.GenerateOrdinary(input with { Supports = copied }, deck));
    }

    [Fact]
    public void UnsupportedConditionalEffectsStayUnknownWhenTheirConditionBecomesActive()
    {
        var card = Definitions[0] with { UniqueEffects = [new(101, 80, 999, 1, 0, 0)], Bond = 79 };
        var deck = new CompiledTrainingDeck([card]);
        var input = Ordinary([card]);
        Equal(TrainingFormulaCandidate.GenerateOrdinary(input), TrainingNumericKernel.GenerateOrdinary(input, deck));
        var activated = input with { Supports = [card with { Bond = 80 }] };
        Assert.Throws<UnknownMechanismException>(() => TrainingFormulaCandidate.GenerateOrdinary(activated));
        Assert.Throws<UnknownMechanismException>(() => TrainingNumericKernel.GenerateOrdinary(activated, deck));
        var camp = new CampTrainingInput(3601, 5, 0, Environment(), [], new(12, 0, 0, 0, 0, -15, 8), EmptyFacilities());
        Assert.Throws<UnknownMechanismException>(() => TrainingNumericKernel.GenerateCamp(camp with { Level = 4 }));
        var island = new IslandTrainingInput(Environment(), [1, 1, 1, 1, 1], new(8, 6, 4, 4, 10, 0, 7),
            EmptyFacilities().SetItem(5, new(106, 10620, 2, [])), []);
        Equal(TrainingFormulaCandidate.GenerateIsland(island), TrainingNumericKernel.GenerateIsland(island));
    }

    [Fact]
    public void CompiledConditionsKeepFanAndBondBoundariesAndActivatedOverflow()
    {
        var card = Definitions[0] with { UniqueEffects = [new(104, 1000, 3, 0, 0, 0),
            new(101, 80, 8, 2, 2, 7), new(104, 700, 5, 0, 0, 0), new(111, 8, 3, 0, 0, 0)] };
        var deck = new CompiledTrainingDeck([card]);
        foreach (var fans in new[] { 0, 699, 700, 701, 999, 1000, 1399, 1400, 1999, 2000, 2999, 3000, 3499, 3500, int.MaxValue })
            foreach (var bond in new[] { 0, 79, 80, 100 })
                foreach (var level in new[] { 1, 3, 5 })
                {
                    var input = Ordinary([card with { Bond = bond }]) with { Fans = fans, Level = level };
                    Equal(TrainingFormulaCandidate.GenerateOrdinary(input), TrainingNumericKernel.GenerateOrdinary(input, deck));
                }
        var overflowing = card with { Effects = [new(8, 1)], UniqueEffects = [new(101, 80, 8, int.MaxValue, 0, 0)], Bond = 79 };
        var overflowDeck = new CompiledTrainingDeck([overflowing]);
        Equal(TrainingFormulaCandidate.GenerateOrdinary(Ordinary([overflowing])),
            TrainingNumericKernel.GenerateOrdinary(Ordinary([overflowing]), overflowDeck));
        Assert.Throws<OverflowException>(() => TrainingNumericKernel.GenerateOrdinary(Ordinary([overflowing with { Bond = 80 }]), overflowDeck));
        Assert.Throws<InvalidOperationException>(() => new CompiledTrainingDeck([card with
        { UniqueEffects = [new(104, 1, int.MaxValue, 0, 0, 0)] }]));
    }

    [Fact]
    public void ExactArithmeticDoesNotRequireAnAssumedFixedWidthProductBound()
    {
        var card = new TrainingSupport(1, 50, 1, 101, 100, [.. Enumerable.Repeat(new SupportEffect(1, 1), 128)], []);
        var input = Ordinary([card]);
        var reference = TrainingFormulaCandidate.GenerateOrdinary(input);
        Assert.True(reference.RawGains[0].Denominator.Length > 38);
        Equal(reference, TrainingNumericKernel.GenerateOrdinary(input));
        Equal(reference, TrainingNumericKernel.GenerateOrdinary(input, new([card])));
    }

    [Fact]
    public void OneCompiledDeckCanBeSharedByConcurrentTrajectories()
    {
        var deck = new CompiledTrainingDeck(Definitions);
        Parallel.For(0, 64, sample =>
        {
            var input = Ordinary([.. Definitions.Select(card => card with { Bond = sample * 7 % 101 })]) with { Fans = sample * 1777 };
            Equal(TrainingFormulaCandidate.GenerateOrdinary(input), TrainingNumericKernel.GenerateOrdinary(input, deck));
        });
    }

    [Theory]
    [InlineData(1, 35_791_394)]
    [InlineData(1, 71_582_788)]
    [InlineData(2, 35_791_394)]
    [InlineData(2, 71_582_788)]
    [InlineData(8, 35_791_394)]
    [InlineData(8, 71_582_788)]
    public void IslandEffectScalingKeepsTheIntegerProductBeforeCapping(int effect, int bonus)
    {
        var card = new TrainingSupport(1, 50, 1, 101, 100, [new(effect, bonus)], []);
        var input = new IslandTrainingInput(Environment() with { Motivation = 5 }, [1, 1, 1, 1, 1], new(10, 0, 0, 0, 0, 0, 0),
            EmptyFacilities(), [new(101, [card], 0)]);
        Assert.Equal(100, TrainingFormulaCandidate.GenerateIsland(input).Common.Speed);
        Assert.Equal(100, TrainingNumericKernel.GenerateIsland(input).Common.Speed);
        Assert.Equal(100, TrainingNumericKernel.GenerateIsland(input, new([card])).Common.Speed);
    }

    [Theory]
    [InlineData(0, int.MaxValue, 10)]
    [InlineData(1, 500, 11)]
    [InlineData(2, 500, 12)]
    [InlineData(6, 500, 16)]
    [InlineData(1, 2_147_473_647, 100)]
    [InlineData(1, 2_147_473_648, 100)]
    [InlineData(2, 2_147_478_648, 100)]
    [InlineData(6, int.MaxValue, 100)]
    public void FacilityMultipliersKeepTheirWidthBeforeTheTrainingCap(int friends, int bonus, int expected)
    {
        var cards = Enumerable.Range(1, friends)
            .Select(position => new TrainingSupport(position, 50, position, 101, 100, [], [])).ToImmutableArray();
        var deck = new CompiledTrainingDeck(cards);
        var facilities = EmptyFacilities().SetItem(5, new(106, 10610, 1, [new(103, 0, bonus, 0)]));
        var basis = new TrainingValues(10, 0, 0, 0, 0, 0, 0);
        var island = new IslandTrainingInput(Environment(), [1, 1, 1, 1, 1], basis, facilities, [new(101, cards, 0)]);
        var camp = new CampTrainingInput(3601, 5, 0, Environment(), cards, basis, facilities);
        // With only heads and house active, the small cases truncate 10*(1+.05*n)^2; the large cases cap at 100.
        Assert.Equal(expected, TrainingFormulaCandidate.GenerateIsland(island).Common.Speed);
        Assert.Equal(expected, TrainingNumericKernel.GenerateIsland(island).Common.Speed);
        Assert.Equal(expected, TrainingNumericKernel.GenerateIsland(island, deck).Common.Speed);
        Assert.Equal(expected, TrainingFormulaCandidate.GenerateCamp(camp).Common.Speed);
        Assert.Equal(expected, TrainingNumericKernel.GenerateCamp(camp).Common.Speed);
        Assert.Equal(expected, TrainingNumericKernel.GenerateCamp(camp, deck).Common.Speed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OrdinaryMultiplierOffsetsKeepThePositiveValueUntilCapping(int source)
    {
        var card = new TrainingSupport(1, 50, 1, 101, 100,
            source >= 2 ? [new(source == 2 ? 1 : 8, int.MaxValue)] : [], []);
        var input = Ordinary([card]) with
        {
            GuestCount = source == 0 ? int.MaxValue : 0,
            GrowthPercent = source == 1 ? new(int.MaxValue, 0, 0, 0, 0, 0, 0) : new(),
            EvaluationTrainingPercent = int.MaxValue
        };
        var reference = TrainingFormulaCandidate.GenerateOrdinary(input);
        var hot = TrainingNumericKernel.GenerateOrdinary(input, new([card]));
        Assert.Equal(100, reference.Common.Speed);
        Assert.Equal(100, hot.Common.Speed);
        Assert.Equal(1600, reference.Scenario.Speed);
        Assert.Equal(1600, hot.Scenario.Speed);
        Assert.Equal(1_073_740_723, hot.Overflow.Speed);
    }

    static TrainingEnvironment Environment() => new(3, 0, new(100, 100, 100, 100, 100, 50, 0),
        new(1800, 1800, 1800, 1800, 1800, 100), new(), null);
    static TrainingFormulaInput Ordinary(ImmutableArray<TrainingSupport> supports) => new(101, 3, 3, 0, 0,
        Environment().Current, Environment().Limits, new(), supports, 10, null);
    static ImmutableArray<TrainingFacility> EmptyFacilities() => [.. Enumerable.Range(101, 6).Select(id => new TrainingFacility(id, id * 100 + 10, 1, []))];
    static ImmutableArray<TrainingFacility> Facilities() => [.. EmptyFacilities().Select(facility => facility with
    {
        Effects = [new(1, 9, 2, facility.Id == 106 ? 30 : facility.Id - 100), new(2, 0, 25, 0),
            new(2, 2, 25, facility.Id == 106 ? 3605 : facility.Id + 3500), new(103, 9, facility.Id == 106 ? 500 : 0, 0),
            new(104, 0, 25, 2), new(104, 0, 65, 3)]
    })];
    static void Equal(TrainingFormulaPrediction expected, TrainingNumericResult actual)
    {
        Assert.Equal(expected.Common, actual.Common);
        Assert.Equal(expected.Scenario, actual.Scenario);
    }
}
