using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Inheritance;

public sealed record RelationMembership(int CharacterId, int RelationType);
public sealed record RelationPoint(int RelationType, int Points);
public sealed record RelationSaddle(int Id, int RelationGroupId, bool IsEligibleG1);
public sealed record RelationSource(InheritanceSlot Slot, int CharacterId, ImmutableArray<int> WonSaddleIds);
public sealed record SourceRelation(InheritanceSlot Slot, int CharacterPoints, int CommonG1Points)
{
    public int Total => checked(CharacterPoints + CommonG1Points);
}
public sealed record InheritanceRelationResult(int Total, ImmutableArray<SourceRelation> Sources);

public sealed class InheritanceRelationRules
{
    readonly FrozenDictionary<int, int> points;
    readonly FrozenDictionary<int, FrozenSet<int>> memberships;
    readonly FrozenDictionary<int, RelationSaddle> saddles;

    public InheritanceRelationRules(IEnumerable<RelationPoint> pointMaster, IEnumerable<RelationMembership> memberMaster,
        IEnumerable<RelationSaddle> saddleMaster)
    {
        points = pointMaster.ToFrozenDictionary(row => row.RelationType, row => row.Points);
        memberships = memberMaster.GroupBy(row => row.CharacterId)
            .ToFrozenDictionary(group => group.Key, group => group.Select(row => row.RelationType).ToFrozenSet());
        saddles = saddleMaster.ToFrozenDictionary(row => row.Id);
        if (memberships.Values.SelectMany(value => value).Any(type => !points.ContainsKey(type)))
            throw new InvalidDataException("相性成员表引用未收录的关系类型。");
    }

    public InheritanceRelationResult Calculate(int targetCharacterId, ImmutableArray<RelationSource> sources)
    {
        if (sources.IsDefault || sources.Length != 6 || sources.Any(source => source is null || !Enum.IsDefined(source.Slot))
            || sources.Select(source => source.Slot).Distinct().Count() != 6)
            throw new InvalidDataException("相性计算必须提供六个明确且不重复的谱系槽位。");
        if (!memberships.ContainsKey(targetCharacterId))
            throw new UnknownMechanismException("育成角色缺少相性成员数据，不能按零计算。");
        var ordered = sources.OrderBy(source => source.Slot).ToArray();
        var wins = new HashSet<int>[6];
        for (var index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index];
            if (!memberships.ContainsKey(source.CharacterId) || source.WonSaddleIds.IsDefault)
                throw new InvalidDataException("相性来源的角色或获胜鞍点记录不完整。");
            if (source.WonSaddleIds.Distinct().Count() != source.WonSaddleIds.Length)
                throw new InvalidDataException("同一相性来源包含重复鞍点编号。");
            wins[index] = [];
            foreach (var id in source.WonSaddleIds)
            {
                if (!saddles.TryGetValue(id, out var saddle))
                    throw new UnknownMechanismException($"获胜鞍点 {id} 未收录，不能忽略其相性贡献。");
                if (saddle.IsEligibleG1) wins[index].Add(saddle.RelationGroupId);
            }
        }
        var pair = Relation(ordered[0].CharacterId, ordered[3].CharacterId);
        var pairG1 = G1(0, 3);
        var result = new SourceRelation[6];
        foreach (var parent in new[] { 0, 3 })
        {
            foreach (var ancestor in new[] { parent + 1, parent + 2 })
            {
                var ids = new[] { targetCharacterId, ordered[parent].CharacterId, ordered[ancestor].CharacterId };
                result[ancestor] = new(ordered[ancestor].Slot, ids.Distinct().Count() < 3 ? 0 : Relation(ids), G1(parent, ancestor));
            }
            result[parent] = new(ordered[parent].Slot,
                checked(Relation(targetCharacterId, ordered[parent].CharacterId) + pair + result[parent + 1].CharacterPoints + result[parent + 2].CharacterPoints),
                checked(pairG1 + result[parent + 1].CommonG1Points + result[parent + 2].CommonG1Points));
        }
        return new(checked(result[0].Total + result[3].Total - pair - pairG1), result.ToImmutableArray());

        int Relation(params int[] ids) => memberships[ids[0]].Where(type => ids.Skip(1).All(id => memberships[id].Contains(type))).Sum(type => points[type]);
        int G1(int left, int right) => checked(3 * wins[left].Count(wins[right].Contains));
    }
}
