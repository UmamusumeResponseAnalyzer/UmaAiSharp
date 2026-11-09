using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Events;

public readonly record struct TuckerFriendContext(int StoryId, int ChoiceNumber, int SupportCardId,
    int LimitBreakCount, int SupportExperience, bool HasCharmEffect);
public readonly record struct TuckerFriendState(int Vital, int MaxVital, int Motivation,
    int Bond, bool OutingUnlocked, int StoryStep);
public sealed record TuckerFriendSettlement(TuckerFriendState After, int SkillPointGain, int VitalGainBeforeCap, int ActualVitalGain);

public static class TuckerEventSettlement
{
    // These are selected-event components; attributes, hints, conditions and development Pt are separate.
    public static TuckerFriendSettlement ApplyObservedComponents(TuckerFriendContext context, TuckerFriendState before)
    {
        if (context.SupportCardId != 30257 || context.LimitBreakCount != 4 || context.SupportExperience != 118185)
            throw new UnknownMechanismException("Tucker事件分量仅核验支援30257、突破4、经验118185；其他等级尚未覆盖。");
        if (before.MaxVital is < 1 or > VitalRules.Maximum || before.Vital < 0 || before.Vital > before.MaxVital || before.Motivation is < 1 or > 5
            || before.Bond is < 0 or > 100 || before.StoryStep is < 0 or > 5)
            throw new InvalidDataException("Tucker事件前资源、干劲、羁绊或外出进度无效。");
        var (choice, recovery, maxVitalGain, skillGain, step) = context.StoryId switch
        {
            809049004 => (1, 32, 0, 13, -1),
            809049011 => (0, 32, 4, 0, 1),
            809049012 => (0, 32, 0, 0, 2),
            809049013 => (1, 80, 0, 0, 3),
            809049014 => (0, 40, 0, 0, 4),
            809049015 => (0, 48, 0, 0, 5),
            809049021 => (0, 24, 0, 0, 0),
            _ => throw new UnknownMechanismException($"Tucker故事{context.StoryId}的资源和进度分量尚未核验。"),
        };
        if (context.ChoiceNumber != choice)
            throw new UnknownMechanismException($"Tucker故事{context.StoryId}仅核验原始choice={choice}，不能把其他选择替换成该结果。");
        if (step == -1 ? before.OutingUnlocked || before.StoryStep != 0
            : !before.OutingUnlocked || step > 0 && before.StoryStep != step - 1 || step == 0 && before.StoryStep > 4)
            throw new UnknownMechanismException("Tucker事件与已观测的外出解锁或story_step前态不符。");
        var (vital, maximum) = VitalRules.Apply(before.Vital, before.MaxVital, recovery, maxVitalGain);
        var after = before with
        {
            Vital = vital, MaxVital = maximum, Motivation = Math.Min(5, before.Motivation + 1),
            Bond = Math.Min(100, before.Bond + 5 + (context.HasCharmEffect ? 2 : 0)),
            OutingUnlocked = true, StoryStep = step > 0 ? step : before.StoryStep,
        };
        return new(after, skillGain, recovery, vital - before.Vital);
    }
}
