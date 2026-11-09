using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Pioneer.Races;

public readonly record struct RacePointContext(int Turn, int RawPlayingState, int DeckId, int StoryId,
    int ChoiceNumber, bool IsTargetRace, int CompletedCheckpointType, int CompletedCheckpointResult);

public sealed class PioneerRacePointGain(CheckpointMasterData master)
{
    public int CalculateObservedEvent(RacePointContext context)
    {
        if (context.DeckId != 3 || context.RawPlayingState != 5 || context.StoryId != 400011105 || context.ChoiceNumber != 0)
            throw new UnknownMechanismException("比赛发展 Pt 仅核验 deck=3、阶段5、事件400011105的选择0；不决定比赛结果或事件调度。");
        var bonus = 0;
        if (context.CompletedCheckpointType == 0)
        {
            if (context.CompletedCheckpointResult != 0 || context.Turn != 12 || !context.IsTargetRace)
                throw new UnknownMechanismException("首次评审前仅核验回合12的目标赛发展 Pt；其他分支尚未覆盖。");
        }
        else
        {
            var type = context.CompletedCheckpointType;
            if (type is < 1 or > 4 || context.CompletedCheckpointResult != 2
                || !master.Periods.TryGetValue((3, type), out var previous)
                || !master.Periods.TryGetValue((3, type + 1), out var next)
                || context.Turn <= previous.CheckPointTurn || context.Turn > next.CheckPointTurn)
                throw new UnknownMechanismException("比赛发展 Pt 仅核验第1..4期高评价之后、下一次评审之前的实际比赛事件。");
            var effects = master.GetEffects(type, 2).Where(x => x.EffectType == 202 && x.EffectValue1 == 9).ToArray();
            if (effects.Length != 1 || effects[0].EffectValue2 is not (5 or 10 or 15))
                throw new UnknownMechanismException($"检查点{type}的发展 Pt 加成行超出已核验范围。");
            bonus = effects[0].EffectValue2;
        }
        return checked(20 * (100 + bonus) / 100 + (context.IsTargetRace ? 40 : 0));
    }
}
