using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerRuntimeComponentTests
{
    const string MasterHash = "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886";

    [Fact]
    public void RealOrdinaryTurnInputsCompileIntoFiveExecutableTrainingChoices()
    {
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "island-action-snapshots.json")))!.AsObject();
        using var database = new FixtureMaster(fixture["tables"]!.AsObject());
        var master = new PioneerMasterCompiler(database.Path);
        var frames = fixture["frames"]!.AsArray().ToDictionary(row => row!["ref"]!.ToJsonString(), row => row!.AsObject());
        var previews = 0;
        foreach (var row in fixture["immediates"]!.AsArray())
        {
            var frame = frames[row!["before"]!.ToJsonString()];
            var start = master.TrainingStart(frame, false, null);
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, true)], MasterHash);
            var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
            foreach (var command in start.Commands)
            {
                var actual = scenario.PreviewTraining(root, command.CommandId).Gains;
                var common = frame["home"]!["command_info_array"]!.AsArray().Single(value => value!["command_id"]!.GetValue<int>() == command.CommandId)!;
                var extra = frame["s"]!["command_info_array"]!.AsArray().Single(value => value!["command_id"]!.GetValue<int>() == command.CommandId)!;
                Assert.Equal(Values(common["params_inc_dec_info_array"]!.AsArray()), actual.Common);
                Assert.Equal(Values(extra["params_inc_dec_info_array"]!.AsArray()), actual.Scenario);
                Assert.Null(scenario.ValidateAction(root, PioneerRuntimeAction.Train(command.CommandId)));
                var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                    [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Train(command.CommandId))], 0);
                var execution = SimulationRunner.CreateExecution(scenario, script, root, 13, 0);
                var result = SimulationRunner.Continue(scenario, script, ref execution);
                Assert.Equal((int)PioneerStopCode.TrainingEventsRequired, result.Stop!.Value.Code);
                Assert.Equal(TrainingPreviewSettlement.Apply(start.Environment.Current, start.Environment.Limits, actual.Common, actual.Scenario), execution.State.Header.Resources);
                if (start.CompletedCheckpoint < 5)
                {
                    var points = frame["s"]!["pioneer_point_gain_info_array"]!.AsArray()
                        .Single(value => value!["command_type"]!.GetValue<int>() == 1 && value["command_id"]!.GetValue<int>() == command.CommandId)!;
                    Assert.Equal(start.DevelopmentPoints + points["gain_num"]!.GetValue<int>(), execution.State.Header.DevelopmentPoints);
                }
                Assert.Equal(actual.Overflow, execution.State.Header.FutureOverflow);
                previews++;
            }
        }
        Assert.Equal(35, previews);

        static TrainingValues Values(JsonArray changes)
        {
            var values = changes.ToDictionary(value => value!["target_type"]!.GetValue<int>(), value => value!["value"]!.GetValue<int>());
            return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3),
                values.GetValueOrDefault(4), values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
        }
    }

    [Fact]
    public void SevenRealIslandWindowsUseGeneratedInputsAndStopBeforeUnimplementedScheduling()
    {
        var source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "island-action-snapshots.json"));
        var fixture = JsonNode.Parse(source)!.AsObject();
        using var database = new FixtureMaster(fixture["tables"]!.AsObject());
        var master = new PioneerMasterCompiler(database.Path);
        var facilities = new PioneerFacilityCompiler(database.Path);
        var frames = fixture["frames"]!.AsArray().ToDictionary(row => row!["ref"]!.ToJsonString(), row => row!.AsObject());
        var count = 0;
        foreach (var row in fixture["immediates"]!.AsArray())
        {
            var before = frames[row!["before"]!.ToJsonString()];
            var after = frames[row["after"]!.ToJsonString()];
            var start = master.IslandStart(before, facilities, false, null);
            var participant = start.Training.Assignments.First(group => !group.Supports.IsEmpty).Supports[0];
            var deckIndex = start.State.Deck.IndexOf(start.State.Deck.First(card => card.Position == participant.Position));
            var conflicting = start.State.Deck.SetItem(deckIndex, start.State.Deck[deckIndex] with { Bond = participant.Bond == 100 ? 79 : 100 });
            Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
            { State = start.State with { Deck = conflicting } }, [], "conflicting-deck"));
            var data = PioneerRuntimeData.Compile(start, [], Convert.ToHexStringLower(SHA256.HashData(source)));
            var scenario = new PioneerScenario(data);
            var root = data.CreateInitialState();
            Assert.True(root.Header.KnownParts.HasFlag(PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.Tickets
                | PioneerKnownParts.CompletedCheckpoint));
            Assert.Equal(PioneerKnownParts.None, root.Header.KnownParts & (PioneerKnownParts.Planning | PioneerKnownParts.DeckId
                | PioneerKnownParts.CheckpointResult | PioneerKnownParts.CheckpointProgress));
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(new((int)PioneerStage.IslandTraining, 0, PioneerScenario.DecisionKey(root.Header.Turn, 3101)), PioneerRuntimeAction.Island())], 0);
            if (count is 0 or 6) PioneerStateAudit.AssertDeterministic(data, script);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 13, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal((int)PioneerStopCode.IslandContinuationUnknown, result.Stop!.Value.Code);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(0, result.Progress.SettledActions);
            Assert.Equal(master.Environment(after["c"]!.AsObject(), null).Current, execution.State.Header.Resources);
            Assert.Equal(TrainingNumericKernel.GenerateIsland(start.Training).Overflow, execution.State.Header.FutureOverflow);
            Assert.Equal(after["s"]!["pioneer_info"]!["pioneer_pt"]!.GetValue<int>(), execution.State.Header.DevelopmentPoints);
            Assert.Equal(after["s"]!["shima_training_info"]!["remain_num"]!.GetValue<int>(), execution.State.Header.Tickets);
            var afterBonds = after["c"]!["evaluation_info_array"]!.AsArray()
                .ToDictionary(value => value!["target_id"]!.GetValue<int>(), value => value!["evaluation"]!.GetValue<int>());
            Assert.Equal(Enumerable.Range(1, 6).Select(position => afterBonds[position]),
                execution.State.GetSection(PioneerStateSection.SupportBonds).ToArray());
            Assert.False(execution.State.Header.TurnKnown);
            Assert.False(execution.State.Header.RawPlayingStateKnown);
            Assert.Equal(root.Header.Turn, execution.State.Header.SourceTurn);
            Assert.Equal(root.WorkspaceBytes, execution.State.WorkspaceBytes);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            for (var mask = 0; mask < 64; mask++)
            {
                var assignments = start.Training.Assignments.Select(group => group with
                {
                    Supports = [.. group.Supports.Select(card => card with { Bond = (mask & (1 << (card.Position - 1))) == 0 ? 0 : 100 })]
                }).ToImmutableArray();
                var varied = start with
                {
                    Training = start.Training with { Assignments = assignments },
                    State = start.State with { Assignments = assignments,
                        Deck = [.. start.State.Deck.Select(card => card with { Bond = (mask & (1 << (card.Position - 1))) == 0 ? 0 : 100 })] }
                };
                var expected = TrainingFormulaCandidate.GenerateIsland(varied.Training);
                var compiled = PioneerRuntimeData.Compile(varied, [], "island-condition-boundaries");
                var currentScenario = new PioneerScenario(compiled);
                var current = SimulationRunner.CreateExecution(currentScenario, script, compiled.CreateInitialState(), 13, 0);
                var actual = SimulationRunner.Continue(currentScenario, script, ref current);
                Assert.Equal((int)PioneerStopCode.IslandContinuationUnknown, actual.Stop!.Value.Code);
                Assert.Equal(TrainingPreviewSettlement.Apply(varied.State.Values, varied.State.Limits, expected.Common, expected.Scenario), current.State.Header.Resources);
                Assert.Equal(result.RandomState, actual.RandomState);
            }
            if (row["after"]![1]!.GetValue<int>() == 1884)
            {
                var grants = execution.State.GetSection(PioneerStateSection.TemporaryGrants).ToArray();
                Assert.DoesNotContain(Enumerable.Range(0, grants.Length / 6).Select(index => grants[index * 6 + 3]), position => position == 2);
            }
            if (count == 0)
            {
                var missingBonus = PioneerRuntimeData.Compile(start with { State = start.State with { CheckpointBonusPercent = null } },
                    [], "missing-bonus-boundary").CreateInitialState();
                var knownZeroBonus = PioneerRuntimeData.Compile(start with { State = start.State with { CheckpointBonusPercent = 0 } },
                    [], "known-zero-bonus-boundary").CreateInitialState();
                Assert.Equal(0, missingBonus.Header.CheckpointBonusPercent);
                Assert.Equal(0, knownZeroBonus.Header.CheckpointBonusPercent);
                Assert.False(missingBonus.Header.KnownParts.HasFlag(PioneerKnownParts.CheckpointBonusPercent));
                Assert.True(knownZeroBonus.Header.KnownParts.HasFlag(PioneerKnownParts.CheckpointBonusPercent));
                var inconsistent = start.Training.Facilities.SetItem(0, start.Training.Facilities[0] with
                { Effects = [.. start.Training.Facilities[0].Effects, new TrainingFacilityEffect(1, 0, 1, 1)] });
                Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
                { Training = start.Training with { Facilities = inconsistent } }, [], "inconsistent-input"));
                Assert.NotNull(scenario.ValidateAction(root, PioneerRuntimeAction.Island() with { EventId = 1 }));
            }
            count++;
        }
        Assert.Equal(7, count);
    }

    [Fact]
    public void Real107And108ConstructionEventsCommitFacilitiesAndTicketOnlyOnce()
    {
        using var masterRows = File.OpenText(RepoFile("src/UmaAiSharp.Pioneer/Evidence/construction-master.jsonl"));
        var master = PioneerMasterData.ReadExport(masterRows, MasterHash);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-runtime-construction.json")));
        foreach (var row in fixture.RootElement.GetProperty("windows").EnumerateArray())
        {
            var rawBefore = row.GetProperty("before"); var rawAfter = row.GetProperty("after");
            var before = ConstructionEvidence.ReadObservation(rawBefore, rawBefore.GetProperty("pioneer_data_set"));
            var expected = ConstructionEvidence.ReadObservation(rawAfter, rawAfter.GetProperty("pioneer_data_set"));
            var selected = rawBefore.GetProperty("unchecked_event_array")[0];
            var current = new PioneerEvent(selected.GetProperty("event_id").GetInt32(), selected.GetProperty("chara_id").GetInt32(),
                selected.GetProperty("story_id").GetInt32(), selected.GetProperty("play_timing").GetInt32());
            var data = PioneerRuntimeData.Compile(new PioneerStart.Construction(before, current), [],
                row.GetProperty("sourceSha256").GetString()!, master);
            var scenario = new PioneerScenario(data);
            var root = data.CreateInitialState();
            Assert.True(root.Header.KnownParts.HasFlag(PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.Tickets
                | PioneerKnownParts.Planning | PioneerKnownParts.DeckId));
            Assert.Equal(PioneerKnownParts.None, root.Header.KnownParts & (PioneerKnownParts.CompletedCheckpoint
                | PioneerKnownParts.CheckpointResult | PioneerKnownParts.CheckpointBonusPercent | PioneerKnownParts.CheckpointProgress));
            var script = EventScript(root, current);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal((int)PioneerStopCode.ConstructionRemainderUnknown, result.Stop!.Value.Code);
            Assert.Equal(expected.Facilities.SelectMany(value => new[] { value.FacilityId, value.StateId }),
                execution.State.GetSection(PioneerStateSection.FacilityStates).ToArray());
            Assert.Equal(expected.IslandTrainingTickets, execution.State.Header.Tickets);
            Assert.Equal(before.DevelopmentPoints, execution.State.Header.DevelopmentPoints);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(before.PendingStoryIds!.Value.ToArray(), execution.State.GetSection(PioneerStateSection.PendingStories).ToArray());
            Assert.False(execution.State.Header.TurnKnown);
        }
    }

    [Fact]
    public void RealCheckpointResourceAndProgressComponentsShareOneStateAndDoNotConfirmTheUnfinishedEvent()
    {
        var resource = JsonNode.Parse(File.ReadLines(RepoFile("src/UmaAiSharp.Pioneer/Development/Evidence/checkpoint-resource-observations.jsonl")).First())!;
        var progress = JsonNode.Parse(File.ReadLines(RepoFile("src/UmaAiSharp.Pioneer/Development/Evidence/checkpoint-progress-observations.jsonl")).First())!;
        Assert.Equal(resource["beforeRecordId"]!.GetValue<int>(), progress["beforeRecordId"]!.GetValue<int>());
        Assert.Equal(resource["afterRecordId"]!.GetValue<int>(), progress["afterRecordId"]!.GetValue<int>());
        using var rows = File.OpenText(RepoFile("src/UmaAiSharp.Pioneer/Development/Evidence/checkpoint-master.jsonl"));
        var master = CheckpointMasterData.ReadExport(rows, MasterHash);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var context = resource["context"]!.Deserialize<CheckpointEventContext>(options);
        var beforeResources = resource["before"]!.Deserialize<CheckpointResources>(options);
        var beforeProgress = progress["before"]!.Deserialize<CheckpointProgress>(options);
        // Identity was independently read from the existing paired 268 -> 270 observation window.
        var current = new PioneerEvent(211011, 0, 400011004, 20);
        var data = PioneerRuntimeData.Compile(new PioneerStart.Checkpoint(context, current, beforeResources, beforeProgress), [],
            resource["sourceSha256"]!.GetValue<string>(), checkpointMaster: master);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState(); var script = EventScript(root, current);
        Assert.True(root.Header.KnownParts.HasFlag(PioneerKnownParts.DevelopmentPoints | PioneerKnownParts.DeckId
            | PioneerKnownParts.CheckpointResult | PioneerKnownParts.CheckpointProgress));
        Assert.Equal(PioneerKnownParts.None, root.Header.KnownParts & (PioneerKnownParts.Tickets | PioneerKnownParts.Planning
            | PioneerKnownParts.CompletedCheckpoint | PioneerKnownParts.CheckpointBonusPercent));
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal((int)PioneerStopCode.CheckpointRemainderUnknown, result.Stop!.Value.Code);
        Assert.Equal(resource["after"]!["developmentPoints"]!.GetValue<int>(), execution.State.Header.DevelopmentPoints);
        Assert.Equal(resource["after"]!["vital"]!.GetValue<int>(), execution.State.Header.Resources.Vital);
        Assert.Equal(progress["after"]!["directorBond"]!.GetValue<int>(), execution.State.Header.DirectorBond);
        Assert.False(execution.State.Header.DisplayConfirmed);
        Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
    }

    static ActionScript<PioneerDecisionView, PioneerRuntimeAction> EventScript(PioneerState root, PioneerEvent current)
        => new([new(new((int)root.Header.Stage, 0, PioneerScenario.DecisionKey(root.Header.Turn, current.EventId)), PioneerRuntimeAction.Choose(current, 0))], 0);
    static string RepoFile(string file, [CallerFilePath] string source = "")
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../..", file));

    internal static JsonObject ConstructionTables()
    {
        var tables = new JsonObject();
        foreach (var line in File.ReadLines(RepoFile("src/UmaAiSharp.Pioneer/Evidence/construction-master.jsonl")))
        {
            var entry = JsonNode.Parse(line)!;
            var table = entry["table"]!.GetValue<string>();
            if (!tables.ContainsKey(table)) tables[table] = new JsonArray();
            tables[table]!.AsArray().Add(entry["row"]!.DeepClone());
        }
        return tables;
    }

    internal sealed class FixtureMaster : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UmaAiSharp-runtime-fixture-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public FixtureMaster(JsonObject tables)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "master.sqlite");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var table in tables)
            {
                var rows = table.Value!.AsArray(); var columns = rows[0]!.AsObject().Select(pair => pair.Key).ToArray();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
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
        public void Dispose() => Directory.Delete(directory, true);
    }
}
