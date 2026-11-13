using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public readonly record struct SupportEffect(int Type, int Value);
public readonly record struct SupportUniqueEffect(int Type, int Value, int Parameter1, int Parameter2, int Parameter3, int Parameter4);
public sealed record TrainingSupport(int CardId, int Level, int Position, int TrainingCommandId, int Bond,
    ImmutableArray<SupportEffect> Effects, ImmutableArray<SupportUniqueEffect> UniqueEffects);
public sealed record ResolvedTrainingSupport(int CardId, bool FriendshipEligible, TrainingValues BaseBonuses,
    int TrainingEffect, int MotivationEffect, int WisdomRecovery,
    ImmutableArray<int> FriendshipBonuses, ImmutableArray<int> FailureReductions, ImmutableArray<int> VitalReductions,
    ImmutableArray<int> TrainingEffectParts, ImmutableArray<int> MotivationEffectParts)
{
    internal ImmutableArray<SupportEffect> FriendshipTrainingEffects { get; init; } = [];

    internal long FriendshipTrainingBonus(int type, int scale = 100)
    {
        var bonus = 0L;
        foreach (var effect in FriendshipTrainingEffects)
            if (effect.Type == type) bonus = checked(bonus + (long)effect.Value * scale / 100);
        return bonus;
    }
}

public static class TrainingSupportEffects
{
    public const string Version = "local-support-training-effects-v2";

    // Resolves effects at an already supplied card level; it does not draw locations or trigger friendship randomly.
    public static ResolvedTrainingSupport Resolve(TrainingSupport support, int commandId, int trainingLevel, int fans)
    {
        var values = new Dictionary<int, int>();
        var friendship = ImmutableArray.CreateBuilder<int>();
        var failure = ImmutableArray.CreateBuilder<int>();
        var vital = ImmutableArray.CreateBuilder<int>();
        var training = ImmutableArray.CreateBuilder<int>();
        var motivation = ImmutableArray.CreateBuilder<int>();
        void Add(int type, int value)
        {
            if (type == 8) training.Add(value);
            if (type == 2) motivation.Add(value);
            switch (type)
            {
                case 0: return;
                case 1: friendship.Add(value); return;
                case 27: failure.Add(value); return;
                case 28: vital.Add(value); return;
                case 2 or 3 or 4 or 5 or 6 or 7 or 8 or 30 or 31:
                    values[type] = checked(values.GetValueOrDefault(type) + value); return;
                // These fields affect initial state, events, races, hints or placement, not the selected training gain.
                case 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 25 or 26 or 32 or 33: return;
                default: throw new UnknownMechanismException($"支援卡{support.CardId}的训练效果type={type}尚未实现。");
            }
        }
        foreach (var effect in support.Effects) Add(effect.Type, effect.Value);
        foreach (var effect in support.UniqueEffects)
        {
            switch (effect.Type)
            {
                case < 100: Add(effect.Type, effect.Value); break;
                case 101:
                    if (support.Bond >= effect.Value)
                    {
                        Add(effect.Parameter1, effect.Parameter2);
                        Add(effect.Parameter3, effect.Parameter4);
                    }
                    break;
                case 104:
                    if (effect.Value <= 0) throw new InvalidDataException("粉丝数固有的除数必须大于零。");
                    Add(8, Math.Min(fans / effect.Value, effect.Parameter1));
                    break;
                case 111: Add(effect.Value, checked(trainingLevel * effect.Parameter1)); break;
                // These affect initial attributes, training bonds or next-turn placement, not resource gains.
                case 105 or 113 or 121 or 122: break;
                default: throw new UnknownMechanismException($"支援卡{support.CardId}的固有type={effect.Type}尚未实现。");
            }
        }
        return new(support.CardId, support.TrainingCommandId == commandId && support.Bond >= 80,
            new(values.GetValueOrDefault(3), values.GetValueOrDefault(4), values.GetValueOrDefault(5),
                values.GetValueOrDefault(6), values.GetValueOrDefault(7), 0, values.GetValueOrDefault(30)),
            values.GetValueOrDefault(8), values.GetValueOrDefault(2), values.GetValueOrDefault(31),
            friendship.ToImmutable(), failure.ToImmutable(), vital.ToImmutable(), training.ToImmutable(), motivation.ToImmutable())
        {
            FriendshipTrainingEffects = ReadFriendshipTrainingEffects(support),
        };
    }

    internal static ImmutableArray<SupportEffect> ReadFriendshipTrainingEffects(TrainingSupport support)
    {
        ImmutableArray<SupportEffect>.Builder? effects = null;
        foreach (var effect in support.UniqueEffects)
        {
            if (effect.Type != 113) continue;
            if (effect.Value is not (2 or 8 or 28) || effect.Parameter1 < 0 || effect.Value == 28 && effect.Parameter1 > 100
                || effect.Parameter2 != 0 || effect.Parameter3 != 0 || effect.Parameter4 != 0)
                throw new UnknownMechanismException($"支援卡{support.CardId}的友情训练固有113参数尚未接入。");
            (effects ??= ImmutableArray.CreateBuilder<SupportEffect>()).Add(new(effect.Value, effect.Parameter1));
        }
        return effects?.ToImmutable() ?? [];
    }
}
