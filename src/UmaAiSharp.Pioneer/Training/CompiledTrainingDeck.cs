using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

// Condition tables and their effect atoms are immutable and shared by trajectories.
public sealed class CompiledTrainingDeck
{
    internal const int BondTableBytes = 64 * sizeof(int);
    [InlineArray(64)]
    struct BondBonuses { int first; }

    readonly FrozenDictionary<int, CompiledSupport> cards;
    readonly BondBonuses bondBonuses;
    public long SharedMemoryBytes { get; }

    public CompiledTrainingDeck(ImmutableArray<TrainingSupport> definitions)
    {
        if (definitions.IsDefault) throw new ArgumentException("编成训练定义必须显式提供。", nameof(definitions));
        var compiled = new Dictionary<int, CompiledSupport>(definitions.Length);
        var bonuses = new BondBonuses();
        var bytes = 512L + BondTableBytes;
        foreach (var definition in definitions)
        {
            if (definition.Position is < 1 or > 6)
                throw new ArgumentException("编成位置必须为1..6。", nameof(definitions));
            foreach (var effect in definition.UniqueEffects)
            {
                if (effect.Type is >= 100 and not (101 or 104 or 105 or 111 or 113 or 121 or 122))
                    throw new UnknownMechanismException($"支援卡{definition.CardId}的固有type={effect.Type}尚未实现，不能编译该编成。");
                if (effect.Type != 121) continue;
                if (effect.Value < 0 || effect.Parameter1 < 0 || effect.Parameter2 != 0 || effect.Parameter3 != 0 || effect.Parameter4 != 0)
                    throw new UnknownMechanismException($"支援卡{definition.CardId}的全队羁绊固有121参数未支持。");
                for (var mask = 0; mask < 64; mask++)
                    bonuses[mask] = checked(bonuses[mask] + effect.Value
                        + ((mask & (1 << (definition.Position - 1))) != 0 ? effect.Parameter1 : 0));
            }
            var card = new CompiledSupport(definition, ExecutionMemoryPlan.LimitBytes - bytes);
            bytes = checked(bytes + card.SharedMemoryBytes);
            if (!compiled.TryAdd(definition.Position, card))
                throw new ArgumentException($"训练编成的位置{definition.Position}重复。", nameof(definitions));
        }
        cards = compiled.ToFrozenDictionary();
        bondBonuses = bonuses;
        SharedMemoryBytes = bytes;
    }

    internal int TrainingBondBonus(byte participants) => bondBonuses[participants];

    internal ResolvedTrainingSupport Resolve(TrainingSupport support, int command, int level, int fans)
    {
        if (!cards.TryGetValue(support.Position, out var card))
            throw new ArgumentException($"支援位置{support.Position}没有编译后的训练定义。");
        var definition = card.Definition;
        if (support.CardId != definition.CardId || support.Level != definition.Level || support.TrainingCommandId != definition.TrainingCommandId
            || !support.Effects.AsSpan().SequenceEqual(definition.Effects.AsSpan())
            || !support.UniqueEffects.AsSpan().SequenceEqual(definition.UniqueEffects.AsSpan()))
            throw new ArgumentException($"支援位置{support.Position}的卡片或静态效果与编译定义不一致，必须重新编译编成。");
        return card.Resolve(support.Bond, command, level, fans);
    }

    // The runtime has already checked card identity and immutable effects at compilation.
    internal ResolvedTrainingSupport Resolve(int position, int bond, int command, int level, int fans)
        => cards[position].Resolve(bond, command, level, fans);

    sealed class CompiledSupport
    {
        public TrainingSupport Definition { get; }
        readonly ResolvedTrainingSupport ordinary;
        readonly ResolvedTrainingSupport friendly;
        readonly int dynamicStart;
        readonly int[] bondThresholds;
        readonly int bondCount;
        readonly int[] fanThresholds;
        readonly int fanCount;
        readonly int levels;
        readonly Resolution[] resolutions;
        public long SharedMemoryBytes { get; }

        public CompiledSupport(TrainingSupport definition, long availableBytes)
        {
            Definition = definition;
            while (dynamicStart < definition.UniqueEffects.Length
                && (definition.UniqueEffects[dynamicStart].Type < 100 || definition.UniqueEffects[dynamicStart].Type is 105 or 113 or 121 or 122))
                dynamicStart++;
            // Only the static prefix is moved: checked additions and atom order remain identical to the reference.
            var prefix = definition with { Bond = 0, UniqueEffects = definition.UniqueEffects[..dynamicStart] };
            ordinary = TrainingSupportEffects.Resolve(prefix, definition.TrainingCommandId, 1, 0);
            if (dynamicStart != definition.UniqueEffects.Length)
                ordinary = ordinary with { FriendshipTrainingEffects = TrainingSupportEffects.ReadFriendshipTrainingEffects(definition) };
            friendly = ordinary with { FriendshipEligible = true };
            bondThresholds = new int[definition.UniqueEffects.Length + 1];
            bondThresholds[0] = int.MinValue;
            var next = 1;
            levels = 1;
            var fanCapacity = 1L;
            foreach (var effect in definition.UniqueEffects)
            {
                if (effect.Type == 101) bondThresholds[next++] = effect.Value;
                if (effect.Type == 111) levels = 5;
                if (effect.Type == 104 && effect.Value > 0 && effect.Parameter1 > 0)
                    fanCapacity = checked(fanCapacity + Math.Min(int.MaxValue / effect.Value, effect.Parameter1));
            }
            Array.Sort(bondThresholds, 0, next);
            bondCount = 1;
            for (var index = 1; index < next; index++)
                if (bondThresholds[index] != bondThresholds[bondCount - 1]) bondThresholds[bondCount++] = bondThresholds[index];
            var rowCapacity = checked(fanCapacity * bondCount * levels);
            // Reserve retained rows, effect arrays and compilation scratch before allocating the table.
            SharedMemoryBytes = checked(4096L + fanCapacity * 8 + bondThresholds.Length * 8L
                + rowCapacity * (640L + 16L * (definition.Effects.Length + 2L * definition.UniqueEffects.Length)));
            if (SharedMemoryBytes > availableBytes)
                throw new InvalidOperationException($"支援卡{definition.CardId}的条件预编译需要{SharedMemoryBytes}字节，超过共享容量预算。");
            fanThresholds = new int[checked((int)fanCapacity)];
            next = 1;
            foreach (var effect in definition.UniqueEffects)
                if (effect.Type == 104 && effect.Value > 0 && effect.Parameter1 > 0)
                    for (var count = 1; count <= Math.Min(int.MaxValue / effect.Value, effect.Parameter1); count++)
                        fanThresholds[next++] = checked(count * effect.Value);
            Array.Sort(fanThresholds);
            fanCount = 1;
            for (var index = 1; index < fanThresholds.Length; index++)
                if (fanThresholds[index] != fanThresholds[fanCount - 1]) fanThresholds[fanCount++] = fanThresholds[index];
            resolutions = new Resolution[checked(fanCount * bondCount * levels)];
            var row = 0;
            foreach (var bond in bondThresholds.AsSpan(0, bondCount))
                for (var level = 1; level <= levels; level++)
                    for (var fan = 0; fan < fanCount; fan++)
                    {
                        try
                        {
                            var value = ResolveConditions(bond, level, fanThresholds[fan]);
                            resolutions[row] = new(value, ReferenceEquals(value, ordinary) ? friendly
                                : value with { FriendshipEligible = true }, null, 0);
                        }
                        catch (UnknownMechanismException error) { resolutions[row] = new(null, null, error.Message, 1); }
                        catch (InvalidDataException error) { resolutions[row] = new(null, null, error.Message, 2); }
                        catch (OverflowException error) { resolutions[row] = new(null, null, error.Message, 3); }
                        row++;
                    }
        }

        public ResolvedTrainingSupport Resolve(int bond, int command, int level, int fans)
        {
            var bondIndex = bondCount == 1 ? 0 : Array.BinarySearch(bondThresholds, 0, bondCount, bond);
            if (bondIndex < 0) bondIndex = ~bondIndex - 1;
            var fanIndex = fanCount == 1 ? 0 : Array.BinarySearch(fanThresholds, 0, fanCount, fans);
            if (fanIndex < 0) fanIndex = ~fanIndex - 1;
            ref readonly var result = ref resolutions[(bondIndex * levels + (levels == 1 ? 0 : level - 1)) * fanCount + fanIndex];
            // Preserve the original failure at the condition that activates it; exceptions are never shared across workers.
            return result.Failure switch
            {
                0 => (bond >= 80 && command == Definition.TrainingCommandId ? result.Friendly : result.Ordinary)!,
                1 => throw new UnknownMechanismException(result.Error!),
                2 => throw new InvalidDataException(result.Error),
                3 => throw new OverflowException(result.Error),
                _ => throw new InvalidOperationException("训练条件表包含无效结果。")
            };
        }

        readonly record struct Resolution(ResolvedTrainingSupport? Ordinary, ResolvedTrainingSupport? Friendly, string? Error, byte Failure);

        ResolvedTrainingSupport ResolveConditions(int bond, int level, int fans)
        {
            if (dynamicStart == Definition.UniqueEffects.Length) return ordinary;
            var values = new EffectsAccumulator(ordinary);
            for (var i = dynamicStart; i < Definition.UniqueEffects.Length; i++)
            {
                var effect = Definition.UniqueEffects[i];
                switch (effect.Type)
                {
                    case < 100: values.Add(effect.Type, effect.Value); break;
                    case 101:
                        if (bond >= effect.Value)
                        {
                            values.Add(effect.Parameter1, effect.Parameter2);
                            values.Add(effect.Parameter3, effect.Parameter4);
                        }
                        break;
                    case 104:
                        if (effect.Value <= 0) throw new InvalidDataException("粉丝数固有的除数必须大于零。");
                        values.Add(8, Math.Min(fans / effect.Value, effect.Parameter1));
                        break;
                    case 111: values.Add(effect.Value, checked(level * effect.Parameter1)); break;
                    case 105 or 113 or 121 or 122: break;
                    default: throw new UnknownMechanismException($"支援卡{Definition.CardId}的固有type={effect.Type}尚未实现。");
                }
            }
            return values.Build();
        }
    }

    struct EffectsAccumulator(ResolvedTrainingSupport seed)
    {
        TrainingValues bonuses = seed.BaseBonuses;
        int trainingValue = seed.TrainingEffect, motivationValue = seed.MotivationEffect, wisdomRecovery = seed.WisdomRecovery;
        bool changed;
        ImmutableArray<int>.Builder? friendship, failure, vital, training, motivation;

        public void Add(int type, int value)
        {
            if (type == 8) (training ??= seed.TrainingEffectParts.ToBuilder()).Add(value);
            if (type == 2) (motivation ??= seed.MotivationEffectParts.ToBuilder()).Add(value);
            switch (type)
            {
                case 0: return;
                case 1: (friendship ??= seed.FriendshipBonuses.ToBuilder()).Add(value); break;
                case 27: (failure ??= seed.FailureReductions.ToBuilder()).Add(value); break;
                case 28: (vital ??= seed.VitalReductions.ToBuilder()).Add(value); break;
                case 2: motivationValue = checked(motivationValue + value); break;
                case 3: bonuses = bonuses with { Speed = checked(bonuses.Speed + value) }; break;
                case 4: bonuses = bonuses with { Stamina = checked(bonuses.Stamina + value) }; break;
                case 5: bonuses = bonuses with { Power = checked(bonuses.Power + value) }; break;
                case 6: bonuses = bonuses with { Guts = checked(bonuses.Guts + value) }; break;
                case 7: bonuses = bonuses with { Wisdom = checked(bonuses.Wisdom + value) }; break;
                case 8: trainingValue = checked(trainingValue + value); break;
                case 30: bonuses = bonuses with { SkillPoints = checked(bonuses.SkillPoints + value) }; break;
                case 31: wisdomRecovery = checked(wisdomRecovery + value); break;
                case 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 25 or 26 or 32 or 33: return;
                default: throw new UnknownMechanismException($"支援卡{seed.CardId}的训练效果type={type}尚未实现。");
            }
            changed = true;
        }

        public readonly ResolvedTrainingSupport Build() => !changed ? seed : new(seed.CardId, seed.FriendshipEligible,
            bonuses, trainingValue, motivationValue, wisdomRecovery,
            friendship?.ToImmutable() ?? seed.FriendshipBonuses,
            failure?.ToImmutable() ?? seed.FailureReductions,
            vital?.ToImmutable() ?? seed.VitalReductions,
            training?.ToImmutable() ?? seed.TrainingEffectParts,
            motivation?.ToImmutable() ?? seed.MotivationEffectParts)
        { FriendshipTrainingEffects = seed.FriendshipTrainingEffects };
    }
}
