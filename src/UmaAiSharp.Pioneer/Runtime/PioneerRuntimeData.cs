using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

internal readonly record struct CompiledConstructionStep(int StateOffset, int PreviousStateId, int TargetStateId, int CumulativeCost);

public sealed class PioneerRuntimeData
{
    internal PioneerStart Start { get; }
    internal ImmutableArray<PioneerConditionalOutcome> Outcomes { get; }
    internal PioneerConstructionRules? Construction { get; }
    internal ImmutableArray<CompiledConstructionStep> ConstructionSteps { get; } = [];
    internal CheckpointSettlement? Checkpoints { get; }
    internal CompiledTrainingDeck? IslandDeck { get; }
    internal CompiledIslandFacilities? IslandFacilities { get; }
    internal int IslandOrdinaryMask { get; }
    internal CompiledTrainingDeck? TrainingDeck { get; }
    internal ImmutableArray<TrainingCommandOption> TrainingCommands { get; } = [];
    internal ImmutableArray<CompiledCampTraining> CampCommands { get; } = [];
    internal int TrainingFailureBias { get; }
    internal int? TrainingCharacterId { get; }
    internal ImmutableArray<ImmutableArray<DeclaredEventSettlement>> EventRewards { get; } = [];
    internal int EventChoiceMinimum { get; }
    internal int EventChoiceMaximum { get; }
    internal DeclaredEventRewards? CharacterEndingReward { get; }
    readonly PioneerState initial;

    public string RulesIdentity { get; }
    public bool IsConditional => Outcomes.Length > 0;
    public int ConditionalOutcomeCount => Outcomes.Length;
    public PioneerStage InitialStage => initial.Header.Stage;
    public long SharedMemoryBytes { get; }

    public static PioneerRuntimeData Compile(PioneerStart start,
        ImmutableArray<PioneerConditionalOutcome> conditionalOutcomes, string rulesIdentity,
        PioneerMasterData? constructionMaster = null, CheckpointMasterData? checkpointMaster = null)
        => new(start, conditionalOutcomes, rulesIdentity, constructionMaster, checkpointMaster);

    PioneerRuntimeData(PioneerStart start, ImmutableArray<PioneerConditionalOutcome> outcomes,
        string rulesIdentity, PioneerMasterData? constructionMaster, CheckpointMasterData? checkpointMaster)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesIdentity);
        if (outcomes.IsDefault) throw new InvalidDataException("条件结果数组必须明确提供，未知概率时可显式为空。");
        var previous = -1;
        foreach (var outcome in outcomes)
        {
            if (outcome is null || outcome.ActionOrdinal <= previous)
                throw new InvalidDataException("条件结果必须按非负动作序号严格递增，同一次动作不能重复提供结果。");
            if (outcome is PioneerConditionalOutcome.ProjectNpcSelection npc && npc.NpcIds.IsDefault)
                throw new InvalidDataException("NPC条件结果必须显式提供身份数组。");
            if (outcome is not (PioneerConditionalOutcome.TrainingResult or PioneerConditionalOutcome.RestEvent or PioneerConditionalOutcome.TrainingFailure or PioneerConditionalOutcome.FailurePenalty
                or PioneerConditionalOutcome.HintAttributeGroup or PioneerConditionalOutcome.ProjectNpcSelection or PioneerConditionalOutcome.EventRewardResult))
                throw new InvalidDataException("未实现的条件结果类型不能进入Pioneer执行器。");
            previous = outcome.ActionOrdinal;
        }
        Start = start;
        Outcomes = outcomes;
        RulesIdentity = rulesIdentity + "/pioneer-runtime-components-v13/" + TrainingFormulaCandidate.Version;
        if (start is PioneerStart.CharacterEnding ending)
        {
            DeclaredEventRewards.Validate(ending.Resources, ending.Limits, ending.Motivation);
            if (ending.Turn != 78 || ending.RawPlayingState != 5 || ending.CharacterState != 3 || ending.Event.EventId != 6
                || ending.Event.CharacterId <= 0 || ending.Event.StoryId != 500000000L + ending.Event.CharacterId * 1000L + 111
                || ending.Event.PlayTiming != 3)
                throw new InvalidDataException("角色结局起点要求回合78、角色状态3、阶段5和事件6/角色结局111/时机3。");
            CharacterEndingReward = new(new([new(1, 1, 5, 0), new(1, 2, 5, 0), new(1, 3, 5, 0),
                new(1, 4, 5, 0), new(1, 5, 5, 0), new(1, 30, 20, 0)]));
            RulesIdentity += "/pioneer-character-ending-v1";
        }
        if (start is PioneerStart.DeclaredEvent revealed)
        {
            DeclaredEventRewards.Validate(revealed.Resources, revealed.Limits, revealed.Motivation);
            if (revealed.CharacterEffects.IsDefault || revealed.CharacterEffects.Any(effect => effect <= 0)
                || revealed.CharacterEffects.Distinct().Count() != revealed.CharacterEffects.Length)
                throw new InvalidDataException("已揭示事件需要明确且无重复的角色效果数组。");
            if (revealed.Choices.IsDefaultOrEmpty || revealed.Choices.Any(choice => choice is null || choice.ChoiceNumber < 0
                || choice.Branches.IsDefaultOrEmpty || choice.Branches.Any(branch => branch is null))
                || revealed.Choices.Select(choice => choice.ChoiceNumber).Distinct().Count() != revealed.Choices.Length)
                throw new InvalidDataException("已揭示事件需要互异的明确选项及完整的候选结果数组。");
            if (revealed.Partners is { } partners && (partners.IsDefault
                || partners.Any(partner => partner.TargetId <= 0 || partner.CharacterId <= 0 || partner.Bond is < 0 or > 100)
                || partners.Select(partner => partner.TargetId).Distinct().Count() != partners.Length
                || partners.Select(partner => partner.CharacterId).Distinct().Count() != partners.Length
                || !partners.Where(partner => partner.IsSupport).Select(partner => partner.TargetId).Order().SequenceEqual([1, 2, 3, 4, 5, 6])))
                throw new InvalidDataException("事件人员表要求完整六卡卡位、明确角色身份、0..100羁绊，target_id和角色身份不能重复。");
            EventRewards = [.. revealed.Choices.Select(choice => choice.Branches.Select(branch => new DeclaredEventSettlement(branch, revealed.Partners)).ToImmutableArray())];
            EventChoiceMinimum = revealed.Choices.Min(choice => choice.ChoiceNumber);
            EventChoiceMaximum = revealed.Choices.Max(choice => choice.ChoiceNumber);
            RulesIdentity += "/" + DeclaredEventRewards.Version + "/" + DeclaredEventSettlement.Version;
        }
        if (start is PioneerStart.Planning) RulesIdentity += "/" + PioneerConstructionRules.PlanningVersion;
        if (start is PioneerStart.TrainingTurn turn)
        {
            ValidateTrainingTurn(turn);
            TrainingCharacterId = turn.CharacterId;
            RulesIdentity += "/pioneer-training-turn-v12/" + TrainingLevelRules.Version + "/" + RestRules.Version
                + "/" + TrainingFailureRate.Version;
            TrainingDeck = new(turn.Deck);
            TrainingCommands = [.. turn.Commands.OrderBy(command => TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(command.CommandId)))];
            if (turn.Camp is { } camp)
                CampCommands = [.. TrainingCommands.Select((command, index) =>
                    TrainingNumericKernel.CompileCamp(command.CommandId, command.Level, camp.Bases[index], camp.Facilities))];
            TrainingFailureBias = TrainingFailureRate.StatusBias(turn.CharacterEffects);
        }
        Construction = constructionMaster is null ? null : new(constructionMaster);
        Checkpoints = checkpointMaster is null ? null : new(checkpointMaster);
        var currentDevelopment = start switch
        {
            PioneerStart.Construction building => building.State,
            PioneerStart.TrainingTurn currentTraining => currentTraining.Development,
            _ => null,
        };
        if (currentDevelopment is not null)
        {
            if (Construction is null) throw new InvalidDataException("当前建设状态缺少master规则。");
            var analysis = Construction.ValidateCurrentPlan(currentDevelopment);
            if (start is PioneerStart.TrainingTurn training)
            {
                ValidateTrainingDevelopment(training, currentDevelopment, Construction);
                RulesIdentity += "/" + ConstructionTriggerRules.Version;
            }
            var steps = ImmutableArray.CreateBuilder<CompiledConstructionStep>(analysis.Steps.Length);
            foreach (var entry in analysis.Steps)
            {
                var facilityIndex = 0;
                while (currentDevelopment.Facilities[facilityIndex].FacilityId != entry.Step.FacilityId) facilityIndex++;
                steps.Add(new(2 * facilityIndex + 1, entry.Step.PreviousStateId, entry.Step.TargetStateId, entry.CumulativeCost));
            }
            ConstructionSteps = steps.MoveToImmutable();
            RulesIdentity += "/" + Construction.Data.SourceSha256;
        }
        if (start is PioneerStart.IslandTraining island)
        {
            ValidateIslandInputs(island);
            foreach (var card in island.State.Deck)
                if (card.Type == 1) IslandOrdinaryMask |= 1 << (card.Position - 1);
            var panels = new TrainingSupport?[6];
            foreach (var assignment in island.Training.Assignments)
                foreach (var panel in assignment.Supports)
                {
                    if (panel.Position is < 1 or > 6 || panels[panel.Position - 1] is not null)
                        throw new InvalidDataException("岛训练编成位置无效或重复。");
                    panels[panel.Position - 1] = panel;
                }
            IslandDeck = new([.. panels.Where(panel => panel is not null).Select(panel => panel!)]);
            IslandFacilities = TrainingNumericKernel.CompileIslandFacilities(island.Training.Facilities);
        }
        initial = Import(start);
        // Cold reference inputs are retained as a correctness boundary. This is a conservative reservation,
        // not a claim to know the CLR's exact object graph size.
        var sourceRows = constructionMaster is null ? 0L : constructionMaster.FacilityKinds.Length
            + constructionMaster.FacilityStates.Length + constructionMaster.FacilityUpgrades.Length
            + constructionMaster.PlanningPeriods.Length + constructionMaster.TicketThresholds.Length;
        if (checkpointMaster is not null)
            sourceRows += checkpointMaster.Periods.Count + checkpointMaster.PointBonuses.Count
                + checkpointMaster.Effects.Sum(pair => pair.Value.Length);
        var outcomeValues = outcomes.Sum(outcome => outcome is PioneerConditionalOutcome.ProjectNpcSelection selection ? selection.NpcIds.Length : 0);
        SharedMemoryBytes = checked(1024 + 256 * sourceRows + 128L * outcomes.Length + 4L * outcomeValues
            + (currentDevelopment is null ? 0 : 128L + 160L * ConstructionSteps.Length)
            + 2L * RulesIdentity.Length + initial.WorkspaceBytes + EstimateStart(start)
            + (TrainingDeck?.SharedMemoryBytes ?? 0)
            + (IslandDeck?.SharedMemoryBytes ?? 0)
            + (IslandFacilities is null ? 0 : CompiledIslandFacilities.SharedMemoryBytes)
            + EventRewards.Sum(choice => 32L + choice.Sum(reward => reward.SharedMemoryBytes))
            + (CharacterEndingReward?.SharedMemoryBytes ?? 0)
            + (start is PioneerStart.IslandTraining islandStart
                ? 4 * SupportStorage(islandStart.Training.Assignments.SelectMany(group => group.Supports)) : 0));
    }

    public PioneerState CreateInitialState() => initial.Clone();

    internal PioneerConditionalOutcome? OutcomeAt(int ordinal)
    {
        foreach (var outcome in Outcomes)
        {
            if (outcome.ActionOrdinal == ordinal) return outcome;
            if (outcome.ActionOrdinal > ordinal) break;
        }
        return null;
    }

    PioneerState Import(PioneerStart start)
    {
        var capacity = start switch
        {
            PioneerStart.DeclaredEvent entry => checked(entry.CharacterEffects.Length + 2 * (entry.Partners?.Length ?? 0)),
            PioneerStart.TrainingTurn turn => checked(22 + turn.CharacterEffects.Length + Math.Max(1, turn.PendingStoryIds.Length)
                + (turn.Development is { } development ? 2 * development.Facilities.Length
                    + development.IslandTrainingThresholds.Length + 4 * development.Plan.Steps.Length : turn.Camp is null ? 0 : 12)
                + (turn.SuccessfulOrdinaryTrainingCounts.HasValue ? 5 : 0)),
            PioneerStart.FailureTraining entry => checked(6 + entry.CharacterEffects.Length),
            PioneerStart.IslandTraining island => checked(29 + island.State.CharacterEffects.Length
                + island.State.HintCandidates.Length + island.State.PendingStoryIds.Length + 30),
            PioneerStart.Construction construction => checked(2 * construction.State.Facilities.Length
                + construction.State.IslandTrainingThresholds.Length + 4 * construction.State.Plan.Steps.Length
                + (construction.State.PendingStoryIds?.Length ?? 0)),
            PioneerStart.Planning planning => checked(6 * planning.State.Facilities.Length + planning.State.IslandTrainingThresholds.Length),
            PioneerStart.TuckerSpecialty specialty => checked(specialty.Deck.Length + 30),
            PioneerStart.ProjectMembers project => checked(2 * (project.Members.Length + 11) + 36),
            PioneerStart.Checkpoint => 5,
            _ => 0,
        };
        var state = new PioneerState(this, capacity);
        ref var header = ref state.Mutable;
        switch (start)
        {
            case PioneerStart.CharacterEnding entry:
                header.Stage = PioneerStage.CharacterEnding;
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Limits | PioneerKnownParts.Motivation
                    | PioneerKnownParts.EventIdentity | PioneerKnownParts.FutureOverflow;
                header.Resources = entry.Resources; header.Limits = entry.Limits; header.Motivation = entry.Motivation;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.Event = entry.Event;
                header.RequiredChoice = 0;
                break;
            case PioneerStart.DeclaredEvent entry:
                header.Stage = PioneerStage.DeclaredEvent;
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Limits | PioneerKnownParts.Motivation
                    | PioneerKnownParts.EventIdentity | PioneerKnownParts.CharacterEffects | PioneerKnownParts.FutureOverflow;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.Event = entry.Event;
                header.Resources = entry.Resources; header.Limits = entry.Limits; header.Motivation = entry.Motivation;
                state.SetSection(PioneerStateSection.CharacterEffects, entry.CharacterEffects.AsSpan());
                if (entry.Partners is { } partners)
                {
                    state.SetSection(PioneerStateSection.PartnerEvaluations,
                        partners.SelectMany(partner => new[] { partner.Bond, partner.OutingUnlocked ? 1 : 0 }).ToArray());
                    header.KnownParts |= PioneerKnownParts.PartnerEvaluations;
                }
                break;
            case PioneerStart.TrainingTurn entry:
                header.Stage = PioneerStage.TrainingTurn;
                header.PendingTrainingLevelEventId = 0;
                header.AvailableRestCommands = entry.AvailableRestCommands;
                for (var i = 0; i < TrainingCommands.Length; i++)
                {
                    var command = TrainingCommands[i];
                    var supports = 0;
                    var hints = 0;
                    foreach (var position in command.SupportPositions) supports |= 1 << (position - 1);
                    foreach (var position in command.HintPositions) hints |= 1 << (position - 1);
                    header.TrainingBoard = header.TrainingBoard.With(i, new((byte)supports, (byte)hints, (byte)command.GuestCount));
                    if (command.Enabled) header.AvailableTrainingCommands |= 1 << i;
                }
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Limits | PioneerKnownParts.Motivation
                    | PioneerKnownParts.SupportBonds | PioneerKnownParts.TrainingLevels | PioneerKnownParts.TrainingExecutionCounts
                    | PioneerKnownParts.CharacterEffects | PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.CompletedCheckpoint
                    | PioneerKnownParts.FutureOverflow | PioneerKnownParts.TrainingBoard;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.CardId = entry.CardId;
                header.Resources = entry.Environment.Current; header.Limits = entry.Environment.Limits;
                header.Motivation = entry.Environment.Motivation; header.Fans = entry.Environment.Fans;
                header.DevelopmentPoints = entry.DevelopmentPoints; header.CompletedCheckpoint = entry.CompletedCheckpoint;
                header.DeckId = entry.PlanningPeriods[0].DeckId; header.KnownParts |= PioneerKnownParts.DeckId;
                header.HasCheckpointBonus = entry.CheckpointBonusPercent.HasValue;
                header.CheckpointBonusPercent = entry.CheckpointBonusPercent.GetValueOrDefault();
                if (header.HasCheckpointBonus) header.KnownParts |= PioneerKnownParts.CheckpointBonusPercent;
                state.SetSection(PioneerStateSection.SupportBonds, entry.Deck.OrderBy(card => card.Position).Select(card => card.Bond).ToArray());
                state.SetSection(PioneerStateSection.TrainingLevels, entry.Camp is { } camp
                    ? camp.OrdinaryLevels.AsSpan() : TrainingCommands.Select(command => command.Level).ToArray());
                if (entry.Camp is { } configuration)
                {
                    state.SetSection(PioneerStateSection.FacilityStates,
                        configuration.Facilities.SelectMany(facility => new[] { facility.Id, facility.StateId }).ToArray());
                    header.KnownParts |= PioneerKnownParts.Facilities;
                }
                state.SetSection(PioneerStateSection.TrainingExecutionCounts, TrainingCommands.Select(command => command.ExecutionCount).ToArray());
                if (entry.SuccessfulOrdinaryTrainingCounts is { } successful)
                {
                    state.SetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts, successful.AsSpan());
                    header.KnownParts |= PioneerKnownParts.SuccessfulOrdinaryTrainingCounts;
                }
                state.SetSection(PioneerStateSection.CharacterEffects, entry.CharacterEffects.AsSpan());
                state.SetSection(PioneerStateSection.PendingStories, entry.PendingStoryIds.AsSpan());
                if (entry.PendingTrainingLevelEvent is { } trainingLevel)
                {
                    header.Stage = PioneerStage.TrainingLevelConfirmation;
                    header.Event = trainingLevel;
                    header.PendingTrainingLevelEventId = trainingLevel.EventId;
                    header.KnownParts |= PioneerKnownParts.EventIdentity;
                }
                if (entry.PendingRestEvent is { } restEvent)
                {
                    header.Stage = PioneerStage.RestConfirmation;
                    header.CommandId = RestRules.OrdinaryCommand;
                    header.Event = restEvent;
                    header.KnownParts |= PioneerKnownParts.EventIdentity;
                }
                if (entry.Development is { } development) ImportDevelopment(state, development);
                break;
            case PioneerStart.FailureTraining entry:
                if (entry.State.Stage != TrainingFailureStage.TrainingDecision || entry.State.PendingEvent is not null
                    || entry.CommandId is not (105 or 3602) || entry.SupportBonds.IsDefault || entry.SupportBonds.Length != 6
                    || entry.CharacterEffects.IsDefault || entry.CharacterEffects.Distinct().Count() != entry.CharacterEffects.Length)
                    throw new InvalidDataException("失败片段起点必须是明确的普通/合宿耐力决策及六张支援羁绊，不接受未来失败事件。");
                _ = TrainingFailureRate.StatusBias(entry.CharacterEffects);
                header.Stage = PioneerStage.FailureTraining;
                header.PendingTrainingLevelEventId = 0;
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Motivation | PioneerKnownParts.SupportBonds
                    | PioneerKnownParts.StaminaExecutionCount | PioneerKnownParts.CharacterEffects;
                header.Turn = entry.State.Turn;
                header.RawPlayingState = 1;
                header.CardId = entry.State.CardId;
                header.Motivation = entry.State.Motivation;
                header.Resources = entry.State.Resources;
                header.CommandId = entry.CommandId;
                header.StaminaExecutionCount = entry.State.StaminaExecutionCount;
                state.SetSection(PioneerStateSection.SupportBonds, entry.SupportBonds.AsSpan());
                state.SetSection(PioneerStateSection.CharacterEffects, entry.CharacterEffects.AsSpan());
                break;
            case PioneerStart.IslandTraining entry:
                var before = entry.State;
                if (before.Deck.IsDefault || before.Deck.Length != 6 || before.Assignments.IsDefault
                    || before.Facilities.IsDefault || before.Facilities.Length != 6 || entry.Training.OrdinaryLevels.Length != 5)
                    throw new InvalidDataException("岛训练起点需要完整的六张支援、六设施、五项等级与人员分配。");
                if (entry.Training.Environment.Current != before.Values || entry.Training.Environment.Limits != before.Limits)
                    throw new InvalidDataException("岛训练数值生成输入与当前状态不一致。");
                header.Stage = PioneerStage.IslandTraining;
                header.CommandId = 3101;
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Limits | PioneerKnownParts.Motivation
                    | PioneerKnownParts.SupportBonds | PioneerKnownParts.IslandAssignments | PioneerKnownParts.Facilities
                    | PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.Tickets | PioneerKnownParts.CompletedCheckpoint
                    | PioneerKnownParts.TrainingLevels | PioneerKnownParts.CharacterEffects | PioneerKnownParts.FutureOverflow;
                header.Turn = before.ActionTurn; header.RawPlayingState = before.RawPlayingState;
                header.Resources = before.Values; header.Limits = before.Limits;
                header.Motivation = entry.Training.Environment.Motivation; header.Fans = entry.Training.Environment.Fans;
                header.DevelopmentPoints = before.DevelopmentPoints; header.Tickets = before.Tickets;
                header.CompletedCheckpoint = before.CompletedCheckpoint;
                header.HasCheckpointBonus = before.CheckpointBonusPercent.HasValue;
                header.CheckpointBonusPercent = before.CheckpointBonusPercent.GetValueOrDefault();
                if (before.CheckpointBonusPercent.HasValue) header.KnownParts |= PioneerKnownParts.CheckpointBonusPercent;
                var bonds = new int[6]; var locations = new int[6]; var facilities = new int[6]; var guests = new int[6];
                var seen = 0;
                foreach (var card in before.Deck)
                {
                    if (card.Position is < 1 or > 6 || (seen & (1 << (card.Position - 1))) != 0)
                        throw new InvalidDataException("六张支援的位置必须是互异的1..6。");
                    seen |= 1 << (card.Position - 1); bonds[card.Position - 1] = card.Bond;
                }
                seen = 0;
                foreach (var facility in before.Facilities)
                {
                    var index = facility.Id - 101;
                    if ((uint)index >= 6 || (seen & (1 << index)) != 0) throw new InvalidDataException("设施身份必须是互异的101..106。");
                    seen |= 1 << index; facilities[index] = facility.StateId;
                }
                foreach (var assignment in before.Assignments)
                {
                    if (assignment.FacilityId is < 101 or > 106) throw new InvalidDataException("岛训练分配设施无效。");
                    guests[assignment.FacilityId - 101] = assignment.GuestCount;
                    foreach (var card in assignment.Supports)
                    {
                        if (card.Position is < 1 or > 6 || locations[card.Position - 1] != 0) throw new InvalidDataException("岛训练参与位置重复或无效。");
                        locations[card.Position - 1] = assignment.FacilityId;
                    }
                }
                state.SetSection(PioneerStateSection.SupportBonds, bonds);
                state.SetSection(PioneerStateSection.IslandSupportFacilities, locations);
                state.SetSection(PioneerStateSection.FacilityStates, facilities);
                state.SetSection(PioneerStateSection.FacilityGuests, guests);
                state.SetSection(PioneerStateSection.TrainingLevels, entry.Training.OrdinaryLevels.AsSpan());
                state.SetSection(PioneerStateSection.CharacterEffects, before.CharacterEffects.AsSpan());
                state.SetSection(PioneerStateSection.HintCandidates, before.HintCandidates.AsSpan());
                state.SetSection(PioneerStateSection.PendingStories, before.PendingStoryIds.AsSpan());
                break;
            case PioneerStart.Planning entry:
                if (Construction is null) throw new InvalidDataException("建设规划缺少master规则。");
                Construction.ValidatePlanningState(entry.State);
                ImportDevelopment(state, entry.State);
                header.Stage = PioneerStage.ConstructionPlanning;
                break;
            case PioneerStart.Construction entry:
                if (entry.Event.CharacterId != 0 || (entry.Event.EventId, entry.Event.StoryId, entry.Event.PlayTiming) is not
                    ((211067, 400011107, 6) or (211076, 400011107, 10) or (211068, 400011108, 1)))
                    throw new InvalidDataException("建设事件身份必须是107/时机6、107/时机10或108/时机1的已识别事件，角色为0。");
                ImportDevelopment(state, entry.State);
                header.Stage = PioneerStage.ConstructionEvent;
                header.KnownParts |= PioneerKnownParts.EventIdentity;
                header.Event = entry.Event;
                if (entry.State.PendingStoryIds is not { } pending || pending.IsDefault || !pending.Contains(entry.Event.StoryId))
                    throw new InvalidDataException("建设事件不在明确的当前事件列表中。");
                break;
            case PioneerStart.HintAttribute entry:
                if (entry.RewardGroups.IsDefaultOrEmpty) throw new InvalidDataException("Hint属性事件必须提供明确的master奖励组。");
                header.Stage = PioneerStage.HintAttributeEvent;
                header.KnownParts = PioneerKnownParts.Resources | PioneerKnownParts.Limits | PioneerKnownParts.EventIdentity | PioneerKnownParts.FutureOverflow;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState;
                header.Event = entry.Event; header.Resources = entry.Resources; header.Limits = entry.Limits;
                break;
            case PioneerStart.TuckerFriend entry:
                if (entry.Context.StoryId != entry.Event.StoryId || entry.SkillPoints < 0)
                    throw new InvalidDataException("Tucker事件身份或技能Pt无效。");
                header.Stage = PioneerStage.TuckerFriendEvent;
                header.KnownParts = PioneerKnownParts.Motivation | PioneerKnownParts.TuckerProgress | PioneerKnownParts.EventIdentity
                    | PioneerKnownParts.Vital | PioneerKnownParts.MaxVital | PioneerKnownParts.SkillPoints;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.Event = entry.Event;
                header.RequiredChoice = entry.Context.ChoiceNumber;
                header.Resources = new(Vital: entry.State.Vital, SkillPoints: entry.SkillPoints, Speed: 0, Stamina: 0, Power: 0, Guts: 0, Wisdom: 0);
                header.Limits = new(0, 0, 0, 0, 0, entry.State.MaxVital);
                header.Motivation = entry.State.Motivation; header.TuckerBond = entry.State.Bond;
                header.OutingUnlocked = entry.State.OutingUnlocked; header.TuckerStoryStep = entry.State.StoryStep;
                header.HasCharm = entry.Context.HasCharmEffect;
                break;
            case PioneerStart.TuckerPoints entry:
                if (entry.Context.StoryId != entry.Event.StoryId || entry.Context.PlayTiming != entry.Event.PlayTiming)
                    throw new InvalidDataException("Tucker发展Pt事件身份不一致。");
                header.Stage = PioneerStage.TuckerPointEvent;
                header.KnownParts = PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.DeckId
                    | PioneerKnownParts.EventIdentity | PioneerKnownParts.SkillPoints;
                header.Turn = entry.Context.Turn; header.RawPlayingState = entry.Context.RawPlayingState; header.Event = entry.Event;
                header.RequiredChoice = entry.Context.ChoiceNumber; header.DeckId = entry.Context.DeckId;
                header.DevelopmentPoints = entry.Resources.DevelopmentPoints;
                header.Resources = new(0, 0, 0, 0, 0, 0, entry.Resources.SkillPoints);
                break;
            case PioneerStart.TuckerSpecialty entry:
                if (entry.Context.StoryId != entry.Event.StoryId || entry.Context.PlayTiming != entry.Event.PlayTiming)
                    throw new InvalidDataException("Tucker临时效果事件身份不一致。");
                header.Stage = PioneerStage.TuckerSpecialtyEvent; header.KnownParts = PioneerKnownParts.SupportBonds | PioneerKnownParts.EventIdentity;
                header.Turn = entry.Context.SourceTurn; header.RawPlayingState = entry.Context.RawPlayingState; header.Event = entry.Event;
                header.RequiredChoice = entry.Context.ChoiceNumber;
                state.SetSection(PioneerStateSection.SupportBonds, entry.Deck.OrderBy(card => card.Position).Select(card => card.Bond).ToArray());
                break;
            case PioneerStart.ProjectMembers entry:
                if (entry.Members.IsDefault || entry.Supports.IsDefault) throw new InvalidDataException("PJ起点必须包含明确的成员和编成。");
                header.Stage = PioneerStage.ProjectMemberEvent;
                header.KnownParts = PioneerKnownParts.ProjectMembers | PioneerKnownParts.EventIdentity | PioneerKnownParts.CheckpointResult;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.Event = entry.Event;
                header.CheckpointResult = entry.ResultState;
                state.SetSection(PioneerStateSection.ProjectMembers, entry.Members.SelectMany(member => new[] { member.TargetId, member.CharacterId }).ToArray());
                break;
            case PioneerStart.Checkpoint entry:
                if (entry.Context.StoryId != entry.Event.StoryId) throw new InvalidDataException("评审事件身份不一致。");
                header.Stage = PioneerStage.CheckpointEvent;
                header.KnownParts = PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.DeckId | PioneerKnownParts.CheckpointResult
                    | PioneerKnownParts.CheckpointProgress | PioneerKnownParts.TrainingLevels | PioneerKnownParts.EventIdentity
                    | PioneerKnownParts.Vital | PioneerKnownParts.MaxVital;
                header.Turn = entry.Context.Turn; header.RawPlayingState = entry.Context.RawPlayingState;
                header.Event = entry.Event; header.DeckId = entry.Context.DeckId; header.RequiredChoice = entry.Context.ChoiceNumber;
                header.CheckpointResult = entry.Context.ResultState;
                header.DevelopmentPoints = entry.Resources.DevelopmentPoints;
                header.Resources = new(0, 0, 0, 0, 0, entry.Resources.Vital, 0); header.Limits = new(0, 0, 0, 0, 0, entry.Resources.MaxVital);
                header.DirectorBond = entry.Progress.DirectorBond; header.HasAttention = entry.Progress.HasAttentionEffect;
                var levels = entry.Progress.TrainingLevels;
                state.SetSection(PioneerStateSection.TrainingLevels, [levels.Speed, levels.Stamina, levels.Power, levels.Guts, levels.Wisdom]);
                break;
            case PioneerStart.CheckpointDisplay entry:
                if (entry.Display.Confirmed) throw new InvalidDataException("评审显示已确认；请提供当前待处理决策的起点。");
                header.Stage = PioneerStage.CheckpointConfirmation;
                header.KnownParts = PioneerKnownParts.EventIdentity | PioneerKnownParts.CheckpointResult;
                header.Turn = entry.Turn; header.RawPlayingState = entry.RawPlayingState; header.Event = entry.Event;
                header.CheckpointResult = entry.Display.CheckpointResult; header.DisplayConfirmed = entry.Display.Confirmed;
                if (entry.Display.CharacterStoryId != entry.Event.StoryId) throw new InvalidDataException("已发奖显示边界故事身份不一致。");
                break;
            default: throw new InvalidDataException("未知Pioneer片段起点类型。");
        }
        if (header.Turn < 1 || header.RawPlayingState < 1) throw new InvalidDataException("片段起点回合或阶段无效。");
        header.TurnKnown = true; header.RawPlayingStateKnown = true;
        header.SourceTurn = header.Turn; header.SourcePlayingState = header.RawPlayingState;
        if ((header.KnownParts & PioneerKnownParts.EventIdentity) != 0 && (header.Event.EventId <= 0 || header.Event.StoryId <= 0))
            throw new InvalidDataException("当前事件必须包含明确的event_id和story_id。");
        return state;
    }

    static void ValidateTrainingTurn(PioneerStart.TrainingTurn entry)
    {
        if (entry.CharacterId is <= 0) throw new InvalidDataException("明确角色身份必须为正整数。");
        if (entry.CardId <= 0 || entry.Turn is < 1 or > 78 || entry.PendingStoryIds.IsDefault
            || entry.CharacterEffects.IsDefault || entry.CharacterEffects.Distinct().Count() != entry.CharacterEffects.Length || entry.DevelopmentPoints < 0)
            throw new InvalidDataException("训练状态要求回合1..78、明确事件队列、角色效果及非负发展Pt。");
        if (entry.AvailableRestCommands is { } restCommands
            && restCommands != 0 && restCommands != (entry.Camp is null ? RestRules.OrdinaryAvailable : RestRules.CampAvailable))
            throw new InvalidDataException("可用休息指令掩码与普通／合宿面板模式不一致。");
        if (entry.PendingTrainingLevelEvent is not null && entry.PendingRestEvent is not null)
            throw new InvalidDataException("当前只能确认一个明确事件，休息与训练升级不能同时作为当前决策。");
        if (entry.PendingRestEvent is { } restEvent)
        {
            if (entry.RawPlayingState != 5 || entry.Camp is not null || entry.PendingStoryIds.Length != 1
                || entry.PendingStoryIds[0] != restEvent.StoryId)
                throw new InvalidDataException("普通休息确认要求阶段5、普通面板和该唯一待确认事件。");
            _ = RestRules.Recovery(restEvent, entry.CharacterId ?? throw new UnknownMechanismException("休息确认缺少master映射的角色身份。"));
        }
        else if (entry.PendingTrainingLevelEvent is { } levelEvent)
        {
            if (entry.RawPlayingState != 5 || levelEvent.CharacterId != 0 || levelEvent.StoryId != 400000005 || levelEvent.PlayTiming != 6
                || entry.PendingStoryIds.Length != 1 || entry.PendingStoryIds[0] != levelEvent.StoryId || entry.Camp is not null)
                throw new InvalidDataException("单项训练升级要求阶段5、角色0、故事400000005、时机6和该唯一待确认事件，训练模式为普通训练。");
            _ = TrainingLevelRules.TargetIndex(levelEvent.EventId);
        }
        else if (entry.RawPlayingState != 1 || !entry.PendingStoryIds.IsEmpty)
            throw new InvalidDataException("训练选择要求阶段1和明确空事件队列。");
        if (entry.CardId != 100701 && entry.Environment.GoldShipBoost is not null)
            throw new InvalidDataException("只有黄金船衣装100701可以携带指定训练加成。");
        if (entry.Deck.IsDefault || entry.Deck.Length != 6 || entry.Deck.Select(card => card.Position).Distinct().Count() != 6
            || entry.Deck.Any(card => card.Position is < 1 or > 6 || card.Bond is < 0 or > 100 || card.Effects.IsDefault || card.UniqueEffects.IsDefault
                || card.TrainingCommandId is not (0 or 101 or 105 or 102 or 103 or 106)))
            throw new InvalidDataException("训练需要位置1..6的完整编成、羁绊和静态面板。");
        if (entry.Commands.IsDefault || entry.Commands.Length != 5 || entry.Commands.Select(command => command.CommandId).Distinct().Count() != 5)
            throw new InvalidDataException("训练需要五项明确指令，禁用项保留enabled=false。");
        if (entry.SuccessfulOrdinaryTrainingCounts is { } successful && (successful.IsDefault || successful.Length != 5 || successful.Any(count => count < 0)))
            throw new InvalidDataException("已知成功普通训练历史要求按五属性顺序提供五项非负次数；未知历史使用null。");
        if (entry.Camp is { } camp && (camp.OrdinaryLevels.IsDefault || camp.OrdinaryLevels.Length != 5
            || camp.OrdinaryLevels.Any(level => level is < 1 or > 5) || camp.Bases.IsDefault || camp.Bases.Length != 5
            || camp.Facilities.IsDefault))
            throw new InvalidDataException("合宿输入需要五项基础训练等级、按五属性顺序排列的master基值及完整设施。");
        if (entry.CompletedCheckpoint is < 0 or > 5 || entry.CompletedCheckpoint < 5 && entry.CheckpointBonusPercent is null or < 0
            || entry.CompletedCheckpoint == 0 && entry.CheckpointBonusPercent != 0 || entry.CompletedCheckpoint == 5 && entry.CheckpointBonusPercent is not null)
            throw new InvalidDataException("普通训练评审期及发展Pt加成不完整或互相矛盾。");
        if (entry.PlanningPeriods.IsDefault || entry.PlanningPeriods.Length != 5)
            throw new InvalidDataException("训练输入需要当前deck对应的五期master规划记录。");
        var completed = 0;
        for (var index = 0; index < entry.PlanningPeriods.Length; index++)
        {
            var period = entry.PlanningPeriods[index];
            if (period is null || period.CheckPointType != index + 1 || period.DeckId != entry.PlanningPeriods[0].DeckId
                || period.DeckId < 0 || period.TargetPt <= 0 || period.PlanningTurn < 1 || period.CheckPointTurn <= period.PlanningTurn
                || index > 0 && period.PlanningTurn != entry.PlanningPeriods[index - 1].CheckPointTurn)
                throw new InvalidDataException("训练规划记录必须属于同一deck并按连续评审期排列。");
            // Checkpoints follow the action on their turn; the next training decision is after that turn.
            if (period.CheckPointTurn < entry.Turn) completed++;
        }
        if (entry.CompletedCheckpoint != completed)
            throw new InvalidDataException($"训练回合{entry.Turn}应已完成{completed}期评审，输入为{entry.CompletedCheckpoint}期。");
        var deck = entry.Deck.ToDictionary(card => card.Position);
        var assigned = 0;
        foreach (var command in entry.Commands)
        {
            var ordinaryCommand = TrainingBaseTable.OrdinaryCommand(command.CommandId);
            var index = TrainingFacilityRules.PrimaryTarget(ordinaryCommand) - 1;
            if (entry.SuccessfulOrdinaryTrainingCounts is { } history && history[index] > command.ExecutionCount)
                throw new InvalidDataException($"训练{command.CommandId}的成功普通次数大于全部执行次数。");
            if (entry.PendingTrainingLevelEvent is { } pending && TrainingLevelRules.TargetIndex(pending.EventId) == index
                && (command.Level >= 5 || entry.SuccessfulOrdinaryTrainingCounts is { } known && (known[index] == 0 || known[index] % 4 != 0)))
                throw new InvalidDataException("单项训练升级的当前等级必须小于5；已知成功普通次数必须是正的4倍数。");
            if ((entry.Camp is null) != (ordinaryCommand == command.CommandId))
                throw new InvalidDataException("训练指令模式与合宿配置不一致，不能混合普通和合宿指令。");
            if (command.Level is < 1 or > 5 || command.ExecutionCount < 0 || command.GuestCount < 0
                || command.FailureCoefficient < 0 || command.EvaluationTrainingPercent < 0 || command.SupportPositions.IsDefault
                || command.SupportPositions.Distinct().Count() != command.SupportPositions.Length
                || command.SupportPositions.Any(position => !deck.ContainsKey(position)) || command.HintPositions.IsDefault
                || command.HintPositions.Distinct().Count() != command.HintPositions.Length
                || command.HintPositions.Any(position => !command.SupportPositions.Contains(position) || deck[position].TrainingCommandId == 0))
                throw new InvalidDataException($"普通训练{command.CommandId}的等级、执行次数、参与者或Hint不完整。");
            if (command.GuestCount > 5 - command.SupportPositions.Length)
                throw new InvalidDataException($"训练{command.CommandId}的支援与PJ参与者总数超过5人：支援{command.SupportPositions.Length}人，PJ{command.GuestCount}人。");
            foreach (var position in command.SupportPositions)
            {
                var bit = 1 << (position - 1);
                if ((assigned & bit) != 0)
                    throw new InvalidDataException($"支援位置{position}重复出现在多个训练；当前编成核未实现双位置效果。");
                assigned |= bit;
            }
            var supports = command.SupportPositions.Select(position => deck[position]).ToImmutableArray();
            if (entry.Camp is { } configuration)
                _ = TrainingNumericKernel.GenerateCamp(new(command.CommandId, command.Level, command.GuestCount,
                    entry.Environment, supports, configuration.Bases[index], configuration.Facilities));
            else
                _ = TrainingNumericKernel.GenerateOrdinary(new(command.CommandId, command.Level, entry.Environment.Motivation,
                    entry.Environment.Fans, command.GuestCount, entry.Environment.Current, entry.Environment.Limits,
                    entry.Environment.GrowthPercent, supports, command.EvaluationTrainingPercent, entry.Environment.GoldShipBoost));
        }
    }

    static void ValidateIslandInputs(PioneerStart.IslandTraining entry)
    {
        var before = entry.State;
        var training = entry.Training;
        IslandActionRules.ValidateInputs(before);
        if (before.CharacterEffects.IsDefault || before.HintCandidates.IsDefault || before.PendingStoryIds.IsDefault
            || before.Assignments.IsDefault || training.Assignments.IsDefault || before.Facilities.IsDefault || training.Facilities.IsDefault)
            throw new InvalidDataException("岛训练人员、设施、角色效果、Hint候选及当前队列不能缺失。");
        if (training.Assignments.Length != before.Assignments.Length
            || training.Assignments.Zip(before.Assignments).Any(pair => pair.First.FacilityId != pair.Second.FacilityId
                || pair.First.GuestCount != pair.Second.GuestCount || pair.First.Supports.IsDefault || pair.Second.Supports.IsDefault
                || pair.First.Supports.Length != pair.Second.Supports.Length
                || pair.First.Supports.Zip(pair.Second.Supports).Any(cards => cards.First.CardId != cards.Second.CardId
                    || cards.First.Position != cards.Second.Position || cards.First.Level != cards.Second.Level
                    || cards.First.Bond != cards.Second.Bond || cards.First.TrainingCommandId != cards.Second.TrainingCommandId
                    || !cards.First.Effects.AsSpan().SequenceEqual(cards.Second.Effects.AsSpan())
                    || !cards.First.UniqueEffects.AsSpan().SequenceEqual(cards.Second.UniqueEffects.AsSpan())))
            || training.Facilities.Length != before.Facilities.Length
            || training.Facilities.Zip(before.Facilities).Any(pair => pair.First.Id != pair.Second.Id
                || pair.First.StateId != pair.Second.StateId || pair.First.Level != pair.Second.Level
                || pair.First.Effects.IsDefault || pair.Second.Effects.IsDefault
                || !pair.First.Effects.AsSpan().SequenceEqual(pair.Second.Effects.AsSpan())))
            throw new InvalidDataException("岛训练生成输入与行动前人员/设施不一致；不能以另一份输入覆盖当前事实。");
    }

    static void ValidateTrainingDevelopment(PioneerStart.TrainingTurn training, PioneerDevelopmentState development,
        PioneerConstructionRules construction)
    {
        if (development.Turn != training.Turn || development.RawPlayingState != training.RawPlayingState
            || development.DevelopmentPoints != training.DevelopmentPoints || development.DeckId != training.PlanningPeriods[0].DeckId
            || development.PendingStoryIds is not { } pending || pending.IsDefault || !pending.SequenceEqual(training.PendingStoryIds))
            throw new InvalidDataException("训练与建设状态的回合、阶段、发展Pt、deck或当前事件队列不一致。");
        if (training.Camp is { } camp && development.Facilities.Any(facility =>
            !camp.Facilities.Any(value => value.Id == facility.FacilityId && value.StateId == facility.StateId)))
            throw new InvalidDataException("建设状态与合宿训练设施不一致。");
        if (training.RawPlayingState == 1 && training.Turn is >= 5 and <= 60)
            _ = new ConstructionTriggerRules(construction.Data).ApplyPointGainCandidate(
                new(training.Turn, 1, development.DeckId, development.DevelopmentPoints,
                    development.IslandTrainingThresholds, training.PendingStoryIds), 0, ConstructionPointSource.Training);
    }

    static void ImportDevelopment(PioneerState state, PioneerDevelopmentState source)
    {
        ref var header = ref state.Mutable;
        header.KnownParts |= PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.Planning
            | PioneerKnownParts.DeckId | PioneerKnownParts.Facilities;
        header.Turn = source.Turn; header.RawPlayingState = source.RawPlayingState;
        header.DevelopmentPoints = source.DevelopmentPoints; header.PlanningCost = source.PlanningCost; header.DeckId = source.DeckId;
        if (source.IslandTrainingTickets is { } tickets)
        {
            header.Tickets = tickets;
            header.KnownParts |= PioneerKnownParts.Tickets;
        }
        if (source.PendingStoryIds is not { } pending || pending.IsDefault || source.IslandTrainingThresholds.IsDefault)
            throw new InvalidDataException("建设起点缺少当前事件或岛训练阈值数组。");
        state.SetSection(PioneerStateSection.PendingStories, pending.AsSpan());
        state.SetSection(PioneerStateSection.Thresholds, source.IslandTrainingThresholds.AsSpan());
        state.SetSection(PioneerStateSection.FacilityStates, source.Facilities.SelectMany(value => new[] { value.FacilityId, value.StateId }).ToArray());
        state.SetSection(PioneerStateSection.ConstructionSteps, source.Plan.Steps.SelectMany(value =>
            new[] { value.Priority, value.FacilityId, value.PreviousStateId, value.TargetStateId }).ToArray());
    }

    static long EstimateStart(PioneerStart start) => start switch
    {
        PioneerStart.FailureTraining entry => 512L + 4L * (entry.SupportBonds.Length + entry.CharacterEffects.Length),
        PioneerStart.DeclaredEvent entry => 1056L + 32L * (entry.Partners?.Length ?? 0) + 4L * entry.CharacterEffects.Length + entry.Choices.Sum(choice => 64L
            + choice.Branches.Sum(branch => 64L + 64L * branch.Effects.Length)),
        PioneerStart.TrainingTurn turn => 2048L + 5 * SupportStorage(turn.Deck)
            + (turn.Camp is { } camp ? 512L + FacilityStorage(camp.Facilities) : 0)
            + 64L * turn.PlanningPeriods.Length
            + turn.Commands.Sum(command => 256L + 4L * (command.SupportPositions.Length + command.HintPositions.Length))
            + 4L * (turn.CharacterEffects.Length + turn.PendingStoryIds.Length)
            + (turn.SuccessfulOrdinaryTrainingCounts.HasValue ? 52 : 0)
            + (turn.Development is { } development ? 1024L + 64L * development.Plan.Steps.Length
                + 2L * development.Plan.SemanticId.Length + 32L * development.Facilities.Length
                + 4L * (development.IslandTrainingThresholds.Length + (development.PendingStoryIds?.Length ?? 0)) : 0),
        // Count both retained input graphs even when a caller happens to share their immutable arrays.
        PioneerStart.IslandTraining island => 1024L + 256L * island.State.Deck.Length
            + 4L * (island.State.CharacterEffects.Length + island.State.HintCandidates.Length + island.State.PendingStoryIds.Length
                + island.Training.OrdinaryLevels.Length)
            + FacilityStorage(island.Training.Facilities) + FacilityStorage(island.State.Facilities)
            + island.Training.Assignments.Sum(group => 128L + SupportStorage(group.Supports))
            + island.State.Assignments.Sum(group => 128L + SupportStorage(group.Supports)),
        PioneerStart.Construction construction => 1024L + 64L * construction.State.Plan.Steps.Length
            + 2L * construction.State.Plan.SemanticId.Length + 32L * construction.State.Facilities.Length
            + 4L * (construction.State.IslandTrainingThresholds.Length + (construction.State.PendingStoryIds?.Length ?? 0)),
        PioneerStart.Planning planning => 1024L + 64L * planning.State.Plan.Steps.Length
            + 2L * planning.State.Plan.SemanticId.Length + 32L * planning.State.Facilities.Length
            + 4L * planning.State.IslandTrainingThresholds.Length,
        PioneerStart.ProjectMembers project => 1024L + 64L * (project.Members.Length + project.Supports.Length),
        PioneerStart.HintAttribute hint => 1024L + 64L * hint.RewardGroups.Length,
        PioneerStart.TuckerSpecialty specialty => 1024L + 64L * specialty.Deck.Length,
        _ => 2048,
    };

    static long SupportStorage(IEnumerable<TrainingSupport> supports)
        => supports.Sum(card => 256L + 32L * card.Effects.Length + 48L * card.UniqueEffects.Length);
    static long FacilityStorage(IEnumerable<TrainingFacility> facilities)
        => facilities.Sum(facility => 128L + 32L * facility.Effects.Length);
}
