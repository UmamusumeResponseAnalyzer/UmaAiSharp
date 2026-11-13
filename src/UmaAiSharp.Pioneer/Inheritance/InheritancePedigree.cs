using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Inheritance;

public sealed record PedigreeFactorSlot(int Position, ImmutableArray<int> FactorIds);
public sealed record PedigreeFactorExtension(int Position, int BaseFactorId, int FactorId, string? RegisteredAt = null);
public sealed record ParentFactorSnapshot(ImmutableArray<PedigreeFactorSlot> Slots,
    ImmutableArray<PedigreeFactorExtension> FlatExtensions);

public sealed class InheritancePedigree(IEnumerable<InitialFactor> factorMaster)
{
    readonly FrozenDictionary<int, InitialFactor> factors = factorMaster.ToFrozenDictionary(factor => factor.Id);

    public ImmutableArray<InheritanceSource> MapParent(ParentFactorSnapshot parent, InheritanceSlot parentSlot, bool effective = false)
    {
        if (parentSlot is not (InheritanceSlot.Parent1 or InheritanceSlot.Parent2))
            throw new ArgumentException("完整亲本只能映射到第一或第二亲本槽位。", nameof(parentSlot));
        if (parent.Slots.IsDefault || parent.FlatExtensions.IsDefault)
            throw new InvalidDataException("完整亲本必须提供基础谱系及顶层扩展数组；缺失不能表示空。");
        if (parent.Slots.Any(slot => slot is null || slot.Position is not (1 or 10 or 20 or 11 or 12 or 21 or 22))
            || parent.Slots.Select(slot => slot.Position).Distinct().Count() != parent.Slots.Length)
            throw new InvalidDataException("亲本谱系位置重复或未知。");
        var slots = parent.Slots.ToDictionary(slot => slot.Position);
        if (new[] { 1, 10, 20 }.Any(position => !slots.ContainsKey(position)))
            throw new InvalidDataException("完整亲本缺少本人或其两个父母的基础因子。");
        foreach (var slot in parent.Slots)
        {
            if (slot.FactorIds.IsDefault || slot.FactorIds.Distinct().Count() != slot.FactorIds.Length)
                throw new InvalidDataException("亲本基础因子缺失或同一位置重复。");
            foreach (var id in slot.FactorIds)
                if (!factors.ContainsKey(id)) throw new UnknownMechanismException($"亲本基础因子 {id} 未收录。");
        }
        var replacements = new Dictionary<(int Position, int Base), PedigreeFactorExtension>();
        var additions = new List<PedigreeFactorExtension>();
        var added = new HashSet<(int Position, int Result)>();
        foreach (var extension in parent.FlatExtensions)
        {
            if (extension is null || !slots.TryGetValue(extension.Position, out var slot))
                throw new InvalidDataException("顶层扩展指向没有基础谱系的位置。");
            if (!factors.TryGetValue(extension.FactorId, out var result))
                throw new UnknownMechanismException($"扩展因子 {extension.FactorId} 未收录。");
            if (slot.FactorIds.Contains(extension.FactorId) || !added.Add((extension.Position, extension.FactorId)))
                throw new InvalidDataException("扩展结果重复或已经存在于基础因子中。");
            if (extension.BaseFactorId == 0)
            {
                additions.Add(extension);
                continue;
            }
            if (!slot.FactorIds.Contains(extension.BaseFactorId))
                throw new InvalidDataException("扩展替换缺少基础因子。");
            var basis = factors[extension.BaseFactorId];
            if (basis.GroupId != result.GroupId || basis.Type != result.Type)
                throw new InvalidDataException("扩展替换的基础与结果不是同组因子。");
            var key = (extension.Position, extension.BaseFactorId);
            if (replacements.TryGetValue(key, out var previous))
            {
                if (extension.RegisteredAt is null || previous.RegisteredAt is null || extension.RegisteredAt == previous.RegisteredAt)
                    throw new InvalidDataException("同一谱系位置和基础因子有无法排序的强化记录。");
                if (StringComparer.Ordinal.Compare(extension.RegisteredAt, previous.RegisteredAt) < 0) continue;
            }
            replacements[key] = extension;
        }
        // Only the selected parent's self and parents enter the current six-source pedigree.
        return new[] { 1, 10, 20 }.Select((position, offset) => new InheritanceSource(
            (InheritanceSlot)((int)parentSlot + offset),
            effective ? [.. slots[position].FactorIds.Where(id => !replacements.ContainsKey((position, id))),
                .. replacements.Values.Concat(additions).Where(extension => extension.Position == position).Select(extension => extension.FactorId)]
                : slots[position].FactorIds,
            replacements.Values.Concat(additions).Where(extension => extension.Position == position)
                .Select(extension => extension.FactorId).ToImmutableArray())).ToImmutableArray();
    }
}
