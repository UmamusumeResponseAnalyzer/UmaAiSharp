using System.Text.Json;
using UmaAiSharp.Cli;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingFlowTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void RealTrainingRootReachesFailureChoiceAndForksBeforeThePenalty(int index, bool positiveThinking)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-failure-snapshots.json")));
        var row = fixture.RootElement.GetProperty("windows")[index];
        var path = PioneerExecutionCommandTests.Example(index == 0 ? "camp-failure-replay.json" : "ordinary-failure-replay.json");
        var loaded = PioneerExecutionCommand.Load(path);
        var failure = row.GetProperty("failureEvent").Deserialize<TrainingFailureEvent>(PioneerExecutionCommand.JsonOptions)!;
        var current = new PioneerEvent(failure.EventId, failure.CharacterId, failure.StoryId, failure.PlayTiming);
        var command = row.GetProperty("commandId").GetInt32();
        var data = loaded.Data;
        var originalEffects = loaded.Root.GetSection(PioneerStateSection.CharacterEffects).ToArray();
        if (positiveThinking)
        {
            var input = JsonSerializer.Deserialize<PioneerExecutionInput>(File.ReadAllBytes(path), PioneerExecutionCommand.JsonOptions)!;
            var start = input.Start.Data.Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            data = PioneerRuntimeData.Compile(start with { CharacterEffects = [originalEffects[0], 25, .. originalEffects[1..]],
                SuccessfulOrdinaryTrainingCounts = [.. loaded.Root.GetSection(PioneerStateSection.TrainingExecutionCounts)] },
                [new PioneerConditionalOutcome.TrainingFailure(0, failure),
                    new PioneerConditionalOutcome.FailurePenalty(1, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne)], "protected-failure");
        }
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        var script = loaded.Script;
        Assert.Equal(PioneerStage.TrainingTurn, root.Header.Stage);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 83, 0);
        var initialRandom = execution.RulesRandom.State;
        var first = SimulationRunner.Advance(scenario, script, ref execution);
        Assert.Equal(TrajectoryStatus.Advanced, first.Status);
        Assert.Equal((1, 1), (first.Progress.ConsumedActions, first.Progress.SettledActions));
        Assert.Equal(PioneerStage.FailureChoice, execution.State.Header.Stage);
        Assert.Equal(current, scenario.Inspect(execution.State, 1).Decision.Context.Event);
        Assert.Equal(root.Header.Resources, execution.State.Header.Resources);
        Assert.False(execution.State.Header.TrainingSucceeded);
        Assert.Equal(root.GetSection(PioneerStateSection.TrainingExecutionCounts)[1] + 1, execution.State.Header.StaminaExecutionCount);
        Assert.Equal(execution.State.Header.StaminaExecutionCount, execution.State.GetSection(PioneerStateSection.TrainingExecutionCounts)[1]);
        var fork = SimulationRunner.CreateExecution(scenario, script, root, 999, 1);
        SimulationRunner.ForkInto(scenario, script, execution, ref fork);
        var finished = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(finished, SimulationRunner.Continue(scenario, script, ref fork));
        Assert.Equal((int)PioneerStopCode.NextTurnEventsUnknown, finished.Stop!.Value.Code);
        Assert.Equal((2, 1), (finished.Progress.ConsumedActions, finished.Progress.SettledActions));
        Assert.Equal(initialRandom, finished.RandomState.Rules);
        var expected = row.GetProperty("choiceAfter");
        Assert.Equal(expected.GetProperty("turn").GetInt32(), execution.State.Header.Turn);
        Assert.Equal(positiveThinking ? root.Header.Motivation : expected.GetProperty("motivation").GetInt32(), execution.State.Header.Motivation);
        Assert.Equal(originalEffects, execution.State.GetSection(PioneerStateSection.CharacterEffects).ToArray());
        Assert.Equal(originalEffects, fork.State.GetSection(PioneerStateSection.CharacterEffects).ToArray());
        Assert.Equal(root.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray(),
            execution.State.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray());
        Assert.Equal(positiveThinking, root.GetSection(PioneerStateSection.CharacterEffects).Contains(25));
        Assert.Equal(expected.GetProperty("resources").Deserialize<TrainingValues>(PioneerExecutionCommand.JsonOptions), execution.State.Header.Resources);
        Assert.Equal(finished, SimulationRunner.Continue(scenario, script, ref execution));
        Assert.Equal(row.GetProperty("before").GetProperty("resources").Deserialize<TrainingValues>(PioneerExecutionCommand.JsonOptions), root.Header.Resources);
        Assert.Equal(row.GetProperty("before").GetProperty("turn").GetInt32(), root.Header.Turn);
        Assert.Equal(root.GetSection(PioneerStateSection.TrainingExecutionCounts)[1] + 1, execution.State.GetSection(PioneerStateSection.TrainingExecutionCounts)[1]);
        PioneerStateAudit.AssertDeterministic(data, script);

        var invalid = root.Clone();
        var random = new SimulationRandom(83);
        var invalidResult = scenario.ApplyAction(ref invalid, PioneerRuntimeAction.Train(command == 105 ? 101 : 3601), ref random);
        Assert.False(invalidResult.ActionConsumed);
        Assert.Equal((int)PioneerStopCode.UnsupportedComponentInput, invalidResult.Stop!.Value.Code);
        Assert.Equal(root.Header, invalid.Header);
        Assert.Equal(root.GetSection(PioneerStateSection.TrainingExecutionCounts).ToArray(), invalid.GetSection(PioneerStateSection.TrainingExecutionCounts).ToArray());
        Assert.Equal(83UL, random.State);
    }
}
