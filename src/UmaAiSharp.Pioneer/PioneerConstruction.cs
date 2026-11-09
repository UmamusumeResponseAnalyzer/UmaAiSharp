using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer;

public readonly record struct PlannedUpgrade(int Priority, int FacilityId, int PreviousStateId, int TargetStateId);
public readonly record struct ObservedFacility(int FacilityId, int StateId);

public sealed class PioneerConstructionPlan
{
    public ImmutableArray<PlannedUpgrade> Steps { get; }
    public string SemanticId { get; }

    public PioneerConstructionPlan(IEnumerable<PlannedUpgrade> steps)
    {
        Steps = [.. steps.OrderBy(x => x.Priority)];
        for (var index = 0; index < Steps.Length; index++)
            if (Steps[index].Priority != index + 1)
                throw new InvalidDataException("建设计划 priority 必须从 1 开始连续且不重复。");
        SemanticId = "plan:" + string.Join(';', Steps.Select(x =>
            FormattableString.Invariant($"{x.Priority}:{x.FacilityId}:{x.PreviousStateId}:{x.TargetStateId}")));
    }
}

public abstract record PioneerAction
{
    public sealed record SubmitConstructionPlan(PioneerConstructionPlan Plan) : PioneerAction;
}

// This is an observed scenario component, not a complete simulation root.
public sealed record PioneerDevelopmentState(int Turn, int RawPlayingState, int DevelopmentPoints,
    int PlanningCost, int DeckId, ImmutableArray<int> IslandTrainingThresholds, int? IslandTrainingTickets,
    ImmutableArray<ObservedFacility> Facilities,
    PioneerConstructionPlan Plan, ImmutableArray<int>? PendingStoryIds);

public sealed record ConstructionStepCost(PlannedUpgrade Step, int Cost, int CumulativeCost);
public sealed record ConstructionPlanAnalysis(ImmutableArray<ConstructionStepCost> Steps,
    int TotalCost, int PlanningCost, bool HasRepeatedFacility)
{
    public int UnallocatedCost => checked(PlanningCost - TotalCost);
}
public sealed record ConstructionComponentSettlement(PioneerDevelopmentState After, int IssuedTickets, int DiscardedTickets);

public sealed class PioneerConstructionRules(PioneerMasterData data)
{
    public const string PlanningVersion = "pioneer-ordered-planning-v1";
    public const string ObservedMasterSha256 = "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886";
    public PioneerMasterData Data { get; } = data;

    // Cost analysis also accepts intermediate plans used by the evidence tools.
    public ConstructionPlanAnalysis Analyze(PioneerConstructionPlan plan, int planningCost)
    {
        if (planningCost < 0)
            throw new InvalidDataException("观测到的 planning_cost 不能为负数。");
        var steps = ImmutableArray.CreateBuilder<ConstructionStepCost>();
        var total = 0;
        foreach (var step in plan.Steps)
        {
            if (!Data.UpgradesByEdge.TryGetValue((step.FacilityId, step.PreviousStateId, step.TargetStateId), out var upgrade))
                throw new UnknownMechanismException($"master {Data.SourceSha256} 没有设施 {step.FacilityId} " +
                    $"从 {step.PreviousStateId} 到 {step.TargetStateId} 的升级记录。可能是未知规则或版本不匹配。");
            total = checked(total + upgrade.Cost);
            steps.Add(new(step, upgrade.Cost, total));
        }
        return new(steps.ToImmutable(), total, planningCost,
            plan.Steps.Select(x => x.FacilityId).Distinct().Count() != plan.Steps.Length);
    }

    internal ConstructionPlanAnalysis ValidateCurrentPlan(PioneerDevelopmentState state)
    {
        if (state.DevelopmentPoints < 0 || state.IslandTrainingTickets is < 0 || state.IslandTrainingThresholds.IsDefault
            || state.Facilities.IsDefault || state.Facilities.Length != Data.FacilityKinds.Length
            || state.Facilities.Select(value => value.FacilityId).Distinct().Count() != state.Facilities.Length
            || state.Facilities.Any(value => !Data.StatesById.ContainsKey((value.FacilityId, value.StateId))))
            throw new InvalidDataException("当前建设状态需要非负发展Pt、明确阈值、每种设施的唯一有效状态，以及非负或未知的岛训练券数量。");
        var thresholds = Data.TicketThresholds.Where(value => value.DeckId == state.DeckId
            && value.TurnMin <= state.Turn && state.Turn <= value.TurnMax).Select(value => value.GainShimaTrainingPt).Order().ToArray();
        if (thresholds.Length != 0 && !state.IslandTrainingThresholds.Order().SequenceEqual(thresholds))
            throw new InvalidDataException("当前建设阈值与master周期不一致。");
        var period = Data.PlanningPeriods.SingleOrDefault(value => value.DeckId == state.DeckId
            && value.PlanningTurn < state.Turn && state.Turn <= value.CheckPointTurn);
        if (period is not null && state.PlanningCost != period.TargetPt)
            throw new InvalidDataException("当前建设预算与master周期不一致。");
        var analysis = Analyze(state.Plan, state.PlanningCost);
        if (analysis.HasRepeatedFacility || analysis.TotalCost > state.PlanningCost)
            throw new InvalidDataException("当前建设计划包含重复设施或超出规划预算。");
        foreach (var step in state.Plan.Steps)
        {
            var current = state.Facilities.Single(value => value.FacilityId == step.FacilityId).StateId;
            if (current != step.PreviousStateId && current != step.TargetStateId)
                throw new InvalidDataException($"当前设施{step.FacilityId}既不是计划前态也不是计划目标。");
        }
        return analysis;
    }

    // Facility and ticket-count effects only. Event, turn and unmodeled partner allocation remain unresolved.
    public ConstructionComponentSettlement ApplySettlementComponentsCandidate(PioneerDevelopmentState state)
    {
        if (state.DeckId != 3 || state.PendingStoryIds is not { Length: 1 } pending
            || !((pending[0] == 400011107 && state.Turn is >= 5 and <= 60 && state.RawPlayingState == 5)
                || (pending[0] == 400011108 && state.Turn == 41 && state.RawPlayingState == 1)))
            throw new UnknownMechanismException("设施结算证据仅覆盖 deck_id=3 的单事件局部状态：" +
                "400011107 / turn=5..60 / playing_state=5，或 400011108 / turn=41 / playing_state=1。" +
                "完整事件及其他数据范围尚未核验。");
        if (state.IslandTrainingTickets is not (0 or 1))
            throw new UnknownMechanismException("建设结算票数证据仅覆盖前态明确持有 0 或 1 张岛训练券；缺失值不能补零。");
        var analysis = Analyze(state.Plan, state.PlanningCost);
        if (analysis.HasRepeatedFacility)
            throw new UnknownMechanismException("设施结算证据尚未覆盖重复设施计划。");
        if (state.IslandTrainingThresholds.IsDefaultOrEmpty)
            throw new InvalidDataException("设施结算缺少岛训练阈值。");
        var reached = state.IslandTrainingThresholds.Where(x => x <= state.DevelopmentPoints).ToArray();
        if (reached.Length == 0)
            throw new UnknownMechanismException("待处理建设事件没有已达到的岛训练阈值，超出已核验范围。");
        var milestone = reached.Max();
        var facilities = state.Facilities.ToDictionary(x => x.FacilityId, x => x.StateId);
        foreach (var step in analysis.Steps)
        {
            if (!facilities.TryGetValue(step.Step.FacilityId, out var current))
                throw new InvalidDataException($"设施结算缺少设施 {step.Step.FacilityId} 的前态。");
            if (current != step.Step.PreviousStateId && current != step.Step.TargetStateId)
                throw new UnknownMechanismException($"设施 {step.Step.FacilityId} 的观测状态 {current} " +
                    "既不是计划前态也不是计划目标，超出已核验范围。");
            if (step.CumulativeCost <= milestone)
                facilities[step.Step.FacilityId] = step.Step.TargetStateId;
        }
        return new(state with
        {
            Facilities = [.. state.Facilities.Select(x => new ObservedFacility(x.FacilityId, facilities[x.FacilityId]))],
            IslandTrainingTickets = 1,
        }, IssuedTickets: 1, DiscardedTickets: state.IslandTrainingTickets.Value);
    }

    public void ValidateForSubmission(PioneerDevelopmentState state, PioneerConstructionPlan plan)
    {
        var period = ValidatePlanningState(state);
        var facilities = state.Facilities.ToDictionary(value => value.FacilityId, value => value.StateId);
        var selected = new HashSet<int>();
        var cost = 0;
        foreach (var step in plan.Steps)
        {
            if (!selected.Add(step.FacilityId)) throw new IllegalActionException($"设施{step.FacilityId}在同一计划中重复升级。");
            if (!facilities.TryGetValue(step.FacilityId, out var current) || step.PreviousStateId != current)
                throw new IllegalActionException($"设施{step.FacilityId}的计划前态{step.PreviousStateId}与当前设施不一致。");
            if (!Data.UpgradesByEdge.TryGetValue((step.FacilityId, current, step.TargetStateId), out var edge))
                throw new IllegalActionException($"设施{step.FacilityId}不存在从{current}到{step.TargetStateId}的升级边。");
            cost = checked(cost + edge.Cost);
        }
        if (cost > period.TargetPt) throw new IllegalActionException($"建设费用{cost}超过本期预算{period.TargetPt}。");
        if (HasRequiredExtension(facilities, selected, period.TargetPt - cost))
            throw new IllegalActionException("剩余预算仍可追加非熟练技巧路线的设施升级，请补全建设计划。");
    }

    public IEnumerable<PioneerConstructionPlan> EnumeratePlans(PioneerDevelopmentState state)
    {
        var period = ValidatePlanningState(state);
        var facilities = state.Facilities.ToDictionary(value => value.FacilityId, value => value.StateId);
        var edges = Data.FacilityUpgrades.Where(edge => facilities[edge.FacilityId] == edge.CurrentStateId)
            .OrderBy(edge => edge.FacilityId).ThenBy(edge => edge.TargetStateId).ToArray();
        var selected = new HashSet<int>();
        var steps = new List<PlannedUpgrade>(facilities.Count);
        foreach (var plan in Visit(period.TargetPt)) yield return plan;

        IEnumerable<PioneerConstructionPlan> Visit(int remaining)
        {
            if (!HasRequiredExtension(facilities, selected, remaining)) yield return new(steps);
            foreach (var edge in edges)
            {
                if (selected.Contains(edge.FacilityId) || edge.Cost > remaining) continue;
                selected.Add(edge.FacilityId);
                steps.Add(new(steps.Count + 1, edge.FacilityId, edge.CurrentStateId, edge.TargetStateId));
                foreach (var plan in Visit(remaining - edge.Cost)) yield return plan;
                steps.RemoveAt(steps.Count - 1);
                selected.Remove(edge.FacilityId);
            }
        }
    }

    public PioneerDevelopmentState Submit(PioneerDevelopmentState state, PioneerConstructionPlan plan)
    {
        ValidateForSubmission(state, plan);
        return state with { Plan = plan, Turn = state.Turn == 3 ? state.Turn : checked(state.Turn + 1), RawPlayingState = 1 };
    }

    public PlanningPeriod ValidatePlanningState(PioneerDevelopmentState state)
    {
        if (state.RawPlayingState != 35 || state.PendingStoryIds is not { IsEmpty: true })
            throw new IllegalActionException("提交建设计划要求阶段35且当前事件队列为空。");
        var period = Data.PlanningPeriods.SingleOrDefault(value => value.DeckId == state.DeckId && value.PlanningTurn == state.Turn)
            ?? throw new IllegalActionException($"master没有deck={state.DeckId}、turn={state.Turn}的建设规划期。");
        if (state.PlanningCost != period.TargetPt) throw new InvalidDataException("当前建设预算与master规划期不一致。");
        if (state.Facilities.IsDefault || state.Facilities.Length != Data.FacilityKinds.Length
            || state.Facilities.Select(value => value.FacilityId).Distinct().Count() != state.Facilities.Length
            || state.Facilities.Any(value => !Data.StatesById.ContainsKey((value.FacilityId, value.StateId))))
            throw new InvalidDataException("建设规划需要每种设施的唯一当前状态。");
        return period;
    }

    bool HasRequiredExtension(Dictionary<int, int> facilities, HashSet<int> selected, int remaining)
        => Data.FacilityUpgrades.Any(edge => !selected.Contains(edge.FacilityId)
            && edge.CurrentStateId == facilities[edge.FacilityId] && edge.Cost <= remaining
            && edge.TargetStateId % 10 != 2);
}
