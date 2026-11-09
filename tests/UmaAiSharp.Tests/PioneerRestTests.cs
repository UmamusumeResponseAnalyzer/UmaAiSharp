using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.Cli;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerRestTests
{
    [Fact]
    public void CapturedRestActionsUseRealDecisionOrderAndPreserveIndependentHistory()
    {
        foreach (var row in Cases())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            var ordinary = row.GetProperty("kind").GetString() == "ordinary";
            var current = ordinary ? row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecutionCommand.JsonOptions) : default;
            var data = PioneerRuntimeData.Compile(start, ordinary ? [new PioneerConditionalOutcome.RestEvent(0, current)] : [], "captured-rest");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var command = ordinary ? RestRules.OrdinaryCommand : RestRules.CampCommand;
            var first = scenario.Inspect(root, 0).Decision;
            Assert.Equal(start.AvailableRestCommands, first.Context.AvailableRestCommands);
            ImmutableArray<ScriptedAction<PioneerRuntimeAction>> steps = [new(first.Id, PioneerRuntimeAction.Rest(command))];
            if (ordinary) steps = [.. steps, new(new((int)PioneerStage.RestConfirmation, 1,
                PioneerScenario.DecisionKey(start.Turn, current.EventId)), PioneerRuntimeAction.Choose(current, 0))];
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(steps, 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
            var fork = SimulationRunner.CreateExecution(scenario, script, root, 999, 1);
            var random = new RandomStreamState(execution.RulesRandom.State, execution.PolicyRandom.State);
            if (ordinary)
            {
                var selected = SimulationRunner.Advance(scenario, script, ref execution);
                Assert.Equal(TrajectoryStatus.Advanced, selected.Status);
                Assert.Equal(start.Environment.Current, execution.State.Header.Resources);
                Assert.Equal(start.Turn, execution.State.Header.Turn);
                Assert.Equal(PioneerStage.RestConfirmation, execution.State.Header.Stage);
                Assert.Equal(current, execution.State.Header.Event);
                Assert.Equal([current.StoryId], execution.State.GetSection(PioneerStateSection.PendingStories).ToArray());
            }
            SimulationRunner.ForkInto(scenario, script, execution, ref fork);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref fork));
            Assert.Equal(fork.State.Header, execution.State.Header);
            Assert.Equal((ordinary ? 2 : 1, ordinary ? 1 : 0), (result.Progress.ConsumedActions, result.Progress.SettledActions));
            Assert.Equal((int)PioneerStopCode.RestContinuationRequired, result.Stop!.Value.Code);
            Assert.Equal(random, result.RandomState);
            var expected = row.GetProperty("expectedVitals").Deserialize<int[]>()!;
            Assert.Equal(expected[0], execution.State.Header.Resources.Vital);
            Assert.Equal(expected[1], execution.State.Header.Limits.Vital);
            Assert.Equal(expected[2], execution.State.Header.Motivation);
            Assert.Equal(start.Environment.Current, execution.State.Header.Resources with { Vital = start.Environment.Current.Vital });
            Assert.Equal(root.Header.DevelopmentPoints, execution.State.Header.DevelopmentPoints);
            Assert.Equal(start.Environment.Current, root.Header.Resources);
            Assert.Equal(start.Environment.Motivation, root.Header.Motivation);
            Assert.Equal(start.Turn, execution.State.Header.Turn);
            Assert.False(execution.State.Header.TurnKnown);
            Assert.False(execution.State.Header.RawPlayingStateKnown);
            Assert.False(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.EventIdentity));
            foreach (var section in Enum.GetValues<PioneerStateSection>())
                Assert.Equal(root.GetSection(section).ToArray(), execution.State.GetSection(section).ToArray());
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            SimulationRunner.ResetSample(scenario, script, root, ref execution, 71, 0);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            PioneerStateAudit.AssertDeterministic(data, script);
        }
    }

    [Fact]
    public void RevealedRestConfirmationRunsWithoutAConditionalOutcome()
    {
        foreach (var row in Cases().Where(row => row.TryGetProperty("eventStart", out _)))
        {
            var start = row.GetProperty("eventStart").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            var data = PioneerRuntimeData.Compile(start, [], "revealed-rest");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var current = start.PendingRestEvent!.Value;
            var decision = scenario.Inspect(root, 0).Decision;
            Assert.Equal(PioneerStage.RestConfirmation, decision.Context.Stage);
            Assert.Equal(new((int)PioneerStage.RestConfirmation, 0, PioneerScenario.DecisionKey(start.Turn, current.EventId)), decision.Id);
            var random = new SimulationRandom(71);
            var result = scenario.ApplyAction(ref root, PioneerRuntimeAction.Choose(current, 0), ref random);
            Assert.True(result.ActionConsumed);
            Assert.False(result.ActionSettled);
            Assert.Equal(row.GetProperty("expectedVitals")[0].GetInt32(), root.Header.Resources.Vital);
            Assert.Equal(71UL, random.State);
            Assert.NotNull(scenario.ValidateAction(root, PioneerRuntimeAction.Choose(current, 0)));
        }
    }

    [Fact]
    public void MissingOutcomesUnknownEffectsAndUnavailableCommandsDoNotMutateState()
    {
        var row = Cases()[0];
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
        var current = row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecutionCommand.JsonOptions);
        foreach (var (input, outcomes, code) in new (PioneerStart.TrainingTurn, ImmutableArray<PioneerConditionalOutcome>, PioneerStopCode)[]
        {
            (start, [], PioneerStopCode.RestOutcomeRequired),
            (start with { AvailableRestCommands = null }, [], PioneerStopCode.RestAvailabilityRequired),
            (start with { AvailableRestCommands = 0 }, [], PioneerStopCode.InvalidAction),
            (start with { CharacterId = null }, [new PioneerConditionalOutcome.RestEvent(0, current)], PioneerStopCode.RestCharacterRequired),
            (start, [new PioneerConditionalOutcome.TrainingResult(0, true)], PioneerStopCode.OutcomeIdentityMismatch),
            (start with { CharacterEffects = [.. start.CharacterEffects, 1] },
                [new PioneerConditionalOutcome.RestEvent(0, current)], PioneerStopCode.UnsupportedComponentInput)
        })
        {
            var data = PioneerRuntimeData.Compile(input, outcomes, "rest-boundary");
            var scenario = new PioneerScenario(data); var state = data.CreateInitialState(); var before = state.Header;
            var random = new SimulationRandom(71);
            var result = scenario.ApplyAction(ref state, PioneerRuntimeAction.Rest(RestRules.OrdinaryCommand), ref random);
            Assert.False(result.ActionConsumed);
            Assert.Equal((int)code, result.Stop!.Value.Code);
            Assert.Equal(before, state.Header);
            Assert.Equal(71UL, random.State);
            Assert.Empty(state.GetSection(PioneerStateSection.PendingStories).ToArray());
        }
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { AvailableRestCommands = 3 }, [], "invalid-mode"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        {
            RawPlayingState = 5, PendingRestEvent = current with { CharacterId = 1 }, PendingStoryIds = [current.StoryId]
        }, [], "invalid-event"));
    }

    [Fact]
    public void RestClampsVitalAndMotivationWithoutAdvancingUnknownScheduling()
    {
        foreach (var row in new[] { Cases()[0], Cases().First(row => row.GetProperty("kind").GetString() == "camp") })
        foreach (var maximum in new[] { 100, 120 })
        foreach (var vital in new[] { 0, maximum - 1, maximum })
        foreach (var motivation in new[] { 1, 4, 5 })
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            var ordinary = start.Camp is null;
            start = start with { Environment = start.Environment with { Current = start.Environment.Current with { Vital = vital },
                Limits = start.Environment.Limits with { Vital = maximum }, Motivation = motivation } };
            var current = ordinary ? row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecutionCommand.JsonOptions) : default;
            var data = PioneerRuntimeData.Compile(start, ordinary ? [new PioneerConditionalOutcome.RestEvent(0, current)] : [], "rest-limits");
            var scenario = new PioneerScenario(data); var state = data.CreateInitialState(); var random = new SimulationRandom(71);
            scenario.ApplyAction(ref state, PioneerRuntimeAction.Rest(ordinary ? 701 : 3606), ref random);
            if (ordinary) scenario.ApplyAction(ref state, PioneerRuntimeAction.Choose(current, 0), ref random);
            Assert.Equal(Math.Min(maximum, vital + (ordinary ? RestRules.Recovery(current, start.CharacterId!.Value) : 40)), state.Header.Resources.Vital);
            Assert.Equal(ordinary ? motivation : Math.Min(5, motivation + 1), state.Header.Motivation);
            Assert.Equal(71UL, random.State);
            Assert.False(state.Header.TurnKnown);
        }
    }

    [Fact]
    public void RestCliUsesTheSameScriptsForSingleAndBatchExecution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var row in new[] { Cases()[0], Cases().First(row => row.GetProperty("kind").GetString() == "camp") })
            {
                var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
                var ordinary = start.Camp is null; var mode = ordinary ? PioneerExecutionMode.Conditional : PioneerExecutionMode.Autonomous;
                var current = ordinary ? row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecutionCommand.JsonOptions) : default;
                ImmutableArray<ScriptedAction<PioneerRuntimeAction>> steps = [new(new((int)PioneerStage.TrainingTurn, 0,
                    PioneerScenario.DecisionKey(start.Turn, 0)), PioneerRuntimeAction.Rest(ordinary ? 701 : 3606))];
                if (ordinary) steps = [.. steps, new(new((int)PioneerStage.RestConfirmation, 1,
                    PioneerScenario.DecisionKey(start.Turn, current.EventId)), PioneerRuntimeAction.Choose(current, 0))];
                var input = new PioneerExecutionInput(1, "rest-cli", mode, new("trainingTurn", row.GetProperty("start")),
                    ordinary ? [new("restEvent", JsonSerializer.SerializeToElement(new { actionOrdinal = 0, @event = current }, PioneerExecutionCommand.JsonOptions))] : [], steps);
                var path = Path.Combine(directory, ordinary ? "ordinary.json" : "camp.json");
                File.WriteAllText(path, JsonSerializer.Serialize(input, PioneerExecutionCommand.JsonOptions));
                var result = path + ".result.json";
                Assert.Equal(3, Program.Main(["pioneer", ordinary ? "replay" : "run", "--input", path, "--seed", "71", "--output", result]));
                using var single = JsonDocument.Parse(File.ReadAllText(result));
                Assert.Equal(row.GetProperty("expectedVitals")[0].GetInt32(), single.RootElement.GetProperty("finalState")
                    .GetProperty("header").GetProperty("resources").GetProperty("vital").GetInt32());
                var batch = path + ".batch.json";
                Assert.Equal(3, Program.Main(["pioneer", "batch", "--input", path, "--seed", "71", "--samples", "131", "--workers", "4", "--output", batch]));
                using var batchDocument = JsonDocument.Parse(File.ReadAllText(batch));
                Assert.Equal(131, batchDocument.RootElement.GetProperty("results").GetArrayLength());
                Assert.All(batchDocument.RootElement.GetProperty("results").EnumerateArray(), result =>
                    Assert.Equal((int)PioneerStopCode.RestContinuationRequired, result.GetProperty("stop").GetProperty("code").GetInt32()));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    static JsonElement[] Cases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/rest-action-windows.json")));
        return fixture.RootElement.GetProperty("cases").EnumerateArray().Select(row => row.Clone()).ToArray();
    }
}
