using UmaAiSharp.Core;
using UmaAiSharp.Base.Events;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    static int ObservedTurn(in PioneerStateHeader current, PioneerTurnObservation frame)
        => frame.Turn ?? (current.Turn + (frame.Arrival is PioneerArrival.Ready or PioneerArrival.NextTurnEvent ? 1 : 0));

    bool MatchesObservedTurn(in PioneerStateHeader current, PioneerTurnObservation frame)
    {
        var turn = ObservedTurn(current, frame);
        if (turn is < 1 or > 78 || turn < current.Turn || turn > current.Turn + 1) return false;
        if (frame.Arrival == PioneerArrival.NextTurnEvent) return turn == current.Turn + 1;
        if (frame.Arrival is PioneerArrival.SameTurnEvent or PioneerArrival.TrainingLevel) return turn == current.Turn;
        if (frame.Arrival == PioneerArrival.TurnConfirmation)
            return turn == current.Turn && turn == 13 && current.Stage != PioneerStage.TurnConfirmation
                && frame.ChoiceCount is null or <= 1
                && (frame.Event is not { } confirmation || confirmation.StoryId == FirstYearConfirmation.StoryId
                    && confirmation.CharacterId == FirstYearConfirmation.CharacterId && confirmation.PlayTiming == FirstYearConfirmation.PlayTiming);
        return frame.AvailableRestCommands is null or 0 || frame.AvailableRestCommands
            == ((data.IsWholeRun ? IsCamp(turn) : !data.CampCommands.IsEmpty) ? RestRules.CampAvailable : RestRules.OrdinaryAvailable);
    }

    TransitionResult ArriveObserved(PioneerState state, PioneerTurnObservation frame, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var turn = ObservedTurn(header, frame);
        // A training level may be queued behind earlier events on this turn.
        if (frame.Event is { StoryId: TrainingLevelRules.StoryId } level)
        {
            var command = TrainingLevelRules.ObservedCommand(level);
            var index = TrainingFacilityRules.PrimaryTarget(command) - 1;
            if (turn != header.Turn || frame.ChoiceCount is > 1
                || header.PendingTrainingLevelCommandId is { } predicted && predicted != command
                || state.GetSection(PioneerStateSection.TrainingLevels)[index] == 5
                || !header.TrainingSucceeded || header.CommandId != command)
                return Partial(state, PioneerStopCode.OutcomeIdentityMismatch, turnKnown: true);
            header.PendingTrainingLevelCommandId = command;
            return RevealObservedEvent(state, level, PioneerStage.TrainingLevelConfirmation, frame.RawPlayingState ?? 5);
        }
        if (header.PendingTrainingLevelCommandId is > 0 && (turn > header.Turn || frame.Arrival == PioneerArrival.Ready))
            return Partial(state, PioneerStopCode.OutcomeIdentityMismatch, turnKnown: true);
        if (frame.Arrival == PioneerArrival.Ready)
        {
            if (!data.IsWholeRun && (turn >= 78 || IsCamp(turn) != IsCamp(header.Turn)))
                return Partial(state, PioneerStopCode.NextTurnPhaseRequired);
            return ArriveAtTraining(state, turn, frame, ref random);
        }
        if (turn != header.Turn)
        {
            header.Turn = turn;
            state.ExpireGrants(turn);
            state.TurnBoard = null;
        }
        header.Event = default;
        header.ObservedEventChoiceCount = 0;
        header.KnownParts &= ~(PioneerKnownParts.EventIdentity | PioneerKnownParts.TrainingBoard | PioneerKnownParts.IslandAssignments);
        header.AvailableTrainingCommands = 0;
        header.AvailableRestCommands = null;
        state.SetSection(PioneerStateSection.PendingStories, []);
        if (frame.Arrival == PioneerArrival.TurnConfirmation)
            return RevealObservedEvent(state, frame.Event ?? FirstYearConfirmation, PioneerStage.TurnConfirmation, frame.RawPlayingState ?? 5);
        if (frame.Event is { } pending && frame.ChoiceCount is { } choiceCount)
        {
            header.ObservedEventChoiceCount = choiceCount;
            return RevealObservedEvent(state, pending, PioneerStage.ObservedEvent, frame.RawPlayingState!.Value);
        }

        if (frame.Event is { } revealed)
        {
            header.Event = revealed;
            header.KnownParts |= PioneerKnownParts.EventIdentity;
            state.SetSection(PioneerStateSection.PendingStories, [revealed.StoryId]);
        }
        var stop = Partial(state, frame.Arrival == PioneerArrival.OtherPhase ? PioneerStopCode.NextTurnPhaseRequired
            : PioneerStopCode.TurnEventDispatchRequired, turnKnown: frame.Turn.HasValue || frame.Arrival != PioneerArrival.OtherPhase);
        if (frame.RawPlayingState is { } phase)
        {
            header.RawPlayingState = phase;
            header.RawPlayingStateKnown = true;
        }
        if (frame is { Arrival: PioneerArrival.OtherPhase, Turn: not null, RawPlayingState: not null })
            return TransitionResult.Stopped(true, true, stop.Stop!.Value);
        return stop;
    }

    static TransitionResult RevealObservedEvent(PioneerState state, PioneerEvent current, PioneerStage stage, int phase)
    {
        ref var header = ref state.Mutable;
        state.SetSection(PioneerStateSection.PendingStories, [current.StoryId]);
        header.Event = current;
        header.Stage = stage;
        header.RawPlayingState = phase;
        header.RawPlayingStateKnown = header.TurnKnown = true;
        header.RequiredChoice = 0;
        if (stage != PioneerStage.ObservedEvent) header.ObservedEventChoiceCount = 0;
        header.KnownParts = (header.KnownParts | PioneerKnownParts.EventIdentity)
            & ~(PioneerKnownParts.TrainingBoard | PioneerKnownParts.IslandAssignments);
        header.AvailableTrainingCommands = 0;
        header.AvailableRestCommands = null;
        header.ActionOrdinal++;
        return TransitionResult.Settled((int)stage);
    }

    TransitionResult ApplyObservedEvent(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        if (data.OutcomeAt(header.ActionOrdinal) is not PioneerConditionalOutcome.ObservedEventResult outcome)
            return MissingOutcome(state, PioneerStopCode.ObservedEventResultRequired);
        if (outcome.Changes is { } changes)
        {
            if (!ApplyObservedEventChanges(state, changes))
                return Missing(state, PioneerStopCode.ConstructionSettlementRequired);
        }
        else
        {
            var training = TrainingBaseTable.CommandIndex(header.CommandId);
            if (outcome.ChoiceNumber != 0 || !header.KnownParts.HasFlag(PioneerKnownParts.TrainingResult)
                || !header.TrainingSucceeded || training < 0 || !header.TrainingBoard[training].Reporter
                || header.Event.StoryId != 400000029 + NpcTrainingReward(header.ReporterBond))
                return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
            ApplyReporterTrainingEvent(state, training);
        }
        RecordCurrentStory(state, outcome.ChoiceNumber);
        header.ConditionalOutcomeCount++;
        return ArriveObserved(state, outcome.Next, ref random);
    }

    bool ApplyObservedEventChanges(PioneerState state, PioneerObservedEventChanges changes)
    {
        ref var header = ref state.Mutable;
        var values = header.Resources;
        var limits = header.Limits;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var resources = changes.Resources.GetValueOrDefault();
        var caps = changes.Limits.GetValueOrDefault();
        checked
        {
            values = new(values.Speed + resources.Speed, values.Stamina + resources.Stamina, values.Power + resources.Power,
                values.Guts + resources.Guts, values.Wisdom + resources.Wisdom, values.Vital + resources.Vital,
                values.SkillPoints + resources.SkillPoints);
            limits = new(limits.Speed + caps.Speed, limits.Stamina + caps.Stamina, limits.Power + caps.Power,
                limits.Guts + caps.Guts, limits.Wisdom + caps.Wisdom, limits.Vital + caps.Vital);
        }
        var motivation = checked(header.Motivation + changes.Motivation);
        var fans = checked(header.Fans + changes.Fans);
        var development = checked(header.DevelopmentPoints + changes.DevelopmentPoints);
        DeclaredEventRewards.Validate(values, limits, motivation);
        foreach (var bond in changes.Bonds ?? [])
            if (ObservedBond(state, bond.TargetId) + bond.Change is < 0 or > 100)
                throw new InvalidDataException($"事件 {header.Event.StoryId} 的 target_id={bond.TargetId} 羁绊变化越界。");
        var effectMismatch = false;
        foreach (var effect in changes.RemovedEffects ?? []) effectMismatch |= !effects.Contains(effect);
        foreach (var effect in changes.AddedEffects ?? []) effectMismatch |= effects.Contains(effect);
        foreach (var grant in changes.Grants ?? [])
            if (grant.SourceTurn != header.Turn || grant.StoryId != header.Event.StoryId)
                throw new InvalidDataException($"事件 {header.Event.StoryId} 的临时效果来源与当前事件不一致。");
        foreach (var friend in changes.UnlockedFriends ?? [])
            if (data.TrainingStart!.Deck[friend - 1].TrainingCommandId != 0)
                throw new InvalidDataException($"事件 {header.Event.StoryId} 的卡位 {friend} 缺少友人外出资格。");
        if (fans < 0 || development < 0 || effectMismatch)
            throw new InvalidDataException($"事件 {header.Event.StoryId} 的粉丝、发展 Pt 或角色状态变化与前态不符。");

        if (header.Event.StoryId is 400011107 or 400011108 && !TrySettleConstructionEvent(state))
            return false;
        header.Resources = values;
        header.Limits = limits;
        header.Motivation = motivation;
        header.Fans = fans;
        header.DevelopmentPoints = development;
        header.FutureGainedSkillPoints += resources.SkillPoints;
        // A displayed delta cannot establish the hidden overflow on a capped attribute.
        if (values.Speed == limits.Speed || values.Stamina == limits.Stamina || values.Power == limits.Power
            || values.Guts == limits.Guts || values.Wisdom == limits.Wisdom)
            header.KnownParts &= ~PioneerKnownParts.FutureOverflow;
        foreach (var bond in changes.Bonds ?? []) ObservedBond(state, bond.TargetId) += bond.Change;
        foreach (var hint in changes.Hints ?? [])
            state.MutableSection(PioneerStateSection.HintLevels)[data.TurnRules!.Hints!.FindSlot(hint.Skill)] = hint.Level;
        foreach (var effect in changes.RemovedEffects ?? []) state.RemoveCharacterEffect(effect);
        foreach (var effect in changes.AddedEffects ?? []) state.AddCharacterEffect(effect);
        effects = state.GetSection(PioneerStateSection.CharacterEffects);
        header.HasCharm = effects.Contains(8);
        header.HasAttention = effects.Contains(9);
        foreach (var friend in changes.UnlockedFriends ?? []) header.UnlockedFriendMask |= 1 << (friend - 1);
        if (changes.Grants is { IsEmpty: false } grants)
        {
            state.AppendGrants(grants.AsSpan());
            state.TurnBoard = null;
        }
        return true;
    }

    static ref int ObservedBond(PioneerState state, int target)
    {
        if (target == 102) return ref state.Mutable.DirectorBond;
        if (target == 103) return ref state.Mutable.ReporterBond;
        return ref state.MutableSection(PioneerStateSection.SupportBonds)[target - 1];
    }
}
