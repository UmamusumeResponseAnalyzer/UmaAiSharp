using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Cli;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.IslandTraining;

namespace UmaAiSharp.Tests;

public sealed class IslandActionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SettlesRealActionsWithoutImportingFutureTurnOrConstructionRewards(bool removeTicket, bool wrongRequestSource)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "island-action-snapshots.json")))!.AsObject();
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-IslandAction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "chain-04"));
        try
        {
            var frames = fixture["frames"]!.AsArray();
            if (removeTicket)
                frames.Single(frame => frame!["ref"]![1]!.GetValue<int>() == 1058)!["s"]!["shima_training_info"]!["remain_num"] = 0;
            if (wrongRequestSource)
                fixture["immediates"]!.AsArray().Single(row => row!["after"]![1]!.GetValue<int>() == 1060)!["requestRef"]![0] = "other-source";
            var framesPath = Path.Combine(directory, "frames.jsonl");
            File.WriteAllLines(framesPath, frames.Select(frame => frame!.ToJsonString()));
            File.WriteAllLines(Path.Combine(directory, "chain-04", "immediate.jsonl"), fixture["immediates"]!.AsArray().Select(row => row!.ToJsonString()));
            File.WriteAllLines(Path.Combine(directory, "chain-04", "chains.jsonl"), fixture["chains"]!.AsArray().Select(row => row!.ToJsonString()));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(framesPath)));
            File.WriteAllText(Path.Combine(directory, "chain-04", "manifest.json"), new JsonObject
            {
                ["inputs"] = new JsonArray(new JsonObject { ["path"] = "fixture/expansion-20261108/extract-01/frames.jsonl", ["sha256"] = hash })
            }.ToJsonString());
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
            var output = Path.Combine(directory, "output");
            if (!removeTicket && !wrongRequestSource)
            {
                var frame = frames.Single(value => value!["ref"]![1]!.GetValue<int>() == 1058)!.AsObject();
                var start = new PioneerMasterCompiler(masterPath).IslandStart(frame, new(masterPath), false, null);
                Assert.Equal(new[] { 1, 1, 2, 1, 1 }, start.Training.OrdinaryLevels);
            }
            var report = IslandActionAudit.Run(directory, framesPath, masterPath, output);

            Assert.Equal(7, report.Counts["SelectedImmediateActions"]);
            var rejected = removeTicket || wrongRequestSource;
            Assert.Equal(rejected ? 6 : 7, report.Counts["ResolvedPreviewMatched"]);
            Assert.Equal(rejected ? 6 : 7, report.Counts["GeneratedMatched"]);
            Assert.Equal(rejected ? 1 : 0, report.Counts.GetValueOrDefault("UnsupportedOrInvalidAction"));
            Assert.Equal(0, report.Counts.GetValueOrDefault("ResolvedPreviewMismatch"));
            Assert.Equal(0, report.Counts.GetValueOrDefault("GeneratedMismatch"));
            Assert.Equal(2, report.Counts["ImmediateTurnDelta:1"]);
            Assert.Equal(1, report.Counts["EmptyQueueEnd:0:34"]);
            Assert.Equal(1, report.Counts["EmptyQueueEnd:0:39"]);
            Assert.Equal(rejected ? 1 : 2, report.Counts["SeparateConstructionRegrant"]);
            Assert.Equal(4, report.Counts["TuckerGrantsMatched"]);
            var actions = File.ReadLines(Path.Combine(output, "island-actions.jsonl")).Select(line => JsonNode.Parse(line)!).ToArray();
            var moved = actions.Single(action => action["afterPacketId"]!.GetValue<int>() == 1268);
            Assert.Equal(32, moved["settlement"]!["sourceTurn"]!.GetValue<int>());
            Assert.Equal(33, moved["responseTurn"]!.GetValue<int>());
            Assert.All(moved["settlement"]!["temporaryGrants"]!.AsArray(), grant => Assert.Equal(32, grant!["sourceTurn"]!.GetValue<int>()));
            var constructed = actions.Single(action => action["afterPacketId"]!.GetValue<int>() == 2128);
            Assert.Equal(0, constructed["settlement"]!["ticketsAfter"]!.GetValue<int>());
            var sparse = actions.Single(action => action["afterPacketId"]!.GetValue<int>() == 1884);
            Assert.DoesNotContain(sparse["settlement"]!["temporaryGrants"]!.AsArray(), grant => grant!["position"]!.GetValue<int>() == 2);
            var tucker = File.ReadLines(Path.Combine(output, "island-event-grants.jsonl")).Select(line => JsonNode.Parse(line)!)
                .Single(step => step["eventPacketId"]!.GetValue<int>() == 1886);
            Assert.Contains(tucker["grants"]!.AsArray(), grant => grant!["position"]!.GetValue<int>() == 2 && grant["value"]!.GetValue<int>() == 20);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingPointContextDoesNotMeanZeroGain()
    {
        Assert.Equal(0, IslandActionRules.DevelopmentPointGain(5, null, 12, true));
        Assert.Throws<UnknownMechanismException>(() => IslandActionRules.DevelopmentPointGain(4, null, 12, true));
        Assert.Throws<UnknownMechanismException>(() => IslandActionRules.DevelopmentPointGain(5, 20, 12, true));
    }
}
