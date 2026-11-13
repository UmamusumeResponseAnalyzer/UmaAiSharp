using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Races;

namespace UmaAiSharp.MCP.Evidence;

public sealed record RacePointComponentCase(EvidenceReference Before, EvidenceReference After, long RequestRecordId,
    RacePointContext Context, int BeforePoints, int AfterPoints, int CandidateGain, bool Matches);
public sealed record TuckerPointComponentCase(EvidenceReference Before, EvidenceReference After, long RequestRecordId,
    TuckerEventContext Context, PioneerPointResources ResourcesBefore, PioneerPointResources ResourcesAfter,
    PioneerPointResources Candidate, bool Matches);
public sealed record DevelopmentComponentAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Inputs,
    int RaceSelections, int RaceMatches, int RaceSelectionsWithoutAgreedLabel, int TuckerSelections, int TuckerMatches,
    ValidationSummary Validation);

public static partial class DevelopmentComponentAudit
{
    // Research labels are admissible only against the exact normalized input used by both studies.
    public static DevelopmentComponentAuditReport Run(string inputDirectory, string researchDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(inputDirectory);
        var research = Path.GetFullPath(researchDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("发展分量核验输出目录非空，拒绝覆盖已有证据。");
        var construction = Path.Combine(research, "construction-checkpoints");
        var provenancePath = Path.Combine(construction, "run-02", "provenance.json");
        var provenance = ReadJson(provenancePath);
        var paths = new List<(string Path, string Alias)> { (provenancePath, "construction-provenance") };
        foreach (var expected in provenance.GetProperty("inputs").EnumerateObject())
        {
            var path = Path.Combine(input, expected.Name);
            if (Fingerprint(path, expected.Name).Sha256 != expected.Value.GetString())
                throw new InvalidDataException($"发展分量研究标签与 {expected.Name} 的完整 SHA-256 不匹配；不能把旧来源标签用于新增或重导出的观测。");
            paths.Add((path, expected.Name));
        }
        var transitionsPath = Path.Combine(construction, "run-02", "transitions.jsonl");
        var analysisPath = Path.Combine(construction, "analysis-02", "summary.json");
        if (Fingerprint(transitionsPath, "transitions").Sha256 != ReadJson(analysisPath).GetProperty("inputSha256").GetString())
            throw new InvalidDataException("建设研究转移文件与 analysis-02 输入 SHA-256 不匹配。");
        var labelsPath = Path.Combine(construction, "deep-03", "race-labels.json");
        var racePath = Path.Combine(research, "races", "hypotheses-02", "pt-candidate.json");
        var raceInputsPath = Path.Combine(research, "races", "run-02", "summary.json");
        foreach (var expected in ReadJson(raceInputsPath).GetProperty("inputs").EnumerateArray())
        {
            var name = Path.GetFileName(expected.GetProperty("path").GetString()!);
            var local = paths.SingleOrDefault(x => x.Alias == name);
            if (local.Path is not null && Fingerprint(local.Path, name).Sha256 != expected.GetProperty("sha256").GetString())
                throw new InvalidDataException($"比赛研究标签与 {name} 的完整 SHA-256 不匹配。");
        }
        paths.AddRange([(transitionsPath, "construction-transitions"), (analysisPath, "construction-analysis"),
            (labelsPath, "construction-race-labels"), (racePath, "race-candidate-labels"), (raceInputsPath, "race-inputs")]);
        var fingerprints = paths.Select(x => Fingerprint(x.Path, x.Alias)).ToArray();
        var hashes = provenance.GetProperty("sourceHashes").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
        var raceLabels = ReadJson(racePath).EnumerateArray().Where(x => x.TryGetProperty("race", out _))
            .ToDictionary(x => (hashes[x.GetProperty("ref").GetProperty("source").GetString()!], x.GetProperty("ref").GetProperty("packetId").GetInt64()));
        var agreedLabels = new Dictionary<(string Source, long Record), bool>();
        foreach (var row in ReadJson(labelsPath).GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("ambiguousGoalCondition").GetBoolean()) continue;
            var key = (row.GetProperty("sourceSha256").GetString()!, row.GetProperty("afterRecordId").GetInt64());
            if (!raceLabels.TryGetValue(key, out var other)) continue;
            var target = row.GetProperty("target").GetBoolean();
            var resolvedTarget = row.GetProperty("goalRows").EnumerateArray().Any(x => Int(x, "condition_type") == 1
                && Int(x, "condition_id") == Int(row, "programId"));
            if (target != resolvedTarget || target != other.GetProperty("target").GetBoolean()
                || row.GetProperty("raceEndRecordId").GetInt64() != other.GetProperty("race").GetProperty("packetId").GetInt64())
                throw new InvalidDataException($"比赛分类研究在 source={key.Item1}、record={key.Item2} 不一致。");
            agreedLabels.Add(key, target);
        }
        using var masterRows = File.OpenText(Path.Combine(input, "master-rows.jsonl"));
        var master = CheckpointMasterData.ReadExport(masterRows, hashes["master"]);
        var raceRules = new PioneerRacePointGain(master);
        var raceCases = new List<RacePointComponentCase>();
        var tuckerCases = new List<TuckerPointComponentCase>();
        var consumedLabels = new HashSet<(string Source, long Record)>();
        var unlabelled = 0;
        foreach (var line in File.ReadLines(transitionsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var selected = row.GetProperty("selected");
            if (selected.GetArrayLength() != 1) continue;
            var story = Int(selected[0], "story_id");
            if (story is not (400011105 or 400011111)) continue;
            var source = row.GetProperty("sourceSha256").GetString()!;
            var beforeId = row.GetProperty("beforeRecordId").GetInt64();
            var requestId = row.GetProperty("requestRecordId").GetInt64();
            var afterId = row.GetProperty("afterRecordId").GetInt64();
            var before = row.GetProperty("before");
            var after = row.GetProperty("after");
            var chara = before.GetProperty("chara");
            var scenario = before.GetProperty("scenario");
            var info = scenario.GetProperty("pioneer_info");
            var request = row.GetProperty("request");
            if (!hashes.Values.Contains(source) || !(beforeId < requestId && requestId < afterId)
                || Int(request, "current_turn") != Int(chara, "turn") || Int(request, "event_id") != Int(selected[0], "event_id"))
                throw new InvalidDataException($"发展分量转移关联或请求上下文不一致：source={source}、record={afterId}。");
            var oldResources = new PioneerPointResources(Int(info, "pioneer_pt"), Int(chara, "skill_point"));
            var newResources = new PioneerPointResources(Int(after.GetProperty("scenario").GetProperty("pioneer_info"), "pioneer_pt"), Int(after.GetProperty("chara"), "skill_point"));
            if (story == 400011105)
            {
                if (!agreedLabels.TryGetValue((source, afterId), out var target)) { unlabelled++; continue; }
                if (!consumedLabels.Add((source, afterId)))
                    throw new InvalidDataException($"比赛核验重复引用同一响应：source={source}、record={afterId}。");
                var checkpoints = scenario.GetProperty("check_point_info_array").EnumerateArray().OrderBy(x => Int(x, "check_point_type")).ToArray();
                var context = new RacePointContext(Int(chara, "turn"), Int(chara, "playing_state"), Int(info, "deck_id"), story,
                    Int(request, "choice_number"), target, checkpoints.Length == 0 ? 0 : Int(checkpoints[^1], "check_point_type"),
                    checkpoints.Length == 0 ? 0 : Int(checkpoints[^1], "result_state"));
                var gain = raceRules.CalculateObservedEvent(context);
                raceCases.Add(new(new(source, beforeId), new(source, afterId), requestId, context,
                    oldResources.DevelopmentPoints, newResources.DevelopmentPoints, gain,
                    checked(oldResources.DevelopmentPoints + gain) == newResources.DevelopmentPoints));
            }
            else
            {
                var supports = chara.GetProperty("support_card_array").EnumerateArray().Where(x => Int(x, "support_card_id") == 30257).ToArray();
                if (supports.Length != 1) throw new InvalidDataException("Tucker 已观测资源分支需要唯一支援30257。");
                var support = supports[0];
                var context = new TuckerEventContext(Int(chara, "turn"), Int(chara, "playing_state"), Int(info, "deck_id"), story,
                    Int(selected[0], "play_timing"), Int(request, "choice_number"), Int(support, "support_card_id"), Int(support, "limit_break_count"), Int(support, "exp"));
                var candidate = TuckerPointSettlement.ApplyObservedChoiceOne(context, oldResources);
                tuckerCases.Add(new(new(source, beforeId), new(source, afterId), requestId, context, oldResources, newResources, candidate, candidate == newResources));
            }
        }
        if (raceCases.Count != agreedLabels.Count)
            throw new InvalidDataException("比赛研究标签未全部对应唯一明确选择转移。");
        for (var i = 0; i < paths.Count; i++)
            if (Fingerprint(paths[i].Path, paths[i].Alias) != fingerprints[i])
                throw new InvalidDataException($"核验过程中输入 {paths[i].Alias} 发生变化。");
        var report = new DevelopmentComponentAuditReport("1", fingerprints, raceCases.Count, raceCases.Count(x => x.Matches),
            unlabelled, tuckerCases.Count, tuckerCases.Count(x => x.Matches), new("CandidateOnly",
                ["仅核验输入指纹限定的明确事件局部分量；研究标签没有覆盖新增数据。", "未生成比赛结果、通用赛后奖励、低评价分支、其他Tucker选择或事件调度。"]));
        Directory.CreateDirectory(output);
        File.WriteAllLines(Path.Combine(output, "race-point-components.jsonl"), raceCases.Select(x => JsonSerializer.Serialize(x, EvidenceJson.LineOptions)), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(output, "tucker-point-components.jsonl"), tuckerCases.Select(x => JsonSerializer.Serialize(x, EvidenceJson.LineOptions)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "development-component-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static int Int(JsonElement value, string property) => value.GetProperty(property).GetInt32();
    static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "DevelopmentComponentEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
