using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Pioneer.Events;

public static class RestRules
{
    public const string Version = "jp-pioneer-rest-components-v2";
    public const int OrdinaryCommand = 701, CampCommand = 3606;
    public const int OrdinaryAvailable = 1, CampAvailable = 2;

    public static bool IsStory(int storyId, int characterId) => characterId > 0
        && storyId - (500000000L + characterId * 1000L) is 701 or 718;

    public static int Recovery(in PioneerEvent current, int characterId)
    {
        if (current.CharacterId != characterId || current.PlayTiming != 6 || !IsStory(current.StoryId, characterId))
            throw new InvalidDataException("休息事件的角色、故事或时机与当前育成角色不一致。");
        return current.StoryId % 1000 == 701 ? 70 : 50;
    }

    public static void ValidateEffects(ReadOnlySpan<int> effects)
    {
        foreach (var effect in effects)
            if (effect is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 25 or 26 or 27 or 31 or 36))
                throw new UnknownMechanismException($"角色效果{effect}在休息时的解除或附加后果尚未接入。");
    }
}
