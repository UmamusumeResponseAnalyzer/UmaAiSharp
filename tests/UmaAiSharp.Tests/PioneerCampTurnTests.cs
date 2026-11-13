using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerCampTurnTests
{
    [Fact]
    public void RealCampPanelsImportAndExecuteWithTheirOwnLevelsFacilitiesAndPointGains()
    {
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/pioneer-camp-turns.json")))!;
        var tables = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/island-action-snapshots.json")))!["tables"]!.AsObject();
        foreach (var table in fixture["tables"]!.AsObject()) tables[table.Key] = table.Value!.DeepClone();
        foreach (var table in PioneerRuntimeComponentTests.ConstructionTables()) tables[table.Key] = table.Value!.DeepClone();
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(tables);
        var master = new PioneerMasterCompiler(PioneerMasterReader.ReadTraining(database.Path)); var facilities = new PioneerFacilityCompiler(PioneerMasterReader.ReadFacilities(database.Path));
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-camp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var index = 0;
            foreach (var row in fixture["cases"]!.AsArray())
            {
                var frame = row!["frame"]!.AsObject();
                var start = master.TrainingStart(frame, false, null, facilities);
                Assert.NotNull(start.Camp);
                Assert.All(start.Commands, command => Assert.Equal(5, command.Level));
                Assert.Contains(start.Camp.OrdinaryLevels, level => level < 5);
                var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, true)], "camp-real");
                var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
                Assert.True(scenario.Inspect(root, 0).Decision.Context.IsCamp);
                Assert.Equal(start.Camp.OrdinaryLevels.ToArray(), root.GetSection(PioneerStateSection.TrainingLevels).ToArray());
                Assert.NotNull(scenario.ValidateAction(root, PioneerRuntimeAction.Train(101)));
                Assert.Throws<ArgumentException>(() => scenario.PreviewTraining(root, 101));
                foreach (var command in start.Commands)
                {
                    var current = frame["home"]!["command_info_array"]!.AsArray().Single(x => x!["command_id"]!.GetValue<int>() == command.CommandId)!;
                    var extra = frame["s"]!["command_info_array"]!.AsArray().Single(x => x!["command_id"]!.GetValue<int>() == command.CommandId)!;
                    var preview = scenario.PreviewTraining(root, command.CommandId);
                    Assert.Equal(Values(current["params_inc_dec_info_array"]!), preview.Gains.Common);
                    Assert.Equal(Values(extra["params_inc_dec_info_array"]!), preview.Gains.Scenario);
                    Assert.Equal(current["failure_rate"]!.GetValue<int>(), preview.FailureRate);
                    var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>([new(scenario.Inspect(root, 0).Decision.Id,
                        PioneerRuntimeAction.Train(command.CommandId))], 0);
                    var execution = SimulationRunner.CreateExecution(scenario, script, root, 77, 0);
                    var result = SimulationRunner.Continue(scenario, script, ref execution);
                    Assert.Equal((int)PioneerStopCode.TrainingEventsRequired, result.Stop!.Value.Code);
                    Assert.Equal(root.GetSection(PioneerStateSection.TrainingLevels).ToArray(), execution.State.GetSection(PioneerStateSection.TrainingLevels).ToArray());
                    Assert.Equal(preview.Gains.Overflow, execution.State.Header.FutureOverflow);
                    if (start.CompletedCheckpoint < 5)
                    {
                        var points = frame["s"]!["pioneer_point_gain_info_array"]!.AsArray()
                            .Single(x => x!["command_type"]!.GetValue<int>() == 1 && x["command_id"]!.GetValue<int>() == command.CommandId)!;
                        Assert.Equal(start.DevelopmentPoints + points["gain_num"]!.GetValue<int>(), execution.State.Header.DevelopmentPoints);
                    }
                    else Assert.Equal(start.DevelopmentPoints, execution.State.Header.DevelopmentPoints);
                    if (row["transition"] is { } observed && observed["command"]!.GetValue<int>() == command.CommandId)
                    {
                        var values = observed["resources"]!;
                        Assert.Equal(new(values["speed"]!.GetValue<int>(), values["stamina"]!.GetValue<int>(), values["power"]!.GetValue<int>(),
                            values["guts"]!.GetValue<int>(), values["wiz"]!.GetValue<int>(), values["vital"]!.GetValue<int>(), values["skill_point"]!.GetValue<int>()),
                            execution.State.Header.Resources);
                        Assert.Equal(observed["developmentPoints"]!.GetValue<int>(), execution.State.Header.DevelopmentPoints);
                        var counts = observed["executionCounts"]!.AsArray().ToDictionary(x => x!["base_command_id"]!.GetValue<int>(), x => x!["exec_count"]!.GetValue<int>());
                        Assert.Equal(new[] { 101, 105, 102, 103, 106 }.Select(id => counts[id]), execution.State.GetSection(PioneerStateSection.TrainingExecutionCounts).ToArray());
                    }
                    Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
                }
                if (index == 0)
                {
                    PioneerTrainingTurnTests.VerifyBondSettlement(start);
                    Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { Camp = null }, [], "missing-camp"));
                    Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
                    { Commands = start.Commands.SetItem(0, start.Commands[0] with { GuestCount = 6 }) }, [], "overfull-camp"));
                }

                var packet = Path.Combine(directory, $"{index}.msgpack");
                var payload = new JsonObject { ["data"] = new JsonObject { ["chara_info"] = frame["c"]!.DeepClone(),
                    ["home_info"] = frame["home"]!.DeepClone(), ["pioneer_data_set"] = frame["s"]!.DeepClone(), ["unchecked_event_array"] = new JsonArray() } };
                File.WriteAllBytes(packet, MessagePackSerializer.ConvertFromJson(payload.ToJsonString()));
                var output = Path.Combine(directory, $"import-{index++}");
                Assert.Equal("completed", PioneerImport.Run(packet, row["endpoint"]!.GetValue<string>(), null, database.Path, output).Status);
                var loaded = PioneerExecution.Load(Path.Combine(output, "input.json"), database.Path);
                Assert.True(new PioneerScenario(loaded.Data).Inspect(loaded.Root, 0).Decision.Context.IsCamp);
                var candidates = JsonSerializer.Deserialize<RootCandidate<PioneerRuntimeAction>[]>(File.ReadAllBytes(Path.Combine(output, "candidates.json")), PioneerExecution.JsonOptions)!;
                Assert.Equal(Enumerable.Range(3601, 6), candidates.Select(candidate => candidate.Action.CommandId));
                Assert.Equal(PioneerActionKind.Rest, candidates[^1].Action.Kind);
            }
            Assert.Equal(4, index);
        }
        finally { Directory.Delete(directory, true); }
    }

    static TrainingValues Values(JsonNode changes)
    {
        var values = changes.AsArray().ToDictionary(x => x!["target_type"]!.GetValue<int>(), x => x!["value"]!.GetValue<int>());
        return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3),
            values.GetValueOrDefault(4), values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
    }
}
