using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer;

namespace UmaAiSharp.Tests;

public sealed class PioneerForecastTests
{
    static readonly ForecastAction[] Actions = [.. new[] { 101, 105, 102, 103, 106 }.Select(id => new ForecastAction(ForecastActionKind.Training, id))];
    static readonly CompiledGoal Goal = new GoalDefinition([], [new([new(new AttributeUtility(TrainingAttribute.Power, new([new(0, 1)])), 1)])]).Compiled;

    [Fact]
    public void TrainingUsesCurrentResourcesBondsCapsAndExactSettlementInsteadOfDonorGains()
    {
        var (start, input) = Fixture(15);
        var current = start with { Environment = start.Environment with { Current = start.Environment.Current with { Speed = 1205, Power = 1799 } },
            Deck = start.Deck.SetItem(0, start.Deck[0] with { Bond = 79 }) };
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", current, Actions);
        foreach (var action in Actions)
        {
            var before = Values(current);
            var state = new PioneerForecastState(new(15, ForecastActionKind.Home, 0), before, [0]);
            var random = new SimulationRandom(29);
            var result = scenario.ApplyAction(ref state, action, ref random);
            Assert.Null(result.Stop);
            Assert.Equal(Oracle(current, action.Id), state.Fields.ToArray());
            Assert.Equal(before, Values(current));
        }
    }

    [Fact]
    public void CompleteSearchForksWorkersCandidateOrderAndPrefixesPreserveValuesAndBothRandomStreams()
    {
        var (start, input) = Fixture(77);
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", start, Actions);
        var root = new PioneerForecastState(new(77, ForecastActionKind.Home, 0), Values(start), [0]);
        var original = root.Fields.ToArray();
        var candidates = Actions.Select(action => new RootCandidate<ForecastAction>(action.Id, action)).ToArray();
        var baseline = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 89, 2049, 1, ownedActionPayloadBytes: 0);
        Assert.Equal(SearchStatus.Completed, baseline.Status);
        Assert.True(baseline.Candidates[0].Batch.Results.Select(sample => sample.Outcome).Distinct().Count() > 1);
        foreach (var workers in new[] { 4, 16, 32 })
        {
            var result = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 89, 2049, workers, ownedActionPayloadBytes: 0);
            Assert.Equal(baseline.Ranking, result.Ranking);
            foreach (var candidate in result.Candidates)
                Assert.Equal(baseline.Candidates.Single(row => row.Candidate.Id == candidate.Candidate.Id).Batch.Results, candidate.Batch.Results);
        }
        var prefix = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates.Reverse().ToArray(), Goal, 89, 65, 4, ownedActionPayloadBytes: 0);
        foreach (var candidate in prefix.Candidates)
            Assert.Equal(baseline.Candidates.Single(row => row.Candidate.Id == candidate.Candidate.Id).Batch.Results.Take(65), candidate.Batch.Results);
        Assert.Equal(original, root.Fields.ToArray());

        var policy = scenario.CreatePolicy();
        var execution = SimulationRunner.CreateExecution(scenario, policy, root, 89, 0);
        SimulationRunner.Advance(scenario, policy, ref execution);
        var copy = SimulationRunner.CreateExecution(scenario, policy, root, 2, 1);
        SimulationRunner.ForkInto(scenario, policy, execution, ref copy);
        Assert.Equal(SimulationRunner.Continue(scenario, policy, ref execution), SimulationRunner.Continue(scenario, policy, ref copy));
        Assert.Equal(execution.State.Fields.ToArray(), copy.State.Fields.ToArray());
        SimulationRunner.ResetSample(scenario, policy, root, ref copy, 89, 0);
        Assert.Equal(original, copy.State.Fields.ToArray());
        Assert.Equal(InspectionKind.Decision, scenario.Inspect(copy.State, 0).Kind);
    }

    [Fact]
    public void ExcludingInputRunPreventsItsFutureFromSupplyingTransitionsAndCancellationRetainsSlots()
    {
        var (start, input) = Fixture(77);
        input = input with { Transitions = [.. input.Transitions.Select(row => row with { Run = "excluded" })] };
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", start, Actions);
        var root = new PioneerForecastState(new(77, ForecastActionKind.Home, 0), Values(start), [0]);
        var candidates = Actions.Select(action => new RootCandidate<ForecastAction>(action.Id, action)).ToArray();
        var result = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 29, 65, 4, ownedActionPayloadBytes: 0);
        Assert.Equal(SearchStatus.Stopped, result.Status);
        Assert.Null(result.RecommendedCandidateId);
        Assert.All(result.Candidates, row => Assert.All(row.Batch.Results, sample =>
        {
            Assert.Equal(2002, sample.Stop!.Value.Code);
            Assert.Equal(0, sample.Progress.ConsumedActions);
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        result = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 29, 65, 4, cancellation.Token, ownedActionPayloadBytes: 0);
        Assert.Equal(SearchStatus.Cancelled, result.Status);
        Assert.All(result.Candidates, row =>
        {
            Assert.Equal(65, row.Batch.Results.Length);
            Assert.All(row.Batch.Results, sample => Assert.Equal(SimulationStopReason.Cancelled, sample.Stop!.Value.Reason));
        });
    }

    [Fact]
    public void MissingFailureBranchStopsBeforeCommitAndDoesNotTreatUnobservedFailureAsZeroProbability()
    {
        var (start, input) = Fixture(77);
        start = start with { Environment = start.Environment with { Current = start.Environment.Current with { Vital = 0 } } };
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", start, Actions);
        var stopped = 0;
        var oracle = PioneerRuntimeData.Compile(start, [], "failure-preview");
        var rate = new PioneerScenario(oracle).PreviewTraining(oracle.CreateInitialState(), 101).FailureRate;
        for (var seed = 0UL; seed < 64; seed++)
        {
            var values = Values(start);
            var state = new PioneerForecastState(new(77, ForecastActionKind.Home, 0), values, [0]);
            var random = new SimulationRandom(seed);
            var transition = scenario.ApplyAction(ref state, Actions[0], ref random);
            var draw = new SimulationRandom(seed);
            Assert.Equal(draw.NextInt(100) < rate, transition.Stop is { Code: 2005 });
            if (transition.Stop is not { Code: 2005 }) continue;
            stopped++;
            Assert.False(transition.ActionConsumed);
            Assert.Equal(values, state.Fields.ToArray());
            Assert.Equal(seed, random.State);
        }
        Assert.True(stopped > 0);
    }

    [Fact]
    public void OrderedEventChangesPreserveTheCapAndUnknownOverflowAndRejectInvalidActions()
    {
        var (start, input) = Fixture(77);
        input = input with { Transitions = [.. input.Transitions.Where(row => row.From.Kind == ForecastActionKind.Event).Take(1)
            .Select(row => row with { Changes = [new(0, 900, false), new(5, 5, false), new(10, 70, false)] })] };
        var action = new ForecastAction(ForecastActionKind.Event, 6);
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", start, [action]);
        var state = new PioneerForecastState(new(78, ForecastActionKind.Event, 123), Values(start), [0]);
        var random = new SimulationRandom(3);
        Assert.NotNull(scenario.ValidateAction(state, new(ForecastActionKind.Training, 101)));
        Assert.Null(scenario.ApplyAction(ref state, action, ref random).Stop);
        var result = scenario.GetOutcome(state);
        Assert.Equal(1805, result.Attributes.Speed);
        Assert.Equal(100, state.Fields[10]);
        Assert.Null(result.FutureOverflow);
        Assert.NotNull(scenario.ValidateAction(state, action));
    }

    [Fact]
    public void ModelRejectsMissingCoreFactsAndPrematureTerminal()
    {
        var (_, input) = Fixture(77);
        var row = input.Transitions[0];
        Assert.Throws<InvalidDataException>(() => new PioneerForecastModel(input with
        { Transitions = input.Transitions.SetItem(0, row with { Before = row.Before with { Values = row.Before.Values.SetItem(10, PioneerForecastModel.Unknown) } }) }, "fixture"));
        Assert.Throws<InvalidDataException>(() => new PioneerForecastModel(input with
        { Transitions = input.Transitions.SetItem(0, row with { To = null }) }, "fixture"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 9)]
    public void EventHistoryConditionsSelectionAndExcludedRunCannotSupplyOutcomes(int visited, int expectedGain)
    {
        var (start, input) = Fixture(77);
        var row = input.Transitions[^1];
        input = input with { Transitions = [
            row with { Before = row.Before with { History = [] }, Changes = [new(2, 1, false)] },
            row with { Before = row.Before with { History = [0] }, Changes = [new(2, 9, false)] },
            row with { Run = "excluded", Before = row.Before with { Values = row.Before.Values.SetItem(14, int.MaxValue) }, Changes = [new(2, 100, false)] }
        ] };
        var action = new ForecastAction(ForecastActionKind.Event, 6);
        var root = new PioneerForecastState(new(78, ForecastActionKind.Event, 123), Values(start), [visited]);
        var scenario = new PioneerForecastScenario(new(input, "fixture"), "deck", "excluded", start, [action]);
        var random = new SimulationRandom(5);
        Assert.Null(scenario.ApplyAction(ref root, action, ref random).Stop);
        Assert.Equal(start.Environment.Current.Power + expectedGain, scenario.GetOutcome(root).Attributes.Power);
    }

    [Fact]
    public void RevealedChoicesContinueThroughCurrentDeckAndCalibratedFutureEventsWithoutAnIdenticalConfiguration()
    {
        var (start, input) = Fixture(77);
        var before = Values(start);
        var eventAction = new ForecastAction(ForecastActionKind.Event, 10000, 1);
        var terminalRows = input.Transitions.Select((row, index) => (row, index)).Where(item => item.row.From.Kind == ForecastActionKind.Event).ToArray();
        input = input with
        {
            Stories = [123, 456],
            Transitions = input.Transitions.Add(new("other", "deck", new(76, ForecastActionKind.Event, 456), new(77, ForecastActionKind.Home, 0),
                eventAction, new([.. before], []), [], 456, -1, true, "fixture", 300)),
            EventRewards = [.. terminalRows.Select(item => new ForecastEventReward(item.index, 1,
                new("donor", 123, new(1, 101, 6, 6, 3, 0, 0), [new(1, 3, 10 + item.index, 0)], [])))],
        };
        var choices = new[] { eventAction, eventAction with { Choice = 2 } };
        var declared = new PioneerStart.DeclaredEvent(76, 5, new(10000, 1001, 456, 6), start.Environment.Current,
            start.Environment.Limits, start.Environment.Motivation, [],
            [new(1, [new([new(1, 3, 10, 0)])]), new(2, [new([new(1, 3, 100, 0)])])]);
        var target = start with { CardId = 199901, CharacterId = 1999, Turn = 76,
            Deck = start.Deck.SetItem(0, start.Deck[0] with { CardId = 39999, Effects = [new(1, 40), new(26, 100)] }),
            Environment = start.Environment with { GrowthPercent = new(0, 0, 100, 0, 0, 0, 0) } };
        var scenario = new PioneerForecastScenario(new(input, "calibrated"), "unseen-character-and-deck", "input", target, choices, declared);
        Assert.Equal("deck", scenario.EmpiricalConfiguration);
        var root = new PioneerForecastState(new(76, ForecastActionKind.Event, 456), before, [0, 0]);
        var candidates = choices.Select((action, index) => new RootCandidate<ForecastAction>(index, action)).ToArray();
        var baseline = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 47, 2049, 1, ownedActionPayloadBytes: 0);
        Assert.Equal(SearchStatus.Completed, baseline.Status);
        Assert.Equal(1, baseline.RecommendedCandidateId);
        foreach (var workers in new[] { 4, 16, 32 })
        {
            var result = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root, candidates, Goal, 47, 2049, workers, ownedActionPayloadBytes: 0);
            Assert.Equal(baseline.Ranking, result.Ranking);
            for (var i = 0; i < candidates.Length; i++) Assert.Equal(baseline.Candidates[i].Batch.Results, result.Candidates[i].Batch.Results);
        }
        var unmodified = new PioneerForecastScenario(new(input, "calibrated"), "deck", "input", start, choices, declared);
        var reference = RootSearch.Evaluate(unmodified, unmodified.CreatePolicy(), root, candidates, Goal, 47, 2049, 1, ownedActionPayloadBytes: 0);
        Assert.True(baseline.Candidates[1].Summary!.UnconditionalUtilities[0] > reference.Candidates[1].Summary!.UnconditionalUtilities[0]);
        Assert.Equal(before, root.Fields.ToArray());
    }

    [Fact]
    public void ConstructionConfirmationIssuesTicketEvenWhenTheReachedPlanPrefixWasAlreadyBuilt()
    {
        var (start, input) = Fixture(15);
        var plan = new PioneerConstructionPlan([new(1, 101, 10100, 10110)]);
        var facilities = Enumerable.Range(101, 6).Select(id => new ObservedFacility(id, id == 101 ? 10110 : id * 100)).ToImmutableArray();
        var master = new PioneerMasterData(new('a', 64), [.. facilities.Select(row => new FacilityKind(row.FacilityId, 0))],
            [.. facilities.Select((row, i) => new FacilityState(i, row.FacilityId, row.FacilityId == 101 ? 1 : 0, 0, row.StateId)), new(7, 101, 0, 0, 10100)],
            [new(1, 101, 10100, 10110, 100)], [new(1, 3, 2, 12, 24, 500)], [new(1, 3, 13, 24, 250), new(2, 3, 13, 24, 500)]);
        var development = new PioneerDevelopmentState(15, 5, 300, 500, 3, [250, 500], 0, facilities, plan, [400011107]);
        var before = Values(start).Concat(facilities.Select(row => row.StateId)).Append(0).ToArray();
        before[15] = 300;
        var action = new ForecastAction(ForecastActionKind.Event, 6);
        input = input with { Construction = master, Fields = [.. input.Fields, .. facilities.Select(row => $"facility:{row.FacilityId}"), "island_tickets"],
            Stories = [400011107], Transitions = [new("donor", "deck", new(15, ForecastActionKind.Event, 400011107), new(16, ForecastActionKind.Home, 0),
                action, new([.. before], []), [], 400011107, -1, true, "fixture", 1)] };
        var scenario = new PioneerForecastScenario(new(input, "construction"), "deck", "input", start, [action], development: development);
        var root = new PioneerForecastState(new(15, ForecastActionKind.Event, 400011107), before, [0]);
        var random = new SimulationRandom(1);
        Assert.Null(scenario.ApplyAction(ref root, action, ref random).Stop);
        Assert.Equal(1, root.Fields[^1]);
        Assert.Equal(before.AsSpan(33, 6).ToArray(), root.Fields.Slice(33, 6).ToArray());
        Assert.Equal(0, before[^1]);
    }

    [Fact]
    public void UnexplainedCurrentTrainingEffectsStopWithoutCommittingStateOrRandomness()
    {
        var (start, input) = Fixture(77);
        start = start with { Deck = start.Deck.SetItem(0, start.Deck[0] with { UniqueEffects = [new(101, 80, 999, 10, 0, 0)] }) };
        var scenario = new PioneerForecastScenario(new(input, "unknown"), "deck", "input", start, Actions);
        var before = Values(start);
        var root = new PioneerForecastState(new(77, ForecastActionKind.Home, 0), before, [0]);
        var random = new SimulationRandom(91);
        var result = scenario.ApplyAction(ref root, Actions[0], ref random);
        Assert.Equal(2017, result.Stop!.Value.Code);
        Assert.False(result.ActionConsumed);
        Assert.Equal(before, root.Fields.ToArray());
        Assert.Equal(91UL, random.State);
    }

    [Fact]
    public void CurrentTurnCharacterBoostCannotBecomeAnImplicitPermanentFutureEffect()
    {
        var (start, input) = Fixture(77);
        start = start with { CharacterId = 1007, CardId = 100701,
            Environment = start.Environment with { GoldShipBoost = new(101) } };
        Assert.Throws<UnknownMechanismException>(() => new PioneerForecastScenario(new(input, "unknown-history"), "deck", "input", start, Actions));
    }

    static (PioneerStart.TrainingTurn Start, ForecastModelInput Input) Fixture(int turn)
    {
        var commands = new[] { 101, 105, 102, 103, 106 };
        var deck = commands.Select((command, index) => new TrainingSupport(30001 + index, 50, index + 1, command, 80,
            [new(1, 20), new(2, 20)], [])).Append(new(30257, 50, 6, 0, 50, [new(27, 20)], [])).ToImmutableArray();
        var options = commands.Select((command, index) => new TrainingCommandOption(command, 1, 3, 1,
            command == 101 ? [1, 6] : [index + 1], [], true, 720, 10)).ToImmutableArray();
        var start = new PioneerStart.TrainingTurn(turn, 1, 100101, new(5, 0, new(1195, 100, 100, 100, 100, 100, 20),
            new(1800, 1800, 1800, 1800, 1800, 100), new(), null), deck, options, [8], [], 600, turn == 77 ? 5 : 1, turn == 77 ? null : 10,
            [new(16, 3, 1, 3, 12, 400), new(17, 3, 2, 12, 24, 500), new(18, 3, 3, 24, 36, 600),
                new(19, 3, 4, 36, 48, 600), new(20, 3, 5, 48, 60, 800)], CharacterId: 1001);
        var before = Values(start);
        var rows = Actions.Select(action => new ForecastTransition("donor", "deck", new(turn, ForecastActionKind.Home, 0),
            new(turn + 1, ForecastActionKind.Event, 123), action, new([.. before], []),
            [.. Oracle(start, action.Id).Select((value, index) => new ForecastChange(index, value - before[index], false)).Where(change => change.Delta != 0)],
            0, 0, true, "fixture", action.Id)).ToList();
        for (var i = 0; i < 2; i++) rows.Add(new("donor", "deck", new(78, ForecastActionKind.Event, 123), null,
            new(ForecastActionKind.Event, 6), new([.. before], []), [new(2, 5 + i, false)], 123, -1, true, "fixture", 200 + i));
        return (start, new(3, "master", "evidence", [.. PioneerForecastScenario.BasicFields, "effect:8"], [123], [.. Enumerable.Repeat(720, 30)], [start], [.. rows], [], [], [], null, [], []));
    }

    static int[] Oracle(PioneerStart.TrainingTurn start, int command)
    {
        var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, true)], "oracle");
        var state = data.CreateInitialState();
        var scenario = new PioneerScenario(data);
        var random = new SimulationRandom(1);
        scenario.ApplyAction(ref state, PioneerRuntimeAction.Train(command), ref random);
        var result = Values(start);
        var resources = state.Header.Resources;
        result[0] = resources.Speed; result[1] = resources.Stamina; result[2] = resources.Power; result[3] = resources.Guts; result[4] = resources.Wisdom;
        result[10] = resources.Vital; result[13] = resources.SkillPoints; result[15] = state.Header.DevelopmentPoints;
        state.GetSection(PioneerStateSection.SupportBonds).CopyTo(result.AsSpan(16, 6));
        state.GetSection(PioneerStateSection.TrainingExecutionCounts).CopyTo(result.AsSpan(27, 5));
        return result;
    }

    static int[] Values(PioneerStart.TrainingTurn start)
    {
        var r = start.Environment.Current; var c = start.Environment.Limits;
        return [r.Speed, r.Stamina, r.Power, r.Guts, r.Wisdom, c.Speed, c.Stamina, c.Power, c.Guts, c.Wisdom,
            r.Vital, c.Vital, start.Environment.Motivation, r.SkillPoints, start.Environment.Fans, start.DevelopmentPoints,
            .. start.Deck.Select(card => card.Bond), .. start.Commands.Select(command => command.Level),
            .. start.Commands.Select(command => command.ExecutionCount), 1];
    }
}
