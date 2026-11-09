using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public enum TrainingFailureStage { TrainingDecision, FailureChoice, NextTurnEventsRequired }
public enum ResolvedFailureBranch { StaminaMinusFiveMotivationMinusOne }
public readonly record struct TrainingFailureEvent(int EventId, int CharacterId, int StoryId, int PlayTiming);
public readonly record struct TrainingFailureState(int CardId, int Turn, int Motivation, TrainingValues Resources,
    int StaminaExecutionCount, TrainingFailureStage Stage, TrainingFailureEvent? PendingEvent);

public static class TrainingFailureSettlement
{
    public const string Version = "jp-pioneer-observed-stamina-failure-v2";

    // The failure and its event identity are explicit conditional inputs; this method does not draw either.
    public static TrainingFailureState BeginResolvedFailure(in TrainingFailureState before, ReadOnlySpan<int> supportBonds,
        int commandId, TrainingFailureEvent failure)
    {
        if (before.Stage != TrainingFailureStage.TrainingDecision || before.PendingEvent is not null)
            throw new IllegalActionException("当前不是训练决策，不能重复执行失败尝试。");
        if (before.Turn < 1 || before.Motivation is < 1 or > 5 || before.StaminaExecutionCount < 0
            || before.Resources.Vital < 0 || before.Resources.SkillPoints < 0)
            throw new InvalidDataException("失败前的回合、资源或执行次数无效。");
        if (supportBonds.Length != 6 || supportBonds.ContainsAnyExcept(100))
            throw new UnknownMechanismException("已见失败的六张支援均满羁绊；未满羁绊的失败后果尚未核验。");
        var supported = (before.CardId, commandId, failure.EventId, failure.CharacterId, failure.StoryId, failure.PlayTiming) switch
        {
            (104501, 3602, 211087, 0, 400011112, 6) => true,
            (104503, 105, 7043, 1045, 501045713, 6) => true,
            _ => false
        };
        if (!supported) throw new UnknownMechanismException("该角色、训练与失败事件组合未核验；不推测失败事件选择规则。");
        return before with { StaminaExecutionCount = checked(before.StaminaExecutionCount + 1),
            Stage = TrainingFailureStage.FailureChoice, PendingEvent = failure };
    }

    // Choice 1 has more than one displayed result. The already identified branch is mandatory.
    public static (TrainingFailureState State, bool ConsumePositiveThinking) ResolveChoice(in TrainingFailureState before,
        int eventId, int choiceNumber, ResolvedFailureBranch branch, bool positiveThinking)
    {
        if (before.Stage != TrainingFailureStage.FailureChoice || before.PendingEvent?.EventId != eventId)
            throw new IllegalActionException("失败选择与当前待处理事件不匹配，或已被消费。");
        if (choiceNumber != 1 || branch != ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne)
            throw new UnknownMechanismException("仅核验选择1中明确识别的耐力-5、干劲-1分支；其他展示或概率未知。");
        if (before.Resources.Stamina is < 5 or > 1200 || before.Motivation <= 1)
            throw new UnknownMechanismException("失败惩罚在低属性、低干劲或1200以上的结算边界尚未核验。");
        var motivation = MotivationRules.Apply(before.Motivation, -1, positiveThinking, false);
        return (before with { Turn = checked(before.Turn + 1), Motivation = motivation.Value,
            Resources = before.Resources with { Stamina = before.Resources.Stamina - 5 },
            PendingEvent = null, Stage = TrainingFailureStage.NextTurnEventsRequired }, motivation.ConsumePositiveThinking);
    }
}
