using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Cli.Evidence;

namespace UmaAiSharp.Tests;

public sealed class RaceRouteAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-RaceRoute-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AuditKeepsConditionalSelectionAndRejectsUntrustedOrInvalidSnapshots()
    {
        Directory.CreateDirectory(directory);
        var master = Path.Combine(directory, "master.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = master, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE single_mode_route(id INTEGER,scenario_id INTEGER,chara_id INTEGER,race_set_id INTEGER,condition_set_id INTEGER,priority INTEGER);
                INSERT INTO single_mode_route VALUES(122,0,1135,1135,0,0);
                CREATE TABLE single_mode_route_race(id INTEGER,race_set_id INTEGER,scenario_group_id INTEGER,target_type INTEGER,sort_id INTEGER,turn INTEGER,race_type INTEGER,condition_type INTEGER,condition_id INTEGER,condition_value_1 INTEGER,condition_value_2 INTEGER,determine_race INTEGER,determine_race_flag INTEGER,alt_determine_race INTEGER,alt_determine_race_flag INTEGER,determine_race_for_generate INTEGER);
                INSERT INTO single_mode_route_race VALUES(1523,1135,100,1,5,44,0,1,168,3,0,1,0,99,0,99);
                INSERT INTO single_mode_route_race VALUES(1524,1135,100,1,5,44,0,1,76,2,0,2,113501,0,0,0);
                CREATE TABLE card_data(id INTEGER,chara_id INTEGER);
                INSERT INTO card_data VALUES(113501,1135);
                CREATE TABLE single_mode_scenario_group(group_id INTEGER,scenario_id INTEGER);
                INSERT INTO single_mode_scenario_group VALUES(100,11);
                """;
            command.ExecuteNonQuery();
        }
        var observations = new List<PioneerObservation>();
        var exchanges = new List<ExchangeSummary>();
        foreach (var (packet, goal, scenario, status) in new[]
            { (2, 1523, 11, "Paired"), (4, 1524, 11, "Paired"), (6, 1524, 11, "ConflictingCapture"),
                (8, 1524, 10, "Paired"), (10, 9999, 11, "Paired"), (12, 9999, 11, "Paired") })
        {
            var visible = new JsonObject { ["chara_info"] = new JsonObject
            {
                ["card_id"] = 113501, ["route_id"] = 122, ["turn"] = packet, ["scenario_id"] = scenario,
                ["route_race_id_array"] = new JsonArray(goal),
            } };
            var observation = new PioneerObservation("fixture", packet, packet.ToString(), "/single_mode_pioneer/check_event",
                "run", "2026-11-05T00:00:00Z", new Dictionary<string, ObservedInteger>(), new Dictionary<string, string>(), [], visible);
            observations.Add(observation);
            exchanges.Add(new(observation.ExchangeKey, observation.Endpoint, status, [new("fixture", packet - 1)],
                [new("fixture", packet)], 1, 1, 0, [], []));
        }
        File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"), observations.Select(x => JsonSerializer.Serialize(x)));
        File.WriteAllLines(Path.Combine(directory, "exchanges.jsonl"), exchanges.Select(x => JsonSerializer.Serialize(x)));

        var report = RaceRouteAudit.Run(directory, master, Path.Combine(directory, "report"));

        Assert.Equal(2, report.Counts["MappedRouteStates"]);
        Assert.Equal(2, report.Counts["RequiresSelectedGoalState"]);
        Assert.Equal(1, report.Counts["RouteStateTransitionsInInputOrder"]);
        Assert.Equal(1, report.Counts["ExchangeNotPaired"]);
        Assert.Equal(3, report.Counts["RejectedFrames"]);
        Assert.Equal(3, report.Issues.Count);
        Assert.Equal("MappingIncomplete", report.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
