using System.Collections.Immutable;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public enum PioneerStage
{
    None = 0,
    FailureTraining = 1, FailureChoice, IslandTraining, ConstructionEvent, HintAttributeEvent,
    TuckerFriendEvent, TuckerPointEvent, TuckerSpecialtyEvent, ProjectMemberEvent,
    CheckpointEvent, CheckpointConfirmation, UnsupportedContinuation, ConstructionPlanning, TrainingTurn, DeclaredEvent,
    CharacterEnding, Terminal, TrainingLevelConfirmation, RestConfirmation, TurnConfirmation,
}

public enum PioneerStopCode
{
    None = 0,
    TrainingOutcomeRequired = 1001, FailureBranchRequired, HintOutcomeRequired, ProjectMembersRequired,
    NextTurnEventsUnknown, IslandContinuationUnknown, ConstructionRemainderUnknown,
    HintRemainderUnknown, TuckerRemainderUnknown, ProjectRemainderUnknown, CheckpointRemainderUnknown,
    EventContinuationUnknown, UnsupportedComponentInput, InvalidAction, WrongEventIdentity,
    OutcomeIdentityMismatch, NoCompleteTerminal, PlanningContinuationRequired, TrainingEventsRequired, TrainingFailureEventRequired,
    EventRewardOutcomeRequired, EventRewardRemainderRequired, TrainingLevelContinuationRequired,
    RestAvailabilityRequired, RestOutcomeRequired, RestContinuationRequired, RestCharacterRequired,
    NextTurnPhaseRequired, TurnArrivalOutcomeRequired, TurnEventDispatchRequired,
    TrainingLevelHistoryRequired, ConstructionSettlementRequired = 1037,
}

[Flags]
public enum PioneerKnownParts : uint
{
    None = 0, Resources = 1, Limits = 2, Motivation = 4, SupportBonds = 8,
    IslandAssignments = 16, Facilities = 32, DevelopmentPoints = 64, TrainingLevels = 128,
    StaminaExecutionCount = 256, CharacterEffects = 512, ProjectMembers = 1024,
    TuckerProgress = 2048, EventIdentity = 4096, Vital = 8192, MaxVital = 16384, SkillPoints = 32768,
    Tickets = 65536, Planning = 131072, DeckId = 262144, CompletedCheckpoint = 524288,
    CheckpointBonusPercent = 1048576, CheckpointResult = 2097152, CheckpointProgress = 4194304,
    TrainingExecutionCounts = 8388608, TrainingResult = 16777216,
    FutureOverflow = 33554432,
    TrainingBoard = 67108864,
    SuccessfulOrdinaryTrainingCounts = 134217728,
    PartnerEvaluations = 268435456,
}

public sealed record TrainingCommandOption(int CommandId, int Level, int ExecutionCount, int GuestCount,
    ImmutableArray<int> SupportPositions, ImmutableArray<int> HintPositions, bool Enabled,
    int FailureCoefficient, int EvaluationTrainingPercent,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Director = false);

public sealed record CampTrainingConfiguration(ImmutableArray<int> OrdinaryLevels,
    ImmutableArray<TrainingValues> Bases, ImmutableArray<TrainingFacility> Facilities);

public readonly record struct PioneerEvent(int EventId, int CharacterId, int StoryId, int PlayTiming)
{
    public const int TrainingHintStory = 801000003;

    public static bool IsCharacterEndingStory(int storyId) => storyId is > 500000111 and < 600000000 && storyId % 1000 == 111;
}

public readonly record struct PioneerStoryOccurrence(int StoryId, int Turn, int Phase, int PlayTiming,
    int CharacterId, int SupportCardId, int PartnerId, int ChoiceNumber, int ProgressStage = 0, int ProgressLength = 0);

public readonly record struct PioneerRaceOption(int ProgramId, int Grade, int FirstPlaceFans, bool Target, string? Name,
    int RequiredFans = 0, int CareerGoalId = 0);
public readonly record struct PioneerScheduledRace(int Turn, PioneerRaceOption Race);
public readonly record struct PioneerRaceHistory(int Turn, int ProgramId, int Rank);
public readonly record struct PioneerRaceReservation(int Year, int ProgramId);
public readonly record struct PioneerCareerGoal(int Id, int FirstTurn, int Deadline, int FanTarget, int MaximumGrade, int MaximumRank, int RaceCount);
public readonly record struct PioneerFriendProgress(int Position, int CharacterId, bool OutingUnlocked, int Outings);
public sealed record PioneerHomeActions(bool Treatment, bool Outing, bool FriendOuting, ImmutableArray<PioneerFriendProgress> Friends,
    ImmutableArray<PioneerRaceOption> Races, ImmutableArray<PioneerScheduledRace> ScheduledRaces, int UnseenFriendMask,
    int Grade = 3, ulong BlockedRaceTurns = 0,
    ImmutableArray<PioneerCareerGoal>? CareerGoals = null, ImmutableArray<PioneerRaceHistory>? RaceHistory = null);

public readonly record struct PioneerIslandBoard(TrainingBoard Outdoor, TrainingPlacement House)
{
    internal TrainingPlacement this[int index] => index == 5 ? House : Outdoor[index];
}

// Starts describe an observed current boundary. They contain neither future states nor future queues.
public abstract record PioneerStart
{
    public sealed record CharacterEnding(int Turn, int RawPlayingState, int CharacterState, PioneerEvent Event,
        TrainingValues Resources, TrainingLimits Limits, int Motivation) : PioneerStart;
    public sealed record DeclaredEvent(int Turn, int RawPlayingState, PioneerEvent Event,
        TrainingValues Resources, TrainingLimits Limits, int Motivation,
        ImmutableArray<int> CharacterEffects, ImmutableArray<DeclaredEventChoice> Choices,
        ImmutableArray<EventPartner>? Partners = null) : PioneerStart;
    public sealed record TrainingTurn(int Turn, int RawPlayingState, int CardId, TrainingEnvironment Environment,
        ImmutableArray<TrainingSupport> Deck, ImmutableArray<TrainingCommandOption> Commands,
        ImmutableArray<int> CharacterEffects, ImmutableArray<int> PendingStoryIds,
        int DevelopmentPoints, int CompletedCheckpoint, int? CheckpointBonusPercent,
        ImmutableArray<PlanningPeriod> PlanningPeriods, CampTrainingConfiguration? Camp = null,
        ImmutableArray<int>? SuccessfulOrdinaryTrainingCounts = null,
        PioneerEvent? PendingTrainingLevelEvent = null, int? AvailableRestCommands = null,
        PioneerEvent? PendingRestEvent = null, int? CharacterId = null,
        PioneerDevelopmentState? Development = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<PioneerStoryOccurrence>? ObservedStoryHistory = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<int>? CheckpointResults = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        int? DirectorBond = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        PioneerHomeActions? HomeActions = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        PioneerIslandBoard? ObservedIsland = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        UmaAiSharp.Pioneer.Inheritance.PioneerInheritanceContext? Inheritance = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<TemporarySupportGrant>? ObservedGrants = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        PioneerHintProgress? HintProgress = null) : PioneerStart;
    public sealed record FailureTraining(TrainingFailureState State, int CommandId,
        ImmutableArray<int> SupportBonds, ImmutableArray<int> CharacterEffects) : PioneerStart;
    public sealed record IslandTraining(IslandActionState State, IslandTrainingInput Training) : PioneerStart;
    public sealed record Construction(PioneerDevelopmentState State, PioneerEvent Event) : PioneerStart;
    public sealed record Planning(PioneerDevelopmentState State,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        TrainingTurn? Continuation = null) : PioneerStart;
    public sealed record HintAttribute(int Turn, int RawPlayingState, PioneerEvent Event,
        TrainingValues Resources, TrainingLimits Limits, ImmutableArray<TrainingValues> RewardGroups) : PioneerStart;
    public sealed record TuckerFriend(int Turn, int RawPlayingState, PioneerEvent Event,
        TuckerFriendContext Context, TuckerFriendState State, int SkillPoints) : PioneerStart;
    public sealed record TuckerPoints(TuckerEventContext Context, PioneerEvent Event,
        PioneerPointResources Resources) : PioneerStart;
    public sealed record TuckerSpecialty(TuckerSpecialtyContext Context, PioneerEvent Event,
        ImmutableArray<IslandActionSupport> Deck) : PioneerStart;
    public sealed record ProjectMembers(int Turn, int RawPlayingState, PioneerEvent Event,
        int ResultState, int TrainingCharacterId, ImmutableArray<PioneerProjectSupport> Supports,
        ImmutableArray<PioneerProjectMember> Members) : PioneerStart;
    public sealed record Checkpoint(CheckpointEventContext Context, PioneerEvent Event,
        CheckpointResources Resources, CheckpointProgress Progress) : PioneerStart;
    public sealed record CheckpointDisplay(int Turn, int RawPlayingState, PioneerEvent Event,
        SettledCheckpointDisplay Display) : PioneerStart;
}

public abstract record PioneerConditionalOutcome(int ActionOrdinal)
{
    public sealed record EventRewardResult(int AtAction, int ChoiceNumber, int BranchIndex) : PioneerConditionalOutcome(AtAction);
    public sealed record TrainingResult(int AtAction, bool Success) : PioneerConditionalOutcome(AtAction);
    public sealed record RestEvent(int AtAction, PioneerEvent Event) : PioneerConditionalOutcome(AtAction);
    public sealed record TurnArrival(int AtAction, PioneerArrival Arrival, TrainingBoard? Board, int? AvailableTrainingCommands,
        int? AvailableRestCommands, PioneerEvent? Event = null, PioneerIslandBoard? IslandBoard = null) : PioneerConditionalOutcome(AtAction);
    public sealed record TrainingFailure(int AtAction, TrainingFailureEvent Event) : PioneerConditionalOutcome(AtAction);
    public sealed record FailurePenalty(int AtAction, ResolvedFailureBranch Branch) : PioneerConditionalOutcome(AtAction);
    public sealed record HintAttributeGroup(int AtAction, int GroupIndex) : PioneerConditionalOutcome(AtAction);
    public sealed record ProjectNpcSelection(int AtAction, ImmutableArray<int> NpcIds) : PioneerConditionalOutcome(AtAction);
}

public enum PioneerActionKind { Train = 1, IslandTraining, ChooseEvent, SubmitConstruction, Rest, Treat, Outing, Race }
public readonly record struct PioneerRuntimeAction(PioneerActionKind Kind, int CommandId = 0,
    int EventId = 0, int CharacterId = 0, int ChoiceNumber = 0, PioneerConstructionPlan? Plan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int StoryId = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int SupportPosition = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long OwnedPayloadBytes => Plan is null ? 0 : 128L + 16L * Plan.Steps.Length + 2L * Plan.SemanticId.Length;
    public static PioneerRuntimeAction Train(int commandId) => new(PioneerActionKind.Train, commandId);
    public static PioneerRuntimeAction Rest(int commandId) => new(PioneerActionKind.Rest, commandId);
    public static PioneerRuntimeAction Island() => new(PioneerActionKind.IslandTraining);
    public static PioneerRuntimeAction Choose(PioneerEvent current, int choice)
        => new(PioneerActionKind.ChooseEvent, EventId: current.EventId, CharacterId: current.CharacterId, ChoiceNumber: choice,
            StoryId: current.StoryId);
    public static PioneerRuntimeAction Submit(PioneerConstructionPlan plan) => new(PioneerActionKind.SubmitConstruction, Plan: plan);
}

// Copies visible scalars only; neither mutable storage nor conditional outcomes are exposed to policies.
public readonly record struct PioneerDecisionView(PioneerStage Stage, int Turn, int RawPlayingState,
    PioneerKnownParts KnownParts, TrainingValues Resources, int Motivation, int CommandId,
    PioneerEvent Event, int MinimumChoice, int MaximumChoice, PioneerDevelopmentState? Planning = null,
    int AvailableTrainingCommands = 0, ImmutableArray<DeclaredEventChoice> EventChoices = default, bool IsCamp = false,
    TrainingBoard? TrainingBoard = null, int? AvailableRestCommands = null, PioneerRuntimeAction? SuggestedAction = null);
