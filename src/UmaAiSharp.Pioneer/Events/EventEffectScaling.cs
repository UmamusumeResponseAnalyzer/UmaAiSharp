using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Events;

// Event bonuses belong to the event's support, not to the whole deck.
public readonly record struct EventEffectScaling(int RecoveryPercent, int ResourcePercent)
{
    public static EventEffectScaling FromSupport(TrainingSupport support)
    {
        var recovery = 0;
        var resources = 0;
        foreach (var effect in support.Effects) Add(effect.Type, effect.Value);
        foreach (var effect in support.UniqueEffects)
        {
            if (effect.Type < 100) Add(effect.Type, effect.Value);
            else if (effect.Type == 101 && support.Bond >= effect.Value)
            {
                Add(effect.Parameter1, effect.Parameter2);
                Add(effect.Parameter3, effect.Parameter4);
            }
            else if (effect.Type is not (101 or 104 or 105 or 111 or 113 or 121 or 122))
                throw new UnknownMechanismException($"支援卡{support.CardId}的固有{effect.Type}尚未解释，不能推断事件加成。");
        }
        return new(recovery, resources);

        void Add(int type, int value)
        {
            if (type is not (25 or 26)) return;
            if (value < 0) throw new InvalidDataException("事件加成不能为负数。");
            if (type == 25) recovery = checked(recovery + value);
            else resources = checked(resources + value);
        }
    }

    public int Apply(int target, int basis)
    {
        if (basis <= 0) return basis;
        var percent = target switch { 10 => RecoveryPercent, >= 1 and <= 5 or 30 => ResourcePercent, _ => 0 };
        return checked((int)((long)basis * (100L + percent) / 100));
    }

    // Nonnegative bonuses make this map injective; a mismatch is not rounded to a nearby reward.
    public int? InferUncappedBase(int target, int observed)
    {
        if (observed < 0) return observed;
        var percent = target switch { 10 => RecoveryPercent, >= 1 and <= 5 or 30 => ResourcePercent, _ => 0 };
        var divisor = 100L + percent;
        if (divisor < 100) throw new InvalidDataException("事件加成不能为负数。");
        var basis = ((long)observed * 100 + divisor - 1) / divisor;
        return basis * divisor / 100 == observed ? checked((int)basis) : null;
    }
}
