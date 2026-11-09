using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Cli.Evidence;

public sealed record CheckpointSubmissionAudit(EvidenceReference Before, EvidenceReference After,
    long RequestRecordId, int Turn, int DeckId, int CheckpointType, int ResultState, int DevelopmentPoints, int MasterTarget,
    bool TargetComparisonMatches, bool ResourcesUnchanged, int AfterPhase, IReadOnlyList<int> NewStories);
public sealed record CheckpointSettlementAudit(EvidenceReference Before, EvidenceReference After,
    long RequestRecordId, CheckpointEventContext Context, CheckpointResources ResourcesBefore, CheckpointResources ResourcesAfter,
    int AfterTurn, int AfterPhase, CheckpointResourceSettlement Candidate, bool ResourcesMatch);
public sealed record CheckpointStoryCoverage(int StoryId, int Observations, int Matches);
public sealed record CheckpointProgressAudit(EvidenceReference Before, EvidenceReference After, long RequestRecordId,
    CheckpointEventContext Context, CheckpointProgress ProgressBefore, CheckpointProgress ProgressAfter,
    int ObservedAddedMembers, int ObservedRemovedMembers, CheckpointProgressSettlement Candidate, bool Matches);
public sealed record CheckpointAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Sources,
    IReadOnlyList<SourceFingerprint> Inputs, int MatchedSubmissions, int TargetComparisonMatches, int SubmissionsWithoutResourceChanges,
    int MatchedEventSelections, int CandidateResourceMatches, IReadOnlyList<CheckpointStoryCoverage> Stories,
    IReadOnlyList<EvidenceIssue> Issues, VersionBinding VersionBinding, ValidationSummary Validation,
    int MatchedProgressSelections, int CandidateProgressMatches);

public static class CheckpointAudit
{
    sealed record PendingEvent(int EventId, int StoryId);
    sealed record Snapshot(PioneerObservation Observation, int Turn, int Phase, int DeckId,
        CheckpointResources Resources, Dictionary<int, int> Results, ImmutableArray<PendingEvent> Events);

    public static CheckpointAuditReport Run(string inputDirectory, string outputDirectory)
    {
        var input = Path.GetFullPath(inputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("检查点核验输出目录非空，拒绝覆盖已有证据。");
        var inputNames = new[] { "audit.json", "master-rows.jsonl", "exchanges.jsonl", "pioneer-actions.jsonl", "pioneer-observations.jsonl" };
        var fingerprints = inputNames.Select(name => Fingerprint(Path.Combine(input, name), name)).ToArray();
        var sourceReport = JsonSerializer.Deserialize<EvidenceReport>(
            File.ReadAllText(Path.Combine(input, "audit.json")), EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("检查点核验缺少输入证据报告。");
        if (sourceReport.SchemaVersion != "1") throw new InvalidDataException("检查点核验不支持该证据报告版本。");
        var hashes = sourceReport.Sources.ToDictionary(x => x.Alias, x => x.Sha256, StringComparer.Ordinal);
        if (!hashes.TryGetValue("master", out var masterHash))
            throw new InvalidDataException("检查点核验缺少 master 来源哈希。");
        using var masterRows = File.OpenText(Path.Combine(input, "master-rows.jsonl"));
        var master = CheckpointMasterData.ReadExport(masterRows, masterHash);
        var settlement = new CheckpointSettlement(master);
        var exchanges = ReadRows<ExchangeSummary>(Path.Combine(input, "exchanges.jsonl"))
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var actions = ReadRows<ObservedAction>(Path.Combine(input, "pioneer-actions.jsonl"))
            .Where(x => x.Endpoint.EndsWith("/check_event", StringComparison.Ordinal) || x.Endpoint.EndsWith("/check_point", StringComparison.Ordinal))
            .GroupBy(x => x.ExchangeKey).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var previous = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
        var submissions = new List<CheckpointSubmissionAudit>();
        var settlements = new List<CheckpointSettlementAudit>();
        var progressSettlements = new List<CheckpointProgressAudit>();
        var issues = new List<EvidenceIssue>();
        Directory.CreateDirectory(output);
        using var submissionOutput = new StreamWriter(new FileStream(Path.Combine(output, "checkpoint-submissions.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var settlementOutput = new StreamWriter(new FileStream(Path.Combine(output, "checkpoint-settlements.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        using var progressOutput = new StreamWriter(new FileStream(Path.Combine(output, "checkpoint-progress.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

        foreach (var observation in ReadRows<PioneerObservation>(Path.Combine(input, "pioneer-observations.jsonl")))
        {
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (common?["chara_info"] is not JsonObject chara || observation.Visible["pioneer_data_set"] is not JsonObject scenario)
            {
                previous.Remove(observation.Source);
                continue;
            }
            Snapshot current;
            try
            {
                var info = scenario["pioneer_info"] as JsonObject ?? throw new InvalidDataException("缺少 pioneer_info 对象。");
                var results = scenario["check_point_info_array"] as JsonArray ?? throw new InvalidDataException("缺少 check_point_info_array 数组。");
                var events = common["unchecked_event_array"] as JsonArray ?? throw new InvalidDataException("缺少 unchecked_event_array 数组。");
                current = new(observation, RequiredInt(chara, "turn"), RequiredInt(chara, "playing_state"), RequiredInt(info, "deck_id"),
                    new(RequiredInt(info, "pioneer_pt"), RequiredInt(chara, "vital"), RequiredInt(chara, "max_vital")),
                    results.Select(x => x as JsonObject ?? throw new InvalidDataException("检查点结果项必须为对象。"))
                        .ToDictionary(x => RequiredInt(x, "check_point_type"), x => RequiredInt(x, "result_state")),
                    events.Select(x => x as JsonObject ?? throw new InvalidDataException("待处理事件项必须为对象。"))
                        .Select(x => new PendingEvent(RequiredInt(x, "event_id"), RequiredInt(x, "story_id"))).ToImmutableArray());
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                issues.Add(new("CheckpointObservationIncomplete", observation.Source, observation.PacketId, error.Message));
                previous.Remove(observation.Source);
                continue;
            }
            var before = previous.GetValueOrDefault(observation.Source);
            previous[observation.Source] = current;
            var isSubmission = observation.Endpoint.EndsWith("/check_point", StringComparison.Ordinal);
            var isEvent = observation.Endpoint.EndsWith("/check_event", StringComparison.Ordinal);
            if (!isSubmission && !isEvent) continue;
            if (before is null || before.Observation.RunKey is null || before.Observation.RunKey != observation.RunKey)
            {
                if (isSubmission) issues.Add(new("CheckpointPreviousStateUnavailable", observation.Source, observation.PacketId, "缺少同一局相邻完整前态。"));
                continue;
            }
            if (!actions.TryGetValue(observation.ExchangeKey, out var requests) || requests.Length != 1)
            {
                if (isSubmission || before.Events.Any(x => CheckpointSettlement.ObservedEventOutcomes.ContainsKey(x.StoryId)))
                    issues.Add(new("CheckpointRequestUnavailable", observation.Source, observation.PacketId, "缺少唯一已配对请求。"));
                continue;
            }
            var action = requests[0];
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint || action.Endpoint != observation.Endpoint
                || !exchange.Requests.Contains(new(action.Source, action.PacketId))
                || !exchange.Responses.Contains(new(observation.Source, observation.PacketId))
                || action.Source != observation.Source || before.Observation.PacketId >= action.PacketId || action.PacketId >= observation.PacketId)
            {
                if (isSubmission || before.Events.Any(x => CheckpointSettlement.ObservedEventOutcomes.ContainsKey(x.StoryId)))
                    issues.Add(new("CheckpointPairingOrOrderUnproven", observation.Source, observation.PacketId,
                        "关联必须为 Paired 并列出同源请求/响应；捕获顺序必须为前态 < 请求 < 响应。"));
                continue;
            }
            if (!hashes.TryGetValue(observation.Source, out var sourceHash))
                throw new InvalidDataException("检查点观测引用了证据报告中不存在的源。");
            var beforeReference = new EvidenceReference(sourceHash, before.Observation.PacketId);
            var afterReference = new EvidenceReference(sourceHash, observation.PacketId);
            try
            {
                if (isSubmission)
                {
                    if (before.Phase != 34 || RequiredInt(action.Fields, "current_turn") != before.Turn || current.Turn != before.Turn)
                        throw new InvalidDataException("check_point 请求与前后回合或评审阶段 34 不匹配。");
                    var added = current.Results.Where(x => !before.Results.TryGetValue(x.Key, out var old) || old != x.Value).ToArray();
                    if (added.Length != 1) throw new InvalidDataException("check_point 必须对应一项新增或变化的检查点结果。");
                    var result = added[0];
                    if (!master.Periods.TryGetValue((before.DeckId, result.Key), out var period) || period.CheckPointTurn != before.Turn)
                        throw new InvalidDataException("检查点结果与 master 的 deck/turn 周期不匹配。");
                    var audit = new CheckpointSubmissionAudit(beforeReference, afterReference, action.PacketId, before.Turn, before.DeckId,
                        result.Key, result.Value, before.Resources.DevelopmentPoints, period.TargetPt,
                        result.Value == (before.Resources.DevelopmentPoints >= period.TargetPt ? 2 : 1),
                        before.Resources == current.Resources, current.Phase, current.Events.Select(x => x.StoryId).ToArray());
                    submissions.Add(audit);
                    submissionOutput.WriteLine(JsonSerializer.Serialize(audit, EvidenceJson.LineOptions));
                }
                else
                {
                    var request = action.Fields["single_mode_check_event_request_common"] as JsonObject
                        ?? throw new InvalidDataException("缺少 single_mode_check_event_request_common。");
                    var selected = before.Events.Where(x => x.EventId == RequiredInt(request, "event_id")).ToArray();
                    if (selected.Length != 1 || !CheckpointSettlement.ObservedEventOutcomes.TryGetValue(selected[0].StoryId, out var outcome)) continue;
                    if (RequiredInt(request, "current_turn") != before.Turn)
                        throw new InvalidDataException("评审事件请求 current_turn 与前态不匹配。");
                    if (!before.Results.TryGetValue(outcome.Type, out var result))
                        throw new InvalidDataException($"前态没有检查点 {outcome.Type} 的结果，不能用故事 ID 补值。");
                    var context = new CheckpointEventContext(before.Turn, before.Phase, before.DeckId,
                        outcome.Type, result, selected[0].StoryId, RequiredInt(request, "choice_number"));
                    var candidate = settlement.ApplyResourcesCandidate(context, before.Resources);
                    var audit = new CheckpointSettlementAudit(beforeReference, afterReference, action.PacketId, context,
                        before.Resources, current.Resources, current.Turn, current.Phase, candidate, candidate.After == current.Resources);
                    settlements.Add(audit);
                    settlementOutput.WriteLine(JsonSerializer.Serialize(audit, EvidenceJson.LineOptions));
                    if (context.ResultState == 2)
                    {
                        var oldCommon = PioneerNormalizer.GetCommon(before.Observation.Visible, before.Observation.Endpoint)!;
                        var progressBefore = ReadProgress(oldCommon["chara_info"]!.AsObject());
                        var progressAfter = ReadProgress(chara);
                        var oldMembers = ReadProjectMembers(before.Observation.Visible["pioneer_data_set"]!.AsObject());
                        var newMembers = ReadProjectMembers(scenario);
                        var progressCandidate = settlement.ApplyHighEvaluationProgress(context, progressBefore);
                        var addedMembers = newMembers.Except(oldMembers).Count();
                        var removedMembers = oldMembers.Except(newMembers).Count();
                        var progressAudit = new CheckpointProgressAudit(beforeReference, afterReference, action.PacketId, context,
                            progressBefore, progressAfter, addedMembers, removedMembers, progressCandidate,
                            progressCandidate.TrainingLevels == progressAfter.TrainingLevels && progressCandidate.DirectorBond == progressAfter.DirectorBond
                                && addedMembers == progressCandidate.AddedProjectMemberCount && removedMembers == 0);
                        progressSettlements.Add(progressAudit);
                        progressOutput.WriteLine(JsonSerializer.Serialize(progressAudit, EvidenceJson.LineOptions));
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or ArgumentException)
            {
                issues.Add(new("CheckpointTransitionUnsupported", observation.Source, observation.PacketId, error.Message));
            }
        }
        for (var i = 0; i < inputNames.Length; i++)
            if (Fingerprint(Path.Combine(input, inputNames[i]), inputNames[i]) != fingerprints[i])
                throw new InvalidDataException($"核验过程中输入文件 {inputNames[i]} 发生变化。");
        var report = new CheckpointAuditReport("1", sourceReport.Sources, fingerprints, submissions.Count,
            submissions.Count(x => x.TargetComparisonMatches), submissions.Count(x => x.ResourcesUnchanged),
            settlements.Count, settlements.Count(x => x.ResourcesMatch),
            settlements.GroupBy(x => x.Context.StoryId).OrderBy(x => x.Key)
                .Select(x => new CheckpointStoryCoverage(x.Key, x.Count(), x.Count(row => row.ResourcesMatch))).ToArray(),
            issues, sourceReport.VersionBinding, new("CandidateOnly",
            ["核验指定评审事件的 Pt/体力，以及高评价训练等级、理事长羁绊和新增PJ人数；不代表整个事件或整局模拟完成。",
                "第 1..4 期低于目标结果的结算、其他 deck 和未观测选择分支尚未核验。",
                "目标 Pt 二分类仅为观测对照，缺少低于首阈值的结果证据；不作为完整结果判定器。",
                "属性、技能、具体PJ成员身份和后续事件调度不由本分量结算。"]),
            progressSettlements.Count, progressSettlements.Count(x => x.Matches));
        File.WriteAllText(Path.Combine(output, "checkpoint-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static int RequiredInt(JsonObject value, string name) => value[name] is JsonValue field && field.TryGetValue<int>(out var result)
        ? result : throw new InvalidDataException($"缺少整数 {name}。");

    static CheckpointProgress ReadProgress(JsonObject chara)
    {
        var levels = chara["training_level_info_array"] as JsonArray
            ?? throw new InvalidDataException("评审进度分量缺少 training_level_info_array。");
        var levelMap = levels.Select(x => x as JsonObject ?? throw new InvalidDataException("训练等级项必须为对象。"))
            .ToDictionary(x => RequiredInt(x, "command_id"), x => RequiredInt(x, "level"));
        var commands = new[] { 101, 105, 102, 103, 106 };
        if (commands.Any(x => !levelMap.ContainsKey(x)))
            throw new InvalidDataException("评审进度分量缺少101/105/102/103/106普通训练等级。");
        var evaluations = chara["evaluation_info_array"] as JsonArray
            ?? throw new InvalidDataException("评审进度分量缺少 evaluation_info_array。");
        var directors = evaluations.OfType<JsonObject>().Where(x => RequiredInt(x, "target_id") == 102).ToArray();
        if (directors.Length != 1) throw new InvalidDataException("评审进度分量需要唯一理事长 target_id=102。");
        var effects = chara["chara_effect_id_array"] as JsonArray
            ?? throw new InvalidDataException("评审进度分量缺少 chara_effect_id_array。");
        return new(new(levelMap[101], levelMap[105], levelMap[102], levelMap[103], levelMap[106]),
            RequiredInt(directors[0], "evaluation"), effects.Any(x => x!.GetValue<int>() == 9));
    }

    static HashSet<int> ReadProjectMembers(JsonObject scenario)
    {
        var rows = scenario["evaluation_info_array"] as JsonArray
            ?? throw new InvalidDataException("评审进度分量缺少 Pioneer evaluation_info_array。");
        return rows.Select(x => x as JsonObject ?? throw new InvalidDataException("PJ成员项必须为对象。"))
            .Where(x => RequiredInt(x, "is_pioneer_team") != 0).Select(x => RequiredInt(x, "target_id")).ToHashSet();
    }

    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "NormalizedPioneerEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

    static IEnumerable<T> ReadRows<T>(string path)
    {
        foreach (var line in File.ReadLines(path))
            yield return JsonSerializer.Deserialize<T>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException($"证据文件 {Path.GetFileName(path)} 包含空行对象。");
    }
}
