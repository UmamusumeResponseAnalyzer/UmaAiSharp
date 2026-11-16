using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Scoring;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Hints;

public sealed record PioneerCardHints(int CardId, ImmutableArray<HintSkill> Skills, TrainingValues AttributeReward);
public sealed record PioneerHintProgress(ImmutableArray<HintLevel> Levels, ImmutableArray<OwnedScoringSkill> OwnedSkills,
    ImmutableArray<int> DisabledSkillIds, int TalentLevel, ImmutableArray<int> Aptitudes);

public sealed class PioneerHintRules
{
    readonly record struct Card(TrainingSupport Support, int[] SkillSlots, TrainingValues AttributeReward);
    readonly Card[] cards = new Card[6];
    readonly Dictionary<HintSkill, int> slots = [];
    readonly List<int> initialLevels = [];
    public int StateCapacity => initialLevels.Count;
    public long SharedMemoryBytes => 512L + 64L * initialLevels.Count + cards.Sum(card => 4L * (card.SkillSlots?.Length ?? 0));

    public PioneerHintRules(ImmutableArray<PioneerCardHints> catalog, IEnumerable<InheritanceSkill> skills,
        ImmutableArray<TrainingSupport> deck, PioneerHintProgress progress, IEnumerable<HintSkill>? observed = null)
    {
        var ownedIds = progress.OwnedSkills.Select(skill => skill.Id).ToHashSet();
        var learned = skills.Where(skill => ownedIds.Contains(skill.Id))
            .Select(skill => new HintSkill(skill.GroupId, skill.Rarity)).Distinct().ToImmutableArray();
        foreach (var hint in progress.Levels) initialLevels[Slot(hint.Skill)] = hint.Level;
        foreach (var support in deck)
        {
            if (support.TrainingCommandId == 0) continue;
            var pool = catalog.Single(card => card.CardId == support.CardId);
            var entries = HintRewardPool.Evaluate(pool.Skills, progress.Levels, learned);
            cards[support.Position - 1] = new(support,
                [.. entries.Where(entry => entry.Exclusion == HintPoolExclusion.None).Select(entry => Slot(entry.Skill))],
                pool.AttributeReward);
        }
        if (observed is not null)
            foreach (var skill in observed) Slot(skill);
    }

    // Deck pools and both inheritance lotteries use the same level for a given skill group and rarity.
    internal int Slot(HintSkill skill)
    {
        if (slots.TryGetValue(skill, out var index)) return index;
        slots.Add(skill, initialLevels.Count);
        initialLevels.Add(-1);
        return initialLevels.Count - 1;
    }

    internal ReadOnlySpan<int> InitialLevels => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(initialLevels);
    internal int FindSlot(HintSkill skill) => slots.GetValueOrDefault(skill, -1);

    internal ImmutableArray<HintLevel> ReadLevels(ReadOnlySpan<int> levels)
    {
        var result = ImmutableArray.CreateBuilder<HintLevel>();
        foreach (var (skill, slot) in slots)
            if (levels[slot] >= 0) result.Add(new(skill, levels[slot]));
        return result.ToImmutable();
    }

    static (int Level, int Count) RewardSize(TrainingSupport support, int bond)
    {
        var level = 1;
        var count = 1;
        foreach (var effect in support.Effects)
        {
            if (effect.Type == 17) level += effect.Value;
            if (effect.Type == 33) count += effect.Value;
        }
        foreach (var effect in support.UniqueEffects)
        {
            if (effect.Type == 17) level += effect.Value;
            if (effect.Type == 33) count += effect.Value;
            if (effect.Type != 101 || bond < effect.Value) continue;
            if (effect.Parameter1 == 17) level += effect.Parameter2;
            if (effect.Parameter3 == 17) level += effect.Parameter4;
            if (effect.Parameter1 == 33) count += effect.Parameter2;
            if (effect.Parameter3 == 33) count += effect.Parameter4;
        }
        return (level, count);
    }

    internal TrainingValues Draw(int position, int bond, Span<int> levels, ref SimulationRandom random)
    {
        var (support, pool, attributes) = cards[position];
        var (level, count) = RewardSize(support, bond);
        var eligible = 0;
        Span<int> available = stackalloc int[pool.Length];
        foreach (var slot in pool)
            if (levels[slot] < 5) available[eligible++] = slot;
        var draw = random.NextScaled(eligible + 1);
        if (draw == eligible) return attributes;
        // The extra Hint applies to the skill branch. Observed double awards use different skills.
        for (var i = 0; i < count; i++)
        {
            var slot = available[draw];
            levels[slot] = Math.Min(5, Math.Max(0, levels[slot]) + level);
            available[draw] = available[--eligible];
            if (eligible == 0 || i + 1 == count) break;
            draw = random.NextScaled(eligible);
        }
        return default;
    }

    internal (TrainingValues Attributes, double AttributeProbability, double EquivalentSkillPoints) ExpectedReward(int position, int bond,
        ReadOnlySpan<int> levels, ReadOnlySpan<int> aptitudes, in TrainingValues resources, int discount, FutureSkillRating rating)
    {
        var (support, pool, attributes) = cards[position];
        var (level, count) = RewardSize(support, bond);
        var eligible = 0;
        var skillPoints = 0.0;
        foreach (var slot in pool)
        {
            if (levels[slot] >= 5) continue;
            eligible++;
            skillPoints += rating.HintValue(slot, Math.Min(5, Math.Max(0, levels[slot]) + level),
                levels, aptitudes, resources, discount);
        }
        return (attributes, 1.0 / (eligible + 1), Math.Min(count, eligible) * skillPoints / (eligible + 1));
    }
}
