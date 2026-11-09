using System.Runtime.CompilerServices;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public enum PioneerStateSection
{
    SupportBonds, IslandSupportFacilities, FacilityStates, FacilityGuests, TrainingLevels,
    CharacterEffects, HintCandidates, PendingStories, Thresholds, ConstructionSteps,
    TemporaryGrants, ProjectMembers, AddedProjectNpcs, TrainingExecutionCounts, SuccessfulOrdinaryTrainingCounts,
    PartnerEvaluations,
}

[InlineArray(SectionLengths.Count)]
internal struct SectionLengths { public const int Count = 16; int first; }

public struct PioneerStateHeader
{
    public PioneerStage Stage;
    public PioneerKnownParts KnownParts;
    public PioneerStopCode StopCode;
    public PioneerStage ContinuationFrom;
    public bool TurnKnown, RawPlayingStateKnown;
    public int SourceTurn, SourcePlayingState;
    public int Turn, RawPlayingState, CardId, Motivation, Fans, CommandId;
    public TrainingValues Resources;
    public TrainingLimits Limits;
    public PioneerEvent Event;
    public int ActionOrdinal, ConditionalOutcomeCount, RequiredChoice;
    public int StaminaExecutionCount, DevelopmentPoints, PlanningCost, DeckId, Tickets;
    public int CompletedCheckpoint, CheckpointResult, CheckpointBonusPercent;
    public bool HasCheckpointBonus, OutingUnlocked, DisplayConfirmed, HasCharm, HasAttention;
    public int TuckerBond, TuckerStoryStep, DirectorBond;
    public long FutureGainedSkillPoints;
    public AttributeValues FutureOverflow;
    public bool TrainingSucceeded;
    public int EventRewardResourceMask, EventRewardRemainingEffects;
    public TrainingBoard TrainingBoard;
    public int AvailableTrainingCommands;
    public int? AvailableRestCommands;
    public int? PendingTrainingLevelEventId;
}

// One owner, one contiguous variable-length buffer. No mutable references are shared by copies.
public sealed class PioneerState
{
    internal PioneerStateHeader Mutable;
    SectionLengths lengths;
    int[] buffer;
    int used;

    public PioneerRuntimeData Data { get; }
    public ref readonly PioneerStateHeader Header => ref Mutable;
    public int UsedBufferIntegers => used;
    public int BufferCapacityIntegers => buffer.Length;
    public long MutablePayloadBytes => Unsafe.SizeOf<PioneerStateHeader>() + Unsafe.SizeOf<SectionLengths>() + 4L * used;
    // Includes a conservative allowance for this object and the array object on the supported runtime.
    public long WorkspaceBytes => Unsafe.SizeOf<PioneerStateHeader>() + Unsafe.SizeOf<SectionLengths>() + 4L * buffer.Length + 128;

    internal PioneerState(PioneerRuntimeData data, int capacity)
    {
        Data = data;
        buffer = new int[capacity];
    }

    public ReadOnlySpan<int> GetSection(PioneerStateSection section)
    {
        var index = (int)section;
        if ((uint)index >= SectionLengths.Count) throw new ArgumentOutOfRangeException(nameof(section));
        return buffer.AsSpan(Offset(index), lengths[index]);
    }

    internal void SetSection(PioneerStateSection section, ReadOnlySpan<int> values)
    {
        var index = (int)section;
        var offset = Offset(index);
        var oldLength = lengths[index];
        var nextUsed = checked(used - oldLength + values.Length);
        EnsureCapacity(nextUsed);
        buffer.AsSpan(offset + oldLength, used - offset - oldLength)
            .CopyTo(buffer.AsSpan(offset + values.Length));
        values.CopyTo(buffer.AsSpan(offset, values.Length));
        lengths[index] = values.Length;
        used = nextUsed;
    }

    internal Span<int> MutableSection(PioneerStateSection section)
        => buffer.AsSpan(Offset((int)section), lengths[(int)section]);

    internal void RemoveCharacterEffect(int effect)
    {
        var effects = MutableSection(PioneerStateSection.CharacterEffects);
        var index = effects.IndexOf(effect);
        if (index < 0) throw new InvalidOperationException($"待消费的角色效果{effect}不存在。");
        effects[(index + 1)..].CopyTo(effects[index..]);
        SetSection(PioneerStateSection.CharacterEffects, effects[..^1]);
    }

    internal void AppendGrants(ReadOnlySpan<int> grants)
    {
        var index = (int)PioneerStateSection.TemporaryGrants;
        var insertion = Offset(index) + lengths[index];
        EnsureCapacity(checked(used + grants.Length));
        buffer.AsSpan(insertion, used - insertion).CopyTo(buffer.AsSpan(insertion + grants.Length));
        grants.CopyTo(buffer.AsSpan(insertion, grants.Length));
        lengths[index] += grants.Length;
        used += grants.Length;
    }

    public void CopyInto(PioneerState destination)
    {
        if (!ReferenceEquals(Data, destination.Data))
            throw new ArgumentException("状态复制要求同一份已编译编成与规则数据。", nameof(destination));
        if (ReferenceEquals(this, destination)) return;
        destination.EnsureCapacity(used);
        buffer.AsSpan(0, used).CopyTo(destination.buffer);
        destination.used = used;
        destination.lengths = lengths;
        destination.Mutable = Mutable;
    }

    public PioneerState Clone()
    {
        var destination = new PioneerState(Data, buffer.Length);
        CopyInto(destination);
        return destination;
    }

    int Offset(int index)
    {
        var offset = 0;
        for (var i = 0; i < index; i++) offset += lengths[i];
        return offset;
    }

    void EnsureCapacity(int required)
    {
        if (required > buffer.Length)
            throw new InvalidOperationException($"Pioneer固定工作区容量{buffer.Length}不足以容纳{required}个整数；必须在边界重新编译容量，执行期间不扩容。");
    }
}
