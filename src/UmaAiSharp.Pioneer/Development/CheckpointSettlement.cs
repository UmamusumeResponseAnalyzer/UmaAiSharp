using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Development;

public readonly record struct CheckpointResources(int DevelopmentPoints, int Vital, int MaxVital);
public readonly record struct CheckpointEventContext(int Turn, int RawPlayingState, int DeckId,
    int CheckpointType, int ResultState, int StoryId, int ChoiceNumber);
public sealed record CheckpointResourceSettlement(CheckpointResources After, int RecoveryBeforeVitalCap,
    int ActualRecovery);
public sealed record CheckpointEvaluation(int CheckpointType, int ResultState, int TargetPoints,
    ImmutableArray<CheckpointEffect> Effects);
public readonly record struct OrdinaryTrainingLevels(int Speed, int Stamina, int Power, int Guts, int Wisdom);
public readonly record struct CheckpointProgress(OrdinaryTrainingLevels TrainingLevels, int DirectorBond, bool HasAttentionEffect);
public sealed record CheckpointProgressSettlement(OrdinaryTrainingLevels TrainingLevels, int DirectorBond,
    int AddedProjectMemberCount);

public sealed class CheckpointSettlement(CheckpointMasterData master)
{
    internal CheckpointMasterData Master => master;
    public const string Version = "observed-checkpoint-resource-settlement-v1";
    public static FrozenDictionary<int, (int Type, int Result)> ObservedEventOutcomes { get; } =
        new Dictionary<int, (int, int)>
        {
            [400011004] = (1, 2), [400011008] = (2, 2), [400011012] = (3, 2),
            [400011017] = (4, 2), [400011021] = (5, 2), [400011022] = (5, 1),
        }.ToFrozenDictionary();

    public CheckpointEvaluation EvaluateReachedTargetCandidate(int turn, int rawPlayingState, int deckId,
        int checkpointType, int developmentPoints)
    {
        if (deckId != 3 || rawPlayingState != 34 || checkpointType is < 1 or > 5
            || !master.Periods.TryGetValue((deckId, checkpointType), out var period) || period.CheckPointTurn != turn)
            throw new UnknownMechanismException($"评审结果证据仅覆盖 deck=3、阶段34及 master 的第1..5期回合；收到 deck={deckId}、phase={rawPlayingState}、type={checkpointType}、turn={turn}。");
        if (developmentPoints < period.TargetPt)
            throw new UnknownMechanismException($"发展 Pt={developmentPoints} 未达到目标 {period.TargetPt}；低评价判定及完整分支尚未实现，停止模拟。");
        return new(checkpointType, 2, period.TargetPt, master.GetEffects(checkpointType, 2));
    }

    public CheckpointProgressSettlement ApplyHighEvaluationProgress(CheckpointEventContext context, CheckpointProgress before)
    {
        if (context.DeckId != 3 || context.RawPlayingState != 5 || context.ChoiceNumber != 0 || context.ResultState != 2
            || !ObservedEventOutcomes.TryGetValue(context.StoryId, out var outcome)
            || outcome != (context.CheckpointType, context.ResultState)
            || !master.Periods.TryGetValue((context.DeckId, context.CheckpointType), out var period)
            || context.Turn != period.CheckPointTurn)
            throw new UnknownMechanismException("评审训练等级及羁绊分量仅核验 deck=3、阶段5的五期高评价事件选择0；低评价及事件调度未包含。");
        var levels = before.TrainingLevels;
        if (before.DirectorBond is < 0 or > 100 || new[] { levels.Speed, levels.Stamina, levels.Power, levels.Guts, levels.Wisdom }.Any(x => x is < 1 or > 5))
            throw new InvalidDataException("评审前理事长羁绊必须为0..100，五项普通训练等级必须为1..5。");
        var addsMembers = context.CheckpointType is 2 or 4;
        var updated = addsMembers ? new OrdinaryTrainingLevels(Math.Min(5, levels.Speed + 1), Math.Min(5, levels.Stamina + 1),
            Math.Min(5, levels.Power + 1), Math.Min(5, levels.Guts + 1), Math.Min(5, levels.Wisdom + 1)) : levels;
        return new(updated, Math.Min(100, before.DirectorBond + 5 + (before.HasAttentionEffect ? 2 : 0)), addsMembers ? 3 : 0);
    }

    // Only the selected event's Pt/vital components. Other rewards and event scheduling remain separate.
    public CheckpointResourceSettlement ApplyResourcesCandidate(CheckpointEventContext context, CheckpointResources before)
    {
        if (context.DeckId != 3 || context.RawPlayingState != 5 || context.ChoiceNumber != 0
            || !ObservedEventOutcomes.TryGetValue(context.StoryId, out var outcome)
            || outcome != (context.CheckpointType, context.ResultState))
            throw new UnknownMechanismException($"评审分量结算超出证据范围：deck={context.DeckId}、phase={context.RawPlayingState}、story={context.StoryId}、choice={context.ChoiceNumber}。");
        if (!master.Periods.TryGetValue((context.DeckId, context.CheckpointType), out var period)
            || context.Turn != period.CheckPointTurn)
            throw new UnknownMechanismException($"master 没有 deck={context.DeckId}、检查点={context.CheckpointType}、turn={context.Turn} 的评审周期。");
        if (before.DevelopmentPoints < 0 || before.MaxVital <= 0 || before.Vital < 0 || before.Vital > before.MaxVital)
            throw new ArgumentException("评审前发展 Pt、体力或体力上限无效。", nameof(before));
        if ((before.DevelopmentPoints >= period.TargetPt) != (context.ResultState == 2))
            throw new UnknownMechanismException($"评审结果 {context.ResultState} 与发展 Pt={before.DevelopmentPoints}、目标={period.TargetPt} 不符合已观测关系；不自动改写结果。");

        return SettleResources(period, before);
    }

    internal CheckpointResourceSettlement SettleResources(PlanningPeriod period, CheckpointResources before)
    {
        var bonus = master.PointBonuses[period.CheckPointType];
        var surplus = Math.Max(0, before.DevelopmentPoints - period.TargetPt);
        var recovery = (int)Math.Min(bonus.AddValueMax, (long)surplus * bonus.PtRate / 1000);
        var actualRecovery = Math.Min(recovery, before.MaxVital - before.Vital);
        return new(before with { DevelopmentPoints = 0, Vital = before.Vital + actualRecovery }, recovery, actualRecovery);
    }
}
