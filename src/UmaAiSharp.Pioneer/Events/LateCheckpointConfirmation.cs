using System.Collections.Frozen;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Events;

public sealed record SettledCheckpointDisplay(int CharacterStoryId, int CheckpointResult, bool Confirmed);

public static class LateCheckpointConfirmation
{
    static readonly FrozenSet<int> ObservedStories = new[]
    {
        501003408, 501004407, 501007407, 501008407, 501009431, 501010407, 501011406, 501014407,
        501016411, 501017409, 501018407, 501021407, 501023407, 501024407, 501026407, 501027408,
        501032408, 501035407, 501037410, 501038406, 501041407, 501045407, 501050407, 501052408,
        501054407, 501056407, 501060407, 501061407, 501062408, 501063407, 501066407, 501067406,
        501068410, 501073105, 501077407, 501103105, 501135105, 501141105,
    }.ToFrozenSet();

    // Import only after the checkpoint response's complete rewards are already reflected in state.
    // The current partial checkpoint calculator cannot itself certify that boundary.
    public static SettledCheckpointDisplay ObserveAppliedBoundary(int turn, int phase, int resultState,
        int eventId, int storyId, int playTiming, int choiceCount)
    {
        if (turn != 61 || phase != 1 || resultState is not (1 or 2) || eventId != 3005 || playTiming != 1
            || choiceCount != 0 || !ObservedStories.Contains(storyId))
            throw new UnknownMechanismException("仅可记录末期评审已完整结算后的已观测角色显示事件边界；其他无选项事件不能视为已发奖。");
        return new(storyId, resultState, false);
    }

    public static SettledCheckpointDisplay Confirm(SettledCheckpointDisplay state, int eventId, int storyId, int choiceNumber)
    {
        if (state.Confirmed || eventId != 3005 || storyId != state.CharacterStoryId || choiceNumber != 0)
            throw new IllegalActionException("末期角色显示事件已确认，或确认身份/原始choice不符。");
        if (!ObservedStories.Contains(state.CharacterStoryId) || state.CheckpointResult is not (1 or 2))
            throw new UnknownMechanismException("没有已核验的末期评审已发奖边界。");
        return state with { Confirmed = true };
    }
}
