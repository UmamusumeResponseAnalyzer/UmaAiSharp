using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public static class TrainingBaseTable
{
    public const string Version = "jp-ordinary-training-base-inference-v1";

    public static int OrdinaryCommand(int command) => command switch
    {
        3601 => 101, 3602 => 105, 3603 => 102, 3604 => 103, 3605 => 106,
        101 or 105 or 102 or 103 or 106 => command,
        _ => throw new UnknownMechanismException($"未实现的训练指令{command}。")
    };

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
