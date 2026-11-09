using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class PioneerExecutionCommandTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-PioneerCommand-" + Guid.NewGuid().ToString("N"));

    public PioneerExecutionCommandTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("ordinary-failure-replay.json", 1)]
    [InlineData("camp-failure-replay.json", 0)]
    public void RealBeforeOnlyExamplesLoadAndReplayTheTwoConditionalDecisions(string example, int index)
    {
        var path = Example(example);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var window = Window(index);
        var before = window["before"]!;
        var after = window["choiceAfter"]!;
        var loaded = PioneerExecutionCommand.Load(path);
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
        Assert.Equal(3, PioneerExecutionCommand.Run("replay", Arguments(path, output, ulong.MaxValue.ToString())));
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
        var loaded = PioneerExecutionCommand.Load(path);
        Assert.Equal(PioneerExecutionMode.Autonomous, loaded.Mode);
        Assert.Equal(0, loaded.Data.ConditionalOutcomeCount);
        var output = Path.Combine(directory, "autonomous.json");
        Assert.Equal(3, Program.Main(["pioneer", "run", "--input", path, "--seed", "1234", "--output", output]));
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

    [Fact]
    public void BatchReportsPreserveEverySampleAndRandomStreamAcrossWorkersAndLengths()
    {
        JsonNode? baseline = null;
        foreach (var workers in new[] { 1, 4, 16, 32 })
        {
            var output = Path.Combine(directory, $"batch-{workers}.json");
            var options = Arguments(Example("ordinary-failure-replay.json"), output);
            options.Add("--samples", "131"); options.Add("--workers", workers.ToString());
            Assert.Equal(3, PioneerExecutionCommand.Run("batch", options));
            var report = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal(131, Int(report, "sampleCount"));
            Assert.Equal(131, Int(report, "stopped"));
            Assert.Equal(0, Int(report, "completed"));
            Assert.Null(report["finalState"]);
            Assert.Equal(131, report["results"]!.AsArray().Count);
            for (var sample = 0; sample < 131; sample++)
            {
                var result = report["results"]![sample]!;
                Assert.Equal(sample, Int(result, "sampleIndex"));
                Assert.Equal((int)PioneerStopCode.NextTurnEventsUnknown, Int(result["stop"]!, "code"));
                Assert.Null(result["outcome"]);
                AssertRandomState(result, 1234, sample);
            }
            if (baseline is not null)
            {
                Assert.True(JsonNode.DeepEquals(baseline["results"], report["results"]));
                Assert.True(JsonNode.DeepEquals(baseline["provenance"], report["provenance"]));
            }
            baseline = report;
        }
        var prefixOutput = Path.Combine(directory, "prefix.json");
        var prefixOptions = Arguments(Example("ordinary-failure-replay.json"), prefixOutput);
        prefixOptions.Add("--samples", "17"); prefixOptions.Add("--workers", "4");
        Assert.Equal(3, PioneerExecutionCommand.Run("batch", prefixOptions));
        var prefix = JsonNode.Parse(File.ReadAllText(prefixOutput))!;
        for (var sample = 0; sample < 17; sample++)
            Assert.True(JsonNode.DeepEquals(baseline!["results"]![sample], prefix["results"]![sample]));
    }

    [Theory]
    [InlineData("missing-start")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("missing-speed")]
    [InlineData("missing-event-identity")]
    [InlineData("missing-action-command")]
    [InlineData("missing-decision-key")]
    [InlineData("numeric-enum")]
    [InlineData("unknown-enum")]
    [InlineData("null-outcome")]
    [InlineData("null-start")]
    [InlineData("unsupported-start")]
    [InlineData("unsupported-outcome")]
    [InlineData("wrong-data-shape")]
    [InlineData("autonomous-with-outcomes")]
    [InlineData("out-of-order-outcomes")]
    [InlineData("future-event-in-start")]
    [InlineData("future-state-in-start")]
    public void InputBoundaryRejectsMissingAmbiguousOrFutureStateFields(string variant)
    {
        var json = JsonNode.Parse(File.ReadAllText(Example("ordinary-failure-replay.json")))!.AsObject();
        switch (variant)
        {
            case "missing-start": json.Remove("start"); break;
            case "unknown-field": json["guessMissing"] = true; break;
            case "duplicate-field": break;
            case "missing-speed": json["start"]!["data"]!["environment"]!["current"]!.AsObject().Remove("speed"); break;
            case "missing-event-identity": json["outcomes"]![0]!["data"]!["event"]!.AsObject().Remove("characterId"); break;
            case "missing-action-command": json["script"]![0]!["action"]!.AsObject().Remove("commandId"); break;
            case "missing-decision-key": json["script"]![0]!["decisionId"]!.AsObject().Remove("key"); break;
            case "numeric-enum": json["mode"] = 1; break;
            case "unknown-enum": json["mode"] = "guess"; break;
            case "null-outcome": json["outcomes"]![0] = null; break;
            case "null-start": json["start"] = null; break;
            case "unsupported-start": json["start"]!["kind"] = "unknown"; break;
            case "unsupported-outcome": json["outcomes"]![0]!["kind"] = "unknown"; break;
            case "wrong-data-shape": json["start"]!["data"] = new JsonArray(); break;
            case "autonomous-with-outcomes": json["mode"] = "autonomous"; break;
            case "out-of-order-outcomes": json["outcomes"]![1]!["data"]!["actionOrdinal"] = 0; break;
            case "future-event-in-start": json["start"]!["data"]!["pendingEvent"] = json["outcomes"]![0]!["data"]!["event"]!.DeepClone(); break;
            case "future-state-in-start": json["start"]!["data"]!["choiceAfter"] = Window(1)["choiceAfter"]!.DeepClone(); break;
            default: throw new InvalidOperationException(variant);
        }
        var text = json.ToJsonString();
        if (variant == "duplicate-field") text = text.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal);
        var input = Path.Combine(directory, "bad.json");
        File.WriteAllText(input, text);
        var error = Record.Exception(() => PioneerExecutionCommand.Load(input));
        Assert.True(error is JsonException or InvalidDataException or ArgumentException,
            $"{variant}应当在输入边界明确拒绝，实际为{error?.GetType().Name ?? "成功"}：{error?.Message}");
        var output = Path.Combine(directory, "bad-report.json");
        Assert.Equal(2, Program.Main(["pioneer", "replay", "--input", input, "--seed", "1234", "--output", output]));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ChangedTurnOrDecisionIdentityCannotSilentlyConsumeTheScript()
    {
        var json = JsonNode.Parse(File.ReadAllText(Example("ordinary-failure-replay.json")))!;
        json["start"]!["data"]!["turn"] = 31;
        var input = Path.Combine(directory, "shifted-turn.json");
        File.WriteAllText(input, json.ToJsonString());
        var output = Path.Combine(directory, "shifted-report.json");
        Assert.Equal(3, PioneerExecutionCommand.Run("replay", Arguments(input, output)));
        var report = JsonNode.Parse(File.ReadAllText(output))!;
        var result = report["results"]![0]!;
        Assert.Equal("scriptMismatch", result["stop"]!["reason"]!.GetValue<string>());
        Assert.Equal(0, Int(result["progress"]!, "consumedActions"));
        Assert.Equal(535, Int(report["finalState"]!["header"]!["resources"]!, "stamina"));
    }

    [Theory]
    [InlineData("run", "conditional")]
    [InlineData("replay", "autonomous")]
    [InlineData("replay", "negative-seed")]
    [InlineData("replay", "unused-master")]
    [InlineData("replay", "unknown-option")]
    [InlineData("batch", "zero-samples")]
    [InlineData("batch", "too-many-workers")]
    public void InvalidCommandParametersProduceNoReport(string command, string variant)
    {
        var input = Example(variant == "autonomous" ? "ordinary-autonomous.json" : "ordinary-failure-replay.json");
        var output = Path.Combine(directory, "invalid-report.json");
        var values = Arguments(input, output);
        if (command == "batch") { values.Add("--samples", "2"); values.Add("--workers", "1"); }
        switch (variant)
        {
            case "negative-seed": values["--seed"] = "-1"; break;
            case "unused-master": values["--master"] = Path.Combine(directory, "unused.mdb"); break;
            case "unknown-option": values["--guess"] = "true"; break;
            case "zero-samples": values["--samples"] = "0"; break;
            case "too-many-workers": values["--workers"] = "33"; break;
        }
        Assert.Equal(2, Program.Main(["pioneer", command, .. values.SelectMany(pair => new[] { pair.Key, pair.Value })]));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExistingReportsAndInputFilesAreNeverOverwritten()
    {
        var input = Path.Combine(directory, "input.json");
        File.Copy(Example("ordinary-failure-replay.json"), input);
        var bytes = File.ReadAllBytes(input);
        Assert.Throws<InvalidDataException>(() => PioneerExecutionCommand.Run("replay", Arguments(input, input)));
        Assert.Equal(bytes, File.ReadAllBytes(input));
        var output = Path.Combine(directory, "report.json");
        Assert.Equal(3, PioneerExecutionCommand.Run("replay", Arguments(input, output)));
        var report = File.ReadAllBytes(output);
        Assert.Throws<InvalidDataException>(() => PioneerExecutionCommand.Run("replay", Arguments(input, output)));
        Assert.Equal(report, File.ReadAllBytes(output));
        Assert.Equal(bytes, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("members")]
    [InlineData("supports")]
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
        var error = Assert.Throws<JsonException>(() => PioneerExecutionCommand.Load(input));
        Assert.Contains($"$.start.data.{collection}[0]", error.Message);
    }

    [Fact]
    public void CancelledBatchWritesEveryRequestedSlotToTheReport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Combine(directory, "cancelled.json");
        var arguments = Arguments(Example("ordinary-failure-replay.json"), output);
        arguments.Add("--samples", "129");
        arguments.Add("--workers", "32");
        Assert.Equal(3, PioneerExecutionCommand.Run("batch", arguments, cancellation.Token));
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

    static Dictionary<string, string> Arguments(string input, string output, string seed = "1234")
        => new(StringComparer.Ordinal) { ["--input"] = input, ["--seed"] = seed, ["--output"] = output };
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
