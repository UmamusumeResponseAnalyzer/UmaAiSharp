using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;

namespace UmaAiSharp.Tests;

public sealed class McpStdioTests
{
    [Fact]
    public async Task StdioPublishesCurrentToolsAndEnforcesTheirInputs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { typeof(PioneerTools).Assembly.Location },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        server.StandardInput.AutoFlush = true;
        var stderr = server.StandardError.ReadToEndAsync();
        var requestId = 0;
        try
        {
            await Request("initialize", new()
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "UmaAiSharp.Tests", ["version"] = "1" }
            });
            await Write(new() { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            var tools = (await Request("tools/list", new()))["tools"]!.AsArray();
            string[] expected =
            [
                "evidence_audit", "evidence_checkpoint", "evidence_construction", "evidence_development_components",
                "evidence_event_components", "evidence_hint_pool", "evidence_initial_state",
                "evidence_island_action", "evidence_placement_hint", "evidence_point_gain", "evidence_race_points",
                "evidence_race_route", "evidence_scoring", "evidence_terminal", "evidence_training",
                "evidence_training_formula", "evidence_training_modes", "evidence_validate", "goals_evaluate",
                "pioneer_batch",
                "pioneer_import_capture", "pioneer_import_packet", "pioneer_recommend", "pioneer_replay", "pioneer_run"
            ];
            Assert.Equal(expected, tools.Select(tool => tool!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            var recommend = tools.Single(tool => tool!["name"]!.GetValue<string>() == "pioneer_recommend")!["inputSchema"]!;
            Assert.Equal("string", recommend["properties"]!["seed"]!["type"]!.GetValue<string>());
            Assert.Equal("integer", recommend["properties"]!["workers"]!["type"]!.GetValue<string>());
            Assert.Equal("precise", recommend["properties"]!["model"]!["default"]!.GetValue<string>());
            foreach (var tool in tools.Where(tool => tool!["name"]!.GetValue<string>().StartsWith("pioneer_", StringComparison.Ordinal)))
            {
                var schema = tool!["inputSchema"]!;
                Assert.Contains("rulesPath", schema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
                Assert.False(schema["properties"]!.AsObject().ContainsKey("masterPath"));
            }
            var invalid = await Call("pioneer_import_capture", new()
            {
                ["capturePath"] = "missing.sqlite",
                ["packetId"] = "14",
                ["masterPath"] = "missing.mdb",
                ["outputDirectory"] = "unused-output"
            });
            Assert.True(invalid["isError"]!.GetValue<bool>());
            Assert.Contains("masterPath", invalid["content"]![0]!["text"]!.GetValue<string>());

            var outcomesPath = Path.GetTempFileName();
            var reportPath = outcomesPath + ".report";
            try
            {
                File.WriteAllText(outcomesPath, """
                    [{"attributes":{"speed":1200,"stamina":1201,"power":1300,"guts":1300,"wisdom":1300},
                      "caps":{"speed":1800,"stamina":1800,"power":1800,"guts":1800,"wisdom":1800},
                      "futureGainedSkillPoints":20,"futureOverflow":null,"verifiedAttributeRating":null}]
                    """);
                var evaluated = await Call("goals_evaluate", new()
                {
                    ["goalPath"] = PioneerExecutionTests.Example("../stamina-curve-goal.json"),
                    ["outcomesPath"] = outcomesPath,
                    ["outputPath"] = reportPath
                });
                Assert.False(evaluated["isError"]?.GetValue<bool>() ?? false, evaluated.ToJsonString());
                Assert.Equal("completed", evaluated["structuredContent"]!["status"]!.GetValue<string>());
                Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(reportPath, timeout.Token))!["successCount"]!.GetValue<int>());
            }
            finally
            {
                File.Delete(outcomesPath);
                File.Delete(reportPath);
            }
        }
        finally
        {
            server.StandardInput.Close();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await server.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { server.Kill(entireProcessTree: true); }
            await stderr;
        }

        Task<JsonNode> Call(string name, JsonObject args) =>
            Request("tools/call", new() { ["name"] = name, ["arguments"] = args.DeepClone() });

        async Task<JsonNode> Request(string method, JsonObject args)
        {
            var id = ++requestId;
            await Write(new() { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = id, ["params"] = args });
            var line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            var message = JsonNode.Parse(line)!;
            Assert.Equal(id, message["id"]!.GetValue<int>());
            Assert.NotNull(message["result"]);
            return message["result"]!;
        }

        Task Write(JsonObject message) =>
            server.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), timeout.Token);
    }
}
