using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingFailureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AttemptAndChoiceMatchSeparatelyDecodedRawWindows(int index)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "training-failure-snapshots.json")))!;
        var window = fixture["windows"]![index]!;
        Assert.Equal("ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b", window["sourceSha256"]!.GetValue<string>());
        var packets = window["packets"]!.AsArray();
        Assert.Equal(7, packets.Count);
        Assert.All(packets, packet => Assert.Matches("^[0-9a-f]{64}$", packet!["payloadSha256"]!.GetValue<string>()));
        var rawEvent = window["failureEvent"]!;
        var failure = new TrainingFailureEvent(Int(rawEvent, "eventId"), Int(rawEvent, "characterId"), Int(rawEvent, "storyId"), Int(rawEvent, "playTiming"));
        var initial = State(window["before"]!, TrainingFailureStage.TrainingDecision, null);
        var command = Int(window, "commandId");
        var bonds = window["before"]!["supportBonds"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray();
        var pending = TrainingFailureSettlement.BeginResolvedFailure(initial, bonds, command, failure);
        AssertState(State(window["immediateAfter"]!, TrainingFailureStage.FailureChoice, failure), pending);
        Assert.Equal(1, Int(window["before"]!, "playingState"));
        Assert.Equal(5, Int(window["immediateAfter"]!, "playingState"));
        Assert.Throws<IllegalActionException>(() => TrainingFailureSettlement.BeginResolvedFailure(pending, bonds, command, failure));
        var (after, consumed) = TrainingFailureSettlement.ResolveChoice(pending, failure.EventId, Int(window, "choiceNumber"), ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne, false);
        Assert.False(consumed);
        AssertState(State(window["choiceAfter"]!, TrainingFailureStage.NextTurnEventsRequired, null), after);
        Assert.Equal(1, Int(window["choiceAfter"]!, "playingState"));
        Assert.Throws<IllegalActionException>(() => TrainingFailureSettlement.ResolveChoice(after, failure.EventId, 1, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne, false));
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureSettlement.ResolveChoice(pending, failure.EventId, 2, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne, false));
        Assert.Equal(Int(window["before"]!, "staminaExecutionCount"), initial.StaminaExecutionCount);
    }

    [Fact]
    public void UnknownFailureEffectsDoNotSilentlyUseTheObservedBranch()
    {
        var initial = new TrainingFailureState(104503, 30, 5, new(100, 535, 100, 100, 100, 65, 300),
            1, TrainingFailureStage.TrainingDecision, null);
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureSettlement.BeginResolvedFailure(initial, [99, 100, 100, 100, 100, 100], 105, new(7043, 1045, 501045713, 6)));
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureSettlement.BeginResolvedFailure(initial, [100, 100, 100, 100, 100, 100],
            106, new(7043, 1045, 501045713, 6)));
    }

    static int Int(JsonNode node, string key) => node[key]!.GetValue<int>();

    static TrainingFailureState State(JsonNode value, TrainingFailureStage stage, TrainingFailureEvent? pending)
    {
        var resources = value["resources"]!;
        return new(Int(value, "cardId"), Int(value, "turn"), Int(value, "motivation"),
            new(Int(resources, "speed"), Int(resources, "stamina"), Int(resources, "power"), Int(resources, "guts"),
                Int(resources, "wisdom"), Int(resources, "vital"), Int(resources, "skillPoints")),
            Int(value, "staminaExecutionCount"),
            stage, pending);
    }

    static void AssertState(TrainingFailureState expected, TrainingFailureState actual)
    {
        Assert.Equal(expected, actual);
    }
}
