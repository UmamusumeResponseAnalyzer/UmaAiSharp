using UmaAiSharp.Core;
using System.Collections.Immutable;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
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
        if (data.OutcomeAt(state.Header.ActionOrdinal) is not (null or PioneerConditionalOutcome.TurnArrival))
            return Missing(state, PioneerStopCode.OutcomeIdentityMismatch);
        ref var header = ref state.Mutable;
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
                // shortcut: UmaAi's ordinary outing prior, until a Pioneer outing sample covers command 301.
                var recover = random.NextInt(2) == 0;
                ApplyMotivationGain(state, recover ? 1 : 2);
                if (recover) header.Resources = header.Resources with { Vital = Math.Min(header.Limits.Vital, header.Resources.Vital + 10) };
                break;
            case PioneerActionKind.Outing:
                var friend = data.TrainingStart!.HomeActions!.Friends.Single(friend => friend.Position == action.SupportPosition);
                var position = friend.Position - 1;
                var shift = position * 3;
                var step = (header.FriendOutings >> shift) & 7;
                var (recovery, maximum, gains) = FriendOuting(data.TrainingStart.Deck[position], friend.CharacterId, step);
                var (vital, maxVital) = VitalRules.Apply(header.Resources.Vital, header.Limits.Vital, recovery, maximum);
                header.Resources = header.Resources with { Vital = vital };
                header.Limits = header.Limits with { Vital = maxVital };
                ApplyMotivationGain(state, 1);
                var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
                bonds[position] = Math.Min(100, bonds[position] + 5 + (state.GetSection(PioneerStateSection.CharacterEffects).Contains(8) ? 2 : 0));
                header.FriendOutings += 1 << shift;
                if (autonomous && data.IsWholeRun && friend.CharacterId == 9049)
                    AppendTuckerGrants(state, data.TurnRules!.OrdinarySupportMask, TemporarySupportGrantSource.TuckerEvent,
                        TuckerOutingSpecialty(step), 809049011 + step);
                if (friend.CharacterId == 9049 && header.Turn is >= 8 and <= 60)
                {
                    points = 80;
                    gains = gains with { SkillPoints = 3 };
                }
                ApplyProgressionReward(state, gains);
                break;
            case PioneerActionKind.Race:
                var rules = data.TurnRules!;
                var race = rules.RaceOption(header.Turn, action.CommandId);
                if (autonomous && data.IsWholeRun && header.Turn is 74 or 76 or 78)
                {
                    SettleUraRace(state, header.Turn, ref random);
                    return header.Turn == 78 ? FinishAutonomousEnding(state, ref random)
                        : ArriveAtTraining(state, header.Turn + 1, null, ref random);
                }
                if (race.Target)
                {
                    ApplyProgressionReward(state, 3 * rules.RaceRewardPercent / 100, 45 * rules.RaceRewardPercent / 100);
                    if (autonomous && data.IsWholeRun) header.DirectorBond = Math.Min(100, header.DirectorBond + 4);
                }
                else
                {
                    var (value, bonus) = OptionalRaceReward(race.Grade, rules.RaceRewardPercent);
                    var attributeIndex = random.NextInt(5);
                    ApplyProgressionReward(state, new(attributeIndex == 0 ? value : 0, attributeIndex == 1 ? value : 0,
                        attributeIndex == 2 ? value : 0, attributeIndex == 3 ? value : 0, attributeIndex == 4 ? value : 0, 0, bonus));
                    header.Resources = header.Resources with { Vital = Math.Max(0, header.Resources.Vital - 15) };
                }
                // 3,677 observed wins. The multiplier pools the measured win-margin and activated-skill bonuses.
                var draw = random.NextInt(3677);
                var percent = draw switch { < 29 => 100, < 187 => 102, < 598 => 104, < 1030 => 106, < 1327 => 108, < 2376 => 109, _ => 111 };
                header.Fans += checked((int)(((long)race.FirstPlaceFans * rules.FanRewardPercent * percent + 5000) / 10000));
                if (!race.Target && autonomous && data.IsWholeRun)
                {
                    // shortcut: observed 500-fan bonus by grade. Replace marginal rates when its trigger is isolated.
                    var (bonusWindows, windows) = race.Grade switch
                    {
                        100 => (264, 895), 200 => (15, 56), 300 => (24, 69), _ => (20, 50),
                    };
                    if (random.NextInt(windows) < bonusWindows) header.Fans += 500;
                }
                header.RaceGrade = Math.Max(3, header.RaceGrade);
                var counts = state.MutableSection(PioneerStateSection.CareerRaceCounts);
                for (var i = 0; i < counts.Length; i++)
                {
                    var goal = rules.CareerGoals[i];
                    if (header.Turn >= goal.FirstTurn && header.Turn <= goal.Deadline && race.Grade <= goal.MaximumGrade)
                        counts[i]++;
                }
                if (header.Turn >= data.TrainingStart!.PlanningPeriods[0].PlanningTurn && header.CompletedCheckpoint < 5)
                    points = RaceDevelopmentPoints(state, race.Target);
                source = PioneerArrivalSource.Race;
                break;
        }
        var before = header.DevelopmentPoints;
        header.DevelopmentPoints += points;
        if (points > 0 && !ApplyConstructionThresholds(state, before))
            return Partial(state, PioneerStopCode.ConstructionSettlementRequired, turnKnown: true);
        header.CommandId = action.CommandId;
        return AdvanceTurn(state, source, ref random);
    }

    ImmutableArray<PioneerRaceOption> RaceOptions(PioneerState state)
    {
        var current = state.Header;
        var rules = data.TurnRules!;
        if (ActiveScheduledRace(state) is { } scheduled)
            return [scheduled];
        if (data.Start is PioneerStart.TrainingTurn && current.Turn == current.SourceTurn)
            return data.TrainingStart!.HomeActions!.Races;
        return autonomous && data.IsWholeRun ? rules.OptionalRaces(current.Turn, current.RaceGrade) : [];
    }

    static bool CanEnterRace(in PioneerStateHeader state, in PioneerRaceOption race)
        => state.Fans >= race.RequiredFans && (race.Target || race.Grade switch
        {
            900 => state.RaceGrade == 1,
            800 => state.RaceGrade == 2,
            _ => state.RaceGrade >= 3,
        });

    IEnumerable<PioneerRaceOption> RaceCandidates(PioneerState state)
    {
        var current = state.Header;
        var races = RaceOptions(state);
        var mandatory = races.Any(race => race.Target);
        var collapse = data.Start is PioneerStart.Planning || current.Turn != current.SourceTurn;
        // In a generated turn, races in one grade have the same resource reward. Keep the legal entry with the most fans.
        var grades = 0;
        foreach (var race in races)
        {
            if (!CanEnterRace(current, race) || mandatory && !race.Target) continue;
            var bit = 1 << (race.Grade / 100);
            if (collapse && (grades & bit) != 0) continue;
            grades |= bit;
            yield return race;
        }
    }

    PioneerRaceOption? ActiveScheduledRace(PioneerState state)
    {
        var rules = data.TurnRules;
        var turn = state.Header.Turn;
        return rules?.ScheduledRace(turn) is { } race && state.Header.Fans >= race.RequiredFans
            && (race.Target || !rules.RaceBlocked(turn)) ? race : null;
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
            ApplyProgressionReward(state, 3 * rules.RaceRewardPercent / 100, 45 * rules.RaceRewardPercent / 100);
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

    static (int Recovery, int MaxVital, TrainingValues Gains) FriendOuting(TrainingSupport card, int character, int step)
    {
        // shortcut: non-Tucker friends share the ordinary observed friend recovery prior until their own outings are captured.
        if (character != 9049) return (20, 0, default);
        var scaling = EventEffectScaling.FromSupport(card);
        var recovery = step switch { 0 or 1 => 20, 2 => 50, 3 => 25, _ => 30 };
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

    static int TuckerOutingSpecialty(int step) => step switch { 2 => 30, 4 => 120, _ => 20 };

    void ApplyTuckerRepeat(PioneerState state, int participants, bool island, ref SimulationRandom random)
    {
        if (data.TrainingStart?.HomeActions is not { } home) return;
        foreach (var friend in home.Friends)
        {
            if (friend.CharacterId != 9049 || (participants & (1 << (friend.Position - 1))) == 0) continue;
            var stamina = data.TurnRules!.DrawTuckerRepeat(island, ref random);
            if (stamina == 0) return;
            ApplyProgressionReward(state, new(0, stamina, 0, 0, 0, 0, 0));
            AppendTuckerGrants(state, data.TurnRules.OrdinarySupportMask, TemporarySupportGrantSource.TuckerEvent, 20, 809049032);
            return;
        }
    }

    void ApplyFirstFriendTraining(PioneerState state, int first)
    {
        ref var header = ref state.Mutable;
        if (first == 0) return;
        header.UnseenFriendMask &= ~first;
        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
        foreach (var friend in data.TrainingStart!.HomeActions!.Friends)
        {
            if ((first & (1 << (friend.Position - 1))) == 0) continue;
            ApplyMotivationGain(state, 1);
            if (friend.CharacterId != 9049) continue;
            var gain = EventEffectScaling.FromSupport(data.TrainingStart.Deck[friend.Position - 1]).Apply(1, 5);
            var maximum = Math.Min(VitalRules.Maximum, header.Limits.Vital + 4);
            header.Limits = header.Limits with { Vital = maximum };
            bonds[friend.Position - 1] = Math.Min(100, bonds[friend.Position - 1] + 10);
            ApplyProgressionReward(state, new(gain, gain, gain, 0, 0, 0, 0));
        }
    }

    bool DrawFriendUnlock(PioneerState state, ref SimulationRandom random)
    {
        if (data.TrainingStart?.HomeActions is not { } home) return false;
        ref var header = ref state.Mutable;
        foreach (var friend in home.Friends)
        {
            var index = friend.Position - 1;
            var mask = 1 << index;
            if (friend.CharacterId != 9049 || ((header.UnseenFriendMask | header.UnlockedFriendMask) & mask) != 0) continue;
            var high = state.GetSection(PioneerStateSection.SupportBonds)[index] >= 60;
            // UmaAi-rust's ordinary event opportunity. Only the manual outing eligibility carries over.
            if (random.NextInt(100) >= (high ? 25 : 10)) continue;
            header.UnlockedFriendMask |= mask;
            return true;
        }
        return false;
    }

    static void ApplyMotivationGain(PioneerState state, int gain)
        => state.Mutable.Motivation = MotivationRules.Apply(state.Header.Motivation, gain, false,
            state.GetSection(PioneerStateSection.CharacterEffects).Contains(5)).Value;
}
