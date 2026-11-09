using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public static class TrainingLevelRules
{
    public const string Version = "pioneer-training-level-trigger-v2";

    static ReadOnlySpan<int> EventIds => [211024, 211027, 211025, 211026, 211028];

    public static int TargetIndex(int eventId)
    {
        var index = EventIds.IndexOf(eventId);
        return index >= 0 ? index : throw new UnknownMechanismException($"事件{eventId}不是已识别的单项训练升级。");
    }

    // A triggered upgrade waits behind earlier events; it does not change the visible decision or level.
    public static int? AfterSuccessfulOrdinary(int targetIndex, int currentLevel, int? successfulCount)
    {
        if ((uint)targetIndex >= 5 || currentLevel is < 1 or > 5 || successfulCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(successfulCount), "升级触发要求训练索引0..4、等级1..5，以及明确为正数或未知的成功普通次数。");
        if (currentLevel == 5) return 0;
        if (successfulCount is not { } count) return null;
        return count % 4 == 0 ? EventIds[targetIndex] : 0;
    }
}
