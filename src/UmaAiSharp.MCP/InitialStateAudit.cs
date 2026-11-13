using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP.Evidence;

public sealed record InitialStateAuditReport(string SchemaVersion, string RuleVersion,
    IReadOnlyList<SourceFingerprint> Inputs, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<EvidenceIssue> Issues, IReadOnlyList<string> Limitations, string Status);

public static class InitialStateAudit
{
    static readonly (int Group, string Field)[] Aptitudes =
    [
        (11, "proper_ground_turf"), (12, "proper_ground_dirt"),
        (21, "proper_running_style_nige"), (22, "proper_running_style_senko"),
        (23, "proper_running_style_sashi"), (24, "proper_running_style_oikomi"),
        (31, "proper_distance_short"), (32, "proper_distance_mile"),
        (33, "proper_distance_middle"), (34, "proper_distance_long")
    ];

    public static InitialStateAuditReport Run(string studyDirectory, string evidenceDirectory,
        string packetsDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("开局核验输出目录非空，拒绝覆盖。");
        var startsPath = Path.Combine(studyDirectory, "starts.jsonl");
        var provenancePath = Path.Combine(studyDirectory, "provenance.json");
        var auditPath = Path.Combine(evidenceDirectory, "audit.json");
        var exchangePath = Path.Combine(evidenceDirectory, "exchanges.jsonl");
        var paths = new List<(string Path, string Alias)>
        {
            (startsPath, "start-locators"), (provenancePath, "study-provenance"),
            (auditPath, "capture-audit"), (exchangePath, "capture-exchanges"), (masterPath, "master")
        };
        var original = JsonSerializer.Deserialize<EvidenceReport>(File.ReadAllText(auditPath), EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("证据报告不能为 null。");
        var provenance = ReadObject(File.ReadAllText(provenancePath));
        var history = Objects(provenance, "inputs");
        var historicalAudit = history.Single(row => Path.GetFullPath(Text(row, "path")) == Path.GetFullPath(auditPath));
        if (Fingerprint(auditPath, "capture-audit").Sha256 != Text(historicalAudit, "sha256"))
            throw new InvalidDataException("开局定位研究与指定证据报告的哈希不一致。");
        var connections = new Dictionary<string, SqliteConnection>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in original.Sources.Where(source => source.Kind == "DebugSaveAllPackets"))
        {
            var origin = history.Single(row => Text(row, "sha256") == source.Sha256);
            var path = Path.Combine(packetsDirectory, Path.GetFileName(Text(origin, "path")));
            sourcePaths.Add(source.Alias, path);
            paths.Add((path, source.Alias));
        }
        var fingerprints = paths.Select(input => Fingerprint(input.Path, input.Alias)).ToArray();
        foreach (var source in original.Sources.Where(source => source.Kind == "DebugSaveAllPackets"))
            if (source.Sha256 != fingerprints.Single(value => value.Alias == source.Alias).Sha256)
                issues.Add(new("CaptureChangedSinceResearch", source.Alias, null,
                    "源库与研究时哈希不同；本次按原始行重新核对请求、响应和亲本，不沿用研究预测或匹配结论。"));
        var exchanges = File.ReadLines(exchangePath)
            .Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("exchange 不能为 null。"))
            .SelectMany(exchange => exchange.Responses.Select(reference => (reference, exchange)))
            .ToDictionary(value => value.reference, value => value.exchange);
        var master = new InitialStateMaster(masterPath);
        var seen = new HashSet<PacketReference>();
        var snapshotPackets = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var snapshotCache = new Dictionary<PacketReference, RawPacket>();
        Directory.CreateDirectory(outputDirectory);
        try
        {
            foreach (var (alias, path) in sourcePaths)
            {
                var db = EvidenceAudit.OpenReadOnly(path);
                connections.Add(alias, db);
                using var command = db.CreateCommand();
                command.CommandText = "SELECT id FROM packets WHERE direction='response' AND (endpoint IN ('/umamusume/load/index','/umamusume/pre_single_mode/index','/umamusume/trained_chara/load') OR endpoint LIKE '%/finish') ORDER BY id DESC";
                using var reader = command.ExecuteReader();
                var ids = new List<long>();
                while (reader.Read()) ids.Add(reader.GetInt64(0));
                snapshotPackets.Add(alias, ids.ToArray());
            }
            using var output = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "initial-state-samples.jsonl"),
                FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
            foreach (var line in File.ReadLines(startsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locator = ReadObject(line);
                var source = Text(locator, "source");
                var responseId = Long(locator, "packet");
                var requestId = Long(locator, "requestPacket");
                var reference = new PacketReference(source, responseId);
                if (!seen.Add(reference)) throw new InvalidDataException("开局定位包含重复响应，拒绝重复计数。");
                Count("LocatedStarts");
                try
                {
                    if (!connections.TryGetValue(source, out var db))
                        throw new InvalidDataException("开局来源别名未映射到输入库。");
                    if (!exchanges.TryGetValue(reference, out var exchange) || exchange.Status != "Paired"
                        || exchange.Requests.Count != 1 || exchange.Responses.Count != 1
                        || exchange.Requests[0] != new PacketReference(source, requestId))
                        throw new InvalidDataException("开局定位没有对应同源的唯一配对请求和响应。");
                    var request = ReadPacket(db, requestId);
                    var response = ReadPacket(db, responseId);
                    ValidatePair(db, request, response);
                    if (response.Endpoint != "/umamusume/single_mode_pioneer/start" || response.Endpoint != exchange.Endpoint)
                        throw new InvalidDataException("开局定位指向其他端点。");
                    var common = Object(request.Body, "single_mode_start_request_common");
                    var selected = Object(common, "start_chara");
                    if (Int(Object(selected, "rental_succession_trained_chara"), "trained_chara_id") != 0)
                        throw new UnknownMechanismException("租借或活动亲本选择未核验；本入口只解析已捕获的自有亲本。");
                    var chara = Object(PioneerNormalizer.GetCommon(Object(response.Body, "data"), response.Endpoint)
                        ?? throw new InvalidDataException("开局响应缺少公共数据。"), "chara_info");
                    if (Int(chara, "turn") != 1 || Int(chara, "scenario_id") != 11 || Int(selected, "scenario_id") != 11
                        || Int(chara, "card_id") != Int(selected, "card_id"))
                        throw new InvalidDataException("开局响应的回合、剧本或角色与请求不一致。");
                    var sources = ImmutableArray.CreateBuilder<InheritanceSource>();
                    var relations = ImmutableArray.CreateBuilder<RelationSource>();
                    var usedSnapshots = new List<object>();
                    var parentRefs = Objects(locator, "parentRefs");
                    if (parentRefs.Length != 2) throw new InvalidDataException("开局定位必须含两个亲本快照引用。");
                    for (var parentIndex = 0; parentIndex < 2; parentIndex++)
                    {
                        var parentRef = parentRefs[parentIndex];
                        var parentId = Long(parentRef, "packet");
                        if (Text(parentRef, "source") != source || parentId >= requestId)
                            throw new InvalidDataException("亲本快照必须属于同一来源并早于开局请求。");
                        var trainedId = Int(selected, $"succession_trained_chara_id_{parentIndex + 1}");
                        var latest = LatestOwnSnapshot(source, request, trainedId);
                        var parentPacket = latest.Packet;
                        parentId = parentPacket.Id;
                        if (parentId != Long(parentRef, "packet"))
                        {
                            Count("ResearchParentLocatorWasNotLatest");
                            issues.Add(new("ResearchParentLocatorWasNotLatest", source, responseId,
                                "研究定位不是本次已捕获的最新自有亲本；本次输入由原始库独立选择。"));
                        }
                        Count("LatestOwnParentSnapshotsSelected");
                        if (parentPacket.Direction != "response" || parentPacket.Actor != request.Actor
                            || parentPacket.CapturedAt >= request.CapturedAt)
                            throw new InvalidDataException("亲本快照的方向、参与者或捕获时序不符。");
                        if (!exchanges.TryGetValue(new(source, parentId), out var parentExchange)
                            || parentExchange.Status != "Paired" || parentExchange.Requests.Count != 1 || parentExchange.Responses.Count != 1
                            || parentExchange.Requests[0].Source != source)
                            throw new InvalidDataException("亲本快照没有唯一同源的配对请求。");
                        ValidatePair(db, ReadPacket(db, parentExchange.Requests[0].Id), parentPacket);
                        var parent = latest.Snapshot;
                        var ancestors = Objects(parent, "succession_chara_array");
                        if (ancestors.Any(row => row.ContainsKey("factor_extend_array")))
                            throw new UnknownMechanismException("祖辈对象出现嵌套扩展字段；当前已确认协议使用亲本顶层扁平数组，不能混合两种来源。");
                        var pedigree = new ParentFactorSnapshot(
                            [new(1, FactorIds(parent)), .. ancestors.Select(row => new PedigreeFactorSlot(Int(row, "position_id"), FactorIds(row)))],
                            Objects(parent, "factor_extend_array").Select(row => new PedigreeFactorExtension(Int(row, "position_id"),
                                Int(row, "base_factor_id"), Int(row, "factor_id"))).ToImmutableArray());
                        var mapped = master.Pedigree.MapParent(pedigree, parentIndex == 0 ? InheritanceSlot.Parent1 : InheritanceSlot.Parent2);
                        if (!pedigree.FlatExtensions.IsEmpty)
                            throw new UnknownMechanismException("亲本顶层扩展的结构已解析；非空扩展的实际开局结算未核验，本次不放行。");
                        sources.AddRange(mapped);
                        usedSnapshots.Add(new { packet = parentId, latest.Pointer, topLevelExtensions = "ExplicitEmpty" });
                        foreach (var offset in new[] { 0, 1, 2 })
                        {
                            var sourceRow = offset == 0 ? parent : ancestors.Single(row => Int(row, "position_id") == offset * 10);
                            var slot = (InheritanceSlot)(parentIndex * 3 + offset);
                            relations.Add(new(slot, master.CharacterId(Int(sourceRow, "card_id")), Integers(sourceRow, "win_saddle_id_array")));
                        }
                    }
                    var input = master.Character(Int(chara, "card_id"), Int(chara, "rarity"), Int(chara, "talent_level"));
                    var deck = Objects(chara, "support_card_array").Select(master.Support).ToImmutableArray();
                    var prediction = master.StateRules.Compose(input, master.ScenarioLimits, sources.ToImmutable(), deck);
                    var actualAttributes = Values(chara, "");
                    var actualLimits = Values(chara, "max_");
                    var actualAptitudes = Aptitudes.Select(value => new InitialAptitude(value.Group, Int(chara, value.Field))).ToImmutableArray();
                    var actualHints = Objects(chara, "skill_tips_array").Select(value => new InheritedHint(Int(value, "group_id"),
                        Int(value, "rarity"), Int(value, "level"))).OrderBy(value => value.GroupId).ThenBy(value => value.Rarity).ToImmutableArray();
                    var relation = master.RelationRules.Calculate(master.CharacterId(input.CardId), relations.ToImmutable());
                    var actualRelation = Int(common, "current_succession_rank_point");
                    var actualSkills = Objects(chara, "skill_array").Select(value => new InitialSkill(Int(value, "skill_id"), Int(value, "level")))
                        .OrderBy(value => value.SkillId).ToImmutableArray();
                    if (actualSkills.Select(value => value.SkillId).Distinct().Count() != actualSkills.Length)
                        throw new InvalidDataException("开局技能数组包含重复技能。");
                    var evaluations = Objects(chara, "evaluation_info_array").ToDictionary(value => Int(value, "target_id"));
                    var actualBonds = deck.OrderBy(value => value.Position).Select(card => new InitialSupportBond(card.Position, card.CardId,
                        evaluations.TryGetValue(card.Position, out var evaluation) ? Int(evaluation, "evaluation")
                            : throw new InvalidDataException("开局缺少对应卡位的支援羁绊。"))).ToImmutableArray();
                    var matchesByField = new Dictionary<string, bool>
                    {
                        ["Attributes"] = prediction.Attributes == actualAttributes,
                        ["Limits"] = prediction.Limits == actualLimits,
                        ["Aptitudes"] = prediction.Aptitudes.SequenceEqual(actualAptitudes),
                        ["ParentHints"] = prediction.ParentHints.SequenceEqual(actualHints),
                        ["ClientRelation"] = relation.Total == actualRelation,
                        ["Skills"] = prediction.Skills.SequenceEqual(actualSkills),
                        ["SupportBonds"] = prediction.SupportBonds.SequenceEqual(actualBonds)
                    };
                    Count("ComponentsChecked");
                    foreach (var (field, matches) in matchesByField) Count(field + (matches ? "Matched" : "Mismatch"));
                    if (matchesByField.Values.All(value => value)) Count("AllComparedComponentsMatched");
                    Count("CompleteFactorInput");
                    for (var index = 0; index < actualBonds.Length; index++)
                        Count(prediction.SupportBonds[index] == actualBonds[index] ? "SupportBondSlotsMatched" : "SupportBondSlotsMismatch");
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        source, requestPacket = requestId, responsePacket = responseId,
                        parentSnapshots = usedSnapshots,
                        prediction, relation, actualAttributes, actualLimits, actualAptitudes,
                        actualHints, actualSkills, actualBonds, actualClientRelation = actualRelation, matchesByField,
                        completeOpeningState = false
                    }, EvidenceJson.LineOptions));
                }
                catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or InvalidOperationException
                    or KeyNotFoundException or ArgumentException or OverflowException)
                {
                    Count("UnsupportedOrIncompleteInputs");
                    issues.Add(new("InitialStateInputNotEvaluated", source, responseId, error.Message));
                }
            }
        }
        finally { foreach (var db in connections.Values) db.Dispose(); }
        foreach (var input in paths)
            if (Fingerprint(input.Path, input.Alias).Sha256 != fingerprints.Single(value => value.Alias == input.Alias).Sha256)
                throw new InvalidDataException("开局核验期间输入改变，本次输出不能作为稳定证据。");
        var mismatches = counts.Where(value => value.Key.EndsWith("Mismatch", StringComparison.Ordinal)).Sum(value => value.Value);
        var report = new InitialStateAuditReport("1", InitialStateRules.Version, fingerprints, counts, issues,
            ["ResearchLocatorsNotIndependentHoldout", "RawPacketsDecodedAgain", "ComparedFieldsOnlyNotCompleteOpeningState",
                "StartRarityTalentAndSupportExperienceAreInitializationInputs", "WholeParentFlatExtensionsRequired",
                "LatestCapturedOwnParentsOnly", "NonemptyExtensionSettlementNotImplemented", "RarityOneToThreeTalentOneOnly",
                "ClientRelationMatchDoesNotValidateInheritanceProbability", "MidRunInheritanceNotImplemented",
                "LocalMasterAcceptedWithoutStrictResourceVersionBinding"],
            counts.GetValueOrDefault("ComponentsChecked") == 0 ? "NoEligibleSamples"
                : mismatches > 0 ? "ComponentCounterexamplesFound" : "ListedComponentsMatched");
        File.WriteAllText(Path.Combine(outputDirectory, "initial-state-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;

        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        (RawPacket Packet, JsonObject Snapshot, string Pointer) LatestOwnSnapshot(string source, RawPacket startRequest, int trainedId)
        {
            foreach (var id in snapshotPackets[source].Where(id => id < startRequest.Id))
            {
                var reference = new PacketReference(source, id);
                if (!snapshotCache.TryGetValue(reference, out var packet))
                    snapshotCache.Add(reference, packet = ReadPacket(connections[source], id));
                if (packet.Actor != startRequest.Actor || packet.CapturedAt >= startRequest.CapturedAt) continue;
                var data = Object(packet.Body, "data");
                var root = packet.Endpoint.EndsWith("/finish", StringComparison.Ordinal)
                    ? Object(data, "single_mode_finish_common") : data;
                var candidates = new List<(JsonObject Value, string Pointer)>();
                foreach (var key in new[] { "trained_chara", "trained_chara_array" })
                {
                    if (!root.ContainsKey(key)) continue;
                    var rows = Objects(root, key);
                    for (var index = 0; index < rows.Length; index++)
                        if (Int(rows[index], "trained_chara_id") == trainedId)
                            candidates.Add((rows[index], (packet.Endpoint.EndsWith("/finish", StringComparison.Ordinal)
                                ? "$.data.single_mode_finish_common." : "$.data.") + key + $"[{index}]"));
                }
                if (candidates.Count > 1) throw new InvalidDataException("同一原包含多个同编号自有亲本，不能任取一个。");
                if (candidates.Count == 1) return (packet, candidates[0].Value, candidates[0].Pointer);
            }
            throw new InvalidDataException("开局前没有捕获到对应的完整自有亲本来源。");
        }
    }

    static ImmutableArray<int> FactorIds(JsonObject source) => Objects(source, "factor_info_array")
        .Select(row => Int(row, "factor_id")).ToImmutableArray();

    sealed record RawPacket(long Id, string Direction, string Endpoint, string RequestId, string Actor,
        string AppVersion, string ResourceVersion, DateTimeOffset CapturedAt, JsonObject Body);

    static RawPacket ReadPacket(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT direction,endpoint,request_id,viewer_id,app_ver,res_ver,captured_at_utc,payload FROM packets WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("原始库缺少定位记录。");
        if (Enumerable.Range(0, 8).Any(reader.IsDBNull)) throw new InvalidDataException("原始记录缺少配对或版本字段。");
        if (!DateTimeOffset.TryParse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var capturedAt))
            throw new InvalidDataException("原始记录的捕获时间不是有效日期时间。");
        var result = new RawPacket(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), capturedAt,
            PacketAudit.Decode((byte[])reader.GetValue(7)));
        if (reader.Read()) throw new InvalidDataException("原始记录 ID 不唯一。");
        return result;
    }

    static void ValidatePair(SqliteConnection db, RawPacket request, RawPacket response)
    {
        if (request.Direction != "request" || response.Direction != "response" || request.Id >= response.Id
            || request.CapturedAt >= response.CapturedAt || request.Endpoint != response.Endpoint
            || request.RequestId != response.RequestId || request.Actor != response.Actor
            || request.AppVersion != response.AppVersion || request.ResourceVersion != response.ResourceVersion)
            throw new InvalidDataException("原始请求响应的身份、端点、版本或时序不一致。");
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM packets WHERE request_id=$request AND endpoint=$endpoint";
        command.Parameters.AddWithValue("$request", request.RequestId);
        command.Parameters.AddWithValue("$endpoint", request.Endpoint);
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 2)
            throw new InvalidDataException("当前原库同一请求存在其他捕获，不能沿用研究时的唯一配对结论。");
    }

    static SourceFingerprint Fingerprint(string path, string alias)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new(alias, alias == "master" ? "GameMasterSqlite" : "InitialStateInput",
                Convert.ToHexStringLower(SHA256.HashData(file)), file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            error.Data["FileRole"] = $"开局输入 {alias}";
            throw;
        }
    }

    static JsonObject ReadObject(string json) => JsonSerializer.Deserialize<JsonObject>(json, EvidenceJson.ReadOptions)
        ?? throw new InvalidDataException("JSON 顶层必须是对象。");
    internal static JsonObject Object(JsonObject row, string key) => row[key] as JsonObject
        ?? throw new InvalidDataException($"{key} 必须是显式对象。");
    internal static JsonObject[] Objects(JsonObject row, string key) => row[key] is JsonArray values
        ? values.Select(value => value as JsonObject ?? throw new InvalidDataException($"{key} 含非对象项。")).ToArray()
        : throw new InvalidDataException($"{key} 必须是显式数组。");
    internal static int Int(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<int>(out var result)
        ? result : throw new InvalidDataException($"{key} 必须是显式整数。");
    static long Long(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<long>(out var result)
        ? result : throw new InvalidDataException($"{key} 必须是显式整数。");
    static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var result)
        ? result : throw new InvalidDataException($"{key} 必须是显式字符串。");
    static ImmutableArray<int> Integers(JsonObject row, string key) => row[key] is JsonArray values
        ? values.Select(value => value is JsonValue item && item.TryGetValue<int>(out var result) ? result
            : throw new InvalidDataException($"{key} 含非整数项。")).ToImmutableArray()
        : throw new InvalidDataException($"{key} 必须是显式数组。");
    static InitialAttributes Values(JsonObject row, string prefix) => new(Int(row, prefix + "speed"), Int(row, prefix + "stamina"),
        Int(row, prefix + "power"), Int(row, prefix + "guts"), Int(row, prefix + "wiz"));

    public sealed class InitialStateMaster
    {
        readonly Dictionary<int, Dictionary<string, int>> cards;
        readonly Dictionary<(int, int), Dictionary<string, int>> rarity;
        readonly Dictionary<int, Dictionary<string, int>> supports;
        readonly Dictionary<int, Dictionary<string, int>> uniques;
        readonly Dictionary<int, Dictionary<string, int>> skillSets;
        readonly ILookup<int, Dictionary<string, int>> levels;
        readonly ILookup<int, Dictionary<string, int>> supportEffects;
        public InitialStateRules StateRules { get; }
        public InheritanceRelationRules RelationRules { get; }
        public InheritancePedigree Pedigree { get; }
        public InitialAttributes ScenarioLimits { get; }

        public InitialStateMaster(string path)
        {
            using var db = EvidenceAudit.OpenReadOnly(path);
            cards = Rows(db, "SELECT id,chara_id FROM card_data").ToDictionary(row => row["id"]);
            rarity = Rows(db, "SELECT * FROM card_rarity_data").ToDictionary(row => (row["card_id"], row["rarity"]));
            supports = Rows(db, "SELECT * FROM support_card_data").ToDictionary(row => row["id"]);
            uniques = Rows(db, "SELECT * FROM support_card_unique_effect").ToDictionary(row => row["id"]);
            skillSets = Rows(db, "SELECT * FROM skill_set").ToDictionary(row => row["id"]);
            levels = Rows(db, "SELECT * FROM support_card_level").ToLookup(row => row["rarity"]);
            supportEffects = Rows(db, "SELECT * FROM support_card_effect_table").ToLookup(row => row["id"]);
            var scenario = Rows(db, "SELECT max_speed,max_stamina,max_pow,max_guts,max_wiz FROM single_mode_scenario WHERE id=11").Single();
            ScenarioLimits = Values(scenario, "max_");
            var factorRows = Rows(db, "SELECT factor_id,factor_type,factor_group_id,rarity FROM succession_factor")
                .Select(row => new InitialFactor(row["factor_id"], row["factor_type"], row["factor_group_id"], row["rarity"])).ToArray();
            Pedigree = new(factorRows);
            StateRules = new(factorRows,
                Rows(db, "SELECT factor_type,value_1,value_2,add_point FROM succession_initial_factor")
                    .Select(row => new InitialFactorRule(row["factor_type"], row["value_1"], row["value_2"], row["add_point"])),
                Rows(db, "SELECT factor_group_id,effect_id,target_type,value_1,value_2 FROM succession_factor_effect")
                    .Select(row => new InheritanceFactorEffect(row["factor_group_id"], row["effect_id"], row["target_type"], row["value_1"], row["value_2"])),
                Rows(db, "SELECT id,group_id,rarity FROM skill_data").Select(row => new InheritanceSkill(row["id"], row["group_id"], row["rarity"])));
            RelationRules = new(
                Rows(db, "SELECT relation_type,relation_point FROM succession_relation").Select(row => new RelationPoint(row["relation_type"], row["relation_point"])),
                Rows(db, "SELECT chara_id,relation_type FROM succession_relation_member").Select(row => new RelationMembership(row["chara_id"], row["relation_type"])),
                Rows(db, "SELECT s.id,s.relation_group_id,CASE WHEN s.win_saddle_type=3 AND s.condition=0 AND r.grade=100 THEN 1 ELSE 0 END eligible FROM single_mode_wins_saddle s LEFT JOIN race_instance i ON i.id=s.race_instance_id_1 LEFT JOIN race r ON r.id=i.race_id")
                    .Select(row => new RelationSaddle(row["id"], row["relation_group_id"], row["eligible"] == 1)));
        }

        public int CharacterId(int cardId) => cards.TryGetValue(cardId, out var row) ? row["chara_id"]
            : throw new UnknownMechanismException($"角色卡 {cardId} 没有 master 记录。");
        public InitialCharacter Character(int cardId, int grade, int talentLevel)
        {
            if (!rarity.TryGetValue((cardId, grade), out var row))
                throw new UnknownMechanismException("育成卡稀有度缺少初始 master 记录。");
            if (!skillSets.TryGetValue(row["skill_set"], out var skillSet))
                throw new UnknownMechanismException("育成卡稀有度引用的初始技能集合未收录。");
            var skills = Enumerable.Range(1, 20).Where(index => skillSet[$"skill_id{index}"] != 0)
                .Select(index => new InitialSkill(skillSet[$"skill_id{index}"], skillSet[$"skill_level{index}"])).ToImmutableArray();
            return new(cardId, grade, Values(row, ""), Values(row, "max_"),
                Aptitudes.Select(value => new InitialAptitude(value.Group, row[value.Field])).ToImmutableArray(), talentLevel, skills);
        }
        public InitialSupportCard Support(JsonObject input)
        {
            var cardId = Int(input, "support_card_id");
            if (!supports.TryGetValue(cardId, out var card)) throw new UnknownMechanismException($"支援卡 {cardId} 未收录。");
            var eligibleLevels = levels[card["rarity"]].Where(row => row["total_exp"] <= Int(input, "exp")).ToArray();
            if (eligibleLevels.Length == 0) throw new InvalidDataException("支援经验没有对应等级。");
            var level = eligibleLevels.Max(row => row["level"]);
            var effects = supportEffects[card["effect_table_id"]].Select(row => new SupportEffect(row["type"], EffectAtLevel(row, level))).ToImmutableArray();
            if (effects.IsEmpty) throw new UnknownMechanismException("支援效果表缺失，不能按零计算。");
            var unique = ImmutableArray.CreateBuilder<SupportUniqueEffect>();
            if (card["unique_effect_id"] != 0)
            {
                if (!uniques.TryGetValue(card["unique_effect_id"], out var row)) throw new UnknownMechanismException("支援固有表缺失。");
                if (level >= row["lv"])
                    foreach (var slot in new[] { 0, 1 })
                        unique.Add(new(row[$"type_{slot}"], row[$"value_{slot}"], row[$"value_{slot}_1"],
                            row[$"value_{slot}_2"], row[$"value_{slot}_3"], row[$"value_{slot}_4"]));
            }
            return new(Int(input, "position"), cardId, card["support_card_type"], card["command_id"], effects, unique.ToImmutable());
        }

        static int EffectAtLevel(Dictionary<string, int> row, int level)
        {
            var points = new[] { (Level: 1, Value: row["init"]) }.Concat(Enumerable.Range(1, 10)
                .Select(index => (Level: index * 5, Value: row[$"limit_lv{index * 5}"]))).Where(point => point.Value >= 0).ToArray();
            var lower = points.Where(point => point.Level <= level).ToArray();
            if (lower.Length == 0) return 0; // The effect has not unlocked at this level.
            var low = lower[^1];
            var higher = points.Where(point => point.Level >= level).ToArray();
            if (higher.Length == 0 || higher[0].Level == low.Level) return low.Value;
            var high = higher[0];
            return checked((low.Value * (high.Level - level) + high.Value * (level - low.Level)) / (high.Level - low.Level));
        }
        static InitialAttributes Values(Dictionary<string, int> row, string prefix) => new(row[prefix + "speed"], row[prefix + "stamina"],
            row[prefix + "pow"], row[prefix + "guts"], row[prefix + "wiz"]);
        static Dictionary<string, int>[] Rows(SqliteConnection db, string sql)
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, int>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    if (reader.IsDBNull(index) || reader.GetValue(index) is not long)
                        throw new InvalidDataException($"开局 master 字段 {reader.GetName(index)} 不是整数。");
                    row.Add(reader.GetName(index), reader.GetInt32(index));
                }
                rows.Add(row);
            }
            return rows.ToArray();
        }
    }
}
