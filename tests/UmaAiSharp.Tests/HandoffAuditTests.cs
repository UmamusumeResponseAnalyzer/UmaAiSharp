using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class HandoffAuditTests
{
    [Fact]
    public void RetainsRepeatedTurnLabelsAndSeparatesUnobservedBetweenFrameEffects()
    {
        var first = Frame(5, State(5, 100, 50), State(5, 105, 40), 5, -10);
        var second = Frame(5, State(5, 105, 70), State(6, 108, 60), 3, -10);
        using var input = new StringReader(first + "\n" + second);
        using var output = new StringWriter();
        var audit = HandoffAudit.AuditRun("v1", input, output);
        Assert.Equal(2, audit.Frames);
        Assert.Equal(1, audit.RepeatedTurnLabels);
        Assert.Equal(1, audit.SameTurnActions);
        Assert.Equal(1, audit.AdvancingActions);
        Assert.Equal(1, audit.BetweenFrameResourceChanges);
        Assert.Equal(0, audit.DeltaMismatches);
        var frames = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<HandoffFrameAudit>(line, EvidenceJson.ReadOptions)!).ToArray();
        Assert.Equal([5, 5], frames.Select(x => x.LabelledTurn));
        Assert.Equal([1, 2], frames.Select(x => x.Line));
        Assert.Equal(30, frames[1].BetweenFrameResourceChanges["vital"]);
    }

    [Fact]
    public void MissingInitialStateDoesNotAcquireFabricatedDefaults()
    {
        using var input = new StringReader(Frame(5, null, State(6, 100, 50), 0, 0));
        using var output = new StringWriter();
        var audit = HandoffAudit.AuditRun("v5", input, output);
        Assert.Equal(1, audit.IncompleteStates);
        Assert.Equal(0, audit.AdvancingActions);
        var frame = JsonSerializer.Deserialize<HandoffFrameAudit>(output.ToString(), EvidenceJson.ReadOptions)!;
        Assert.Null(frame.BeforeTurn);
        Assert.Empty(frame.DeltaDifferences);
    }

    [Fact]
    public void DeltaDisagreementIsReportedWithoutChangingEitherSnapshot()
    {
        using var input = new StringReader(Frame(2, State(1, 100, 50), State(2, 105, 40), 99, -10));
        using var output = new StringWriter();
        var audit = HandoffAudit.AuditRun("v2", input, output);
        Assert.Equal(1, audit.DeltaMismatches);
        var frame = JsonSerializer.Deserialize<HandoffFrameAudit>(output.ToString(), EvidenceJson.ReadOptions)!;
        Assert.Equal(94, frame.DeltaDifferences["speed"]);
    }

    [Fact]
    public void UnknownAttributeValueIsNotConvertedToZero()
    {
        var before = State(1, 100, 50);
        before["stats"]![0] = null;
        using var input = new StringReader(Frame(2, before, State(2, 105, 40), 5, -10));
        using var output = new StringWriter();
        var error = Assert.Throws<InvalidDataException>(() => HandoffAudit.AuditRun("v2", input, output));
        Assert.Contains("v2/turns.jsonl 第 1 行", error.Message);
        Assert.Equal("", output.ToString());
    }

    static JsonObject State(int turn, int speed, int vital) => new()
    {
        ["turn"] = turn, ["stats"] = new JsonArray(speed, 100, 100, 100, 100),
        ["vital"] = vital, ["skill_pt"] = 100, ["pt"] = 100, ["fans"] = 100,
    };

    static string Frame(int turn, JsonObject? before, JsonObject after, int speedDelta, int vitalDelta) => new JsonObject
    {
        ["turn"] = turn, ["action"] = new JsonObject { ["command_id"] = 101 },
        ["before"] = before, ["after"] = after,
        ["delta"] = new JsonObject
        {
            ["stats"] = new JsonArray(speedDelta, 0, 0, 0, 0), ["vital"] = vitalDelta,
            ["skill_pt"] = 0, ["pt"] = 0, ["fans"] = 0,
        },
    }.ToJsonString();
}
