using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Pioneer.Events;

public static class RestRules
{
    public const string Version = "jp-pioneer-rest-components-v1";
    public const int OrdinaryCommand = 701, CampCommand = 3606;
    public const int OrdinaryAvailable = 1, CampAvailable = 2;

    public static bool IsSupportedEvent(int eventId) => eventId is 7010 or 7058 or 7059 or 7061 or 7062;

    public static int Recovery(in PioneerEvent current, int characterId)
    {
        var gain = current.EventId switch
        {
            7010 or 7058 or 7061 => 50,
            7059 or 7062 => 70,
            _ => throw new UnknownMechanismException($"休息事件{current.EventId}的结算尚未接入。")
        };
        if (current.CharacterId != characterId || current.PlayTiming != 6
            || current.StoryId != 500000000L + current.CharacterId * 1000L + (gain == 70 ? 701 : 718))
            throw new InvalidDataException("休息事件的角色、故事或时机与当前育成角色不一致。");
        return gain;
    }

    public static void ValidateEffects(ReadOnlySpan<int> effects)
    {
        foreach (var effect in effects)
            if (effect is not (7 or 8 or 9 or 10 or 11 or 12 or 13 or 25 or 26 or 27 or 31 or 36))
                throw new UnknownMechanismException($"角色效果{effect}在休息时的解除或附加后果尚未接入。");
    }
}
