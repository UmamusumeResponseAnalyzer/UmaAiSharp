using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public readonly record struct PioneerTrainingHistoryFrequency(int CampPeriods, int CommandId, int OtherExecutions, int Count);

public sealed record PioneerTrainingHistoryPrefix(int Turn, ImmutableArray<int> UnobservedExecutions,
    ImmutableArray<int> ObservedOrdinarySuccesses, int? UnseenFriendMask = null,
    ImmutableArray<TemporarySupportGrant>? ObservedGrants = null,
    ImmutableArray<PioneerStoryOccurrence>? ObservedStories = null,
    GoldShipTrainingBoost? GoldShipBoost = null,
    ImmutableArray<PioneerRaceHistory>? RaceHistory = null, ImmutableArray<PioneerRaceReservation>? RaceReservations = null)
{
    public bool NeedsEstimate => UnobservedExecutions.Any(count => count != 0);
}

public sealed class PioneerTrainingHistoryDistribution
{
    readonly (int Minimum, int[] Cumulative)[] commands = new (int, int[])[5];

    internal long SharedMemoryBytes => 256 + commands.Sum(command => 64L + 4L * command.Cumulative.Length);

    public PioneerTrainingHistoryDistribution(PioneerStart.TrainingTurn panel, PioneerTrainingHistoryPrefix history,
        ImmutableArray<PioneerTrainingHistoryFrequency> frequencies)
    {
        if (frequencies.IsDefaultOrEmpty) throw new InvalidDataException("规则资源缺少训练历史拟合数据。");
        var checkpoints = panel.CheckpointResults!.Value;
        var boosts = (checkpoints.Length >= 2 && checkpoints[1] == 2 ? 1 : 0)
            + (checkpoints.Length >= 4 && checkpoints[3] == 2 ? 1 : 0);
        var campPeriods = (history.Turn > 37 ? 1 : 0) + (history.Turn > 61 ? 1 : 0);
        foreach (var command in panel.Commands)
        {
            var index = TrainingBaseTable.CommandIndex(command.CommandId);
            var ordinary = TrainingBaseTable.OrdinaryCommand(command.CommandId);
            var level = panel.Camp?.OrdinaryLevels[index] ?? command.Level;
            var observed = history.ObservedOrdinarySuccesses[index];
            var unknown = history.UnobservedExecutions[index];
            var fromLevel = (level - 1 - boosts) * 4;
            var minimum = Math.Max(observed, fromLevel);
            var maximum = Math.Min(observed + unknown, command.ExecutionCount);
            if (level < 5) maximum = Math.Min(maximum, fromLevel + 3);
            if (minimum > maximum)
                throw new InvalidDataException($"训练 {ordinary} 的等级 {level}、评审进度和执行次数不相容。");
            var weights = new int[maximum - minimum + 1];
            var total = 0;
            for (var count = minimum; count <= maximum; count++)
            {
                var other = unknown - (count - observed);
                // One pseudocount preserves feasible outcomes absent from the selected training windows.
                total = checked(total + 1 + frequencies.Where(row => row.CampPeriods == campPeriods
                    && row.CommandId == ordinary && row.OtherExecutions == other).Sum(row => row.Count));
                weights[count - minimum] = total;
            }
            commands[index] = (minimum, weights);
        }
    }

    internal void Draw(Span<int> successes, ref SimulationRandom random)
    {
        for (var index = 0; index < commands.Length; index++)
        {
            var (minimum, cumulative) = commands[index];
            var draw = cumulative.Length == 1 ? 0 : random.NextInt(cumulative[^1]);
            successes[index] = minimum + cumulative.AsSpan().IndexOfAnyInRange(draw + 1, int.MaxValue);
        }
    }
}
