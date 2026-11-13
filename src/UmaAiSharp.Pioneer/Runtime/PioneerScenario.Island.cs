using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    public IEnumerable<RootCandidate<PioneerRuntimeAction>> Candidates(PioneerState state)
    {
        var current = state.Header;
        if (current.Stage == PioneerStage.ConstructionPlanning)
        {
            var id = 0;
            foreach (var plan in data.Construction!.EnumeratePlans(CurrentPlanning(state)))
                yield return new(++id, PioneerRuntimeAction.Submit(plan));
            yield break;
        }
        if (current.Stage is PioneerStage.RestConfirmation or PioneerStage.TrainingLevelConfirmation or PioneerStage.TurnConfirmation)
        {
            yield return new(current.Event.StoryId, PioneerRuntimeAction.Choose(current.Event, current.RequiredChoice));
            yield break;
        }
        if (current.Stage != PioneerStage.TrainingTurn) yield break;
        if (ActiveScheduledRace(state) is { } scheduled)
        {
            yield return new(100000 + scheduled.ProgramId, new(PioneerActionKind.Race, scheduled.ProgramId));
            yield break;
        }
        for (var index = 0; index < 5; index++)
            if ((current.AvailableTrainingCommands & (1 << index)) != 0)
            {
                var command = TrainingCommand(index, current);
                yield return new(command, PioneerRuntimeAction.Train(command));
            }
        if (current.AvailableRestCommands is > 0)
        {
            var command = CampCommands(current) ? RestRules.CampCommand : RestRules.OrdinaryCommand;
            yield return new(command, PioneerRuntimeAction.Rest(command));
        }
        if (IslandAvailable(state)) yield return new(3101, PioneerRuntimeAction.Island());
        if (data.TrainingStart?.HomeActions is not { } home) yield break;
        var treatment = new PioneerRuntimeAction(PioneerActionKind.Treat, 801);
        if (HomeActionEnabled(state, treatment)) yield return new(801, treatment);
        var outing = new PioneerRuntimeAction(PioneerActionKind.Outing, 301);
        if (HomeActionEnabled(state, outing)) yield return new(301, outing);
        foreach (var friend in home.Friends)
        {
            var action = new PioneerRuntimeAction(PioneerActionKind.Outing, 390, SupportPosition: friend.Position);
            if (HomeActionEnabled(state, action)) yield return new(200000 + friend.Position, action);
        }
        foreach (var race in RaceCandidates(state))
            yield return new(100000 + race.ProgramId, new(PioneerActionKind.Race, race.ProgramId));
    }

    static bool IslandAvailable(PioneerState state)
    {
        ref readonly var current = ref state.Header;
        return current.Stage == PioneerStage.TrainingTurn && current.Tickets > 0
            && (current.KnownParts & PioneerKnownParts.IslandAssignments) != 0;
    }

    bool TryPreviewIsland(PioneerState state, out TrainingNumericResult preview)
    {
        if (!IslandAvailable(state))
        {
            preview = default;
            return false;
        }
        var input = IslandInput(state);
        preview = PreviewIsland(state, input);
        return true;
    }

    int IslandDevelopmentPoints(PioneerState state)
    {
        ref readonly var header = ref state.Header;
        var board = header.IslandBoard;
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        var participants = 0;
        var friendship = false;
        for (var i = 0; i < 6; i++)
        {
            var cell = board[i];
            participants += cell.SupportCount + cell.GuestCount;
            var command = TrainingFacilityRules.Command(101 + i);
            if (i < 5)
                foreach (var card in data.TrainingStart!.Deck)
                    if (card.TrainingCommandId == command && bonds[card.Position - 1] >= 80
                        && (cell.SupportMask & (1 << (card.Position - 1))) != 0) friendship = true;
        }
        return IslandActionRules.DevelopmentPointGain(header.CompletedCheckpoint,
            header.HasCheckpointBonus ? header.CheckpointBonusPercent : null, participants, friendship);
    }

    IslandTrainingInput IslandInput(PioneerState state)
    {
        if (data.Start is PioneerStart.IslandTraining imported) return imported.Training;
        ref readonly var header = ref state.Header;
        var start = data.TrainingStart!;
        var facilities = data.TurnRules!.CurrentFacilities(state);
        ref readonly var board = ref header.IslandBoard;
        var count = 0;
        for (var i = 0; i < 6; i++)
        {
            var group = board[i];
            if (group.SupportMask != 0 || group.GuestCount != 0) count++;
        }
        var assignments = ImmutableArray.CreateBuilder<IslandTrainingAssignment>(count);
        for (var i = 0; i < 6; i++)
        {
            var group = board[i];
            if (group.SupportMask == 0 && group.GuestCount == 0) continue;
            assignments.Add(new(101 + i, data.IslandSupports[group.SupportMask], group.GuestCount));
        }
        return new(start.Environment, [], data.TurnRules.IslandBase(facilities), facilities, assignments.MoveToImmutable());
    }

    TrainingNumericResult PreviewIsland(PioneerState state, IslandTrainingInput input)
    {
        ref readonly var header = ref state.Header;
        var environment = input.Environment with
        { Current = header.Resources, Limits = header.Limits, Motivation = header.Motivation, Fans = header.Fans,
            GoldShipBoost = header.Turn == header.SourceTurn ? input.Environment.GoldShipBoost : null };
        return data.Model.Island(input, (data.IslandDeck ?? data.TrainingDeck)!, environment,
            state.GetSection(PioneerStateSection.TrainingLevels), state.GetSection(PioneerStateSection.SupportBonds),
            data.IslandFacilities);
    }

    TransitionResult ApplyIsland(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var wholeRun = data.IsWholeRun && autonomous;
        var observedArrival = data.OutcomeAt(header.ActionOrdinal) is PioneerConditionalOutcome.TurnArrival;
        var input = IslandInput(state);
        var numeric = PreviewIsland(state, input);
        IslandActionRules.ValidateGains(numeric.Common, numeric.Scenario);
        var values = TrainingPreviewSettlement.Apply(header.Resources, header.Limits, numeric.Common, numeric.Scenario);
        var participants = 0;
        var mask = 0;
        var friendship = false;
        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
        foreach (var group in input.Assignments)
        {
            participants = checked(participants + group.Supports.Length + group.GuestCount);
            foreach (var card in group.Supports)
            {
                mask |= 1 << (card.Position - 1);
                friendship |= bonds[card.Position - 1] >= 80 && card.TrainingCommandId == TrainingFacilityRules.Command(group.FacilityId);
            }
        }
        var before = header.DevelopmentPoints;
        header.DevelopmentPoints += data.TrainingStart is not null ? IslandDevelopmentPoints(state)
            : IslandActionRules.DevelopmentPointGain(header.CompletedCheckpoint,
                header.HasCheckpointBonus ? header.CheckpointBonusPercent : null, participants, friendship);
        header.FutureGainedSkillPoints += values.SkillPoints - header.Resources.SkillPoints;
        header.FutureOverflow += numeric.Overflow;
        header.Resources = values;
        header.Tickets--;
        header.CommandId = 3101;
        var charm = state.GetSection(PioneerStateSection.CharacterEffects).Contains(8) ? 2 : 0;
        var ordinaryMask = data.IslandOrdinaryMask;
        if (data.TrainingStart is { } turn)
            foreach (var card in turn.Deck)
                if (card.TrainingCommandId != 0) ordinaryMask |= 1 << (card.Position - 1);
        for (var index = 0; index < bonds.Length; index++)
            bonds[index] = Math.Min(100, bonds[index] + ((ordinaryMask & (1 << index)) != 0 ? 10 : 7) + charm);

        if (!wholeRun && !observedArrival)
        {
            if (data.TrainingStart is null)
                AppendTuckerGrants(state, mask & ordinaryMask, TemporarySupportGrantSource.IslandTraining);
            else
            {
                if (data.IsWholeRun && !ApplyConstructionThresholds(state, before))
                    return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
                GrantTuckerSpecialty(state, mask, TemporarySupportGrantSource.IslandTraining);
            }
            return Partial(state, PioneerStopCode.IslandContinuationUnknown);
        }

        var firstFriends = header.UnseenFriendMask & mask;
        ApplyFirstFriendTraining(state, firstFriends);
        if (data.IsWholeRun && !ApplyConstructionThresholds(state, before))
            return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
        GrantTuckerSpecialty(state, mask, TemporarySupportGrantSource.IslandTraining);
        if (wholeRun) ApplyTuckerRepeat(state, mask & ~firstFriends, island: true, ref random);
        var hintMask = 0;
        for (var i = 0; i < 6; i++) hintMask |= header.IslandBoard[i].HintMask;
        if (autonomous && hintMask != 0)
        {
            var all = 0;
            foreach (var facility in input.Facilities)
                if (facility.Id != 106 && facility.Effects.Any(effect => effect is { Type: 102, Condition: 3 }))
                    all |= header.IslandBoard[facility.Id - 101].HintMask;
            SettleTrainingHint(state, all, all: true, ref random);
            SettleTrainingHint(state, hintMask & ~all, all: false, ref random);
        }
        return AdvanceTurn(state, PioneerArrivalSource.IslandTraining, ref random);
    }

    void GrantTuckerSpecialty(PioneerState state, int participants, TemporarySupportGrantSource source)
    {
        var ordinary = 0;
        var tucker = 0;
        foreach (var card in data.TrainingStart!.Deck)
        {
            if (card.CardId == 30257) tucker = 1 << (card.Position - 1);
            else if (card.TrainingCommandId != 0) ordinary |= 1 << (card.Position - 1);
        }
        if ((participants & tucker) != 0) AppendTuckerGrants(state, participants & ordinary, source);
    }

    static void AppendTuckerGrants(PioneerState state, int mask, TemporarySupportGrantSource source, int value = 60, int story = 0)
    {
        Span<int> positions = stackalloc int[6];
        Span<TemporarySupportGrant> grants = stackalloc TemporarySupportGrant[6];
        var length = TrainingBoard.Positions((byte)mask, positions);
        for (var index = 0; index < length; index++)
            grants[index] = new(state.Header.Turn, source, story == 0 ? null : story, positions[index], 19, value);
        if (length != 0) state.AppendGrants(grants[..length]);
    }

    bool AllCampHints(PioneerState state, int command)
    {
        if (data.TurnRules is not { Hints: not null } rules || command < 3601) return false;
        return rules.CurrentFacilities(state).Any(facility => facility.Effects.Any(effect => effect.Type == 102
            && effect.Condition == 2 && effect.Target == command));
    }

    void SettleTrainingHint(PioneerState state, int hintMask, bool all, ref SimulationRandom random)
    {
        var rules = data.TurnRules!;
        if (rules.Hints is not null) hintMask &= rules.OrdinarySupportMask;
        if (hintMask == 0) return;
        Span<int> hints = stackalloc int[6];
        var count = TrainingBoard.Positions((byte)hintMask, hints);
        if (!all && count > 1)
        {
            hints[0] = hints[data.IsWholeRun
                ? random.At(state.Header.Turn, PioneerDraw.HintSelection).NextScaled(count) : random.NextInt(count)];
            count = 1;
        }
        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
        for (var i = 0; i < count; i++)
        {
            var position = hints[i] - 1;
            bonds[position] = Math.Min(100, bonds[position] + 5);
            if (rules.Hints is not { } pool) continue;
            ref var header = ref state.Mutable;
            var reward = data.IsWholeRun ? random.At(header.Turn, PioneerDraw.HintReward, position) : random;
            var gain = pool.Draw(position, bonds[position], state.MutableSection(PioneerStateSection.HintLevels), ref reward);
            if (!data.IsWholeRun) random = reward;
            if (gain == default) continue;
            var after = HintRewardPool.ApplyAttributeReward(header.Resources, header.Limits, gain);
            header.Resources = after.Resources;
            header.FutureOverflow += after.Overflow;
            header.FutureGainedSkillPoints += gain.SkillPoints;
        }
    }
}
