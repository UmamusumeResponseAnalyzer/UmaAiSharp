using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UmaAiSharp.Core;

namespace UmaAiSharp.Base.Training;

public readonly record struct SupportEffect(int Type, int Value);
public readonly record struct SupportUniqueEffect(int Type, int Value, int Parameter1, int Parameter2, int Parameter3, int Parameter4);
public sealed record TrainingSupport(int CardId, int Level, int Position, int TrainingCommandId, int Bond,
    ImmutableArray<SupportEffect> Effects, ImmutableArray<SupportUniqueEffect> UniqueEffects);
public sealed record ResolvedTrainingSupport(int CardId, bool FriendshipEligible, TrainingValues BaseBonuses,
    int TrainingEffect, int MotivationEffect, int WisdomRecovery,
    ImmutableArray<int> FriendshipBonuses, ImmutableArray<int> FailureReductions, ImmutableArray<int> VitalReductions,
    ImmutableArray<int> TrainingEffectParts, ImmutableArray<int> MotivationEffectParts)
{
    [JsonIgnore]
    public ImmutableArray<SupportEffect> FriendshipTrainingEffects { get; internal init; } = [];
    [JsonIgnore]
    public ImmutableArray<SupportUniqueEffect> PanelTrainingEffects { get; internal init; } = [];

    public int PanelTrainingBonus(int vital, int maxVital, int supportCount, int? totalSupportBond,
        int scale = 100, ReadOnlySpan<int> bonds = default)
    {
        var bonus = 0;
        foreach (var effect in PanelTrainingEffects)
        {
            var teamBond = totalSupportBond;
            if (effect.Type == 109 && !bonds.IsEmpty)
            {
                teamBond = 0;
                foreach (var bond in bonds) teamBond += bond;
            }
            var value = effect.Type switch
            {
                108 => Math.Min(effect.Parameter4, (maxVital - effect.Parameter1) * effect.Parameter2 / 100 + effect.Parameter3),
                109 => (teamBond ?? throw new InvalidDataException($"支援卡 {CardId} 固有 109 需要全队羁绊。")) / effect.Parameter1,
                110 => effect.Parameter1 * supportCount,
                114 => effect.Parameter2 - Math.Max(0, 100 - vital) / effect.Parameter1,
                _ => throw new InvalidOperationException($"无效训练面板固有 {effect.Type}。"),
            };
            bonus = checked(bonus + value * scale / 100);
        }
        return bonus;
    }
}

public static class TrainingSupportEffects
{
    public static ReadOnlySpan<int> SpecialtyCommands => [101, 105, 102, 103, 106];

    // Uniques 103 and 120 count the full deck, including cards absent from the selected training.
    public static TrainingSupport BindDeckBonuses(TrainingSupport support, IEnumerable<(int CardId, int SupportType, int CommandId)> deck)
    {
        if (!support.UniqueEffects.Any(effect => effect.Type is 103 or 120)) return support;
        Span<int> counts = stackalloc int[7];
        counts.Clear();
        foreach (var card in deck)
        {
            var index = card.SupportType switch { 1 => SpecialtyCommands.IndexOf(card.CommandId), 2 => 5, 3 => 6, _ => -1 };
            if (index < 0) throw new UnknownMechanismException($"支援卡{card.CardId}的编成类型无法用于固有103或120。");
            counts[index]++;
        }
        var distinctTypes = counts.Length - counts.Count(0);
        // Friend and group are distinct for 103 but both grant skill points for 120.
        counts[5] += counts[6];
        ReadOnlySpan<int> targets = [3, 4, 5, 6, 7, 30];
        var effects = ImmutableArray.CreateBuilder<SupportUniqueEffect>();
        foreach (var effect in support.UniqueEffects)
        {
            if (effect.Type == 103)
            {
                if (distinctTypes >= effect.Value) effects.Add(new(8, effect.Parameter1, 0, 0, 0, 0));
                continue;
            }
            if (effect.Type != 120)
            {
                effects.Add(effect);
                continue;
            }
            if (effect.Value != 1 || effect.Parameter1 != 80 || effect.Parameter2 != 1 || effect.Parameter3 != 2 || effect.Parameter4 != 0)
                throw new UnknownMechanismException($"支援卡{support.CardId}的编成固有120参数尚未接入。");
            // The deck is fixed, so only the bond threshold remains a runtime condition.
            for (var index = 0; index < targets.Length; index += 2)
                effects.Add(new(101, effect.Parameter1, targets[index], Math.Min(effect.Parameter3, counts[index]),
                    targets[index + 1], Math.Min(effect.Parameter3, counts[index + 1])));
        }
        return support with { UniqueEffects = effects.ToImmutable() };
    }

    // The command is the normalized training specialty. Group-card or scenario-granted friendship has separate conditions.
    public static bool IsFriendshipEligible(int specialtyCommand, int trainingCommand, int bond)
        => specialtyCommand > 0 && specialtyCommand == trainingCommand && bond >= 80;

    internal static void ValidateTrainingConditions(int trainingLevel, int fans)
    {
        if (trainingLevel is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(trainingLevel), trainingLevel, "支援效果仅覆盖训练等级1..5。");
        ArgumentOutOfRangeException.ThrowIfNegative(fans);
    }

    // Ordinary card effects are already resolved at the supplied card level.
    public static ResolvedTrainingSupport Resolve(TrainingSupport support, int commandId, int trainingLevel, int fans)
    {
        ValidateTrainingConditions(trainingLevel, fans);
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
                case 41:
                    for (var attribute = 3; attribute <= 7; attribute++)
                        values[attribute] = checked(values.GetValueOrDefault(attribute) + value);
                    return;
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
                case 102:
                    if (support.Bond >= effect.Value && commandId != support.TrainingCommandId) Add(8, effect.Parameter1);
                    break;
                case 104:
                    if (effect.Value <= 0) throw new InvalidDataException("粉丝数固有的除数必须大于零。");
                    Add(8, Math.Min(fans / effect.Value, effect.Parameter1));
                    break;
                case 111: Add(effect.Value, checked(trainingLevel * effect.Parameter1)); break;
                // These affect initial attributes, training bonds or next-turn placement, not resource gains.
                case 105 or 108 or 109 or 110 or 112 or 113 or 114 or 115 or 118 or 121 or 122: break;
                default: throw new UnknownMechanismException($"支援卡{support.CardId}的固有type={effect.Type}尚未实现。");
            }
        }
        return new(support.CardId, IsFriendshipEligible(support.TrainingCommandId, commandId, support.Bond),
            new(values.GetValueOrDefault(3), values.GetValueOrDefault(4), values.GetValueOrDefault(5),
                values.GetValueOrDefault(6), values.GetValueOrDefault(7), 0, values.GetValueOrDefault(30)),
            values.GetValueOrDefault(8), values.GetValueOrDefault(2), values.GetValueOrDefault(31),
            friendship.ToImmutable(), failure.ToImmutable(), vital.ToImmutable(), training.ToImmutable(), motivation.ToImmutable())
        {
            FriendshipTrainingEffects = ReadFriendshipTrainingEffects(support),
            PanelTrainingEffects = [.. support.UniqueEffects.Where(effect => effect.Type is 108 or 109 or 110 or 114)],
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
