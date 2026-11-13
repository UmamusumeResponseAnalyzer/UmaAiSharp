using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;

namespace UmaAiSharp.Tests;

public sealed class McpStdioTests
{
    [Fact]
    public async Task StdioPublishesToolsAndChainsCaptureImportIntoStrictRecommendation()
    {
        using var capture = new PioneerEventCaptureTests.Capture();
        capture.Populate();
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
                "evidence_event_components", "evidence_handoff", "evidence_hint_pool", "evidence_initial_state",
                "evidence_island_action", "evidence_placement_hint", "evidence_point_gain", "evidence_race_points",
                "evidence_race_route", "evidence_scoring", "evidence_terminal", "evidence_training",
                "evidence_training_formula", "evidence_training_modes", "evidence_validate", "goals_evaluate",
                "pioneer_batch", "pioneer_build_model", "pioneer_calibrate_events", "pioneer_forecast",
                "pioneer_import_capture", "pioneer_import_packet", "pioneer_recommend", "pioneer_replay", "pioneer_run"
            ];
            Assert.Equal(expected, tools.Select(tool => tool!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            var recommend = tools.Single(tool => tool!["name"]!.GetValue<string>() == "pioneer_recommend")!["inputSchema"]!;
            Assert.Equal("string", recommend["properties"]!["seed"]!["type"]!.GetValue<string>());
            Assert.Equal("integer", recommend["properties"]!["workers"]!["type"]!.GetValue<string>());
            Assert.Equal("precise", recommend["properties"]!["model"]!["default"]!.GetValue<string>());
            Assert.DoesNotContain("masterPath", recommend["required"]!.AsArray().Select(value => value!.GetValue<string>()));

            var import = await Call("pioneer_import_capture", new()
            {
                ["capturePath"] = capture.Path,
                ["packetId"] = "14",
                ["masterPath"] = capture.Master,
                ["outputDirectory"] = capture.Output
            });
            Assert.Equal("completed", import["structuredContent"]!["status"]!.GetValue<string>());
            var artifacts = import["structuredContent"]!["artifacts"]!;
            var reportPath = capture.Output + ".recommendation.json";
            try
            {
                var args = new JsonObject
                {
                    ["inputPath"] = artifacts["input"]!.GetValue<string>(),
                    ["candidatesPath"] = artifacts["candidates"]!.GetValue<string>(),
                    ["goalPath"] = PioneerExecutionTests.Example("../power-goal.json"),
                    ["seed"] = ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["samples"] = 1,
                    ["workers"] = 1,
                    ["outputPath"] = reportPath,
                    ["extra"] = true
                };
                var invalid = await Call("pioneer_recommend", args);
                Assert.True(invalid["isError"]?.GetValue<bool>() ?? false, "MCP 接受了未知参数 extra。");
                Assert.Contains("extra", invalid["content"]![0]!["text"]!.GetValue<string>());
                Assert.False(File.Exists(reportPath));

                args.Remove("extra");
                args["model"] = "invalid";
                invalid = await Call("pioneer_recommend", args);
                Assert.True(invalid["isError"]?.GetValue<bool>() ?? false, "MCP 接受了未知数值模型。");
                Assert.False(File.Exists(reportPath));

                args["model"] = "fast";
                var stopped = await Call("pioneer_recommend", args);
                Assert.False(stopped["isError"]?.GetValue<bool>() ?? false);
                Assert.Equal("stopped", stopped["structuredContent"]!["status"]!.GetValue<string>());
                Assert.Equal(reportPath, stopped["structuredContent"]!["artifacts"]!["report"]!.GetValue<string>());
                var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, timeout.Token))!;
                var provenance = report["search"]!["candidates"]![0]!["batch"]!["provenance"]!;
                Assert.Contains("/fast/", provenance["rulesIdentity"]!.GetValue<string>());
            }
            finally { File.Delete(reportPath); }
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
