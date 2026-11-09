using System.Collections.Immutable;
using System.Text.Json;

namespace UmaAiSharp.Pioneer;

public sealed record EvidenceReference(string SourceSha256, long RecordId);
public sealed record FacilityProjectionDifference(int FacilityId, int ObservedStateId, int ThresholdStateId);
public sealed record ConstructionObservationAudit(EvidenceReference Source, int Turn, int RawPlayingState,
    int DevelopmentPoints, int TotalCost, int PlanningCost, ImmutableArray<int>? PendingStoryIds,
    ImmutableArray<FacilityProjectionDifference> ImmediateCompletionDifferences,
    bool HasRepeatedFacility);
public sealed record ConstructionSettlementAudit(EvidenceReference Before, EvidenceReference After,
    int StoryId, int DevelopmentPoints, int ReachedThreshold,
    ImmutableArray<FacilityProjectionDifference> RawPointDifferences,
    ImmutableArray<FacilityProjectionDifference> ReachedThresholdDifferences);

public static class ConstructionEvidence
{
    public static PioneerDevelopmentState ReadObservation(JsonElement common, JsonElement scenario)
    {
        var chara = RequiredProperty(common, "chara_info");
        var info = RequiredProperty(scenario, "pioneer_info");
        var facilities = RequiredProperty(scenario, "facility_info_array").EnumerateArray()
            .Select(x => new ObservedFacility(RequiredProperty(x, "facility_id").GetInt32(), RequiredProperty(x, "state_id").GetInt32()))
            .ToImmutableArray();
        var plan = new PioneerConstructionPlan(RequiredProperty(scenario, "planning_info_array").EnumerateArray()
            .Select(x => new PlannedUpgrade(RequiredProperty(x, "priority").GetInt32(), RequiredProperty(x, "facility_id").GetInt32(),
                RequiredProperty(x, "prev_state_id").GetInt32(), RequiredProperty(x, "target_state_id").GetInt32())));
        // A missing queue in, for example, race_end is not evidence that no events are pending.
        var pending = common.TryGetProperty("unchecked_event_array", out var events)
            ? (ImmutableArray<int>?)events.EnumerateArray().Select(x => RequiredProperty(x, "story_id").GetInt32()).ToImmutableArray()
            : null;
        var ticketInfo = RequiredProperty(scenario, "shima_training_info");
        var tickets = ticketInfo.ValueKind switch
        {
            JsonValueKind.Object => (int?)RequiredProperty(ticketInfo, "remain_num").GetInt32(),
            JsonValueKind.Array when ticketInfo.GetArrayLength() == 0 => null,
            _ => throw new InvalidDataException("shima_training_info 必须为对象或协议中的空数组。"),
        };
        return new(RequiredProperty(chara, "turn").GetInt32(), RequiredProperty(chara, "playing_state").GetInt32(),
            RequiredProperty(info, "pioneer_pt").GetInt32(), RequiredProperty(info, "planning_cost").GetInt32(),
            RequiredProperty(info, "deck_id").GetInt32(),
            RequiredProperty(info, "gain_shima_training_pt_array").EnumerateArray().Select(x => x.GetInt32()).ToImmutableArray(),
            tickets, facilities, plan, pending);
    }

    static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"建设观测字段 {name} 的父级必须为对象，实际为 {parent.ValueKind}。");
        return parent.TryGetProperty(name, out var value) ? value
            : throw new InvalidDataException($"建设观测缺少必需字段 {name}。");
    }

    // Deliberately tests a falsifiable hypothesis. This projection never mutates a game state.
    public static ConstructionObservationAudit CheckImmediateCompletion(PioneerConstructionRules rules,
        PioneerDevelopmentState observation, EvidenceReference source)
    {
        var analysis = rules.Analyze(observation.Plan, observation.PlanningCost);
        if (analysis.HasRepeatedFacility)
            throw new InvalidDataException("即时建设假设检查仅适用于每个设施出现一次的观测计划；" +
                "重复设施的计划需要单独核验，不能据此认定游戏不允许。");
        var facilities = observation.Facilities.ToDictionary(x => x.FacilityId, x => x.StateId);
        var differences = ImmutableArray.CreateBuilder<FacilityProjectionDifference>();
        foreach (var step in analysis.Steps)
        {
            if (!facilities.TryGetValue(step.Step.FacilityId, out var actual))
                throw new InvalidDataException($"观测缺少设施 {step.Step.FacilityId} 的状态。");
            var expected = observation.DevelopmentPoints >= step.CumulativeCost
                ? step.Step.TargetStateId : step.Step.PreviousStateId;
            if (actual != expected)
                differences.Add(new(step.Step.FacilityId, actual, expected));
        }
        return new(source, observation.Turn, observation.RawPlayingState, observation.DevelopmentPoints,
            analysis.TotalCost, analysis.PlanningCost, observation.PendingStoryIds,
            differences.ToImmutable(), analysis.HasRepeatedFacility);
    }

    // Compares an already observed event settlement. It does not decide when that event occurs.
    public static ConstructionSettlementAudit CheckObservedSettlement(PioneerConstructionRules rules,
        PioneerDevelopmentState before, PioneerDevelopmentState after,
        EvidenceReference beforeSource, EvidenceReference afterSource)
    {
        if (before.PendingStoryIds is not { Length: 1 } pendingBefore || pendingBefore[0] is not (400011107 or 400011108)
            || after.PendingStoryIds is not { } pendingAfter || pendingAfter.Contains(pendingBefore[0]))
            throw new InvalidDataException("建设结算核验需要前后响应均提供事件数组，且唯一建设事件 " +
                "400011107 或 400011108 在前态存在、后态消失。");
        if (before.Plan.SemanticId != after.Plan.SemanticId || before.DevelopmentPoints != after.DevelopmentPoints
            || !before.IslandTrainingThresholds.SequenceEqual(after.IslandTrainingThresholds))
            throw new InvalidDataException("建设结算核验要求前后计划、开拓 Pt 和岛训练阈值相同；其他转移尚未覆盖。");
        var analysis = rules.Analyze(before.Plan, before.PlanningCost);
        if (analysis.HasRepeatedFacility)
            throw new InvalidDataException("建设结算假设的实包证据未覆盖重复设施计划。");
        var reached = 0;
        foreach (var threshold in before.IslandTrainingThresholds)
            if (threshold <= before.DevelopmentPoints && threshold > reached)
                reached = threshold;
        var actual = after.Facilities.ToDictionary(x => x.FacilityId, x => x.StateId);
        var original = before.Facilities.ToDictionary(x => x.FacilityId, x => x.StateId);
        if (!actual.Keys.ToHashSet().SetEquals(original.Keys))
            throw new InvalidDataException("建设结算前后设施集合不一致。");
        ImmutableArray<FacilityProjectionDifference> Compare(int budget)
        {
            var expected = new Dictionary<int, int>(original);
            foreach (var step in analysis.Steps)
            {
                if (!expected.ContainsKey(step.Step.FacilityId))
                    throw new InvalidDataException($"观测缺少设施 {step.Step.FacilityId} 的状态。");
                if (step.CumulativeCost <= budget)
                    expected[step.Step.FacilityId] = step.Step.TargetStateId;
            }
            return [.. expected.OrderBy(x => x.Key).Where(x => actual[x.Key] != x.Value)
                .Select(x => new FacilityProjectionDifference(x.Key, actual[x.Key], x.Value))];
        }
        return new(beforeSource, afterSource, pendingBefore[0], before.DevelopmentPoints, reached,
            Compare(before.DevelopmentPoints), Compare(reached));
    }
}
