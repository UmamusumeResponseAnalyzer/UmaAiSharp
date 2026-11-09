using System.Collections.Immutable;

namespace UmaAiSharp.Pioneer.Events;

public readonly record struct EventPartner(int TargetId, int CharacterId, int Bond, bool IsSupport, bool OutingUnlocked);

// Partner identities are immutable; the trajectory stores bond and outing flag pairs in this order.
internal sealed class DeclaredEventSettlement
{
    public const string Version = "pioneer-declared-event-partners-v1";
    readonly record struct PartnerChange(int Offset, int BondGain, bool UnlockOuting);
    readonly ImmutableArray<PartnerChange> changes;
    public DeclaredEventRewards Resources { get; }
    public bool HasPartnerEffects { get; }
    public long SharedMemoryBytes => 96L + Resources.SharedMemoryBytes + 16L * changes.Length;

    public DeclaredEventSettlement(DeclaredEventBranch branch, ImmutableArray<EventPartner>? partners)
    {
        if (branch.Effects.IsDefault) throw new InvalidDataException("事件结果必须明确提供奖励数组。");
        var other = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
        var compiled = ImmutableArray.CreateBuilder<PartnerChange>();
        foreach (var effect in branch.Effects)
        {
            if (partners is { } roster && effect.DisplayId is 4 or 5 or 11 && effect.Value0 > 0
                && effect.Value1 >= 0 && effect.Value2 == 0 && (effect.DisplayId != 11 || effect.Value1 == 0))
            {
                var index = -1;
                for (var i = 0; i < roster.Length; i++)
                    if (roster[i].CharacterId == effect.Value0 && (effect.DisplayId != 5 || roster[i].IsSupport))
                    { index = i; break; }
                if (index >= 0)
                {
                    compiled.Add(new(index * 2, effect.Value1, effect.DisplayId == 11));
                    HasPartnerEffects = true;
                    continue;
                }
                if (effect.DisplayId == 5)
                {
                    HasPartnerEffects = true;
                    continue;
                }
            }
            other.Add(effect);
        }
        changes = compiled.ToImmutable();
        Resources = new(new(other.ToImmutable()));
    }

    public void ApplyPartners(Span<int> values)
    {
        foreach (var change in changes)
        {
            values[change.Offset] = (int)Math.Min(100L, values[change.Offset] + (long)change.BondGain);
            if (change.UnlockOuting) values[change.Offset + 1] = 1;
        }
    }
}
