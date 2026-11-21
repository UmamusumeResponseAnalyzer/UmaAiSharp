using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Base.Inheritance;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Game;

namespace UmaAiSharp.Tests;

public sealed class MidRunInheritanceTests
{
    [Fact]
    public void CapturedUmaPlanChoicesRankByRawAttributeAndPointGains()
    {
        // Only the fields consumed by RankLotteries from the two-choice check_event response.
        var common = JsonNode.Parse("""
            {
              "chara_info":{"speed":1427,"stamina":955,"power":1108,"guts":1044,"wiz":1120,"skill_point":3236},
              "unchecked_event_array":[{"story_id":400000040,"succession_event_info":{"succession_gain_info_array":[
                {"lottery_id":1,"speed":1471,"stamina":982,"power":1108,"guts":1089,"wiz":1132,"skill_point":3236},
                {"lottery_id":2,"speed":1474,"stamina":982,"power":1132,"guts":1118,"wiz":1144,"skill_point":3236}
              ]}}]
            }
            """)!.AsObject();

        Assert.Equal([
            new PioneerInheritanceLottery(2, new(47, 27, 24, 74, 24), 0, 243),
            new PioneerInheritanceLottery(1, new(44, 27, 0, 45, 12), 0, 172)
        ], PioneerInheritanceCapture.RankLotteries(common));
    }

    [Theory]
    [InlineData(false, 7, 0.5)]
    [InlineData(true, 7, 0.75)]
    [InlineData(true, 8, 0.5)] // A and B both score 22 raw points, so the first candidate wins ties.
    public void FixedPedigreeChoosesWholeResultsBeforePanelCaps(bool hasUmaPlan, int firstPoints, double probabilityB)
    {
        var context = Family(hasUmaPlan, 101);
        var data = new InheritanceData([new(101, 1, 1, 1, 10)],
            [new(1, 1, 1, 6, 0), new(1, 1, 11, 1, 0), new(1, 1, 41, 1001, 1), new(1, 1, 41, 1002, 1), new(1, 1, 6, firstPoints, 0),
             new(1, 2, 1, 16, 0), new(1, 2, 61, 3, 0), new(1, 2, 12, 2, 0),
             new(1, 2, 41, 1001, 4), new(1, 2, 6, 3, 0)],
            [new(1001, 100, 1), new(1002, 101, 1)], [new(1, 0)],
            [.. Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1))], [], [],
            new([new(1, 10, 10000)], [new(10, 1, 1), new(10, 2, 1)], [1]));
        var rules = MidRunInheritanceRules.Compile(data, context, 1, 101, skill => skill.GroupId - 100, [], hasUpcomingInheritance: true)!;

        // At full Speed candidate A would win on displayed gains with seven Pt, despite its lower declared score.
        foreach (var initialSpeed in new[] { 200, 1200 })
        {
            var samples = Enumerable.Range(0, 256).Select(sampleIndex =>
            {
                var resources = new TrainingValues(initialSpeed, 0, 0, 0, 0, 100, 0);
                var limits = new TrainingLimits(1200, 1200, 1200, 1200, 1200, 100);
                var aptitudes = context.Aptitudes.ToArray();
                var hints = new[] { -1, -1 };
                var overflow = new AttributeValues(7, 0, 0, 0, 0);
                rules.Apply(ref resources, ref limits, aptitudes, hints, ref overflow, sampleIndex);
                Assert.True(hints[1] is -1 or 1);
                Assert.True(resources.Speed <= limits.Speed);
                return new { resources.Speed, resources.SkillPoints, Cap = limits.Speed, Overflow = overflow.Speed,
                    Turf = aptitudes[0], Dirt = aptitudes[1], Hint = hints[0], ExclusiveHint = hints[1] };
            }).ToArray();
            Assert.Equal(initialSpeed == 1200 ? 1200 + 3 * probabilityB : 206 + 10 * probabilityB, samples.Average(s => s.Speed));
            Assert.Equal(1200 + 3 * probabilityB, samples.Average(s => s.Cap));
            Assert.Equal(initialSpeed == 1200 ? 10 + 2 * probabilityB : 7, samples.Average(s => s.Overflow));
            Assert.Equal(firstPoints + (3 - firstPoints) * probabilityB, samples.Average(s => s.SkillPoints));
            Assert.Equal(2 - probabilityB, samples.Average(s => s.Turf));
            Assert.Equal(1 + 2 * probabilityB, samples.Average(s => s.Dirt));
            Assert.Equal(1 + 3 * probabilityB, samples.Average(s => s.Hint));
            Assert.Equal(1 - probabilityB, samples.Average(s => Math.Max(0, s.ExclusiveHint)));
        }
    }

    [Fact]
    public void ConditionalAddonFollowsTheWinningFactorGroup()
    {
        var context = Family(true, 101, 201);
        var data = new InheritanceData([new(101, 1, 1, 1, 10), new(201, 1, 2, 1, 10), new(301, 4, 3, 1, 10)],
            [new(1, 1, 1, 2, 0), new(2, 1, 2, 4, 0), new(3, 1, 41, 1001, 1)], [new(1001, 100, 1)], [new(1, 0)],
            [.. Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1))], [],
            [new(301, 2, [1, 2])], new([new(1, 10, 5000)], [new(10, 1, 1)], [1]));
        var rules = MidRunInheritanceRules.Compile(data, context, 1, 101, _ => 0, [], hasUpcomingInheritance: true)!;
        var speed = 0;
        var stamina = 0;
        var unlocks = 0;
        for (var sample = 0; sample < 256; sample++)
        {
            var resources = new TrainingValues(0, 0, 0, 0, 0, 100, 0);
            var limits = new TrainingLimits(1200, 1200, 1200, 1200, 1200, 100);
            var hints = new[] { -1 };
            var overflow = default(AttributeValues);
            rules.Apply(ref resources, ref limits, context.Aptitudes.ToArray(), hints, ref overflow, sample);
            speed += resources.Speed;
            stamina += resources.Stamina;
            if (hints[0] == 1) unlocks++;
        }
        // Winners of score 0/2/4/6 occur in 1/3/5/7 of the 16 equally probable pairs.
        Assert.Equal(1.25, speed / 256.0);
        Assert.Equal(3, stamina / 256.0);
        Assert.Equal(7.0 / 16, unlocks / 256.0);
    }

    [Theory]
    [InlineData(1190, 1300, 1206.25, 22.5, 0)]
    [InlineData(1190, 1210, 1206.25, 22.5, 0)]
    [InlineData(1190, 1205, 1205, 20, 1.25)]
    [InlineData(1250, 1300, 1261.25, 22.5, 0)]
    public void InheritanceAveragesRawAttributeGains(int initialSpeed, int speedLimit, double expectedSpeed,
        double expectedRawGain, double expectedOverflow)
    {
        var context = Family(false, 101);
        var data = new InheritanceData([new(101, 1, 1, 1, 10)],
            [new(1, 1, 1, 5, 0), new(1, 2, 1, 40, 0)], [], [new(1, 0)],
            [.. Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1))], [], [],
            new([new(1, 10, 10000)], [new(10, 1, 1), new(10, 2, 1)], [1]));
        var rules = MidRunInheritanceRules.Compile(data, context, 1, 101, _ => throw new InvalidOperationException(), [], hasUpcomingInheritance: true)!;
        var displayed = 0;
        var rawGain = 0;
        var initialRaw = initialSpeed + Math.Max(0, initialSpeed - 1200);
        var overflowSum = 0;
        for (var sample = 0; sample < 1024; sample++)
        {
            var resources = new TrainingValues(initialSpeed, 0, 0, 0, 0, 100, 0);
            var limits = new TrainingLimits(speedLimit, 1200, 1200, 1200, 1200, 100);
            var aptitudes = context.Aptitudes.ToArray();
            var overflow = default(AttributeValues);
            rules.Apply(ref resources, ref limits, aptitudes, [], ref overflow, sample);
            Assert.InRange(resources.Speed, (int)Math.Floor(expectedSpeed), (int)Math.Ceiling(expectedSpeed));
            Assert.Equal(speedLimit, limits.Speed);
            displayed += resources.Speed;
            rawGain += resources.Speed + Math.Max(0, resources.Speed - 1200) - initialRaw;
            overflowSum += overflow.Speed;
        }
        Assert.Equal(expectedSpeed, displayed / 1024.0);
        Assert.Equal(expectedRawGain, rawGain / 1024.0);
        Assert.Equal(expectedOverflow, overflowSum / 1024.0);
    }

    [Theory]
    [InlineData(4, false, 5, 2)]
    [InlineData(5, false, 5, 4)]
    [InlineData(-1, true, -1, 4)]
    public void RepeatedHintsConvertToAttributeOverflowAfterSaturation(int initialHint, bool owned, int finalHint, int expectedOverflow)
    {
        var context = Family(false, 101);
        var data = new InheritanceData([new(101, 1, 1, 1, 10)],
            [new(1, 1, 41, 1001, 1), new(1, 1, 41, 1001, 1), new(1, 1, 6, 7, 0)],
            [new(1001, 100, 1)], [new(1, 0)],
            [.. Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1))], [], [],
            new([new(1, 10, 10000)], [new(10, 1, 1)], [0, 0, 0, 1]));
        var rules = MidRunInheritanceRules.Compile(data, context, 1, 101, _ => 0, owned ? [1001] : [], hasUpcomingInheritance: true)!;
        var capped = new TrainingValues(1200, 1200, 1200, 1200, 1200, 100, 20);
        var totalOverflow = 0;
        for (var sample = 0; sample < 1024; sample++)
        {
            var resources = capped;
            var limits = new TrainingLimits(1200, 1200, 1200, 1200, 1200, 100);
            var aptitudes = context.Aptitudes.ToArray();
            var hints = new[] { initialHint };
            var overflow = new AttributeValues(7, 0, 0, 0, 0);
            rules.Apply(ref resources, ref limits, aptitudes, hints, ref overflow, sample, MidRunInheritanceRules.SeniorTurn);
            Assert.Equal(capped with { SkillPoints = 27 }, resources);
            Assert.Equal(finalHint, hints[0]);
            totalOverflow += overflow.Speed + overflow.Stamina + overflow.Power + overflow.Guts + overflow.Wisdom - 7;
        }
        // Each conversion grants four raw points to one capped attribute, or two displayed points of overflow.
        Assert.InRange(totalOverflow / 1024.0, expectedOverflow - 0.005, expectedOverflow + 0.005);
    }

    static InheritanceContext Family(bool hasUmaPlan, params int[] factorIds)
    {
        var relatives = Enum.GetValues<InheritanceSlot>()
            .Select(slot => new RelationSource(slot, (int)slot + 2, [])).ToImmutableArray();
        return new(new(new([new(1, [.. factorIds]), new(10, []), new(20, [])], []), relatives[..3]),
            new(new([new(1, []), new(10, []), new(20, [])], []), relatives[3..]),
            hasUmaPlan, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
    }
}
