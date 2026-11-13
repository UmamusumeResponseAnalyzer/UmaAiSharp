using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class PlacementHintAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Placement-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CountsRunTurnOpportunitiesWithoutDeduplicatingEqualOutcomesAcrossTurns()
    {
        var first = Frame(1, 1);
        first.Visible["chara_info"]!["evaluation_info_array"]![0]!["is_appear"] = 0;
        var repeat = first with { PacketId = 2, ExchangeKey = "exchange2" };
        var next = Frame(3, 2);
        var conflict1 = Frame(4, 3);
        var conflict2 = Frame(5, 3);
        conflict2.Visible["home_info"]!["command_info_array"]![0]!["training_partner_array"] = new JsonArray(1);
        var report = Run([first, repeat, next, conflict1, conflict2]);

        Assert.Equal(5, report.Counts["ReadyFrames"]);
        Assert.Equal(3, report.Counts["ObservedReadyRunTurns"]);
        Assert.Equal(1, report.Counts["RepeatedReadyBoardFrames"]);
        Assert.Equal(1, report.Counts["MultipleBoardRunTurnsNotInBaselineComparison"]);
        var initial = Assert.Single(report.Strata, row => row.CardId == 123 && !row.Introduced);
        Assert.Equal(1, initial.Absent.Trials);
        Assert.Empty(initial.UnadjustedBaselines);
        var active = Assert.Single(report.Strata, row => row.CardId == 123 && row.Introduced);
        Assert.Equal(1, active.Absent.Trials);
        Assert.Equal(1, active.Absent.Successes);
        Assert.True(active.DeckHasUnique122);
        Assert.Equal("DescriptiveEvidenceOnly", report.Status);
        var opportunities = File.ReadLines(Path.Combine(directory, "report", "placement-opportunities.jsonl"))
            .Select(line => JsonSerializer.Deserialize<PlacementOpportunity>(line, EvidenceJson.ReadOptions)!).ToArray();
        Assert.Equal(2, opportunities.Single(row => row.Turn == 3).Boards.Count);
    }

    [Theory]
    [InlineData("missing-introduction", "UnsupportedOrMalformedFrame")]
    [InlineData("placeholder", "DisabledOrPlaceholderPreview")]
    [InlineData("pending", "PendingOrUnobservedEvents")]
    [InlineData("unpaired", "ExchangeNotPaired")]
    public void MissingContextAndPendingBoardsAreNotRandomAbsenceTrials(string kind, string rejected)
    {
        var frame = Frame(1, 1);
        if (kind == "other-scenario") frame.Visible["chara_info"]!["scenario_id"] = 10;
        var command = frame.Visible["home_info"]!["command_info_array"]![0]!.AsObject();
        if (kind == "missing-hints") command.Remove("tips_event_partner_array");
        if (kind == "duplicate-partner") command["training_partner_array"] = new JsonArray(1, 1);
        if (kind == "missing-introduction") frame.Visible["chara_info"]!["evaluation_info_array"]![0]!.AsObject().Remove("is_appear");
        if (kind == "hint-without-partner") command["tips_event_partner_array"] = new JsonArray(1);
        if (kind == "placeholder") command["params_inc_dec_info_array"] = null;
        if (kind == "pending") frame.Visible["unchecked_event_array"] = new JsonArray(new JsonObject
            { ["event_id"] = 10, ["story_id"] = 20, ["play_timing"] = 1, ["chara_id"] = 0 });
        var report = Run([frame], kind == "unpaired" ? "ConflictingCapture" : "Paired");
        Assert.Equal(1, report.Counts[rejected]);
        Assert.Empty(report.Strata);
        Assert.Equal(0, report.Counts.GetValueOrDefault("ReadyFrames"));
    }

    PlacementHintAuditReport Run(PioneerObservation[] frames, string status = "Paired")
    {
        Directory.CreateDirectory(directory);
        var masterPath = Path.Combine(directory, "master.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = masterPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE support_card_data(id INTEGER,rarity INTEGER,effect_table_id INTEGER,unique_effect_id INTEGER,command_id INTEGER,support_card_type INTEGER);
                CREATE TABLE support_card_unique_effect(id INTEGER,lv INTEGER,type_0 INTEGER,value_0 INTEGER,type_1 INTEGER,value_1 INTEGER);
                INSERT INTO support_card_unique_effect VALUES(123,1,19,20,0,0),(124,1,122,19,0,0);
                CREATE TABLE support_card_effect_table(id INTEGER,type INTEGER,init INTEGER,limit_lv5 INTEGER,limit_lv10 INTEGER,limit_lv15 INTEGER,limit_lv20 INTEGER,limit_lv25 INTEGER,limit_lv30 INTEGER,limit_lv35 INTEGER,limit_lv40 INTEGER,limit_lv45 INTEGER,limit_lv50 INTEGER);
                CREATE TABLE support_card_level(rarity INTEGER,level INTEGER,total_exp INTEGER);
                INSERT INTO support_card_level VALUES(3,50,0);
                """;
            command.ExecuteNonQuery();
            foreach (var id in Enumerable.Range(123, 6))
            {
                command.CommandText = $"INSERT INTO support_card_data VALUES({id},3,{id},{(id is 123 or 124 ? id : 0)},101,1);"
                    + $"INSERT INTO support_card_effect_table VALUES({id},19,80,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1);";
                command.ExecuteNonQuery();
            }
        }
        File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"), frames.Select(frame => JsonSerializer.Serialize(frame)));
        File.WriteAllLines(Path.Combine(directory, "exchanges.jsonl"), frames.Select(frame => JsonSerializer.Serialize(
            new ExchangeSummary(frame.ExchangeKey, frame.Endpoint, status, [new("fixture", frame.PacketId + 100)],
                [new("fixture", frame.PacketId)], 1, 1, 0, [], []))));
        return PlacementHintAudit.Run(directory, masterPath, Path.Combine(directory, "report"));
    }

    static PioneerObservation Frame(long packet, int turn)
    {
        var deck = new JsonArray(Enumerable.Range(1, 6).Select(position => (JsonNode)new JsonObject
            { ["position"] = position, ["support_card_id"] = 122 + position, ["exp"] = 0 }).ToArray());
        var evaluations = new JsonArray(Enumerable.Range(1, 6).Select(position => (JsonNode)new JsonObject
            { ["target_id"] = position, ["is_appear"] = 1 }).ToArray());
        var commands = new JsonArray(new[] { 101, 105, 102, 103, 106 }.Select(command => (JsonNode)new JsonObject
        {
            ["command_id"] = command, ["command_type"] = 1, ["is_enable"] = 1,
            ["params_inc_dec_info_array"] = new JsonArray(), ["training_partner_array"] = new JsonArray(),
            ["tips_event_partner_array"] = new JsonArray(),
        }).ToArray());
        var visible = new JsonObject
        {
            ["chara_info"] = new JsonObject { ["turn"] = turn, ["playing_state"] = 1, ["scenario_id"] = 11,
                ["support_card_array"] = deck, ["evaluation_info_array"] = evaluations },
            ["home_info"] = new JsonObject { ["command_info_array"] = commands },
            ["unchecked_event_array"] = new JsonArray(),
        };
        return new("fixture", packet, "exchange" + packet, "/single_mode/check_event", "run", "2026-11-05T00:00:00Z",
            new Dictionary<string, ObservedInteger>(), new Dictionary<string, string>(), [], visible);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
