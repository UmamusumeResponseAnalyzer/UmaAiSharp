using System.Collections.Immutable;
using System.Numerics;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerJointPlacementTests
{
    [Theory]
    [InlineData(false, 0, 492, 4416)]
    [InlineData(true, 10, 1584, 24255)]
    public void NpcWeightsPreserveTheObservedOpportunityDenominator(bool camp, int sea, int absent, int present)
    {
        var distribution = new PioneerPlacementDistribution(Input(PlacementMemberKind.ProjectNpc, -1) with { Camp = camp, SeaHousePercent = sea });
        Assert.Equal((decimal)absent / (absent + present), (decimal)distribution.Weights[5] / distribution.TotalWeight);
        Assert.Equal(0UL, distribution.HintNumerator);
        Assert.All(distribution.Weights.Take(5), weight => Assert.Equal(distribution.Weights[0], weight));
        Assert.Throws<ArgumentException>(() => new PioneerPlacementDistribution(Input(PlacementMemberKind.ProjectNpc, -1) with { HintPercent = 10 }));
    }

    [Fact]
    public void JointBoardMatchesWholeBoardRejectionAndDrawsHintsOncePerTurn()
    {
        var model = Model();
        var placements = new SupportPlacement[model.Members.Length];
        var random = new SimulationRandom(821);
        var reference = random;
        var rejected = 0;
        for (var sample = 0; sample < 1024; sample++)
        {
            var expected = Reference(model.Members, ref reference, ref rejected);
            var board = model.Sample(placements, ref random);
            Assert.Equal(expected, placements);
            Assert.Equal(reference.State, random.State);
            for (var training = 0; training < 5; training++)
            {
                var cell = board[training];
                Assert.InRange(cell.SupportCount + cell.GuestCount, 0, 5);
                Assert.Equal(0, cell.HintMask & ~cell.SupportMask);
                Assert.Equal(placements.Take(6).Count(value => value.TrainingIndex == training), cell.SupportCount);
                Assert.Equal(placements.Take(6).Count(value => value.TrainingIndex == training && value.HasHint), BitOperations.PopCount((uint)cell.HintMask));
                Assert.Equal(placements.Skip(6).Count(value => value.TrainingIndex == training), cell.GuestCount);
            }
            Assert.Equal(new(-1, false), placements[5]);
        }
        Assert.True(rejected > 0);
    }

    static SupportPlacement[] Reference(ImmutableArray<PlacementMember> members, ref SimulationRandom random, ref int rejected)
    {
        var hints = new bool[6];
        for (var index = 0; index < 6; index++)
        {
            var distribution = members[index].Distribution;
            hints[index] = members[index].Appeared && (distribution.HintNumerator == distribution.HintDenominator
                || distribution.HintNumerator != 0 && random.NextUInt64(distribution.HintDenominator) < distribution.HintNumerator);
        }
        while (true)
        {
            var values = new SupportPlacement[members.Length];
            for (var index = 0; index < members.Length; index++)
            {
                var position = -1;
                if (members[index].Appeared)
                {
                    var weights = members[index].Distribution.Weights;
                    var draw = random.NextUInt64(members[index].Distribution.TotalWeight);
                    var cumulative = 0UL;
                    for (var candidate = 0; candidate < weights.Length; candidate++)
                    {
                        cumulative += weights[candidate];
                        if (draw >= cumulative) continue;
                        position = candidate == 5 ? -1 : candidate;
                        break;
                    }
                }
                values[index] = new(position, position >= 0 && index < 6 && hints[index]);
            }
            if (values.Where(value => value.TrainingIndex >= 0).GroupBy(value => value.TrainingIndex).All(group => group.Count() <= 5)) return values;
            rejected++;
        }
    }

    static PlacementInput Input(PlacementMemberKind kind, int specialty) => new(kind, specialty, false, 10,
        0, 0, 0, 0, 0, 0, 0, false, 30, []);

    static PioneerJointPlacement Model()
    {
        var members = Enumerable.Range(1, 5).Select(position => new PlacementMember(position, true,
            new(Input(PlacementMemberKind.Training, position - 1) with { SpecialtyPercent = 80, HintPercent = 60 }))).ToList();
        members.Add(new(6, false, new(Input(PlacementMemberKind.Tucker, -1))));
        var npc = new PioneerPlacementDistribution(Input(PlacementMemberKind.ProjectNpc, -1));
        members.AddRange(Enumerable.Range(1001, 12).Select(id => new PlacementMember(id, true, npc)));
        return new([.. members]);
    }
}
