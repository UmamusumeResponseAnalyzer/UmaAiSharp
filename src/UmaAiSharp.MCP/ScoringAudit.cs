using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.Pioneer.Scoring;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP.Evidence;

public sealed record ScoringAuditObservation(string InputKey, string Source, long PacketId, string Endpoint,
    string JsonPath, string Kind, CharacterScoringInput Input, int ObservedScore, CandidateScore? Candidate,
    int? Residual, string Status, IReadOnlyList<int> MissingSkillIds);
public sealed record ScoringCoverage(string Attribute, int? Minimum, int? Maximum,
    IReadOnlyList<ScoreValueCount> ObservedValues);
public sealed record ScoreValueCount(int Value, int Count);
public sealed record ScoringAuditReport(string SchemaVersion, string CandidateVersion,
    IReadOnlyList<SourceFingerprint> Sources, long ResponsePackets, long CandidateObservations,
    int UniqueInputScorePairs, int DuplicateObservations, int IncompleteObservations, int EvaluatedStates, int ExactMatches,
    int UnscoredStates, int ConflictingInputScores, IReadOnlyList<ScoreValueCount> ResidualDistribution,
    IReadOnlyList<ScoringCoverage> AttributeCoverage, IReadOnlyList<int> ObservedSkillIds,
    IReadOnlyList<int> MissingMasterSkillIds, IReadOnlyList<string> AppVersions,
    VersionBinding VersionBinding, IReadOnlyList<string> ApplicabilityLimits,
    IReadOnlyList<EvidenceIssue> Issues, ValidationSummary Validation);

public static class ScoringAudit
{
    public static ScoringAuditReport Run(string packetsPath, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = Directory.Exists(packetsPath)
            ? Directory.GetFiles(packetsPath, "*.sqlite", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : [Path.GetFullPath(packetsPath)];
        if (paths.Length == 0) throw new InvalidDataException("评分审计没有输入包库。");
        var sources = paths.Select((path, index) => (Path: Path.GetFullPath(path), Alias: $"packets-{index + 1:D4}")).ToArray();
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("评分审计输出目录非空，拒绝覆盖已有证据。");
        var fingerprints = sources.Select(source => Fingerprint(source.Path, source.Alias, "DebugSaveAllPackets"))
            .Append(Fingerprint(masterPath, "master", "GameMasterSqlite")).ToArray();
        var master = ReadSkillMaster(masterPath);
        Directory.CreateDirectory(output);
        using var observations = new StreamWriter(new FileStream(Path.Combine(output, "scoring-observations.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<(string Input, int Score)>();
        var scoresByInput = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var evaluated = new List<ScoringAuditObservation>();
        var coverageInputs = new List<CharacterScoringInput>();
        var missingSkills = new SortedSet<int>();
        var observedSkills = new SortedSet<int>();
        var apps = new SortedSet<string>(StringComparer.Ordinal);
        var resources = new SortedSet<string>(StringComparer.Ordinal);
        var responses = 0L;
        var candidates = 0L;
        var duplicates = 0;
        var unscored = 0;

        foreach (var (path, alias) in sources)
        {
            using var connection = EvidenceAudit.OpenReadOnly(path);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,endpoint,app_ver,res_ver,payload FROM packets WHERE direction='response' ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                responses++;
                var packetId = reader.GetInt64(0);
                var endpoint = reader.GetString(1);
                JsonObject root;
                try { root = PacketAudit.Decode((byte[])reader.GetValue(4)); }
                catch (Exception error) when (error is MessagePackSerializationException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    issues.Add(new("ScorePacketDecodeFailure", alias, packetId, $"响应无法解码（{error.GetType().Name}）；无法确认其中是否含评分样本。"));
                    continue;
                }
                foreach (var sample in Extract(root, issues, alias, packetId))
                {
                    candidates++;
                    if (!reader.IsDBNull(2)) apps.Add(reader.GetString(2));
                    if (!reader.IsDBNull(3)) resources.Add(reader.GetString(3));
                    else issues.Add(new("ScoringPacketVersionMissing", alias, packetId, "评分观测缺少捕获时res_ver。"));
                    CharacterScoringInput input;
                    try { input = ReadScoringInput(sample.Character); }
                    catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
                    {
                        issues.Add(new("ScoringInputIncomplete", alias, packetId, $"{sample.Path}: {error.Message}"));
                        continue;
                    }
                    var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, EvidenceJson.Options)));
                    if (!scoresByInput.TryGetValue(key, out var inputScores)) scoresByInput.Add(key, inputScores = []);
                    inputScores.Add(sample.Score);
                    if (!seen.Add((key, sample.Score))) { duplicates++; continue; }
                    coverageInputs.Add(input);
                    foreach (var skill in input.Skills) observedSkills.Add(skill.Id);
                    var absent = input.Skills.Select(skill => skill.Id).Where(id => !master.ContainsKey(id)).Distinct().Order().ToArray();
                    foreach (var id in absent) missingSkills.Add(id);
                    CandidateScore? score = null;
                    var status = absent.Length == 0 ? "EvaluatedCandidate" : "MissingMasterSkill";
                    if (absent.Length == 0)
                    {
                        try { score = ScoringCandidates.Evaluate(input, master); }
                        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
                        {
                            status = "CandidateDomainUnsupported";
                            issues.Add(new(status, alias, packetId, $"{sample.Path}: {error.Message}"));
                        }
                    }
                    var observation = new ScoringAuditObservation(key, alias, packetId, endpoint, sample.Path, sample.Kind,
                        input, sample.Score, score, score is null ? null : checked(sample.Score - score.Total), status, absent);
                    observations.WriteLine(JsonSerializer.Serialize(observation, EvidenceJson.LineOptions));
                    if (score is null) unscored++;
                    else evaluated.Add(observation);
                }
            }
        }

        foreach (var (path, alias) in sources.Append((Path.GetFullPath(masterPath), "master")))
        {
            var before = fingerprints.Single(source => source.Alias == alias);
            var after = Fingerprint(path, alias, before.Kind);
            if (before.Sha256 != after.Sha256 || before.Bytes != after.Bytes)
                issues.Add(new("ScoringSourceChanged", alias, null, "审计期间源文件改变，结果不是固定输入快照证据。"));
        }
        var coverage = new (string Name, Func<CharacterScoringInput, int> Value)[]
        {
            ("speed", input => input.Speed), ("stamina", input => input.Stamina), ("power", input => input.Power),
            ("guts", input => input.Guts), ("wiz", input => input.Wisdom)
        }.Select(attribute =>
        {
            var values = coverageInputs.Select(attribute.Value).GroupBy(value => value).OrderBy(group => group.Key)
                .Select(group => new ScoreValueCount(group.Key, group.Count())).ToArray();
            return new ScoringCoverage(attribute.Name, values.FirstOrDefault()?.Value, values.LastOrDefault()?.Value, values);
        }).ToArray();
        var blockers = new List<string>
        {
            "AttributeAndSkillComponentsNotIndependentlyIdentified", "UnobservedInputsNotValidated"
        };
        if (evaluated.Count == 0) blockers.Add("NoEvaluableScoringStates");
        if (evaluated.Any(sample => sample.Residual != 0)) blockers.Add("CandidateScoreMismatch");
        if (unscored != 0) blockers.Add("UnscoredStates");
        if (issues.Count != 0) blockers.Add("ScoringEvidenceIssues");
        var conflicts = scoresByInput.Count(pair => pair.Value.Count > 1);
        if (conflicts != 0) blockers.Add("SameCandidateInputDifferentObservedScores");
        var report = new ScoringAuditReport("1", ScoringCandidates.Version, fingerprints, responses, candidates,
            seen.Count, duplicates, issues.Count(issue => issue.Code == "ScoringInputIncomplete"),
            evaluated.Count, evaluated.Count(sample => sample.Residual == 0), unscored, conflicts,
            evaluated.GroupBy(sample => sample.Residual!.Value).OrderBy(group => group.Key)
                .Select(group => new ScoreValueCount(group.Key, group.Count())).ToArray(),
            coverage, observedSkills.ToArray(), missingSkills.ToArray(), apps.ToArray(),
            new("Unverified", null, resources.ToArray(), "采用本地master进行核验；版本未绑定属于适用性限制，不作为本次核验门槛。"),
            ["MasterResourceVersionUnverified", "ArchiveCreationVersionUnknown", "GameRegionNotIndependentlyVerified"],
            issues, new("Blocked", blockers));
        File.WriteAllText(Path.Combine(output, "scoring-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static CharacterScoringInput ReadScoringInput(JsonObject character)
    {
        var owned = Objects(Array(character, "skill_array"), "skill_array")
            .Select(skill => new OwnedScoringSkill(Int(skill, "skill_id"), Int(skill, "level")))
            .OrderBy(skill => skill.Id).ThenBy(skill => skill.Level).ToImmutableArray();
        if (owned.Select(skill => skill.Id).Distinct().Count() != owned.Length)
            throw new InvalidDataException("skill_array 含重复技能 ID。");
        return new(Int(character, "speed"), Int(character, "stamina"), Int(character, "power"), Int(character, "guts"), Int(character, "wiz"),
            [Int(character, "proper_running_style_nige"), Int(character, "proper_running_style_senko"),
                Int(character, "proper_running_style_sashi"), Int(character, "proper_running_style_oikomi")],
            [Int(character, "proper_distance_short"), Int(character, "proper_distance_mile"),
                Int(character, "proper_distance_middle"), Int(character, "proper_distance_long")], owned);
    }

    static FrozenDictionary<int, ScoringSkillDefinition> ReadSkillMaster(string path)
    {
        using var db = EvidenceAudit.OpenReadOnly(path);
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,grade_value,tag_id FROM skill_data ORDER BY id";
        using var reader = command.ExecuteReader();
        var skills = new Dictionary<int, ScoringSkillDefinition>();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var tags = reader.GetString(2);
            skills.Add(id, new(id, reader.GetInt32(1),
                tags.Length == 0 ? [] : [.. tags.Split('/').Select(value => int.Parse(value, CultureInfo.InvariantCulture))]));
        }
        return skills.ToFrozenDictionary();
    }

    static IEnumerable<(JsonObject Character, int Score, string Path, string Kind)> Extract(
        JsonNode node, List<EvidenceIssue> issues, string source, long packetId, string path = "$")
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                if (array[index] is { } child)
                    foreach (var sample in Extract(child, issues, source, packetId, $"{path}[{index}]")) yield return sample;
            yield break;
        }
        if (node is not JsonObject map) yield break;
        if (map.ContainsKey("trained_chara_id") && map.ContainsKey("rank_score"))
        {
            if (map["rank_score"] is JsonValue rank && rank.TryGetValue<int>(out var score))
                yield return (map, score, path, "TrainedCharaArchive");
            else issues.Add(new("InvalidObservedRankScore", source, packetId, $"{path}.rank_score不是整数。"));
        }
        if (map["single_mode_finish_common"] is JsonObject finish)
        {
            if (finish["chara_info"] is JsonObject character && finish["trained_chara"] is JsonArray archive
                && finish["trained_chara_id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id))
            {
                var matches = archive.OfType<JsonObject>().Where(chara => chara["trained_chara_id"] is JsonValue archiveId
                    && archiveId.TryGetValue<long>(out var value) && value == id).ToArray();
                if (matches.Length == 1 && matches[0]["rank_score"] is JsonValue rank && rank.TryGetValue<int>(out var score))
                    yield return (character, score, path + ".single_mode_finish_common.chara_info", "FinishCurrentChara");
                else issues.Add(new("FinishScorePairingFailed", source, packetId, "终局chara_info不能唯一匹配同响应trained_chara_id对应的评分。"));
            }
            else issues.Add(new("FinishScoreShapeIncomplete", source, packetId, "终局评分配对缺少chara_info、trained_chara或trained_chara_id。"));
        }
        foreach (var (name, child) in map)
            if (child is JsonObject or JsonArray)
                foreach (var sample in Extract(child, issues, source, packetId, path + "." + name)) yield return sample;
    }

    static SourceFingerprint Fingerprint(string path, string alias, string kind)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new(alias, kind, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
