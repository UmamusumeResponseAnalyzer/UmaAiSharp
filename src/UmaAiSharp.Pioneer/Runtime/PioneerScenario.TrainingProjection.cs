using System.Collections.Frozen;
using System.Numerics;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    readonly record struct TrainingProspect(TrainingValues Gains, TrainingPlacement Best, TrainingPlacement Second,
        double Opportunity, int Level);

    sealed class TrainingProjection
    {
        sealed record TrainingCalendar(int[] OrdinaryDays, int[] OrdinaryIndex, int[] CampDays, int[] FixedAttributeRewards);
        readonly FrozenDictionary<int, TrainingCalendar> calendars;
        public long SharedMemoryBytes => 4096L + 1500L * calendars.Count;
        readonly bool hasIslandLevelEffects;
        readonly int[] reviewLevels = new int[79];
        readonly int[] trainingReviewTurns;
        readonly int[] expectedTickets = new int[79];
        readonly int[] evaluationPercent = new int[79];

        public TrainingProjection(PioneerRuntimeData data)
        {
            var rules = data.TurnRules!;
            var start = data.TrainingStart!;
            var periods = start.PlanningPeriods;
            var thresholds = data.Construction!.Data.TicketThresholds;
            hasIslandLevelEffects = start.Deck.Any(card => card.UniqueEffects.Any(effect => effect.Type == 111));
            trainingReviewTurns = [.. periods.Where(period => period.CheckPointType is 2 or 4)
                .Select(period => period.CheckPointTurn).Order()];
            calendars = rules.ScheduleVariants.ToFrozenDictionary(variant => variant, variant => CompileCalendar(rules, variant));
            for (var day = 77; day > 0; day--)
            {
                reviewLevels[day] = trainingReviewTurns.Count(turn => turn < day);
                var completed = periods.Count(period => period.CheckPointTurn < day);
                evaluationPercent[day] = completed == 0 ? 0 : data.Checkpoints!.Master.GetEffects(completed, 2)
                    .Single(effect => effect is { EffectType: 200, EffectValue1: 0 }).EffectValue2;
                var futureTickets = 0;
                for (var i = completed; i < periods.Length; i++)
                {
                    if (periods[i].PlanningTurn <= day) continue;
                    var first = periods[i].PlanningTurn == 3 ? 3 : periods[i].PlanningTurn + 1;
                    futureTickets += thresholds.Count(row => row.DeckId == periods[i].DeckId
                        && row.TurnMin <= first && first <= row.TurnMax);
                }
                expectedTickets[day] = futureTickets + (day <= 64 ? 2 : 0);
            }
        }

        static TrainingCalendar CompileCalendar(PioneerTurnRules rules, int variant)
        {
            var ordinary = Enumerable.Range(1, 72).Where(day => !IsCamp(day) && rules.ScheduledRace(day, variant) is null).ToArray();
            var index = new int[79];
            var count = 0;
            for (var day = 1; day < index.Length; day++)
            {
                if (count < ordinary.Length && ordinary[count] == day) count++;
                index[day] = count;
            }
            var camps = new int[80];
            var fixedRewards = new int[80];
            fixedRewards[78] = 10 * rules.RaceRewardPercent / 100 + 5;
            for (var day = 77; day > 0; day--)
            {
                var race = rules.ScheduledRace(day, variant);
                var camp = day is not (74 or 76) && IsCamp(day) && race is null;
                camps[day] = camps[day + 1] + (camp ? 1 : 0);
                fixedRewards[day] = (day is 74 or 76 ? 10 * rules.RaceRewardPercent / 100
                    : race is { Target: true } ? 3 * rules.RaceRewardPercent / 100 : 0) + fixedRewards[day + 1];
            }
            return new(ordinary, index, camps, fixedRewards);
        }

        public void ValueAttributes(PioneerState state, bool inCamp, Span<double> weights, Span<double> ordinaryUses,
            Span<int> forecasts, Span<TrainingProspect> prospects)
        {
            ref readonly var current = ref state.Header;
            var turn = current.Turn;
            var calendar = calendars[current.ScheduleVariant];
            var ordinary = calendar.OrdinaryDays.Length - calendar.OrdinaryIndex[turn];
            var camp = calendar.CampDays[turn + 1];
            var fixedGains = FixedAttributeGain(current) + calendar.FixedAttributeRewards[turn + 1];
            var values = current.Resources;
            var caps = current.Limits;
            ReadOnlySpan<int> stats = [values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom];
            ReadOnlySpan<int> limits = [caps.Speed, caps.Stamina, caps.Power, caps.Guts, caps.Wisdom];
            var levels = state.GetSection(PioneerStateSection.TrainingLevels);
            var bonds = state.GetSection(PioneerStateSection.SupportBonds);
            var data = state.Data;
            var board = data.TurnRules!.BoardRules(state, inCamp);
            var environment = data.TrainingStart!.Environment with
            { Current = default, Motivation = current.Motivation, Fans = current.Fans, GoldShipBoost = null };
            var forecastMultiplier = 1 + (current.EvaluationTrainingPercent + evaluationPercent[77]) / 200.0;
            var tickets = Math.Min(ordinary, RemainingTickets(state));
            // Reserve turns for tickets, recovery and optional races before allocating training.
            var ordinaryBudget = Math.Max(0, ordinary - tickets) * .80;
            Span<double> need = stackalloc double[5];
            Span<double> uses = stackalloc double[5];
            Span<double> efficiencies = stackalloc double[5];
            Span<double> planned = stackalloc double[5];
            Span<double> reserves = stackalloc double[5];
            Span<TrainingValues> campGains = stackalloc TrainingValues[5];
            Span<int> cards = stackalloc int[6];
            var islandTotal = default(TrainingValues);
            // shortcut: project the current built layout and refresh the expected yield when facilities change.
            if (tickets > 0)
                foreach (var input in board.ForecastIslands)
                {
                    var island = data.Model.Island(input, data.TrainingDeck!, environment, levels, bonds, board.IslandFacilities);
                    islandTotal = Add(islandTotal, Add(island.UncappedCommon, island.UncappedScenario));
                }
            for (var i = 0; i < 5; i++)
            {
                // Use a representative level for the allocation, then date each marginal upgrade separately.
                var level = Math.Min(5, levels[i] + (reviewLevels[77] - reviewLevels[turn]) / 2 + ordinary / 26);
                prospects[i] = ProductiveTraining(data, board.ForecastBoards, environment, bonds, i, level, cards);
                reserves[i] = fixedGains + Math.Max(0, 73 - turn)
                    + tickets * Attribute(islandTotal, i) / (double)PioneerTurnRules.ForecastBoardCount;
                need[i] = Math.Max(0, RawEquivalent((limits[i] * 19 + 19) / 20) - RawEquivalent(stats[i]) - reserves[i]);
                efficiencies[i] = Math.Max(1, Attribute(prospects[i].Gains, i) * forecastMultiplier);
                planned[i] = need[i];
            }
            var trainingBudget = ordinaryBudget + camp * .92;
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var totalUses = 0.0;
                for (var i = 0; i < 5; i++)
                {
                    uses[i] = planned[i] / efficiencies[i];
                    totalUses += uses[i];
                }
                var budgetScale = Math.Min(1, trainingBudget / Math.Max(1, totalUses));
                for (var i = 0; i < 5; i++)
                {
                    var other = 0.0;
                    for (var j = 0; j < 5; j++)
                        if (j != i)
                            other += uses[j] * Attribute(prospects[j].Gains, i) * forecastMultiplier;
                    planned[i] = Math.Max(0, need[i] - other * budgetScale);
                }
            }
            var pressure = 0.0;
            var totalPlanned = 0.0;
            for (var i = 0; i < 5; i++)
            {
                uses[i] = planned[i] / efficiencies[i];
                totalPlanned += uses[i];
            }
            for (var i = 0; i < 5; i++)
            {
                var chance = prospects[i].Opportunity;
                var share = uses[i] / Math.Max(1, totalPlanned);
                ordinaryUses[i] = Math.Min(ordinaryBudget * share, ordinary * chance);
                var supply = ordinaryBudget * (.20 + .55 * chance) * efficiencies[i];
                if (camp > 0)
                {
                    var cell = prospects[i].Best;
                    var count = TrainingBoard.Positions(cell.SupportMask, cards);
                    var preview = data.Model.Preview(environment, 3601 + i, 5, cell.GuestCount, 0, 0, 0,
                        cards[..count], bonds, data.TrainingDeck!, board.Camp[i]).Gains;
                    campGains[i] = Add(preview.UncappedCommon, preview.UncappedScenario);
                    supply += camp * .25 * Attribute(campGains[i], i);
                }
                weights[i] = (planned[i] + 32) / (supply + 55);
                pressure += weights[i] / 5;
            }
            var balance = .36 + .43 * Math.Clamp((turn - 32) / 32.0, 0, 1);
            for (var i = 0; i < 5; i++)
                weights[i] = Math.Clamp(1 + balance * (weights[i] - pressure) / (pressure + .35), .45, 1.9);
            for (var i = 0; i < 5; i++)
            {
                var projected = reserves[i];
                for (var j = 0; j < 5; j++)
                {
                    projected += ordinaryUses[j] * Attribute(prospects[j].Gains, i) * forecastMultiplier;
                    if (camp > 0) projected += camp * .92 * uses[j] / Math.Max(1, totalPlanned) * Attribute(campGains[j], i);
                }
                forecasts[i] = (int)projected;
            }
        }

        static TrainingProspect ProductiveTraining(PioneerRuntimeData data, ReadOnlySpan<TrainingBoard> boards,
            in TrainingEnvironment environment, ReadOnlySpan<int> bonds, int training, int level, Span<int> cards)
        {
            var command = TrainingFacilityRules.Command(101 + training);
            Span<int> totals = stackalloc int[PioneerTurnRules.ForecastBoardCount];
            var best = -1;
            var second = -1;
            var bestGain = default(TrainingValues);
            var secondGain = default(TrainingValues);
            for (var i = 0; i < boards.Length; i++)
            {
                var cell = boards[i][training];
                var count = TrainingBoard.Positions(cell.SupportMask, cards);
                var gains = data.Model.Preview(environment, command, level, cell.GuestCount, 0, 0, 0,
                    cards[..count], bonds, data.TrainingDeck!, null).Gains.UncappedCommon;
                totals[i] = Total(gains);
                if (best < 0 || totals[i] > totals[best])
                {
                    (second, secondGain) = (best, bestGain);
                    (best, bestGain) = (i, gains);
                }
                else if (second < 0 || totals[i] > totals[second]) (second, secondGain) = (i, gains);
            }
            var sum = Add(bestGain, secondGain);
            var opportunities = 0;
            foreach (var total in totals)
                if (total * 2 >= totals[best] + totals[second]) opportunities++;
            return new(new(sum.Speed / 2, sum.Stamina / 2, sum.Power / 2, sum.Guts / 2, sum.Wisdom / 2,
                sum.Vital / 2, sum.SkillPoints / 2), boards[best][training], boards[second][training],
                (double)opportunities / boards.Length, level);
        }

        int RemainingTickets(PioneerState state)
        {
            ref readonly var current = ref state.Header;
            var tickets = current.Tickets + expectedTickets[current.Turn];
            foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
                if (threshold > current.DevelopmentPoints) tickets++;
            return tickets;
        }

        public (int NextOutingTurn, int OutingTurns, int CampTurns, int BeforeOuting, int BeforeTraining) RecoveryOutlook(
            int turn, int lastTurn, int tickets, int variant)
        {
            const int campCost = 15;
            var calendar = calendars[variant];
            var first = calendar.OrdinaryIndex[turn];
            var outingTurns = calendar.OrdinaryDays.Length - first;
            var nextOuting = outingTurns == 0 ? 0 : calendar.OrdinaryDays[first];
            var beforeOuting = outingTurns == 0 ? 0 : (calendar.CampDays[turn + 1] - calendar.CampDays[nextOuting]) * campCost;
            var ordinary = calendar.OrdinaryIndex[lastTurn] - first;
            var camp = calendar.CampDays[turn + 1] - calendar.CampDays[lastTurn + 1];
            // shortcut: budget one recovery cycle using representative non-wisdom training costs.
            var spent = Math.Clamp(Math.Max(0, ordinary - tickets) * 16 + (camp - 1) * campCost, 0, 50);
            return (nextOuting, outingTurns, camp, beforeOuting, spent);
        }

        public double UpgradeValue(PioneerState state, int training, double uses, ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights,
            Span<double> islandGains, double skillPointValue, in TrainingProspect prospect)
        {
            ref readonly var current = ref state.Header;
            var levels = state.GetSection(PioneerStateSection.TrainingLevels);
            var level = levels[training];
            var successes = state.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts);
            var turn = current.Turn;
            var calendar = calendars[current.ScheduleVariant];
            var ordinaryIndex = calendar.OrdinaryIndex;
            var ordinaryCalendar = calendar.OrdinaryDays;
            var first = ordinaryIndex[turn];
            var days = ordinaryCalendar.Length - first;
            if (level == 5 || successes.IsEmpty || days == 0) return 0;

            var untilUpgrade = TrainingLevelRules.SuccessesPerLevel - 1 - successes[training] % TrainingLevelRules.SuccessesPerLevel;
            if (uses < untilUpgrade || uses == untilUpgrade && !hasIslandLevelEffects) return 0;

            var data = state.Data;
            var environment = data.TrainingStart!.Environment with
            {
                Current = current.Resources, Limits = current.Limits, Motivation = current.Motivation,
                Fans = current.Fans, GoldShipBoost = null,
            };
            var bonds = state.GetSection(PioneerStateSection.SupportBonds);
            var command = TrainingFacilityRules.Command(101 + training);
            var board = state.TurnBoard!;
            Span<int> positions = stackalloc int[6];
            Span<int> islandLevels = stackalloc int[5];
            var tickets = 0;
            var highest = level;
            if (hasIslandLevelEffects)
                foreach (var other in levels)
                    highest = Math.Max(highest, other);
            if (hasIslandLevelEffects && level == highest && board.ForecastIslands.Length > 0)
                tickets = RemainingTickets(state);
            var value = 0.0;
            // Both paths have the same level again after the next success at each milestone.
            for (var before = untilUpgrade; before <= uses && level < 5; before += TrainingLevelRules.SuccessesPerLevel, level++)
            {
                var useful = Math.Min(1, uses - before);
                var nextDay = useful == 0 ? 73 : ordinaryCalendar[first + ExpectedDayOffset(before + .5)];
                var lower = level + reviewLevels[nextDay] - reviewLevels[turn];
                if (useful > 0 && lower < 5)
                {
                    var percent = Math.Max(current.EvaluationTrainingPercent, evaluationPercent[nextDay]);
                    value += useful * .5 * (OrdinaryGain(prospect.Best, lower, percent, positions, bonds, forecasts, weights)
                        + OrdinaryGain(prospect.Second, lower, percent, positions, bonds, forecasts, weights));
                }
                if (tickets == 0) continue;
                var from = before == 0 ? Math.Max(6, turn + 1) : Math.Max(6, ordinaryCalendar[first + ExpectedDayOffset(before - .5)] + 1);
                var capReview = reviewLevels[turn] + 5 - level;
                var through = capReview <= trainingReviewTurns.Length
                    ? Math.Min(nextDay - 1, trainingReviewTurns[capReview - 1]) : nextDay - 1;
                var islandDays = ordinaryIndex[through] - ordinaryIndex[from - 1];
                if (islandDays <= 0) continue;
                var islandLevel = level + reviewLevels[from] - reviewLevels[turn];
                ref var islandGain = ref islandGains[islandLevel - 1];
                if (double.IsNaN(islandGain))
                {
                    // Island support effects use the maximum of the five ordinary levels.
                    islandLevels.Fill(islandLevel);
                    var sum = 0.0;
                    foreach (var input in board.ForecastIslands)
                    {
                        islandLevels[0] = islandLevel;
                        var baseline = data.Model.Island(input, data.TrainingDeck!, environment, islandLevels, bonds, board.IslandFacilities);
                        islandLevels[0]++;
                        var upgraded = data.Model.Island(input, data.TrainingDeck!, environment, islandLevels, bonds, board.IslandFacilities);
                        sum += UpgradeGain(current, baseline, upgraded, forecasts, weights, skillPointValue);
                    }
                    islandGain = sum / board.ForecastIslands.Length;
                }
                value += Math.Min(islandDays, (double)tickets * islandDays / days) * islandGain;
            }
            return value;

            int ExpectedDayOffset(double ordinal) => (int)Math.Min(days - 1, ordinal * days / uses);

            double OrdinaryGain(TrainingPlacement cell, int lower, int percent, Span<int> cards, ReadOnlySpan<int> supportBonds,
                ReadOnlySpan<int> attributeForecasts, ReadOnlySpan<double> attributeWeights)
            {
                var count = TrainingBoard.Positions(cell.SupportMask, cards);
                var baseline = data.Model.Preview(environment, command, lower, cell.GuestCount, percent, 0, 0,
                    cards[..count], supportBonds, data.TrainingDeck!, null).Gains;
                var upgraded = data.Model.Preview(environment, command, lower + 1, cell.GuestCount, percent, 0, 0,
                    cards[..count], supportBonds, data.TrainingDeck!, null).Gains;
                return UpgradeGain(state.Header, baseline, upgraded, attributeForecasts, attributeWeights, skillPointValue);
            }
        }

        static double UpgradeGain(in PioneerStateHeader current, in TrainingNumericResult baseline, in TrainingNumericResult upgraded,
            ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights, double skillPointValue)
        {
            var common = Difference(upgraded.Common, baseline.Common);
            var scenario = Difference(upgraded.Scenario, baseline.Scenario);
            return AttributeGain(current, forecasts, common, scenario, weights)
                + (common.SkillPoints + scenario.SkillPoints) * skillPointValue + .47 * common.Vital;
        }

        public double TuckerSpecialtyValue(PioneerState state, int mask, int bondGain, ReadOnlySpan<int> forecasts,
            ReadOnlySpan<double> weights, ReadOnlySpan<TrainingProspect> prospects, double skillPointValue)
        {
            var value = 0.0;
            var bonds = state.GetSection(PioneerStateSection.SupportBonds);
            while (mask != 0)
            {
                var slot = BitOperations.TrailingZeroCount((uint)mask);
                mask &= mask - 1;
                var card = state.Data.TrainingStart!.Deck[slot];
                var index = TrainingBaseTable.CommandIndex(card.TrainingCommandId);
                var distribution = state.TurnBoard!.ForecastPlacement.Members[slot].Distribution;
                var weight = (double)distribution.Weights[index];
                var other = distribution.TotalWeight - weight;
                var boosted = 1.6 * weight;
                var extraAttendance = boosted / (boosted + other) - weight / (weight + other);
                var prospect = prospects[index];
                var basis = TrainingBaseTable.GetOrdinary(card.TrainingCommandId, prospect.Level);
                // shortcut: use the representative productive-board premium, reduced before friendship.
                var fraction = bonds[slot] + bondGain >= 80 ? .7 : .5;
                var extra = Difference(prospect.Gains, basis);
                value += extraAttendance * fraction * (AttributeGain(state.Header, forecasts,
                    DisplayGains(state.Header.Resources, extra), default, weights) + extra.SkillPoints * skillPointValue);
            }
            return value;
        }

        static TrainingValues Add(in TrainingValues a, in TrainingValues b) => new(a.Speed + b.Speed, a.Stamina + b.Stamina,
            a.Power + b.Power, a.Guts + b.Guts, a.Wisdom + b.Wisdom, a.Vital + b.Vital, a.SkillPoints + b.SkillPoints);

        static TrainingValues Difference(in TrainingValues a, in TrainingValues b) => new(a.Speed - b.Speed, a.Stamina - b.Stamina,
            a.Power - b.Power, a.Guts - b.Guts, a.Wisdom - b.Wisdom, a.Vital - b.Vital, a.SkillPoints - b.SkillPoints);

        static int Total(TrainingValues value) => value.Speed + value.Stamina + value.Power + value.Guts + value.Wisdom;

        static int Attribute(TrainingValues values, int index) => index switch
        {
            0 => values.Speed, 1 => values.Stamina, 2 => values.Power, 3 => values.Guts, _ => values.Wisdom,
        };
    }
}
