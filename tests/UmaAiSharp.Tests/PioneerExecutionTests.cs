using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class PioneerExecutionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-PioneerCommand-" + Guid.NewGuid().ToString("N"));

    public PioneerExecutionTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("ordinary-failure-replay.json", 1)]
    public void RealBeforeOnlyExamplesLoadAndReplayTheTwoConditionalDecisions(string example, int index)
    {
        var path = Example(example);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var window = Window(index);
        var before = window["before"]!;
        var after = window["choiceAfter"]!;
        var loaded = PioneerExecution.Load(path);
        Assert.Equal(PioneerExecutionMode.Conditional, loaded.Mode);
        Assert.Equal(2, loaded.Data.ConditionalOutcomeCount);
        Assert.Equal(Int(before, "turn"), loaded.Root.Header.Turn);
        Assert.Equal(Int(before, "staminaExecutionCount"), loaded.Root.GetSection(PioneerStateSection.TrainingExecutionCounts)[1]);
        Assert.Equal(Int(before["resources"]!, "stamina"), loaded.Root.Header.Resources.Stamina);
        Assert.Equal(PioneerStage.TrainingTurn, loaded.Root.Header.Stage);
        Assert.Null(loaded.MasterSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), loaded.InputSha256);

        var state = json["start"]!["data"]!;
        Assert.True(JsonNode.DeepEquals(before["resources"], state["environment"]!["current"]));
        Assert.Equal(before["supportBonds"]!.AsArray().Select(value => value!.GetValue<int>()), loaded.Root.GetSection(PioneerStateSection.SupportBonds).ToArray());
        Assert.Empty(state["pendingStoryIds"]!.AsArray());
        Assert.Null(state["pendingEvent"]);
        Assert.Null(state["immediateAfter"]);
        Assert.Null(state["choiceAfter"]);
        Assert.True(JsonNode.DeepEquals(window["failureEvent"], json["outcomes"]![0]!["data"]!["event"]));
        Assert.Equal(PioneerScenario.DecisionKey(Int(before, "turn"), 0),
            json["script"]![0]!["decisionId"]!["key"]!.GetValue<long>());
        Assert.Equal(PioneerScenario.DecisionKey(Int(before, "turn"), Int(window["failureEvent"]!, "eventId")),
            json["script"]![1]!["decisionId"]!["key"]!.GetValue<long>());

        var output = Path.Combine(directory, "replay.json");
        Assert.Equal("stopped", PioneerExecution.Run(path, ulong.MaxValue, output, PioneerExecutionMode.Conditional).Status);
        var report = JsonNode.Parse(File.ReadAllText(output))!;
        var result = report["results"]![0]!;
        Assert.Equal("conditional", report["mode"]!.GetValue<string>());
        Assert.Equal(0, Int(report, "completed"));
        Assert.Equal(1, Int(report, "stopped"));
        Assert.Equal("stopped", result["status"]!.GetValue<string>());
        Assert.Equal((int)PioneerStopCode.NextTurnEventsUnknown, Int(result["stop"]!, "code"));
        Assert.Equal(2, Int(result["progress"]!, "consumedActions"));
        Assert.Equal(1, Int(result["progress"]!, "settledActions"));
        Assert.Null(result["outcome"]);
        var header = report["finalState"]!["header"]!;
        Assert.True(JsonNode.DeepEquals(after["resources"], header["resources"]));
        Assert.Equal(Int(after, "turn"), Int(header, "turn"));
        Assert.Equal(Int(after, "motivation"), Int(header, "motivation"));
        Assert.Equal(Int(after, "staminaExecutionCount"), Int(header, "staminaExecutionCount"));
        Assert.True(header["turnKnown"]!.GetValue<bool>());
        Assert.False(header["rawPlayingStateKnown"]!.GetValue<bool>());
        Assert.Equal(0L, header["futureGainedSkillPoints"]!.GetValue<long>());
        AssertRandomState(result, ulong.MaxValue, 0);
    }

    [Fact]
    public void AutonomousExampleStopsBeforeConsumingAnUnknownTrainingOutcome()
    {
        var path = Example("ordinary-autonomous.json");
        var loaded = PioneerExecution.Load(path);
        Assert.Equal(PioneerExecutionMode.Autonomous, loaded.Mode);
        Assert.Equal(0, loaded.Data.ConditionalOutcomeCount);
        var output = Path.Combine(directory, "autonomous.json");
        Assert.Equal("stopped", PioneerExecution.Run(path, 1234, output, PioneerExecutionMode.Autonomous).Status);
        var report = JsonNode.Parse(File.ReadAllText(output))!;
        var result = report["results"]![0]!;
        Assert.Equal("autonomous", report["mode"]!.GetValue<string>());
        Assert.Equal((int)PioneerStopCode.TrainingOutcomeRequired, Int(result["stop"]!, "code"));
        Assert.Equal(0, Int(result["progress"]!, "consumedActions"));
        Assert.Null(result["outcome"]);
        var before = Window(1)["before"]!;
        var header = report["finalState"]!["header"]!;
        Assert.True(JsonNode.DeepEquals(before["resources"], header["resources"]));
        Assert.Equal(Int(before, "turn"), Int(header, "turn"));
        Assert.Equal(Int(before, "staminaExecutionCount"), Int(header, "staminaExecutionCount"));
        AssertRandomState(result, 1234, 0);
    }

    [Theory]
    [InlineData("duplicate-field")]
    [InlineData("missing-decision-key")]
    [InlineData("null-outcome")]
    [InlineData("autonomous-with-outcomes")]
    [InlineData("out-of-order-outcomes")]
    [InlineData("future-state-in-start")]
    public void InputBoundaryRejectsMissingAmbiguousOrFutureStateFields(string variant)
    {
        var json = JsonNode.Parse(File.ReadAllText(Example("ordinary-failure-replay.json")))!.AsObject();
        switch (variant)
        {
            case "duplicate-field": break;
            case "missing-decision-key": json["script"]![0]!["decisionId"]!.AsObject().Remove("key"); break;
            case "null-outcome": json["outcomes"]![0] = null; break;
            case "autonomous-with-outcomes": json["mode"] = "autonomous"; break;
            case "out-of-order-outcomes": json["outcomes"]![1]!["data"]!["actionOrdinal"] = 0; break;
            case "future-state-in-start": json["start"]!["data"]!["choiceAfter"] = Window(1)["choiceAfter"]!.DeepClone(); break;
            default: throw new InvalidOperationException(variant);
        }
        var text = json.ToJsonString();
        if (variant == "duplicate-field") text = text.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal);
        var input = Path.Combine(directory, "bad.json");
        File.WriteAllText(input, text);
        var error = Record.Exception(() => PioneerExecution.Load(input));
        Assert.True(error is JsonException or InvalidDataException or ArgumentException,
            $"{variant}应当在输入边界明确拒绝，实际为{error?.GetType().Name ?? "成功"}：{error?.Message}");
    }

    [Theory]
    [InlineData("conditional")]
    [InlineData("negative-seed")]
    [InlineData("unused-master")]
    [InlineData("too-many-workers")]
    public void InvalidToolInputsProduceNoReport(string variant)
    {
        var input = Example(variant == "autonomous" ? "ordinary-autonomous.json" : "ordinary-failure-replay.json");
        var output = Path.Combine(directory, "invalid-report.json");
        Assert.Throws<InvalidDataException>(() => variant switch
        {
            "conditional" => PioneerTools.Run(input, "1234", output, default),
            "negative-seed" => PioneerTools.Replay(input, "-1", output, default),
            "unused-master" => PioneerTools.Replay(input, "1234", output, default, Path.Combine(directory, "unused.mdb")),
            "too-many-workers" => PioneerTools.Batch(input, "1234", 2, 33, output, default),
            _ => throw new InvalidOperationException(variant)
        });
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExistingReportsAndInputFilesAreNeverOverwritten()
    {
        var input = Path.Combine(directory, "input.json");
        File.Copy(Example("ordinary-failure-replay.json"), input);
        var bytes = File.ReadAllBytes(input);
        Assert.Throws<InvalidDataException>(() => PioneerExecution.Run(input, 1234, input, PioneerExecutionMode.Conditional));
        Assert.Equal(bytes, File.ReadAllBytes(input));
        var output = Path.Combine(directory, "report.json");
        Assert.Equal("stopped", PioneerExecution.Run(input, 1234, output, PioneerExecutionMode.Conditional).Status);
        var report = File.ReadAllBytes(output);
        Assert.Throws<InvalidDataException>(() => PioneerExecution.Run(input, 1234, output, PioneerExecutionMode.Conditional));
        Assert.Equal(report, File.ReadAllBytes(output));
        Assert.Equal(bytes, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("members")]
    public void NestedReferenceCollectionsRejectNullElementsAtTheJsonBoundary(string collection)
    {
        var json = JsonNode.Parse(File.ReadAllText(Example("ordinary-autonomous.json")))!;
        json["start"] = JsonNode.Parse("""
            {"kind":"projectMembers","data":{"turn":3,"rawPlayingState":5,
            "event":{"eventId":1,"characterId":0,"storyId":400011029,"playTiming":6},
            "resultState":0,"trainingCharacterId":1001,"supports":[],"members":[]}}
            """);
        json["start"]!["data"]![collection] = new JsonArray((JsonNode?)null);
        var input = Path.Combine(directory, "null-collection.json");
        File.WriteAllText(input, json.ToJsonString());
        var error = Assert.Throws<JsonException>(() => PioneerExecution.Load(input));
        Assert.Contains($"$.start.data.{collection}[0]", error.Message);
    }

    [Fact]
    public void CancelledBatchWritesEveryRequestedSlotToTheReport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Combine(directory, "cancelled.json");
        Assert.Equal("cancelled", PioneerExecution.Run(Example("ordinary-failure-replay.json"), 1234, output,
            samples: 129, workers: 32, cancellationToken: cancellation.Token).Status);
        var report = JsonNode.Parse(File.ReadAllText(output))!;
        Assert.Equal(129, report["results"]!.AsArray().Count);
        Assert.Equal(0, Int(report, "completed"));
        for (var sample = 0; sample < 129; sample++)
        {
            var result = report["results"]![sample]!;
            Assert.Equal(sample, Int(result, "sampleIndex"));
            Assert.Equal("cancelled", result["stop"]!["reason"]!.GetValue<string>());
            Assert.Equal(0, Int(result["progress"]!, "consumedActions"));
            AssertRandomState(result, 1234, sample);
        }
    }

    static void AssertRandomState(JsonNode result, ulong seed, int sample)
    {
        Assert.Equal(SimulationRandom.DeriveSeed(seed, sample, SimulationRandomStream.Rules), result["randomState"]!["rules"]!.GetValue<ulong>());
        Assert.Equal(SimulationRandom.DeriveSeed(seed, sample, SimulationRandomStream.Policy), result["randomState"]!["policy"]!.GetValue<ulong>());
    }
    static int Int(JsonNode node, string key) => node[key]!.GetValue<int>();
    static JsonNode Window(int index) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "training-failure-snapshots.json")))!["windows"]![index]!;
    internal static string Example(string name, [CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../examples/pioneer", name));
    public void Dispose() => Directory.Delete(directory, true);
}
