using System.Collections.Immutable;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;
using UmaAiSharp.Base.Hints;

namespace UmaAiSharp.Pioneer.Ending;

public sealed record PioneerEndingContext(int EventId, int StoryId, int Turn, int PlayingState, int ChoiceNumber,
    ImmutableArray<int> CheckpointResults, bool HasEndingHint);
public sealed record PioneerEndingReward(TrainingValues After, int HintGroupId, int HintRarity, int HintLevel);

public static class PioneerEndingSettlement
{
    internal const int HighAttribute = 55, HighSkillPoints = 540;
    internal static readonly HintLevel HighHint = new(new(21036, 2), 1);
    internal static readonly HintLevel LowFinalReviewHint = new(new(21036, 1), 3);

    public static PioneerEndingReward ApplyCandidate(PioneerEndingContext context, TrainingValues before, TrainingLimits limits)
    {
        if (context.StoryId != 400011027 || context.Turn != 78 || context.PlayingState != 5
            || context.ChoiceNumber != 0 || context.CheckpointResults.Length != 5 || context.CheckpointResults.Any(result => result != 2)
            || context.HasEndingHint)
            throw new UnknownMechanismException("结局候选只覆盖 turn78、阶段5、五次高评价、明确选择故事400011027且未持有21036/2 Hint的窗口。");
        if (before.SkillPoints < 0 || before.Vital < 0 || before.Vital > limits.Vital)
            throw new ArgumentException("结局前技能 Pt 或体力无效。", nameof(before));
        var after = new TrainingValues(
            Attribute(before.Speed, limits.Speed), Attribute(before.Stamina, limits.Stamina),
            Attribute(before.Power, limits.Power), Attribute(before.Guts, limits.Guts), Attribute(before.Wisdom, limits.Wisdom),
            before.Vital, checked(before.SkillPoints + HighSkillPoints));
        return new(after, HighHint.Skill.GroupId, HighHint.Skill.Rarity, HighHint.Level);
    }

    static int Attribute(int before, int cap)
    {
        if (before < 0 || before > cap) throw new ArgumentException("结局前属性必须在0与明确上限之间。");
        var gain = AttributeGain.FromRaw(before, HighAttribute);
        return before + Math.Min(cap - before, gain);
    }
}
