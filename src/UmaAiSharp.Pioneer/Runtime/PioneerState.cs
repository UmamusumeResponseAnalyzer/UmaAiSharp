using System.Runtime.CompilerServices;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public enum PioneerStateSection
{
    SupportBonds, IslandSupportFacilities, FacilityStates, FacilityGuests, TrainingLevels,
    CharacterEffects, HintCandidates, PendingStories, Thresholds, ConstructionSteps,
    TemporaryGrants, ProjectMembers, AddedProjectNpcs, TrainingExecutionCounts, SuccessfulOrdinaryTrainingCounts,
    PartnerEvaluations, CheckpointResults, InheritanceAptitudes, HintLevels, StoryHistory, CareerRaceCounts,
}

[InlineArray(SectionLengths.Count)]
internal struct SectionLengths { public const int Count = 21; int first; }

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
    public int ProjectNpcCount, EvaluationTrainingPercent, CompletedUraRaces;
    public int CompletedInheritances;
    public int FailedCareerGoal;
    internal int RaceGrade;
    internal PioneerOrdinaryStoryProgress OrdinaryStories;
    internal int TraineeStoryMask;
    public bool DeferredConstruction, PioneerCompleted;
    public int UnseenFriendMask, UnlockedFriendMask, FriendOutings;
    public long FutureGainedSkillPoints;
    public AttributeValues FutureOverflow;
    public bool TrainingSucceeded;
    internal bool TrainingFailureIllness;
    public int EventRewardResourceMask, EventRewardRemainingEffects;
    public TrainingBoard TrainingBoard;
    public PioneerIslandBoard IslandBoard;
    public int AvailableTrainingCommands;
    public int? AvailableRestCommands;
    public int? PendingTrainingLevelCommandId;
}

// One owner, one contiguous variable-length buffer. No mutable references are shared by copies.
public sealed class PioneerState
{
    const int GrantSection = (int)PioneerStateSection.TemporaryGrants;
    internal PioneerStateHeader Mutable;
    internal PioneerBoardRules? TurnBoard;
    SectionLengths lengths;
    int[] buffer;
    int used;

    public PioneerRuntimeData Data { get; }
    public ref readonly PioneerStateHeader Header => ref Mutable;
    public int UsedBufferIntegers => used;
    public int BufferCapacityIntegers => buffer.Length;
    public long MutablePayloadBytes => Unsafe.SizeOf<PioneerStateHeader>() + Unsafe.SizeOf<SectionLengths>() + 4L * used;
    // Includes a conservative allowance for this object and the array object on the supported runtime.
    public long WorkspaceBytes => Unsafe.SizeOf<PioneerStateHeader>() + Unsafe.SizeOf<SectionLengths>() + 4L * buffer.Length + 128
        + (Data.TurnRules is null ? 0 : PioneerTurnRules.BoardWorkspaceBytes);

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

    internal const int StoryOccurrenceSize = 10;
    internal void RecordStory(PioneerStoryOccurrence story)
    {
        EnsureCapacity(checked(used + StoryOccurrenceSize));
        var end = used - lengths[GrantSection];
        buffer.AsSpan(end, lengths[GrantSection]).CopyTo(buffer.AsSpan(end + StoryOccurrenceSize));
        Span<int> values = [story.StoryId, story.Turn, story.Phase, story.PlayTiming, story.CharacterId,
            story.SupportCardId, story.PartnerId, story.ChoiceNumber, story.ProgressStage, story.ProgressLength];
        values.CopyTo(buffer.AsSpan(end));
        lengths[(int)PioneerStateSection.StoryHistory] += StoryOccurrenceSize;
        used += StoryOccurrenceSize;
    }

    internal void RemoveCharacterEffect(int effect)
    {
        var effects = MutableSection(PioneerStateSection.CharacterEffects);
        var index = effects.IndexOf(effect);
        if (index < 0) throw new InvalidOperationException($"待消费的角色效果{effect}不存在。");
        effects[(index + 1)..].CopyTo(effects[index..]);
        SetSection(PioneerStateSection.CharacterEffects, effects[..^1]);
    }

    internal void AppendGrants(ReadOnlySpan<TemporarySupportGrant> grants)
    {
        var count = checked(grants.Length * 6);
        EnsureCapacity(checked(used + count));
        var packed = buffer.AsSpan(used, count);
        for (var i = 0; i < grants.Length; i++)
        {
            var grant = grants[i];
            var offset = i * 6;
            packed[offset] = grant.SourceTurn; packed[offset + 1] = (int)grant.Source;
            packed[offset + 2] = grant.StoryId ?? 0; packed[offset + 3] = grant.Position;
            packed[offset + 4] = grant.EffectType; packed[offset + 5] = grant.Value;
        }
        lengths[GrantSection] += count;
        used += count;
    }

    internal void AddCharacterEffect(int effect)
    {
        var current = GetSection(PioneerStateSection.CharacterEffects);
        if (current.Contains(effect)) return;
        Span<int> updated = stackalloc int[current.Length + 1];
        current.CopyTo(updated);
        updated[^1] = effect;
        SetSection(PioneerStateSection.CharacterEffects, updated);
    }

    internal void ExpireGrants(int turn)
    {
        var grants = MutableSection(PioneerStateSection.TemporaryGrants);
        var offset = 0;
        while (offset < grants.Length && grants[offset] < turn - 1) offset += 6;
        if (offset == 0) return;
        grants[offset..].CopyTo(grants);
        lengths[GrantSection] -= offset;
        used -= offset;
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
        destination.TurnBoard = TurnBoard;
    }

    public PioneerState Clone()
    {
        var destination = new PioneerState(Data, buffer.Length);
        CopyInto(destination);
        return destination;
    }

    int Offset(int index)
    {
        // Keep temporary grants physically last so they can grow or expire without shifting history.
        if (index == GrantSection) return used - lengths[GrantSection];
        var offset = 0;
        for (var i = 0; i < index; i++) offset += lengths[i];
        return index > GrantSection ? offset - lengths[GrantSection] : offset;
    }

    void EnsureCapacity(int required)
    {
        if (required > buffer.Length)
            throw new InvalidOperationException($"Pioneer固定工作区容量{buffer.Length}不足以容纳{required}个整数；必须在边界重新编译容量，执行期间不扩容。");
    }
}
