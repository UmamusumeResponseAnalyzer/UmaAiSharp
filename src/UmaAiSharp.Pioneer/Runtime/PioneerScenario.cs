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

public sealed class PioneerScenario(PioneerRuntimeData data)
    : ISimulationScenario<PioneerState, PioneerDecisionView, PioneerRuntimeAction, TerminalMetrics>
{
    public string RulesIdentity => data.RulesIdentity;
    public long SharedMemoryBytes => data.SharedMemoryBytes;
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
        var key = DecisionKey(current.Turn, eventDecision ? current.Event.EventId : current.CommandId);
        var view = new PioneerDecisionView(current.Stage, current.Turn, current.RawPlayingState, current.KnownParts,
            current.Resources, current.Motivation, current.CommandId, current.Event,
            failure ? 1 : current.Stage == PioneerStage.DeclaredEvent ? data.EventChoiceMinimum : current.RequiredChoice,
            failure ? 2 : current.Stage == PioneerStage.DeclaredEvent ? data.EventChoiceMaximum : current.RequiredChoice,
            current.Stage == PioneerStage.ConstructionPlanning ? ((PioneerStart.Planning)data.Start).State : null,
            current.Stage == PioneerStage.TrainingTurn ? current.AvailableTrainingCommands : 0,
            current.Stage == PioneerStage.DeclaredEvent ? ((PioneerStart.DeclaredEvent)data.Start).Choices : [],
            current.Stage == PioneerStage.TrainingTurn && !data.CampCommands.IsEmpty,
            current.KnownParts.HasFlag(PioneerKnownParts.TrainingBoard) ? current.TrainingBoard : null,
            current.Stage == PioneerStage.TrainingTurn ? current.AvailableRestCommands : null);
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
            PioneerActionKind.IslandTraining => action.CommandId == 0 && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is null,
            PioneerActionKind.ChooseEvent => action.CommandId == 0 && action.EventId > 0 && action.CharacterId >= 0 && action.ChoiceNumber >= 0 && action.Plan is null,
            PioneerActionKind.SubmitConstruction => action.CommandId == 0 && action.EventId == 0 && action.CharacterId == 0 && action.ChoiceNumber == 0 && action.Plan is not null,
            _ => false,
        };
        if (!shapeMatches) return Illegal(current.Stage);
        if (data.OutcomeAt(current.ActionOrdinal) is not null && current.Stage is not
            (PioneerStage.FailureTraining or PioneerStage.FailureChoice or PioneerStage.HintAttributeEvent or PioneerStage.ProjectMemberEvent
                or PioneerStage.TrainingTurn or PioneerStage.DeclaredEvent))
            return Stop(PioneerStopCode.OutcomeIdentityMismatch, current.Stage);
        if (current.Stage == PioneerStage.TrainingTurn)
        {
            if (action.Kind == PioneerActionKind.Rest)
            {
                if (current.AvailableRestCommands is not { } available) return Stop(PioneerStopCode.RestAvailabilityRequired, current.Stage);
                var required = action.CommandId == RestRules.OrdinaryCommand ? RestRules.OrdinaryAvailable : RestRules.CampAvailable;
                return (available & required) != 0 ? null : Illegal(current.Stage);
            }
            if (action.Kind == PioneerActionKind.Train)
                for (var index = 0; index < data.TrainingCommands.Length; index++)
                    if (data.TrainingCommands[index].CommandId == action.CommandId && (current.AvailableTrainingCommands & (1 << index)) != 0) return null;
            return Illegal(current.Stage);
        }
        if (current.Stage == PioneerStage.FailureTraining)
            return action.Kind == PioneerActionKind.Train && action.CommandId == current.CommandId ? null : Illegal(current.Stage);
        if (current.Stage == PioneerStage.ConstructionPlanning)
        {
            if (action.Kind != PioneerActionKind.SubmitConstruction) return Illegal(current.Stage);
            try { data.Construction!.ValidateForSubmission(((PioneerStart.Planning)data.Start).State, action.Plan!); }
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
        try
        {
            return state.Header.Stage switch
            {
                PioneerStage.FailureTraining => BeginFailure(state, state.Header.CommandId),
                PioneerStage.TrainingTurn => action.Kind == PioneerActionKind.Rest ? ApplyRest(state, action.CommandId)
                    : ApplyTraining(state, action.CommandId, ref random),
                PioneerStage.RestConfirmation => ConfirmRest(state),
                PioneerStage.TrainingLevelConfirmation => ConfirmTrainingLevel(state),
                PioneerStage.DeclaredEvent => ApplyDeclaredEvent(state, action),
                PioneerStage.CharacterEnding => FinishCharacterEnding(state),
                PioneerStage.FailureChoice => FinishFailure(state, action),
                PioneerStage.IslandTraining => ApplyIsland(state),
                PioneerStage.ConstructionEvent => ApplyConstruction(state),
                PioneerStage.ConstructionPlanning => SubmitConstruction(state, action.Plan!),
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
        return new(new(values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom),
            new(limits.Speed, limits.Stamina, limits.Power, limits.Guts, limits.Wisdom),
            checked((int)header.FutureGainedSkillPoints),
            (header.KnownParts & PioneerKnownParts.FutureOverflow) != 0 ? header.FutureOverflow : null,
            checked(ScoringCandidates.StatusToPoint(values.Speed) + ScoringCandidates.StatusToPoint(values.Stamina)
                + ScoringCandidates.StatusToPoint(values.Power) + ScoringCandidates.StatusToPoint(values.Guts) + ScoringCandidates.StatusToPoint(values.Wisdom)));
    }

    TransitionResult FinishCharacterEnding(PioneerState state)
    {
        var after = data.CharacterEndingReward!.Apply(state.Header.Resources, state.Header.Limits, state.Header.Motivation, false, false);
        var gained = checked(state.Header.FutureGainedSkillPoints + after.Resources.SkillPoints - state.Header.Resources.SkillPoints);
        var overflow = state.Header.FutureOverflow + after.Overflow;
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
        if (command.CommandId != commandId) throw new ArgumentException("训练指令与当前面板模式不一致。", nameof(commandId));
        var environment = ((PioneerStart.TrainingTurn)data.Start).Environment with
        { Current = state.Header.Resources, Limits = state.Header.Limits, Fans = state.Header.Fans, Motivation = state.Header.Motivation };
        var camp = data.CampCommands.IsEmpty ? (CompiledCampTraining?)null : data.CampCommands[index];
        var placement = state.Header.TrainingBoard[index];
        Span<int> positions = stackalloc int[6];
        var length = TrainingBoard.Positions(placement.SupportMask, positions);
        return TrainingNumericKernel.Preview(environment, commandId, camp.HasValue ? 5 : state.GetSection(PioneerStateSection.TrainingLevels)[index],
            placement.GuestCount, command.EvaluationTrainingPercent, command.FailureCoefficient, data.TrainingFailureBias,
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
        // The forecast model samples the displayed integer rate; forced replay results consume no draw.
        var success = conditional is PioneerConditionalOutcome.TrainingResult result ? result.Success
            : preview.FailureRate == 0 || preview.FailureRate < 100 && nextRandom.NextInt(100) >= preview.FailureRate;
        var construction = false;
        ref var header = ref state.Mutable;
        if (success)
        {
            var gains = preview.Gains;
            var common = gains.Common with { Vital = Math.Max(-header.Resources.Vital, gains.Common.Vital) };
            var values = TrainingPreviewSettlement.Apply(header.Resources, header.Limits, common, gains.Scenario);
            var turn = (PioneerStart.TrainingTurn)data.Start;
            var points = checked(header.DevelopmentPoints + (header.Turn < turn.PlanningPeriods[0].PlanningTurn || header.CompletedCheckpoint == 5 ? 0
                : PioneerPointGain.OrdinaryTraining(placement.SupportCount + placement.GuestCount, header.CheckpointBonusPercent, preview.Friendship)));
            if (turn.Development is not null && turn.Camp is null && header.Turn is >= 5 and <= 60)
                construction = ConstructionTriggerRules.CrossedThreshold(state.GetSection(PioneerStateSection.Thresholds),
                    header.DevelopmentPoints, points) != 0;
            var gained = checked(header.FutureGainedSkillPoints + values.SkillPoints - header.Resources.SkillPoints);
            var overflow = header.FutureOverflow + gains.Overflow;
            var successfulCount = (int?)null;
            if (turn.Camp is null && (header.KnownParts & PioneerKnownParts.SuccessfulOrdinaryTrainingCounts) != 0)
            {
                var counts = state.MutableSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts);
                counts[index] = checked(counts[index] + 1);
                successfulCount = counts[index];
            }
            header.PendingTrainingLevelEventId = turn.Camp is null
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
            Span<int> hints = stackalloc int[6];
            var hintCount = TrainingBoard.Positions(placement.HintMask, hints);
            state.SetSection(PioneerStateSection.HintCandidates, hints[..hintCount]);
        }
        random = nextRandom;
        state.MutableSection(PioneerStateSection.TrainingExecutionCounts)[index] = count;
        header.CommandId = commandId; header.TrainingSucceeded = success; header.KnownParts |= PioneerKnownParts.TrainingResult;
        if (conditional is not null) header.ConditionalOutcomeCount++;
        if (construction)
        {
            state.SetSection(PioneerStateSection.PendingStories, [400011107]);
            header.Event = new(211067, 0, 400011107, 6);
            header.KnownParts |= PioneerKnownParts.EventIdentity;
            header.Stage = PioneerStage.ConstructionEvent; header.RawPlayingState = 5;
            header.RequiredChoice = 0; header.ActionOrdinal++;
            return TransitionResult.Settled((int)header.Stage);
        }
        return Partial(state, success ? PioneerStopCode.TrainingEventsRequired : PioneerStopCode.TrainingFailureEventRequired, turnKnown: true);
    }

    static TransitionResult ConfirmTrainingLevel(PioneerState state)
    {
        var index = TrainingLevelRules.TargetIndex(state.Header.Event.EventId);
        state.MutableSection(PioneerStateSection.TrainingLevels)[index]++;
        state.Mutable.PendingTrainingLevelEventId = 0;
        state.SetSection(PioneerStateSection.PendingStories, []);
        state.Mutable.Event = default;
        state.Mutable.KnownParts &= ~PioneerKnownParts.EventIdentity;
        return Partial(state, PioneerStopCode.TrainingLevelContinuationRequired);
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

    TransitionResult ApplyRest(PioneerState state, int commandId)
    {
        if (commandId == RestRules.CampCommand)
        {
            if (data.OutcomeAt(state.Header.ActionOrdinal) is not null)
                return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
            RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
            var vital = VitalRules.Apply(state.Header.Resources.Vital, state.Header.Limits.Vital, 40, 0).Vital;
            ref var camp = ref state.Mutable;
            camp.Resources = camp.Resources with { Vital = vital };
            camp.Motivation = Math.Min(5, camp.Motivation + 1);
            camp.CommandId = commandId;
            return Partial(state, PioneerStopCode.RestContinuationRequired);
        }
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not PioneerConditionalOutcome.RestEvent outcome)
            return MissingOutcome(state, PioneerStopCode.RestOutcomeRequired);
        if (data.TrainingCharacterId is not { } characterId) return Missing(state, PioneerStopCode.RestCharacterRequired);
        _ = RestRules.Recovery(outcome.Event, characterId);
        RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
        state.SetSection(PioneerStateSection.PendingStories, [outcome.Event.StoryId]);
        ref var header = ref state.Mutable;
        header.CommandId = commandId;
        header.Event = outcome.Event;
        header.Stage = PioneerStage.RestConfirmation;
        header.RawPlayingState = 5;
        header.KnownParts |= PioneerKnownParts.EventIdentity;
        header.ActionOrdinal++; header.ConditionalOutcomeCount++;
        return TransitionResult.Settled((int)header.Stage);
    }

    static TransitionResult ConfirmRest(PioneerState state)
    {
        var gain = RestRules.Recovery(state.Header.Event, state.Data.TrainingCharacterId!.Value);
        RestRules.ValidateEffects(state.GetSection(PioneerStateSection.CharacterEffects));
        var vital = VitalRules.Apply(state.Header.Resources.Vital, state.Header.Limits.Vital, gain, 0).Vital;
        state.Mutable.Resources = state.Header.Resources with { Vital = vital };
        state.Mutable.Event = default;
        state.Mutable.KnownParts &= ~PioneerKnownParts.EventIdentity;
        state.SetSection(PioneerStateSection.PendingStories, []);
        return Partial(state, PioneerStopCode.RestContinuationRequired);
    }

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

    TransitionResult ApplyIsland(PioneerState state)
    {
        var imported = (PioneerStart.IslandTraining)data.Start;
        ref var header = ref state.Mutable;
        var environment = imported.Training.Environment with
        { Current = header.Resources, Limits = header.Limits, Motivation = header.Motivation, Fans = header.Fans };
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        var numeric = TrainingNumericKernel.Island(imported.Training, data.IslandDeck!, environment,
            state.GetSection(PioneerStateSection.TrainingLevels), bonds, data.IslandFacilities!);
        IslandActionRules.ValidateGains(numeric.Common, numeric.Scenario);
        var values = TrainingPreviewSettlement.Apply(header.Resources, header.Limits, numeric.Common, numeric.Scenario);
        var participants = 0;
        var friendship = false;
        foreach (var group in imported.Training.Assignments)
        {
            participants = checked(participants + group.Supports.Length + group.GuestCount);
            foreach (var card in group.Supports)
                friendship |= bonds[card.Position - 1] >= 80 && card.TrainingCommandId == TrainingFacilityRules.Command(group.FacilityId);
        }
        var pointGain = IslandActionRules.DevelopmentPointGain(header.CompletedCheckpoint,
            header.HasCheckpointBonus ? header.CheckpointBonusPercent : null, participants, friendship);
        var points = checked(header.DevelopmentPoints + pointGain);
        var gained = checked(header.FutureGainedSkillPoints + values.SkillPoints - header.Resources.SkillPoints);
        var overflow = header.FutureOverflow + numeric.Overflow;
        var charm = state.GetSection(PioneerStateSection.CharacterEffects).Contains(8);
        var locations = state.GetSection(PioneerStateSection.IslandSupportFacilities);
        Span<int> updatedBonds = stackalloc int[6];
        Span<int> grants = stackalloc int[30];
        var grantLength = 0;
        // Emit effects in position order, matching the audit component's ordering.
        for (var position = 1; position <= 6; position++)
        {
            var ordinary = (data.IslandOrdinaryMask & (1 << (position - 1))) != 0;
            updatedBonds[position - 1] = Math.Min(100, bonds[position - 1] + (ordinary ? 10 : 7) + (charm ? 2 : 0));
            if (!ordinary || locations[position - 1] == 0) continue;
            grants[grantLength++] = header.Turn;
            grants[grantLength++] = (int)TemporarySupportGrantSource.IslandTraining;
            grants[grantLength++] = 0;
            grants[grantLength++] = position;
            grants[grantLength++] = 19;
            grants[grantLength++] = 60;
        }
        header.Resources = values; header.DevelopmentPoints = points; header.Tickets--;
        header.FutureGainedSkillPoints = gained; header.FutureOverflow = overflow;
        updatedBonds.CopyTo(state.MutableSection(PioneerStateSection.SupportBonds));
        state.AppendGrants(grants[..grantLength]);
        return Partial(state, PioneerStopCode.IslandContinuationUnknown);
    }

    TransitionResult ApplyConstruction(PioneerState state)
    {
        ref var header = ref state.Mutable;
        var pending = state.GetSection(PioneerStateSection.PendingStories);
        if (header.DeckId != 3 || pending.Length != 1
            || !((pending[0] == 400011107 && header.Turn is >= 5 and <= 60 && header.RawPlayingState == 5)
                || (pending[0] == 400011108 && header.Turn == 41 && header.RawPlayingState == 1))
            || (header.KnownParts & PioneerKnownParts.Tickets) == 0 || header.Tickets is not (0 or 1))
            return Missing(state, PioneerStopCode.UnsupportedComponentInput);
        var milestone = 0;
        foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
            if (threshold <= header.DevelopmentPoints) milestone = Math.Max(milestone, threshold);
        if (milestone == 0) return Missing(state, PioneerStopCode.UnsupportedComponentInput);
        var target = state.MutableSection(PioneerStateSection.FacilityStates);
        foreach (var step in data.ConstructionSteps)
            if (target[step.StateOffset] != step.PreviousStateId && target[step.StateOffset] != step.TargetStateId)
                return Missing(state, PioneerStopCode.UnsupportedComponentInput);
        foreach (var step in data.ConstructionSteps)
            if (step.CumulativeCost <= milestone) target[step.StateOffset] = step.TargetStateId;
        header.Tickets = 1;
        return Partial(state, PioneerStopCode.ConstructionRemainderUnknown);
    }

    TransitionResult SubmitConstruction(PioneerState state, PioneerConstructionPlan plan)
    {
        var submitted = data.Construction!.Submit(((PioneerStart.Planning)data.Start).State, plan);
        Span<int> steps = stackalloc int[plan.Steps.Length * 4];
        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            steps[index * 4] = step.Priority; steps[index * 4 + 1] = step.FacilityId;
            steps[index * 4 + 2] = step.PreviousStateId; steps[index * 4 + 3] = step.TargetStateId;
        }
        state.SetSection(PioneerStateSection.ConstructionSteps, steps);
        ref var header = ref state.Mutable;
        header.Turn = submitted.Turn; header.RawPlayingState = submitted.RawPlayingState;
        header.ActionOrdinal++; header.StopCode = PioneerStopCode.PlanningContinuationRequired;
        header.ContinuationFrom = PioneerStage.ConstructionPlanning; header.Stage = PioneerStage.UnsupportedContinuation;
        return TransitionResult.Stopped(true, true, Stop(header.StopCode, header.ContinuationFrom));
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
        Span<int> packed = stackalloc int[30];
        var length = PackGrants(grants, packed);
        state.AppendGrants(packed[..length]);
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
            state.Header.CheckpointResult, current.EventId, current.StoryId, current.PlayTiming, 0);
        var after = LateCheckpointConfirmation.Confirm(observed with { Confirmed = state.Header.DisplayConfirmed }, action.EventId, current.StoryId, action.ChoiceNumber);
        state.Mutable.DisplayConfirmed = after.Confirmed;
        return Partial(state, PioneerStopCode.EventContinuationUnknown);
    }

    static int PackGrants(ImmutableArray<TemporarySupportGrant> grants, Span<int> packed)
    {
        for (var i = 0; i < grants.Length; i++)
        {
            var grant = grants[i]; var offset = i * 6;
            packed[offset] = grant.SourceTurn; packed[offset + 1] = (int)grant.Source;
            packed[offset + 2] = grant.StoryId ?? 0; packed[offset + 3] = grant.Position;
            packed[offset + 4] = grant.EffectType; packed[offset + 5] = grant.Value;
        }
        return grants.Length * 6;
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
        PioneerStopCode.IslandContinuationUnknown => "岛训练数值、羁绊、扣券和效果原子已结算，后续事件/阶段/效果生命周期未知。",
        PioneerStopCode.ConstructionRemainderUnknown => "建设设施与券分量已结算，人员分配及完整事件后果未知。",
        PioneerStopCode.HintRemainderUnknown => "Hint属性奖励已结算，其他后果与后续调度未知。",
        PioneerStopCode.TuckerRemainderUnknown => "Tucker已支持分量已结算，其余后果与后续调度未知。",
        PioneerStopCode.ProjectRemainderUnknown => "PJ成员记录已追加，其余事件后果与人员分配未知。",
        PioneerStopCode.CheckpointRemainderUnknown => "评审资源/训练等级/理事长羁绊已结算，其余奖励及调度未知。",
        PioneerStopCode.EventContinuationUnknown => "当前已发奖显示确认已消费，后续事件调度未知。",
        PioneerStopCode.UnsupportedComponentInput => "当前组件输入超出已核验范围；没有替换为默认规则。",
        PioneerStopCode.OutcomeIdentityMismatch => "条件结果没有指向当前输入中的有效结果身份。",
        PioneerStopCode.WrongEventIdentity => "动作event_id/character_id与当前决策事件不一致。",
        PioneerStopCode.InvalidAction => "动作类型、命令或选项不符合当前决策。",
        PioneerStopCode.PlanningContinuationRequired => "建设计划已提交，等待训练面板生成。",
        _ => "没有完整Pioneer终局路径。",
    };
}
