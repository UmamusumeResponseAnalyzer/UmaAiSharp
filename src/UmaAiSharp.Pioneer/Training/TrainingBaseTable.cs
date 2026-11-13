using UmaAiSharp.Core;
using System.Collections.Immutable;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Pioneer.Training;

public static class TrainingBaseTable
{
    public const string Version = "jp-ordinary-training-base-inference-v1";

    static ReadOnlySpan<int> OrdinaryCommands => [101, 105, 102, 103, 106];

    public static int CommandIndex(int command) => command is >= 3601 and <= 3605
        ? command - 3601 : OrdinaryCommands.IndexOf(command);

    public static int OrdinaryCommand(int command) => CommandIndex(command) is >= 0 and var index
        ? OrdinaryCommands[index] : throw new UnknownMechanismException($"未实现的训练指令{command}。");

    public static TrainingValues FromFacilityRows(int command, ImmutableArray<ForecastFacilityBase> bases,
        ImmutableArray<TrainingFacility> facilities)
    {
        var levels = facilities.ToDictionary(value => value.Id, value => value.Level);
        var values = new Dictionary<int, int>();
        foreach (var row in bases.Where(row => row.Command == command))
        {
            if (row.RequiredFacility != 0 && levels[row.RequiredFacility] == 0) continue;
            var target = row.Target;
            if (target is not (1 or 2 or 3 or 4 or 5 or 10 or 30)) throw new UnknownMechanismException($"未知训练基础target={target}。");
            if (values.TryGetValue(target, out var previous) && previous != row.Value)
                throw new UnknownMechanismException("同一训练基础分量存在冲突master条目。");
            values[target] = row.Value;
        }
        if (values.Count == 0) throw new UnknownMechanismException($"command={command}没有训练基础master条目。");
        return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3), values.GetValueOrDefault(4),
            values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
    }

    // Level 1 agrees with scenario 11 master. Higher levels are packet-derived candidates,
    // including explicit counterexamples to the handoff MujintoStore table; see the audit report.
    public static TrainingValues GetOrdinary(int commandId, int level) => (commandId, level) switch
    {
        (101, 1) => new(12, 0, 1, 0, 0, -20, 6),
        (101, 2) => new(13, 0, 1, 0, 0, -21, 6),
        (101, 3) => new(14, 0, 1, 0, 0, -22, 6),
        (101, 4) => new(15, 0, 2, 0, 0, -24, 6),
        (101, 5) => new(16, 0, 3, 0, 0, -25, 6),
        (105, 1) => new(0, 9, 0, 5, 0, -20, 6),
        (105, 2) => new(0, 10, 0, 5, 0, -21, 6),
        (105, 3) => new(0, 11, 0, 5, 0, -22, 6),
        (105, 4) => new(0, 12, 0, 6, 0, -24, 6),
        (105, 5) => new(0, 13, 0, 7, 0, -26, 6),
        (102, 1) => new(0, 3, 11, 0, 0, -20, 6),
        (102, 2) => new(0, 3, 12, 0, 0, -21, 6),
        (102, 3) => new(0, 4, 13, 0, 0, -22, 6),
        (102, 4) => new(0, 4, 14, 0, 0, -24, 6),
        (102, 5) => new(0, 5, 15, 0, 0, -26, 6),
        (103, 1) => new(2, 0, 2, 10, 0, -20, 6),
        (103, 2) => new(2, 0, 2, 11, 0, -21, 6),
        (103, 3) => new(2, 0, 2, 12, 0, -22, 6),
        (103, 4) => new(3, 0, 2, 13, 0, -24, 6),
        (103, 5) => new(3, 0, 3, 14, 0, -26, 6),
        (106, 1) => new(5, 0, 0, 0, 8, 5, 6),
        (106, 2) => new(5, 0, 0, 0, 9, 5, 6),
        (106, 3) => new(5, 0, 0, 0, 10, 5, 6),
        (106, 4) => new(6, 0, 0, 0, 11, 5, 6),
        (106, 5) => new(7, 0, 0, 0, 12, 5, 6),
        _ => throw new UnknownMechanismException($"普通训练基础值没有command_id={commandId}, level={level}的候选条目。")
    };
}
