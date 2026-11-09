using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Development;

public readonly record struct PioneerPointResources(int DevelopmentPoints, int SkillPoints);
public readonly record struct TuckerEventContext(int Turn, int RawPlayingState, int DeckId, int StoryId,
    int PlayTiming, int ChoiceNumber, int SupportCardId, int LimitBreakCount, int SupportExperience);

public static class TuckerPointSettlement
{
    // The observed support is 30257, limit break 4, exp 118185 (master rarity 3, level 50).
    public static PioneerPointResources ApplyObservedChoiceOne(TuckerEventContext context, PioneerPointResources before)
    {
        if (context.Turn is < 8 or > 60 || context.RawPlayingState != 5 || context.DeckId != 3
            || context.StoryId != 400011111 || context.PlayTiming != 6 || context.ChoiceNumber != 1
            || context.SupportCardId != 30257 || context.LimitBreakCount != 4 || context.SupportExperience != 118185)
            throw new UnknownMechanismException("Tucker 资源结算仅核验事件400011111/时机6/选择1、deck=3、回合8..60及支援30257满突破经验118185；其他选项或等级尚未覆盖。");
        if (before.DevelopmentPoints < 0 || before.SkillPoints < 0)
            throw new InvalidDataException("Tucker 结算前发展 Pt 和技能 Pt 不能为负数。");
        return new(checked(before.DevelopmentPoints + 80), checked(before.SkillPoints + 3));
    }
}
