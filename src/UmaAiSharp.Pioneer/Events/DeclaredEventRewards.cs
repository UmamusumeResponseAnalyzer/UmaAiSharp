using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Events;

public readonly record struct DeclaredEventEffect(int DisplayId, int Value0, int Value1, int Value2);
public sealed record DeclaredEventBranch(ImmutableArray<DeclaredEventEffect> Effects);
public sealed record DeclaredEventChoice(int ChoiceNumber, ImmutableArray<DeclaredEventBranch> Branches);
public readonly record struct EventResourceResult(TrainingValues Resources, TrainingLimits Limits, int Motivation,
    bool ConsumePositiveThinking, AttributeValues Overflow);

// Compiles the resource portion of a revealed reward. Other effects retain their original atoms.
public sealed class DeclaredEventRewards
{
    public const string Version = "pioneer-declared-event-resources-v2";
    readonly TrainingValues gain;
    readonly int maximumVitalGain, motivationGain;
    readonly bool repeatedResource;
    public ImmutableArray<DeclaredEventEffect> RemainingEffects { get; }
    public int ResourceMask { get; }
    public long SharedMemoryBytes => 128L + 16L * RemainingEffects.Length;

    public DeclaredEventRewards(DeclaredEventBranch branch)
    {
        if (branch.Effects.IsDefault) throw new InvalidDataException("事件结果必须明确提供奖励数组。");
        var remaining = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
        foreach (var effect in branch.Effects)
        {
            if (effect.DisplayId <= 0) throw new InvalidDataException("事件奖励display_id必须大于零。");
            if (effect.DisplayId is not (1 or 2) || effect.Value2 != 0
                || effect.Value0 is not (1 or 2 or 3 or 4 or 5 or 10 or 11 or 20 or 30))
            {
                remaining.Add(effect);
                continue;
            }
            if (effect.Value1 < 0) throw new InvalidDataException("事件增减量须非负，方向由display_id表示。");
            var value = effect.DisplayId == 1 ? effect.Value1 : -effect.Value1;
            var bit = 1 << (effect.Value0 switch { <= 5 => effect.Value0 - 1, 10 => 5, 11 => 6, 20 => 7, _ => 8 });
            repeatedResource |= (ResourceMask & bit) != 0;
            ResourceMask |= bit;
            switch (effect.Value0)
            {
                case 1: gain = gain with { Speed = checked(gain.Speed + value) }; break;
                case 2: gain = gain with { Stamina = checked(gain.Stamina + value) }; break;
                case 3: gain = gain with { Power = checked(gain.Power + value) }; break;
                case 4: gain = gain with { Guts = checked(gain.Guts + value) }; break;
                case 5: gain = gain with { Wisdom = checked(gain.Wisdom + value) }; break;
                case 10: gain = gain with { Vital = checked(gain.Vital + value) }; break;
                case 11: maximumVitalGain = checked(maximumVitalGain + value); break;
                case 20: motivationGain = checked(motivationGain + value); break;
                case 30: gain = gain with { SkillPoints = checked(gain.SkillPoints + value) }; break;
            }
        }
        RemainingEffects = remaining.ToImmutable();
    }

    public EventResourceResult Apply(TrainingValues before, TrainingLimits limits, int motivation, bool positiveThinking, bool migraine)
    {
        Validate(before, limits, motivation);
        if (repeatedResource)
            throw new UnknownMechanismException("同一事件结果的同资源多原子顺序尚未接入结算。");
        if (migraine && motivationGain > 0 && !RemainingEffects.IsEmpty)
            throw new UnknownMechanismException("片头痛期间的干劲增加同时带有未解释效果，状态解除与奖励顺序尚未接入。");
        if (maximumVitalGain < 0 || gain.Speed < 0 || gain.Stamina < 0 || gain.Power < 0 || gain.Guts < 0 || gain.Wisdom < 0)
            throw new UnknownMechanismException("事件的负属性或体力上限降低尚未接入资源结算。");
        // The reference max-vital cap is 120; Tucker observations support applying the new cap before recovery.
        var vital = VitalRules.Apply(before.Vital, limits.Vital, gain.Vital, maximumVitalGain);
        var skillPoints = checked(before.SkillPoints + gain.SkillPoints);
        if (skillPoints < 0) throw new UnknownMechanismException("事件技能Pt减少超过当前余额，尚未接入下限处理。");
        var changedMotivation = MotivationRules.Apply(motivation, motivationGain, positiveThinking, migraine);
        var speed = Attribute(before.Speed, limits.Speed, gain.Speed);
        var stamina = Attribute(before.Stamina, limits.Stamina, gain.Stamina);
        var power = Attribute(before.Power, limits.Power, gain.Power);
        var guts = Attribute(before.Guts, limits.Guts, gain.Guts);
        var wisdom = Attribute(before.Wisdom, limits.Wisdom, gain.Wisdom);
        return new(new(speed.Value, stamina.Value, power.Value, guts.Value, wisdom.Value,
            vital.Vital, skillPoints),
            limits with { Vital = vital.Limit }, changedMotivation.Value, changedMotivation.ConsumePositiveThinking,
            new(speed.Overflow, stamina.Overflow, power.Overflow, guts.Overflow, wisdom.Overflow));
    }

    public static void Validate(TrainingValues resources, TrainingLimits limits, int motivation)
    {
        if (motivation is < 1 or > 5 || limits.Vital is < 1 or > VitalRules.Maximum || resources.Vital < 0 || resources.Vital > limits.Vital
            || resources.SkillPoints < 0 || resources.Speed < 0 || resources.Speed > limits.Speed
            || resources.Stamina < 0 || resources.Stamina > limits.Stamina || resources.Power < 0 || resources.Power > limits.Power
            || resources.Guts < 0 || resources.Guts > limits.Guts || resources.Wisdom < 0 || resources.Wisdom > limits.Wisdom)
            throw new InvalidDataException("事件前态的属性、上限、体力、技能Pt或干劲无效。");
    }

    internal static (int Value, int Overflow) Attribute(int before, int limit, int value)
    {
        // The over-1200 portion is halved with a one-point minimum, including a crossing action.
        var lower = Math.Min(value, Math.Max(0, 1200 - before));
        var upper = value - lower;
        var gain = (long)lower + (upper > 0 ? Math.Max(1, upper / 2) : 0);
        var after = (int)Math.Min(limit, before + gain);
        return (after, checked((int)(gain - (after - before))));
    }
}
