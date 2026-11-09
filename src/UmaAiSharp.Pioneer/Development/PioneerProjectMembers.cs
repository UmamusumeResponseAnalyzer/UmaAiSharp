using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Development;

public sealed record PioneerProjectMember(int TargetId, int CharacterId);
public sealed record PioneerProjectSupport(int Position, int CharacterId, int CardType);
public sealed record PioneerProjectNpc(int TargetId, int TrainingPartnerId, int Bond, bool IsAppear,
    bool IsOuting, int StoryStep);
public sealed record PioneerProjectJoin(ImmutableArray<PioneerProjectMember> Members, ImmutableArray<PioneerProjectNpc> AddedNpcs);

public static class PioneerProjectMembers
{
    static readonly int[] LinkCharacters = [1021, 1027, 1031, 1040, 1069];

    // The selected NPC IDs are an explicit outcome, not an action or a sampled candidate population.
    public static PioneerProjectJoin ApplyObservedSelection(int storyId, int turn, int resultState, int trainingCharacter,
        ImmutableArray<PioneerProjectSupport> supports, ImmutableArray<PioneerProjectMember> before,
        ImmutableArray<int> selectedNpcIds)
    {
        var initial = storyId == 400011029 && turn == 3 && resultState == 0;
        var checkpoint = resultState == 2 && ((storyId == 400011008 && turn == 24) || (storyId == 400011017 && turn == 48));
        if (!initial && !checkpoint)
            throw new UnknownMechanismException("PJ追加仅覆盖第3回合初始化和第2/4期高评价；身份抽样规则未实现。");
        if (supports.IsDefault || supports.Length != 6 || !supports.Select(x => x.Position).Order().SequenceEqual([1, 2, 3, 4, 5, 6])
            || supports.Count(x => x.CardType == 1) != 5 || supports.Count(x => x.CardType == 2 && x.CharacterId == 9049) != 1)
            throw new UnknownMechanismException("PJ追加当前仅覆盖五张普通支援与Tucker的明确编成。");
        var expectedBefore = initial ? 0 : storyId == 400011008 ? 11 : 14;
        if (before.IsDefault || before.Length != expectedBefore || before.Select(x => x.TargetId).Distinct().Count() != before.Length
            || before.Select(x => x.CharacterId).Distinct().Count() != before.Length)
            throw new InvalidDataException("PJ追加缺少符合已观测阶段的完整、无重复前态成员表。");
        if (selectedNpcIds.IsDefault || selectedNpcIds.Length != (initial ? 6 : 3)
            || selectedNpcIds.Any(id => id <= 0) || selectedNpcIds.Distinct().Count() != selectedNpcIds.Length)
            throw new InvalidDataException("必须显式提供初始化6个或评审3个不重复NPC结果；不能省略后自动抽取。");
        var excluded = supports.Select(x => x.CharacterId).Append(trainingCharacter)
            .Concat(before.Select(x => x.CharacterId)).ToHashSet();
        if (selectedNpcIds.Any(excluded.Contains))
            throw new InvalidDataException("NPC结果与育成角色、编成支援或已有成员重叠。");
        if (initial && LinkCharacters.Where(id => !excluded.Contains(id)).Any(id => !selectedNpcIds.Contains(id)))
            throw new UnknownMechanismException("初始化结果缺少已核验的可用链接角色；不自动补人。");
        var members = before.ToBuilder();
        if (initial)
            members.AddRange(supports.Where(x => x.CardType == 1).Select(x => new PioneerProjectMember(x.Position, x.CharacterId)));
        var existingTargets = members.Select(x => x.TargetId).ToHashSet();
        if (selectedNpcIds.Any(existingTargets.Contains))
            throw new InvalidDataException("NPC结果的target_id与已有成员或普通支援卡位冲突。");
        members.AddRange(selectedNpcIds.Select(id => new PioneerProjectMember(id, id)));
        return new(members.ToImmutable(), selectedNpcIds.Select(id => new PioneerProjectNpc(id, id, 0, true, false, 0)).ToImmutableArray());
    }
}
