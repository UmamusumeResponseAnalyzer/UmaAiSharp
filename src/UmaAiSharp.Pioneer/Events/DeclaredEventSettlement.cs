using System.Collections.Immutable;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Pioneer.Events;

public readonly record struct EventPartner(int TargetId, int CharacterId, int Bond, bool IsSupport, bool OutingUnlocked);

// Partner identities are immutable; the trajectory stores bond and outing flag pairs in this order.
internal sealed class DeclaredEventSettlement
{
    readonly record struct PartnerChange(int Offset, int BondGain, bool UnlockOuting);
    readonly ImmutableArray<PartnerChange> changes;
    public ImmutableArray<DeclaredEventEffect> Conditions { get; }
    public bool MissingDeclaration { get; }
    public DeclaredEventRewards Resources { get; }
    public bool HasPartnerEffects { get; }
    public long SharedMemoryBytes => 128L + Resources.SharedMemoryBytes + 16L * (changes.Length + Conditions.Length);

    public DeclaredEventSettlement(DeclaredEventBranch branch, ImmutableArray<EventPartner>? partners)
    {
        if (branch.Effects.IsDefault) throw new InvalidDataException("事件结果必须明确提供奖励数组。");
        MissingDeclaration = branch.MissingDeclaration;
        if (MissingDeclaration && !branch.Effects.IsEmpty) throw new InvalidDataException("缺失奖励声明的事件分支不能携带奖励原子。");
        var other = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
        var compiled = ImmutableArray.CreateBuilder<PartnerChange>();
        var conditions = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
        var changesMood = branch.Effects.Any(atom => atom is { DisplayId: 1 or 2, Value0: 20 });
        foreach (var effect in branch.Effects)
        {
            // Mood can consume Positive Thinking or be blocked by a migraine; their order is not in the declaration.
            if (effect is { DisplayId: 9 or 10 or 37, Value0: > 0, Value1: 0, Value2: 0 }
                && !(changesMood && effect.Value0 is 5 or 25))
            {
                conditions.Add(effect);
                continue;
            }
            if (effect is { DisplayId: 13, Value0: >= 0, Value1: 0, Value2: 0 })
            {
                for (var attribute = 1; attribute <= 5; attribute++) other.Add(new(1, attribute, effect.Value0, 0));
                continue;
            }
            if (partners is { } roster && effect.DisplayId is 4 or 5 or 11 or 35 or 36 && effect.Value0 > 0
                && effect.Value1 >= 0 && effect.Value2 == 0 && (effect.DisplayId != 11 || effect.Value1 == 0))
            {
                var supportOnly = effect.DisplayId is 5 or 36;
                var index = -1;
                for (var i = 0; i < roster.Length; i++)
                    if (roster[i].CharacterId == effect.Value0 && (!supportOnly || roster[i].IsSupport))
                    { index = i; break; }
                if (index >= 0)
                {
                    compiled.Add(new(index * 2, effect.DisplayId is 35 or 36 ? -effect.Value1 : effect.Value1, effect.DisplayId == 11));
                    HasPartnerEffects = true;
                    continue;
                }
                if (supportOnly)
                {
                    HasPartnerEffects = true;
                    continue;
                }
            }
            other.Add(effect);
        }
        changes = compiled.ToImmutable();
        Conditions = conditions.ToImmutable();
        Resources = new(new(other.ToImmutable()));
    }

    public void ApplyConditions(PioneerState state)
    {
        foreach (var effect in Conditions)
            if (effect.DisplayId != 10) state.AddCharacterEffect(effect.Value0);
            else if (state.GetSection(PioneerStateSection.CharacterEffects).Contains(effect.Value0))
                state.RemoveCharacterEffect(effect.Value0);
    }

    public void ApplyPartners(Span<int> values)
    {
        foreach (var change in changes)
        {
            values[change.Offset] = (int)Math.Clamp(values[change.Offset] + (long)change.BondGain, 0, 100);
            if (change.UnlockOuting) values[change.Offset + 1] = 1;
        }
    }
}
