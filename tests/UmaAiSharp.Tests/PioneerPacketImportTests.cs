using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class PioneerPacketImportTests
{
    [Fact]
    public void CurrentPacketCompilesTrainingChoicesAndEndingIntoExecutableInputs()
    {
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "island-action-snapshots.json")))!;
        foreach (var table in PioneerRuntimeComponentTests.ConstructionTables()) fixture["tables"]![table.Key] = table.Value!.DeepClone();
        using var master = new PioneerRuntimeComponentTests.FixtureMaster(fixture["tables"]!.AsObject());
        var masterHash = SHA256.HashData(File.ReadAllBytes(master.Path));
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-packet-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var frames = fixture["frames"]!.AsArray().ToDictionary(row => row!["ref"]!.ToJsonString(), row => row!);
            var checkedFrames = 0;
            foreach (var row in fixture["immediates"]!.AsArray())
            {
                var frame = frames[row!["before"]!.ToJsonString()];
                var common = new JsonObject { ["chara_info"] = frame["c"]!.DeepClone(), ["home_info"] = frame["home"]!.DeepClone(),
                    ["unchecked_event_array"] = frame["events"]!.DeepClone(), ["viewer_id"] = 123456789 };
                // The component fixture projects Pioneer fields without retaining the protocol scenario tag.
                common["chara_info"]!["scenario_id"] = 11;
                var wrapped = checkedFrames % 2 == 0;
                var data = wrapped ? new JsonObject { ["single_mode_load_common"] = common } : common;
                data["pioneer_data_set"] = frame["s"]!.DeepClone();
                var packet = Path.Combine(directory, $"training-{checkedFrames}.msgpack");
                File.WriteAllBytes(packet, MessagePackSerializer.ConvertFromJson(new JsonObject { ["data"] = data }.ToJsonString()));
                var output = Path.Combine(directory, $"training-{checkedFrames}");
                Assert.Equal("completed", PioneerImport.Run(packet, wrapped ? "/single_mode/load" : "/single_mode/check_event",
                    null, master.Path, output).Status);
                var loaded = PioneerExecution.Load(Path.Combine(output, "input.json"), master.Path);
                Assert.Equal(PioneerStage.TrainingTurn, loaded.Root.Header.Stage);
                Assert.Equal(frame["c"]!["turn"]!.GetValue<int>(), loaded.Root.Header.Turn);
                var candidates = JsonSerializer.Deserialize<RootCandidate<PioneerRuntimeAction>[]>(File.ReadAllBytes(Path.Combine(output, "candidates.json")), PioneerExecution.JsonOptions)!;
                Assert.Equal(5, candidates.Count(candidate => candidate.Action.Kind == PioneerActionKind.Train));
                Assert.Single(candidates, candidate => candidate.Action.Kind == PioneerActionKind.Rest);
                var scenario = new PioneerScenario(loaded.Data);
                foreach (var candidate in candidates) Assert.Null(scenario.ValidateAction(loaded.Root, candidate.Action));
                Assert.DoesNotContain("viewer", File.ReadAllText(Path.Combine(output, "input.json")), StringComparison.OrdinalIgnoreCase);
                checkedFrames++;
            }
            Assert.Equal(7, checkedFrames);

            var endings = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-character-endings.json")))!["cases"]!.AsArray();
            var cards = fixture["tables"]!["card_data"]!.AsArray();
            var ending = endings.First(row => cards.Any(card => card!["chara_id"]!.GetValue<int>() == row!["characterId"]!.GetValue<int>()))!;
            var chara = frames.Values.First()["c"]!.DeepClone();
            chara["scenario_id"] = 11;
            chara["card_id"] = cards.First(card => card!["chara_id"]!.GetValue<int>() == ending["characterId"]!.GetValue<int>())!["id"]!.DeepClone();
            chara["state"] = 3; chara["turn"] = 78; chara["playing_state"] = 5; chara["motivation"] = ending["motivation"]!.DeepClone();
            var fields = new[] { "speed", "stamina", "power", "guts", "wiz", "vital", "skill_point" };
            for (var i = 0; i < fields.Length; i++) chara[fields[i]] = ending["before"]![i]!.DeepClone();
            for (var i = 0; i < 6; i++) chara["max_" + fields[i]] = ending["caps"]![i]!.DeepClone();
            var response = new JsonObject { ["data"] = new JsonObject { ["chara_info"] = chara,
                ["unchecked_event_array"] = new JsonArray(ending["event"]!.DeepClone()) } };
            var endingPacket = Path.Combine(directory, "ending.msgpack");
            File.WriteAllBytes(endingPacket, MessagePackSerializer.ConvertFromJson(response.ToJsonString()));
            var endingOutput = Path.Combine(directory, "ending");
            Assert.Equal("completed", PioneerImport.Run(endingPacket, "/single_mode/check_event", null, master.Path, endingOutput).Status);
            var goal = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/power-goal.json"));
            var recommendation = Path.Combine(directory, "recommendation.json");
            Assert.Equal("completed", PioneerRecommendation.Run(Path.Combine(endingOutput, "input.json"), goal,
                Path.Combine(endingOutput, "candidates.json"), 71, 3, 4, recommendation).Status);
            var result = JsonNode.Parse(File.ReadAllBytes(recommendation))!["search"]!;
            Assert.Equal("completed", result["status"]!.GetValue<string>());
            Assert.Equal(ending["finish"]![0]!.GetValue<int>(), result["candidates"]![0]!["batch"]!["results"]![0]!["outcome"]!["attributes"]!["speed"]!.GetValue<int>());
            Assert.Throws<InvalidDataException>(() => PioneerImport.Run(endingPacket, "/single_mode/check_event", null, master.Path, endingOutput));

            response["data"]!["unchecked_event_array"] = new JsonArray();
            File.WriteAllBytes(endingPacket, MessagePackSerializer.ConvertFromJson(response.ToJsonString()));
            var unknown = Path.Combine(directory, "unknown");
            Assert.Equal("stopped", PioneerImport.Run(endingPacket, "/single_mode/check_event", null, master.Path, unknown).Status);
            Assert.False(File.Exists(Path.Combine(unknown, "input.json")));
            Assert.Equal("unknownMechanism", JsonNode.Parse(File.ReadAllBytes(Path.Combine(unknown, "import.json")))!["reason"]!.GetValue<string>());
            chara["scenario_id"] = 1;
            var invalidPayloads = new[]
            {
                MessagePackSerializer.ConvertFromJson(response.ToJsonString()),
                MessagePackSerializer.ConvertFromJson("{\"data\":{\"single_mode_load_common\":\"invalid\"}}"),
                new byte[] { 0xc1 },
            };
            for (var i = 0; i < invalidPayloads.Length; i++)
            {
                File.WriteAllBytes(endingPacket, invalidPayloads[i]);
                var invalid = Path.Combine(directory, $"invalid-{i}");
                Assert.Throws<InvalidDataException>(() => PioneerTools.ImportPacket(endingPacket, i == 1 ? "/single_mode/load" : "/single_mode/check_event",
                    invalid, default, masterPath: master.Path));
                Assert.False(Directory.Exists(invalid));
            }
            Assert.Equal(masterHash, SHA256.HashData(File.ReadAllBytes(master.Path)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
