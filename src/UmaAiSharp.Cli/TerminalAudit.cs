using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UmaAiSharp.Cli.Evidence;

public sealed record TerminalAuditReport(IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<EvidenceIssue> Issues,
    IReadOnlyList<string> Limitations, string Status);

public static class TerminalAudit
{
    static readonly string[] Attributes = ["speed", "stamina", "power", "guts", "wiz"];

    public static TerminalAuditReport Run(string inputDirectory, string outputDirectory)
    {
        var paths = new[] { "pioneer-observations.jsonl", "exchanges.jsonl" }
            .Select(name => Path.Combine(inputDirectory, name)).ToArray();
        var fingerprints = paths.Select(Fingerprint).ToArray();
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("终局核验输出目录非空，拒绝覆盖。");
        var exchanges = File.ReadLines(paths[1]).Select(line =>
            JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("请求响应配对记录不能为 null。"))
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<(string Source, string Run)>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        Directory.CreateDirectory(output);
        using var writer = new StreamWriter(new FileStream(Path.Combine(output, "terminal-observations.jsonl"), FileMode.CreateNew));
        foreach (var line in File.ReadLines(paths[0]))
        {
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("终局观测不能为 null。");
            var endpoint = observation.Endpoint[(observation.Endpoint.LastIndexOf('/') + 1)..];
            if (endpoint == "pioneer_complete") Count("ScenarioCompleteIsNotFinish");
            if (endpoint != "finish") continue;
            Count("FinishResponses");
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint || !exchange.Responses.Contains(new(observation.Source, observation.PacketId)))
            { Count("UnpairedFinishResponses"); continue; }
            try
            {
                var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint)
                    ?? throw new InvalidDataException("finish 缺少 common。");
                var selected = SelectFinishedCharacter(common);
                if (observation.RunKey is null) throw new InvalidDataException("finish 缺少育成身份。");
                var chara = (JsonObject)common["chara_info"]!;
                var unspent = Integer(chara, "skill_point");
                if (unspent < 0) throw new InvalidDataException("终局剩余技能 Pt 不能为负数。");
                var total = Integer(selected, "total_skill_pt");
                if (!seen.Add((observation.Source, observation.RunKey)))
                { Count("RepeatedFinishResponses"); continue; }
                Count("MatchedFinishResults");
                Count(unspent == total ? "TotalPtEqualsUnspent" : "TotalPtDiffersFromUnspent");
                // Observed totals are not future-only gains; path overflow cannot be reconstructed here.
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    observation.Source, observation.PacketId, observation.RunKey,
                    attributes = Attributes.ToDictionary(key => key, key => Integer(selected, key)),
                    limits = Attributes.ToDictionary(key => key, key => Integer(chara, "max_" + key)),
                    unspentSkillPoints = unspent, totalSkillPoints = total,
                    rankScore = Integer(selected, "rank_score"), rank = Integer(selected, "rank"),
                    skills = selected["skill_array"], factors = selected["factor_info_array"],
                    extendedFactors = selected["factor_extend_array"], scenarioData = selected["scenario_data_array"],
                    projectionPresence = new[] { "factor_info_array", "factor_extend_array", "scenario_data_array" }
                        .ToDictionary(key => key, key => !selected.ContainsKey(key) ? "Missing" : selected[key] is null ? "Null" : "Present"),
                }, EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException)
            {
                Count("RejectedFinishResponses");
                issues.Add(new("FinishResultMismatch", observation.Source, observation.PacketId, error.Message));
            }
        }
        for (var i = 0; i < paths.Length; i++)
            if (Fingerprint(paths[i]) != fingerprints[i]) throw new InvalidDataException("终局核验期间输入发生变化。");
        var report = new TerminalAuditReport(fingerprints, counts, issues,
            ["ObservedFinishProjectionOnly", "FinalRewardsAndFactorGenerationNotImplemented",
                "TotalSkillPointsAreNotFutureGains", "PathOverflowNotReconstructed", "NoAutonomousTrajectoryValidation"],
            issues.Count == 0 && counts.GetValueOrDefault("MatchedFinishResults") > 0
                && counts.GetValueOrDefault("UnpairedFinishResponses") == 0 ? "ObservedFinishResultsMatched" : "FinishValidationIncomplete");
        using var stream = new FileStream(Path.Combine(output, "terminal-audit.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, report, EvidenceJson.Options);
        return report;
    }

    public static JsonObject SelectFinishedCharacter(JsonObject common)
    {
        var chara = common["chara_info"] as JsonObject ?? throw new InvalidDataException("finish 缺少 chara_info。");
        if (Integer(chara, "scenario_id") != 11 || Integer(chara, "state") != 4 || Integer(chara, "playing_state") != 11)
            throw new InvalidDataException("finish 不属于已观测的 Pioneer 完成状态（scenario=11、state=4、playing_state=11）。");
        var id = Integer(common, "trained_chara_id");
        var inventory = common["trained_chara"] as JsonArray ?? throw new InvalidDataException("finish 缺少 trained_chara 数组。");
        var selected = inventory.Select(item => item as JsonObject ?? throw new InvalidDataException("殿堂条目必须为对象。"))
            .Where(item => Integer(item, "trained_chara_id") == id).ToArray();
        if (selected.Length != 1) throw new InvalidDataException($"finish 的 trained_chara_id 对应 {selected.Length} 个条目，要求唯一。");
        foreach (var key in Attributes)
        {
            var value = Integer(selected[0], key);
            if (value < 0 || value != Integer(chara, key) || value > Integer(chara, "max_" + key))
                throw new InvalidDataException($"终局属性 {key} 与 chara_info 或上限不一致。");
        }
        if (selected[0]["skill_array"] is not JsonArray skills || chara["skill_array"] is not JsonArray currentSkills
            || !JsonNode.DeepEquals(skills, currentSkills))
            throw new InvalidDataException("终局 skill_array 与 chara_info 不一致或字段缺失。");
        foreach (var key in new[] { "total_skill_pt", "rank_score", "rank" })
            if (Integer(selected[0], key) < 0) throw new InvalidDataException($"终局 {key} 不能为负数。");
        return (JsonObject)selected[0].DeepClone();
    }

    static long Integer(JsonObject value, string key)
    {
        if (value[key] is JsonValue number)
        {
            if (number.TryGetValue<long>(out var integer)) return integer;
            if (number.TryGetValue<int>(out var smallInteger)) return smallInteger;
        }
        throw new InvalidDataException($"字段 {key} 缺失或不是整数。");
    }

    static SourceFingerprint Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return new(Path.GetFileName(path), "TerminalAuditInput", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
