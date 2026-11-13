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

    PioneerRuntimeAction? SuggestAction(PioneerState state)
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
        var forecast = ProjectedAttributeGain(current);
        Span<double> attributeWeights = stackalloc double[5];
        attributeWeights.Fill(1);
        Span<double> ordinaryUses = stackalloc double[5];
        trainingProjection?.ValueAttributes(state, attributeWeights, ordinaryUses);
        var nextCamp = turn is >= 34 and <= 36 ? 37 : turn is >= 58 and <= 60 ? 61 : 0;
        var inCamp = CampCommands(current);
        var daysToCamp = nextCamp == 0 ? 0 : nextCamp - turn;
        var actionAfterThisTurn = turn + 1;
        while (actionAfterThisTurn < 78 && rules.ScheduledRace(actionAfterThisTurn) is not null) actionAfterThisTurn++;
        var mood = current.Motivation;
        var hasMigraine = state.GetSection(PioneerStateSection.CharacterEffects).Contains(5);
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        var tickets = current.Tickets;
        var nextThreshold = int.MaxValue;
        foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
            if (threshold > points) nextThreshold = Math.Min(nextThreshold, threshold);
        var futureIslandTurns = 0;
        // shortcut: estimate the next ticket issue from typical development points per turn.
        var lastIslandTurn = nextThreshold == int.MaxValue ? 72
            : Math.Min(72, turn + Math.Max(1, (nextThreshold - points + 59) / 60));
        for (var day = turn + 1; tickets > 0 && day <= lastIslandTurn; day++)
            if (!IsCamp(day) && rules.ScheduledRace(day) is null) futureIslandTurns++;
        var deferredConstruction = current.DeferredConstruction;
        var islandValue = 0.0;
        var islandReserve = 0.0;
        var islandTicketConflict = false;
        var islandPoints = 0;
        var islandAvailable = false;
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
            if (TryPreviewIsland(state, out var island))
            {
                islandAvailable = true;
                islandPoints = IslandDevelopmentPoints(state);
                islandValue = AttributeGain(current, forecast, island.Common, island.Scenario, attributeWeights)
                    + SkillValue(island.Common.SkillPoints + island.Scenario.SkillPoints)
                    + Math.Min(1, turnsLeft / 38.0) * BondGain(63, bonds, 10);
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
                var colors = System.Numerics.BitOperations.PopCount((uint)rainbow);
                // shortcut: value a better rainbow board and the chance to replace a low-vitality turn.
                var premium = 31 * Math.Clamp(eligible / 2.0 - colors, 0, 2);
                islandReserve = Math.Min(240, (.66 * islandValue + .85 * Math.Max(0, vital - 40) + premium)
                    * Math.Clamp((futureIslandTurns - tickets + 1) / 2.0, 0, 1));
                islandTicketConflict = futureIslandTurns < tickets && (nextThreshold != int.MaxValue || turn >= 65);
            }

            foreach (var candidate in Candidates(state))
            {
                var action = candidate.Action;
                var score = double.NegativeInfinity;
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
                        var raceValue = AttributeGain(current, forecast, new(speed, stamina, power, guts, wisdom, 0, 0), default, attributeWeights) / 5;
                        score = raceValue + SkillValue(skill) + Progress(racePoints, false)
                            + Vitality(Math.Max(0, vital - 15), racePoints) - Vitality(vital, 0);
                        var shortage = fanTarget - current.Fans;
                        if (shortage > 0 && expectedFans > 0 && turn <= fanDeadline)
                            score += Math.Min(shortage, expectedFans) / (double)shortage
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
                            score += 20 + (slots <= needed ? 1000 : 120.0 / (1 + slots - needed));
                        }
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
                        var benefit = AttributeGain(current, forecast, gains.Common, gains.Scenario, attributeWeights)
                            + SkillValue(gains.Common.SkillPoints + gains.Scenario.SkillPoints)
                            + Vitality(afterVital, development) - Vitality(vital, 0)
                            + Math.Min(1, turnsLeft / 38.0) * BondGain(placement.SupportMask, bonds, 7)
                            + (firstFriend ? Motivation(1) + 12 : 0)
                            + (placement.Director && turn < 55 && current.DirectorBond < 60 ? 3 : 0)
                            + Progress(development, false);
                        if (!inCamp && trainingProjection is { } projection)
                            benefit += projection.UpgradeValue(state, index, ordinaryUses[index], attributeWeights);
                        var failure = preview.FailureRate / 100.0;
                        score = (1 - failure) * benefit - failure * (18 + Motivation(1) + failure * (45 + 2 * Motivation(1)));
                        break;
                    case PioneerActionKind.IslandTraining when islandAvailable:
                        score = islandValue + Vitality(vital, islandPoints) - Vitality(vital, 0) + Progress(islandPoints, true);
                        break;
                    case PioneerActionKind.Rest:
                        var camp = action.CommandId == RestRules.CampCommand;
                        score = Vitality(Math.Min(maxVital, vital + (camp ? 40 : rules.ExpectedOrdinaryRest)), 0)
                            - Vitality(vital, 0) + (camp ? Motivation(1) : 0) + Progress(0, false);
                        break;
                    case PioneerActionKind.Treat:
                        score = Math.Min(25, turnsLeft) + Vitality(Math.Min(maxVital, vital + 20), 0)
                            - Vitality(vital, 0) + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing when action.CommandId == 301:
                        score = (Motivation(1) + Motivation(2)) / 2
                            + (Vitality(Math.Min(maxVital, vital + 10), 0) - Vitality(vital, 0)) / 2 + Progress(0, false);
                        break;
                    case PioneerActionKind.Outing:
                        var position = action.SupportPosition - 1;
                        var friend = data.TrainingStart!.HomeActions!.Friends.Single(item => item.Position == action.SupportPosition);
                        var step = (current.FriendOutings >> (position * 3)) & 7;
                        var (recoveredVital, maximum, reward) = FriendOuting(data.TrainingStart.Deck[position], friend.CharacterId, step);
                        var (recovered, _) = VitalRules.Apply(vital, maxVital, recoveredVital, maximum);
                        var tucker = friend.CharacterId == 9049;
                        var outingDevelopment = tucker && turn is >= 8 and <= 60 ? 80 : 0;
                        var specialty = tucker && actionAfterThisTurn == turn + 1 && turnsLeft > 0
                            ? TuckerOutingSpecialty(step) * .09 : 0;
                        // The outing's extra recovery option is worth saving while normal recovery can already cover this board.
                        var waitValue = Math.Max(0, recoveredVital - (maxVital - vital)) * Math.Min(.16, turnsLeft / 70.0);
                        score = AttributeGain(current, forecast, DisplayGains(current.Resources, reward), default, attributeWeights)
                            + Vitality(recovered, outingDevelopment) - Vitality(vital, 0)
                            + Motivation(1) + specialty + Progress(outingDevelopment, false) - waitValue
                            + Math.Min(1, turnsLeft / 45.0) * maximum;
                        break;
                }
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
            var urgency = inCamp && (actionAfterThisTurn is >= 38 and <= 40 or >= 62 and <= 64) ? .18 : 0;
            return Math.Min(1, turnsLeft / 4.0) * ((1.03 + urgency) * Math.Min(value, 50)
                + .47 * Math.Clamp(value - 50, 0, 25) + .13 * Math.Max(0, value - 75) + campReserve);
        }

        double Motivation(int increase)
        {
            if (hasMigraine || mood >= 5 || turnsLeft == 0) return 0;
            // shortcut: estimate the useful training turns before another recovery opportunity.
            var productiveTurns = Math.Min(turnsLeft, turn <= 12 ? 8 : 6);
            return Math.Min(5 - mood, increase) * productiveTurns * (turn <= 12 ? 4.5 : 3.2);
        }

        double Progress(int gain, bool spendTicket)
        {
            var crosses = turn is >= 5 and <= 60 && points + gain >= nextThreshold && (!inCamp || !deferredConstruction);
            var assessment = gain == 0 ? 0 : Math.Min(gain, Math.Max(0, target - points))
                * (daysToReview <= 3 ? .18 : .09);
            var issue = crosses && (tickets == 0 || spendTicket) ? 27 : 0;
            var carry = 0.0;
            if (spendTicket && !crosses)
                carry = islandReserve;
            if (!spendTicket && islandAvailable && (crosses || islandTicketConflict))
                carry += Math.Min(105, .65 * islandValue);
            return assessment + issue - carry;
        }
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
    {
        var values = current.Resources;
        var limits = current.Limits;
        return Value(values.Speed, limits.Speed, common.Speed + scenario.Speed) * (weights.IsEmpty ? 1 : weights[0])
            + Value(values.Stamina, limits.Stamina, common.Stamina + scenario.Stamina) * (weights.IsEmpty ? 1 : weights[1])
            + Value(values.Power, limits.Power, common.Power + scenario.Power) * (weights.IsEmpty ? 1 : weights[2])
            + Value(values.Guts, limits.Guts, common.Guts + scenario.Guts) * (weights.IsEmpty ? 1 : weights[3])
            + Value(values.Wisdom, limits.Wisdom, common.Wisdom + scenario.Wisdom) * (weights.IsEmpty ? 1 : weights[4]);

        double Value(int before, int cap, int gain)
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
