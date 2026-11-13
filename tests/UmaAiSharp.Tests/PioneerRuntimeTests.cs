using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerRuntimeTests
{
    [Fact]
    public void DecisionIdentityIncludesTheTurnAndActionPayloadsDoNotIgnoreExtraFields()
    {
        var (data, expected) = FailureCase(1);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        var originalScript = FailureScript(root, expected.Event);
        var changedStart = new PioneerStart.FailureTraining(new(root.Header.CardId, root.Header.Turn + 1, root.Header.Motivation,
            root.Header.Resources, root.Header.StaminaExecutionCount,
            TrainingFailureStage.TrainingDecision, null), root.Header.CommandId,
            [.. root.GetSection(PioneerStateSection.SupportBonds)], [.. root.GetSection(PioneerStateSection.CharacterEffects)]);
        var changedData = PioneerRuntimeData.Compile(changedStart, [], "different-current-turn");
        var changedScenario = new PioneerScenario(changedData); var changedRoot = changedData.CreateInitialState();
        var wrongTurn = SimulationRunner.CreateExecution(changedScenario, originalScript, changedRoot, 0, 0);
        var stopped = SimulationRunner.Continue(changedScenario, originalScript, ref wrongTurn);
        Assert.Equal(SimulationStopReason.ScriptMismatch, stopped.Stop!.Value.Reason);
        Assert.Equal(0, stopped.Progress.ConsumedActions);
        Assert.NotNull(scenario.ValidateAction(root, PioneerRuntimeAction.Train(root.Header.CommandId) with { EventId = 1 }));
        var execution = SimulationRunner.CreateExecution(scenario, originalScript, root, 0, 0);
        _ = SimulationRunner.Advance(scenario, originalScript, ref execution);
        var choice = PioneerRuntimeAction.Choose(execution.State.Header.Event, 1) with { CommandId = 105 };
        Assert.NotNull(scenario.ValidateAction(execution.State, choice));

        var mismatchData = PioneerRuntimeData.Compile(changedStart,
            [new PioneerConditionalOutcome.FailurePenalty(0, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne)], "wrong-condition-kind");
        var mismatchScenario = new PioneerScenario(mismatchData); var mismatchRoot = mismatchData.CreateInitialState();
        var mismatchScript = FailureScript(mismatchRoot, expected.Event);
        var mismatch = SimulationRunner.CreateExecution(mismatchScenario, mismatchScript, mismatchRoot, 0, 0);
        var wrongCondition = SimulationRunner.Continue(mismatchScenario, mismatchScript, ref mismatch);
        Assert.Equal((int)PioneerStopCode.OutcomeIdentityMismatch, wrongCondition.Stop!.Value.Code);
        Assert.Equal(0, wrongCondition.Progress.ConsumedActions);
        Assert.Equal(mismatchRoot.Header, mismatch.State.Header);
    }

    [Fact]
    public void ConditionalRewardAndVariableBuffersAreCopiedWithoutAliasingOrRepeatedRewards()
    {
        var current = new PioneerEvent(41, 7, 6001, 6);
        var start = new PioneerStart.HintAttribute(12, 5, current,
            new(1199, 100, 1800, 100, 100, 50, 200), new(1800, 1800, 1800, 1800, 1800, 100),
            [new(5, 0, 6, 0, 0, 0, 10)]);
        var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.HintAttributeGroup(0, 0)], "boundary-test");
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var script = SingleEventScript(root, current, 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 9, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal((int)PioneerStopCode.HintRemainderUnknown, result.Stop!.Value.Code);
        Assert.Equal(1202, execution.State.Header.Resources.Speed);
        Assert.Equal(210, execution.State.Header.Resources.SkillPoints);
        Assert.Equal(10L, execution.State.Header.FutureGainedSkillPoints);
        Assert.Equal(3, execution.State.Header.FutureOverflow.Power);
        Assert.True(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.FutureOverflow));
        Assert.Equal(default, root.Header.FutureOverflow);
        Assert.Equal(200, root.Header.Resources.SkillPoints);
        Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
        Assert.Equal(3, execution.State.Header.FutureOverflow.Power);
    }

    [Fact]
    public void ActualProjectMemberRecordsAreGeneratedInOwnedStorageAndRemainStoppedAtMissingRemainder()
    {
        var row = ReadEvidence<ProjectMemberFixture>("project-member-observations.jsonl").First(value => value.StoryId == 400011008);
        // This fixture preserves numeric components; the local test event ID is only an identity token.
        var current = new PioneerEvent(1, 0, row.StoryId, 6);
        var start = new PioneerStart.ProjectMembers(row.Turn, 5, current, row.ResultState, row.TrainingCharacter, row.Supports, row.Before);
        var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.ProjectNpcSelection(0, row.SelectedNpcIds)], row.SourceSha256);
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var script = SingleEventScript(root, current, 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal((int)PioneerStopCode.ProjectRemainderUnknown, result.Stop!.Value.Code);
        Assert.Equal(row.ExpectedMembers.SelectMany(value => new[] { value.TargetId, value.CharacterId }),
            execution.State.GetSection(PioneerStateSection.ProjectMembers).ToArray());
        Assert.Equal(row.Before.Length * 2, root.GetSection(PioneerStateSection.ProjectMembers).Length);
        var copy = root.Clone();
        execution.State.CopyInto(copy);
        var expected = copy.GetSection(PioneerStateSection.ProjectMembers).ToArray();
        root.CopyInto(execution.State);
        Assert.Equal(expected, copy.GetSection(PioneerStateSection.ProjectMembers).ToArray());
        Assert.Equal(root.UsedBufferIntegers, execution.State.UsedBufferIntegers);
        Assert.Equal(root.BufferCapacityIntegers, copy.BufferCapacityIntegers);
    }

    [Fact]
    public void TuckerAndAlreadyAppliedDisplayUseActualComponentFixturesWithoutInventingTheOtherRewards()
    {
        var row = ReadEvidence<TuckerFriendFixture>("tucker-friend-observations.jsonl").First(value => value.Context.StoryId == 809049011);
        var current = new PioneerEvent(1, 9049, row.Context.StoryId, 6);
        var start = new PioneerStart.TuckerFriend(20, 5, current, row.Context, row.Before, 100);
        var data = PioneerRuntimeData.Compile(start, [], row.SourceSha256);
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var script = SingleEventScript(root, current, row.Context.ChoiceNumber);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal((int)PioneerStopCode.TuckerRemainderUnknown, result.Stop!.Value.Code);
        Assert.Equal(row.After.Vital, execution.State.Header.Resources.Vital);
        Assert.Equal(row.After.MaxVital, execution.State.Header.Limits.Vital);
        Assert.Equal(row.After.StoryStep, execution.State.Header.TuckerStoryStep);
        Assert.False(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.Resources));

        var display = ReadEvidence<LateCheckpointFixture>("late-checkpoint-confirmations.jsonl").First();
        current = new(display.EventId, 0, display.StoryId, display.PlayTiming);
        var confirmed = LateCheckpointConfirmation.ObserveAppliedBoundary(display.Turn, display.Phase, display.ResultState,
            display.StoryId, display.PlayTiming, display.ChoiceCount);
        data = PioneerRuntimeData.Compile(new PioneerStart.CheckpointDisplay(display.Turn, display.Phase, current, confirmed), [], display.SourceSha256);
        scenario = new(data); root = data.CreateInitialState(); script = SingleEventScript(root, current, display.ChoiceNumber);
        execution = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
        result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.True(execution.State.Header.DisplayConfirmed);
        Assert.Equal((int)PioneerStopCode.EventContinuationUnknown, result.Stop!.Value.Code);
        Assert.Equal(0, execution.State.Header.FutureGainedSkillPoints);
        Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
        var duplicate = new PioneerStart.CheckpointDisplay(display.Turn, display.Phase, current, confirmed with { Confirmed = true });
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(duplicate, [], display.SourceSha256).CreateInitialState());
    }

    static ActionScript<PioneerDecisionView, PioneerRuntimeAction> FailureScript(PioneerState root, TrainingFailureEvent failure)
        => new([new(new((int)PioneerStage.FailureTraining, 0, PioneerScenario.DecisionKey(root.Header.Turn, root.Header.CommandId)), PioneerRuntimeAction.Train(root.Header.CommandId)),
            new(new((int)PioneerStage.FailureChoice, 1, PioneerScenario.DecisionKey(root.Header.Turn, failure.EventId)),
                PioneerRuntimeAction.Choose(new(failure.EventId, failure.CharacterId, failure.StoryId, failure.PlayTiming), 1))], 0);

    static ActionScript<PioneerDecisionView, PioneerRuntimeAction> SingleEventScript(PioneerState root, PioneerEvent current, int choice)
        => new([new(new((int)root.Header.Stage, 0, PioneerScenario.DecisionKey(root.Header.Turn, current.EventId)), PioneerRuntimeAction.Choose(current, choice))], 0);

    static (PioneerRuntimeData Data, FailureExpected Expected) FailureCase(int index)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "training-failure-snapshots.json")))!;
        var window = fixture["windows"]![index]!;
        var before = window["before"]!;
        var after = window["choiceAfter"]!;
        var eventNode = window["failureEvent"]!;
        var failure = new TrainingFailureEvent(Int(eventNode, "eventId"), Int(eventNode, "characterId"), Int(eventNode, "storyId"), Int(eventNode, "playTiming"));
        var source = new TrainingFailureState(Int(before, "cardId"), Int(before, "turn"), Int(before, "motivation"), Values(before["resources"]!),
            Int(before, "staminaExecutionCount"),
            TrainingFailureStage.TrainingDecision, null);
        var data = PioneerRuntimeData.Compile(new PioneerStart.FailureTraining(source, Int(window, "commandId"),
            [.. before["supportBonds"]!.AsArray().Select(value => value!.GetValue<int>())],
            [.. before["characterEffects"]!.AsArray().Select(value => value!.GetValue<int>())]),
            [new PioneerConditionalOutcome.TrainingFailure(0, failure),
                new PioneerConditionalOutcome.FailurePenalty(1, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne)],
            fixture["sourceSha256"]![0]!.GetValue<string>());
        return (data, new(failure, Values(window["immediateAfter"]!["resources"]!), Values(after["resources"]!),
            Int(after, "turn"), source.StaminaExecutionCount));
    }

    static TrainingValues Values(JsonNode node) => new(Int(node, "speed"), Int(node, "stamina"), Int(node, "power"),
        Int(node, "guts"), Int(node, "wisdom"), Int(node, "vital"), Int(node, "skillPoints"));
    static int Int(JsonNode node, string key) => node[key]!.GetValue<int>();
    static IEnumerable<T> ReadEvidence<T>(string file, [CallerFilePath] string source = "")
        => File.ReadLines(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../src/UmaAiSharp.Pioneer/Development/Evidence", file)))
            .Select(line => JsonSerializer.Deserialize<T>(line, EvidenceJson.ReadOptions)!);
    sealed record FailureExpected(TrainingFailureEvent Event, TrainingValues Immediate, TrainingValues After, int AfterTurn, int BeforeCount);
}
