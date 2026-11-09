using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingPreviewTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Training-" + Guid.NewGuid().ToString("N"));

    public TrainingPreviewTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void AddsBothPreviewComponentsWithoutHalvingHighAttributesAndClipsVitalAtObservedLimit()
    {
        var before = new TrainingValues(1210, 1000, 1200, 800, 900, 99, 100);
        var limits = new TrainingLimits(1900, 1800, 1800, 1600, 1600, 108);

        var after = TrainingPreviewSettlement.Apply(before, limits,
            [new(1, 17), new(3, 5), new(10, 10), new(30, 6)], [new(1, 12), new(3, 4), new(30, 7)]);

        Assert.Equal(new TrainingValues(1239, 1000, 1209, 800, 900, 108, 113), after);
        Assert.Equal(1210, before.Speed);
    }

    [Fact]
    public void UnknownTargetsAndUnobservedClippingCannotBecomeInventedGainsOrOverflow()
    {
        var before = new TrainingValues(100, 100, 100, 100, 100, 10, 100);
        var limits = new TrainingLimits(110, 110, 110, 110, 110, 100);

        Assert.Throws<UnknownMechanismException>(() => TrainingPreviewSettlement.Apply(before, limits, [new(999, 1)], []));
        Assert.Throws<UnknownMechanismException>(() => TrainingPreviewSettlement.Apply(before, limits, [new(1, 20)], []));
        Assert.Throws<UnknownMechanismException>(() => TrainingPreviewSettlement.Apply(before, limits, [new(10, -20)], []));
    }

    [Fact]
    public void RepeatableAuditSeparatesStaleSnapshotsFromApplicableLocalTransitions()
    {
        var before = Frame(1, 10, 98, 100, false);
        var after = Frame(3, 10, 100, 107, true);
        var staleBefore = Frame(5, 20, 98, 100, false);
        var staleAfter = Frame(7, 22, 60, 150, true);
        File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"),
            new[] { before, after, staleBefore, staleAfter }.Select(value => JsonSerializer.Serialize(value, EvidenceJson.Options).ReplaceLineEndings("")));
        var actions = new[] { Action(2, "exchange-3", 10, 98), Action(6, "exchange-7", 21, 60) };
        File.WriteAllLines(Path.Combine(directory, "pioneer-actions.jsonl"),
            actions.Select(value => JsonSerializer.Serialize(value, EvidenceJson.Options).ReplaceLineEndings("")));
        WriteExchanges([Exchange("exchange-3", 2, 3), Exchange("exchange-7", 6, 7)]);

        var report = TrainingPreviewAudit.Run(directory, Path.Combine(directory, "report"));

        Assert.Equal(2, report.TrainingCommandResponses);
        Assert.Equal(1, report.EligibleTransitions);
        Assert.Equal(1, report.ExactMatches);
        Assert.Equal(1, report.VitalUpperClips);
        Assert.Equal(1, report.Counts["StaleSnapshot"]);
        Assert.Equal(1, report.Counts["TurnDelta:0"]);
        Assert.Equal("ObservedTransitionsMatched", report.Status);
        Assert.Contains("NoCounterfactualTrainingGainGeneration", report.ApplicabilityLimits);
        Assert.Contains("RawPreCapGainsAndAttributeOverflowUnknown", report.ApplicabilityLimits);
    }

    [Theory]
    [InlineData("ConflictingCapture", 3)]
    [InlineData("Paired", 99)]
    public void AuditRejectsConflictingOrUnreferencedResponses(string status, long responseId)
    {
        File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"),
            new[] { Frame(1, 10, 98, 100, false), Frame(3, 10, 100, 107, true) }
                .Select(value => JsonSerializer.Serialize(value, EvidenceJson.Options).ReplaceLineEndings("")));
        File.WriteAllText(Path.Combine(directory, "pioneer-actions.jsonl"),
            JsonSerializer.Serialize(Action(2, "exchange-3", 10, 98), EvidenceJson.Options).ReplaceLineEndings(""));
        WriteExchanges([Exchange("exchange-3", 2, responseId) with { Status = status }]);

        var report = TrainingPreviewAudit.Run(directory, Path.Combine(directory, "report"));

        Assert.Equal(0, report.EligibleTransitions);
        Assert.Equal(1, report.Counts["ExchangeNotPaired"]);
        Assert.Equal("NoEligibleSamples", report.Status);
    }

    static PioneerObservation Frame(long id, int turn, int vital, int speed, bool result)
    {
        var chara = new JsonObject
        {
            ["turn"] = turn, ["speed"] = speed, ["stamina"] = 100, ["power"] = 100,
            ["guts"] = 100, ["wiz"] = 100, ["vital"] = vital, ["skill_point"] = 100,
            ["max_speed"] = 200, ["max_stamina"] = 200, ["max_power"] = 200,
            ["max_guts"] = 200, ["max_wiz"] = 200, ["max_vital"] = 100
        };
        var data = new JsonObject { ["chara_info"] = chara };
        if (result) data["command_result"] = new JsonObject { ["command_id"] = 101, ["result_state"] = 2 };
        else
        {
            data["home_info"] = new JsonObject
            {
                ["command_info_array"] = new JsonArray(new JsonObject
                {
                    ["command_id"] = 101, ["command_type"] = 1, ["is_enable"] = 1,
                    ["params_inc_dec_info_array"] = new JsonArray(
                        new JsonObject { ["target_type"] = 1, ["value"] = 5 },
                        new JsonObject { ["target_type"] = 10, ["value"] = 5 })
                })
            };
            data["pioneer_data_set"] = new JsonObject
            {
                ["command_info_array"] = new JsonArray(new JsonObject
                {
                    ["command_id"] = 101, ["command_type"] = 1,
                    ["params_inc_dec_info_array"] = new JsonArray(new JsonObject { ["target_type"] = 1, ["value"] = 2 })
                })
            };
        }
        return new("fixture", id, $"exchange-{id}", "/umamusume/single_mode_pioneer/" + (result ? "exec_command" : "check_event"),
            "actor-and-run", "2026-01-01T00:00:00Z", new Dictionary<string, ObservedInteger>(),
            new Dictionary<string, string>(), [], data);
    }

    static ObservedAction Action(long id, string exchange, int turn, int vital) => new("fixture", id, exchange,
        "/umamusume/single_mode_pioneer/exec_command", "2026-01-01T00:00:00Z", new JsonObject
        {
            ["single_mode_exec_command_request_common"] = new JsonObject
            {
                ["command_type"] = 1, ["command_id"] = 101, ["current_turn"] = turn, ["current_vital"] = vital
            }
        });

    static ExchangeSummary Exchange(string key, long requestId, long responseId) => new(key,
        "/umamusume/single_mode_pioneer/exec_command", "Paired", [new("fixture", requestId)], [new("fixture", responseId)],
        1, 1, 0, ["app"], ["res"]);

    void WriteExchanges(ExchangeSummary[] exchanges) => File.WriteAllLines(Path.Combine(directory, "exchanges.jsonl"),
        exchanges.Select(value => JsonSerializer.Serialize(value, EvidenceJson.Options).ReplaceLineEndings("")));

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
