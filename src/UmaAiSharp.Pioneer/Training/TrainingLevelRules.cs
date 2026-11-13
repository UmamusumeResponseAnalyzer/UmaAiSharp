using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Pioneer.Training;

public static class TrainingLevelRules
{
    public const string Version = "pioneer-training-level-trigger-v3";
    public const int StoryId = 400000005;
    internal const int SuccessesPerLevel = 4;

    static ReadOnlySpan<int> Commands => [101, 105, 102, 103, 106];
    static ReadOnlySpan<int> ProtocolIds => [211024, 211027, 211025, 211026, 211028];

    public static int ObservedCommand(in PioneerEvent current)
    {
        var index = ProtocolIds.IndexOf(current.EventId);
        return current is { StoryId: StoryId, CharacterId: 0, PlayTiming: 6 } && index >= 0 ? Commands[index]
            : throw new UnknownMechanismException($"故事{current.StoryId}的训练升级缺少明确的协议目标，event_id={current.EventId}。");
    }

    internal static PioneerEvent Reveal(int commandId)
    {
        var index = Commands.IndexOf(commandId);
        return new(ProtocolIds[index], 0, StoryId, 6);
    }

    // A triggered upgrade waits behind earlier events; it does not change the visible decision or level.
    public static int? AfterSuccessfulOrdinary(int targetIndex, int currentLevel, int? successfulCount)
    {
        if ((uint)targetIndex >= 5 || currentLevel is < 1 or > 5 || successfulCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(successfulCount), "升级触发要求训练索引0..4、等级1..5，以及明确为正数或未知的成功普通次数。");
        if (currentLevel == 5) return 0;
        if (successfulCount is not { } count) return null;
        return count % SuccessesPerLevel == 0 ? Commands[targetIndex] : 0;
    }
}
