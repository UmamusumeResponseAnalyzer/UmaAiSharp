using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class InitialStateAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-InitialState-" + Guid.NewGuid().ToString("N"));
    readonly string packets;
    readonly string master;
    readonly string evidence;
    readonly string study;
    readonly JsonObject observed;

    public InitialStateAuditTests()
    {
        Directory.CreateDirectory(directory);
        packets = Path.Combine(directory, "capture.sqlite");
        master = Path.Combine(directory, "master.sqlite");
        evidence = Path.Combine(directory, "evidence");
        study = Path.Combine(directory, "study");
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(study);
        CreateMaster();
        observed = new JsonObject
        {
            ["card_id"] = 1, ["rarity"] = 1, ["talent_level"] = 1, ["turn"] = 1, ["scenario_id"] = 11,
            ["speed"] = 40, ["stamina"] = 10, ["power"] = 10, ["guts"] = 10, ["wiz"] = 10,
            ["max_speed"] = 1124, ["max_stamina"] = 1100, ["max_power"] = 1100, ["max_guts"] = 1100, ["max_wiz"] = 1100,
            ["skill_tips_array"] = new JsonArray(),
            ["skill_array"] = new JsonArray(new JsonObject { ["skill_id"] = 900001, ["level"] = 1 }),
            ["evaluation_info_array"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 6)
                .Select(position => new { target_id = position, evaluation = 20 })),
            ["support_card_array"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 6)
                .Select(position => new { position, support_card_id = 99 + position, exp = 0 }))
        };
        foreach (var field in AptitudeFields) observed[field] = field == "proper_distance_short" ? 3 : 1;
        using (var db = Open(packets))
        {
            Execute(db, "CREATE TABLE packets(id INTEGER PRIMARY KEY,direction TEXT,endpoint TEXT,request_id TEXT,viewer_id TEXT,app_ver TEXT,res_ver TEXT,captured_at_utc TEXT,payload BLOB)");
            AddPacket(db, 1, "request", "/umamusume/load/index", "load-pair", new JsonObject());
            AddPacket(db, 2, "response", "/umamusume/load/index", "load-pair", new JsonObject
            {
                ["data"] = new JsonObject { ["trained_chara"] = new JsonArray(Parent(20, 2, 4, 5), Parent(30, 3, 6, 7)) }
            });
            AddPacket(db, 3, "request", "/umamusume/single_mode_pioneer/start", "start-pair", new JsonObject
            {
                ["single_mode_start_request_common"] = new JsonObject
                {
                    ["current_succession_rank_point"] = 14,
                    ["start_chara"] = new JsonObject { ["card_id"] = 1, ["scenario_id"] = 11,
                        ["succession_trained_chara_id_1"] = 20, ["succession_trained_chara_id_2"] = 30,
                        ["rental_succession_trained_chara"] = new JsonObject { ["trained_chara_id"] = 0 } }
                }
            });
            AddPacket(db, 4, "response", "/umamusume/single_mode_pioneer/start", "start-pair", StartResponse());
        }
        WriteInputs();
    }

    [Fact]
    public void RawInputsMapWholeParentExtensionCoverageAndCompareSevenComponents()
    {
        var report = Run();
        Assert.Equal("ListedComponentsMatched", report.Status);
        Assert.Equal(1, report.Counts["AllComparedComponentsMatched"]);
        Assert.Equal(1, report.Counts["CompleteFactorInput"]);
        Assert.Equal(1, report.Counts["SkillsMatched"]);
        Assert.Equal(6, report.Counts["SupportBondSlotsMatched"]);
        Assert.Equal(2, report.Counts["LatestOwnParentSnapshotsSelected"]);
        Assert.False(report.Counts.ContainsKey("MissingAncestorExtensionCoverage"));
        Assert.Empty(report.Issues);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("pair")]
    public void MissingOutputWrongOutputAndWrongPairHaveDifferentOutcomes(string change)
    {
        using (var db = Open(packets))
        {
            if (change == "pair") Execute(db, "UPDATE packets SET request_id='wrong' WHERE id=4");
            else if (change == "duplicate") AddPacket(db, 5, "response", "/umamusume/single_mode_pioneer/start", "start-pair", StartResponse());
            else if (change == "date") Execute(db, "UPDATE packets SET captured_at_utc='bad-date' WHERE id=4");
            else if (change == "extension")
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT payload FROM packets WHERE id=2";
                var body = PacketAudit.Decode((byte[])command.ExecuteScalar()!);
                ((JsonObject)body["data"]!["trained_chara"]![0]!).Remove("factor_extend_array");
                command.CommandText = "UPDATE packets SET payload=$body WHERE id=2";
                command.Parameters.AddWithValue("$body", MessagePackSerializer.ConvertFromJson(body.ToJsonString()));
                command.ExecuteNonQuery();
            }
            else
            {
                if (change == "missing") observed.Remove("stamina");
                else observed["speed"] = 41;
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE packets SET payload=$body WHERE id=4";
                command.Parameters.AddWithValue("$body", MessagePackSerializer.ConvertFromJson(StartResponse().ToJsonString()));
                command.ExecuteNonQuery();
            }
        }
        // Update only the locator provenance: matching is still against the independently decoded payload.
        WriteInputs();
        var report = Run();
        if (change == "mismatch")
        {
            Assert.Equal("ComponentCounterexamplesFound", report.Status);
            Assert.Equal(1, report.Counts["AttributesMismatch"]);
            Assert.False(report.Counts.ContainsKey("UnsupportedOrIncompleteInputs"));
        }
        else
        {
            Assert.Equal("NoEligibleSamples", report.Status);
            Assert.Equal(1, report.Counts["UnsupportedOrIncompleteInputs"]);
            Assert.False(report.Counts.ContainsKey("AttributesMismatch"));
        }
    }

    [Fact]
    public void ParentSnapshotIsSelectedFromRawHistoryRatherThanResearchLocator()
    {
        var path = Path.Combine(study, "starts.jsonl");
        var locator = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var parent in locator["parentRefs"]!.AsArray()) parent!["packet"] = 1;
        File.WriteAllText(path, locator.ToJsonString());
        var report = Run();
        Assert.Equal(1, report.Counts["AllComparedComponentsMatched"]);
        Assert.Equal(2, report.Counts["ResearchParentLocatorWasNotLatest"]);
    }

    InitialStateAuditReport Run() => InitialStateAudit.Run(study, evidence, directory, master, Path.Combine(directory, "output"));

    JsonObject StartResponse() => new()
    {
        ["data"] = new JsonObject { ["single_mode_start_common"] = new JsonObject { ["chara_info"] = observed.DeepClone() } }
    };

    static JsonObject Parent(int id, int card, int ancestor1, int ancestor2)
    {
        var parent = Source(card);
        parent["trained_chara_id"] = id;
        parent["factor_extend_array"] = new JsonArray();
        var first = Source(ancestor1); first["position_id"] = 10;
        var second = Source(ancestor2); second["position_id"] = 20;
        parent["succession_chara_array"] = new JsonArray(first, second);
        return parent;
    }

    static JsonObject Source(int card) => new()
    {
        ["card_id"] = card, ["win_saddle_id_array"] = new JsonArray(),
        ["factor_info_array"] = new JsonArray(new JsonObject { ["factor_id"] = 101 }, new JsonObject { ["factor_id"] = 201 })
    };

    void WriteInputs()
    {
        var report = new EvidenceReport("1", DateTimeOffset.UtcNow,
            [new("packets-0001", "DebugSaveAllPackets", Hash(packets), new FileInfo(packets).Length)],
            new("Unverified", null, [], "test"), "Unverified", new([], [], 0),
            new(4, 2, 2, 2, 0, 0, 0, 0, 1, [], [], [], [], []), [], new("Blocked", []));
        var audit = Path.Combine(evidence, "audit.json");
        File.WriteAllText(audit, JsonSerializer.Serialize(report, EvidenceJson.Options));
        var exchanges = new[]
        {
            new ExchangeSummary("load", "/umamusume/load/index", "Paired", [new("packets-0001", 1)], [new("packets-0001", 2)], 1, 1, 0, ["test"], ["test"]),
            new ExchangeSummary("start", "/umamusume/single_mode_pioneer/start", "Paired", [new("packets-0001", 3)], [new("packets-0001", 4)], 1, 1, 0, ["test"], ["test"])
        };
        File.WriteAllLines(Path.Combine(evidence, "exchanges.jsonl"), exchanges.Select(value => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        File.WriteAllText(Path.Combine(study, "provenance.json"), JsonSerializer.Serialize(new
        {
            inputs = new[] { new { path = audit, sha256 = Hash(audit) }, new { path = packets, sha256 = Hash(packets) } }
        }));
        File.WriteAllText(Path.Combine(study, "starts.jsonl"), JsonSerializer.Serialize(new
        {
            source = "packets-0001", packet = 4, requestPacket = 3,
            parentRefs = new[] { new { source = "packets-0001", packet = 2 }, new { source = "packets-0001", packet = 2 } }
        }));
    }

    static void AddPacket(SqliteConnection db, int id, string direction, string endpoint, string pair, JsonObject body)
    {
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO packets VALUES($id,$direction,$endpoint,$pair,'private-test-actor','test','test',$time,$body)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$pair", pair);
        command.Parameters.AddWithValue("$time", new DateTimeOffset(2026, 1, 1, 0, 0, id, TimeSpan.Zero).ToString("O"));
        command.Parameters.AddWithValue("$body", MessagePackSerializer.ConvertFromJson(body.ToJsonString()));
        command.ExecuteNonQuery();
    }

    static readonly string[] AptitudeFields = ["proper_ground_turf", "proper_ground_dirt", "proper_running_style_nige", "proper_running_style_senko",
        "proper_running_style_sashi", "proper_running_style_oikomi", "proper_distance_short", "proper_distance_mile", "proper_distance_middle", "proper_distance_long"];

    void CreateMaster()
    {
        using var db = Open(master);
        Table("card_data", ["id", "chara_id"], Enumerable.Range(1, 7).Select(id => new[] { id, id }));
        Table("card_rarity_data", ["card_id", "rarity", "skill_set", "speed", "stamina", "pow", "guts", "wiz", "max_speed", "max_stamina", "max_pow", "max_guts", "max_wiz", .. AptitudeFields],
            [[1, 1, 10, 10, 10, 10, 10, 10, 1000, 1000, 1000, 1000, 1000, .. Enumerable.Repeat(1, 10)]]);
        Table("support_card_data", ["id", "rarity", "effect_table_id", "unique_effect_id", "support_card_type", "command_id"], Enumerable.Range(100, 6).Select(id => new[] { id, 1, id, 0, 1, 101 }));
        Table("support_card_unique_effect", ["id"], []);
        Table("support_card_level", ["rarity", "level", "total_exp"], [[1, 1, 0]]);
        Table("support_card_effect_table", ["id", "type", "init", .. Enumerable.Range(1, 10).Select(index => "limit_lv" + index * 5)],
            Enumerable.Range(100, 6).SelectMany(id => new[] { new[] { id, 9, 0 }.Concat(Enumerable.Repeat(0, 10)).ToArray(),
                new[] { id, 14, 20 }.Concat(Enumerable.Repeat(20, 10)).ToArray() }));
        Table("single_mode_scenario", ["id", "max_speed", "max_stamina", "max_pow", "max_guts", "max_wiz"], [[11, 100, 100, 100, 100, 100]]);
        Table("succession_factor", ["factor_id", "factor_type", "factor_group_id", "rarity"], [[101, 1, 1, 1], [201, 2, 31, 1]]);
        Table("succession_initial_factor", ["factor_type", "value_1", "value_2", "add_point"],
            [[1, 1, 0, 5], [1, 2, 0, 12], [1, 3, 0, 21], [1, 1, 1, 4], [1, 2, 1, 9], [1, 3, 1, 16],
                [2, 1, 3, 1], [2, 4, 6, 2], [2, 7, 9, 3], [2, 10, 999, 4], [3, 1, 1, 0], [3, 2, 2, 0], [3, 3, 4, 0]]);
        Table("succession_factor_effect", ["factor_group_id", "effect_id", "target_type", "value_1", "value_2"], []);
        Table("skill_data", ["id", "group_id", "rarity"], [[900001, 9001, 1]]);
        Table("skill_set", ["id", .. Enumerable.Range(1, 20).SelectMany(index => new[] { "skill_id" + index, "skill_level" + index })],
            [[10, 900001, 1, .. Enumerable.Repeat(0, 38)]]);
        Table("succession_relation", ["relation_type", "relation_point"], [[1, 2]]);
        Table("succession_relation_member", ["chara_id", "relation_type"], Enumerable.Range(1, 7).Select(id => new[] { id, 1 }));
        Table("single_mode_wins_saddle", ["id", "relation_group_id", "win_saddle_type", "condition", "race_instance_id_1"], []);
        Table("race_instance", ["id", "race_id"], []);
        Table("race", ["id", "grade"], []);

        void Table(string name, string[] columns, IEnumerable<int[]> rows)
        {
            Execute(db, $"CREATE TABLE {name}({string.Join(',', columns.Select(column => column + " INTEGER"))})");
            foreach (var row in rows) Execute(db, $"INSERT INTO {name} VALUES({string.Join(',', row)})");
        }
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    static string Hash(string path)
    {
        using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
