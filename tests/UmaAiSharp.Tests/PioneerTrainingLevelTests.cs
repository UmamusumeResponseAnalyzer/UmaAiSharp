using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingLevelTests
{
    [Fact]
    public void CapturedLevelConfirmationsPreserveResourcesAndStopBeforeScheduling()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-level-events.json")));
        var cases = fixture.RootElement.GetProperty("cases");
        Assert.Equal(7, cases.GetArrayLength());
        Assert.Equal(5, cases.EnumerateArray().Select(row => row.GetProperty("start").GetProperty("pendingTrainingLevelEvent").GetProperty("eventId").GetInt32()).Distinct().Count());
        foreach (var row in cases.EnumerateArray())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
            var data = PioneerRuntimeData.Compile(start, [], "captured-level");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var current = start.PendingTrainingLevelEvent!.Value;
            Assert.Equal(TrainingLevelRules.ObservedCommand(current), root.Header.PendingTrainingLevelCommandId);
            var decision = scenario.Inspect(root, 0).Decision;
            Assert.Equal(PioneerStage.TrainingLevelConfirmation, decision.Context.Stage);
            Assert.Equal(new((int)PioneerStage.TrainingLevelConfirmation, 0, PioneerScenario.DecisionKey(start.Turn, current.EventId)), decision.Id);
            var action = PioneerRuntimeAction.Choose(current, 0);
            Assert.Null(scenario.ValidateAction(root, action));
            Assert.NotNull(scenario.ValidateAction(root, PioneerRuntimeAction.Train(101)));
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>([new(decision.Id, action)], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
            var fork = SimulationRunner.CreateExecution(scenario, script, root, 999, 1);
            SimulationRunner.ForkInto(scenario, script, execution, ref fork);
            var random = new RandomStreamState(execution.RulesRandom.State, execution.PolicyRandom.State);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref fork));
            Assert.Equal(TrajectoryStatus.Stopped, result.Status);
            Assert.Equal((int)PioneerStopCode.TrainingLevelContinuationRequired, result.Stop!.Value.Code);
            Assert.Equal((1, 0), (result.Progress.ConsumedActions, result.Progress.SettledActions));
            Assert.Equal(random, result.RandomState);
            Assert.Equal(row.GetProperty("afterLevels").Deserialize<int[]>(), execution.State.GetSection(PioneerStateSection.TrainingLevels).ToArray());
            Assert.Equal(row.GetProperty("beforeLevels").Deserialize<int[]>(), root.GetSection(PioneerStateSection.TrainingLevels).ToArray());
            Assert.Equal(start.Environment.Current, execution.State.Header.Resources);
            Assert.Equal(start.Environment.Limits, execution.State.Header.Limits);
            Assert.Equal(start.Environment.Motivation, execution.State.Header.Motivation);
            Assert.Equal(root.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray(),
                execution.State.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts).ToArray());
            Assert.Equal(root.GetSection(PioneerStateSection.TrainingExecutionCounts).ToArray(),
                execution.State.GetSection(PioneerStateSection.TrainingExecutionCounts).ToArray());
            Assert.False(execution.State.Header.TurnKnown);
            Assert.False(execution.State.Header.RawPlayingStateKnown);
            Assert.False(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.EventIdentity));
            Assert.Equal(0, execution.State.Header.PendingTrainingLevelCommandId);
            Assert.True(execution.State.GetSection(PioneerStateSection.PendingStories).IsEmpty);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(fork.State.Header, execution.State.Header);
            SimulationRunner.ResetSample(scenario, script, root, ref execution, 71, 0);
            Assert.Equal(current, execution.State.Header.Event);
            Assert.Equal(TrainingLevelRules.ObservedCommand(current), execution.State.Header.PendingTrainingLevelCommandId);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));

            var observed = PioneerRuntimeData.Compile(start with { SuccessfulOrdinaryTrainingCounts = null }, [], "observed-level");
            var observedRoot = observed.CreateInitialState();
            var unknownHistory = SimulationRunner.CreateExecution(new PioneerScenario(observed), script, observedRoot, 71, 0);
            SimulationRunner.Continue(new PioneerScenario(observed), script, ref unknownHistory);
            Assert.Equal(execution.State.GetSection(PioneerStateSection.TrainingLevels).ToArray(),
                unknownHistory.State.GetSection(PioneerStateSection.TrainingLevels).ToArray());
            Assert.False(unknownHistory.State.Header.KnownParts.HasFlag(PioneerKnownParts.SuccessfulOrdinaryTrainingCounts));
        }
    }

}
