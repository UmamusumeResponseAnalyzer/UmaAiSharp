using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingHistoryTests
{
    [Fact]
    public void CapturedPrefixSeparatesSuccessfulOrdinaryCampAndFailureWithoutReadingFuture()
    {
        using var fixture = new Capture();
        fixture.Populate();
        var before = PioneerTrainingHistory.Read(fixture.Path, 11);
        Assert.Equal([1, 0, 0, 0, 0], before.SuccessfulOrdinaryCounts.ToArray());
        Assert.Equal((1L, 11L, 3, 1, 1), (before.History.StartPacketId, before.History.AnchorPacketId,
            before.History.TrainingActions, before.History.CampActions, before.History.FailedActions));
        fixture.Add(100, "response", "exec_command", 100, new JsonObject { ["invalid_future"] = true });
        var after = PioneerTrainingHistory.Read(fixture.Path, 11);
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Payload, after.Payload);
        Assert.Equal(before.SuccessfulOrdinaryCounts.ToArray(), after.SuccessfulOrdinaryCounts.ToArray());
        Assert.Equal([1, 0, 0, 0, 0], PioneerTrainingHistory.Read(fixture.Path, 5).SuccessfulOrdinaryCounts.ToArray());
    }

    [Theory]
    [InlineData("missingStart")]
    [InlineData("missingRequest")]
    [InlineData("missingResponse")]
    [InlineData("otherRun")]
    public void IncompleteOrUnidentifiedHistoryDoesNotProduceCounts(string fault)
    {
        using var fixture = new Capture();
        fixture.Populate(fault);
        Assert.Throws<UnknownMechanismException>(() => PioneerTrainingHistory.Read(fixture.Path, 11));
    }

    [Fact]
    public void ConflictingDuplicateCaptureIsRejected()
    {
        using var fixture = new Capture();
        fixture.Populate("conflict");
        Assert.Throws<InvalidDataException>(() => PioneerTrainingHistory.Read(fixture.Path, 11));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RuntimeHistoryCountsOnlySuccessfulOrdinaryActionsAndForksIndependently(bool camp, bool success)
    {
        var path = PioneerExecutionTests.Example(camp ? "camp-failure-replay.json" : "ordinary-failure-replay.json");
        var input = JsonSerializer.Deserialize<PioneerExecutionInput>(File.ReadAllBytes(path), PioneerExecution.JsonOptions)!;
        var start = input.Start.Data.Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        int[] order = [101, 105, 102, 103, 106];
        var counts = order.Select(id => start.Commands.Single(command => TrainingBaseTable.OrdinaryCommand(command.CommandId) == id).ExecutionCount).ToArray();
        var data = PioneerRuntimeData.Compile(start with { SuccessfulOrdinaryTrainingCounts = [.. counts] },
            [new PioneerConditionalOutcome.TrainingResult(0, success)], "training-history");
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var command = camp ? 3602 : 105;
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Train(command))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
        var stopped = SimulationRunner.Continue(scenario, script, ref execution);
        var expected = counts.ToArray();
        if (!camp && success) expected[1]++;
        Assert.Equal(expected, execution.State.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray());
        Assert.Equal(counts, root.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray());
        Assert.Equal(stopped, SimulationRunner.Continue(scenario, script, ref execution));
        var unknown = PioneerRuntimeData.Compile(start, [], "unknown-history").CreateInitialState();
        Assert.False(unknown.Header.KnownParts.HasFlag(PioneerKnownParts.SuccessfulOrdinaryTrainingCounts));
        Assert.True(unknown.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).IsEmpty);
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { SuccessfulOrdinaryTrainingCounts = [int.MaxValue, 0, 0, 0, 0] }, [], "invalid-history"));
    }

    sealed class Capture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UmaAiSharp-history-" + Guid.NewGuid().ToString("N") + ".sqlite");
        readonly SqliteConnection db;

        public Capture()
        {
            db = new SqliteConnection($"Data Source={Path};Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE packets(id INTEGER PRIMARY KEY,direction TEXT,endpoint TEXT,request_id TEXT,viewer_id TEXT,payload BLOB,app_ver TEXT,res_ver TEXT)";
            command.ExecuteNonQuery();
        }

        public void Populate(string? fault = null)
        {
            if (fault != "missingStart") Add(1, "response", "start", 1, Frame(1, [0, 0, 0, 0, 0], wrap: "single_mode_start_common", run: fault == "otherRun" ? 42 : 9876543210));
            var request = new JsonObject { ["single_mode_exec_command_request_common"] = new JsonObject
                { ["current_turn"] = 1, ["command_type"] = 1, ["command_id"] = 101 } };
            if (fault != "missingRequest") { Add(2, "request", "exec_command", 2, request); Add(4, "request", "exec_command", 2, request); }
            var response = Frame(2, [1, 0, 0, 0, 0], 101, fault == "unknownResult" ? 99 : 2);
            if (fault != "missingResponse")
            {
                Add(3, "response", "exec_command", 2, response);
                if (fault == "conflict") response["extra"] = 1;
                Add(5, "response", "exec_command", 2, response);
            }
            var action = request["single_mode_exec_command_request_common"]!;
            action["current_turn"] = 2; action["command_id"] = 3602;
            Add(6, "request", "exec_command", 6, request);
            Add(7, "response", "exec_command", 6, Frame(3, [1, 1, 0, 0, 0], 3602, 2));
            action["current_turn"] = 3; action["command_id"] = 105;
            Add(8, "request", "exec_command", 8, request);
            Add(9, "response", "exec_command", 8, Frame(3, [1, 2, 0, 0, 0], 105, 1));
            if (fault == "realReplay")
            {
                Add(10, "request", "exec_command", 10, request);
                Add(11, "response", "exec_command", 10, Frame(3, [1, 2, 0, 0, 0], 105, 1));
            }
            else
            {
                if (fault is null) Add(10, "response", "start", 1, Frame(1, [0, 0, 0, 0, 0], wrap: "single_mode_start_common"));
                Add(11, "response", "load", 11, Frame(4, [1, 2, 0, 0, 0], wrap: "single_mode_load_common"));
            }
        }

        public void Add(long id, string direction, string endpoint, int request, JsonObject payload)
        {
            using var query = db.CreateCommand();
            query.CommandText = "INSERT INTO packets VALUES($id,$direction,$endpoint,$request,'actor',$payload,'app','res')";
            query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$direction", direction);
            query.Parameters.AddWithValue("$endpoint", "/umamusume/single_mode_pioneer/" + endpoint);
            query.Parameters.AddWithValue("$request", request.ToString("x32"));
            query.Parameters.AddWithValue("$payload", MessagePackSerializer.ConvertFromJson(payload.ToJsonString()));
            query.ExecuteNonQuery();
        }

        static JsonObject Frame(int turn, int[] counts, int? command = null, int? result = null, string? wrap = null, long run = 9876543210)
        {
            var common = new JsonObject { ["chara_info"] = new JsonObject { ["turn"] = turn, ["single_mode_chara_id"] = run, ["scenario_id"] = 11 } };
            if (command.HasValue) common["command_result"] = new JsonObject { ["command_id"] = command.Value, ["result_state"] = result!.Value };
            var data = wrap is null ? common : new JsonObject { [wrap] = common };
            int[] commands = [101, 105, 102, 103, 106];
            data["pioneer_data_set"] = new JsonObject { ["training_exec_info_array"] = new JsonArray(
                commands.Select((value, index) => (JsonNode)new JsonObject { ["base_command_id"] = value, ["exec_count"] = counts[index] }).ToArray()) };
            return new JsonObject { ["data"] = data };
        }

        public void Dispose() { db.Dispose(); File.Delete(Path); }
    }
}
