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

    public bool HasFirstStepAdvantage(in PioneerState state, in PioneerRuntimeAction left, in PioneerRuntimeAction right)
    {
        if (left == right || left.Kind is not (PioneerActionKind.Train or PioneerActionKind.IslandTraining
            or PioneerActionKind.Rest or PioneerActionKind.Outing)
            || right.Kind is not (PioneerActionKind.Train or PioneerActionKind.IslandTraining or PioneerActionKind.Rest
                or PioneerActionKind.Outing or PioneerActionKind.Race))
            return false;
        Span<FirstStepValue> values = stackalloc FirstStepValue[2];
        _ = SuggestAction(state, values, left, right);
        var (preferred, alternative) = (values[0], values[1]);
        var gap = preferred.Score - alternative.Score;
        var scale = .15 * Math.Max(Math.Abs(preferred.Reward), Math.Abs(alternative.Reward));
        // Count a next-turn placement lead when the rest of the action already has the better value.
        var nextBoardGap = preferred.NextBoardBenefit - alternative.NextBoardBenefit;
        return gap > 0 && (gap >= scale || nextBoardGap > 0 && gap >= nextBoardGap);
    }

    readonly record struct FirstStepValue(double Score, double Reward, double NextBoardBenefit);

    PioneerRuntimeAction? SuggestAction(PioneerState state, Span<FirstStepValue> comparison = default,
        PioneerRuntimeAction left = default, PioneerRuntimeAction right = default)
    {
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
        attributeForecast.Fill(ProjectedAttributeGain(current));
        attributeWeights.Fill(1);
        Span<double> ordinaryUses = stackalloc double[5];
        trainingProjection?.ValueAttributes(state, attributeWeights, ordinaryUses, attributeForecast);
        var nextCamp = turn is >= 34 and <= 36 ? 37 : turn is >= 58 and <= 60 ? 61 : 0;
        var inCamp = CampCommands(current);
        var daysToCamp = nextCamp == 0 ? 0 : nextCamp - turn;
        var actionAfterThisTurn = turn + 1;
        while (actionAfterThisTurn < 78 && rules.ScheduledRace(actionAfterThisTurn) is not null) actionAfterThisTurn++;
        var campAhead = inCamp && actionAfterThisTurn < 78 && IsCamp(actionAfterThisTurn);
        var mood = current.Motivation;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var hasMigraine = effects.Contains(5);
        var positiveThinking = effects.Contains(25);
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
        var tuckerMask = rules.TuckerMask;
        var tuckerSlot = BitOperations.TrailingZeroCount((uint)tuckerMask);
        var ticketValue = 0.0;
        var careerCounts = state.GetSection(PioneerStateSection.CareerRaceCounts);
        var fanTarget = 0;
        var fanDeadline = 73;
        foreach (var goal in rules.CareerGoals)
            if (goal.Deadline >= turn && goal.Deadline < fanDeadline && current.Fans < goal.FanTarget)
                (fanTarget, fanDeadline) = (goal.FanTarget, goal.Deadline);
        for (var day = turn + 1; day < fanDeadline; day++)
            if (rules.ScheduledRace(day) is { Target: true } goalRace)
            {
                var required = goalRace.RequiredFans;
                if (required <= current.Fans) continue;
                fanTarget = required;
                fanDeadline = day - 1;
                break;
            }
        try
        {
            if (nextThreshold != int.MaxValue && !deferredConstruction)
            {
                var firstUse = actionAfterThisTurn;
                while (firstUse <= 72 && (IsCamp(firstUse) || rules.ScheduledRace(firstUse) is not null)) firstUse++;
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
                    + SkillValue(island.Common.SkillPoints + island.Scenario.SkillPoints)
                    + Math.Min(1, turnsLeft / 38.0) * BondGain(rules.OrdinarySupportMask, bonds, 10 + (current.HasCharm ? 2 : 0));
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

            foreach (var candidate in Candidates(state))
            {
                var action = candidate.Action;
                if (!comparison.IsEmpty && action != left && action != right) continue;
                var score = double.NegativeInfinity;
                var reward = 0.0;
                var nextBoardBenefit = 0.0;
                switch (action.Kind)
                {
                    case PioneerActionKind.Race:
                    {
                        var race = rules.RaceOption(turn, action.CommandId);
                        if (race.Target) return action;
                        var (gain, skill) = OptionalRaceReward(race.Grade, rules.RaceRewardPercent);
                        var expectedFans = (int)((long)race.FirstPlaceFans * rules.FanRewardPercent * 108 / 10000);
                        var racePoints = rules.WholeRun && turn >= periods[0].PlanningTurn && checkpoint < periods.Length
                            ? RaceDevelopmentPoints(state, target: false) : 0;
                        var speed = Display(current.Resources.Speed, gain);
                        var stamina = Display(current.Resources.Stamina, gain);
                        var power = Display(current.Resources.Power, gain);
                        var guts = Display(current.Resources.Guts, gain);
                        var wisdom = Display(current.Resources.Wisdom, gain);
                        var raceValue = AttributeGain(current, attributeForecast, new(speed, stamina, power, guts, wisdom, 0, 0), default, attributeWeights) / 5;
                        reward = raceValue + SkillValue(skill) + Progress(racePoints, false);
                        var shortage = fanTarget - current.Fans;
                        if (shortage > 0 && expectedFans > 0 && turn <= fanDeadline)
                            reward += Math.Min(shortage, expectedFans) / (double)shortage
                                * (18 + 160.0 / (1 + fanDeadline - turn));
                        for (var i = 0; i < careerCounts.Length; i++)
                        {
                            var goal = rules.CareerGoals[i];
                            var needed = goal.RaceCount - careerCounts[i];
                            if (needed <= 0 || turn < goal.FirstTurn || turn > goal.Deadline || race.Grade > goal.MaximumGrade) continue;
                            var slots = 1;
                            for (var day = turn + 1; day <= goal.Deadline; day++)
                            {
                                if (rules.ScheduledRace(day) is { } scheduled)
                                {
                                    if (scheduled.Grade <= goal.MaximumGrade) slots++;
                                    continue;
                                }
                                if (rules.OptionalRaces(day, current.RaceGrade).Any(option => option.Grade <= goal.MaximumGrade)) slots++;
                            }
                            reward += 20 + (slots <= needed ? 1000 : 120.0 / (1 + slots - needed));
                        }
                        score = reward + Vitality(Math.Max(0, vital - 15), racePoints) - Vitality(vital, 0);
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
                        var firstFriend = (placement.SupportMask & current.UnseenFriendMask) != 0;
                        var bondGain = data.TrainingDeck!.TrainingBondBonus(placement.SupportMask) + (current.HasCharm ? 2 : 0);
                        var hints = placement.HintMask & rules.OrdinarySupportMask;
                        var hintBond = hints == 0 ? 0
                            : BondGain(hints, bonds, 7 + bondGain + 5) - BondGain(hints, bonds, 7 + bondGain);
                        if (hintBond > 0 && !AllCampHints(state, action.CommandId))
                            hintBond /= BitOperations.PopCount((uint)hints);
                        var tuckerValue = TuckerTrainingValue(placement.SupportMask, bondGain, island: false, attributeForecast, attributeWeights,
                            out nextBoardBenefit);
                        var upgrade = 0.0;
                        if (!inCamp && trainingProjection is { } projection)
                            upgrade = projection.UpgradeValue(state, index, ordinaryUses[index], attributeWeights);
                        reward = AttributeGain(current, attributeForecast, gains.Common, gains.Scenario, attributeWeights)
                            + SkillValue(gains.Common.SkillPoints + gains.Scenario.SkillPoints)
                            + Math.Min(1, turnsLeft / 38.0)
                                * (BondGain(placement.SupportMask & rules.OrdinarySupportMask, bonds, 7 + bondGain) + hintBond)
                            + (firstFriend ? Motivation(1) + 12 : 0)
                            + (placement.Director && turn < 55 && current.DirectorBond < 60 ? 3 : 0)
                            + tuckerValue + upgrade
                            + Progress(development, false);
                        var benefit = reward + Vitality(afterVital, development) - Vitality(vital, 0);
                        var failure = preview.FailureRate / 100.0;
                        var mild = 18 + mildMoodLoss * moodStepValue;
                        var severe = 63 + severeMoodLoss * moodStepValue;
                        score = (1 - failure) * benefit - failure * ((1 - failure) * mild + failure * severe);
                        break;
                    case PioneerActionKind.IslandTraining when islandAvailable:
                        reward = islandValue + Progress(islandPoints, true)
                            + TuckerTrainingValue(islandParticipants, current.HasCharm ? 2 : 0, island: true, attributeForecast, attributeWeights,
                                out nextBoardBenefit);
                        score = reward + Vitality(vital, islandPoints) - Vitality(vital, 0);
                        break;
                    case PioneerActionKind.Rest:
                        var camp = action.CommandId == RestRules.CampCommand;
                        var rested = camp ? Vitality(Math.Min(maxVital, vital + 40), 0)
                            : (1 - rules.LongRestProbability) * Vitality(Math.Min(maxVital, vital + 50), 0)
                                + rules.LongRestProbability * Vitality(Math.Min(maxVital, vital + 70), 0);
                        reward = rested - Vitality(vital, 0) + (camp ? Motivation(1) : 0);
                        score = reward + Progress(0, false);
                        break;
                    case PioneerActionKind.Treat:
                        score = Math.Min(25, turnsLeft) + Vitality(Math.Min(maxVital, vital + 20), 0)
                            - Vitality(vital, 0) + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing when action.CommandId == 301:
                        reward = (Motivation(1) + Motivation(2)) / 2
                            + (Vitality(Math.Min(maxVital, vital + 10), 0) - Vitality(vital, 0)) / 2;
                        score = reward + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing:
                        var position = action.SupportPosition - 1;
                        var friend = data.TrainingStart!.HomeActions!.Friends.Single(item => item.Position == action.SupportPosition);
                        var step = (current.FriendOutings >> (position * 3)) & 7;
                        var (recoveredVital, maximum, outingReward) = FriendOuting(data.TrainingStart.Deck[position], friend.CharacterId, step);
                        var (recovered, _) = VitalRules.Apply(vital, maxVital, recoveredVital, maximum);
                        var tucker = friend.CharacterId == 9049;
                        var outingDevelopment = tucker && turn is >= 8 and <= 60 ? 80 : 0;
                        var specialty = tucker && actionAfterThisTurn == turn + 1 && turnsLeft > 0
                            ? TuckerOutingSpecialty(step) * .09 : 0;
                        nextBoardBenefit = specialty;
                        // The outing's extra recovery option is worth saving while normal recovery can already cover this board.
                        var waitValue = Math.Max(0, recoveredVital - (maxVital - vital)) * Math.Min(.16, turnsLeft / 70.0);
                        reward = AttributeGain(current, attributeForecast, DisplayGains(current.Resources, outingReward), default, attributeWeights)
                            + Vitality(recovered, outingDevelopment) - Vitality(vital, 0)
                            + Motivation(1) + specialty
                            + Math.Min(1, turnsLeft / 45.0) * maximum;
                        score = reward + Progress(outingDevelopment, false) - waitValue;
                        break;
                }
                if (!comparison.IsEmpty)
                    comparison[action == left ? 0 : 1] = new(score, reward, nextBoardBenefit);
                if (score <= best) continue;
                best = score;
                bestAction = action;
            }
        }
        catch (UnknownMechanismException) { return null; }
        return bestAction;

        double Vitality(double value, int development)
        {
            if (recovery is not null)
                value = Math.Min(maxVital, value + Math.Min(recovery.AddValueMax,
                    (long)Math.Max(0, points + development - target) * recovery.PtRate / 1000));
            var campReserve = daysToCamp > 0 && actionAfterThisTurn <= nextCamp
                ? .70 / daysToCamp * Math.Min(value, 90) : 0;
            // Preserve the next camp board by carrying enough vitality to cover an ordinary camp training.
            var campBoard = campAhead ? .85 * Math.Clamp(value - 45, 0, 25) : 0;
            return Math.Min(1, turnsLeft / 4.0) * (1.03 * Math.Min(value, 50)
                + .47 * Math.Clamp(value - 50, 0, 25) + .13 * Math.Max(0, value - 75) + campReserve + campBoard);
        }

        double Motivation(int increase)
        {
            return (MotivationRules.Apply(mood, increase, positiveThinking, hasMigraine).Value - mood) * moodStepValue;
        }

        double TuckerTrainingValue(int participants, int bonusBond, bool island, ReadOnlySpan<int> forecasts,
            ReadOnlySpan<double> weights, out double nextBoard)
        {
            nextBoard = 0;
            if ((participants & tuckerMask) == 0) return 0;
            var tuckerBond = state.GetSection(PioneerStateSection.SupportBonds)[tuckerSlot];
            var first = (state.Header.UnseenFriendMask & tuckerMask) != 0;
            var gain = (island ? 7 : 4) + bonusBond;
            var value = first ? 0 : AttributeGain(state.Header, forecasts,
                new(0, (int)Math.Round(rules.ExpectedTuckerRepeat(island)), 0, 0, 0, 0, 0), default, weights);
            if (!first && (state.Header.UnlockedFriendMask & tuckerMask) == 0 && turn < 25 && tuckerBond < 60)
            {
                // Value the expected time saved before the first outing, accounting for unlocks below 60.
                var steps = (60 - tuckerBond + 3) / 4;
                var remaining = Math.Max(0, (60 - tuckerBond - gain + 3) / 4);
                var before = ExpectedUnlockTurns(steps);
                var after = ExpectedUnlockTurns(remaining);
                var firstOuting = FriendOuting(data.TrainingStart!.Deck[tuckerSlot], 9049, 0);
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
            nextBoard = trainingProjection?.TuckerSpecialtyValue(state, participants & rules.OrdinarySupportMask,
                island ? 10 + bonusBond : 7 + bonusBond, forecasts, weights) ?? 0;
            return value + nextBoard;
        }

        double Progress(int gain, bool spendTicket)
        {
            var crosses = turn is >= 5 and <= 60 && points + gain >= nextThreshold && (!inCamp || !deferredConstruction);
            var assessment = gain == 0 ? 0 : Math.Min(gain, Math.Max(0, target - points))
                * (daysToReview <= 3 ? .18 : .09);
            var issue = crosses && (tickets == 0 || spendTicket) ? Math.Min(32, ticketValue) : 0;
            var carry = 0.0;
            if (islandAvailable)
            {
                var nextIssue = nextThreshold == int.MaxValue ? 72
                    : Math.Min(72, turn + Math.Max(1, (nextThreshold - points + 59) / 60));
                var chances = 0;
                for (var day = turn + 1; day <= nextIssue; day++)
                    if (!IsCamp(day) && rules.ScheduledRace(day) is null) chances++;
                if (spendTicket && !crosses)
                    carry = otherIslandValue * Math.Clamp((chances - tickets + 1) / 2.0, 0, 1);
                else if (!spendTicket && (crosses || chances < tickets))
                    carry = Math.Min(105, .65 * islandValue);
            }
            return assessment + issue - carry;
        }
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
        for (var i = 0; i < 5; i++)
        {
            if ((built & (1 << i)) == 0) continue;
            var basis = TrainingBaseTable.GetOrdinary(TrainingFacilityRules.Command(101 + i), 1);
            value += AttributeGain(header, forecasts, DisplayGains(header.Resources, basis), default, weights) + SkillValue(basis.SkillPoints);
        }
        var house = (built & 32) != 0;
        if (built == 0) return 0;
        var bond = BondGain(data.TurnRules!.OrdinarySupportMask, state.GetSection(PioneerStateSection.SupportBonds), 10);
        return .30 * value + (house ? 6 : 0) + .22 * bond * Math.Min(1, (77 - header.Turn) / 38.0);
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

    static double SkillValue(int points) => .025 * points;

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
            if (turn is 74 or 76 || turnRules.ScheduledRace(turn) is not null) continue;
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
                        var weight = effect.Target == 30 ? .05 : weights[effect.Target - 1];
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
