using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Inheritance;

public readonly record struct InitialAttributes(int Speed, int Stamina, int Power, int Guts, int Wisdom)
{
    public int this[int group] => group switch
    {
        1 => Speed, 2 => Stamina, 3 => Power, 4 => Guts, 5 => Wisdom,
        _ => throw new ArgumentOutOfRangeException(nameof(group))
    };
    public static InitialAttributes operator +(InitialAttributes a, InitialAttributes b) =>
        new(checked(a.Speed + b.Speed), checked(a.Stamina + b.Stamina), checked(a.Power + b.Power),
            checked(a.Guts + b.Guts), checked(a.Wisdom + b.Wisdom));
}

public sealed record InitialCharacter(int CardId, int Rarity, InitialAttributes Attributes,
    InitialAttributes Limits, ImmutableArray<InitialAptitude> Aptitudes, int TalentLevel, ImmutableArray<InitialSkill> Skills);
public sealed record InitialSkill(int SkillId, int Level);
public sealed record InitialSupportBond(int Position, int CardId, int Bond);
public sealed record InitialSupportComponents(InitialAttributes Attributes, ImmutableArray<InitialSupportBond> Bonds);
public sealed record InitialSupportCard(int Position, int CardId, int CardType, int TrainingCommandId,
    ImmutableArray<SupportEffect> Effects, ImmutableArray<SupportUniqueEffect> UniqueEffects);
public sealed record InheritedHint(int GroupId, int Rarity, int Level);
public sealed record InheritanceFactorEffect(int GroupId, int EffectId, int TargetType, int Value1, int Value2);
public sealed record InheritanceSkill(int Id, int GroupId, int Rarity);
public sealed record InitialStateComponents(InitialAttributes Attributes, InitialAttributes Limits,
    ImmutableArray<InitialAptitude> Aptitudes, ImmutableArray<InheritedHint> ParentHints,
    InitialAttributes SupportAttributes, ImmutableArray<InitialSkill> Skills,
    ImmutableArray<InitialSupportBond> SupportBonds, InitialInheritanceComponents Inheritance);

public sealed class InitialStateRules
{
    public const string Version = "initial-seven-components-flat-pedigree-v2";
    readonly InitialInheritanceRules inheritance;
    readonly FrozenDictionary<int, InitialFactor> factors;
    readonly FrozenDictionary<int, int> greenSelectors;
    readonly ILookup<(int GroupId, int EffectId), InheritanceFactorEffect> effects;
    readonly FrozenDictionary<int, InheritanceSkill> skills;

    public InitialStateRules(IEnumerable<InitialFactor> factorMaster, IEnumerable<InitialFactorRule> initialMaster,
        IEnumerable<InheritanceFactorEffect> effectMaster, IEnumerable<InheritanceSkill> skillMaster)
    {
        var factorRows = factorMaster.ToArray();
        var initialRows = initialMaster.ToArray();
        inheritance = new(factorRows, initialRows);
        factors = factorRows.ToFrozenDictionary(row => row.Id);
        greenSelectors = initialRows.Where(row => row.Type == 3).ToFrozenDictionary(row => row.Value1, row => row.Value2);
        if (!greenSelectors.Keys.Order().SequenceEqual([1, 2, 3]))
            throw new InvalidDataException("绿因子开局 master 必须明确提供 1、2、3 星效果选择。");
        effects = effectMaster.ToLookup(row => (row.GroupId, row.EffectId));
        skills = skillMaster.ToFrozenDictionary(row => row.Id);
    }

    public InitialStateComponents Compose(InitialCharacter character, InitialAttributes scenarioLimits,
        ImmutableArray<InheritanceSource> sources, ImmutableArray<InitialSupportCard> deck)
    {
        // Validate complete source input before entering the explicitly partial calculator.
        inheritance.Evaluate(sources, character.Aptitudes);
        return ComposeListedFactors(character, scenarioLimits,
            sources.Select(source => new ListedInheritanceSource(source.Slot, source.FactorIds)).ToImmutableArray(), deck);
    }

    public InitialStateComponents ComposeListedFactors(InitialCharacter character, InitialAttributes scenarioLimits,
        ImmutableArray<ListedInheritanceSource> sources, ImmutableArray<InitialSupportCard> deck)
    {
        if (character.Rarity is < 1 or > 3 || character.TalentLevel != 1)
            throw new UnknownMechanismException("初始技能集合仅核验稀有度 1 至 3、才能等级 1；其他才能/稀有度效果未接入。");
        if (character.Skills.IsDefault || character.Skills.Any(skill => skill is null || skill.SkillId <= 0 || skill.Level <= 0)
            || character.Skills.Select(skill => skill.SkillId).Distinct().Count() != character.Skills.Length)
            throw new InvalidDataException("初始技能集合必须显式提供有效且不重复的技能及等级。");
        if (character.Skills.Any(skill => !skills.ContainsKey(skill.SkillId)))
            throw new UnknownMechanismException("初始技能集合引用未收录的技能。");
        var inherited = inheritance.EvaluateListedFactors(sources, character.Aptitudes);
        var support = InitialSupportRules.Calculate(deck);
        var blue = inherited.Blue;
        var values = new InitialAttributes(blue[0].AttributePoints, blue[1].AttributePoints, blue[2].AttributePoints,
            blue[3].AttributePoints, blue[4].AttributePoints);
        var caps = new InitialAttributes(blue[0].LimitPoints, blue[1].LimitPoints, blue[2].LimitPoints,
            blue[3].LimitPoints, blue[4].LimitPoints);
        var hints = ImmutableArray.CreateBuilder<InheritedHint>();
        foreach (var source in sources.Where(source => source.Slot is InheritanceSlot.Parent1 or InheritanceSlot.Parent2))
            foreach (var factor in source.FactorIds.Select(id => factors[id]).Where(factor => factor.Type == 3))
            {
                if (!greenSelectors.TryGetValue(factor.Stars, out var selector))
                    throw new UnknownMechanismException($"绿因子 {factor.Id} 的星级没有开局效果选择。");
                // Initial green inheritance selects the hint component, not the mid-run effect bundle.
                var selected = effects[(factor.GroupId, selector)].Where(effect => effect.TargetType == 41).ToArray();
                if (selected.Length != 1 || selected[0].Value2 is < 1 or > 5)
                    throw new UnknownMechanismException($"绿因子 {factor.Id} 的开局效果不是已确认的单一技能提示。");
                if (!skills.TryGetValue(selected[0].Value1, out var skill))
                    throw new UnknownMechanismException($"绿因子 {factor.Id} 引用的技能未收录。");
                if (hints.Any(hint => hint.GroupId == skill.GroupId && hint.Rarity == skill.Rarity))
                    throw new UnknownMechanismException("两个父母的开局绿因子指向同一提示；合并语义未核验。");
                hints.Add(new(skill.GroupId, skill.Rarity, selected[0].Value2));
            }
        return new(character.Attributes + values + support.Attributes, character.Limits + scenarioLimits + caps,
            inherited.Red.Select(red => new InitialAptitude(red.AptitudeGroupId, red.After)).ToImmutableArray(),
            hints.OrderBy(hint => hint.GroupId).ThenBy(hint => hint.Rarity).ToImmutableArray(), support.Attributes,
            character.Skills.OrderBy(skill => skill.SkillId).ToImmutableArray(), support.Bonds,
            inherited with { UncalculatedFactors = inherited.UncalculatedFactors.Where(factor =>
                factor.Type != 3 || factor.Slot is not (InheritanceSlot.Parent1 or InheritanceSlot.Parent2)).ToImmutableArray() });
    }
}

public static class InitialSupportRules
{
    public static InitialSupportComponents Calculate(ImmutableArray<InitialSupportCard> deck)
    {
        if (deck.IsDefault || deck.Length != 6 || deck.Any(card => card is null)
            || !deck.Select(card => card.Position).Order().SequenceEqual([1, 2, 3, 4, 5, 6]))
            throw new InvalidDataException("初始支援属性必须提供六个不重复卡位。");
        var totals = new int[5];
        var bonds = ImmutableArray.CreateBuilder<InitialSupportBond>();
        foreach (var card in deck)
        {
            if (card.CardType is < 1 or > 3 || card.Effects.IsDefault || card.UniqueEffects.IsDefault)
                throw new InvalidDataException("支援类型或初始效果输入不完整。");
            var bond = 0;
            foreach (var effect in card.Effects) Add(effect.Type, effect.Value);
            foreach (var unique in card.UniqueEffects)
            {
                if (unique.Type < 100) Add(unique.Type, unique.Value);
                else if (unique.Type == 105)
                {
                    if (unique.Value < 0 || unique.Parameter1 < 0 || unique.Parameter2 != 0 || unique.Parameter3 != 0 || unique.Parameter4 != 0)
                        throw new UnknownMechanismException("105 固有出现未解释的附加参数。");
                    foreach (var member in deck)
                    {
                        if (member.CardType == 1)
                        {
                            var index = member.TrainingCommandId switch
                            {
                                101 => 0, 105 => 1, 102 => 2, 103 => 3, 106 => 4,
                                _ => throw new UnknownMechanismException("105 固有的普通卡编成类型未知。")
                            };
                            totals[index] = checked(totals[index] + unique.Value);
                        }
                        else for (var index = 0; index < 5; index++)
                            totals[index] = checked(totals[index] + unique.Parameter1);
                    }
                }
                else if (unique.Type == 101)
                {
                    if (unique.Parameter1 is >= 9 and <= 14 || unique.Parameter3 is >= 9 and <= 14)
                        throw new UnknownMechanismException("条件固有包含初始属性或羁绊；其开局生效顺序未核验。");
                }
                else if (unique.Type == 115)
                {
                    throw new UnknownMechanismException("115 全队初始羁绊的对象与上限边界未实包核验。");
                }
                // Classified training, placement and hint effects; see the initial-state evidence scope.
                else if (unique.Type is not (102 or 103 or 104 or 106 or 107 or 108 or 109 or 110 or 111
                    or 112 or 113 or 114 or 116 or 117 or 118 or 119 or 120 or 121 or 122))
                    throw new UnknownMechanismException($"初始属性尚未分类固有类型 {unique.Type}。");
            }
            if (bond > 100) throw new UnknownMechanismException("初始支援羁绊超过 100；截断边界未核验。");
            bonds.Add(new(card.Position, card.CardId, bond));

            void Add(int type, int value)
            {
                if (type is >= 9 and <= 14)
                {
                    if (value < 0) throw new InvalidDataException("初始属性或羁绊加成不能为负数。");
                    if (type == 14) bond = checked(bond + value);
                    else totals[type - 9] = checked(totals[type - 9] + value);
                }
                else if (type is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 15 or 16 or 17 or 18 or 19
                    or 25 or 26 or 27 or 28 or 30 or 31 or 32 or 33 or 41))
                    throw new UnknownMechanismException($"初始效果尚未分类原子类型 {type}。");
            }
        }
        return new(new(totals[0], totals[1], totals[2], totals[3], totals[4]), bonds.OrderBy(value => value.Position).ToImmutableArray());
    }
}
