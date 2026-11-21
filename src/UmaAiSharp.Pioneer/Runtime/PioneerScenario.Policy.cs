using System.Numerics;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Ending;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Scoring;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    static readonly int[] PolicyAttributeScores = Enumerable.Range(0, 2401).Select(ScoringCandidates.StatusToPoint).ToArray();

    PioneerRuntimeAction? SuggestAction(PioneerState state, out string? errorMessage)
    {
        errorMessage = null;
        ref readonly var current = ref state.Header;
        if (current.Stage is PioneerStage.RestConfirmation or PioneerStage.TrainingLevelConfirmation or PioneerStage.ConstructionEvent
            or PioneerStage.TurnConfirmation or PioneerStage.CharacterEnding)
            return PioneerRuntimeAction.Choose(current.Event, current.RequiredChoice);
        if (data.TurnRules is not { } rules) return null;
        if (current.Stage == PioneerStage.ConstructionPlanning)
            return PioneerRuntimeAction.Submit(ChooseConstructionPlan(state));
        if (current.Stage != PioneerStage.TrainingTurn)
            return null;
        if (ActiveScheduledRace(state) is { } mandatory)
            return new(PioneerActionKind.Race, mandatory.ProgramId);

        PioneerRuntimeAction? bestAction = null;
        var best = double.NegativeInfinity;
        var vital = current.Resources.Vital;
        var maxVital = current.Limits.Vital;
        var turn = current.Turn;
        var turnsLeft = Math.Max(0, 77 - turn);
        var periods = data.TrainingStart!.PlanningPeriods;
        var points = current.DevelopmentPoints;
        var checkpoint = current.CompletedCheckpoint;
        var period = rules.WholeRun && checkpoint < periods.Length ? periods[checkpoint] : null;
        var target = period?.TargetPt ?? 0;
        var daysToReview = period is null ? 0 : period.CheckPointTurn - turn;
        var recovery = period is null || daysToReview != 0 ? null : data.Checkpoints!.Master.PointBonuses[checkpoint + 1];
        Span<double> attributeWeights = stackalloc double[5];
        Span<int> attributeForecast = stackalloc int[5];
        Span<double> ordinaryUses = stackalloc double[5];
        Span<TrainingProspect> prospects = stackalloc TrainingProspect[5];
        Span<double> islandUpgradeGains = stackalloc double[4];
        islandUpgradeGains.Fill(double.NaN);
        ValueTrainingAttributes(state, attributeWeights, ordinaryUses, attributeForecast, prospects);
        var nextCamp = turn is >= 34 and <= 36 ? 37 : turn is >= 58 and <= 60 ? 61 : 0;
        var inCamp = CampCommands(current);
        var daysToCamp = nextCamp == 0 ? 0 : nextCamp - turn;
        var actionAfterThisTurn = turn + 1;
        while (actionAfterThisTurn < 78 && rules.ScheduledRace(actionAfterThisTurn, current.ScheduleVariant) is not null) actionAfterThisTurn++;
        var campAhead = actionAfterThisTurn < 78 && IsCamp(actionAfterThisTurn);
        var mood = current.Motivation;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var hasMigraine = effects.Contains(5);
        var positiveThinking = effects.Contains(25);
        var skillPointValue = SkillPointValue(state);
        // shortcut: estimate the useful training turns before another recovery opportunity.
        var moodStepValue = Math.Min(turnsLeft, turn <= 12 ? 8 : 6) * (turn <= 12 ? 4.5 : 3.2);
        var mildMoodLoss = mood - MotivationRules.Apply(mood, -1, positiveThinking, hasMigraine).Value;
        var severeMoodLoss = mood - MotivationRules.Apply(mood, -3, positiveThinking, hasMigraine).Value;
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        var tickets = current.Tickets;
        var nextThreshold = int.MaxValue;
        foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
            if (threshold > points) nextThreshold = Math.Min(nextThreshold, threshold);
        var deferredConstruction = current.DeferredConstruction;
        var islandValue = 0.0;
        var islandPoints = 0;
        var islandAvailable = false;
        var islandParticipants = 0;
        var otherIslandValue = 0.0;
        var (nextOuting, outingTurns, futureCampTurns, vitalBeforeOuting, projectedVitalCost) =
            trainingProjection?.RecoveryOutlook(turn, period?.CheckPointTurn ?? 77, tickets, current.ScheduleVariant) ?? default;
        var outingCapacity = outingTurns == 0 ? 0 : Math.Max(0, outingTurns - tickets) / (double)outingTurns;
        (int Recovery, double Reward) outingReserve = default;
        (int Recovery, double Reward) otherOutingReserve = default;
        var reserveFriend = 0;
        var tuckerMask = rules.TuckerMask;
        var tuckerSlot = BitOperations.TrailingZeroCount((uint)tuckerMask);
        var ticketValue = 0.0;
        var careerCounts = state.GetSection(PioneerStateSection.CareerRaceCounts);
        var (fanShortfall, fanDeadline) = rules.FirstFanShortfall(turn, current.Fans, current.ScheduleVariant, MinimumRaceFanPercent);
        try
        {
            if (outingCapacity > 0 && current.UnlockedFriendMask != 0 && data.TrainingStart.HomeActions is { } home)
            {
                foreach (var friend in home.Friends)
                {
                    var position = friend.Position - 1;
                    var step = (current.FriendOutings >> (position * 3)) & 7;
                    if (step >= 5 || (current.UnlockedFriendMask & 1 << position) == 0) continue;
                    var card = data.TrainingStart.Deck[position];
                    var (recovered, _, reward) = FriendOuting(state, card, friend.CharacterId, step);
                    var pending = (Recovery: recovered, Reward: OutingReward(card, friend.CharacterId, step, reward, attributeForecast, attributeWeights));
                    if (pending.Recovery > outingReserve.Recovery)
                    {
                        otherOutingReserve = outingReserve;
                        outingReserve = pending;
                        reserveFriend = friend.Position;
                    }
                    else if (pending.Recovery > otherOutingReserve.Recovery) otherOutingReserve = pending;
                }
            }
            if (nextThreshold != int.MaxValue && !deferredConstruction)
            {
                var firstUse = actionAfterThisTurn;
                while (firstUse <= 72 && (IsCamp(firstUse) || rules.ScheduledRace(firstUse, current.ScheduleVariant) is not null)) firstUse++;
                if (firstUse <= 72)
                {
                    ticketValue = TicketValue(state, nextThreshold, attributeForecast, attributeWeights);
                    if (!inCamp) ticketValue /= firstUse - turn;
                }
            }
            if (TryPreviewIsland(state, out var island))
            {
                islandAvailable = true;
                islandPoints = IslandDevelopmentPoints(state);
                for (var i = 0; i < 6; i++) islandParticipants |= current.IslandBoard[i].SupportMask;
                islandValue = AttributeGain(current, attributeForecast, island.Common, island.Scenario, attributeWeights)
                    + (island.Common.SkillPoints + island.Scenario.SkillPoints) * skillPointValue
                    + Math.Min(1, turnsLeft / 38.0) * BondGain(rules.OrdinarySupportMask, bonds, 10 + (current.HasCharm ? 2 : 0));
                if (rules.SkillRating is not null)
                {
                    var (all, one) = IslandHintMasks(current.IslandBoard, rules.CurrentFacilities(state));
                    var baseBond = 10 + (current.HasCharm ? 2 : 0);
                    var hintBondGain = baseBond + HintBondGain(current);
                    islandValue += ExpectedHintValue(state, all, true, hintBondGain, attributeForecast, attributeWeights, skillPointValue)
                        + ExpectedHintValue(state, one, false, hintBondGain, attributeForecast, attributeWeights, skillPointValue);
                    var allBond = BondGain(all, bonds, hintBondGain) - BondGain(all, bonds, baseBond);
                    var oneBond = one == 0 ? 0 : (BondGain(one, bonds, hintBondGain) - BondGain(one, bonds, baseBond))
                        / BitOperations.PopCount((uint)one);
                    islandValue += Math.Min(1, turnsLeft / 38.0) * (allBond + oneBond);
                }
                var eligible = 0;
                var rainbow = 0;
                foreach (var card in data.TrainingStart!.Deck)
                {
                    if (bonds[card.Position - 1] < 80 || card.TrainingCommandId == 0) continue;
                    eligible++;
                    var index = TrainingBaseTable.CommandIndex(card.TrainingCommandId);
                    if ((current.IslandBoard.Outdoor[index].SupportMask & (1 << (card.Position - 1))) != 0)
                        rainbow |= 1 << index;
                }
                var colors = BitOperations.PopCount((uint)rainbow);
                var premium = 31 * Math.Clamp(eligible / 2.0 - colors, 0, 2);
                otherIslandValue = Math.Min(240, .66 * islandValue + premium);
            }

            var recoveryBefore = RecoveryValue(vital, 0);
            foreach (var candidate in Candidates(state))
            {
                var action = candidate.Action;
                var score = double.NegativeInfinity;
                var reward = 0.0;
                switch (action.Kind)
                {
                    case PioneerActionKind.Race:
                    {
                        var race = rules.RaceOption(action.CommandId);
                        if (race.Target) return action;
                        var (gain, skill) = OptionalRaceReward(race.Grade, rules.RaceRewardPercent);
                        var (interviews, raceWindows) = ReporterInterviewFrequency(race.Grade);
                        var interviewChance = interviews / (double)raceWindows;
                        var expectedFans = 500 * interviewChance;
                        foreach (var (percent, count) in RaceFanMultipliers)
                            expectedFans += (double)count / RaceFanSamples * rules.FirstPlaceFans(race.FirstPlaceFans, percent);
                        var racePoints = rules.WholeRun && turn >= periods[0].PlanningTurn && checkpoint < periods.Length
                            ? RaceDevelopmentPoints(state, target: false) : 0;
                        var speed = Display(current.Resources.Speed, gain);
                        var stamina = Display(current.Resources.Stamina, gain);
                        var power = Display(current.Resources.Power, gain);
                        var guts = Display(current.Resources.Guts, gain);
                        var wisdom = Display(current.Resources.Wisdom, gain);
                        var raceValue = AttributeGain(current, attributeForecast, new(speed, stamina, power, guts, wisdom, 0, 0), default, attributeWeights) / 5;
                        reward = raceValue + skill * skillPointValue + Progress(racePoints, false)
                            + UpcomingUniqueUpgradeValue(current, race, skillPointValue);
                        if (rules.WholeRun)
                        {
                            var (light, heavy, total) = RaceFatigueFrequency(NextRaceStreak(current));
                            var moodLossChance = (light + heavy) / (double)total;
                            var afterInterview = MotivationRules.Apply(mood, 1, false, hasMigraine).Value;
                            var afterFatigue = MotivationRules.Apply(afterInterview, -1, positiveThinking, hasMigraine).Value;
                            reward += interviewChance * ((afterInterview - mood
                                + moodLossChance * (afterFatigue - afterInterview)) * moodStepValue
                                + ReporterBondValue(current, current.HasAttention ? 17 : 15, attributeForecast, attributeWeights, skillPointValue))
                                - (1 - interviewChance) * moodLossChance * mildMoodLoss * moodStepValue;
                            if (heavy > 0)
                            {
                                ReadOnlySpan<int> attrs = [current.Resources.Speed, current.Resources.Stamina,
                                    current.Resources.Power, current.Resources.Guts, current.Resources.Wisdom];
                                for (var i = 0; i < attrs.Length; i++)
                                    reward -= (heavy / (double)total) * (3.0 / 5) * attributeWeights[i]
                                        * (Rating(attrs[i]) - Rating(TrainingFailureSettlement.LoseAttribute(attrs[i], 10))) / 7;
                            }
                        }
                        if (fanShortfall > 0 && expectedFans > 0)
                            reward += Math.Min(fanShortfall, expectedFans) / (double)fanShortfall
                                * (18 + 160.0 / (1 + fanDeadline - turn));
                        for (var i = 0; i < careerCounts.Length; i++)
                        {
                            var goal = rules.CareerGoals[i];
                            var needed = goal.RaceCount - careerCounts[i];
                            if (needed <= 0 || turn < goal.FirstTurn || turn > goal.Deadline || race.Grade > goal.MaximumGrade) continue;
                            var availableFans = current.Fans + rules.FirstPlaceFans(race.FirstPlaceFans, MaximumRaceFanPercent) + 500;
                            var slots = 1 + Math.Max(0, rules.ReachableRaceCount(turn, availableFans, Math.Max(3, current.RaceGrade),
                                current.ScheduleVariant, goal, MaximumRaceFanPercent));
                            reward += 20 + (slots <= needed ? 1000 : 120.0 / (1 + slots - needed));
                        }
                        score = reward + RecoveryValue(Math.Max(0, vital - 15), racePoints) - recoveryBefore;
                        break;
                    }
                    case PioneerActionKind.Train:
                        var preview = PreviewTraining(state, action.CommandId);
                        var gains = preview.Gains;
                        var afterVital = Math.Clamp(vital + gains.Common.Vital + gains.Scenario.Vital, 0, maxVital);
                        var index = TrainingBaseTable.CommandIndex(action.CommandId);
                        var placement = current.TrainingBoard[index];
                        var development = rules.WholeRun && turn >= periods[0].PlanningTurn && checkpoint < periods.Length
                            ? PioneerPointGain.OrdinaryTraining(placement.SupportCount + placement.GuestCount,
                                current.CheckpointBonusPercent, preview.Friendship)
                            : 0;
                        var bondGain = data.TrainingDeck!.TrainingBondBonus(placement.SupportMask) + (current.HasCharm ? 2 : 0);
                        var hints = placement.HintMask & rules.OrdinarySupportMask;
                        var allHints = hints != 0 && AllCampHints(state, action.CommandId);
                        var hintBondGain = 7 + bondGain + HintBondGain(current);
                        var hintBond = hints == 0 ? 0
                            : BondGain(hints, bonds, hintBondGain) - BondGain(hints, bonds, 7 + bondGain);
                        if (hintBond > 0 && !allHints)
                            hintBond /= BitOperations.PopCount((uint)hints);
                        var tuckerValue = TuckerTrainingValue(placement.SupportMask, bondGain, island: false, attributeForecast, attributeWeights, prospects);
                        var upgrade = 0.0;
                        if (!inCamp && trainingProjection is { } projection)
                            upgrade = projection.UpgradeValue(state, index, ordinaryUses[index], attributeForecast, attributeWeights,
                                islandUpgradeGains, skillPointValue, prospects[index]);
                        reward = AttributeGain(current, attributeForecast, gains.Common, gains.Scenario, attributeWeights)
                            + (gains.Common.SkillPoints + gains.Scenario.SkillPoints) * skillPointValue
                            + ExpectedHintValue(state, hints, allHints, hintBondGain, attributeForecast, attributeWeights, skillPointValue)
                            + Math.Min(1, turnsLeft / 38.0)
                                * (BondGain(placement.SupportMask & rules.OrdinarySupportMask, bonds, 7 + bondGain) + hintBond)
                            + FirstFriendTrainingValue(placement.SupportMask, attributeForecast, attributeWeights)
                            + NpcTrainingValue(placement, index, gains, attributeForecast, attributeWeights)
                            + tuckerValue + upgrade
                            + Progress(development, false);
                        var benefit = reward + RecoveryValue(afterVital, development) - recoveryBefore;
                        var failure = preview.FailureRate / 100.0;
                        if (index == 4)
                        {
                            var failedVital = Math.Min(maxVital, vital + TrainingFailureSettlement.WisdomRecovery);
                            score = (1 - failure) * benefit + failure * (RecoveryValue(failedVital, 0) - recoveryBefore);
                            break;
                        }
                        var mild = 8 + TrainingFailureSettlement.LightAttributeLoss * attributeWeights[index] + mildMoodLoss * moodStepValue;
                        var severe = 63 + severeMoodLoss * moodStepValue;
                        score = (1 - failure) * benefit - failure * ((1 - failure) * mild + failure * severe);
                        break;
                    case PioneerActionKind.IslandTraining when islandAvailable:
                        reward = islandValue + Progress(islandPoints, true)
                            + FirstFriendTrainingValue(islandParticipants, attributeForecast, attributeWeights)
                            + TuckerTrainingValue(islandParticipants, current.HasCharm ? 2 : 0, island: true, attributeForecast, attributeWeights, prospects);
                        score = reward + RecoveryValue(vital, islandPoints) - recoveryBefore;
                        break;
                    case PioneerActionKind.Rest:
                        var camp = action.CommandId == RestRules.CampCommand;
                        var rested = camp ? RecoveryValue(Math.Min(maxVital, vital + 40), 0)
                            : (1 - rules.LongRestProbability) * RecoveryValue(Math.Min(maxVital, vital + 50), 0)
                                + rules.LongRestProbability * RecoveryValue(Math.Min(maxVital, vital + 70), 0);
                        reward = rested - recoveryBefore + (camp ? Motivation(1) : 0);
                        score = reward + Progress(0, false);
                        break;
                    case PioneerActionKind.Treat:
                        score = Math.Min(25, turnsLeft) + RecoveryValue(Math.Min(maxVital, vital + 20), 0)
                            - recoveryBefore + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing when action.CommandId == 301:
                        foreach (var (restore, motivation, count) in OrdinaryOutings)
                            reward += count * (Motivation(motivation) + RecoveryValue(Math.Min(maxVital, vital + restore), 0) - recoveryBefore);
                        score = reward / OrdinaryOutingSamples + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing:
                        var position = action.SupportPosition - 1;
                        var friend = data.TrainingStart!.HomeActions!.Friends.Single(item => item.Position == action.SupportPosition);
                        var card = data.TrainingStart.Deck[position];
                        var step = (current.FriendOutings >> (position * 3)) & 7;
                        var (recoveredVital, maximum, outingReward) = FriendOuting(state, card, friend.CharacterId, step);
                        var (recovered, newMaximum) = VitalRules.Apply(vital, maxVital, recoveredVital, maximum);
                        var tucker = friend.CharacterId == 9049;
                        var gift = tucker && TuckerGiftAvailable(current);
                        var outingDevelopment = gift ? ChooseTuckerGiftPoints(state, recovered) : 0;
                        var specialty = tucker && actionAfterThisTurn == turn + 1 && turnsLeft > 0
                            ? TuckerOutingSpecialty(step) * .09 : 0;
                        (int Recovery, double Reward) following = default;
                        if (outingCapacity > 0 && step < 4)
                        {
                            var (nextRecovery, _, nextReward) = FriendOuting(state, card, friend.CharacterId, step + 1);
                            following = (nextRecovery, OutingReward(card, friend.CharacterId, step + 1, nextReward, attributeForecast, attributeWeights));
                        }
                        reward = OutingReward(card, friend.CharacterId, step, outingReward, attributeForecast, attributeWeights)
                            + (gift ? 3 * skillPointValue : 0)
                            + RecoveryValue(recovered, outingDevelopment, friend.Position, following, newMaximum) - recoveryBefore
                            + Motivation(1) + specialty
                            + Math.Min(1, turnsLeft / 45.0) * maximum;
                        score = reward + Progress(outingDevelopment, false);
                        break;
                }
                if (score <= best) continue;
                best = score;
                bestAction = action;
            }
        }
        catch (UnknownMechanismException error)
        {
            errorMessage = error.Message;
            return null;
        }
        return bestAction;

        double RecoveryValue(double value, int development, int consumedFriend = 0, (int Recovery, double Reward) following = default, int limit = 0)
        {
            limit = limit == 0 ? maxVital : limit;
            if (recovery is not null)
                value = Math.Min(limit, value + Math.Min(recovery.AddValueMax,
                    (long)Math.Max(0, points + development - target) * recovery.PtRate / 1000));
            var reserve = consumedFriend == reserveFriend ? otherOutingReserve : outingReserve;
            if (following.Recovery > reserve.Recovery) reserve = following;
            // The same headroom is shared by rest and the next outing. Using an outing spends that option.
            var weight = reserve.Recovery == 0 ? 0 : outingCapacity / (nextOuting - turn);
            value += weight * Math.Min(reserve.Recovery, limit - Math.Max(0, value - vitalBeforeOuting));
            var heldReward = weight * reserve.Reward;
            var immediate = VitalityValue(value, turnsLeft, actionAfterThisTurn <= nextCamp ? daysToCamp : 0, campAhead);
            if (projectedVitalCost == 0) return immediate + heldReward;
            // The last projected camp board still needs enough vitality to train after earlier boards have been used.
            return .5 * immediate + .5 * VitalityValue(Math.Max(0, value - projectedVitalCost), turnsLeft, campAhead: futureCampTurns > 0) + heldReward;
        }

        double OutingReward(TrainingSupport card, int character, int step, in TrainingValues gains,
            ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights)
        {
            var (slot, levels) = character == 9049 ? TuckerOutingHint(card.CardId, step) : default;
            var hint = levels != 0 && rules.SkillRating is not null ? EventHintValue(state, slot, levels) : 0;
            return AttributeGain(state.Header, forecasts, DisplayGains(state.Header.Resources, gains), default, weights)
                + gains.SkillPoints * skillPointValue + hint;
        }

        double Motivation(int increase)
        {
            return (MotivationRules.Apply(mood, increase, positiveThinking, hasMigraine).Value - mood) * moodStepValue;
        }

        double FirstFriendTrainingValue(int participants, ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights)
        {
            var first = participants & state.Header.UnseenFriendMask;
            var value = Motivation(BitOperations.PopCount((uint)first));
            if ((first & tuckerMask) == 0) return value;
            var gains = TuckerFirstTrainingReward(tuckerSlot);
            var extraVital = Math.Min(TuckerFirstTrainingMaxVital, VitalRules.Maximum - maxVital);
            return value + AttributeGain(state.Header, forecasts, DisplayGains(state.Header.Resources, gains), default, weights)
                + extraVital * Math.Min(1, turnsLeft / 45.0);
        }

        double NpcTrainingValue(TrainingPlacement placement, int index, in TrainingNumericResult gains,
            ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights)
        {
            if (!data.IsWholeRun || !placement.Director && !placement.Reporter) return 0;
            var current = state.Header;
            var attention = current.HasAttention ? 2 : 0;
            var immediateBond = 2 + attention;
            var value = placement.Director ? DirectorBondValue(current, immediateBond + 5 + attention)
                + skillPointValue * NpcTrainingReward(current.DirectorBond + immediateBond) : 0;
            if (!placement.Reporter) return value;
            var attr = NpcTrainingReward(current.ReporterBond + immediateBond) + attention;
            current.Resources = TrainingPreviewSettlement.Apply(current.Resources, current.Limits,
                gains.Common with { Vital = Math.Max(-current.Resources.Vital, gains.Common.Vital) }, gains.Scenario);
            return value + AttributeGain(current, forecasts,
                DisplayGains(current.Resources, TrainingAttributeReward(index, attr)), default, weights)
                + ReporterBondValue(current, immediateBond + 7 + attention, forecasts, weights, skillPointValue);
        }

        double TuckerTrainingValue(int participants, int bonusBond, bool island, ReadOnlySpan<int> forecasts,
            ReadOnlySpan<double> weights, ReadOnlySpan<TrainingProspect> prospects)
        {
            if ((participants & tuckerMask) == 0) return 0;
            var tuckerBond = state.GetSection(PioneerStateSection.SupportBonds)[tuckerSlot];
            var first = (state.Header.UnseenFriendMask & tuckerMask) != 0;
            var gain = (island ? 7 : 4) + bonusBond
                + (first ? TuckerFirstTrainingBond + (state.Header.HasCharm ? 2 : 0) : 0);
            var value = first ? 0 : AttributeGain(state.Header, forecasts,
                new(0, (int)Math.Round(rules.ExpectedTuckerRepeat(island)), 0, 0, 0, 0, 0), default, weights);
            if ((state.Header.UnlockedFriendMask & tuckerMask) == 0 && turn < 25 && tuckerBond < 60)
            {
                // Value the expected time saved before the first outing, accounting for unlocks below 60.
                var steps = (60 - tuckerBond + 3) / 4;
                var remaining = Math.Max(0, (60 - tuckerBond - gain + 3) / 4);
                var before = ExpectedUnlockTurns(steps);
                var after = ExpectedUnlockTurns(remaining);
                var firstOuting = FriendOuting(state, data.TrainingStart!.Deck[tuckerSlot], 9049, 0);
                var outingValue = AttributeGain(state.Header, forecasts, DisplayGains(state.Header.Resources, firstOuting.Gains),
                    default, weights) + firstOuting.Recovery * .47 + firstOuting.MaxVital * 3;
                value += (before - after) / (1 + before) * outingValue;

                static double ExpectedUnlockTurns(int trainings)
                {
                    // shortcut: space friend trainings three turns apart while approaching the high-probability tier.
                    var low = FriendUnlockChance(0) / 100.0;
                    var high = FriendUnlockChance(60) / 100.0;
                    return 1 / low + Math.Pow(1 - low, 3 * trainings) * (1 / high - 1 / low);
                }
            }
            if (actionAfterThisTurn != turn + 1 || turnsLeft == 0) return value;
            var nextBoard = trainingProjection?.TuckerSpecialtyValue(state, participants & rules.OrdinarySupportMask,
                island ? 10 + bonusBond : 7 + bonusBond, forecasts, weights, prospects, skillPointValue) ?? 0;
            return value + nextBoard;
        }

        double Progress(int gain, bool spendTicket)
        {
            var crosses = turn is >= 5 and <= 60 && points + gain >= nextThreshold && (!inCamp || !deferredConstruction);
            var assessment = AssessmentPointValue(gain, target, points, daysToReview);
            var issue = crosses && (tickets == 0 || spendTicket) ? Math.Min(32, ticketValue) : 0;
            var carry = 0.0;
            if (islandAvailable)
            {
                var nextIssue = nextThreshold == int.MaxValue ? 72
                    : Math.Min(72, turn + Math.Max(1, (nextThreshold - points + 59) / 60));
                var chances = 0;
                for (var day = turn + 1; day <= nextIssue; day++)
                    if (!IsCamp(day) && rules.ScheduledRace(day, state.Header.ScheduleVariant) is null) chances++;
                if (spendTicket && !crosses)
                    carry = otherIslandValue * Math.Clamp((chances - tickets + 1) / 2.0, 0, 1);
                else if (!spendTicket && (crosses || chances < tickets))
                    carry = Math.Min(105, .65 * islandValue);
            }
            return assessment + issue - carry;
        }
    }

    static double AssessmentPointValue(int gain, int target, int points, int daysToReview)
        => Math.Min(gain, Math.Max(0, target - points)) * (daysToReview <= 3 ? .18 : .09);

    double UpcomingUniqueUpgradeValue(in PioneerStateHeader state, in PioneerRaceOption race, double skillPointValue)
    {
        var next = state.Turn + 1;
        if (next is not (51 or 55 or 72) || next == 55 && state.DirectorBond < 60) return 0;
        var threshold = UniqueUpgradeFanThreshold(next, data.TrainingStart!.CharacterId);
        var needed = threshold - state.Fans;
        if (needed <= 0) return 0;
        var rules = data.TurnRules!;
        var (interviews, races) = ReporterInterviewFrequency(race.Grade);
        var eligible = 0;
        foreach (var (percent, count) in RaceFanMultipliers)
        {
            var fans = rules.FirstPlaceFans(race.FirstPlaceFans, percent);
            if (fans >= needed) eligible += count * races;
            else if (fans + 500 >= needed) eligible += count * interviews;
        }
        return eligible / (double)(RaceFanSamples * races) * (rules.UniqueSkillLevelRating / 7.0 - (next == 55 ? 25 : 20) * skillPointValue);
    }

    int ChooseTuckerGiftPoints(PioneerState state, int vital)
    {
        const int award = 80;
        ref readonly var current = ref state.Header;
        if (current.Tickets == 0) return award;
        var points = current.DevelopmentPoints;
        var threshold = ConstructionTriggerRules.CrossedThreshold(state.GetSection(PioneerStateSection.Thresholds), points, points + award);
        if (threshold == 0) return award;

        var next = current.Turn + 1;
        while (next <= 72 && (IsCamp(next) || data.TurnRules!.ScheduledRace(next, current.ScheduleVariant) is not null)) next++;
        if (next > 72) return award;

        var period = data.TrainingStart!.PlanningPeriods[current.CompletedCheckpoint];
        var days = period.CheckPointTurn - current.Turn;
        var value = AssessmentPointValue(award, period.TargetPt, points, days);
        if (days == 0)
        {
            var recovery = data.Checkpoints!.Master.PointBonuses[period.CheckPointType];
            var space = current.Limits.Vital - vital;
            var before = Recovery(points);
            var after = Recovery(points + award);
            value += VitalityValue(vital + after, 77 - current.Turn) - VitalityValue(vital + before, 77 - current.Turn);

            int Recovery(int present) => Math.Min(space, Math.Min(recovery.AddValueMax,
                Math.Max(0, present - period.TargetPt) * recovery.PtRate / 1000));
        }

        // Issuing a replacement now consumes the chance to use the held ticket before crossing this threshold.
        Span<double> weights = stackalloc double[5];
        Span<int> forecasts = stackalloc int[5];
        ValueTrainingAttributes(state, weights, stackalloc double[5], forecasts);
        return value >= TicketValue(state, threshold, forecasts, weights) ? award : 0;
    }

    double TicketValue(PioneerState state, int threshold, ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights)
    {
        var header = state.Header;
        var master = data.Construction!.Data;
        var facilities = state.GetSection(PioneerStateSection.FacilityStates);
        var plan = state.GetSection(PioneerStateSection.ConstructionSteps);
        var built = 0;
        for (var i = 0; i < facilities.Length; i += 2)
            if (master.StatesById[(facilities[i], facilities[i + 1])].FacilityLv > 0)
                built |= 1 << (facilities[i] - 101);
        var spent = 0;
        for (var i = 0; i < plan.Length; i += 4)
        {
            spent += master.UpgradesByEdge[(plan[i + 1], plan[i + 2], plan[i + 3])].Cost;
            if (spent > threshold) break;
            built |= 1 << (plan[i + 1] - 101);
        }

        // shortcut: value access to one useful board using built training types and the useful part of the all-deck bond gain.
        var value = 0.0;
        var skillPointValue = SkillPointValue(state);
        for (var i = 0; i < 5; i++)
        {
            if ((built & (1 << i)) == 0) continue;
            var basis = TrainingBaseTable.GetOrdinary(TrainingFacilityRules.Command(101 + i), 1);
            value += AttributeGain(header, forecasts, DisplayGains(header.Resources, basis), default, weights) + basis.SkillPoints * skillPointValue;
        }
        var house = (built & 32) != 0;
        if (built == 0) return 0;
        var bond = BondGain(data.TurnRules!.OrdinarySupportMask, state.GetSection(PioneerStateSection.SupportBonds), 10);
        return .30 * value + (house ? 6 : 0) + .22 * bond * Math.Min(1, (77 - header.Turn) / 38.0);
    }

    int SelectResourceReward(PioneerState state, ReadOnlySpan<TrainingValues> choices, int followingRecovery = 0,
        ReadOnlySpan<double> otherValues = default)
    {
        ref readonly var current = ref state.Header;
        Span<double> weights = stackalloc double[5];
        Span<int> forecasts = stackalloc int[5];
        ValueTrainingAttributes(state, weights, stackalloc double[5], forecasts);
        var turnsLeft = 77 - current.Turn;
        var vital = current.Resources.Vital;
        var maximum = current.Limits.Vital;
        var baselineVitalValue = VitalityValue(Math.Min(maximum, vital + followingRecovery), turnsLeft);
        var skillPointValue = SkillPointValue(state);
        var bestValue = double.NegativeInfinity;
        var best = 0;
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = choices[index];
            var recovered = Math.Min(maximum, Math.Clamp(vital + choice.Vital, 0, maximum) + followingRecovery);
            var recoveryValue = VitalityValue(recovered, turnsLeft) - baselineVitalValue;
            var value = AttributeGain(current, forecasts, DisplayGains(current.Resources, choice), default, weights)
                + recoveryValue + choice.SkillPoints * skillPointValue + (otherValues.IsEmpty ? 0 : otherValues[index]);
            if (value <= bestValue) continue;
            bestValue = value;
            best = index;
        }
        return best;
    }

    void ApplyCalendarChoice(PioneerState state, ReadOnlySpan<TrainingValues> choices, int followingRecovery = 0)
    {
        ApplyProgressionReward(state, choices[SelectResourceReward(state, choices, followingRecovery)]);
    }

    void ValueTrainingAttributes(PioneerState state, Span<double> weights, Span<double> ordinaryUses, Span<int> forecasts,
        Span<TrainingProspect> prospects = default)
    {
        if (trainingProjection is { } projection)
            projection.ValueAttributes(state, CampCommands(state.Header), weights, ordinaryUses, forecasts,
                prospects.IsEmpty ? stackalloc TrainingProspect[5] : prospects);
        else
        {
            forecasts.Fill(ProjectedAttributeGain(state.Header));
            weights.Fill(1);
        }
    }

    static double VitalityValue(double value, int turnsLeft, int daysToCamp = 0, bool campAhead = false)
    {
        var campReserve = daysToCamp > 0 ? .70 / daysToCamp * Math.Min(value, 90) : 0;
        // Preserve the next camp board by carrying enough vitality to cover an ordinary camp training.
        var campBoard = campAhead ? .85 * Math.Clamp(value - 45, 0, 25) : 0;
        return Math.Min(1, turnsLeft / 4.0) * (1.03 * Math.Min(value, 50)
            + .47 * Math.Clamp(value - 50, 0, 25) + .13 * Math.Max(0, value - 75) + campReserve + campBoard);
    }

    // shortcut: use high reviews and the closing award when projecting remaining raw room.
    static int ProjectedAttributeGain(in PioneerStateHeader current)
        => Math.Max(0, 72 - current.Turn) * 16 + FixedAttributeGain(current) + (current.Turn switch
        {
            <= 73 => 30,
            <= 75 => 20,
            _ => 10,
        });

    static int FixedAttributeGain(in PioneerStateHeader current)
    {
        var remaining = PioneerEndingSettlement.HighAttribute;
        for (var i = Math.Clamp(current.CompletedCheckpoint, 0, 5); i < 5; i++)
            remaining += HighCheckpointAttributes[i];
        return remaining;
    }

    static double AttributeGain(in PioneerStateHeader current, int forecast, TrainingValues common, TrainingValues scenario,
        ReadOnlySpan<double> weights = default)
        => AttributeGain(current, [forecast, forecast, forecast, forecast, forecast], common, scenario, weights);

    static double AttributeGain(in PioneerStateHeader current, ReadOnlySpan<int> forecasts, TrainingValues common,
        TrainingValues scenario, ReadOnlySpan<double> weights)
    {
        var values = current.Resources;
        var limits = current.Limits;
        return Value(values.Speed, limits.Speed, common.Speed + scenario.Speed, forecasts[0]) * (weights.IsEmpty ? 1 : weights[0])
            + Value(values.Stamina, limits.Stamina, common.Stamina + scenario.Stamina, forecasts[1]) * (weights.IsEmpty ? 1 : weights[1])
            + Value(values.Power, limits.Power, common.Power + scenario.Power, forecasts[2]) * (weights.IsEmpty ? 1 : weights[2])
            + Value(values.Guts, limits.Guts, common.Guts + scenario.Guts, forecasts[3]) * (weights.IsEmpty ? 1 : weights[3])
            + Value(values.Wisdom, limits.Wisdom, common.Wisdom + scenario.Wisdom, forecasts[4]) * (weights.IsEmpty ? 1 : weights[4]);

        static double Value(int before, int cap, int gain, int forecast)
        {
            gain = Math.Min(gain, cap - before);
            if (gain == 0) return 0;
            var rawBefore = RawEquivalent(before);
            var rawGain = RawEquivalent(before + gain) - rawBefore;
            var rawCap = RawEquivalent(cap);
            var forecastRaw = rawBefore + forecast;
            var room = Math.Max(0, rawCap - forecastRaw);
            var midpoint = Math.Max(rawBefore, Math.Min(forecastRaw, rawCap - rawGain / 2));
            var added = Rating(DisplayEquivalent(Math.Min(rawCap, midpoint + rawGain)))
                - Rating(DisplayEquivalent(midpoint));
            var uncapped = Rating(DisplayEquivalent(midpoint + rawGain)) - Rating(DisplayEquivalent(midpoint));
            var usableFraction = Math.Clamp(room / (75.0 + forecast * .28), .06, 1);
            return (.25 * added + .75 * uncapped * usableFraction) / 7;
        }
    }

    static int Rating(int value) => value < PolicyAttributeScores.Length
        ? PolicyAttributeScores[value] : ScoringCandidates.StatusToPoint(value);

    static int RawEquivalent(int value) => value + Math.Max(0, value - 1200);

    static int DisplayEquivalent(int value) => value <= 1200 ? value : 1200 + (value - 1200) / 2;

    static TrainingValues DisplayGains(TrainingValues before, TrainingValues gain) => new(
        Display(before.Speed, gain.Speed), Display(before.Stamina, gain.Stamina), Display(before.Power, gain.Power),
        Display(before.Guts, gain.Guts), Display(before.Wisdom, gain.Wisdom), gain.Vital, gain.SkillPoints);

    static int Display(int before, int gain)
    {
        var below = Math.Min(gain, Math.Max(0, 1200 - before));
        return below + (gain > below ? Math.Max(1, (gain - below) / 2) : 0);
    }

    double SkillPointValue(PioneerState state)
        => (data.TurnRules?.SkillRating?.RatingPerPoint(
            FutureSkillRating.PurchaseDiscount(state.GetSection(PioneerStateSection.CharacterEffects))) ?? 0) / 7;

    static double DirectorBondValue(in PioneerStateHeader state, int gain) => state.Turn < 55
        ? 3.0 * Math.Clamp(60 - state.DirectorBond, 0, gain) / (state.HasAttention ? 11 : 7) : 0;

    static double ReporterBondValue(in PioneerStateHeader state, int gain, ReadOnlySpan<int> forecasts,
        ReadOnlySpan<double> weights, double skillPointValue)
    {
        var value = 0.0;
        var from = 0;
        foreach (var to in (ReadOnlySpan<int>)[40, 70, 90])
        {
            var overlap = Math.Min(to, state.ReporterBond + gain) - Math.Max(from, state.ReporterBond);
            if (overlap > 0)
            {
                var before = ReporterEndingReward(from, state.HasAttention);
                var after = ReporterEndingReward(to, state.HasAttention);
                var attr = after.Attribute - before.Attribute;
                var benefit = AttributeGain(state, forecasts,
                    DisplayGains(state.Resources, new(attr, attr, attr, attr, attr, 0, 0)), default, weights)
                    + (after.SkillPoints - before.SkillPoints) * skillPointValue;
                // shortcut: distribute the ending tiers across their bond intervals in the policy.
                value += benefit * overlap / (to - from);
            }
            from = to;
        }
        return value;
    }

    double EventHintValue(PioneerState state, int slot, int levels)
    {
        var hints = state.GetSection(PioneerStateSection.HintLevels);
        var discount = FutureSkillRating.PurchaseDiscount(state.GetSection(PioneerStateSection.CharacterEffects));
        return data.TurnRules!.SkillRating!.HintValue(slot, Math.Min(5, Math.Max(0, hints[slot]) + levels), hints,
            state.GetSection(PioneerStateSection.InheritanceAptitudes), state.Header.Resources, discount) / 7;
    }

    double ExpectedHintValue(PioneerState state, int mask, bool all, int bondGain,
        ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights, double skillPointValue)
    {
        var rules = data.TurnRules!;
        mask &= rules.OrdinarySupportMask;
        if (mask == 0 || rules.SkillRating is not { } rating) return 0;
        var levels = state.GetSection(PioneerStateSection.HintLevels);
        var aptitudes = state.GetSection(PioneerStateSection.InheritanceAptitudes);
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        var discount = FutureSkillRating.PurchaseDiscount(state.GetSection(PioneerStateSection.CharacterEffects));
        ref readonly var current = ref state.Header;
        var count = BitOperations.PopCount((uint)mask);
        var value = 0.0;
        while (mask != 0)
        {
            var position = BitOperations.TrailingZeroCount((uint)mask);
            mask &= mask - 1;
            var (attributes, probability, skillRating) = rules.Hints!.ExpectedReward(position, Math.Min(100, bonds[position] + bondGain),
                levels, aptitudes, current.Resources, discount, rating);
            value += probability * (AttributeGain(current, forecasts, DisplayGains(current.Resources, attributes), default, weights)
                + attributes.SkillPoints * skillPointValue) + skillRating / 7;
        }
        return all ? value : value / count;
    }

    static double BondGain(int mask, ReadOnlySpan<int> bonds, int gain)
    {
        var value = 0.0;
        for (var i = 0; i < bonds.Length; i++)
            if ((mask & (1 << i)) != 0)
                value += 1.85 * Math.Clamp(80 - bonds[i], 0, gain);
        return value;
    }

    PioneerConstructionPlan ChooseConstructionPlan(PioneerState state)
    {
        var current = CurrentPlanning(state);
        var rules = data.Construction!;
        var master = rules.Data;
        var turnRules = data.TurnRules!;
        var header = state.Header;
        var skillPointValue = SkillPointValue(state);
        var forecast = ProjectedAttributeGain(header);
        var deck = data.TrainingStart!.Deck;
        var weights = new double[5];
        var growth = data.TrainingStart.Environment.GrowthPercent;
        var growthPercent = new[] { growth.Speed, growth.Stamina, growth.Power, growth.Guts, growth.Wisdom };
        for (var i = 0; i < weights.Length; i++)
        {
            var raw = i switch
            {
                0 => new TrainingValues(12, 0, 0, 0, 0, 0, 0),
                1 => new TrainingValues(0, 12, 0, 0, 0, 0, 0),
                2 => new TrainingValues(0, 0, 12, 0, 0, 0, 0),
                3 => new TrainingValues(0, 0, 0, 12, 0, 0, 0),
                _ => new TrainingValues(0, 0, 0, 0, 12, 0, 0),
            };
            weights[i] = .25 + AttributeGain(header, forecast, DisplayGains(header.Resources, raw), default)
                * (1 + growthPercent[i] / 100.0) / 12;
        }

        var values = new Dictionary<int, (double Ordinary, double Camp, double Island)>();
        foreach (var edge in master.FacilityUpgrades)
        {
            if (!current.Facilities.Any(facility => facility.FacilityId == edge.FacilityId && facility.StateId == edge.CurrentStateId))
                continue;
            var target = turnRules.Facility(edge.FacilityId, edge.TargetStateId);
            var previous = turnRules.Facility(edge.FacilityId, edge.CurrentStateId);
            var (a, b, c) = Estimate(target);
            var (x, y, z) = Estimate(previous);
            values.Add(edge.Id, (a - x, b - y, c - z));
        }

        var first = current.Turn == 3 ? 3 : current.Turn + 1;
        var ordinary = new int[79];
        var camps = new int[79];
        var islands = new int[79];
        for (var turn = 77; turn >= first; turn--)
        {
            ordinary[turn] = ordinary[turn + 1];
            camps[turn] = camps[turn + 1];
            islands[turn] = islands[turn + 1];
            if (turn is 74 or 76 || turnRules.ScheduledRace(turn, header.ScheduleVariant) is not null) continue;
            if (IsCamp(turn)) camps[turn]++;
            else
            {
                ordinary[turn]++;
                if (turn >= 6) islands[turn]++;
            }
        }
        return rules.BestPlan(current, Score);

        (double Ordinary, double Camp, double Island) Estimate(TrainingFacility facility)
        {
            if (facility.Level == 0) return default;
            var index = facility.Id - 101;
            var primary = index < 5 ? weights[index] : weights.Average();
            var specialtyCards = facility.Id == 106 ? 0 : deck.Count(card => card.TrainingCommandId == TrainingFacilityRules.Command(facility.Id));
            var ordinaryValue = 0.0;
            var campValue = 0.0;
            var islandValue = facility.Id == 106 ? 18 : 8 * primary;
            var islandTargets = 0.0;
            for (var i = 0; i < 5; i++)
                if (TrainingFacilityRules.HasTarget(facility.Id, i + 1)) islandTargets += weights[i];
            foreach (var effect in facility.Effects)
            {
                switch (effect.Type, effect.Condition)
                {
                    case (1, _):
                        var weight = effect.Target == 30 ? skillPointValue : weights[effect.Target - 1];
                        campValue += .55 * weight * effect.Value;
                        islandValue += 2.0 * weight * effect.Value;
                        break;
                    case (2, 0):
                        islandValue += effect.Value * .29 * islandTargets;
                        break;
                    case (2, 2):
                        campValue += effect.Value * .22 * primary;
                        break;
                    case (103, _):
                        campValue += effect.Value * .005;
                        islandValue += effect.Value * .018;
                        break;
                    case (104, 0):
                        // Effect rows are exclusive by the number of rainbow facilities.
                        islandValue += effect.Value * (effect.Target switch { 2 => .38, 3 => .26, 4 => .08, 5 => .015, _ => 0 }) * islandTargets;
                        break;
                    case (201, 0):
                        ordinaryValue += effect.Value * .028 * specialtyCards * primary;
                        break;
                    case (201, 2):
                        campValue += effect.Value * .065 * specialtyCards * primary;
                        break;
                    case (204, 9):
                        ordinaryValue += effect.Value * .001;
                        campValue += effect.Value * .001;
                        break;
                    case (205, 9):
                        ordinaryValue += effect.Value * .08;
                        campValue += effect.Value * .05;
                        islandValue += effect.Value * .09;
                        break;
                }
            }
            return (ordinaryValue, campValue, islandValue);
        }

        double Score(ReadOnlySpan<FacilityUpgrade> steps)
        {
            var cost = 0;
            var score = 0.0;
            foreach (var step in steps)
            {
                cost += step.Cost;
                var threshold = current.IslandTrainingThresholds[0] >= cost
                    ? current.IslandTrainingThresholds[0] : current.IslandTrainingThresholds[^1];
                if (threshold < cost) break;
                // shortcut: project construction timing from observed development throughput, preserving camp deferral.
                var ready = Math.Min(78, first + Math.Max(0, threshold - current.DevelopmentPoints + 69) / 70);
                if (ready is >= 37 and <= 40) ready = 41;
                var value = values[step.Id];
                score += value.Ordinary * ordinary[ready] + value.Camp * camps[ready] + value.Island * islands[ready] * .24;
            }
            return score;
        }
    }
}
