using System.Buffers;
using System.Collections.Immutable;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Scoring;

public sealed record PioneerSkillRule(int Id, int GroupId, int Rarity, int Rate, int Grade, int? Cost,
    int StatusType, int StatusValue, int SolvableType, ImmutableArray<int> Tags);
public readonly record struct PioneerSkillTalent(int CardId, int SkillId, int Rank);
public sealed record PioneerSkillRatingData(ImmutableArray<PioneerSkillRule> Skills, ImmutableArray<PioneerSkillTalent> Talents);

internal sealed class FutureSkillRating
{
    record struct Step(PioneerSkillRule Rule, int HintSlot, ScoringSkillDefinition Score);
    sealed record Group(Step[] Chain, (int Slot, int Rate)[] HintUnlocks, int OpenRate, Step? Owned, int OwnedLevel);
    readonly record struct Choice(int Cost, int Rating);

    readonly Group[] groups;
    readonly int[] groupByHintSlot;
    readonly int maxSpend, maxChoices, startingRating;
    readonly double ptPrice;

    public long SharedMemoryBytes => 64L + 4L * groupByHintSlot.Length
        + groups.Sum(group => 64L + group.Chain.Length * 40L + group.HintUnlocks.Length * 8L);
    public long ScratchBytes => (maxSpend + 1L) * sizeof(int) * 2L;

    // shortcut: price Pt at the nominal catalogue rate until terminal scoring knows the final menu and budget.
    public double RatingPerPoint(int discount) => ptPrice * 100 / (100 - discount);

    public FutureSkillRating(PioneerSkillRatingData catalog, PioneerHintProgress progress, PioneerHintRules hints,
        int cardId, in TrainingValues resources, ReadOnlySpan<int> effects)
    {
        if (progress.Aptitudes.Length != PioneerInheritanceRules.AptitudeTargets.Length
            || progress.Aptitudes.Any(value => value is < 1 or > 8))
            throw new InvalidDataException("技能评分需要完整的十项适性。");
        var byId = catalog.Skills.ToDictionary(skill => skill.Id);
        var known = catalog.Talents.Where(talent => talent.CardId == cardId && talent.Rank <= progress.TalentLevel)
            .Select(talent => byId[talent.SkillId]).ToLookup(skill => skill.GroupId);
        var owned = progress.OwnedSkills.ToLookup(skill => byId[skill.Id].GroupId);
        var disabled = progress.DisabledSkillIds.Select(id => byId[id].GroupId).ToHashSet();
        var menu = new List<Group>();
        groupByHintSlot = new int[hints.StateCapacity];
        Array.Fill(groupByHintSlot, -1);
        foreach (var group in catalog.Skills.GroupBy(skill => skill.GroupId))
        {
            var chain = group.Where(skill => skill is { Rate: > 0, Cost: not null }).OrderBy(skill => skill.Rate).ToArray();
            var ownedSkill = owned[group.Key].SingleOrDefault();
            var initial = ownedSkill is null ? (Step?)null : Compile(byId[ownedSkill.Id]);
            var canRemove = initial?.Rule is { Rate: < 0, SolvableType: 0, Cost: not null };
            if (chain.Length == 0 && !canRemove) continue;
            var whiteRate = chain.Where(skill => skill.Rarity < 2).Select(skill => skill.Rate).DefaultIfEmpty().Max();
            var available = known[group.Key].Concat(ownedSkill is null ? [] : [byId[ownedSkill.Id]]);
            if (disabled.Contains(group.Key))
                available = available.Concat(group.Where(skill => skill.Grade >= 0).OrderBy(skill => skill.Grade).Take(1));
            var openRate = available.Select(AvailableRate).DefaultIfEmpty().Max();
            var unlocks = group.GroupBy(skill => skill.Rarity)
                .Select(rarity => (Slot: hints.FindSlot(new(group.Key, rarity.Key)), Rate: rarity.Max(AvailableRate)))
                .Where(entry => entry is { Slot: >= 0, Rate: > 0 }).ToArray();
            if (unlocks.Length == 0 && openRate <= (initial?.Rule.Rate ?? 0) && !canRemove) continue;
            if (chain.Select(skill => skill.Rate).Distinct().Count() != chain.Length
                || chain.Any(skill => skill.Cost <= 0 || skill.StatusType is < 0 or > 5))
                throw new InvalidDataException($"技能组 {group.Key} 的购买链定义无效。");
            foreach (var unlock in unlocks) groupByHintSlot[unlock.Slot] = menu.Count;
            maxSpend += chain.Sum(skill => skill.Cost!.Value) + (canRemove ? initial!.Value.Rule.Cost!.Value : 0);
            maxChoices = Math.Max(maxChoices, chain.Length + (canRemove ? 1 : 0));
            menu.Add(new([.. chain.Select(Compile)], unlocks, openRate, initial, ownedSkill?.Level ?? 0));

            int AvailableRate(PioneerSkillRule skill) => skill.Rarity < 2 ? whiteRate : skill.Cost.HasValue ? skill.Rate : 0;
        }
        groups = [.. menu];
        startingRating = BestRating(resources, hints.InitialLevels, progress.Aptitudes.AsSpan(), PurchaseDiscount(effects));
        Span<int> allAvailable = stackalloc int[hints.StateCapacity];
        allAvailable.Clear();
        Span<Choice> choices = stackalloc Choice[maxChoices];
        var cost = 0;
        var score = 0;
        foreach (var group in groups)
        {
            var count = BuildChoices(group, allAvailable, progress.Aptitudes.AsSpan(), resources, 0, choices);
            if (count == 0) continue;
            cost += choices[count - 1].Cost;
            score += choices[count - 1].Rating;
        }
        ptPrice = cost == 0 ? 0 : Math.Max(0, (double)score / cost);

        Step Compile(PioneerSkillRule skill) => new(skill, hints.FindSlot(new(skill.GroupId, skill.Rarity)),
            new(skill.Id, skill.Grade, skill.Tags));
    }

    public int Estimate(in TrainingValues resources, ReadOnlySpan<int> hints, ReadOnlySpan<int> aptitudes,
        ReadOnlySpan<int> effects) => BestRating(resources, hints, aptitudes, PurchaseDiscount(effects)) - startingRating;

    public double HintValue(int slot, int newLevel, ReadOnlySpan<int> hints, ReadOnlySpan<int> aptitudes,
        in TrainingValues resources, int discount)
    {
        var index = groupByHintSlot[slot];
        if (index < 0 || ptPrice <= 0) return 0;
        Span<Choice> choices = stackalloc Choice[maxChoices];
        var group = groups[index];
        var before = Surplus(choices[..BuildChoices(group, hints, aptitudes, resources, discount, choices)]);
        var after = Surplus(choices[..BuildChoices(group, hints, aptitudes, resources, discount, choices, slot, newLevel)]);
        return after - before;
    }

    double Surplus(ReadOnlySpan<Choice> choices)
    {
        var best = 0.0;
        foreach (var choice in choices) best = Math.Max(best, choice.Rating - ptPrice * choice.Cost);
        return best;
    }

    internal static int PurchaseDiscount(ReadOnlySpan<int> effects) => effects.Contains(7) ? 10 : effects.Contains(40) ? 4 : 0;

    int BestRating(in TrainingValues resources, ReadOnlySpan<int> hints, ReadOnlySpan<int> aptitudes, int discount)
    {
        var budget = Math.Min(resources.SkillPoints, maxSpend);
        var workspace = ArrayPool<int>.Shared.Rent(budget + 1);
        try
        {
            var best = workspace.AsSpan(0, budget + 1);
            best.Clear();
            Span<Choice> choices = stackalloc Choice[maxChoices];
            var reachable = 0;
            foreach (var group in groups)
            {
                var count = BuildChoices(group, hints, aptitudes, resources, discount, choices);
                if (count == 0 || choices[0].Cost > budget) continue;
                var limit = Math.Min(budget, reachable + choices[count - 1].Cost);
                for (var points = limit; points >= Math.Min(choices[0].Cost, reachable + 1); points--)
                {
                    var rating = best[Math.Min(points, reachable)];
                    foreach (var choice in choices[..count])
                    {
                        if (choice.Cost > points) break;
                        rating = Math.Max(rating, best[Math.Min(points - choice.Cost, reachable)] + choice.Rating);
                    }
                    best[points] = rating;
                }
                reachable = limit;
            }
            return best[reachable];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(workspace);
        }
    }

    static int BuildChoices(Group group, ReadOnlySpan<int> hints, ReadOnlySpan<int> aptitudes, in TrainingValues resources,
        int discount, Span<Choice> choices, int overrideSlot = -1, int overrideLevel = -1)
    {
        var rate = group.OpenRate;
        foreach (var unlock in group.HintUnlocks)
            if (Level(hints, unlock.Slot, overrideSlot, overrideLevel) >= 0)
                rate = Math.Max(rate, unlock.Rate);
        var ownedRate = group.Owned?.Rule.Rate ?? 0;
        var ownedRating = group.Owned is { } owned ? Rating(owned, group.OwnedLevel, aptitudes) : 0;
        var cost = 0;
        var count = 0;
        if (group.Owned is { Rule.Rate: < 0 } negative)
        {
            if (negative.Rule is not { SolvableType: 0, Cost: not null } || !Eligible(negative.Rule, resources)) return 0;
            cost = Cost(negative, hints, discount, overrideSlot, overrideLevel);
            choices[count++] = new(cost, -ownedRating);
        }
        foreach (var step in group.Chain)
        {
            if (step.Rule.Rate <= ownedRate) continue;
            if (step.Rule.Rate > rate || !Eligible(step.Rule, resources)) break;
            cost += Cost(step, hints, discount, overrideSlot, overrideLevel);
            choices[count++] = new(cost, Rating(step, 1, aptitudes) - ownedRating);
        }
        return count;
    }

    static int Level(ReadOnlySpan<int> hints, int slot, int overrideSlot, int overrideLevel)
        => slot < 0 ? -1 : slot == overrideSlot ? overrideLevel : hints[slot];

    static int Cost(Step step, ReadOnlySpan<int> hints, int discount, int overrideSlot, int overrideLevel)
    {
        ReadOnlySpan<int> hintDiscounts = [0, 10, 20, 30, 35, 40];
        return step.Rule.Cost!.Value * (100 - discount - hintDiscounts[Math.Max(0,
            Level(hints, step.HintSlot, overrideSlot, overrideLevel))]) / 100;
    }

    static int Rating(Step step, int level, ReadOnlySpan<int> aptitudes)
        => ScoringCandidates.SkillToPoint(step.Score, level, aptitudes.Slice(2, 4), aptitudes.Slice(6, 4));

    static bool Eligible(PioneerSkillRule skill, in TrainingValues resources)
        => skill.StatusType == 0 || (skill.StatusType switch
        {
            1 => resources.Speed, 2 => resources.Stamina, 3 => resources.Power,
            4 => resources.Guts, 5 => resources.Wisdom,
            _ => throw new InvalidDataException($"技能 {skill.Id} 的属性要求类型无效。"),
        }) >= skill.StatusValue;
}
