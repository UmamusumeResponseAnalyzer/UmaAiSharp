using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Numerics;
using UmaAiSharp.Base.Hints;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Base.Inheritance;

public sealed record InheritanceParent(ParentFactorSnapshot Factors, ImmutableArray<RelationSource> Relations);
public record InheritanceContext(InheritanceParent First, InheritanceParent Second,
    bool HasUmaPlan, ImmutableArray<int> Aptitudes);
public readonly record struct InheritanceFactor(int Id, int Type, int Group, int Stars, int EffectGroup);
public readonly record struct InheritanceEffect(int Group, int EffectId, int Target, int Value, int Count);
public sealed record InheritanceAddon(int FactorId, int RequiredGroups, ImmutableArray<int> Groups);
public readonly record struct InheritanceRate(int FactorType, int EffectGroup, int BasisPoints);
public readonly record struct InheritanceWeight(int EffectGroup, int EffectId, int Count);
public sealed record InheritanceCalibration(ImmutableArray<InheritanceRate> Rates,
    ImmutableArray<InheritanceWeight> Weights, ImmutableArray<int> SaturatedHintWeights);
public sealed record InheritanceData(ImmutableArray<InheritanceFactor> Factors,
    ImmutableArray<InheritanceEffect> Effects, ImmutableArray<InheritanceSkill> Skills,
    ImmutableArray<RelationPoint> RelationPoints, ImmutableArray<RelationMembership> RelationMemberships,
    ImmutableArray<RelationSaddle> Saddles, ImmutableArray<InheritanceAddon> Addons, InheritanceCalibration Calibration);

public readonly record struct InheritanceLottery(int LotteryId, AttributeValues Attributes, int SkillPoints);
public readonly record struct RankedInheritanceLottery(int LotteryId, AttributeValues AttributeGain, int SkillPointGain, int Value);

public sealed partial class MidRunInheritanceRules
{
    public const int ClassicTurn = 31;
    public const int SeniorTurn = 55;

    readonly record struct Effect(int Target, int Value, int Count, int HintIndex, bool Owned);
    sealed record Bundle(int Weight, Effect[] Effects);

    sealed class HintExpectation
    {
        public double Unlock;
        public readonly double[] Gains = new double[6], Conversions = new double[6];
    }

    readonly double[] attributes = new double[5], caps = new double[5], aptitudes = new double[10];
    readonly int[] hintSlots;
    readonly HintExpectation[] hints;
    readonly double conversionPerAttribute;
    double skillPoints;
    public long SharedMemoryBytes => 400L + 196L * hintSlots.Length;

    public static MidRunInheritanceRules? Compile(InheritanceData data, InheritanceContext context, int characterId, int cardId,
        Func<HintSkill, int> registerHint, ImmutableArray<int> ownedSkills, bool hasUpcomingInheritance)
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
        if (context.Aptitudes.Length != Aptitude.GroupIds.Length || context.Aptitudes.Any(value => value is < 1 or > 8))
            throw new InvalidDataException("途中继承需要十项适性，等级范围为1至8。");
        var saturatedHintWeights = data.Calibration.SaturatedHintWeights.ToArray();
        var saturatedHintWeightSum = saturatedHintWeights.Sum();
        if (saturatedHintWeightSum <= 0 || saturatedHintWeights.Any(value => value < 0))
            throw new InvalidDataException("继承满级 Hint 转化分布无有效权重。");
        var compiled = relevant.Where(factor => factor.Type != 7).ToFrozenDictionary(factor => factor.Id, factor =>
        {
            foreach (var effect in effects[factor.Group])
                if (effect.Target is not (>= 1 and <= 7 or 41 or >= 61 and <= 65) && !Aptitude.GroupIds.Contains(effect.Target))
                    throw new UnknownMechanismException($"途中继承因子 {factor.Id} 不支持目标类型 {effect.Target}。");
            var bundles = effects[factor.Group].GroupBy(effect => effect.EffectId).OrderBy(group => group.Key)
                .Select(group => new Bundle(weights[(factor.EffectGroup, group.Key)], [.. group
                    .Where(effect => effect.Target != 41 || factor.Type != 3 || factor.Group != cardId)
                    .Select(effect => effect.Target == 41
                        ? new Effect(41, effect.Value, effect.Count,
                            registerHint(new(skills[effect.Value].GroupId, skills[effect.Value].Rarity)), owned.Contains(effect.Value))
                        : new Effect(effect.Target, effect.Value, effect.Count, -1, false))])).ToArray();
            if (bundles.Length == 0 || bundles.Sum(bundle => bundle.Weight) <= 0)
                throw new InvalidDataException($"继承因子 {factor.Id} 缺少效果档权重。");
            return bundles;
        });
        // The skill evaluator shares the registered Hint slots even after the final inheritance.
        if (!hasUpcomingInheritance) return null;
        var rolls = new List<RewardRoll>();
        foreach (var source in sources)
            foreach (var id in source.FactorIds.Order())
            {
                var factor = factors[id];
                if (factor.Type == 7) continue;
                var probability = Math.Min(1000000,
                    rates[(factor.Type, factor.EffectGroup)] * (100 + relation.Sources[(int)source.Slot].Total));
                rolls.Add(CompileRoll(compiled[id], probability / 1_000_000d, addonGroups.GetValueOrDefault(factor.Group)));
            }
        foreach (var addon in activeAddons)
            rolls.Add(CompileRoll(compiled[addon.FactorId], 1, 0, addon.RequiredGroups,
                addon.Groups.Aggregate(0UL, (mask, group) => mask | addonGroups[group])));
        var hintSlots = compiled.Values.SelectMany(bundles => bundles).SelectMany(bundle => bundle.Effects)
            .Where(effect => effect.Target == 41).Select(effect => effect.HintIndex).Distinct().Order().ToArray();
        var conversionMean = saturatedHintWeights.Select((weight, index) => (index + 1.0) * weight).Sum() / saturatedHintWeightSum;
        return new(context.HasUmaPlan, hintSlots, [.. rolls], conversionMean);
    }

    MidRunInheritanceRules(bool hasUmaPlan, int[] hintSlots, RewardRoll[] rolls, double conversionMean)
    {
        this.hintSlots = hintSlots;
        hints = [.. hintSlots.Select(_ => new HintExpectation())];
        conversionPerAttribute = conversionMean / 5;
        var groups = IndependentGroups(rolls);
        var weights = hasUmaPlan ? WinnerWeights(groups.Select(ScoreMass).ToArray()) : null;
        for (var i = 0; i < groups.Length; i++) AddWeightedRewards(groups[i], weights?[i], conversionMean);
    }

    // Repeated grants of one Hint and conditions for an addon must see their joint outcomes.
    // Groups without either connection contribute independently to the total candidate score.
    static RewardRoll[][] IndependentGroups(RewardRoll[] rolls)
    {
        var parent = Enumerable.Range(0, rolls.Length).ToArray();
        var firstHint = new Dictionary<int, int>();
        var firstTrigger = new Dictionary<int, int>();
        for (var i = 0; i < rolls.Length; i++)
        {
            var roll = rolls[i];
            var triggers = roll.Groups;
            foreach (var branch in roll.Branches)
            {
                triggers |= branch.TriggeredGroup;
                foreach (var effect in branch.Effects)
                    if (effect.Target == 41 && !effect.Owned) Link(firstHint, effect.HintIndex, i);
            }
            while (triggers != 0)
            {
                Link(firstTrigger, BitOperations.TrailingZeroCount(triggers), i);
                triggers &= triggers - 1;
            }
        }
        return [.. Enumerable.Range(0, rolls.Length).GroupBy(Root).Select(group => group.Select(i => rolls[i]).ToArray())];

        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        void Link(Dictionary<int, int> first, int key, int i)
        {
            if (!first.TryAdd(key, i)) parent[Root(i)] = Root(first[key]);
        }
    }

    public void Apply(ref TrainingValues resources, ref TrainingLimits limits, Span<int> currentAptitudes, Span<int> currentHints,
        ref AttributeValues totalOverflow, int sampleIndex, int turn = ClassicTurn)
    {
        var sample = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)sampleIndex);
        sample = ((sample & 0x55555555) << 1) | ((sample >> 1) & 0x55555555);
        sample = ((sample & 0x33333333) << 2) | ((sample >> 2) & 0x33333333);
        sample = ((sample & 0x0f0f0f0f) << 4) | ((sample >> 4) & 0x0f0f0f0f);

        // Adjacent intervals spread rare unlocks across paired samples while preserving each
        // skill's unlock probability and its mean level conditional on being unlocked.
        var unlockRank = Rank(26);
        var levelRank = Rank(27);
        var conversions = 0.0;
        for (var i = 0; i < hints.Length; i++)
        {
            ref var level = ref currentHints[hintSlots[i]];
            var expectation = hints[i];
            conversions += expectation.Conversions[Math.Max(level, 0)];
            if (level < 0)
            {
                var chance = Math.Clamp(expectation.Unlock, 0, 1);
                if (unlockRank < chance)
                    level = Project(Math.Clamp(expectation.Gains[0] / chance, 0, 5), unlockRank / chance);
                unlockRank = Fraction(unlockRank - chance);
            }
            else
            {
                var gain = Math.Clamp(expectation.Gains[level], 0, 5 - level);
                level += Project(gain, levelRank);
                levelRank = Fraction(levelRank - gain);
            }
        }

        Span<int> values = [resources.Speed, resources.Stamina, resources.Power, resources.Guts, resources.Wisdom];
        Span<int> ceilings = [limits.Speed, limits.Stamina, limits.Power, limits.Guts, limits.Wisdom];
        Span<int> overflow = stackalloc int[5];
        for (var i = 0; i < 5; i++)
        {
            var raw = Raw(values[i]) + attributes[i] + conversions * conversionPerAttribute;
            var display = raw <= 1200 ? raw : 1200 + (raw - 1200) / 2;
            var ceiling = ceilings[i] + caps[i];
            var rank = Rank(i);
            values[i] = Project(Math.Min(display, ceiling), rank);
            ceilings[i] = Project(ceiling, rank);
            overflow[i] = Project(Math.Max(0, display - ceiling), Rank(10 + i));
        }
        resources = new(values[0], values[1], values[2], values[3], values[4], resources.Vital,
            resources.SkillPoints + Project(skillPoints, Rank(15)));
        limits = new(ceilings[0], ceilings[1], ceilings[2], ceilings[3], ceilings[4], limits.Vital);
        totalOverflow += new AttributeValues(overflow[0], overflow[1], overflow[2], overflow[3], overflow[4]);
        for (var i = 0; i < currentAptitudes.Length; i++)
            currentAptitudes[i] = Project(Math.Min(8, currentAptitudes[i] + aptitudes[i]), Rank(16 + i));

        double Rank(int dimension)
        {
            var salt = unchecked((uint)(turn * 65537 + dimension + 1) * 0x9e3779b9u);
            salt ^= salt >> 16;
            return (unchecked(sample * (salt | 1) + salt * 0x85ebca6bu) + 0.5) / 4294967296.0;
        }
    }

    static int Project(double value, double rank)
    {
        var whole = (int)value;
        return whole + (rank < value - whole ? 1 : 0);
    }

    static double Fraction(double value) => value - Math.Floor(value);

    public static IEnumerable<RankedInheritanceLottery> RankLotteries(AttributeValues before, int beforePoints,
        IEnumerable<InheritanceLottery> options)
    {
        var beforeValue = Score(before, beforePoints);
        return options.Select(option =>
        {
            var after = option.Attributes;
            return new RankedInheritanceLottery(option.LotteryId, new(after.Speed - before.Speed, after.Stamina - before.Stamina,
                after.Power - before.Power, after.Guts - before.Guts, after.Wisdom - before.Wisdom), option.SkillPoints - beforePoints,
                Score(after, option.SkillPoints) - beforeValue);
        }).OrderByDescending(option => option.Value);
    }

    // The recommender values raw-equivalent attributes plus twice the skill Pt.
    static int Raw(int display) => display + Math.Max(0, display - 1200);

    static int Score(AttributeValues attributes, int skillPoints)
        => Raw(attributes.Speed) + Raw(attributes.Stamina) + Raw(attributes.Power)
            + Raw(attributes.Guts) + Raw(attributes.Wisdom) + 2 * skillPoints;

}
