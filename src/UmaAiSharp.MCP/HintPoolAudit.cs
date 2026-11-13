using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP.Evidence;

public sealed record HintPoolAuditReport(string RuleVersion, IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<EvidenceIssue> Issues, IReadOnlyList<string> Limitations);

public static class HintPoolAudit
{
    static readonly string[] Attributes = ["speed", "stamina", "power", "guts", "wiz"];

    public static HintPoolAuditReport Run(string researchDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("Hint 奖池核验输出目录非空，拒绝覆盖。");
        var rawPath = Path.Combine(researchDirectory, "hints.bin.gz");
        var casesPath = Path.Combine(researchDirectory, "analysis-02", "rewards.jsonl");
        var proofPath = Path.Combine(researchDirectory, "comparison-verified.json");
        var paths = new[] { rawPath, casesPath, proofPath, Path.Combine(researchDirectory, "timeline.bin.gz"), masterPath };
        var fingerprints = paths.Select(Fingerprint).ToArray();
        var proof = Object(JsonNode.Parse(File.ReadAllText(proofPath)));
        foreach (var path in new[] { rawPath, casesPath, paths[3] })
        {
            var expected = Objects(proof, "inputs").Single(row => Path.GetFileName(Text(row, "path")) == Path.GetFileName(path));
            if (Text(expected, "sha256") != Fingerprint(path).Sha256)
                throw new InvalidDataException("云端 Hint 原包或配对定位与研究指纹不一致。");
        }
        var captures = ReadCaptures(rawPath);
        var skills = new Dictionary<int, HintSkill>();
        var pools = new Dictionary<int, List<HintSkill>>();
        var rewards = new Dictionary<(int Card, int Group), TrainingValues>();
        using (var db = EvidenceAudit.OpenReadOnly(masterPath))
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,group_id,rarity FROM skill_data";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) skills.Add(reader.GetInt32(0), new(reader.GetInt32(1), reader.GetInt32(2)));
            command.CommandText = "SELECT support_card_id,hint_gain_type,hint_group,hint_value_1,hint_value_2 FROM single_mode_hint_gain";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var card = rows.GetInt32(0);
                var type = rows.GetInt32(1);
                if (type == 0)
                {
                    if (!pools.TryGetValue(card, out var pool)) pools[card] = pool = [];
                    pool.Add(skills[rows.GetInt32(3)]);
                }
                else if (type == 1)
                {
                    var key = (card, rows.GetInt32(2));
                    var value = rewards.GetValueOrDefault(key);
                    var amount = rows.GetInt32(4);
                    rewards[key] = rows.GetInt32(3) switch
                    {
                        1 => value with { Speed = checked(value.Speed + amount) },
                        2 => value with { Stamina = checked(value.Stamina + amount) },
                        3 => value with { Power = checked(value.Power + amount) },
                        4 => value with { Guts = checked(value.Guts + amount) },
                        5 => value with { Wisdom = checked(value.Wisdom + amount) },
                        30 => value with { SkillPoints = checked(value.SkillPoints + amount) },
                        _ => throw new UnknownMechanismException("Hint 属性奖励 master 含未支持目标。")
                    };
                }
                else throw new UnknownMechanismException($"Hint master 含未支持类型 {type}。");
            }
        }
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<long>();
        void Count(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
        Directory.CreateDirectory(output);
        using var writer = new StreamWriter(new FileStream(Path.Combine(output, "hint-pool-samples.jsonl"), FileMode.CreateNew));
        foreach (var line in File.ReadLines(casesPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = Object(JsonNode.Parse(line));
            var id = Long(locator, "id");
            Count("LocatedRewards");
            if (!seen.Add(id)) throw new InvalidDataException("Hint 奖励定位重复，拒绝重复计数。");
            try
            {
                var current = captures[id];
                var previous = captures[Long(locator, "preId")];
                var response = PacketAudit.Decode(current.Response);
                var beforeResponse = PacketAudit.Decode(previous.Response);
                var request = Object(PacketAudit.Decode(current.Request)["single_mode_check_event_request_common"]);
                var beforeData = Object(beforeResponse["data"]);
                var afterData = Object(response["data"]);
                var before = Object(beforeData["chara_info"]);
                var after = Object(afterData["chara_info"]);
                ValidateIdentity(current, response, before, locator);
                ValidateIdentity(previous, beforeResponse, before, null);
                var turn = Int(before, "turn");
                if (!Text(current.Metadata, "endpoint").EndsWith("/single_mode_pioneer/check_event", StringComparison.Ordinal)
                    || Text(previous.Metadata, "actor") != Text(current.Metadata, "actor")
                    || Int(request, "choice_number") != 0
                    || Int(request, "current_turn") != turn || Int(locator, "turn") != turn
                    || Long(before, "single_mode_chara_id") != Long(after, "single_mode_chara_id")
                    || Int(after, "turn") - turn is not (0 or 1)
                    || Long(Object(beforeResponse["data_headers"]), "servertime") > Long(Object(response["data_headers"]), "servertime"))
                    throw new InvalidDataException("Hint 领取端点、选择、育成身份或回合/时间关系不一致。");
                if (Attributes.Any(key => Int(before, "max_" + key) != Int(after, "max_" + key)))
                    throw new InvalidDataException("Hint 领取窗口改变属性上限，不能套用单个 Hint 奖励结算。");
                var ordering = Text(locator, "ordering");
                if (ordering == "SidChain")
                {
                    if (string.IsNullOrEmpty(Text(previous.Metadata, "nextSid"))
                        || Text(previous.Metadata, "nextSid") != Text(current.Metadata, "requestSid"))
                        throw new InvalidDataException("Hint 定位的 SID 链不匹配。");
                }
                else if (ordering != "ServerTimeUnique") throw new InvalidDataException("未识别的 Hint 配对证据类型。");
                var pending = Objects(beforeData, "unchecked_event_array").Single(value => Int(value, "event_id") == Int(request, "event_id")
                    && Int(value, "chara_id") == Int(request, "chara_id"));
                if (Int(pending, "story_id") != PioneerEvent.TrainingHintStory)
                    throw new InvalidDataException("配对请求指向的故事不属于训练 Hint。");
                var position = Int(Object(pending["event_contents_info"]), "tips_training_partner_id");
                var support = Objects(before, "support_card_array").Single(value => Int(value, "position") == position);
                var card = Int(support, "support_card_id");
                if (card != Int(locator, "cardId")) throw new InvalidDataException("Hint 卡位与研究定位不一致。");
                var hints = Hints(before);
                var owned = Objects(before, "skill_array").Select(value => skills[Int(value, "skill_id")]).ToImmutableArray();
                var ownedBefore = Objects(before, "skill_array").OrderBy(value => Int(value, "skill_id")).ToArray();
                var ownedAfter = Objects(after, "skill_array").OrderBy(value => Int(value, "skill_id")).ToArray();
                if (ownedBefore.Length != ownedAfter.Length || ownedBefore.Zip(ownedAfter).Any(pair => !JsonNode.DeepEquals(pair.First, pair.Second)))
                    throw new InvalidDataException("Hint 领取窗口同时改变已学技能，不能归因于单次 Hint 奖励。");
                if (!pools.TryGetValue(card, out var skillPool)) throw new UnknownMechanismException("支援卡没有已解析的技能候选池。");
                var evaluated = HintRewardPool.Evaluate(skillPool.ToImmutableArray(), hints, owned);
                var eligible = evaluated.Where(value => value.Exclusion == HintPoolExclusion.None).Select(value => value.Skill).ToArray();
                var afterHints = Hints(after);
                var changes = afterHints.Where(value => !hints.Contains(value)).ToArray();
                if (hints.Any(hint => !afterHints.Any(value => value.Skill == hint.Skill && value.Level >= hint.Level)))
                    throw new InvalidDataException("Hint 领取窗口移除了已有 Hint 或降低其等级。");
                var beforeValues = Values(before);
                var afterValues = Values(after);
                var kind = changes.Length == 1 ? "Skill" : changes.Length == 0 ? "Attribute" : "Mixed";
                if (kind == "Skill")
                {
                    if (!eligible.Contains(changes[0].Skill) || beforeValues != afterValues)
                        throw new InvalidDataException("实际 Hint 不在有效奖池中，或同窗口混合属性/Pt变化。");
                }
                else if (kind == "Attribute")
                {
                    var caps = new TrainingLimits(Int(before, "max_speed"), Int(before, "max_stamina"), Int(before, "max_power"),
                        Int(before, "max_guts"), Int(before, "max_wiz"), 0);
                    var candidates = rewards.Where(value => value.Key.Card == card).Select(value => value.Value).ToArray();
                    var matched = candidates.Where(value => HintRewardPool.ApplyAttributeReward(beforeValues, caps, value).Resources == afterValues).ToArray();
                    if (matched.Length == 0) throw new InvalidDataException("Hint 属性变化与 master 奖励组不匹配。");
                    if (beforeValues == afterValues)
                    {
                        var blocked = Array(Object(afterData["not_up_parameter_info"]), "status_type_array").Select(value => value!.GetValue<int>()).ToHashSet();
                        if (!matched.Any(value => Targets(value).SetEquals(blocked)))
                            throw new InvalidDataException("零净收益缺少对应属性封顶标记，不能认作属性奖励。");
                    }
                }
                else throw new InvalidDataException("同次 Hint 领取改变多个技能提示，不能套单次奖池规则。");
                Count("VerifiedRewards"); Count(kind + "Rewards"); Count(ordering + "Pairs");
                if (evaluated.Any(value => value.Exclusion == HintPoolExclusion.AlreadyLearned)) Count("LearnedOverlapRewards");
                if (eligible.Length == 0) Count("EmptyPoolRewards");
                if (eligible.Length == 2) { Count("N2Rewards"); if (kind == "Attribute") Count("N2AttributeRewards"); }
                writer.WriteLine(JsonSerializer.Serialize(new { id, beforeId = Long(locator, "preId"), card, turn, ordering,
                    kind, pool = evaluated, effectiveCount = eligible.Length }, EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or UnknownMechanismException
                or KeyNotFoundException or ArgumentException or OverflowException)
            {
                Count("RejectedRewards");
                issues.Add(new("HintPoolInputOrOutcomeMismatch", "cloud-export", id, error.Message));
            }
        }
        for (var index = 0; index < paths.Length; index++)
            if (Fingerprint(paths[index]) != fingerprints[index]) throw new InvalidDataException("Hint 核验期间输入变化。");
        var report = new HintPoolAuditReport(HintRewardPool.Version, fingerprints, counts, issues,
            ["FrozenResearchPairLocatorsNotNewIndependentPairing", "ServerTimePairsInheritVerifiedStudyTimeline",
                "EffectivePoolOnlyNotDrawWeightsOrFilteringTime", "WhiteCandidateAndWhiteGoldLearnedOverlapOnly",
                "SkillHintLevelIncreaseAndHintSelectionNotGenerated", "AllVersionsAcceptedPerUserInstruction"]);
        using var stream = new FileStream(Path.Combine(output, "hint-pool-audit.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, report, EvidenceJson.Options);
        return report;
    }

    sealed record Capture(JsonObject Metadata, byte[] Request, byte[] Response);
    static Dictionary<long, Capture> ReadCaptures(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        var result = new Dictionary<long, Capture>();
        while (true)
        {
            var first = gzip.ReadByte();
            if (first == -1) return result;
            var rest = reader.ReadBytes(3);
            if (rest.Length != 3) throw new InvalidDataException("云端导出长度头截断。");
            var length = first | rest[0] << 8 | rest[1] << 16 | rest[2] << 24;
            var metadata = Object(JsonNode.Parse(ReadPart(length)));
            var request = ReadPart(reader.ReadInt32());
            var response = ReadPart(reader.ReadInt32());
            result.Add(Long(metadata, "id"), new(metadata, request, response));
        }

        byte[] ReadPart(int size)
        {
            if (size < 0) throw new InvalidDataException("完整 Hint 导出包含缺失片段。");
            var bytes = reader.ReadBytes(size);
            if (bytes.Length != size) throw new InvalidDataException("云端导出内容截断。");
            return bytes;
        }
    }

    static void ValidateIdentity(Capture capture, JsonObject response, JsonObject before, JsonObject? locator)
    {
        var headers = Object(response["data_headers"]);
        var actor = PioneerNormalizer.Hash("viewer\n" + Long(headers, "viewer_id"));
        if (Int(headers, "result_code") != 1 || actor != Text(capture.Metadata, "actor") || Int(before, "scenario_id") != 11)
            throw new InvalidDataException("Hint 原始响应不是成功的同账号 Pioneer 状态。");
        if (locator is not null && (actor != Text(locator, "actor")
            || PioneerNormalizer.Hash(actor + "\n" + Long(before, "single_mode_chara_id")) != Text(locator, "runKey")
            || Convert.ToHexStringLower(SHA256.HashData(capture.Response)) != Text(locator, "responseHash")))
            throw new InvalidDataException("Hint 原包身份或 payload 哈希与定位不一致。");
    }

    static ImmutableArray<HintLevel> Hints(JsonObject chara)
    {
        var hints = Objects(chara, "skill_tips_array")
            .Select(value => new HintLevel(new(Int(value, "group_id"), Int(value, "rarity")), Int(value, "level"))).ToImmutableArray();
        if (hints.Any(value => value.Skill.GroupId <= 0 || value.Skill.Rarity <= 0 || value.Level is < 1 or > 5)
            || hints.Select(value => value.Skill).Distinct().Count() != hints.Length)
            throw new InvalidDataException("Hint 原包包含无效技能组、稀有度、等级或重复提示。");
        return hints;
    }
    static TrainingValues Values(JsonObject chara) => new(Int(chara, "speed"), Int(chara, "stamina"), Int(chara, "power"),
        Int(chara, "guts"), Int(chara, "wiz"), 0, Int(chara, "skill_point"));
    static HashSet<int> Targets(TrainingValues value) => new[] { (1, value.Speed), (2, value.Stamina), (3, value.Power),
        (4, value.Guts), (5, value.Wisdom), (30, value.SkillPoints) }.Where(pair => pair.Item2 != 0).Select(pair => pair.Item1).ToHashSet();
    static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new InvalidDataException("Hint 原包缺少所需对象。");
    static JsonArray Array(JsonObject node, string key) => node[key] as JsonArray ?? throw new InvalidDataException($"Hint 原包缺少数组 {key}。");
    static IEnumerable<JsonObject> Objects(JsonObject node, string key) => Array(node, key).Select(Object);
    static int Int(JsonObject node, string key) => node[key]?.GetValue<int>() ?? throw new InvalidDataException($"Hint 原包缺少整数 {key}。");
    static long Long(JsonObject node, string key) => node[key]?.GetValue<long>() ?? throw new InvalidDataException($"Hint 原包缺少整数 {key}。");
    static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? throw new InvalidDataException($"Hint 原包缺少字符串 {key}。");
    static SourceFingerprint Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new(Path.GetFileName(path), "HintPoolInput", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
