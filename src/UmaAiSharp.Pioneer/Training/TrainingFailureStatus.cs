using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public static class TrainingFailureStatus
{
    public static int StatusBias(IEnumerable<int> effects, ImmutableArray<int> raceOnlyEffects = default)
    {
        var bias = 0;
        foreach (var effect in effects)
            bias += StatusBias(effect, raceOnlyEffects);
        return bias;
    }

    public static int StatusBias(int effect, ImmutableArray<int> raceOnlyEffects = default) => effect switch
    {
        10 or 13 => -2,
        11 => -4,
        6 or 12 => 2,
        1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 25 or 26 or 27 or 31 or 40 => 0,
        _ when !raceOnlyEffects.IsDefaultOrEmpty && raceOnlyEffects.BinarySearch(effect) >= 0 => 0,
        _ => throw new UnknownMechanismException($"角色效果 {effect} 缺少已确认的训练失败率规则。")
    };
}
