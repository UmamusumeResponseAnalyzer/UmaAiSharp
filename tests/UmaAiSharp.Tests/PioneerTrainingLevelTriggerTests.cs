using System.Text.Json;
using UmaAiSharp.Cli;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingLevelTriggerTests
{
    [Fact]
    public void CapturedActionsKeepTriggeredUpgradesSeparateFromVisibleEvents()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-level-triggers.json")));
        var cases = fixture.RootElement.GetProperty("cases");
        Assert.Equal(11, cases.GetArrayLength());
        var autonomousTriggers = 0;
        var commands = new[] { 101, 105, 102, 103, 106 };
        foreach (var row in cases.EnumerateArray())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            var command = row.GetProperty("command").GetInt32();
            var success = row.GetProperty("success").GetBoolean();
            var expected = row.GetProperty("expectedPendingEvent").GetInt32();
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, success)], "captured-upgrade");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Train(command))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 81, 0);
            var initialRandom = new RandomStreamState(execution.RulesRandom.State, execution.PolicyRandom.State);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(TrajectoryStatus.Stopped, result.Status);
            Assert.Equal((int)(success ? PioneerStopCode.TrainingEventsRequired : PioneerStopCode.TrainingFailureEventRequired), result.Stop!.Value.Code);
            Assert.Equal((1, 0), (result.Progress.ConsumedActions, result.Progress.SettledActions));
            Assert.Equal(expected, execution.State.Header.PendingTrainingLevelEventId);
            Assert.Equal(0, root.Header.PendingTrainingLevelEventId);
            Assert.Equal(default, execution.State.Header.Event);
            Assert.False(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.EventIdentity));
            Assert.True(execution.State.GetSection(PioneerStateSection.PendingStories).IsEmpty);
            Assert.Equal(initialRandom, result.RandomState);
            var index = Array.IndexOf(commands, TrainingBaseTable.OrdinaryCommand(command));
            Assert.Equal(row.GetProperty("afterCount").GetInt32(), execution.State.GetSection(PioneerStateSection.SuccessfulOrdinaryTrainingCounts)[index]);
            Assert.Equal(row.GetProperty("beforeLevels").Deserialize<int[]>(), execution.State.GetSection(PioneerStateSection.TrainingLevels).ToArray());
            if (expected != 0)
            {
                Assert.Contains(expected, row.GetProperty("postActionEventIds").Deserialize<int[]>()!);
                Assert.NotNull(scenario.ValidateAction(execution.State, PioneerRuntimeAction.Choose(new(expected, 0, 400000005, 6), 0)));
            }

            var fork = SimulationRunner.CreateExecution(scenario, script, root, 91, 2);
            SimulationRunner.ForkInto(scenario, script, execution, ref fork);
            Assert.Equal(execution.State.Header, fork.State.Header);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref fork));
            SimulationRunner.ResetSample(scenario, script, root, ref execution, 81, 0);
            Assert.Equal(0, execution.State.Header.PendingTrainingLevelEventId);
            Assert.Equal(expected, fork.State.Header.PendingTrainingLevelEventId);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            PioneerStateAudit.AssertDeterministic(data, script);

            var unknown = PioneerRuntimeData.Compile(start with { SuccessfulOrdinaryTrainingCounts = null },
                [new PioneerConditionalOutcome.TrainingResult(0, success)], "unknown-upgrade-counts");
            var unknownScenario = new PioneerScenario(unknown); var unknownRoot = unknown.CreateInitialState();
            var unknownExecution = SimulationRunner.CreateExecution(unknownScenario, script, unknownRoot, 81, 0);
            SimulationRunner.Continue(unknownScenario, script, ref unknownExecution);
            if (success && command < 3601 && row.GetProperty("beforeLevels")[index].GetInt32() < 5)
                Assert.Null(unknownExecution.State.Header.PendingTrainingLevelEventId);
            else Assert.Equal(0, unknownExecution.State.Header.PendingTrainingLevelEventId);

            if (expected != 0 && scenario.PreviewTraining(root, command).FailureRate == 0)
            {
                var autonomous = PioneerRuntimeData.Compile(start, [], "autonomous-upgrade");
                var autonomousScenario = new PioneerScenario(autonomous); var autonomousRoot = autonomous.CreateInitialState();
                var future = SimulationRunner.CreateExecution(autonomousScenario, script, autonomousRoot, 81, 0);
                var predicted = SimulationRunner.Continue(autonomousScenario, script, ref future);
                Assert.Equal(expected, future.State.Header.PendingTrainingLevelEventId);
                Assert.Equal(initialRandom, predicted.RandomState);
                Assert.Equal(0, future.State.Header.ConditionalOutcomeCount);
                autonomousTriggers++;
            }
        }
        Assert.True(autonomousTriggers > 0);
    }

    [Fact]
    public void TriggerRulePreservesUnknownCountsAndDoesNotQueuePastTheLevelCap()
    {
        var events = new[] { 211024, 211027, 211025, 211026, 211028 };
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(index, TrainingLevelRules.TargetIndex(events[index]));
            foreach (var level in new[] { 1, 2, 3, 4 })
            {
                Assert.Null(TrainingLevelRules.AfterSuccessfulOrdinary(index, level, null));
                foreach (var count in new[] { 1, 3, 5, 7, 9, 11, 13, 15, 17 })
                    Assert.Equal(0, TrainingLevelRules.AfterSuccessfulOrdinary(index, level, count));
                foreach (var count in new[] { 4, 8, 12, 16 })
                    Assert.Equal(events[index], TrainingLevelRules.AfterSuccessfulOrdinary(index, level, count));
            }
            Assert.Equal(0, TrainingLevelRules.AfterSuccessfulOrdinary(index, 5, null));
            Assert.Equal(0, TrainingLevelRules.AfterSuccessfulOrdinary(index, 5, 12));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingLevelRules.AfterSuccessfulOrdinary(5, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingLevelRules.AfterSuccessfulOrdinary(0, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingLevelRules.AfterSuccessfulOrdinary(0, 1, 0));
    }
}
