using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Base.Training;

namespace UmaAiSharp.Base.Events;

public readonly record struct DeclaredEventEffect(int DisplayId, int Value0, int Value1, int Value2);
public sealed record DeclaredEventBranch(ImmutableArray<DeclaredEventEffect> Effects, bool MissingDeclaration = false);
public sealed record DeclaredEventChoice(int ChoiceNumber, ImmutableArray<DeclaredEventBranch> Branches);
public readonly record struct EventResourceResult(TrainingValues Resources, TrainingLimits Limits, int Motivation,
    bool ConsumePositiveThinking, AttributeValues Overflow);

// Compiles the resource portion of a revealed reward. Other effects retain their original atoms.
public sealed class DeclaredEventRewards
{
    readonly TrainingValues gain;
    readonly int maximumVitalGain, motivationGain;
    readonly int repeatedResources;
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
            repeatedResources |= ResourceMask & bit;
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

    public EventResourceResult Apply(TrainingValues before, TrainingLimits limits, int motivation, bool positiveThinking, bool migraine,
        List<string>? ignored = null)
    {
        Validate(before, limits, motivation);
        var recovery = Known(5, 10, gain.Vital);
        var maxGain = Known(6, 11, maximumVitalGain);
        if (maxGain < 0 || (repeatedResources & (1 << 6)) != 0)
        {
            Ignore("体力上限变化顺序或下降量未核验，忽略本分支的体力上限与回体。");
            maxGain = recovery = 0;
        }
        var moodGain = Known(7, 20, motivationGain);
        if (migraine && moodGain > 0 && !RemainingEffects.IsEmpty)
        {
            Ignore("片头痛解除与干劲奖励的顺序未核验，忽略干劲增加。");
            moodGain = 0;
        }
        // The reference max-vital cap is 120; Tucker observations support applying the new cap before recovery.
        var vital = VitalRules.Apply(before.Vital, limits.Vital, recovery, maxGain);
        var skillPoints = checked(before.SkillPoints + Known(8, 30, gain.SkillPoints));
        if (skillPoints < 0)
        {
            Ignore("技能 Pt 减少超过余额，下限未核验，忽略技能 Pt 变化。");
            skillPoints = before.SkillPoints;
        }
        var changedMotivation = MotivationRules.Apply(motivation, moodGain, positiveThinking, migraine);
        var speed = SettleAttribute(0, before.Speed, limits.Speed, gain.Speed);
        var stamina = SettleAttribute(1, before.Stamina, limits.Stamina, gain.Stamina);
        var power = SettleAttribute(2, before.Power, limits.Power, gain.Power);
        var guts = SettleAttribute(3, before.Guts, limits.Guts, gain.Guts);
        var wisdom = SettleAttribute(4, before.Wisdom, limits.Wisdom, gain.Wisdom);
        return new(new(speed.Value, stamina.Value, power.Value, guts.Value, wisdom.Value,
            vital.Vital, skillPoints),
            limits with { Vital = vital.Limit }, changedMotivation.Value, changedMotivation.ConsumePositiveThinking,
            new(speed.Overflow, stamina.Overflow, power.Overflow, guts.Overflow, wisdom.Overflow));

        (int Value, int Overflow) SettleAttribute(int index, int current, int limit, int delta)
        {
            delta = Known(index, index + 1, delta);
            if (delta >= 0) return Attribute(current, limit, delta);
            // Negative declared gains use display units even above 1200 (packets-0003 #14914).
            if (current + delta >= 0) return (current + delta, 0);
            Ignore($"属性 {index + 1} 扣减超过当前值，下限未核验。");
            return (current, 0);
        }

        int Known(int bit, int target, int value)
        {
            if ((repeatedResources & (1 << bit)) == 0) return value;
            Ignore($"奖励目标 {target} 出现多次，同一资源的结算顺序未核验。");
            return 0;
        }

        void Ignore(string detail)
        {
            if (ignored is null) throw new UnknownMechanismException(detail);
            ignored.Add(detail);
        }
    }

    public static void Validate(TrainingValues resources, TrainingLimits limits, int motivation)
    {
        if (motivation is < 1 or > 5 || limits.Vital is < 1 or > VitalRules.Maximum || resources.Vital < 0 || resources.Vital > limits.Vital
            || resources.SkillPoints < 0 || resources.Speed < 0 || resources.Speed > limits.Speed
            || resources.Stamina < 0 || resources.Stamina > limits.Stamina || resources.Power < 0 || resources.Power > limits.Power
            || resources.Guts < 0 || resources.Guts > limits.Guts || resources.Wisdom < 0 || resources.Wisdom > limits.Wisdom)
            throw new InvalidDataException("事件前态的属性、上限、体力、技能Pt或干劲无效。");
    }

    static (int Value, int Overflow) Attribute(int before, int limit, int value)
    {
        var gain = AttributeGain.FromRaw(before, value);
        var actual = Math.Min(limit - before, gain);
        return (before + actual, gain - actual);
    }
}
