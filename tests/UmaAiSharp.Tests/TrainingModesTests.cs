using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingModesTests
{
    static TrainingEnvironment Environment => new(3, 0, new(100, 100, 100, 100, 100, 50, 0),
        new(1800, 1800, 1800, 1800, 1800, 100), new(), null);
    static ImmutableArray<TrainingFacility> Facilities => Enumerable.Range(101, 6)
        .Select(id => new TrainingFacility(id, id * 100 + 10, 1, [])).ToImmutableArray();

    [Fact]
    public void OnlyCampAndIslandCapTheExtraLayerAt100()
    {
        var environment = Environment;
        var ordinary = new TrainingFormulaInput(101, 1, 3, 0, 0, environment.Current, environment.Limits, new(), [], 1000, null);
        var facilities = Facilities.SetItem(0, Facilities[0] with { Effects = [new(2, 2, 1000, 3601)] });
        var camp = new CampTrainingInput(3601, 5, 0, environment, [], new(12, 0, 0, 0, 0, -15, 8), facilities);
        var island = new IslandTrainingInput(environment, [1, 1, 1, 1, 1], new(12, 0, 0, 0, 0, 0, 8),
            facilities.SetItem(0, facilities[0] with { Effects = [new(2, 0, 1000, 0)] }), []);

        Assert.Equal(120, TrainingFormulaCandidate.GenerateOrdinary(ordinary).Scenario.Speed);
        Assert.Equal(100, TrainingFormulaCandidate.GenerateCamp(camp).Scenario.Speed);
        Assert.Equal(100, TrainingFormulaCandidate.GenerateIsland(island).Scenario.Speed);
        Assert.Throws<UnknownMechanismException>(() => TrainingFormulaCandidate.GenerateCamp(camp with { Level = 3 }));
        Assert.Throws<UnknownMechanismException>(() => TrainingFormulaCandidate.GenerateIsland(island with
        {
            Facilities = facilities.SetItem(5, facilities[5] with { Level = 2, StateId = 10620, Effects = [new(999, 0, 1, 0)] })
        }));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("non-head-event")]
    [InlineData("unverified-goldship-costume")]
    [InlineData("missing-disable-list")]
    public void RebuildsActualSnapshotsAndRejectsUnprovenOrMalformedInput(string variant)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "training-modes-snapshots.json")))!.AsObject();
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-TrainingModes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var observations = fixture["observations"]!.AsArray();
            var actions = fixture["actions"]!.AsArray();
            if (variant == "wrong-event-request")
                actions.Single(node => node!["packetId"]!.GetValue<int>() == 48817)!["fields"]!["single_mode_check_event_request_common"]!["event_id"] = 999;
            if (variant == "non-head-event")
                observations.Single(node => node!["packetId"]!.GetValue<int>() == 48816)!["visible"]!["unchecked_event_array"]!.AsArray()
                    .Insert(0, new JsonObject { ["event_id"] = 999, ["story_id"] = 999 });
            if (variant == "unverified-goldship-costume")
            {
                foreach (var observation in observations.Where(node => node!["visible"]!["chara_info"]?["card_id"]?.GetValue<int>() == 100701))
                    observation!["visible"]!["chara_info"]!["card_id"] = 100702;
                fixture["tables"]!["card_data"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 100701)!["id"] = (long)100702;
            }
            if (variant == "duplicate-island-guest")
            {
                var visible = observations.Single(node => node!["packetId"]!.GetValue<int>() == 13645)!["visible"]!;
                var supportPositions = visible["chara_info"]!["support_card_array"]!.AsArray().Select(card => card!["position"]!.GetValue<int>()).ToHashSet();
                var groups = visible["pioneer_data_set"]!["shima_training_info"]!["shima_training_partner_info_array"]!.AsArray();
                var source = groups.First(group => group!["training_partner_id_array"]!.AsArray().Any(position => !supportPositions.Contains(position!.GetValue<int>())));
                var guest = source!["training_partner_id_array"]!.AsArray().First(position => !supportPositions.Contains(position!.GetValue<int>()))!.GetValue<int>();
                groups.First(group => !ReferenceEquals(group, source))!["training_partner_id_array"]!.AsArray().Add(guest);
            }
            if (variant == "wrong-exchange-endpoint")
                fixture["exchanges"]!.AsArray().Single(node => node!["responses"]![0]!["id"]!.GetValue<int>() == 51556)!["endpoint"] = "/single_mode/load";
            if (variant == "missing-disable-list")
                observations.Single(node => node!["packetId"]!.GetValue<int>() == 51556)!["visible"]!["home_info"]!.AsObject().Remove("disable_command_id_array");
            foreach (var (name, rows) in new[] { ("pioneer-observations.jsonl", observations),
                ("exchanges.jsonl", fixture["exchanges"]!.AsArray()), ("pioneer-actions.jsonl", actions) })
                File.WriteAllLines(Path.Combine(directory, name), rows.Select(row => row!.ToJsonString()));
            var masterPath = Path.Combine(directory, "master.sqlite");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = masterPath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                foreach (var table in fixture["tables"]!.AsObject())
                {
                    var rows = table.Value!.AsArray();
                    var columns = rows[0]!.AsObject().Select(pair => pair.Key).ToArray();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"CREATE TABLE {table.Key} ({string.Join(',', columns.Select(column => column + " INTEGER"))});";
                    command.ExecuteNonQuery();
                    command.CommandText = $"INSERT INTO {table.Key} VALUES({string.Join(',', columns.Select((_, index) => "$v" + index))});";
                    for (var i = 0; i < columns.Length; i++) command.Parameters.Add("$v" + i, SqliteType.Integer);
                    foreach (var row in rows)
                    {
                        for (var i = 0; i < columns.Length; i++) command.Parameters[i].Value = row![columns[i]]!.GetValue<long>();
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }

            var report = TrainingModesAudit.Run(directory, masterPath, Path.Combine(directory, "result"));

            Assert.DoesNotContain(report.Counts, count => count.Key.EndsWith(".Mismatch", StringComparison.Ordinal) && count.Value != 0);
            foreach (var mode in new[] { "ordinary", "camp", "island" }) Assert.True(report.Counts[mode + ".Matched"] > 0);
            Assert.True(report.Counts["island.UpperOnly"] > 0);
            var validEvent = variant is not ("wrong-event-request" or "non-head-event" or "unverified-goldship-costume");
            Assert.Equal(validEvent ? 1 : 0, report.Counts.GetValueOrDefault("GoldShipSignals"));
            Assert.Equal(validEvent ? 0 : variant == "unverified-goldship-costume" ? 2 : 1, report.Counts.GetValueOrDefault("InvalidEventState"));
            if (variant == "duplicate-island-guest") Assert.Contains(report.Issues, issue => issue.Detail.Contains("跨设施", StringComparison.Ordinal));
            var results = File.ReadLines(Path.Combine(directory, "result", "training-modes-samples.jsonl")).Select(line => JsonNode.Parse(line)!).ToArray();
            var partialFrameExpected = variant is "wrong-exchange-endpoint" or "missing-disable-list" ? Array.Empty<int>() : [102, 3101];
            Assert.Equal(partialFrameExpected, results.Where(row => row["packetId"]!.GetValue<int>() == 51556).Select(row => row["command"]!.GetValue<int>()).Order());
            Assert.Equal(new[] { 101 }, results.Where(row => row["packetId"]!.GetValue<int>() == 57171).Select(row => row["command"]!.GetValue<int>()));
            Assert.Contains(results, row => row["observedCommon"] is null && row["observedScenario"] is not null);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
