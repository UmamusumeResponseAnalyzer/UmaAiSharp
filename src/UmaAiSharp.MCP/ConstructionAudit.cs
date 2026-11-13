using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;

namespace UmaAiSharp.MCP.Evidence;

public sealed record ConstructionEventAudit(ConstructionSettlementAudit Facilities, string SelectionEvidence,
    int BeforeTurn, int AfterTurn, int? BeforeTickets, int? AfterTickets,
    bool CandidateFacilitiesMatch, bool CandidateTicketsMatch, int CandidateIssuedTickets,
    int? ObservedIssuedTickets, bool? CandidateIssuedTicketsMatch);
public sealed record ConstructionTriggerAudit(EvidenceReference Before, EvidenceReference After, string Endpoint,
    int BeforePoints, int AfterPoints, int BeforeReachedThresholdCount, int AfterReachedThresholdCount,
    int BeforeLastGainRightsNum, int AfterLastGainRightsNum, bool SamePlanAndThresholds);
public sealed record ConstructionStoryCoverage(int StoryId, int Observations, int MatchedSelections,
    int FacilityMatches, int TicketMatches, int MatchedIssuedTicketSelections);
public sealed record ConstructionChangeCoverage(string Endpoint, string PreviousStories,
    int FacilityChanges, int TicketCountChanges, int TicketAvailabilityChanges, int MatchedEventSelections);
public sealed record ConstructionComponentChange(EvidenceReference Before, EvidenceReference After,
    string Endpoint, string PreviousStories, string SelectionEvidence, bool FacilitiesChanged,
    int? TicketsBefore, int? TicketsAfter);
public sealed record ConstructionAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Sources,
    int PlanObservations, int PartialPlanObservations, int ImmediatePointMismatches, int SettlementObservations,
    int RawPointMatches, int ReachedThresholdMatches, int CandidateFacilityMatches, int CandidateTicketMatches,
    int MatchedEventSelections, int EventAppearances, int AppearancesWithThresholdIncrease,
    IReadOnlyList<ConstructionStoryCoverage> Stories, IReadOnlyList<ConstructionChangeCoverage> ComponentChanges,
    IReadOnlyList<EvidenceIssue> Issues, VersionBinding VersionBinding,
    ValidationSummary Validation);

public static class ConstructionAudit
{
    sealed record Previous(PioneerObservation Observation, PioneerDevelopmentState State, JsonObject Common);
    sealed record Selection(int EventId, int CharacterId, int ChoiceNumber, int Turn);

    public static ConstructionAuditReport Run(string inputDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(inputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("建设核验输出目录非空，拒绝覆盖已有证据。");
        var sourceReport = JsonSerializer.Deserialize<EvidenceReport>(
            File.ReadAllText(Path.Combine(input, "audit.json")), EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("建设核验缺少输入证据报告。");
        if (sourceReport.SchemaVersion != "1") throw new InvalidDataException("建设核验不支持该证据报告版本。");
        var sourceHashes = sourceReport.Sources.ToDictionary(x => x.Alias, x => x.Sha256, StringComparer.Ordinal);
        if (!sourceHashes.TryGetValue("master", out var masterHash))
            throw new InvalidDataException("输入证据报告缺少 master 来源哈希。");
        using var masterRows = File.OpenText(Path.Combine(input, "master-rows.jsonl"));
        var rules = new PioneerConstructionRules(PioneerMasterData.ReadExport(masterRows, masterHash));
        var issues = new List<EvidenceIssue>();
        var paired = ReadRows<ExchangeSummary>(Path.Combine(input, "exchanges.jsonl"))
            .Where(x => x.Endpoint.EndsWith("/check_event", StringComparison.Ordinal) && x.Status == "Paired")
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var selections = new Dictionary<string, Selection>(StringComparer.Ordinal);
        foreach (var action in ReadRows<ObservedAction>(Path.Combine(input, "pioneer-actions.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!action.Endpoint.EndsWith("/check_event", StringComparison.Ordinal) || !paired.Contains(action.ExchangeKey)) continue;
            try
            {
                var request = action.Fields["single_mode_check_event_request_common"] as JsonObject
                    ?? throw new InvalidDataException("缺少 single_mode_check_event_request_common。");
                if (!selections.TryAdd(action.ExchangeKey,
                    new(RequiredInt(request, "event_id"), RequiredInt(request, "chara_id"), RequiredInt(request, "choice_number"), RequiredInt(request, "current_turn"))))
                    throw new InvalidDataException("同一已配对 exchange 出现多个归一化事件请求。");
            }
            catch (InvalidDataException error)
            {
                issues.Add(new("ConstructionSelectionIncomplete", action.Source, action.PacketId, error.Message));
                selections.Remove(action.ExchangeKey);
            }
        }

        Directory.CreateDirectory(output);
        using var snapshots = new StreamWriter(new FileStream(Path.Combine(output, "construction-observations.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var settlements = new StreamWriter(new FileStream(Path.Combine(output, "construction-settlements.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var triggers = new StreamWriter(new FileStream(Path.Combine(output, "construction-triggers.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var changes = new StreamWriter(new FileStream(Path.Combine(output, "construction-component-changes.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        var previous = new Dictionary<string, Previous>(StringComparer.Ordinal);
        var planObservations = 0;
        var partialPlanObservations = 0;
        var immediateMismatches = 0;
        var settlementObservations = 0;
        var rawMatches = 0;
        var thresholdMatches = 0;
        var candidateMatches = 0;
        var candidateTicketMatches = 0;
        var selectedEvents = 0;
        var evaluatedSettlements = new List<ConstructionEventAudit>();
        var componentChanges = new List<ConstructionComponentChange>();
        var eventAppearances = 0;
        var appearancesWithThresholdIncrease = 0;
        foreach (var observation in ReadRows<PioneerObservation>(Path.Combine(input, "pioneer-observations.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (observation.Visible["pioneer_data_set"] is not JsonObject scenario)
                continue;
            if (common?["chara_info"] is not JsonObject)
            {
                if (scenario["planning_info_array"] is JsonArray { Count: > 0 })
                {
                    partialPlanObservations++;
                    issues.Add(new("PartialConstructionObservation", observation.Source, observation.PacketId,
                        "响应含计划但没有对应的 chara_info；不能用其他响应补齐 turn/playing_state 后核验。"));
                }
                continue;
            }
            if (!sourceHashes.TryGetValue(observation.Source, out var sourceHash))
                throw new InvalidDataException("Pioneer 观测引用了证据报告中不存在的源。");
            PioneerDevelopmentState state;
            try
            {
                state = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(scenario));
                if (state.Plan.Steps.Length > 0)
                {
                    var snapshot = ConstructionEvidence.CheckImmediateCompletion(rules, state, new(sourceHash, observation.PacketId));
                    planObservations++;
                    if (snapshot.ImmediateCompletionDifferences.Length > 0) immediateMismatches++;
                    snapshots.WriteLine(JsonSerializer.Serialize(snapshot, EvidenceJson.LineOptions));
                }
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or InvalidOperationException or UnknownMechanismException)
            {
                issues.Add(new("ConstructionObservationUnsupported", observation.Source, observation.PacketId,
                    $"{error.GetType().Name}: {error.Message}"));
                previous.Remove(observation.Source);
                continue;
            }
            var hasPrevious = previous.TryGetValue(observation.Source, out var before)
                && observation.RunKey is not null && observation.RunKey == before.Observation.RunKey;
            if (hasPrevious && before is not null)
            {
                var facilitiesChanged = !before.State.Facilities.OrderBy(x => x.FacilityId).SequenceEqual(state.Facilities.OrderBy(x => x.FacilityId));
                if (facilitiesChanged || before.State.IslandTrainingTickets != state.IslandTrainingTickets)
                {
                    var stories = before.State.PendingStoryIds is { } known ? string.Join(',', known) : "Missing";
                    var selectionEvidence = observation.Endpoint.EndsWith("/check_event", StringComparison.Ordinal)
                        ? MatchEventSelection(before, selections.GetValueOrDefault(observation.ExchangeKey), null) : "NotEventRequest";
                    var change = new ConstructionComponentChange(new(sourceHash, before.Observation.PacketId), new(sourceHash, observation.PacketId),
                        observation.Endpoint, stories, selectionEvidence, facilitiesChanged,
                        before.State.IslandTrainingTickets, state.IslandTrainingTickets);
                    componentChanges.Add(change);
                    changes.WriteLine(JsonSerializer.Serialize(change, EvidenceJson.LineOptions));
                }
            }
            if (hasPrevious && before!.State.PendingStoryIds is { } oldEvents && !oldEvents.Contains(400011107)
                && state.PendingStoryIds is { } newEvents && newEvents.Contains(400011107))
            {
                try
                {
                    var beforeInfo = before.Observation.Visible["pioneer_data_set"]!["pioneer_info"]!.AsObject();
                    var afterInfo = scenario["pioneer_info"]!.AsObject();
                    var oldReached = before.State.IslandTrainingThresholds.Count(x => x <= before.State.DevelopmentPoints);
                    var newReached = state.IslandTrainingThresholds.Count(x => x <= state.DevelopmentPoints);
                    var samePlan = before.State.Plan.SemanticId == state.Plan.SemanticId
                        && before.State.IslandTrainingThresholds.SequenceEqual(state.IslandTrainingThresholds);
                    var trigger = new ConstructionTriggerAudit(new(sourceHash, before.Observation.PacketId), new(sourceHash, observation.PacketId),
                        observation.Endpoint, before.State.DevelopmentPoints, state.DevelopmentPoints, oldReached, newReached,
                        RequiredInt(beforeInfo, "last_gain_rights_num"), RequiredInt(afterInfo, "last_gain_rights_num"), samePlan);
                    triggers.WriteLine(JsonSerializer.Serialize(trigger, EvidenceJson.LineOptions));
                    eventAppearances++;
                    if (samePlan && newReached > oldReached) appearancesWithThresholdIncrease++;
                }
                catch (InvalidDataException error)
                {
                    issues.Add(new("ConstructionTriggerIncomplete", observation.Source, observation.PacketId, error.Message));
                }
            }
            if (hasPrevious && before is not null
                && observation.Endpoint.EndsWith("/check_event", StringComparison.Ordinal)
                && before.State.PendingStoryIds is { Length: 1 } pending && pending[0] is 400011107 or 400011108
                && state.PendingStoryIds is { } next && !next.Contains(pending[0]))
            {
                try
                {
                    var facilityAudit = ConstructionEvidence.CheckObservedSettlement(rules, before.State, state,
                        new(sourceHash, before.Observation.PacketId), new(sourceHash, observation.PacketId));
                    var candidate = rules.ApplySettlementComponentsCandidate(before.State);
                    var candidateMatch = candidate.After.Facilities.OrderBy(x => x.FacilityId)
                        .SequenceEqual(state.Facilities.OrderBy(x => x.FacilityId));
                    var selectionStatus = MatchEventSelection(before, selections.GetValueOrDefault(observation.ExchangeKey), 0);
                    var ticketMatch = candidate.After.IslandTrainingTickets == state.IslandTrainingTickets;
                    var issuedTickets = scenario["shima_training_gain_info"]?["gain_num"]?.GetValue<int>();
                    if (issuedTickets is null)
                        issues.Add(new("ConstructionIssuedTicketsUnavailable", observation.Source, observation.PacketId,
                            "建设结算响应缺少 shima_training_gain_info.gain_num；不能用剩余券净差补发券量。"));
                    var result = new ConstructionEventAudit(facilityAudit, selectionStatus, before.State.Turn, state.Turn,
                        before.State.IslandTrainingTickets, state.IslandTrainingTickets, candidateMatch, ticketMatch,
                        candidate.IssuedTickets, issuedTickets, issuedTickets is null ? null : issuedTickets == candidate.IssuedTickets);
                    settlements.WriteLine(JsonSerializer.Serialize(result, EvidenceJson.LineOptions));
                    evaluatedSettlements.Add(result);
                    settlementObservations++;
                    if (facilityAudit.RawPointDifferences.Length == 0) rawMatches++;
                    if (facilityAudit.ReachedThresholdDifferences.Length == 0) thresholdMatches++;
                    if (candidateMatch) candidateMatches++;
                    if (ticketMatch) candidateTicketMatches++;
                    if (selectionStatus == "Matched") selectedEvents++;
                }
                catch (Exception error) when (error is InvalidDataException or UnknownMechanismException)
                {
                    issues.Add(new("ConstructionSettlementUnsupported", observation.Source, observation.PacketId, error.Message));
                }
            }
            previous[observation.Source] = new(observation, state, common);
        }
        var report = new ConstructionAuditReport("1", sourceReport.Sources, planObservations, partialPlanObservations, immediateMismatches,
            settlementObservations, rawMatches, thresholdMatches, candidateMatches, candidateTicketMatches, selectedEvents,
            eventAppearances, appearancesWithThresholdIncrease,
            evaluatedSettlements.GroupBy(x => x.Facilities.StoryId).OrderBy(x => x.Key).Select(group =>
                new ConstructionStoryCoverage(group.Key, group.Count(), group.Count(x => x.SelectionEvidence == "Matched"),
                    group.Count(x => x.CandidateFacilitiesMatch), group.Count(x => x.CandidateTicketsMatch),
                    group.Count(x => x.SelectionEvidence == "Matched" && x.CandidateIssuedTicketsMatch == true))).ToArray(),
            componentChanges.GroupBy(x => (x.Endpoint, x.PreviousStories)).OrderBy(x => x.Key.Endpoint, StringComparer.Ordinal)
                .ThenBy(x => x.Key.PreviousStories, StringComparer.Ordinal).Select(group => new ConstructionChangeCoverage(group.Key.Endpoint,
                    group.Key.PreviousStories, group.Count(x => x.FacilitiesChanged),
                    group.Count(x => x.TicketsBefore.HasValue && x.TicketsAfter.HasValue && x.TicketsBefore != x.TicketsAfter),
                    group.Count(x => x.TicketsBefore.HasValue != x.TicketsAfter.HasValue),
                    group.Count(x => x.SelectionEvidence == "Matched"))).ToArray(), issues,
            sourceReport.VersionBinding, new("Blocked",
            ["ConstructionEventSchedulingUnverified", "CompleteEventEffectsUnverified",
                "UnobservedDecksAndPlansUnverified", "TrajectoryCompletenessUnverified"]));
        File.WriteAllText(Path.Combine(output, "construction-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static string MatchEventSelection(Previous before, Selection? selection, int? requiredChoice)
    {
        if (selection is null) return "RequestMissingOrConflicting";
        if (before.Common["unchecked_event_array"] is not JsonArray events) return "EventArrayMissing";
        var matched = events.OfType<JsonObject>().Where(x => RequiredInt(x, "event_id") == selection.EventId
            && RequiredInt(x, "chara_id") == selection.CharacterId).Take(2).ToArray();
        return matched.Length == 1 && selection.Turn == before.State.Turn
            && (requiredChoice is null || selection.ChoiceNumber == requiredChoice
                && RequiredInt(matched[0], "story_id") is 400011107 or 400011108) ? "Matched" : "Mismatch";
    }

    static int RequiredInt(JsonObject value, string name) => PioneerNormalizer.Integer(value, name) is { Status: "Present", Value: { } number }
        && number is >= int.MinValue and <= int.MaxValue ? (int)number : throw new InvalidDataException($"事件核验缺少有效整数 {name}。");

    static IEnumerable<T> ReadRows<T>(string path)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            T value;
            try { value = JsonSerializer.Deserialize<T>(line, EvidenceJson.ReadOptions) ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidDataException($"建设核验输入 {Path.GetFileName(path)} 第 {lineNumber} 行格式无效。"); }
            yield return value;
        }
    }
}
