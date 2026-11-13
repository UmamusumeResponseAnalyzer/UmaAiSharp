using System.Collections.Immutable;
using System.Numerics;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    sealed class TrainingProjection
    {
        public const int SharedMemoryBytes = 8192;
        const int BoardCount = 96;
        const int ProductiveBoards = BoardCount / 3;
        readonly TrainingValues[,] gains = new TrainingValues[5, 5];
        readonly TrainingValues[,] upgrades = new TrainingValues[5, 4];
        readonly double[,] opportunities = new double[5, 5];
        readonly (int Specialty, double Weight, double OtherWeight)[] specialties = new (int, double, double)[6];
        readonly double[] islandLevelGains = new double[4];
        readonly int[] ordinaryIndex = new int[79];
        readonly int[] ordinaryCalendar;
        readonly int[] campDays = new int[80];
        readonly int[] reviewLevels = new int[79];
        readonly int[] expectedTickets = new int[79];
        readonly int[] fixedAttributeRewards = new int[80];
        readonly int[] evaluationPercent = new int[79];

        public TrainingProjection(PioneerRuntimeData data)
        {
            var rules = data.TurnRules!;
            var start = data.TrainingStart!;
            var periods = start.PlanningPeriods;
            var thresholds = data.Construction!.Data.TicketThresholds;
            ordinaryCalendar = [.. Enumerable.Range(1, 72)
                .Where(day => !IsCamp(day) && rules.ScheduledRace(day) is null)];
            var nextOrdinary = 0;
            for (var day = 1; day < ordinaryIndex.Length; day++)
            {
                if (nextOrdinary < ordinaryCalendar.Length && ordinaryCalendar[nextOrdinary] == day) nextOrdinary++;
                ordinaryIndex[day] = nextOrdinary;
            }
            fixedAttributeRewards[78] = 10 * rules.RaceRewardPercent / 100 + 5;
            for (var day = 77; day > 0; day--)
            {
                var camp = day is not (74 or 76) && IsCamp(day) && rules.ScheduledRace(day) is null;
                campDays[day] = campDays[day + 1] + (camp ? 1 : 0);
                reviewLevels[day] = periods.Count(period =>
                    period.CheckPointType is 2 or 4 && period.CheckPointTurn < day);
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
                fixedAttributeRewards[day] = (day is 74 or 76 ? 10 * rules.RaceRewardPercent / 100
                    : rules.ScheduledRace(day) is { Target: true } ? 3 * rules.RaceRewardPercent / 100 : 0)
                    + fixedAttributeRewards[day + 1];
            }

            // Use independent representative boards for planning, with friendship unlocked.
            // shortcut: reference boards use developed bonds and neutral facilities, until opportunity profiles can be conditioned by progression.
            var placement = rules.TrainingPolicyPlacement(data.CreateInitialState());
            foreach (var card in start.Deck)
            {
                if (card.TrainingCommandId == 0) continue;
                var index = TrainingBaseTable.CommandIndex(card.TrainingCommandId);
                var distribution = placement.Members[card.Position - 1].Distribution;
                var weight = distribution.Weights[index];
                specialties[card.Position - 1] = (index, weight, distribution.TotalWeight - weight);
            }
            var random = new SimulationRandom(709981);
            var boards = new TrainingBoard[BoardCount];
            Span<SupportPlacement> positions = stackalloc SupportPlacement[placement.Members.Length];
            for (var i = 0; i < boards.Length; i++) boards[i] = placement.Sample(positions, ref random);
            var environment = start.Environment with
            {
                Current = default,
                Motivation = 5,
                Fans = Math.Max(start.Environment.Fans, 60000),
                GoldShipBoost = null,
            };
            ReadOnlySpan<int> bonds = [100, 100, 100, 100, 100, 100];
            Span<int> cards = stackalloc int[6];
            var previews = new TrainingValues[BoardCount];
            var previous = new TrainingValues[BoardCount];
            var order = new int[BoardCount];
            var productivity = new int[BoardCount];
            if (start.Deck.Any(card => card.UniqueEffects.Any(effect => effect.Type == 111)))
            {
                var layout = data.Construction!.Data.FacilityStates.Where(facility => facility.FacilityLv == 1)
                    .GroupBy(facility => facility.FacilityId).Select(group => group.First())
                    .Select(facility => rules.Facility(facility.FacilityId, facility.StateId)).ToImmutableArray();
                var groups = start.Deck.GroupBy(card => card.TrainingCommandId == 0 ? 106 : 101 + TrainingBaseTable.CommandIndex(card.TrainingCommandId));
                var input = new IslandTrainingInput(environment, [], rules.IslandBase(layout), layout,
                    [.. groups.Select(group => new IslandTrainingAssignment(group.Key, [.. group], 1))]);
                var facilities = TrainingNumericKernel.CompileIslandFacilities(layout);
                Span<int> islandLevels = stackalloc int[5];
                islandLevels.Fill(1);
                var last = TotalIsland(data.Model.Island(input, data.TrainingDeck!, environment, islandLevels, bonds, facilities));
                for (var i = 0; i < islandLevelGains.Length; i++)
                {
                    islandLevels.Fill(i + 2);
                    var next = TotalIsland(data.Model.Island(input, data.TrainingDeck!, environment, islandLevels, bonds, facilities));
                    islandLevelGains[i] = (next - last) * .5;
                    last = next;
                }
            }
            for (var training = 0; training < 5; training++)
            {
                var command = TrainingFacilityRules.Command(101 + training);
                for (var level = 1; level <= 5; level++)
                {
                    for (var i = 0; i < boards.Length; i++)
                    {
                        var cell = boards[i][training];
                        var count = TrainingBoard.Positions(cell.SupportMask, cards);
                        previews[i] = data.Model.Preview(environment, command, level, cell.GuestCount, 0, 0, 0,
                            cards[..count], bonds, data.TrainingDeck!, null).Gains.UncappedCommon;
                    }
                    for (var i = 0; i < previews.Length; i++)
                    {
                        order[i] = i;
                        productivity[i] = -Total(previews[i]);
                    }
                    Array.Sort(productivity, order);
                    var sum = default(TrainingValues);
                    var increase = default(TrainingValues);
                    for (var i = 0; i < ProductiveBoards; i++)
                    {
                        var board = order[i];
                        var preview = previews[board];
                        sum = Add(sum, preview);
                        if (level > 1) increase = Add(increase, Difference(preview, previous[board]));
                    }
                    gains[training, level - 1] = new(sum.Speed / ProductiveBoards, sum.Stamina / ProductiveBoards,
                        sum.Power / ProductiveBoards, sum.Guts / ProductiveBoards, sum.Wisdom / ProductiveBoards,
                        sum.Vital / ProductiveBoards, sum.SkillPoints / ProductiveBoards);
                    if (level > 1) upgrades[training, level - 2] = increase;
                    previews.CopyTo(previous, 0);
                    var threshold = (double)Total(sum) / ProductiveBoards;
                    var effective = 0;
                    foreach (var preview in previews)
                        if (Total(preview) >= threshold) effective++;
                    opportunities[training, level - 1] = (double)effective / BoardCount;
                }
            }
        }

        public void ValueAttributes(PioneerState state, Span<double> weights, Span<double> ordinaryUses, Span<int> forecasts)
        {
            ref readonly var current = ref state.Header;
            var turn = current.Turn;
            var ordinary = ordinaryCalendar.Length - ordinaryIndex[turn];
            var camp = campDays[turn + 1];
            var fixedGains = FixedAttributeGain(current) + fixedAttributeRewards[turn + 1];
            var values = current.Resources;
            var caps = current.Limits;
            ReadOnlySpan<int> stats = [values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom];
            ReadOnlySpan<int> limits = [caps.Speed, caps.Stamina, caps.Power, caps.Guts, caps.Wisdom];
            var levels = state.GetSection(PioneerStateSection.TrainingLevels);
            var forecastMultiplier = 1 + (current.EvaluationTrainingPercent + evaluationPercent[77]) / 200.0;
            var thresholds = state.GetSection(PioneerStateSection.Thresholds);
            var tickets = (double)current.Tickets + expectedTickets[turn];
            foreach (var threshold in thresholds)
                if (threshold > current.DevelopmentPoints)
                    tickets++;
            // Reserve turns for tickets, recovery and optional races before allocating training.
            var ordinaryBudget = Math.Max(0, ordinary - tickets) * .80;
            Span<double> need = stackalloc double[5];
            Span<double> uses = stackalloc double[5];
            Span<int> expectedLevels = stackalloc int[5];
            Span<double> efficiencies = stackalloc double[5];
            Span<double> planned = stackalloc double[5];
            // shortcut: pool the raw value per remaining ticket until it can be projected by facility and rainbow count.
            var reserve = fixedGains + Math.Max(0, 73 - turn) + tickets * (40 + turn / 2.0);
            for (var i = 0; i < 5; i++)
            {
                // Use a representative level for the allocation, then date each marginal upgrade separately.
                var level = Math.Min(5, levels[i] + (reviewLevels[77] - reviewLevels[turn]) / 2 + ordinary / 26);
                expectedLevels[i] = level;
                need[i] = Math.Max(0, RawEquivalent((limits[i] * 19 + 19) / 20) - RawEquivalent(stats[i]) - reserve);
                efficiencies[i] = Math.Max(1, Attribute(gains[i, level - 1], i) * forecastMultiplier);
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
                            other += uses[j] * Attribute(gains[j, expectedLevels[j] - 1], i) * forecastMultiplier;
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
                var chance = opportunities[i, expectedLevels[i] - 1];
                var share = uses[i] / Math.Max(1, totalPlanned);
                ordinaryUses[i] = Math.Min(ordinaryBudget * share, ordinary * chance);
                var supply = ordinaryBudget * (.20 + .55 * chance) * efficiencies[i]
                    + camp * .25 * Attribute(gains[i, 4], i) * 1.4;
                weights[i] = (planned[i] + 32) / (supply + 55);
                pressure += weights[i] / 5;
            }
            var balance = .36 + .43 * Math.Clamp((turn - 32) / 32.0, 0, 1);
            for (var i = 0; i < 5; i++)
                weights[i] = Math.Clamp(1 + balance * (weights[i] - pressure) / (pressure + .35), .45, 1.9);
            for (var i = 0; i < 5; i++)
            {
                var projected = reserve;
                for (var j = 0; j < 5; j++)
                    projected += (ordinaryUses[j] + camp * .92 * uses[j] / Math.Max(1, totalPlanned))
                        * Attribute(gains[j, expectedLevels[j] - 1], i) * forecastMultiplier;
                // Blend the previous uniform projection with the deck's usable boards and common rewards.
                forecasts[i] = (int)(.70 * forecasts[i] + .30 * projected);
            }
        }

        public double UpgradeValue(PioneerState state, int training, double uses, ReadOnlySpan<double> weights)
        {
            var level = state.GetSection(PioneerStateSection.TrainingLevels)[training];
            var atHighestLevel = true;
            foreach (var otherLevel in state.GetSection(PioneerStateSection.TrainingLevels)) atHighestLevel &= level >= otherLevel;
            var successes = state.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts);
            var turn = state.Header.Turn;
            var first = ordinaryIndex[turn];
            var days = ordinaryCalendar.Length - first;
            if (level == 5 || successes.IsEmpty || days == 0) return 0;

            var remaining = TrainingLevelRules.SuccessesPerLevel - successes[training] % TrainingLevelRules.SuccessesPerLevel;
            if (uses == 0 && remaining != 1) return 0;
            var next = Math.Max(1, remaining - 1);
            var tickets = state.Header.Tickets + expectedTickets[turn];
            foreach (var threshold in state.GetSection(PioneerStateSection.Thresholds))
                if (threshold > state.Header.DevelopmentPoints)
                    tickets++;
            var value = 0.0;
            // A success today brings each reachable milestone forward by one productive ordinary training.
            for (var after = next; after <= days && level < 5; after += TrainingLevelRules.SuccessesPerLevel, level++)
            {
                // shortcut: smooth the reachability of a milestone around its expected occurrence.
                var immediate = after == next && remaining == 1;
                var reach = immediate ? 1 : Math.Clamp((uses - after + 1) / 3, 0, 1);
                if (reach == 0) break;
                var dayOffset = uses == 0 ? days - 1 : (int)Math.Min(days - 1, (after - .5) * days / uses);
                var nextDay = immediate ? turn + 1 : ordinaryCalendar[first + dayOffset];
                var lower = level + reviewLevels[nextDay] - reviewLevels[turn];
                if (lower >= 5) break;
                var gain = upgrades[training, lower - 1];
                var multiplier = 1 + Math.Max(state.Header.EvaluationTrainingPercent, evaluationPercent[nextDay]) / 100.0;
                // Only the island boards between this upgrade and the next regular use benefit.
                var islandUses = atHighestLevel && nextDay <= 72 ? Math.Min(tickets,
                    tickets * Math.Min(73 - nextDay, days / Math.Max(1, uses)) / Math.Max(1, 72 - turn)) : 0;
                var island = islandUses * islandLevelGains[lower - 1];
                var ordinaryGain = multiplier * (gain.Speed * weights[0] + gain.Stamina * weights[1]
                    + gain.Power * weights[2] + gain.Guts * weights[3] + gain.Wisdom * weights[4])
                    + SkillValue(gain.SkillPoints) + .47 * gain.Vital;
                value += reach * (Math.Min(1, uses) * ordinaryGain / ProductiveBoards + island);
            }
            return value;
        }

        public double TuckerSpecialtyValue(PioneerState state, int mask, int bondGain, ReadOnlySpan<int> forecasts, ReadOnlySpan<double> weights)
        {
            var value = 0.0;
            var bonds = state.GetSection(PioneerStateSection.SupportBonds);
            var levels = state.GetSection(PioneerStateSection.TrainingLevels);
            while (mask != 0)
            {
                var slot = BitOperations.TrailingZeroCount((uint)mask);
                mask &= mask - 1;
                var (index, weight, other) = specialties[slot];
                var boosted = 1.6 * weight;
                var extraAttendance = boosted / (boosted + other) - weight / (weight + other);
                var level = levels[index] - 1;
                var productive = gains[index, level];
                var basis = TrainingBaseTable.GetOrdinary(TrainingFacilityRules.Command(101 + index), level + 1);
                // shortcut: use the representative productive-board premium, reduced before friendship.
                var fraction = bonds[slot] + bondGain >= 80 ? .7 : .5;
                var extra = Difference(productive, basis);
                value += extraAttendance * fraction * AttributeGain(state.Header, forecasts,
                    DisplayGains(state.Header.Resources, extra), default, weights);
            }
            return value;
        }

        static TrainingValues Add(in TrainingValues a, in TrainingValues b) => new(a.Speed + b.Speed, a.Stamina + b.Stamina,
            a.Power + b.Power, a.Guts + b.Guts, a.Wisdom + b.Wisdom, a.Vital + b.Vital, a.SkillPoints + b.SkillPoints);

        static TrainingValues Difference(in TrainingValues a, in TrainingValues b) => new(a.Speed - b.Speed, a.Stamina - b.Stamina,
            a.Power - b.Power, a.Guts - b.Guts, a.Wisdom - b.Wisdom, a.Vital - b.Vital, a.SkillPoints - b.SkillPoints);

        static int Total(TrainingValues value) => value.Speed + value.Stamina + value.Power + value.Guts + value.Wisdom;
        static int TotalIsland(in TrainingNumericResult result) => Total(result.Common) + Total(result.Scenario);

        static int Attribute(TrainingValues values, int index) => index switch
        {
            0 => values.Speed, 1 => values.Stamina, 2 => values.Power, 3 => values.Guts, _ => values.Wisdom,
        };
    }
}
