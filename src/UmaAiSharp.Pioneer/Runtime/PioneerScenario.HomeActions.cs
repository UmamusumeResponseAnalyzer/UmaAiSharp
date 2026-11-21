using UmaAiSharp.Base.Races;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;
using System.Collections.Immutable;
using UmaAiSharp.Base.Events;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    // Famitsu's 121 outings: karaoke, riverside/small fortune, medium fortune, great fortune.
    const int OrdinaryOutingSamples = 121;
    static readonly (int Recovery, int Motivation, int Count)[] OrdinaryOutings = [(0, 2, 44), (10, 1, 59), (20, 1, 13), (30, 1, 5)];
    const int RaceFanSamples = 3677;
    static readonly (int Percent, int Count)[] RaceFanMultipliers =
        [(100, 29), (102, 158), (104, 411), (106, 432), (108, 297), (109, 1049), (111, 1301)];
    static readonly int MinimumRaceFanPercent = RaceFanMultipliers.Min(row => row.Percent);
    static readonly int MaximumRaceFanPercent = RaceFanMultipliers.Max(row => row.Percent);

    bool HomeActionEnabled(PioneerState state, in PioneerRuntimeAction action)
    {
        if (data.TrainingStart?.HomeActions is not { } home || data.TurnRules is null) return false;
        ref readonly var header = ref state.Header;
        var observed = data.Start is PioneerStart.TrainingTurn && header.Turn == header.SourceTurn;
        if (ActiveScheduledRace(state) is { } scheduled)
            return action.Kind == PioneerActionKind.Race && action.CommandId == scheduled.ProgramId;
        var ordinary = !CampCommands(header);
        switch (action.Kind)
        {
            case PioneerActionKind.Treat:
                return action.CommandId == 801 && (observed ? home.Treatment : ordinary
                    && (header.TrainingFailureIllness || TreatableCondition(state) != 0));
            case PioneerActionKind.Outing when action.CommandId == 301:
                return observed ? home.Outing : ordinary;
            case PioneerActionKind.Outing:
                var position = action.SupportPosition - 1;
                return (observed ? home.FriendOuting : ordinary) && (header.UnlockedFriendMask & 1 << position) != 0
                    && ((header.FriendOutings >> (position * 3)) & 7) < 5;
            case PioneerActionKind.Race:
                foreach (var race in RaceCandidates(state))
                    if (race.ProgramId == action.CommandId) return true;
                return false;
        }
        return false;
    }

    static int TreatableCondition(PioneerState state)
    {
        var condition = 0;
        foreach (var effect in state.GetSection(PioneerStateSection.CharacterEffects))
            if (effect is >= 1 and <= 6 && (condition == 0 || effect < condition)) condition = effect;
        return condition;
    }

    TransitionResult ApplyHomeAction(PioneerState state, PioneerRuntimeAction action, ref SimulationRandom random)
    {
        var conditional = data.OutcomeAt(state.Header.ActionOrdinal);
        if (conditional is not (null or PioneerConditionalOutcome.TurnArrival or PioneerConditionalOutcome.TargetRaceWin))
            return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
        ref var header = ref state.Mutable;
        var paired = autonomous && data.IsWholeRun;
        var source = PioneerArrivalSource.Outing;
        var points = 0;
        switch (action.Kind)
        {
            case PioneerActionKind.Treat:
                // shortcut: 14/14 observed successful treatments. Use one ordinary condition until failure or multi-illness windows are available.
                var condition = TreatableCondition(state);
                if (condition != 0) state.RemoveCharacterEffect(condition);
                header.TrainingFailureIllness = false;
                header.Resources = header.Resources with { Vital = Math.Min(header.Limits.Vital, header.Resources.Vital + 20) };
                source = PioneerArrivalSource.Treatment;
                break;
            case PioneerActionKind.Outing when action.CommandId == 301:
                // shortcut: use the measured destination prior until enough Pioneer outings can identify its own rates.
                var destination = paired ? random.At(header.Turn, PioneerDraw.Outing).NextInt(OrdinaryOutingSamples)
                    : random.NextInt(OrdinaryOutingSamples);
                foreach (var (restore, motivation, count) in OrdinaryOutings)
                {
                    if (destination < count)
                    {
                        RecoverVital(state, restore);
                        ApplyMotivationGain(state, motivation);
                        break;
                    }
                    destination -= count;
                }
                break;
            case PioneerActionKind.Outing:
                points = ApplyFriendOuting(state, action.SupportPosition) ?? 0;
                break;
            case PioneerActionKind.Race:
                var rules = data.TurnRules!;
                var race = ActiveScheduledRace(state) ?? rules.RaceOption(action.CommandId);
                var observedWin = conditional as PioneerConditionalOutcome.TargetRaceWin;
                if (observedWin is not null && (!race.Target
                    || observedWin.PostRaceChoice != (GoldShipRepeatWin(state, race) ? 1 : 0)))
                    return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
                if (autonomous && data.IsWholeRun && header.Turn is 74 or 76 or 78)
                {
                    SettleUraRace(state, header.Turn, ref random);
                    return header.Turn == 78 ? FinishAutonomousEnding(state, ref random)
                        : ArriveAtTraining(state, header.Turn + 1, null, ref random);
                }
                if (race.Target)
                {
                    header.LastTargetRaceMotivation = (byte)header.Motivation;
                    ApplyCareerGoalReward(state, race);
                    if (autonomous && data.IsWholeRun) ApplyCareerRouteAfterWin(state, race.CareerGoalId, ref random);
                }
                else
                {
                    var (value, bonus) = OptionalRaceReward(race.Grade, rules.RaceRewardPercent);
                    var attributeIndex = paired ? random.At(header.Turn, PioneerDraw.RaceAttribute).NextInt(5) : random.NextInt(5);
                    ApplyProgressionReward(state, new(attributeIndex == 0 ? value : 0, attributeIndex == 1 ? value : 0,
                        attributeIndex == 2 ? value : 0, attributeIndex == 3 ? value : 0, attributeIndex == 4 ? value : 0, 0, bonus));
                    header.Resources = header.Resources with { Vital = Math.Max(0, header.Resources.Vital - 15) };
                }
                // 3,677 observed wins. The multiplier pools the measured win-margin and activated-skill bonuses.
                if (observedWin is not null) header.Fans = checked(header.Fans + observedWin.FansGained);
                else
                {
                    var draw = paired ? random.At(header.Turn, PioneerDraw.RaceFans).NextInt(RaceFanSamples) : random.NextInt(RaceFanSamples);
                    foreach (var (percent, count) in RaceFanMultipliers)
                    {
                        if (draw < count)
                        {
                            header.Fans += rules.FirstPlaceFans(race.FirstPlaceFans, percent);
                            break;
                        }
                        draw -= count;
                    }
                }
                if (!race.Target && autonomous && data.IsWholeRun)
                {
                    // shortcut: reporter interview rate by race grade until its trigger is isolated.
                    var (bonusWindows, windows) = ReporterInterviewFrequency(race.Grade);
                    if (random.At(header.Turn, PioneerDraw.RaceBonus).NextScaled(windows) < bonusWindows)
                    {
                        header.Fans += 500;
                        header.ReporterBond = Math.Min(100, header.ReporterBond + 15 + (header.HasAttention ? 2 : 0));
                        ApplyMotivationGain(state, 1);
                    }
                    var fatigue = random.At(header.Turn, PioneerDraw.RaceFatigue);
                    ApplyRaceFatigue(state, NextRaceStreak(header), ref fatigue);
                }
                header.ConsecutiveRaces = NextRaceStreak(header);
                header.LastRaceTurn = header.Turn;
                header.RaceGrade = Math.Max(3, header.RaceGrade);
                state.MutableSection(PioneerStateSection.RaceWins)[header.Turn] = race.ProgramId;
                var counts = state.MutableSection(PioneerStateSection.CareerRaceCounts);
                for (var i = 0; i < counts.Length; i++)
                {
                    var goal = rules.CareerGoals[i];
                    if (header.Turn >= goal.FirstTurn && header.Turn <= goal.Deadline && race.Grade <= goal.MaximumGrade)
                        counts[i]++;
                }
                // A captured target win ends at the character story, before the separate Pioneer point event.
                if (observedWin is null && header.Turn >= data.TrainingStart!.PlanningPeriods[0].PlanningTurn && header.CompletedCheckpoint < 5)
                    points = RaceDevelopmentPoints(state, race.Target);
                source = PioneerArrivalSource.Race;
                break;
        }
        var before = header.DevelopmentPoints;
        header.DevelopmentPoints += points;
        if (points > 0 && !ApplyConstructionThresholds(state, before))
            return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
        header.CommandId = action.CommandId;
        if (conditional is PioneerConditionalOutcome.TargetRaceWin result)
        {
            header.ConditionalOutcomeCount++;
            return ArriveObserved(state, result.Next, ref random);
        }
        return AdvanceTurn(state, source, ref random);
    }

    static (int Interviews, int Races) ReporterInterviewFrequency(int grade) => grade switch
    {
        100 => (264, 895), 200 => (15, 56), 300 => (24, 69), _ => (20, 50),
    };

    static int NextRaceStreak(in PioneerStateHeader state)
        => state.LastRaceTurn == state.Turn - 1 ? state.ConsecutiveRaces + 1 : 1;

    // Matched Pioneer race-out windows with consecutive starts. The fourth group includes longer streaks.
    static (int Light, int Heavy, int Total) RaceFatigueFrequency(int streak) => streak switch
    {
        3 => (78, 0, 129),
        >= 4 => (19, 8, 27),
        _ => (0, 0, 1),
    };

    static void ApplyRaceFatigue(PioneerState state, int streak, ref SimulationRandom random)
    {
        var (light, heavy, total) = RaceFatigueFrequency(streak);
        if (light + heavy == 0) return;
        var branch = random.NextScaled(total);
        if (branch >= light + heavy) return;
        ApplyMotivationGain(state, -1);
        // shortcut: fit condition gains and Lucky Charm consumption together until more complete penalty windows are available.
        var lightPenalty = branch < light;
        if (random.NextScaled(lightPenalty ? 95 : 8) < (lightPenalty ? 15 : 2))
        {
            if (state.GetSection(PioneerStateSection.CharacterEffects).Contains(26)) state.RemoveCharacterEffect(26);
            else state.AddCharacterEffect(3);
        }
        if (lightPenalty) return;
        Span<int> attributes = [0, 1, 2, 3, 4];
        var mask = 0;
        for (var remaining = 5; remaining > 2; remaining--)
        {
            var draw = random.NextScaled(remaining);
            mask |= 1 << attributes[draw];
            attributes[draw] = attributes[remaining - 1];
        }
        ref var header = ref state.Mutable;
        var values = header.Resources;
        header.Resources = values with
        {
            Speed = Loss(values.Speed, 1), Stamina = Loss(values.Stamina, 2), Power = Loss(values.Power, 4),
            Guts = Loss(values.Guts, 8), Wisdom = Loss(values.Wisdom, 16),
        };

        int Loss(int value, int bit) => (mask & bit) == 0 ? value : TrainingFailureSettlement.LoseAttribute(value, 10);
    }

    ImmutableArray<RaceOption> RaceOptions(PioneerState state)
    {
        var current = state.Header;
        var rules = data.TurnRules!;
        if (ActiveScheduledRace(state) is { } scheduled)
            return [scheduled];
        if (data.Start is PioneerStart.TrainingTurn && current.Turn == current.SourceTurn)
            return data.TrainingStart!.HomeActions!.Races;
        return autonomous && data.IsWholeRun ? rules.OptionalRaces(current.Turn, current.RaceGrade) : [];
    }

    IEnumerable<RaceOption> RaceCandidates(PioneerState state)
    {
        var current = state.Header;
        var races = RaceOptions(state);
        var mandatory = races.Any(race => race.Target);
        var collapse = data.Start is PioneerStart.Planning || current.Turn != current.SourceTurn;
        // Keep races whose wins can unlock a later target reward, even when a race in the same grade pays more fans.
        var grades = 0;
        foreach (var race in races)
        {
            if (!race.CanEnter(current.Fans, current.RaceGrade) || mandatory && !race.Target) continue;
            var bit = 1 << (race.Grade / 100);
            if (collapse && (grades & bit) != 0 && (current.CardId / 100, current.Turn, race.ProgramId)
                is not ((1007, 36, 74) or (1008, 35, 73) or (1041, 42, 75))) continue;
            grades |= bit;
            yield return race;
        }
    }

    RaceOption? ActiveScheduledRace(PioneerState state)
    {
        var rules = data.TurnRules;
        var turn = state.Header.Turn;
        return rules?.Schedule.RaceAt(turn, state.Header.ScheduleVariant) is { } race
            && race.CanEnter(state.Header.Fans, state.Header.RaceGrade) ? race : null;
    }

    static (int Attribute, int Skill) OptionalRaceReward(int grade, int percent)
    {
        // Pioneer grade bases from paired post-race windows. Keep the scenario's one-attribute award.
        var (attribute, skill) = grade switch { 100 => (10, 45), 200 or 300 => (8, 35), _ => (5, 30) };
        return (attribute * percent / 100, skill * percent / 100);
    }

    int RaceDevelopmentPoints(PioneerState state, bool target)
    {
        var points = 20 * (100 + state.Header.CheckpointBonusPercent) / 100;
        if (target) return points + 40;
        var facilities = state.GetSection(PioneerStateSection.FacilityStates);
        for (var i = 0; i < facilities.Length; i += 2)
            if (facilities[i] == 106 && facilities[i + 1] == 10630)
            {
                // shortcut: master shows +200%. Use the checkpoint-rounded reward as the base until a Lv3 window fixes the order.
                var percent = data.TurnRules!.Facility(106, facilities[i + 1]).Effects
                    .Single(effect => effect is { Type: 206, Condition: 9, Target: 1 }).Value;
                return points * (100 + percent) / 100;
            }
        return points;
    }

    int? SettleCareerGoals(PioneerState state)
    {
        var rules = data.TurnRules!;
        var counts = state.GetSection(PioneerStateSection.CareerRaceCounts);
        for (var i = 0; i < counts.Length; i++)
        {
            var goal = rules.CareerGoals[i];
            if (state.Header.Turn != goal.Deadline) continue;
            if (state.Header.Fans < goal.FanTarget || counts[i] < goal.RaceCount) return goal.Id;
            ApplyCareerGoalReward(state);
        }
        return null;
    }

    static TransitionResult EndCareer(PioneerState state, int goalId)
    {
        ref var header = ref state.Mutable;
        header.FailedCareerGoal = goalId;
        header.Stage = PioneerStage.Terminal;
        header.ActionOrdinal++;
        return TransitionResult.Settled((int)header.Stage);
    }

    public int? PreviewFriendOutingDevelopmentPoints(PioneerState state, in PioneerRuntimeAction action)
        => action is { Kind: PioneerActionKind.Outing, CommandId: 390 }
            ? ApplyFriendOuting(state.Clone(), action.SupportPosition) : null;

    int? ApplyFriendOuting(PioneerState state, int supportPosition)
    {
        var friend = data.TrainingStart!.HomeActions!.Friends.Single(friend => friend.Position == supportPosition);
        ref var header = ref state.Mutable;
        var position = supportPosition - 1;
        var shift = position * 3;
        var step = (header.FriendOutings >> shift) & 7;
        var card = data.TrainingStart.Deck[position];
        var (recovery, maximum, gains) = FriendOuting(state, card, friend.CharacterId, step);
        var (vital, maxVital) = VitalRules.Apply(header.Resources.Vital, header.Limits.Vital, recovery, maximum);
        header.Resources = header.Resources with { Vital = vital };
        header.Limits = header.Limits with { Vital = maxVital };
        ApplyMotivationGain(state, 1);
        GainFriendBond(state, position, 5);
        header.FriendOutings += 1 << shift;
        if (autonomous && data.IsWholeRun && friend.CharacterId == 9049)
        {
            AppendTuckerGrants(state, data.TurnRules!.OrdinarySupportMask, TemporarySupportGrantSource.TuckerEvent,
                TuckerOutingSpecialty(step), 809049011 + step);
            var (slot, levels) = TuckerOutingHint(card.CardId, step);
            if (levels != 0) GainHint(state, slot, levels);
        }
        ApplyProgressionReward(state, gains);
        if (friend.CharacterId != 9049 || !TuckerGiftAvailable(header)) return null;
        var points = ChooseTuckerGiftPoints(state, header.Resources.Vital);
        ApplyProgressionReward(state, 0, 3);
        return points;
    }

    (int Recovery, int MaxVital, TrainingValues Gains) FriendOuting(PioneerState state, TrainingSupport card, int character, int step)
    {
        // shortcut: non-Tucker friends share the ordinary observed friend recovery prior until their own outings are captured.
        if (character != 9049) return (20, 0, default);
        var scaling = EventEffectScaling.FromSupport(card);
        if (step == 2)
        {
            var attribute = scaling.Apply(1, 8);
            ReadOnlySpan<TrainingValues> choices = [new(0, 0, 0, 0, 0, scaling.Apply(10, 50), 0),
                new(attribute, attribute, attribute, attribute, attribute, 0, scaling.Apply(30, 10))];
            var reward = choices[SelectResourceReward(state, choices)];
            return (reward.Vital, 0, reward with { Vital = 0 });
        }
        var recovery = step switch { 0 or 1 => 20, 3 => 25, _ => 30 };
        var gains = step switch
        {
            0 => new TrainingValues(0, scaling.Apply(2, 35), 0, 0, 0, 0, 0),
            1 => new(scaling.Apply(1, 30), 0, 0, 0, 0, 0, 0),
            3 => new(0, scaling.Apply(2, 15), 0, scaling.Apply(4, 15), 0, 0, 0),
            4 => new(0, 0, scaling.Apply(3, 30), 0, 0, 0, 0),
            _ => default(TrainingValues),
        };
        return (scaling.Apply(10, recovery), step == 0 ? 4 : 0, gains);
    }

    bool TuckerGiftAvailable(in PioneerStateHeader state)
    {
        var periods = data.TrainingStart!.PlanningPeriods;
        return state.Turn >= periods[0].PlanningTurn && state.Turn <= periods[^1].CheckPointTurn;
    }

    static int TuckerOutingSpecialty(int step) => step switch { 2 => 30, 4 => 120, _ => 20 };

    TrainingValues TuckerFirstTrainingReward(int position)
    {
        var gain = EventEffectScaling.FromSupport(data.TrainingStart!.Deck[position]).Apply(1, 5);
        return new(gain, gain, gain, 0, 0, 0, 0);
    }

    const int TuckerFirstTrainingMaxVital = 4;
    const int TuckerFirstTrainingBond = 10;

    (int Slot, int Levels) TuckerOutingHint(int cardId, int step) => (cardId, step) switch
    {
        (30257, 1) => (data.TurnRules!.TuckerOutingWhiteHintSlot, 2),
        (30257, 4) => (data.TurnRules!.TuckerOutingGoldHintSlot, 3),
        _ => default,
    };

    void ApplyTuckerRepeat(PioneerState state, int participants, bool island, ref SimulationRandom random)
    {
        if (data.TrainingStart?.HomeActions is not { } home) return;
        foreach (var friend in home.Friends)
        {
            if (friend.CharacterId != 9049 || (participants & (1 << (friend.Position - 1))) == 0) continue;
            var repeat = random.At(state.Header.Turn, PioneerDraw.TuckerRepeat, friend.Position);
            var rules = data.TurnRules!;
            if (!rules.DrawTuckerRepeat(island, ref repeat)) return;
            ApplyProgressionReward(state, new(0, rules.TuckerRepeatStamina, 0, 0, 0, 0, 0));
            GainFriendBond(state, friend.Position - 1, 5);
            // shortcut: 31/345 fit windows below maximum mood; the hidden branch condition is unresolved.
            if (repeat.NextInt(345) < 31) ApplyMotivationGain(state, 1);
            AppendTuckerGrants(state, rules.OrdinarySupportMask, TemporarySupportGrantSource.TuckerEvent, 20, 809049032);
            return;
        }
    }

    void ApplyFirstFriendTraining(PioneerState state, int first)
    {
        ref var header = ref state.Mutable;
        if (first == 0) return;
        header.UnseenFriendMask &= ~first;
        foreach (var friend in data.TrainingStart!.HomeActions!.Friends)
        {
            if ((first & (1 << (friend.Position - 1))) == 0) continue;
            ApplyMotivationGain(state, 1);
            if (friend.CharacterId != 9049) continue;
            var maximum = Math.Min(VitalRules.Maximum, header.Limits.Vital + TuckerFirstTrainingMaxVital);
            header.Limits = header.Limits with { Vital = maximum };
            GainFriendBond(state, friend.Position - 1, TuckerFirstTrainingBond);
            ApplyProgressionReward(state, TuckerFirstTrainingReward(friend.Position - 1));
        }
    }

    static int FriendUnlockChance(int bond) => bond >= 60 ? 25 : 10;

    bool DrawFriendUnlock(PioneerState state, ref SimulationRandom random)
    {
        if (data.TrainingStart?.HomeActions is not { } home) return false;
        ref var header = ref state.Mutable;
        foreach (var friend in home.Friends)
        {
            var index = friend.Position - 1;
            var mask = 1 << index;
            if (friend.CharacterId != 9049 || ((header.UnseenFriendMask | header.UnlockedFriendMask) & mask) != 0) continue;
            var bond = state.GetSection(PioneerStateSection.SupportBonds)[index];
            // UmaAi-rust sets the opportunity rate. Both choices unlock the outing and apply the shared effects.
            if (random.At(header.Turn, PioneerDraw.FriendUnlock, friend.Position).NextInt(100) >= FriendUnlockChance(bond)) continue;
            var scaling = EventEffectScaling.FromSupport(data.TrainingStart.Deck[index] with { Bond = bond });
            var stat = scaling.Apply(1, 5);
            ReadOnlySpan<TrainingValues> choices = [new(0, 0, 0, 0, 0, scaling.Apply(10, 20), scaling.Apply(30, 10)),
                new(stat, stat, 0, stat, 0, 0, 0)];
            var hintSlot = data.TurnRules!.TuckerUnlockHintSlot;
            var choice = SelectResourceReward(state, choices, otherValues: [0, EventHintValue(state, hintSlot, 5)]);
            header.UnlockedFriendMask |= mask;
            ApplyProgressionReward(state, choices[choice]);
            if (choice == 1) GainHint(state, hintSlot, 5);
            ApplyMotivationGain(state, 1);
            GainFriendBond(state, index, 5);
            state.AddCharacterEffect(26);
            return true;
        }
        return false;
    }

    void ApplyTuckerSideStory(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        // shortcut: one occurrence per run, fitted at 94/8675 at-risk turns; 10/1029 on the holdout.
        if (random.At(header.Turn, PioneerDraw.TuckerSideStory).NextInt(8675) >= 94) return;
        foreach (var card in data.TrainingStart!.Deck)
        {
            if (card.CardId != 30257) continue;
            header.TuckerSideStorySeen = true;
            var scaling = EventEffectScaling.FromSupport(card);
            var gain = scaling.Apply(2, 10);
            ApplyCalendarChoice(state, [new(0, gain, 0, gain, 0, 0, 0),
                new(0, 0, scaling.Apply(3, 5), 0, 0, scaling.Apply(10, 10), 0)]);
            GainFriendBond(state, card.Position - 1, 5);
            return;
        }
    }

    static void GainFriendBond(PioneerState state, int index, int amount)
    {
        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
        bonds[index] = Math.Min(100, bonds[index] + amount + (state.Header.HasCharm ? 2 : 0));
    }

    static void ApplyMotivationGain(PioneerState state, int gain)
    {
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var result = MotivationRules.Apply(state.Header.Motivation, gain, effects.Contains(25), effects.Contains(5));
        state.Mutable.Motivation = result.Value;
        if (result.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
    }
}
