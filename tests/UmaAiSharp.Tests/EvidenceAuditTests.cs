using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class EvidenceAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Evidence-" + Guid.NewGuid().ToString("N"));

    public EvidenceAuditTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void AuditPairsAcrossFilesWithoutGuessingMissingValuesOrVersionBinding()
    {
        var packets = Path.Combine(directory, "packets");
        Directory.CreateDirectory(packets);
        var first = PacketDatabase(Path.Combine(packets, "first.sqlite"));
        var second = PacketDatabase(Path.Combine(packets, "second.sqlite"));
        var requestId = Guid.NewGuid().ToString("N");
        AddPacket(first, requestId, "request", "{\"single_mode_exec_command_request_common\":{\"current_turn\":3,\"command_id\":101}}");
        AddPacket(second, requestId, "response", Response);
        AddPacket(second, requestId, "response", Response);
        AddPacket(second, Guid.NewGuid().ToString("N"), "request", "{\"single_mode_exec_command_request_common\":{\"current_turn\":3,\"command_id\":101}}");
        var master = MasterDatabase();
        var before = new[] { first, second, master }.Select(Hash).ToArray();
        var output = Path.Combine(directory, "report");

        var report = EvidenceAudit.Run(packets, master, output);

        Assert.Equal(1, report.Packets.PairedExchanges);
        Assert.Equal(1, report.Packets.OrphanExchanges);
        Assert.Equal(1, report.Packets.DuplicateCaptures);
        Assert.Equal(1, report.Packets.Observations);
        Assert.Equal("Unverified", report.VersionBinding.Status);
        Assert.DoesNotContain("MasterResourceVersionUnverified", report.Validation.Blockers);
        Assert.DoesNotContain("GameRegionUnverified", report.Validation.Blockers);
        Assert.Contains("TrajectoryCompletenessUnverified", report.Validation.Blockers);
        Assert.Equal(before, new[] { first, second, master }.Select(Hash).ToArray());
        Assert.DoesNotContain(Directory.EnumerateFiles(packets), path => path.EndsWith("-journal", StringComparison.Ordinal));
        var observation = JsonSerializer.Deserialize<PioneerObservation>(
            File.ReadAllLines(Path.Combine(output, "pioneer-observations.jsonl")).Single(), EvidenceJson.Options)!;
        Assert.Equal(new ObservedInteger("Present", 0), observation.Fields["speed"]);
        Assert.Equal(new ObservedInteger("Missing", null), observation.Fields["stamina"]);
        Assert.Equal(new ObservedInteger("Null", null), observation.Fields["guts"]);
        Assert.Equal("ObservedOnly", observation.Completeness);
        Assert.DoesNotContain("viewer_id", observation.Visible.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(report.Master.Domains, domain => domain.Table == "support_card_unique_effect"
            && domain.Column == "type_0" && domain.Values.Any(value => value.Value == "120"));
        Assert.Contains(report.Master.Domains, domain => domain.Table == "shared_condition"
            && domain.Values.Any(value => value.Value == "turn>=3"));
        Assert.Equal(3, report.Master.ExportedRows);
    }

    [Fact]
    public void DifferentBodiesWithSameRequestIdAreConflictingAndNotArbitrarilyPaired()
    {
        var path = PacketDatabase(Path.Combine(directory, "packets.sqlite"));
        var requestId = Guid.NewGuid().ToString("N");
        AddPacket(path, requestId, "request", "{}");
        AddPacket(path, requestId, "response", Response);
        AddPacket(path, requestId, "response", Response.Replace("\"speed\":0", "\"speed\":1", StringComparison.Ordinal));
        using var exchanges = new StringWriter();
        using var observations = new StringWriter();
        using var actions = new StringWriter();
        var issues = new List<EvidenceIssue>();

        var report = PacketAudit.Read([(path, "packets-0001")], exchanges, observations, actions, issues);

        Assert.Equal(1, report.ConflictingExchanges);
        Assert.Equal(0, report.PairedExchanges);
        Assert.Equal(0, report.DuplicateCaptures);
        Assert.Contains(issues, issue => issue.Code == "ConflictingCapture");
    }

    [Fact]
    public void SplitCaptureFilesUseActorIdentityAndCaptureTimeInsteadOfFilenameOrder()
    {
        var later = PacketDatabase(Path.Combine(directory, "a-later.sqlite"));
        var earlier = PacketDatabase(Path.Combine(directory, "z-earlier.sqlite"));
        AddPacket(later, Guid.NewGuid().ToString("N"), "response", Response.Replace("\"turn\":3", "\"turn\":2", StringComparison.Ordinal),
            capturedAt: "2026-01-01T00:02:00Z");
        AddPacket(earlier, Guid.NewGuid().ToString("N"), "response", Response.Replace("\"turn\":3", "\"turn\":1", StringComparison.Ordinal),
            capturedAt: "2026-01-01T00:01:00Z");
        using var exchanges = new StringWriter();
        using var observations = new StringWriter();
        using var actions = new StringWriter();
        var issues = new List<EvidenceIssue>();

        var report = PacketAudit.Read([(later, "packets-0001"), (earlier, "packets-0002")], exchanges, observations, actions, issues);

        var trajectory = Assert.Single(report.Trajectories);
        Assert.Equal(1, trajectory.FirstTurn);
        Assert.Equal(2, trajectory.LastTurn);
        Assert.Equal(0, trajectory.Regressions);
        Assert.Equal("CapturedTimeAcrossSources", trajectory.OrderingEvidence);
        Assert.Equal("ObservedOnly", trajectory.Completeness);
        Assert.DoesNotContain(issues, issue => issue.Code == "TurnRegression");
    }

    [Fact]
    public void ConflictingActorsCannotBePairedEvenWhenRequestIdMatches()
    {
        var path = PacketDatabase(Path.Combine(directory, "packets.sqlite"));
        var requestId = Guid.NewGuid().ToString("N");
        AddPacket(path, requestId, "request", "{}", viewer: "first-actor");
        AddPacket(path, requestId, "response", Response, viewer: "second-actor");
        using var exchanges = new StringWriter();
        using var observations = new StringWriter();
        using var actions = new StringWriter();
        var report = PacketAudit.Read([(path, "packets")], exchanges, observations, actions, []);
        Assert.Equal(1, report.ConflictingExchanges);
        Assert.Equal(0, report.PairedExchanges);
    }

    [Fact]
    public void UnknownActionFieldsAreRetainedAndMissingActorDoesNotInventRunIdentity()
    {
        var request = (JsonObject)JsonNode.Parse("""{"new_action":{"new_option":4},"viewer_id":"private","ip_address":"private","keychain":123,"steam_id":"private"}""")!;
        var action = PioneerNormalizer.ActionFields(request);
        Assert.Equal(4, action["new_action"]!["new_option"]!.GetValue<int>());
        Assert.False(action.ContainsKey("viewer_id"));
        Assert.False(action.ContainsKey("ip_address"));
        Assert.False(action.ContainsKey("keychain"));
        Assert.False(action.ContainsKey("steam_id"));
        var observation = PioneerNormalizer.Normalize((JsonObject)JsonNode.Parse(Response)!, "source", 1, "exchange",
            "/umamusume/single_mode_pioneer/exec_command", null, "2026-01-01T00:00:00Z");
        Assert.Null(observation.RunKey);
        Assert.Equal(0, observation.Fields["speed"].Value);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EvidenceReport>("{\"schemaVersion\":\"1\"}", EvidenceJson.ReadOptions));
    }

    [Fact]
    public void VersionChangesAreExplicitWithinExchangesAndTrajectories()
    {
        var path = PacketDatabase(Path.Combine(directory, "packets.sqlite"));
        var requestId = Guid.NewGuid().ToString("N");
        AddPacket(path, requestId, "request", "{}", resourceVersion: "res-before");
        AddPacket(path, requestId, "response", Response, resourceVersion: "res-before");
        AddPacket(path, requestId, "response", Response, resourceVersion: "res-after");
        using var exchanges = new StringWriter();
        using var observations = new StringWriter();
        using var actions = new StringWriter();
        var issues = new List<EvidenceIssue>();

        var report = PacketAudit.Read([(path, "packets")], exchanges, observations, actions, issues);

        Assert.Contains(issues, issue => issue.Code == "ExchangeVersionConflict");
        Assert.Contains(issues, issue => issue.Code == "TrajectoryVersionConflict");
        Assert.Equal(new[] { "res-after", "res-before" }, Assert.Single(report.Trajectories).ResourceVersions);
        Assert.Equal(0, report.DuplicateCaptures);
    }

    [Fact]
    public void ReadOnlySourceRejectsWritesAndDetachedWal()
    {
        var path = PacketDatabase(Path.Combine(directory, "packets.sqlite"));
        using (var connection = EvidenceAudit.OpenReadOnly(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE packets";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
        File.WriteAllBytes(path + "-wal", [1]);
        Assert.Throws<InvalidDataException>(() => EvidenceAudit.OpenReadOnly(path));
    }

    [Fact]
    public void DiagnosticsRetainLocationsAndFileRolesWithoutPayloadValuesOrAccountPaths()
    {
        var json = new JsonException("Unrecognized type discriminator 'private-value'.", "$.priorities[0]", 2, 10);
        json.Data["FileRole"] = "目标文件";
        var jsonMessage = ToolDiagnostics.Describe(json);
        Assert.Contains("目标文件", jsonMessage, StringComparison.Ordinal);
        Assert.Contains("$.priorities[0]", jsonMessage, StringComparison.Ordinal);
        Assert.Contains("2", jsonMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", jsonMessage, StringComparison.Ordinal);
        var file = new FileNotFoundException("private-account.sqlite", "private-account.sqlite");
        file.Data["FileRole"] = "包库 packets-0001";
        var fileMessage = ToolDiagnostics.Describe(file);
        Assert.Contains("packets-0001", fileMessage, StringComparison.Ordinal);
        Assert.Contains("HRESULT", fileMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account", fileMessage, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ToolDiagnostics.Describe(new InvalidOperationException("implementation bug")));
    }

    [Fact]
    public void LoadShapeIsExplicitAndNonIntegerFieldsAreNotCoerced()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {"data":{"single_mode_load_common":{"chara_info":{"single_mode_chara_id":1,"turn":4,"speed":"100"},"unchecked_event_array":[]},"pioneer_data_set":{"planning_info_array":[]}}}
            """)!;
        var result = PioneerNormalizer.Normalize(root, "fixture", 1, "exchange", "/umamusume/single_mode_pioneer/load",
            "fixture-actor", "2026-01-01T00:00:00Z");
        Assert.Equal(new ObservedInteger("Present", 4), result.Fields["turn"]);
        Assert.Equal(new ObservedInteger("InvalidType", null), result.Fields["speed"]);
        Assert.Equal("Present", result.SectionPresence["unchecked_event_array"]);
        Assert.Equal("Missing", result.SectionPresence["home_info"]);
        Assert.Empty(result.DecisionEvidence);
    }

    [Theory]
    [InlineData("{\"data\":{\"single_mode_load_common\":{\"chara_info\":[]}}}")]
    public void InvalidObjectShapesAreNotSilentlyReportedAsMissingFields(string json)
    {
        Assert.Throws<InvalidOperationException>(() => PioneerNormalizer.Normalize((JsonObject)JsonNode.Parse(json)!,
            "fixture", 1, "exchange", "/umamusume/single_mode_pioneer/load", "actor", "2026-01-01T00:00:00Z"));
    }

    [Theory]
    [InlineData("start", "single_mode_start_common")]
    [InlineData("finish", "single_mode_finish_common")]
    public void LifecycleEnvelopesRetainUnknownSectionsAndExposeCharacterIdentity(string endpoint, string envelope)
    {
        var root = (JsonObject)JsonNode.Parse("""{"data":{"new_protocol_section":{"effect_type":999}}}""")!;
        root["data"]![envelope] = JsonNode.Parse("""{"chara_info":{"single_mode_chara_id":7,"turn":1,"speed":123}}""");
        var result = PioneerNormalizer.Normalize(root, "fixture", 1, "exchange", "/umamusume/single_mode_pioneer/" + endpoint,
            "fixture-actor", "2026-01-01T00:00:00Z");
        Assert.NotNull(result.RunKey);
        Assert.Equal(123, result.Fields["speed"].Value);
        Assert.Equal(999, result.Visible["new_protocol_section"]!["effect_type"]!.GetValue<int>());
        Assert.NotNull(result.Visible[envelope]);
    }

    const string Response = """
        {"data":{"chara_info":{"single_mode_chara_id":7,"turn":3,"scenario_id":11,"speed":0,"guts":null,"viewer_id":"private"},"home_info":{"command_info_array":[{"command_id":101}]},"pioneer_data_set":{"planning_info_array":[],"facility_info_array":[]},"unchecked_event_array":[]}}
        """;

    static string PacketDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE packets(id INTEGER PRIMARY KEY,captured_at_utc TEXT NOT NULL,direction TEXT NOT NULL,
            endpoint TEXT NOT NULL,request_id TEXT NOT NULL,app_ver TEXT,res_ver TEXT,viewer_id TEXT,payload BLOB NOT NULL);
            """;
        command.ExecuteNonQuery();
        return path;
    }

    static void AddPacket(string path, string requestId, string direction, string json,
        string? viewer = "fixture-actor", string capturedAt = "2026-01-01T00:00:00Z", string resourceVersion = "res")
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO packets(captured_at_utc,direction,endpoint,request_id,app_ver,res_ver,viewer_id,payload) VALUES($captured,$direction,'/umamusume/single_mode_pioneer/exec_command',$request,'app',$resource,$viewer,$payload)";
        command.Parameters.AddWithValue("$captured", capturedAt);
        command.Parameters.AddWithValue("$viewer", (object?)viewer ?? DBNull.Value);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$resource", resourceVersion);
        command.Parameters.AddWithValue("$payload", MessagePackSerializer.ConvertFromJson(json));
        command.ExecuteNonQuery();
    }

    string MasterDatabase()
    {
        var path = Path.Combine(directory, "master.mdb");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE single_mode_11_facility_effect(id INTEGER PRIMARY KEY,effect_type INTEGER,effect_value_1 INTEGER);
            INSERT INTO single_mode_11_facility_effect VALUES(1,3,8);
            CREATE TABLE support_card_unique_effect(id INTEGER PRIMARY KEY,type_0 INTEGER,value_0 INTEGER);
            INSERT INTO support_card_unique_effect VALUES(1,120,4);
            CREATE TABLE shared_condition(id INTEGER PRIMARY KEY,condition TEXT);
            INSERT INTO shared_condition VALUES(1,'turn>=3');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
