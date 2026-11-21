using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Base.Hints;
using UmaAiSharp.Base.Inheritance;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;
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
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void UmaPlanChoosesBetweenIndependentWholeResults(bool hasUmaPlan, bool tie, bool chooseSecond)
    {
        var relatives = Enum.GetValues<InheritanceSlot>()
            .Select(slot => new RelationSource(slot, (int)slot + 2, [])).ToImmutableArray();
        var context = new InheritanceContext(
            new(new([new(1, [101]), new(10, []), new(20, [])], []), relatives[..3]),
            new(new([new(1, []), new(10, []), new(20, [])], []), relatives[3..]),
            hasUmaPlan, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
        var data = new InheritanceData([new(101, 1, 1, 1, 10)],
            [new(1, 1, 1, 6, 0), new(1, 1, 11, 1, 0), new(1, 1, 41, 1001, 1),
             new(1, 2, 1, 15, 0), new(1, 2, 61, 3, 0), new(1, 2, 12, 2, 0),
             new(1, 2, 41, 1001, 4), new(1, 2, 6, 3, 0)],
            [new(1001, 100, 1)], [new(1, 0)],
            [.. Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1))], [], [],
            new([new(1, 10, 10000)], [new(10, 1, 1), new(10, 2, 1)], [1]));
        var rules = new MidRunInheritanceRules(data, context, 1, 101, skill =>
        {
            Assert.Equal(new HintSkill(100, 1), skill);
            return 0;
        }, []);

        var resources = new TrainingValues(1199, 0, 0, 0, 0, 100, 0);
        var limits = new TrainingLimits(1200, 1200, 1200, 1200, 1200, 100);
        var aptitudes = context.Aptitudes.ToArray();
        int[] hints = [-1];
        var overflow = new AttributeValues(7, 0, 0, 0, 0);
        var random = new SimulationRandom(1);
        var comparisons = 0;
        rules.Apply(ref resources, ref limits, aptitudes, hints, ref overflow, candidate =>
        {
            comparisons++;
            return tie ? int.MinValue : candidate.HintLevels[0];
        }, ref random);

        Assert.Equal(hasUmaPlan ? 2 : 0, comparisons);
        Assert.Equal(chooseSecond ? 1203 : 1200, resources.Speed);
        Assert.Equal(chooseSecond ? 3 : 0, resources.SkillPoints);
        Assert.Equal(chooseSecond ? 1203 : 1200, limits.Speed);
        Assert.Equal(chooseSecond ? 11 : 9, overflow.Speed);
        Assert.Equal(chooseSecond ? 1 : 2, aptitudes[0]);
        Assert.Equal(chooseSecond ? 3 : 1, aptitudes[1]);
        Assert.Equal(chooseSecond ? 4 : 1, hints[0]);
        Assert.Equal(1UL, random.State);
    }
}
