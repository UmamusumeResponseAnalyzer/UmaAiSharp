using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Cli.Evidence;

public sealed record TrainingPreviewTransition(string Source, long BeforePacketId, long RequestPacketId, long AfterPacketId,
    string RunKey, int CommandId, int ResultState, int BeforeTurn, int AfterTurn, TrainingValues Before,
    TrainingLimits Limits, IReadOnlyList<TrainingParameterChange> CommonPreview,
    IReadOnlyList<TrainingParameterChange> ScenarioPreview, TrainingValues Predicted, TrainingValues Observed, bool Matches);
public sealed record TrainingValueRange(string Parameter, int Minimum, int Maximum);
public sealed record TrainingPreviewAuditReport(string SchemaVersion, string RuleVersion,
    IReadOnlyList<SourceFingerprint> Inputs, int TrainingCommandResponses, int EligibleTransitions,
    int ExactMatches, int VitalUpperClips, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<TrainingValueRange> ObservedRanges, IReadOnlyList<EvidenceIssue> Exclusions,
    IReadOnlyList<string> ApplicabilityLimits, string Status);

public static class TrainingPreviewAudit
{
    public static TrainingPreviewAuditReport Run(string inputDirectory, string outputDirectory)
    {
        var observationsPath = Path.Combine(inputDirectory, "pioneer-observations.jsonl");
        var actionsPath = Path.Combine(inputDirectory, "pioneer-actions.jsonl");
        var exchangesPath = Path.Combine(inputDirectory, "exchanges.jsonl");
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("训练核验输出目录非空，拒绝覆盖已有证据。");
        var fingerprints = new[] { Fingerprint(observationsPath, "observations"), Fingerprint(actionsPath, "actions"), Fingerprint(exchangesPath, "exchanges") };
        var exchanges = File.ReadLines(exchangesPath).Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("请求响应关联记录不能为null。"))
            .ToDictionary(exchange => exchange.Key, StringComparer.Ordinal);
        var actions = File.ReadLines(actionsPath).Select(line => JsonSerializer.Deserialize<ObservedAction>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("动作记录不能为null。"))
            .Where(action => action.Endpoint.EndsWith("/exec_command", StringComparison.Ordinal))
            .GroupBy(action => action.ExchangeKey).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        using var transitions = new StreamWriter(new FileStream(Path.Combine(output, "training-transitions.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        var previous = new Dictionary<string, PioneerObservation>(StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var exclusions = new List<EvidenceIssue>();
        var acceptedValues = new List<TrainingValues>();
        var responses = 0;
        var eligible = 0;
        var exact = 0;
        var vitalClips = 0;
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        void Exclude(PioneerObservation observation, string code, string detail)
        {
            Count(code);
            exclusions.Add(new(code, observation.Source, observation.PacketId, detail));
        }

        foreach (var line in File.ReadLines(observationsPath))
        {
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("育成观测不能为null。");
            previous.TryGetValue(observation.Source, out var beforeObservation);
            // A partial observation also breaks continuity; it cannot silently inherit the previous run.
            previous[observation.Source] = observation;
            if (!observation.Endpoint.EndsWith("/exec_command", StringComparison.Ordinal)) continue;
            responses++;
            if (beforeObservation is null || observation.RunKey is null || beforeObservation.RunKey != observation.RunKey)
            {
                Exclude(observation, "NoAdjacentSameRun", "相邻观测没有相同且明确的actor+run身份。");
                continue;
            }
            if (!actions.TryGetValue(observation.ExchangeKey, out var requests) || requests.Length != 1)
            {
                Exclude(observation, "RequestMissingOrAmbiguous", "没有唯一可关联的exec_command请求。");
                continue;
            }
            var request = requests[0];
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint
                || !exchange.Requests.Contains(new(request.Source, request.PacketId))
                || !exchange.Responses.Contains(new(observation.Source, observation.PacketId)))
            {
                Exclude(observation, "ExchangeNotPaired", "关联必须为Paired且明确列出本次请求、响应引用；不选择冲突正文。");
                continue;
            }
            try
            {
                if (request.Fields["single_mode_exec_command_request_common"] is not JsonObject command)
                    throw new InvalidDataException("请求缺少single_mode_exec_command_request_common对象。");
                if (Int(command, "command_type") != 1) { Count("NotTrainingCommand"); continue; }
                if (request.Source != observation.Source || beforeObservation.PacketId >= request.PacketId || request.PacketId >= observation.PacketId)
                {
                    Exclude(observation, "CaptureOrderUnproven", "未满足同源前置响应<请求<结果响应的捕获顺序。");
                    continue;
                }
                var beforeCommon = PioneerNormalizer.GetCommon(beforeObservation.Visible, beforeObservation.Endpoint);
                var afterCommon = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
                if (beforeCommon?["chara_info"] is not JsonObject beforeChara || afterCommon?["chara_info"] is not JsonObject afterChara
                    || afterCommon["command_result"] is not JsonObject result)
                    throw new InvalidDataException("转移缺少完整chara_info或command_result。");
                var before = Values(beforeChara);
                var observed = Values(afterChara);
                var turn = Int(beforeChara, "turn");
                if (Int(command, "current_turn") != turn || Int(command, "current_vital") != before.Vital)
                {
                    Exclude(observation, "StaleSnapshot", "请求current_turn/current_vital与前置快照不符；净差不能归因于本次训练。");
                    continue;
                }
                var commandId = Int(command, "command_id");
                if (commandId != Int(result, "command_id"))
                {
                    Exclude(observation, "CommandMismatch", "请求与结果的command_id不同。");
                    continue;
                }
                var resultState = Int(result, "result_state");
                if (resultState != 2) { Exclude(observation, "OtherResultState", $"result_state={resultState}不属于本次核验的结果类型2。"); continue; }
                var home = beforeCommon["home_info"] as JsonObject;
                var pioneer = beforeObservation.Visible["pioneer_data_set"] as JsonObject;
                var commonPreview = Preview(home, commandId, requireEnabled: true);
                var scenarioPreview = Preview(pioneer, commandId, requireEnabled: false);
                if (commonPreview is null || scenarioPreview is null)
                {
                    Exclude(observation, "PreviewMissing", "选中训练没有唯一、完整且可用的home和Pioneer预览；null不视为零增量。");
                    continue;
                }
                var limits = Limits(beforeChara);
                if (limits != Limits(afterChara))
                {
                    Exclude(observation, "LimitsChanged", "响应同时改变属性或体力上限，不能用固定上限局部结算解释。");
                    continue;
                }
                var predicted = TrainingPreviewSettlement.Apply(before, limits, commonPreview, scenarioPreview);
                var matches = predicted == observed;
                var afterTurn = Int(afterChara, "turn");
                eligible++;
                if (matches) exact++;
                Count(matches ? "Matched" : "Mismatch");
                Count("TurnDelta:" + (afterTurn - turn));
                var vitalGain = commonPreview.Concat(scenarioPreview).Where(change => change.TargetType == 10).Sum(change => change.Value);
                if ((long)before.Vital + vitalGain > limits.Vital) vitalClips++;
                acceptedValues.Add(before);
                acceptedValues.Add(observed);
                transitions.WriteLine(JsonSerializer.Serialize(new TrainingPreviewTransition(observation.Source,
                    beforeObservation.PacketId, request.PacketId, observation.PacketId, observation.RunKey, commandId,
                    resultState, turn, afterTurn, before, limits, commonPreview, scenarioPreview, predicted, observed, matches), EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or UnknownMechanismException or ArgumentException or OverflowException)
            {
                Exclude(observation, "UnsupportedInputOrSettlement", error.Message);
            }
        }

        foreach (var (path, alias) in new[] { (observationsPath, "observations"), (actionsPath, "actions"), (exchangesPath, "exchanges") })
        {
            var after = Fingerprint(path, alias);
            if (fingerprints.Single(input => input.Alias == alias).Sha256 != after.Sha256)
                throw new InvalidDataException("核验期间标准化证据文件发生变化，本次输出不能作为稳定输入证据。");
        }
        var ranges = new (string Name, Func<TrainingValues, int> Value)[]
        {
            ("speed", value => value.Speed), ("stamina", value => value.Stamina), ("power", value => value.Power),
            ("guts", value => value.Guts), ("wiz", value => value.Wisdom), ("vital", value => value.Vital), ("skill_point", value => value.SkillPoints)
        }.Select(field => acceptedValues.Count == 0 ? null : new TrainingValueRange(field.Name,
            acceptedValues.Min(field.Value), acceptedValues.Max(field.Value))).OfType<TrainingValueRange>().ToArray();
        var report = new TrainingPreviewAuditReport("1", TrainingPreviewSettlement.Version, fingerprints, responses, eligible,
            exact, vitalClips, counts, ranges, exclusions,
            ["OnlySuppliedPreviewSettlement", "ResultState2Only", "FailureOutcomeNotValidated", "AttributeCapClippingNotValidated",
                "VitalLowerClippingNotValidated", "RawPreCapGainsAndAttributeOverflowUnknown", "DoesNotAdvanceTurnOrResolveEvents",
                "NoCounterfactualTrainingGainGeneration", "ObservedInputRangesAreNotExhaustive"],
            eligible == 0 ? "NoEligibleSamples" : exact == eligible ? "ObservedTransitionsMatched" : "CounterexamplesFound");
        File.WriteAllText(Path.Combine(output, "training-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static TrainingParameterChange[]? Preview(JsonObject? section, int commandId, bool requireEnabled)
    {
        if (section?["command_info_array"] is not JsonArray commands) return null;
        var matches = commands.OfType<JsonObject>().Where(command => Int(command, "command_id") == commandId && Int(command, "command_type") == 1).ToArray();
        if (matches.Length != 1 || requireEnabled && Int(matches[0], "is_enable") != 1
            || matches[0]["params_inc_dec_info_array"] is not JsonArray changes) return null;
        return changes.Select(change => change is JsonObject value
            ? new TrainingParameterChange(Int(value, "target_type"), Int(value, "value"))
            : throw new InvalidDataException("训练预览增量项不是对象。")).ToArray();
    }

    static TrainingValues Values(JsonObject value) => new(Int(value, "speed"), Int(value, "stamina"), Int(value, "power"),
        Int(value, "guts"), Int(value, "wiz"), Int(value, "vital"), Int(value, "skill_point"));
    static TrainingLimits Limits(JsonObject value) => new(Int(value, "max_speed"), Int(value, "max_stamina"), Int(value, "max_power"),
        Int(value, "max_guts"), Int(value, "max_wiz"), Int(value, "max_vital"));
    static int Int(JsonObject value, string name) => value[name] is JsonValue field && field.TryGetValue<int>(out var number)
        ? number : throw new InvalidDataException($"训练核验缺少整数{name}。");
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "NormalizedPioneerEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
