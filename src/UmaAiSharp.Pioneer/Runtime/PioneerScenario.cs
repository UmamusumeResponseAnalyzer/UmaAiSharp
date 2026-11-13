using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.Scoring;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario(PioneerRuntimeData data, bool autonomous = false)
    : ISimulationScenario<PioneerState, PioneerDecisionView, PioneerRuntimeAction, TerminalMetrics>
{
    static readonly PioneerEvent FirstYearConfirmation = new(211022, 0, 400000100, 6);
    readonly TrainingProjection? trainingProjection = autonomous && data.IsWholeRun ? new(data) : null;
    public string RulesIdentity { get; } = !autonomous || data.TurnRules is null
        ? data.RulesIdentity + "/" + data.Model.Identity
        : data.RulesIdentity + "/" + data.Model.Identity + (data.TurnRules.WholeRun ? "/pioneer-whole-run-v6" : "/pioneer-direct-turn-v1");
    public long SharedMemoryBytes => data.SharedMemoryBytes + (trainingProjection is null ? 0 : TrainingProjection.SharedMemoryBytes);
    public static long DecisionKey(int turn, int eventOrCommandId) => ((long)turn << 32) | (uint)eventOrCommandId;
    public PioneerState CreateWorkspace(in PioneerState source) => source.Clone();
    public long GetWorkspaceBytes(in PioneerState state) => state.WorkspaceBytes;
    public void CopyState(in PioneerState source, ref PioneerState destination) => source.CopyInto(destination);

    public Inspection<PioneerDecisionView> Inspect(in PioneerState state, long decisionOrdinal)
    {
        ref readonly var current = ref state.Header;
        if (current.Stage == PioneerStage.Terminal)
            return current.ConditionalOutcomeCount == data.ConditionalOutcomeCount
                ? Inspection<PioneerDecisionView>.Terminal((int)current.Stage)
                : Inspection<PioneerDecisionView>.Stopped(new(SimulationStopReason.ScriptMismatch,
                    (int)PioneerStopCode.OutcomeIdentityMismatch, (int)current.Stage,
                    current.ConditionalOutcomeCount, data.ConditionalOutcomeCount));
        if (current.Stage == PioneerStage.UnsupportedContinuation)
            return Inspection<PioneerDecisionView>.Stopped(Stop(current.StopCode, current.ContinuationFrom));
        var failure = current.Stage == PioneerStage.FailureChoice;
        var eventDecision = current.Stage is not (PioneerStage.FailureTraining or PioneerStage.IslandTraining or PioneerStage.TrainingTurn);
        var key = DecisionKey(current.Turn, eventDecision
            ? data.UsesStoryDecisions ? current.Event.StoryId : current.Event.EventId : current.CommandId);
        var view = new PioneerDecisionView(current.Stage, current.Turn, current.RawPlayingState, current.KnownParts,
            current.Resources, current.Motivation, current.CommandId, current.Event,
            failure ? 1 : current.Stage == PioneerStage.DeclaredEvent ? data.EventChoiceMinimum : current.RequiredChoice,
            failure ? 2 : current.Stage == PioneerStage.DeclaredEvent ? data.EventChoiceMaximum : current.RequiredChoice,
            current.Stage == PioneerStage.ConstructionPlanning ? CurrentPlanning(state) : null,
            current.Stage == PioneerStage.TrainingTurn ? current.AvailableTrainingCommands : 0,
            current.Stage == PioneerStage.DeclaredEvent ? ((PioneerStart.DeclaredEvent)data.Start).Choices : [],
            current.Stage == PioneerStage.TrainingTurn && CampCommands(current),
            current.KnownParts.HasFlag(PioneerKnownParts.TrainingBoard) ? current.TrainingBoard : null,
            current.Stage == PioneerStage.TrainingTurn ? current.AvailableRestCommands : null,
            autonomous ? SuggestAction(state) : null);
        return Inspection<PioneerDecisionView>.AtDecision(new(new((int)current.Stage, decisionOrdinal, key), view), (int)current.Stage);
    }

    public SimulationStop? ValidateAction(in PioneerState state, in PioneerRuntimeAction action)
    {
        ref readonly var current = ref state.Header;
        if (current.Stage == PioneerStage.Terminal) return Illegal(current.Stage);
        if (current.Stage == PioneerStage.UnsupportedContinuation) return Stop(current.StopCode, current.ContinuationFrom);
        var shapeMatches = action.Kind switch
        {
            PioneerActionKind.Train => action.CommandId > 0 && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is null,
            PioneerActionKind.Rest => action.CommandId is RestRules.OrdinaryCommand or RestRules.CampCommand
                && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is null,
            PioneerActionKind.Treat or PioneerActionKind.Race => action.CommandId > 0 && action.EventId == 0
                && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is null,
            PioneerActionKind.Outing => action.EventId == 0 && action.ChoiceNumber == 0 && action.Plan is null
                && action.CharacterId == 0
                && (action.CommandId == 301 && action.SupportPosition == 0 || action.CommandId == 390 && action.SupportPosition is >= 1 and <= 6),
            PioneerActionKind.IslandTraining => action.CommandId == 0 && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is null,
            PioneerActionKind.ChooseEvent => action.CommandId == 0 && action.EventId > 0 && action.CharacterId >= 0 && action.ChoiceNumber >= 0 && action.Plan is null,
            PioneerActionKind.SubmitConstruction => action.CommandId == 0 && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is not null,
            _ => false,
        };
        if (!shapeMatches || action.Kind != PioneerActionKind.Outing && action.SupportPosition != 0
            || (action.Kind == PioneerActionKind.ChooseEvent ? action.StoryId < 0 || data.UsesStoryDecisions && action.StoryId == 0
            : action.StoryId != 0)) return Illegal(current.Stage);
        var conditional = data.OutcomeAt(current.ActionOrdinal);
        if (conditional is PioneerConditionalOutcome.TurnArrival arrival)
        {
            if (current.Stage is not (PioneerStage.RestConfirmation or PioneerStage.TrainingLevelConfirmation or PioneerStage.TurnConfirmation)
                && !(current.Stage == PioneerStage.TrainingTurn && action.Kind is PioneerActionKind.Treat or PioneerActionKind.Outing or PioneerActionKind.Race or PioneerActionKind.IslandTraining)
                || arrival.Arrival == PioneerArrival.TrainingLevel
                || arrival.Arrival == PioneerArrival.TurnConfirmation && (current.Turn != 13 || current.Stage == PioneerStage.TurnConfirmation)
                || arrival.Arrival == PioneerArrival.TurnConfirmation && arrival.Event is { } nextEvent
                    && (nextEvent.StoryId != FirstYearConfirmation.StoryId || nextEvent.CharacterId != FirstYearConfirmation.CharacterId
                        || nextEvent.PlayTiming != FirstYearConfirmation.PlayTiming)
                || arrival.AvailableRestCommands is > 0 && arrival.AvailableRestCommands != (data.CampCommands.IsEmpty
                    ? RestRules.OrdinaryAvailable : RestRules.CampAvailable))
                return Stop(PioneerStopCode.OutcomeIdentityMismatch, current.Stage);
        }
        else if (conditional is not null && current.Stage is not
            (PioneerStage.FailureTraining or PioneerStage.FailureChoice or PioneerStage.HintAttributeEvent or PioneerStage.ProjectMemberEvent
                or PioneerStage.TrainingTurn or PioneerStage.DeclaredEvent))
            return Stop(PioneerStopCode.OutcomeIdentityMismatch, current.Stage);
        if (current.Stage == PioneerStage.TrainingTurn)
        {
            if (ActiveScheduledRace(state) is { } race)
                return action.Kind == PioneerActionKind.Race && action.CommandId == race.ProgramId ? null : Illegal(current.Stage);
            if (action.Kind == PioneerActionKind.Rest)
            {
                if (current.AvailableRestCommands is not { } available) return Stop(PioneerStopCode.RestAvailabilityRequired, current.Stage);
                var required = action.CommandId == RestRules.OrdinaryCommand ? RestRules.OrdinaryAvailable : RestRules.CampAvailable;
                return (available & required) != 0 ? null : Illegal(current.Stage);
            }
            if (action.Kind == PioneerActionKind.Train)
                for (var index = 0; index < data.TrainingCommands.Length; index++)
                    if (TrainingCommand(index, current) == action.CommandId && (current.AvailableTrainingCommands & (1 << index)) != 0) return null;
            if (action.Kind == PioneerActionKind.IslandTraining && IslandAvailable(state)) return null;
            if (action.Kind is PioneerActionKind.Treat or PioneerActionKind.Outing or PioneerActionKind.Race
                && HomeActionEnabled(state, action)) return null;
            return Illegal(current.Stage);
        }
        if (current.Stage == PioneerStage.FailureTraining)
            return action.Kind == PioneerActionKind.Train && action.CommandId == current.CommandId ? null : Illegal(current.Stage);
        if (current.Stage == PioneerStage.ConstructionPlanning)
        {
            if (action.Kind != PioneerActionKind.SubmitConstruction) return Illegal(current.Stage);
            try { data.Construction!.ValidateForSubmission(CurrentPlanning(state), action.Plan!); }
            catch (IllegalActionException) { return Illegal(current.Stage); }
            return null;
        }
        if (current.Stage == PioneerStage.IslandTraining)
        {
            var imported = (PioneerStart.IslandTraining)data.Start;
            return action.Kind == PioneerActionKind.IslandTraining && imported.State.CommandEnabled
                && current.Tickets > 0 && current.RawPlayingState == 1 && state.GetSection(PioneerStateSection.PendingStories).IsEmpty
                ? null : Illegal(current.Stage);
        }
        if (action.Kind != PioneerActionKind.ChooseEvent) return Illegal(current.Stage);
        if (action.StoryId != 0 && action.StoryId != current.Event.StoryId)
            return new(SimulationStopReason.IllegalAction, (int)PioneerStopCode.WrongEventIdentity, (int)current.Stage,
                action.StoryId, current.Event.StoryId);
        if (action.EventId != current.Event.EventId || action.CharacterId != current.Event.CharacterId)
            return new(SimulationStopReason.IllegalAction, (int)PioneerStopCode.WrongEventIdentity, (int)current.Stage,
                action.EventId, current.Event.EventId);
        if (current.Stage == PioneerStage.FailureChoice)
            return action.ChoiceNumber is 1 or 2 ? null : Illegal(current.Stage);
        if (current.Stage == PioneerStage.DeclaredEvent)
        {
            foreach (var choice in ((PioneerStart.DeclaredEvent)data.Start).Choices)
                if (choice.ChoiceNumber == action.ChoiceNumber) return null;
            return Illegal(current.Stage);
        }
        return action.ChoiceNumber == current.RequiredChoice ? null : Stop(PioneerStopCode.UnsupportedComponentInput, current.Stage);
    }

    public TransitionResult ApplyAction(ref PioneerState state, in PioneerRuntimeAction action, ref SimulationRandom random)
    {
        if (!ReferenceEquals(state.Data, data)) throw new ArgumentException("场景与状态来自不同编成数据。", nameof(state));
        if (ValidateAction(state, action) is { } invalid) return TransitionResult.Stopped(false, false, invalid);
        if (data.TrainingHistory is { } history && (state.Header.KnownParts & PioneerKnownParts.SuccessfulOrdinaryTrainingCounts) == 0)
        {
            // A sample starts with the same latent history for every root candidate and keeps its rules RNG untouched.
            var historyRandom = new SimulationRandom(SimulationRandom.DeriveSeed(random.State, 0, SimulationRandomStream.Policy));
            Span<int> successes = stackalloc int[5];
            history.Draw(successes, ref historyRandom);
            state.SetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts, successes);
            state.Mutable.KnownParts |= PioneerKnownParts.SuccessfulOrdinaryTrainingCounts;
        }
        try
        {
            return state.Header.Stage switch
            {
                PioneerStage.FailureTraining => BeginFailure(state, state.Header.CommandId),
                PioneerStage.TrainingTurn => action.Kind switch
                {
                    PioneerActionKind.Train => ApplyTraining(state, action.CommandId, ref random),
                    PioneerActionKind.Rest => ApplyRest(state, action.CommandId, ref random),
                    PioneerActionKind.IslandTraining => ApplyIsland(state, ref random),
                    _ => ApplyHomeAction(state, action, ref random),
                },
                PioneerStage.RestConfirmation => ConfirmRest(state, ref random),
                PioneerStage.TurnConfirmation => ConfirmTurn(state, ref random),
                PioneerStage.TrainingLevelConfirmation => ConfirmTrainingLevel(state, ref random),
                PioneerStage.DeclaredEvent => ApplyDeclaredEvent(state, action),
                PioneerStage.CharacterEnding => FinishCharacterEnding(state),
                PioneerStage.FailureChoice => FinishFailure(state, action),
                PioneerStage.IslandTraining => ApplyIsland(state, ref random),
                PioneerStage.ConstructionEvent => ApplyConstruction(state),
                PioneerStage.ConstructionPlanning => SubmitConstruction(state, action.Plan!, ref random),
                PioneerStage.HintAttributeEvent => ApplyHint(state),
                PioneerStage.TuckerFriendEvent => ApplyTuckerFriend(state, action),
                PioneerStage.TuckerPointEvent => ApplyTuckerPoints(state, action),
                PioneerStage.TuckerSpecialtyEvent => ApplyTuckerSpecialty(state, action),
                PioneerStage.ProjectMemberEvent => ApplyProject(state),
                PioneerStage.CheckpointEvent => ApplyCheckpoint(state, action),
                PioneerStage.CheckpointConfirmation => ConfirmCheckpoint(state, action),
                _ => throw new InvalidOperationException("Pioneer执行位置无效。"),
            };
        }
        catch (UnknownMechanismException)
        {
            return TransitionResult.Stopped(false, false, Stop(PioneerStopCode.UnsupportedComponentInput, state.Header.Stage));
        }
    }

    public TransitionResult ResumeAutomatic(ref PioneerState state, ref SimulationRandom random)
    {
        if (state.Header.Stage != PioneerStage.UnsupportedContinuation)
            throw new InvalidOperationException("当前Pioneer状态不在已消费动作的未实现后续步骤。");
        return TransitionResult.Stopped(true, false, Stop(state.Header.StopCode, state.Header.ContinuationFrom));
    }

    public TerminalMetrics GetOutcome(in PioneerState state)
    {
        ref readonly var header = ref state.Header;
        if (header.Stage != PioneerStage.Terminal || header.ConditionalOutcomeCount != data.ConditionalOutcomeCount)
            throw new InvalidOperationException("Pioneer终局要求末尾资源已结算且全部条件结果已消费。");
        var values = header.Resources;
        var limits = header.Limits;
        var attributes = new AttributeValues(values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom);
        return new(attributes,
            new(limits.Speed, limits.Stamina, limits.Power, limits.Guts, limits.Wisdom),
            checked((int)header.FutureGainedSkillPoints),
            (header.KnownParts & PioneerKnownParts.FutureOverflow) != 0 ? header.FutureOverflow : null,
            ScoringCandidates.AttributeScore(attributes), header.FailedCareerGoal == 0 ? null : header.FailedCareerGoal);
    }

    TransitionResult FinishCharacterEnding(PioneerState state)
    {
        var after = PioneerRuntimeData.CharacterEndingReward.Apply(state.Header.Resources, state.Header.Limits, state.Header.Motivation, false, false);
        var gained = checked(state.Header.FutureGainedSkillPoints + after.Resources.SkillPoints - state.Header.Resources.SkillPoints);
        var overflow = state.Header.FutureOverflow + after.Overflow;
        if (state.Header.Event.StoryId != 0) RecordCurrentStory(state);
        ref var header = ref state.Mutable;
        header.Resources = after.Resources; header.FutureGainedSkillPoints = gained; header.FutureOverflow = overflow;
        header.Stage = PioneerStage.Terminal; header.ActionOrdinal++;
        header.Event = default; header.KnownParts &= ~PioneerKnownParts.EventIdentity;
        // Logical prediction ends here; factor selection and account persistence are outside these metrics.
        header.RawPlayingStateKnown = false;
        return TransitionResult.Settled((int)header.Stage);
    }

    public TrainingActionPreview PreviewTraining(in PioneerState state, int commandId)
    {
        if (!ReferenceEquals(state.Data, data) || state.Header.Stage != PioneerStage.TrainingTurn)
            throw new ArgumentException("训练预览要求同一规则配置的待训练状态。", nameof(state));
        var index = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(commandId)) - 1;
        var command = data.TrainingCommands[index];
        if (TrainingCommand(index, state.Header) != commandId) throw new ArgumentException("训练指令与当前面板模式不一致。", nameof(commandId));
        var environment = data.TrainingStart!.Environment with
        { Current = state.Header.Resources, Limits = state.Header.Limits, Fans = state.Header.Fans, Motivation = state.Header.Motivation,
            GoldShipBoost = state.Header.Turn == state.Header.SourceTurn ? data.TrainingStart.Environment.GoldShipBoost : null };
        var camp = commandId >= 3601 ? state.TurnBoard?.Camp[index] ?? data.CampCommands[index] : (CompiledCampTraining?)null;
        var placement = state.Header.TrainingBoard[index];
        var level = camp.HasValue ? 5 : state.GetSection(PioneerStateSection.TrainingLevels)[index];
        var coefficient = data.TurnRules?.FailureCoefficient(commandId, level) ?? command.FailureCoefficient;
        Span<int> positions = stackalloc int[6];
        var length = TrainingBoard.Positions(placement.SupportMask, positions);
        var evaluation = data.Start is PioneerStart.TrainingTurn && state.Header.Turn == state.Header.SourceTurn
            ? command.EvaluationTrainingPercent : state.Header.EvaluationTrainingPercent;
        var bias = 0;
        foreach (var effect in state.GetSection(PioneerStateSection.CharacterEffects)) bias += TrainingFailureRate.StatusBias(effect);
        return data.Model.Preview(environment, commandId, level,
            placement.GuestCount, evaluation, coefficient, bias,
            positions[..length], state.GetSection(PioneerStateSection.SupportBonds), data.TrainingDeck!, camp);
    }

    TransitionResult ApplyTraining(PioneerState state, int commandId, ref SimulationRandom random)
    {
        var conditional = data.OutcomeAt(state.Header.ActionOrdinal);
        if (conditional is PioneerConditionalOutcome.TrainingFailure)
            return BeginFailure(state, commandId);
        if (conditional is not null and not PioneerConditionalOutcome.TrainingResult)
            return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
        var index = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(commandId)) - 1;
        var placement = state.Header.TrainingBoard[index];
        var preview = PreviewTraining(state, commandId);
        var count = checked(state.GetSection(PioneerStateSection.TrainingExecutionCounts)[index] + 1);
        // Commit the draw with the action; an unsupported settlement leaves its pending context unchanged.
        var nextRandom = random;
        var camp = commandId >= 3601;
        // Forced replay results consume no draw from the rule stream.
        var success = conditional is PioneerConditionalOutcome.TrainingResult result ? result.Success
            : preview.FailureRate == 0 || preview.FailureRate < 100 && nextRandom.NextInt(100) >= preview.FailureRate;
        var construction = false;
        var firstFriends = 0;
        ref var header = ref state.Mutable;
        var pointsBefore = header.DevelopmentPoints;
        if (success)
        {
            var gains = preview.Gains;
            var common = gains.Common with { Vital = Math.Max(-header.Resources.Vital, gains.Common.Vital) };
            var values = TrainingPreviewSettlement.Apply(header.Resources, header.Limits, common, gains.Scenario);
            var turn = data.TrainingStart!;
            var points = checked(header.DevelopmentPoints + (header.Turn < turn.PlanningPeriods[0].PlanningTurn || header.CompletedCheckpoint == 5 ? 0
                : PioneerPointGain.OrdinaryTraining(placement.SupportCount + placement.GuestCount, header.CheckpointBonusPercent, preview.Friendship)));
            if (turn.Development is not null && header.Turn is >= 5 and <= 60
                && !(autonomous && data.IsWholeRun) && !camp)
            {
                var thresholds = state.GetSection(PioneerStateSection.Thresholds);
                construction = ConstructionTriggerRules.CrossedThreshold(thresholds, pointsBefore, points) != 0;
            }
            var gained = checked(header.FutureGainedSkillPoints + values.SkillPoints - header.Resources.SkillPoints);
            var overflow = header.FutureOverflow + gains.Overflow;
            var successfulCount = (int?)null;
            if (!camp && (header.KnownParts & PioneerKnownParts.SuccessfulOrdinaryTrainingCounts) != 0)
            {
                var counts = state.MutableSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts);
                counts[index] = checked(counts[index] + 1);
                successfulCount = counts[index];
            }
            header.PendingTrainingLevelCommandId = !camp
                ? TrainingLevelRules.AfterSuccessfulOrdinary(index, state.GetSection(PioneerStateSection.TrainingLevels)[index], successfulCount)
                : 0;
            var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
            var charm = state.GetSection(PioneerStateSection.CharacterEffects).Contains(8);
            var bondBonus = data.TrainingDeck!.TrainingBondBonus(placement.SupportMask);
            foreach (var card in turn.Deck)
                if ((placement.SupportMask & (1 << (card.Position - 1))) != 0)
                    bonds[card.Position - 1] = (int)Math.Min(100L, bonds[card.Position - 1]
                        + (card.TrainingCommandId == 0 ? 4L : 7L) + (charm ? 2 : 0) + bondBonus);
            header.Resources = values; header.DevelopmentPoints = points; header.FutureGainedSkillPoints = gained;
            header.FutureOverflow = overflow;
            firstFriends = header.UnseenFriendMask & placement.SupportMask;
            ApplyFirstFriendTraining(state, firstFriends);
            Span<int> hints = stackalloc int[6];
            var hintCount = TrainingBoard.Positions(placement.HintMask, hints);
            state.SetSection(PioneerStateSection.HintCandidates, hints[..hintCount]);
        }
        random = nextRandom;
        state.MutableSection(PioneerStateSection.TrainingExecutionCounts)[index] = count;
        header.CommandId = commandId; header.TrainingSucceeded = success; header.KnownParts |= PioneerKnownParts.TrainingResult;
        if (conditional is not null) header.ConditionalOutcomeCount++;
        if (construction && !autonomous)
        {
            state.SetSection(PioneerStateSection.PendingStories, [400011107]);
            header.Event = new(211067, 0, 400011107, 6);
            header.KnownParts |= PioneerKnownParts.EventIdentity;
            header.Stage = PioneerStage.ConstructionEvent; header.RawPlayingState = 5;
            header.RequiredChoice = 0; header.ActionOrdinal++;
            return TransitionResult.Settled((int)header.Stage);
        }
        if (autonomous && data.TurnRules is not null)
        {
            var source = camp ? PioneerArrivalSource.CampTraining : PioneerArrivalSource.Training;
            if (success)
            {
                if ((data.IsWholeRun && !ApplyConstructionThresholds(state, pointsBefore))
                    || (construction && !SettleConstruction(state)))
                    return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
                if (data.IsWholeRun)
                {
                    GrantTuckerSpecialty(state, placement.SupportMask, TemporarySupportGrantSource.TuckerTraining);
                    ApplyTuckerRepeat(state, placement.SupportMask & ~firstFriends, island: false, ref random);
                    if (placement.Director) header.DirectorBond = Math.Min(100, header.DirectorBond + (header.HasAttention ? 9 : 7));
                }
                if (header.PendingTrainingLevelCommandId is null)
                    return Partial(state, PioneerStopCode.TrainingLevelHistoryRequired, turnKnown: true);
                if (placement.HintMask != 0)
                    SettleTrainingHint(state, placement.HintMask, AllCampHints(state, commandId), ref random);
            }
            else
            {
                ApplyAutonomousFailure(state, preview.FailureRate, ref random);
            }
            return AdvanceTurn(state, source, ref random);
        }
        if (success && data.TurnRules is not null && placement.HintMask == 0 && header.PendingTrainingLevelCommandId is { } pendingLevel)
            return AdvanceTurn(state, pendingLevel != 0 ? PioneerArrivalSource.TrainingWithLevel
                : data.CampCommands.IsEmpty ? PioneerArrivalSource.Training : PioneerArrivalSource.CampTraining, ref random);
        return Partial(state, success ? PioneerStopCode.TrainingEventsRequired : PioneerStopCode.TrainingFailureEventRequired, turnKnown: true);
    }

    static void ApplyAutonomousFailure(PioneerState state, int risk, ref SimulationRandom random)
    {
        // UmaAi-rust default failure choice. Observed Pioneer outcomes use the conditional path.
        var severe = risk == 100 || risk > 0 && random.NextInt(100) < risk;
        var badTrainer = severe || random.NextInt(99) >= 66;
        if (severe) random.NextInt(2); // Both default heavy outcomes end with the same bad-trainer flag.
        ref var header = ref state.Mutable;
        var values = header.Resources;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var motivation = MotivationRules.Apply(header.Motivation, severe ? -3 : -1, effects.Contains(25), false);
        header.Resources = values with
        {
            Speed = FailureLoss(values.Speed),
            Stamina = severe ? FailureLoss(values.Stamina) : values.Stamina,
            Power = severe ? FailureLoss(values.Power) : values.Power,
        };
        header.Motivation = motivation.Value;
        if (motivation.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
        if (badTrainer)
        {
            if (state.GetSection(PioneerStateSection.CharacterEffects).Contains(10)) state.RemoveCharacterEffect(10);
            if (state.GetSection(PioneerStateSection.CharacterEffects).Contains(11)) state.RemoveCharacterEffect(11);
            state.AddCharacterEffect(6);
        }
        if (severe) header.TrainingFailureIllness = true;

        static int FailureLoss(int displayed)
        {
            var raw = displayed > 1200 ? 2 * displayed - 1200 : displayed;
            var after = Math.Max(0, raw - 10);
            return after > 1200 ? 1200 + (after - 1200) / 2 : after;
        }
    }

    TransitionResult ConfirmTrainingLevel(PioneerState state, ref SimulationRandom random)
    {
        RecordCurrentStory(state);
        SettleTrainingLevel(state, state.Header.PendingTrainingLevelCommandId!.Value);
        state.SetSection(PioneerStateSection.PendingStories, []);
        state.Mutable.Event = default;
        state.Mutable.KnownParts &= ~PioneerKnownParts.EventIdentity;
        return data.TurnRules is null ? Partial(state, PioneerStopCode.TrainingLevelContinuationRequired)
            : AdvanceTurn(state, PioneerArrivalSource.TrainingLevel, ref random);
    }

    static void SettleTrainingLevel(PioneerState state, int command)
    {
        var index = TrainingFacilityRules.PrimaryTarget(command) - 1;
        state.MutableSection(PioneerStateSection.TrainingLevels)[index]++;
        state.Mutable.PendingTrainingLevelCommandId = 0;
    }

    TransitionResult ApplyDeclaredEvent(PioneerState state, PioneerRuntimeAction action)
    {
        var choices = ((PioneerStart.DeclaredEvent)data.Start).Choices;
        var choiceIndex = 0;
        while (choices[choiceIndex].ChoiceNumber != action.ChoiceNumber) choiceIndex++;
        var branches = data.EventRewards[choiceIndex];
        var conditional = data.OutcomeAt(state.Header.ActionOrdinal);
        if (conditional is not null and not PioneerConditionalOutcome.EventRewardResult)
            return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
        if (conditional is null && branches.Length != 1)
            return Missing(state, PioneerStopCode.EventRewardOutcomeRequired);
        var branchIndex = 0;
        if (conditional is PioneerConditionalOutcome.EventRewardResult result)
        {
            if (result.ChoiceNumber != action.ChoiceNumber || (uint)result.BranchIndex >= (uint)branches.Length)
                return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
            branchIndex = result.BranchIndex;
        }
        var settlement = branches[branchIndex];
        var reward = settlement.Resources;
        if (reward.ResourceMask == 0 && !reward.RemainingEffects.IsEmpty && !settlement.HasPartnerEffects)
            return Missing(state, PioneerStopCode.EventRewardRemainderRequired);
        var after = reward.Apply(state.Header.Resources, state.Header.Limits, state.Header.Motivation,
            state.GetSection(PioneerStateSection.CharacterEffects).Contains(25),
            state.GetSection(PioneerStateSection.CharacterEffects).Contains(5));
        var gained = checked(state.Header.FutureGainedSkillPoints + Math.Max(0, after.Resources.SkillPoints - state.Header.Resources.SkillPoints));
        var overflow = state.Header.FutureOverflow + after.Overflow;
        ref var header = ref state.Mutable;
        header.Resources = after.Resources; header.Limits = after.Limits; header.Motivation = after.Motivation;
        header.FutureGainedSkillPoints = gained; header.FutureOverflow = overflow;
        header.EventRewardResourceMask = reward.ResourceMask; header.EventRewardRemainingEffects = reward.RemainingEffects.Length;
        settlement.ApplyPartners(state.MutableSection(PioneerStateSection.PartnerEvaluations));
        if (after.ConsumePositiveThinking)
            state.RemoveCharacterEffect(25);
        if (conditional is not null) header.ConditionalOutcomeCount++;
        return Partial(state, PioneerStopCode.EventRewardRemainderRequired);
    }

    TransitionResult ApplyRest(PioneerState state, int commandId, ref SimulationRandom random)
    {
        if (commandId == RestRules.CampCommand)
        {
            if (data.OutcomeAt(state.Header.ActionOrdinal) is not null)
                return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
            RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
            var vital = VitalRules.Apply(state.Header.Resources.Vital, state.Header.Limits.Vital, 40, 0).Vital;
            ref var camp = ref state.Mutable;
            camp.Resources = camp.Resources with { Vital = vital };
            ApplyMotivationGain(state, 1);
            camp.CommandId = commandId;
            return data.TurnRules is null ? Partial(state, PioneerStopCode.RestContinuationRequired)
                : AdvanceTurn(state, PioneerArrivalSource.CampRest, ref random);
        }
        if (autonomous && data.TurnRules is { } rules)
        {
            RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
            var resources = state.Header.Resources;
            state.Mutable.Resources = resources with { Vital = VitalRules.Apply(resources.Vital, state.Header.Limits.Vital,
                rules.DrawRestRecovery(ref random), 0).Vital };
            state.Mutable.CommandId = commandId;
            return AdvanceTurn(state, PioneerArrivalSource.RestConfirmation, ref random);
        }
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.RestEvent outcome)
            return MissingOutcome(state, PioneerStopCode.RestOutcomeRequired);
        if (data.TrainingCharacterId is not { } characterId) return Missing(state, PioneerStopCode.RestCharacterRequired);
        RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
        var current = outcome.Event;
        _ = RestRules.Recovery(current, characterId);
        state.SetSection(PioneerStateSection.PendingStories, [current.StoryId]);
        ref var header = ref state.Mutable;
        header.CommandId = commandId;
        header.Event = current;
        header.Stage = PioneerStage.RestConfirmation;
        header.RawPlayingState = 5;
        header.KnownParts |= PioneerKnownParts.EventIdentity;
        header.ActionOrdinal++;
        header.ConditionalOutcomeCount++;
        return TransitionResult.Settled((int)header.Stage);
    }

    TransitionResult ConfirmRest(PioneerState state, ref SimulationRandom random)
    {
        var gain = RestRules.Recovery(state.Header.Event, state.Data.TrainingCharacterId!.Value);
        RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
        var vital = VitalRules.Apply(state.Header.Resources.Vital, state.Header.Limits.Vital, gain, 0).Vital;
        state.Mutable.Resources = state.Header.Resources with { Vital = vital };
        RecordCurrentStory(state);
        state.Mutable.Event = default;
        state.Mutable.KnownParts &= ~PioneerKnownParts.EventIdentity;
        state.SetSection(PioneerStateSection.PendingStories, []);
        return data.TurnRules is null ? Partial(state, PioneerStopCode.RestContinuationRequired)
            : AdvanceTurn(state, PioneerArrivalSource.RestConfirmation, ref random);
    }

    TransitionResult ConfirmTurn(PioneerState state, ref SimulationRandom random)
    {
        RecordCurrentStory(state);
        return AdvanceTurn(state, PioneerArrivalSource.TurnConfirmation, ref random);
    }

    void RecordCurrentStory(PioneerState state)
    {
        if (!data.UsesStoryDecisions) return;
        ref readonly var header = ref state.Header;
        state.RecordStory(new(header.Event.StoryId, header.Turn, header.RawPlayingState,
            header.Event.PlayTiming, header.Event.CharacterId, 0, 0, header.RequiredChoice));
    }

    TransitionResult AdvanceTurn(PioneerState state, PioneerArrivalSource source, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var outcome = data.OutcomeAt(header.ActionOrdinal) as PioneerConditionalOutcome.TurnArrival;
        var next = checked(header.Turn + 1);
        var wholeRun = autonomous && data.IsWholeRun;
        if (autonomous)
        {
            if (header.PendingTrainingLevelCommandId is > 0 and var command)
            {
                SettleTrainingLevel(state, command);
                source = PioneerArrivalSource.TrainingLevel;
            }
            // The turn-13 confirmation has no resource effect. Its observations cover the ensuing ordinary events.
            if (header.Turn == 13) source = PioneerArrivalSource.TurnConfirmation;
            if (wholeRun) ApplyFixedTurnEvents(state, header.Turn, arriving: false, ref random);
            else ApplyOrdinaryArrival(state, source, header.Turn, ref random);

            header.KnownParts &= ~PioneerKnownParts.TrainingBoard;
            header.AvailableTrainingCommands = 0;
            header.AvailableRestCommands = null;
            state.SetSection(PioneerStateSection.HintCandidates, []);
            if (wholeRun)
            {
                if (SettleCareerGoals(state) is { } failedGoal) return EndCareer(state, failedGoal);
                var periods = data.TrainingStart!.PlanningPeriods;
                if (header.CompletedCheckpoint < periods.Length && periods[header.CompletedCheckpoint].CheckPointTurn == header.Turn)
                {
                    SettleCheckpoint(state, periods[header.CompletedCheckpoint]);
                    if (header.CompletedCheckpoint < periods.Length)
                    {
                        return BeginPlanning(state, periods[header.CompletedCheckpoint]);
                    }
                    else
                        state.SetSection(PioneerStateSection.Thresholds, []);
                }
                if (next == periods[0].PlanningTurn)
                {
                    return BeginPlanning(state, periods[0]);
                }
                if (next == 41 && header.DeferredConstruction)
                {
                    if (!SettleConstruction(state)) return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
                    header.DeferredConstruction = false;
                }
                if (next == 65)
                {
                    header.Tickets = Math.Min(3, header.Tickets + 2);
                    header.KnownParts |= PioneerKnownParts.Tickets;
                }
                if (next == 73)
                {
                    header.PioneerCompleted = true;
                    header.Tickets = 0;
                }
                if (next is 74 or 76 or 78)
                {
                    SettleUraRace(state, next, ref random);
                    if (next == 78) return FinishAutonomousEnding(state, ref random);
                    next++;
                }
            }
        }
        else
        {
            if (outcome is not null) header.ConditionalOutcomeCount++;
            var level = source == PioneerArrivalSource.TrainingWithLevel;
            if (level || outcome?.Arrival == PioneerArrival.TurnConfirmation)
            {
                header.Event = level ? TrainingLevelRules.Reveal(header.PendingTrainingLevelCommandId!.Value) : outcome!.Event ?? FirstYearConfirmation;
                state.SetSection(PioneerStateSection.PendingStories, [header.Event.StoryId]);
                header.Stage = level ? PioneerStage.TrainingLevelConfirmation : PioneerStage.TurnConfirmation;
                header.RawPlayingState = 5;
                header.RequiredChoice = 0;
                header.KnownParts |= PioneerKnownParts.EventIdentity;
                header.ActionOrdinal++;
                return TransitionResult.Settled((int)header.Stage);
            }
            if (outcome is null) return Partial(state, PioneerStopCode.TurnArrivalOutcomeRequired, turnKnown: true);
            if (outcome.Arrival == PioneerArrival.OtherPhase) return Partial(state, PioneerStopCode.NextTurnPhaseRequired);
            if (outcome.Arrival is PioneerArrival.SameTurnEvent or PioneerArrival.NextTurnEvent)
            {
                if (outcome.Arrival == PioneerArrival.NextTurnEvent) header.Turn = next;
                if (outcome.Event is { } pending)
                {
                    header.Event = pending;
                    header.KnownParts |= PioneerKnownParts.EventIdentity;
                    state.SetSection(PioneerStateSection.PendingStories, [pending.StoryId]);
                }
                return Partial(state, PioneerStopCode.TurnEventDispatchRequired, turnKnown: true);
            }
        }
        if (!wholeRun && (next >= 78 || IsCamp(next) != IsCamp(header.Turn)))
            return Partial(state, PioneerStopCode.NextTurnPhaseRequired);
        return ArriveAtTraining(state, next, outcome, ref random);
    }

    void ApplyOrdinaryArrival(PioneerState state, PioneerArrivalSource source, int turn, ref SimulationRandom random)
    {
        if (data.TurnRules!.DrawOrdinaryEvents(source, turn, ref random) is not { } ordinary) return;
        ref var header = ref state.Mutable;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var after = ordinary.Resources.Apply(header.Resources, header.Limits, header.Motivation, effects.Contains(25), effects.Contains(5));
        header.FutureGainedSkillPoints += after.Resources.SkillPoints - header.Resources.SkillPoints;
        header.FutureOverflow += after.Overflow;
        header.Resources = after.Resources;
        header.Limits = after.Limits;
        header.Motivation = after.Motivation;
        if (after.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
        data.TurnRules.ApplyOrdinaryBond(state, ordinary.Bond, ref random);
    }

    TransitionResult ArriveAtTraining(PioneerState state, int next, PioneerConditionalOutcome.TurnArrival? outcome, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var previous = header.Turn;
        var wasCamp = CampCommands(header);
        header.Turn = next;
        var camp = CampCommands(header);
        if (data.IsWholeRun)
        {
            var hadGrants = !state.GetSection(PioneerStateSection.TemporaryGrants).IsEmpty;
            state.ExpireGrants(next);
            if (hadGrants) state.TurnBoard = null;
            if (autonomous && next != previous)
            {
                SettleScheduledInheritance(state, ref random);
                ApplyFixedTurnEvents(state, next, arriving: true, ref random);
                ApplyOrdinaryStory(state, ref random);
            }
        }
        if (camp != wasCamp) state.TurnBoard = null;
        header.TurnKnown = true;
        header.RawPlayingState = 1;
        header.RawPlayingStateKnown = true;
        header.Stage = PioneerStage.TrainingTurn;
        header.Event = default;
        header.CommandId = 0;
        header.PendingTrainingLevelCommandId = 0;
        var scheduled = autonomous ? data.TurnRules!.ScheduledRace(next) : null;
        if (scheduled is { Target: true, CareerGoalId: > 0 } required && header.Fans < required.RequiredFans)
            return EndCareer(state, required.CareerGoalId);
        var scheduledRace = autonomous && ActiveScheduledRace(state) is not null;
        header.AvailableTrainingCommands = scheduledRace ? 0 : outcome?.AvailableTrainingCommands ?? 31;
        header.AvailableRestCommands = scheduledRace ? 0 : outcome?.AvailableRestCommands ?? (camp ? RestRules.CampAvailable : RestRules.OrdinaryAvailable);
        header.TrainingBoard = scheduledRace ? default : outcome?.Board ?? data.TurnRules!.DrawBoard(state, camp, ref random);
        header.KnownParts &= ~PioneerKnownParts.IslandAssignments;
        header.IslandBoard = default;
        if (outcome?.IslandBoard is { } island)
        {
            header.IslandBoard = island;
            header.KnownParts |= PioneerKnownParts.IslandAssignments;
        }
        else if (autonomous && data.IsWholeRun && !camp && !scheduledRace && header.Turn is >= 6 and <= 72 && header.Tickets > 0)
            data.TurnRules!.DrawIslandBoard(state, ref random);
        header.KnownParts &= ~(PioneerKnownParts.EventIdentity | PioneerKnownParts.TrainingResult);
        header.KnownParts |= PioneerKnownParts.TrainingBoard;
        state.SetSection(PioneerStateSection.PendingStories, []);
        state.SetSection(PioneerStateSection.HintCandidates, []);
        header.ActionOrdinal++;
        return TransitionResult.Settled((int)header.Stage);
    }

    static bool IsCamp(int turn) => turn is >= 37 and <= 40 or >= 61 and <= 64 or >= 73 and <= 78;

    TransitionResult BeginFailure(PioneerState state, int commandId)
    {
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.TrainingFailure outcome)
            return MissingOutcome(state, PioneerStopCode.TrainingOutcomeRequired);
        var before = FailureState(state, TrainingFailureStage.TrainingDecision, null);
        var after = TrainingFailureSettlement.BeginResolvedFailure(before,
            state.GetSection(PioneerStateSection.SupportBonds), commandId, outcome.Event);
        ref var header = ref state.Mutable;
        if ((header.KnownParts & PioneerKnownParts.TrainingExecutionCounts) != 0)
            state.MutableSection(PioneerStateSection.TrainingExecutionCounts)[1] = after.StaminaExecutionCount;
        header.StaminaExecutionCount = after.StaminaExecutionCount;
        header.CommandId = commandId;
        header.TrainingSucceeded = false;
        header.Stage = PioneerStage.FailureChoice;
        header.RawPlayingState = 5;
        header.Event = new(outcome.Event.EventId, outcome.Event.CharacterId, outcome.Event.StoryId, outcome.Event.PlayTiming);
        header.KnownParts |= PioneerKnownParts.EventIdentity | PioneerKnownParts.StaminaExecutionCount | PioneerKnownParts.TrainingResult;
        header.ActionOrdinal++; header.ConditionalOutcomeCount++;
        return TransitionResult.Settled((int)header.Stage);
    }

    TransitionResult FinishFailure(PioneerState state, PioneerRuntimeAction action)
    {
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.FailurePenalty outcome)
            return MissingOutcome(state, PioneerStopCode.FailureBranchRequired);
        var current = state.Header.Event;
        var before = FailureState(state, TrainingFailureStage.FailureChoice, new(current.EventId, current.CharacterId, current.StoryId, current.PlayTiming));
        var (after, consumePositiveThinking) = TrainingFailureSettlement.ResolveChoice(before, action.EventId,
            action.ChoiceNumber, outcome.Branch, state.GetSection(PioneerStateSection.CharacterEffects).Contains(25));
        state.Mutable.Turn = after.Turn; state.Mutable.Motivation = after.Motivation; state.Mutable.Resources = after.Resources;
        if (consumePositiveThinking) state.RemoveCharacterEffect(25);
        state.Mutable.ConditionalOutcomeCount++;
        // The old failure event is consumed. Phase/queue after the turn change are not guessed.
        state.Mutable.Event = default;
        state.Mutable.KnownParts &= ~PioneerKnownParts.EventIdentity;
        state.Mutable.RawPlayingState = 0;
        return Partial(state, PioneerStopCode.NextTurnEventsUnknown, turnKnown: true);
    }

    static TrainingFailureState FailureState(PioneerState state, TrainingFailureStage stage, TrainingFailureEvent? pending)
        => new(state.Header.CardId, state.Header.Turn, state.Header.Motivation, state.Header.Resources,
            (state.Header.KnownParts & PioneerKnownParts.TrainingExecutionCounts) != 0
                ? state.GetSection(PioneerStateSection.TrainingExecutionCounts)[1] : state.Header.StaminaExecutionCount,
            stage, pending);

    TransitionResult ApplyConstruction(PioneerState state)
    {
        ref readonly var header = ref state.Header;
        var pending = state.GetSection(PioneerStateSection.PendingStories);
        if (pending.Length != 1
            || !((pending[0] == 400011107 && header.Turn is >= 5 and <= 60 && header.RawPlayingState == 5)
                || (pending[0] == 400011108 && header.Turn == 41 && header.RawPlayingState == 1))
            || !SettleConstruction(state))
            return Missing(state, PioneerStopCode.UnsupportedComponentInput);
        return Partial(state, PioneerStopCode.ConstructionRemainderUnknown);
    }

    bool ApplyConstructionThresholds(PioneerState state, int before)
    {
        ref var header = ref state.Mutable;
        if (header.Turn is < 5 or > 60) return true;
        foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
            if (before < threshold && threshold <= header.DevelopmentPoints)
            {
                if (!CampCommands(header)) return SettleConstruction(state);
                header.DeferredConstruction = true;
                break;
            }
        return true;
    }

    static bool SettleConstruction(PioneerState state)
    {
        ref var header = ref state.Mutable;
        if (header.DeckId != 3 || (header.KnownParts & PioneerKnownParts.Tickets) == 0 || header.Tickets is not (0 or 1))
            return false;
        var milestone = 0;
        foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
            if (threshold <= header.DevelopmentPoints) milestone = Math.Max(milestone, threshold);
        if (milestone == 0) return false;
        var target = state.MutableSection(PioneerStateSection.FacilityStates);
        var plan = state.GetSection(PioneerStateSection.ConstructionSteps);
        var upgrades = state.Data.Construction!.Data.UpgradesByEdge;
        Span<int> offsets = stackalloc int[6];
        for (var i = 0; i < plan.Length; i += 4)
        {
            var offset = 0;
            while (target[offset] != plan[i + 1]) offset += 2;
            offsets[i / 4] = offset + 1;
            if (target[offset + 1] != plan[i + 2] && target[offset + 1] != plan[i + 3]) return false;
        }
        var cumulative = 0;
        for (var i = 0; i < plan.Length; i += 4)
        {
            cumulative += upgrades[(plan[i + 1], plan[i + 2], plan[i + 3])].Cost;
            if (cumulative > milestone) break;
            target[offsets[i / 4]] = plan[i + 3];
        }
        header.Tickets = 1;
        state.TurnBoard = null;
        return true;
    }

    TransitionResult SubmitConstruction(PioneerState state, PioneerConstructionPlan plan, ref SimulationRandom random)
    {
        var planning = CurrentPlanning(state);
        var submitted = data.Construction!.Submit(planning, plan);
        StoreConstructionPlan(state, plan);
        if (autonomous && data.IsWholeRun)
        {
            return ArriveAtTraining(state, submitted.Turn, null, ref random);
        }
        ref var header = ref state.Mutable;
        header.Turn = submitted.Turn; header.RawPlayingState = submitted.RawPlayingState;
        header.ActionOrdinal++; header.StopCode = PioneerStopCode.PlanningContinuationRequired;
        header.ContinuationFrom = PioneerStage.ConstructionPlanning; header.Stage = PioneerStage.UnsupportedContinuation;
        return TransitionResult.Stopped(true, true, Stop(header.StopCode, header.ContinuationFrom));
    }

    static void StoreConstructionPlan(PioneerState state, PioneerConstructionPlan plan)
    {
        Span<int> steps = stackalloc int[plan.Steps.Length * 4];
        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            steps[index * 4] = step.Priority; steps[index * 4 + 1] = step.FacilityId;
            steps[index * 4 + 2] = step.PreviousStateId; steps[index * 4 + 3] = step.TargetStateId;
        }
        state.SetSection(PioneerStateSection.ConstructionSteps, steps);
    }

    TransitionResult ApplyHint(PioneerState state)
    {
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.HintAttributeGroup outcome)
            return MissingOutcome(state, PioneerStopCode.HintOutcomeRequired);
        var groups = ((PioneerStart.HintAttribute)data.Start).RewardGroups;
        if ((uint)outcome.GroupIndex >= (uint)groups.Length)
            return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
        var after = HintRewardPool.ApplyAttributeReward(state.Header.Resources, state.Header.Limits, groups[outcome.GroupIndex]);
        var gained = checked(state.Header.FutureGainedSkillPoints + after.Resources.SkillPoints - state.Header.Resources.SkillPoints);
        var overflow = state.Header.FutureOverflow + after.Overflow;
        state.Mutable.FutureGainedSkillPoints = gained; state.Mutable.FutureOverflow = overflow;
        state.Mutable.Resources = after.Resources; state.Mutable.ConditionalOutcomeCount++;
        return Partial(state, PioneerStopCode.HintRemainderUnknown);
    }

    TransitionResult ApplyTuckerFriend(PioneerState state, PioneerRuntimeAction action)
    {
        var imported = (PioneerStart.TuckerFriend)data.Start;
        var before = new TuckerFriendState(state.Header.Resources.Vital, state.Header.Limits.Vital, state.Header.Motivation,
            state.Header.TuckerBond, state.Header.OutingUnlocked, state.Header.TuckerStoryStep);
        var after = TuckerEventSettlement.ApplyObservedComponents(imported.Context with { ChoiceNumber = action.ChoiceNumber }, before);
        var skillPoints = checked(state.Header.Resources.SkillPoints + after.SkillPointGain);
        state.Mutable.Resources = state.Header.Resources with { Vital = after.After.Vital, SkillPoints = skillPoints };
        state.Mutable.Limits = state.Header.Limits with { Vital = after.After.MaxVital };
        state.Mutable.Motivation = after.After.Motivation; state.Mutable.TuckerBond = after.After.Bond;
        state.Mutable.OutingUnlocked = after.After.OutingUnlocked; state.Mutable.TuckerStoryStep = after.After.StoryStep;
        state.Mutable.FutureGainedSkillPoints = checked(state.Header.FutureGainedSkillPoints + after.SkillPointGain);
        return Partial(state, PioneerStopCode.TuckerRemainderUnknown);
    }

    TransitionResult ApplyTuckerPoints(PioneerState state, PioneerRuntimeAction action)
    {
        var imported = (PioneerStart.TuckerPoints)data.Start;
        var context = imported.Context with { Turn = state.Header.Turn, RawPlayingState = state.Header.RawPlayingState, ChoiceNumber = action.ChoiceNumber };
        var after = TuckerPointSettlement.ApplyObservedChoiceOne(context, new(state.Header.DevelopmentPoints, state.Header.Resources.SkillPoints));
        state.Mutable.FutureGainedSkillPoints = checked(state.Header.FutureGainedSkillPoints + after.SkillPoints - state.Header.Resources.SkillPoints);
        state.Mutable.DevelopmentPoints = after.DevelopmentPoints;
        state.Mutable.Resources = state.Header.Resources with { SkillPoints = after.SkillPoints };
        return Partial(state, PioneerStopCode.TuckerRemainderUnknown);
    }

    TransitionResult ApplyTuckerSpecialty(PioneerState state, PioneerRuntimeAction action)
    {
        var imported = (PioneerStart.TuckerSpecialty)data.Start;
        var grants = IslandActionRules.GrantTuckerSpecialty(imported.Context with { ChoiceNumber = action.ChoiceNumber }, imported.Deck);
        state.AppendGrants(grants.AsSpan());
        return Partial(state, PioneerStopCode.TuckerRemainderUnknown);
    }

    TransitionResult ApplyProject(PioneerState state)
    {
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.ProjectNpcSelection outcome)
            return MissingOutcome(state, PioneerStopCode.ProjectMembersRequired);
        var imported = (PioneerStart.ProjectMembers)data.Start;
        var packed = state.GetSection(PioneerStateSection.ProjectMembers);
        var members = ImmutableArray.CreateBuilder<PioneerProjectMember>(packed.Length / 2);
        for (var i = 0; i < packed.Length; i += 2) members.Add(new(packed[i], packed[i + 1]));
        var after = PioneerProjectMembers.ApplyObservedSelection(state.Header.Event.StoryId, state.Header.Turn,
            state.Header.CheckpointResult, imported.TrainingCharacterId, imported.Supports, members.MoveToImmutable(), outcome.NpcIds);
        Span<int> newMembers = stackalloc int[34];
        for (var i = 0; i < after.Members.Length; i++) { newMembers[2 * i] = after.Members[i].TargetId; newMembers[2 * i + 1] = after.Members[i].CharacterId; }
        Span<int> npcs = stackalloc int[36];
        for (var i = 0; i < after.AddedNpcs.Length; i++)
        {
            var npc = after.AddedNpcs[i]; var offset = i * 6;
            npcs[offset] = npc.TargetId; npcs[offset + 1] = npc.TrainingPartnerId; npcs[offset + 2] = npc.Bond;
            npcs[offset + 3] = npc.IsAppear ? 1 : 0; npcs[offset + 4] = npc.IsOuting ? 1 : 0; npcs[offset + 5] = npc.StoryStep;
        }
        state.SetSection(PioneerStateSection.ProjectMembers, newMembers[..(after.Members.Length * 2)]);
        state.SetSection(PioneerStateSection.AddedProjectNpcs, npcs[..(after.AddedNpcs.Length * 6)]);
        state.Mutable.ConditionalOutcomeCount++;
        return Partial(state, PioneerStopCode.ProjectRemainderUnknown);
    }

    TransitionResult ApplyCheckpoint(PioneerState state, PioneerRuntimeAction action)
    {
        if (data.Checkpoints is null) return Missing(state, PioneerStopCode.UnsupportedComponentInput);
        var imported = (PioneerStart.Checkpoint)data.Start;
        var context = imported.Context with { ChoiceNumber = action.ChoiceNumber };
        var levels = state.GetSection(PioneerStateSection.TrainingLevels);
        var progress = data.Checkpoints.ApplyHighEvaluationProgress(context,
            new(new(levels[0], levels[1], levels[2], levels[3], levels[4]), state.Header.DirectorBond, state.Header.HasAttention));
        var resources = data.Checkpoints.ApplyResourcesCandidate(context,
            new(state.Header.DevelopmentPoints, state.Header.Resources.Vital, state.Header.Limits.Vital));
        state.Mutable.DevelopmentPoints = resources.After.DevelopmentPoints;
        state.Mutable.Resources = state.Header.Resources with { Vital = resources.After.Vital };
        state.Mutable.DirectorBond = progress.DirectorBond;
        var after = progress.TrainingLevels;
        state.SetSection(PioneerStateSection.TrainingLevels, [after.Speed, after.Stamina, after.Power, after.Guts, after.Wisdom]);
        return Partial(state, PioneerStopCode.CheckpointRemainderUnknown);
    }

    static TransitionResult ConfirmCheckpoint(PioneerState state, PioneerRuntimeAction action)
    {
        var current = state.Header.Event;
        var observed = LateCheckpointConfirmation.ObserveAppliedBoundary(state.Header.Turn, state.Header.RawPlayingState,
            state.Header.CheckpointResult, current.StoryId, current.PlayTiming, 0);
        var after = LateCheckpointConfirmation.Confirm(observed with { Confirmed = state.Header.DisplayConfirmed }, current.StoryId, action.ChoiceNumber);
        state.Mutable.DisplayConfirmed = after.Confirmed;
        return Partial(state, PioneerStopCode.EventContinuationUnknown);
    }

    static TransitionResult Missing(PioneerState state, PioneerStopCode code)
        => TransitionResult.Stopped(false, false, Stop(code, state.Header.Stage));
    TransitionResult MissingOutcome(PioneerState state, PioneerStopCode missing)
        => Missing(state, data.OutcomeAt(state.Header.ActionOrdinal) is null ? missing : PioneerStopCode.OutcomeIdentityMismatch);
    static TransitionResult Partial(PioneerState state, PioneerStopCode code, bool turnKnown = false)
    {
        state.Mutable.ActionOrdinal++;
        state.Mutable.StopCode = code;
        state.Mutable.ContinuationFrom = state.Header.Stage;
        state.Mutable.TurnKnown = turnKnown;
        state.Mutable.RawPlayingStateKnown = false;
        state.Mutable.Stage = PioneerStage.UnsupportedContinuation;
        return TransitionResult.Stopped(true, false, Stop(code, state.Header.ContinuationFrom));
    }
    static SimulationStop Stop(PioneerStopCode code, PioneerStage stage)
        => new(SimulationStopReason.UnknownMechanism, (int)code, (int)stage);
    static SimulationStop Illegal(PioneerStage stage)
        => new(SimulationStopReason.IllegalAction, (int)PioneerStopCode.InvalidAction, (int)stage);

    public static string DescribeStop(PioneerStopCode code) => code switch
    {
        PioneerStopCode.RestAvailabilityRequired => "当前面板缺少休息指令的可用状态。",
        PioneerStopCode.RestOutcomeRequired => "普通休息需要已识别事件身份；完整事件抽样规则尚未接入。",
        PioneerStopCode.RestContinuationRequired => "休息资源已结算；同回合后续事件或下一回合的调度待处理。",
        PioneerStopCode.RestCharacterRequired => "休息事件缺少master映射的育成角色身份。",
        PioneerStopCode.TrainingLevelContinuationRequired => "单项训练等级已增加；同回合后续事件或下一回合的调度待处理。",
        PioneerStopCode.EventRewardOutcomeRequired => "所选事件有多个候选奖励，需要已识别结果身份或该事件的抽样规则。",
        PioneerStopCode.EventRewardRemainderRequired => "事件资源结算范围见状态中的资源掩码；剩余效果和后续调度待处理。",
        PioneerStopCode.TrainingOutcomeRequired => "缺少已识别的训练失败结果；失败抽样及事件生成未实现。",
        PioneerStopCode.FailureBranchRequired => "缺少失败选项的已识别分支；不按选项号码猜测结果。",
        PioneerStopCode.HintOutcomeRequired => "缺少已识别的Hint属性奖励组；奖项抽样未实现。",
        PioneerStopCode.ProjectMembersRequired => "缺少明确NPC身份结果；成员抽样规则未实现。",
        PioneerStopCode.NextTurnEventsUnknown => "失败数值及回合变化已结算，下一回合事件尚未生成。",
        PioneerStopCode.NextTurnPhaseRequired => "行动资源已结算，接续的合宿、评审、规划、比赛或终局阶段尚未生成。",
        PioneerStopCode.TurnEventDispatchRequired => "条件回放指定了待处理事件阶段，当前尚无对应结算。",
        PioneerStopCode.TurnArrivalOutcomeRequired => "条件回放缺少已观察到的回合结果。",
        PioneerStopCode.TrainingLevelHistoryRequired => "缺少成功普通训练次数，无法判断本次是否达到升级里程碑。",
        PioneerStopCode.ConstructionSettlementRequired => "已跨过建设阈值，当前的设施计划或岛训练券状态超出已支持的建设结算范围。",
        PioneerStopCode.IslandContinuationUnknown => "岛训练数值、羁绊、扣券和效果原子已结算，后续事件/阶段/效果生命周期未知。",
        PioneerStopCode.ConstructionRemainderUnknown => "建设设施与券分量已结算，人员分配及完整事件后果未知。",
        PioneerStopCode.HintRemainderUnknown => "Hint属性奖励已结算，其他后果与后续调度未知。",
        PioneerStopCode.TuckerRemainderUnknown => "Tucker已支持分量已结算，其余后果与后续调度未知。",
        PioneerStopCode.ProjectRemainderUnknown => "PJ成员记录已追加，其余事件后果与人员分配未知。",
        PioneerStopCode.CheckpointRemainderUnknown => "评审资源/训练等级/理事长羁绊已结算，其余奖励及调度未知。",
        PioneerStopCode.EventContinuationUnknown => "当前已发奖显示确认已消费，后续事件调度未知。",
        PioneerStopCode.UnsupportedComponentInput => "当前组件输入超出已核验范围；没有替换为默认规则。",
        PioneerStopCode.OutcomeIdentityMismatch => "条件结果没有指向当前输入中的有效结果身份。",
        PioneerStopCode.WrongEventIdentity => "动作故事或协议event_id/character_id与当前决策事件不一致。",
        PioneerStopCode.InvalidAction => "动作类型、命令或选项不符合当前决策。",
        PioneerStopCode.PlanningContinuationRequired => "建设计划已提交，等待训练面板生成。",
        _ => "没有完整Pioneer终局路径。",
    };
}
