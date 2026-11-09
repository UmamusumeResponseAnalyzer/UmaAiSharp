using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Inheritance;

public enum InheritanceSlot { Parent1, Parent1Parent1, Parent1Parent2, Parent2, Parent2Parent1, Parent2Parent2 }
public sealed record InitialFactor(int Id, int Type, int GroupId, int Stars);
public sealed record InitialFactorRule(int Type, int Value1, int Value2, int AddPoint);
public sealed record InheritanceSource(InheritanceSlot Slot, ImmutableArray<int> FactorIds, ImmutableArray<int> ExtendedFactorIds);
public sealed record ListedInheritanceSource(InheritanceSlot Slot, ImmutableArray<int> FactorIds);
public sealed record InitialAptitude(int GroupId, int Grade);
public sealed record BlueInheritanceContribution(int AttributeGroupId, int AttributePoints, int LimitPoints);
public sealed record RedInheritanceContribution(int AptitudeGroupId, int TotalStars, int UncappedLevels, int Before, int After)
{
    public int AppliedLevels => After - Before;
}
public sealed record UncalculatedInitialFactor(InheritanceSlot Slot, int FactorId, int Type);
public sealed record InitialInheritanceComponents(ImmutableArray<BlueInheritanceContribution> Blue,
    ImmutableArray<RedInheritanceContribution> Red, ImmutableArray<UncalculatedInitialFactor> UncalculatedFactors);

// Only the initial blue/red components: callers supply effective sources and the pre-inheritance aptitudes.
public sealed class InitialInheritanceRules
{
    static readonly int[] AptitudeGroups = [11, 12, 21, 22, 23, 24, 31, 32, 33, 34];
    readonly FrozenDictionary<int, InitialFactor> factors;
    readonly FrozenDictionary<(int Stars, int Component), int> blue;
    readonly InitialFactorRule[] red;

    public InitialInheritanceRules(IEnumerable<InitialFactor> factorMaster, IEnumerable<InitialFactorRule> initialMaster)
    {
        factors = factorMaster.ToFrozenDictionary(factor => factor.Id);
        var rules = initialMaster.ToArray();
        var blueRows = rules.Where(rule => rule.Type == 1).ToArray();
        if (blueRows.Any(rule => rule.Value1 is < 1 or > 3 || rule.Value2 is not (0 or 1) || rule.AddPoint < 0)
            || blueRows.Length != 6)
            throw new InvalidDataException("开局蓝因子master必须明确包含星1至3的属性与上限增量各一项。");
        blue = blueRows.ToFrozenDictionary(rule => (rule.Value1, rule.Value2), rule => rule.AddPoint);
        red = rules.Where(rule => rule.Type == 2).OrderBy(rule => rule.Value1).ToArray();
        if (red.Any(rule => rule.Value1 < 1 || rule.Value2 < rule.Value1 || rule.AddPoint < 0)
            || Enumerable.Range(1, 18).Any(stars => red.Count(rule => stars >= rule.Value1 && stars <= rule.Value2) != 1))
            throw new InvalidDataException("开局红因子master必须对六来源星数1至18提供唯一的升档规则。");
    }

    public InitialInheritanceComponents Evaluate(ImmutableArray<InheritanceSource> sources, ImmutableArray<InitialAptitude> before)
    {
        if (sources.IsDefault || sources.Any(source => source is null))
            throw new InvalidDataException("开局继承必须显式提供来源数组。");
        foreach (var source in sources)
        {
            if (source.ExtendedFactorIds.IsDefault)
                throw new InvalidDataException("来源扩展因子字段缺失，不能构造完整开局继承输入。");
            if (!source.ExtendedFactorIds.IsEmpty)
                throw new UnknownMechanismException("开局继承的扩展因子合并规则未实现，不能忽略或自动覆盖原因子。");
        }
        return EvaluateListedFactors(sources.Select(source => new ListedInheritanceSource(source.Slot, source.FactorIds)).ToImmutableArray(), before);
    }

    // Computes explicitly listed factors, without certifying the completeness of extension data.
    public InitialInheritanceComponents EvaluateListedFactors(ImmutableArray<ListedInheritanceSource> sources,
        ImmutableArray<InitialAptitude> before)
    {
        if (sources.IsDefault || sources.Length != 6 || sources.Any(source => source is null || !Enum.IsDefined(source.Slot))
            || sources.Select(source => source.Slot).Distinct().Count() != 6)
            throw new InvalidDataException("开局继承必须明确提供两个亲本及各自两个祖辈，共六个不重复谱系槽位。");
        if (before.IsDefault || before.Length != AptitudeGroups.Length
            || before.Any(value => value is null || value.Grade is < 1 or > 8)
            || !before.Select(value => value.GroupId).Order().SequenceEqual(AptitudeGroups))
            throw new InvalidDataException("开局继承前必须明确提供全部10项适性，等级范围为1至8，目标编号不能重复或缺失。");
        var attributes = new int[5];
        var limits = new int[5];
        var redStars = AptitudeGroups.ToDictionary(group => group, _ => 0);
        var uncalculated = ImmutableArray.CreateBuilder<UncalculatedInitialFactor>();
        foreach (var source in sources.OrderBy(source => source.Slot))
        {
            if (source.FactorIds.IsDefault)
                throw new InvalidDataException("来源因子必须显式提供数组，不能以缺失表示空。");
            if (source.FactorIds.Distinct().Count() != source.FactorIds.Length)
                throw new InvalidDataException("同一个谱系来源的因子编号不能重复。");
            var resolved = source.FactorIds.Select(id => factors.TryGetValue(id, out var factor) ? factor
                : throw new UnknownMechanismException($"本地master未收录继承因子{id}。")).ToArray();
            if (resolved.Count(factor => factor.Type == 1) != 1 || resolved.Count(factor => factor.Type == 2) != 1)
                throw new UnknownMechanismException("当前已核对的开局来源要求恰好一个蓝因子和一个红因子；其他结构待核验。");
            foreach (var factor in resolved)
            {
                switch (factor.Type)
                {
                    case 1:
                        if (factor.GroupId is < 1 or > 5 || factor.Stars is < 1 or > 3)
                            throw new UnknownMechanismException($"蓝因子{factor.Id}的目标或星数不在已覆盖范围。");
                        attributes[factor.GroupId - 1] += blue[(factor.Stars, 0)];
                        limits[factor.GroupId - 1] += blue[(factor.Stars, 1)];
                        break;
                    case 2:
                        if (!redStars.ContainsKey(factor.GroupId) || factor.Stars is < 1 or > 3)
                            throw new UnknownMechanismException($"红因子{factor.Id}的目标或星数不在已覆盖范围。");
                        redStars[factor.GroupId] += factor.Stars;
                        break;
                    default:
                        uncalculated.Add(new(source.Slot, factor.Id, factor.Type));
                        break;
                }
            }
        }
        var blueResult = Enumerable.Range(1, 5).Select(group => new BlueInheritanceContribution(group,
            attributes[group - 1], limits[group - 1])).ToImmutableArray();
        var redResult = before.OrderBy(value => value.GroupId).Select(value =>
        {
            var stars = redStars[value.GroupId];
            var increase = stars == 0 ? 0 : red.Single(rule => stars >= rule.Value1 && stars <= rule.Value2).AddPoint;
            // Initial red factors can raise to A (7); an already higher input is never lowered.
            var after = Math.Max(value.Grade, Math.Min(7, checked(value.Grade + increase)));
            return new RedInheritanceContribution(value.GroupId, stars, increase, value.Grade, after);
        }).ToImmutableArray();
        return new(blueResult, redResult, uncalculated.ToImmutable());
    }
}
