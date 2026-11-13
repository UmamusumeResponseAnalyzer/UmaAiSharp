using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
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
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
            var ordinary = row.GetProperty("kind").GetString() == "ordinary";
            var current = ordinary ? row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecution.JsonOptions) : default;
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
        }
    }

    [Fact]
    public void RestRolloutAndConditionalReplaySettleBeforeReplacingTheBoard()
    {
        var row = Cases().First(row => row.GetProperty("start").GetProperty("turn").GetInt32() == 5);
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        var rest = row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecution.JsonOptions);
        var calibration = new PioneerTurnCalibration([new(rest.StoryId % 1000, 1)],
            [new(PioneerArrivalSource.RestConfirmation, start.Turn, Ready: 0, TurnConfirmation: 0, TrainingLevel: 0,
                SameTurnEvent: 1, NextTurnEvent: 1, OtherPhase: 0)],
            [new(PlacementMemberKind.Training, false, 0, 1, 10), new(PlacementMemberKind.Tucker, false, 0, 1, 10)]);
        var rules = new PioneerTurnRules(calibration,
            start.Commands.Select(command => (command.CommandId, command.Level, command.FailureCoefficient)),
            start, 63, [], []);
        var generated = PioneerRuntimeData.Compile(start, [], "fitted-rest", turnRules: rules);
        var scenario = new PioneerScenario(generated, autonomous: true);
        var state = generated.CreateInitialState();
        var random = new SimulationRandom(71);
        Assert.True(scenario.ApplyAction(ref state, PioneerRuntimeAction.Rest(RestRules.OrdinaryCommand), ref random).ActionSettled);
        Assert.Equal(default, state.Header.Event);
        Assert.Equal(start.Turn + 1, state.Header.Turn);
        Assert.Equal(PioneerStage.TrainingTurn, state.Header.Stage);
        Assert.Equal(row.GetProperty("expectedVitals")[0].GetInt32(), state.Header.Resources.Vital);

        var prior = new PioneerStoryOccurrence(rest.StoryId, start.Turn - 1, 5, rest.PlayTiming, rest.CharacterId, 0, 0, 0);
        var recorded = PioneerRuntimeData.Compile(start with { ObservedStoryHistory = [prior] },
            [new PioneerConditionalOutcome.RestEvent(0, rest with { EventId = 7010 }), new PioneerConditionalOutcome.TurnArrival(1,
                PioneerArrival.Ready, state.Header.TrainingBoard, state.Header.AvailableTrainingCommands, state.Header.AvailableRestCommands)],
            "rest-replay", turnRules: rules, usesStoryDecisions: true);
        var replay = new PioneerScenario(recorded);
        var copy = recorded.CreateInitialState();
        var replayRandom = new SimulationRandom(71);
        Assert.True(replay.ApplyAction(ref copy, PioneerRuntimeAction.Rest(RestRules.OrdinaryCommand), ref replayRandom).ActionSettled);
        Assert.NotNull(replay.ValidateAction(copy, PioneerRuntimeAction.Choose(rest, 0)));
        Assert.True(replay.ApplyAction(ref copy, PioneerRuntimeAction.Choose(copy.Header.Event, 0), ref replayRandom).ActionSettled);
        Assert.Equal(state.Header.TrainingBoard, copy.Header.TrainingBoard);
        Assert.Equal(state.Header.Resources, copy.Header.Resources);
        var history = copy.GetSection(PioneerStateSection.StoryHistory);
        Assert.Equal([rest.StoryId, start.Turn - 1, 5, 6, rest.CharacterId, 0, 0, 0, 0, 0,
            rest.StoryId, start.Turn, 5, 6, rest.CharacterId, 0, 0, 0, 0, 0], history.ToArray());
        Assert.Equal(2, copy.Header.ConditionalOutcomeCount);
        Assert.Equal(71UL, replayRandom.State);

    }

    [Fact]
    public void OrdinaryEventEffectsSettleBeforeTheNextDecisionWithoutCreatingAnEventChoice()
    {
        var row = Cases().First(row => row.GetProperty("start").GetProperty("turn").GetInt32() == 5);
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        var rest = row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecution.JsonOptions);
        var calibration = new PioneerTurnCalibration([new(rest.StoryId % 1000, 1)],
            [new(PioneerArrivalSource.RestConfirmation, start.Turn, 0, 0, 0, 0, 0, 0,
                [new(1, new(10, 0, 0, 0, 0, 10, 5), -1, 0, 5)])],
            [new(PlacementMemberKind.Training, false, 0, 1, 10), new(PlacementMemberKind.Tucker, false, 0, 1, 10)]);
        var rules = new PioneerTurnRules(calibration,
            start.Commands.Select(command => (command.CommandId, command.Level, command.FailureCoefficient)), start, 1, [], []);
        var data = PioneerRuntimeData.Compile(start, [], "ordinary-event", turnRules: rules, usesStoryDecisions: true);
        var scenario = new PioneerScenario(data, autonomous: true);
        var state = data.CreateInitialState();
        var random = new SimulationRandom(71);
        Assert.True(scenario.ApplyAction(ref state, PioneerRuntimeAction.Rest(RestRules.OrdinaryCommand), ref random).ActionSettled);
        Assert.Equal(PioneerStage.TrainingTurn, state.Header.Stage);
        Assert.Equal(start.Turn + 1, state.Header.Turn);
        Assert.Equal(start.Environment.Current.Speed + 10, state.Header.Resources.Speed);
        Assert.Equal(start.Environment.Limits.Vital, state.Header.Resources.Vital);
        Assert.Equal(5, state.Header.FutureGainedSkillPoints);
        Assert.Equal(start.Environment.Motivation - 1, state.Header.Motivation);
        Assert.Equal(Math.Min(100, start.Deck.Single(card => card.Position == 1).Bond + 5),
            state.GetSection(PioneerStateSection.SupportBonds)[0]);
        Assert.Empty(state.GetSection(PioneerStateSection.PendingStories).ToArray());
        Assert.Empty(state.GetSection(PioneerStateSection.StoryHistory).ToArray());
    }

    [Fact]
    public void RevealedRestConfirmationRunsWithoutAConditionalOutcome()
    {
        foreach (var row in Cases().Where(row => row.TryGetProperty("eventStart", out _)))
        {
            var start = row.GetProperty("eventStart").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
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
    public void MissingOutcomesAndUnavailableCommandsDoNotMutateState()
    {
        var row = Cases()[0];
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        var current = row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecution.JsonOptions);
        foreach (var (input, outcomes, code) in new (PioneerStart.TrainingTurn, ImmutableArray<PioneerConditionalOutcome>, PioneerStopCode)[]
        {
            (start, [], PioneerStopCode.RestOutcomeRequired),
            (start with { AvailableRestCommands = null }, [], PioneerStopCode.RestAvailabilityRequired),
            (start with { AvailableRestCommands = 0 }, [], PioneerStopCode.InvalidAction),
            (start with { CharacterId = null }, [new PioneerConditionalOutcome.RestEvent(0, current)], PioneerStopCode.RestCharacterRequired),
            (start, [new PioneerConditionalOutcome.TrainingResult(0, true)], PioneerStopCode.OutcomeIdentityMismatch)
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
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
            var ordinary = start.Camp is null;
            start = start with { Environment = start.Environment with { Current = start.Environment.Current with { Vital = vital },
                Limits = start.Environment.Limits with { Vital = maximum }, Motivation = motivation } };
            var current = ordinary ? row.GetProperty("event").Deserialize<PioneerEvent>(PioneerExecution.JsonOptions) : default;
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

    static JsonElement[] Cases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/rest-action-windows.json")));
        return fixture.RootElement.GetProperty("cases").EnumerateArray().Select(row => row.Clone()).ToArray();
    }
}
