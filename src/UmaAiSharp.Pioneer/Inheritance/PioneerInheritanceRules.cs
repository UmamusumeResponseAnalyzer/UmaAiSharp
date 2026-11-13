using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Inheritance;

public sealed record PioneerInheritanceParent(ParentFactorSnapshot Factors, ImmutableArray<RelationSource> Relations);
public sealed record PioneerInheritanceContext(PioneerInheritanceParent First, PioneerInheritanceParent Second,
    bool HasUmaPlan, ImmutableArray<int> Aptitudes);
public readonly record struct PioneerInheritanceFactor(int Id, int Type, int Group, int Stars, int EffectGroup);
public readonly record struct PioneerInheritanceEffect(int Group, int EffectId, int Target, int Value, int Count);
public sealed record PioneerInheritanceAddon(int FactorId, int RequiredGroups, ImmutableArray<int> Groups);
public readonly record struct PioneerInheritanceRate(int FactorType, int EffectGroup, int BasisPoints);
public readonly record struct PioneerInheritanceWeight(int EffectGroup, int EffectId, int Count);
public sealed record PioneerInheritanceCalibration(ImmutableArray<PioneerInheritanceRate> Rates,
    ImmutableArray<PioneerInheritanceWeight> Weights, ImmutableArray<int> SaturatedHintWeights);
public sealed record PioneerInheritanceData(ImmutableArray<PioneerInheritanceFactor> Factors,
    ImmutableArray<PioneerInheritanceEffect> Effects, ImmutableArray<InheritanceSkill> Skills,
    ImmutableArray<RelationPoint> RelationPoints, ImmutableArray<RelationMembership> RelationMemberships,
    ImmutableArray<RelationSaddle> Saddles, ImmutableArray<PioneerInheritanceAddon> Addons, PioneerInheritanceCalibration Calibration);

public sealed class PioneerInheritanceRules
{
    public static ReadOnlySpan<int> AptitudeTargets => [11, 12, 21, 22, 23, 24, 31, 32, 33, 34];
    readonly record struct Effect(int Target, int Value, int Count, int HintIndex, bool Owned);
    sealed record Bundle(int Weight, Effect[] Effects);
    sealed record Opportunity(int Probability, Bundle[] Bundles, int TotalWeight, ulong AddonGroup);
    sealed record Addon(int RequiredGroups, ulong Groups, Opportunity Opportunity);
    readonly Opportunity[] opportunities;
    readonly Addon[] addons;
    readonly int[] saturatedHintWeights;
    readonly int saturatedHintWeightSum;
    readonly int candidateCount;
    readonly ImmutableArray<int> initialAptitudes;
    public long SharedMemoryBytes => 512L + (opportunities.Length + addons.Length) * 72L
        + opportunities.Concat(addons.Select(addon => addon.Opportunity)).Sum(opportunity =>
            opportunity.Bundles.Sum(bundle => 48L + 32L * bundle.Effects.Length));

    public PioneerInheritanceRules(PioneerInheritanceData data, PioneerInheritanceContext context, int characterId, int cardId,
        PioneerHintRules hints, ImmutableArray<int> ownedSkills)
    {
        var factors = data.Factors.ToFrozenDictionary(factor => factor.Id);
        var pedigree = new InheritancePedigree(data.Factors.Select(factor => new InitialFactor(factor.Id, factor.Type, factor.Group, factor.Stars)));
        var sources = pedigree.MapParent(context.First.Factors, InheritanceSlot.Parent1, effective: true)
            .AddRange(pedigree.MapParent(context.Second.Factors, InheritanceSlot.Parent2, effective: true));
        var relation = new InheritanceRelationRules(data.RelationPoints, data.RelationMemberships, data.Saddles)
            .Calculate(characterId, context.First.Relations.AddRange(context.Second.Relations));
        var rates = data.Calibration.Rates.ToFrozenDictionary(row => (row.FactorType, row.EffectGroup), row => row.BasisPoints);
        var weights = data.Calibration.Weights.ToFrozenDictionary(row => (row.EffectGroup, row.EffectId), row => row.Count);
        var skills = data.Skills.ToFrozenDictionary(row => row.Id);
        var effects = data.Effects.ToLookup(row => row.Group);
        var inherited = sources.SelectMany(source => source.FactorIds).Distinct().Select(id => factors[id]).ToArray();
        var inheritedGroups = inherited.Select(factor => factor.Group).ToHashSet();
        var activeAddons = data.Addons.Where(addon => addon.Groups.Count(inheritedGroups.Contains) >= addon.RequiredGroups).ToArray();
        var addonGroups = activeAddons.SelectMany(addon => addon.Groups).Distinct().Select((group, index) => (group, index))
            .ToFrozenDictionary(pair => pair.group, pair => 1UL << pair.index);
        if (addonGroups.Count > 64) throw new InvalidDataException("继承附加条件超过64个不同触发组。");
        var relevant = inherited.Concat(activeAddons.Select(addon => factors[addon.FactorId])).Distinct().ToArray();
        var owned = ownedSkills.ToHashSet();
        initialAptitudes = context.Aptitudes;
        if (initialAptitudes.Length != AptitudeTargets.Length || initialAptitudes.Any(value => value is < 1 or > 8))
            throw new InvalidDataException("途中继承需要十项适性，等级范围为1至8。");
        candidateCount = context.HasUmaPlan ? 2 : 1;
        saturatedHintWeights = data.Calibration.SaturatedHintWeights.ToArray();
        saturatedHintWeightSum = saturatedHintWeights.Sum();
        if (saturatedHintWeightSum <= 0 || saturatedHintWeights.Any(value => value < 0))
            throw new InvalidDataException("继承满级 Hint 转化分布无有效权重。");
        var compiled = relevant.Where(factor => factor.Type != 7).ToFrozenDictionary(factor => factor.Id, factor =>
        {
            foreach (var effect in effects[factor.Group])
                if (effect.Target is not (>= 1 and <= 7 or 41 or >= 61 and <= 65) && !AptitudeTargets.Contains(effect.Target))
                    throw new UnknownMechanismException($"途中继承因子 {factor.Id} 不支持目标类型 {effect.Target}。");
            var bundles = effects[factor.Group].GroupBy(effect => effect.EffectId).OrderBy(group => group.Key)
                .Select(group => new Bundle(weights[(factor.EffectGroup, group.Key)], [.. group
                    .Where(effect => effect.Target != 41 || factor.Type != 3 || factor.Group != cardId)
                    .Select(effect => effect.Target == 41
                        ? new Effect(41, effect.Value, effect.Count,
                            hints.Slot(new(skills[effect.Value].GroupId, skills[effect.Value].Rarity)), owned.Contains(effect.Value))
                        : new Effect(effect.Target, effect.Value, effect.Count, -1, false))])).ToArray();
            if (bundles.Length == 0 || bundles.Sum(bundle => bundle.Weight) <= 0)
                throw new InvalidDataException($"继承因子 {factor.Id} 缺少效果档权重。");
            return bundles;
        });
        opportunities = [.. sources.SelectMany(source => source.FactorIds.Order()
            .Select(id => factors[id]).Where(factor => factor.Type != 7).Select(factor =>
            {
                var bundles = compiled[factor.Id];
                var probability = Math.Min(1000000,
                    rates[(factor.Type, factor.EffectGroup)] * (100 + relation.Sources[(int)source.Slot].Total));
                return new Opportunity(probability, bundles, bundles.Sum(bundle => bundle.Weight), addonGroups.GetValueOrDefault(factor.Group));
            }))];
        addons = [.. activeAddons.Select(addon =>
        {
            var bundles = compiled[addon.FactorId];
            return new Addon(addon.RequiredGroups, addon.Groups.Aggregate(0UL, (mask, group) => mask | addonGroups[group]),
                new(1000000, bundles, bundles.Sum(bundle => bundle.Weight), 0));
        })];
    }

    internal ReadOnlySpan<int> InitialAptitudes => initialAptitudes.AsSpan();

    public void Apply(ref TrainingValues resources, ref TrainingLimits limits, Span<int> aptitudes, Span<int> hints,
        ref AttributeValues totalOverflow, ref SimulationRandom random)
    {
        Span<int> attributes = stackalloc int[5];
        Span<int> caps = stackalloc int[5];
        Span<int> overflow = stackalloc int[5];
        Span<int> rolledAptitudes = stackalloc int[10];
        // Each of six slots references only one copy of each Hint destination, bounded by the compiled master catalogue.
        Span<int> rolledHints = stackalloc int[hints.Length];
        Span<int> bestAptitudes = stackalloc int[10];
        Span<int> bestHints = stackalloc int[hints.Length];
        var best = int.MinValue;
        var result = resources;
        var resultCaps = limits;
        var resultOverflow = default(AttributeValues);
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            attributes[0] = resources.Speed; attributes[1] = resources.Stamina; attributes[2] = resources.Power;
            attributes[3] = resources.Guts; attributes[4] = resources.Wisdom;
            caps[0] = limits.Speed; caps[1] = limits.Stamina; caps[2] = limits.Power;
            caps[3] = limits.Guts; caps[4] = limits.Wisdom;
            overflow.Clear();
            aptitudes.CopyTo(rolledAptitudes);
            hints.CopyTo(rolledHints);
            var points = resources.SkillPoints;
            var triggeredGroups = 0UL;
            for (var index = 0; index < opportunities.Length + addons.Length; index++)
            {
                Opportunity opportunity;
                if (index < opportunities.Length)
                    opportunity = opportunities[index];
                else
                {
                    var addon = addons[index - opportunities.Length];
                    if (System.Numerics.BitOperations.PopCount(triggeredGroups & addon.Groups) < addon.RequiredGroups) continue;
                    opportunity = addon.Opportunity;
                }
                if (opportunity.Probability != 1000000 && random.NextInt(1000000) >= opportunity.Probability) continue;
                triggeredGroups |= opportunity.AddonGroup;
                var draw = random.NextInt(opportunity.TotalWeight);
                foreach (var bundle in opportunity.Bundles)
                {
                    if ((draw -= bundle.Weight) >= 0) continue;
                    foreach (var effect in bundle.Effects)
                        if (effect.Target is >= 61 and <= 65) caps[effect.Target - 61] += effect.Value;
                    foreach (var effect in bundle.Effects)
                    {
                        switch (effect.Target)
                        {
                            case >= 1 and <= 5:
                                Gain(attributes, caps, overflow, effect.Target - 1, effect.Value);
                                break;
                            case 6:
                                points += effect.Value;
                                break;
                            case 7:
                                Gain(attributes, caps, overflow, random.NextInt(5), effect.Value);
                                break;
                            case 41:
                                ref var level = ref rolledHints[effect.HintIndex];
                                if (effect.Owned || level == 5)
                                    Gain(attributes, caps, overflow, random.NextInt(5), DrawHintConversion(ref random));
                                else
                                    level = Math.Min(5, Math.Max(0, level) + effect.Count);
                                break;
                            case >= 61 and <= 65:
                                break;
                            default:
                                var aptitude = AptitudeTargets.IndexOf(effect.Target);
                                rolledAptitudes[aptitude] = Math.Min(8, rolledAptitudes[aptitude] + effect.Value);
                                break;
                        }
                    }
                    break;
                }
            }
            var value = LotteryValue(attributes[0], attributes[1], attributes[2], attributes[3], attributes[4], points);
            if (value <= best) continue;
            best = value;
            result = new(attributes[0], attributes[1], attributes[2], attributes[3], attributes[4], resources.Vital, points);
            resultCaps = new(caps[0], caps[1], caps[2], caps[3], caps[4], limits.Vital);
            resultOverflow = new(overflow[0], overflow[1], overflow[2], overflow[3], overflow[4]);
            rolledAptitudes.CopyTo(bestAptitudes);
            rolledHints.CopyTo(bestHints);
        }
        resources = result;
        limits = resultCaps;
        totalOverflow += resultOverflow;
        bestAptitudes.CopyTo(aptitudes);
        bestHints.CopyTo(hints);
    }

    // shortcut: temporary inheritance comparison, raw-equivalent attribute gains plus twice the skill Pt.
    public static int LotteryValue(int speed, int stamina, int power, int guts, int wisdom, int points)
        => Raw(speed) + Raw(stamina) + Raw(power) + Raw(guts) + Raw(wisdom) + 2 * points;

    static int Raw(int display) => display + Math.Max(0, display - 1200);

    int DrawHintConversion(ref SimulationRandom random)
    {
        var draw = random.NextInt(saturatedHintWeightSum);
        for (var index = 0; index < saturatedHintWeights.Length; index++)
            if ((draw -= saturatedHintWeights[index]) < 0) return index + 1;
        throw new InvalidOperationException("继承满级 Hint 权重和不一致。");
    }

    static void Gain(Span<int> attributes, ReadOnlySpan<int> caps, Span<int> overflow, int index, int raw)
    {
        var below = Math.Min(raw, Math.Max(0, 1200 - attributes[index]));
        var amount = Math.Max(1, below + (raw - below) / 2);
        var uncapped = attributes[index] + amount;
        attributes[index] = Math.Min(uncapped, caps[index]);
        overflow[index] += uncapped - attributes[index];
    }
}
