using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    sealed class TrainingProjection
    {
        public const int SharedMemoryBytes = 4096;
        const int BoardCount = 96;
        readonly TrainingValues[,] gains = new TrainingValues[5, 5];
        readonly double[,] opportunities = new double[5, 5];
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
                    Array.Sort(previews, static (a, b) => Total(b).CompareTo(Total(a)));
                    var productive = previews.AsSpan(0, BoardCount / 3);
                    var n = productive.Length;
                    var sum = default(TrainingValues);
                    foreach (var preview in productive)
                        sum = new(sum.Speed + preview.Speed, sum.Stamina + preview.Stamina, sum.Power + preview.Power,
                            sum.Guts + preview.Guts, sum.Wisdom + preview.Wisdom, sum.Vital + preview.Vital, 0);
                    gains[training, level - 1] = new(sum.Speed / n, sum.Stamina / n, sum.Power / n,
                        sum.Guts / n, sum.Wisdom / n, sum.Vital / n, 0);
                    var threshold = (double)Total(sum) / n;
                    var effective = 0;
                    foreach (var preview in previews)
                        if (Total(preview) >= threshold) effective++;
                    opportunities[training, level - 1] = (double)effective / BoardCount;
                }
            }
        }

        public void ValueAttributes(PioneerState state, Span<double> weights, Span<double> ordinaryUses)
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
        }

        public double UpgradeValue(PioneerState state, int training, double uses, ReadOnlySpan<double> weights)
        {
            var level = state.GetSection(PioneerStateSection.TrainingLevels)[training];
            var successes = state.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts);
            var turn = state.Header.Turn;
            var first = ordinaryIndex[turn];
            var days = ordinaryCalendar.Length - first;
            if (level == 5 || successes.IsEmpty || days == 0 || uses == 0) return 0;

            var next = TrainingLevelRules.SuccessesPerLevel - successes[training] % TrainingLevelRules.SuccessesPerLevel;
            var value = 0.0;
            // A success today brings each reachable milestone forward by one productive ordinary training.
            for (var after = next; after <= days && level < 5; after += TrainingLevelRules.SuccessesPerLevel, level++)
            {
                // shortcut: smooth the reachability of a milestone around its expected occurrence.
                var reach = Math.Clamp((uses - after + 2) / 3, 0, 1);
                if (reach == 0) break;
                var dayOffset = (int)Math.Min(days - 1, (after - .5) * days / uses);
                var nextDay = ordinaryCalendar[first + dayOffset];
                var lower = level + reviewLevels[nextDay] - reviewLevels[turn];
                if (lower >= 5) break;
                var previous = gains[training, lower - 1];
                var upgraded = gains[training, lower];
                var multiplier = 1 + Math.Max(state.Header.EvaluationTrainingPercent, evaluationPercent[nextDay]) / 100.0;
                value += reach * (multiplier * ((upgraded.Speed - previous.Speed) * weights[0]
                    + (upgraded.Stamina - previous.Stamina) * weights[1] + (upgraded.Power - previous.Power) * weights[2]
                    + (upgraded.Guts - previous.Guts) * weights[3] + (upgraded.Wisdom - previous.Wisdom) * weights[4])
                    + .47 * (upgraded.Vital - previous.Vital));
            }
            return value;
        }

        static int Total(TrainingValues value) => value.Speed + value.Stamina + value.Power + value.Guts + value.Wisdom;

        static int Attribute(TrainingValues values, int index) => index switch
        {
            0 => values.Speed, 1 => values.Stamina, 2 => values.Power, 3 => values.Guts, _ => values.Wisdom,
        };
    }
}
