using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Tests;

public sealed class RootSearchTests
{
    static readonly RootCandidate<int>[] Candidates = [new(20, 0), new(30, 1), new(10, 2)];
    static readonly AttributeValues Caps = new(1800, 1800, 1800, 1800, 1800);

    [Fact]
    public void SearchComparesJointSuccessBeforePriorityUtilityAndKeepsTheRoot()
    {
        var root = CreateRoot();
        var scenario = new Scenario();
        const int samples = 32;
        var result = RootSearch.Evaluate(scenario, new Policy(), root, Candidates, Goal(), 123, samples, 4);
        Assert.Equal(4, scenario.CreatedWorkspaces);
        Assert.Equal(SearchStatus.Completed, result.Status);
        Assert.Equal(10, result.RecommendedCandidateId);
        Assert.Equal(new[] { 10, 30, 20 }, result.Ranking);
        Assert.Equal(samples, result.Candidates[1].Summary!.SuccessCount);
        Assert.True(result.Candidates[0].Summary!.SuccessCount < samples);
        Assert.Equal(0, root.Step);
        Assert.All(root.History, value => Assert.Equal(0, value));
        Assert.All(result.Candidates, candidate => Assert.All(candidate.Batch.Results, sample =>
        {
            Assert.Equal(TrajectoryStatus.Completed, sample.Status);
            Assert.Equal(7, sample.Progress.ConsumedActions);
            Assert.NotEqual(SimulationRandom.DeriveSeed(123, sample.SampleIndex, SimulationRandomStream.Rules), sample.RandomState.Rules);
            Assert.NotEqual(SimulationRandom.DeriveSeed(123, sample.SampleIndex, SimulationRandomStream.Policy), sample.RandomState.Policy);
        }));
    }

    [Fact]
    public void PairedSearchExtendsOnlyUnresolvedCandidatesAndProtectsTheSpecificTrainingPair()
    {
        var scenario = new Scenario(scoreTerminal: true, stableChallenger: true);
        RootCandidate<int>[] candidates = [.. Candidates, new(40, 3)];
        var result = RootSearch.Evaluate(scenario, new Policy(), CreateRoot(), candidates, Goal(1189), 12345, 32, 32,
            rankByTotalRating: true, hasFirstStepAdvantage: (_, left, right) => left == 0 && right is 1 or 2);
        Assert.Equal(20, result.FirstStepPromotedCandidateId);
        Assert.Equal(new[] { 10, 20, 30, 40 }, result.Ranking);
        Assert.True(result.Candidates[1].Summary!.MeanSuccessfulTotalRating > result.Candidates[0].Summary!.MeanSuccessfulTotalRating);
        Assert.Equal(4096, result.Candidates[0].Batch.Results.Length);
        Assert.InRange(result.Candidates[3].Batch.Results.Length, 32, 4095);
        var fixedSamples = RootSearch.Evaluate(scenario, new Policy(), CreateRoot(), candidates, Goal(1189), 12345, 4096, 1,
            rankByTotalRating: true);
        foreach (var (adaptive, fixedResult) in result.Candidates.Zip(fixedSamples.Candidates))
            Assert.Equal(fixedResult.Batch.Results.Take(adaptive.Batch.Results.Length), adaptive.Batch.Results);

        var withHigherGoal = RootSearch.Evaluate(scenario, new Policy(), CreateRoot(), Candidates, Goal(1201), 12345, 32, 4,
            rankByTotalRating: true, hasFirstStepAdvantage: (_, left, right) => left == 0 && right is 1 or 2);
        Assert.Null(withHigherGoal.FirstStepPromotedCandidateId);
        Assert.Equal(10, withHigherGoal.RecommendedCandidateId);

        var unrelatedThird = RootSearch.Evaluate(new Scenario(scoreTerminal: true, stableChallenger: true, uncertainLeader: true),
            new Policy(), CreateRoot(), candidates, Goal(1189), 12345, 32, 4, rankByTotalRating: true,
            hasFirstStepAdvantage: (_, left, right) => left == 3 && right == 1);
        Assert.Equal(new[] { 40, 30, 10, 20 }, unrelatedThird.Ranking);
        Assert.Equal(40, unrelatedThird.FirstStepPromotedCandidateId);
    }

    [Fact]
    public void CompleteOutcomesRandomStreamsRankingAndPrefixesAreWorkerAndCandidateOrderIndependent()
    {
        var root = CreateRoot();
        var scenario = new Scenario();
        var policy = new Policy();
        var goal = Goal();
        var baseline = RootSearch.Evaluate(scenario, policy, root, Candidates, goal, 42, 4097, 1);
        foreach (var workers in new[] { 4, 16, 32 })
        {
            var actual = RootSearch.Evaluate(scenario, policy, root, Candidates, goal, 42, 4097, workers);
            Assert.Equal(baseline.Ranking, actual.Ranking);
            for (var candidate = 0; candidate < Candidates.Length; candidate++)
            {
                Assert.Equal(baseline.Candidates[candidate].Batch.Results, actual.Candidates[candidate].Batch.Results);
                Assert.Equal(baseline.Candidates[candidate].Summary!.SuccessCount, actual.Candidates[candidate].Summary!.SuccessCount);
                Assert.Equal(baseline.Candidates[candidate].Summary!.ConditionalUtilities!.Value.ToArray(),
                    actual.Candidates[candidate].Summary!.ConditionalUtilities!.Value.ToArray());
            }
        }
        var reversed = RootSearch.Evaluate(scenario, policy, root, Candidates.Reverse().ToArray(), goal, 42, 129, 4);
        foreach (var candidate in reversed.Candidates)
            Assert.Equal(baseline.Candidates.Single(value => value.Candidate.Id == candidate.Candidate.Id).Batch.Results.Take(129), candidate.Batch.Results);
    }

    [Fact]
    public void ZeroSuccessRecommendsClosestCandidateAndKeepsTheSameRankingAcrossWorkers()
    {
        var result = RootSearch.Evaluate(new Scenario(), new Policy(), CreateRoot(), Candidates.Reverse().ToArray(), Goal(1799), 3, 2049, 1);
        Assert.Equal(10, result.RecommendedCandidateId);
        Assert.Equal(new[] { 10, 30, 20 }, result.Ranking);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(0, candidate.Summary!.SuccessCount);
            Assert.Null(candidate.Summary.ConditionalUtilities);
        });
        foreach (var workers in new[] { 4, 16, 32 })
        {
            var actual = RootSearch.Evaluate(new Scenario(), new Policy(), CreateRoot(), Candidates, Goal(1799), 3, 2049, workers);
            Assert.Equal(result.Ranking, actual.Ranking);
            foreach (var candidate in actual.Candidates)
            {
                var expected = result.Candidates.Single(row => row.Candidate.Id == candidate.Candidate.Id);
                Assert.Equal(expected.Batch.Results, candidate.Batch.Results);
                Assert.Equal(expected.Summary!.MeanConstraintViolation, candidate.Summary!.MeanConstraintViolation);
                Assert.Equal(expected.Summary.UnconditionalUtilities.ToArray(), candidate.Summary.UnconditionalUtilities.ToArray());
                Assert.Equal(expected.Summary.ZeroSuccessProbabilityUpperBound95, candidate.Summary.ZeroSuccessProbabilityUpperBound95);
            }
        }
        var ties = RootSearch.Evaluate(new Scenario(), new Policy(), CreateRoot(),
            new RootCandidate<int>[] { new(99, 2), new(10, 2) }, Goal(1799), 3, 32, 4);
        Assert.Equal(new[] { 10, 99 }, ties.Ranking);
    }

    [Fact]
    public void StoppedOrCancelledSearchRetainsAllRequestedSlotsAndDoesNotRankPartialBatches()
    {
        var candidates = new RootCandidate<int>[] { new(0, 0), new(4, 4) };
        var stopped = RootSearch.Evaluate(new Scenario(), new Policy(), CreateRoot(), candidates, Goal(), 7, 129, 4);
        Assert.Equal(SearchStatus.Stopped, stopped.Status);
        Assert.Null(stopped.RecommendedCandidateId);
        Assert.Empty(stopped.Ranking);
        Assert.Null(stopped.Candidates[1].Summary);
        Assert.All(stopped.Candidates[1].Batch.Results, sample => Assert.Equal(3, sample.Progress.ConsumedActions));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = RootSearch.Evaluate(new Scenario(), new Policy(), CreateRoot(), candidates, Goal(), 7, 129, 32, cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.RecommendedCandidateId);
        Assert.All(cancelled.Candidates, candidate =>
        {
            Assert.Equal(129, candidate.Batch.Results.Length);
            Assert.Equal(Enumerable.Range(0, 129), candidate.Batch.Results.Select(sample => sample.SampleIndex));
            Assert.All(candidate.Batch.Results, sample => Assert.Equal(SimulationStopReason.Cancelled, sample.Stop!.Value.Reason));
        });
    }

    [Fact]
    public void InvalidCandidatesAndTerminalRootsAreRejectedBeforeSimulating()
    {
        var scenario = new Scenario();
        Assert.Throws<ArgumentException>(() => RootSearch.Evaluate(scenario, new Policy(), CreateRoot(),
            new RootCandidate<int>[] { new(0, 0), new(0, 1) }, Goal(), 1, 16, 1));
        Assert.Throws<ArgumentException>(() => RootSearch.Evaluate(scenario, new Policy(), CreateRoot(),
            new RootCandidate<int>[] { new(0, 9) }, Goal(), 1, 16, 1));
        var terminal = CreateRoot();
        terminal.Step = 7;
        Assert.Throws<ArgumentException>(() => RootSearch.Evaluate(scenario, new Policy(), terminal, Candidates, Goal(), 1, 16, 1));
        Assert.Equal(0, scenario.CreatedWorkspaces);
    }

    static CompiledGoal Goal(int threshold = 1201) => GoalEvaluator.Compile(new(
        [new(TrainingAttribute.Stamina, AttributeComparison.GreaterThan, threshold),
         new(TrainingAttribute.Wisdom, AttributeComparison.GreaterThan, threshold)],
        [new([new(new AttributeDeficit(TrainingAttribute.Speed), -1)]),
         new([new(new AttributeUtility(TrainingAttribute.Power, new([new(0, 1)])), 1)])]), Caps);

    static State CreateRoot() => new() { Attributes = new(1100, 1190, 900, 800, 1190), History = new int[7] };
    struct State
    {
        public int Step;
        public int RootAction;
        public AttributeValues Attributes;
        public int[] History;
    }
    struct Cursor { public int Count; public int[] History; }

    sealed class Policy : ITrajectoryPolicy<int, int, Cursor>
    {
        public long SharedMemoryBytes => 0;
        public Cursor CreateCursor() => new() { History = new int[6] };
        public long GetCursorBytes(in Cursor cursor) => 48;
        public void ResetCursor(ref Cursor cursor, int sampleIndex) => cursor.Count = 0;
        public void CopyCursor(in Cursor source, ref Cursor destination)
        {
            source.History.AsSpan(0, source.Count).CopyTo(destination.History);
            destination.Count = source.Count;
        }
        public PolicySelection<int> ChooseAction(in DecisionPoint<int> decision, ref Cursor cursor, ref SimulationRandom random)
        {
            Assert.Equal(cursor.Count + 1, decision.Id.Ordinal);
            var action = random.NextInt(4);
            cursor.History[cursor.Count++] = action;
            return PolicySelection<int>.Selected(action);
        }
        public SimulationStop? ValidateCompletion(in Cursor cursor)
            => cursor.Count == 6 ? null : new(SimulationStopReason.ScriptMismatch, cursor.Count);
    }

    sealed class Scenario(bool scoreTerminal = false, bool stableChallenger = false, bool uncertainLeader = false)
        : ISimulationScenario<State, int, int, TerminalMetrics>
    {
        public int CreatedWorkspaces;
        public string RulesIdentity => "root-search-test-v1";
        public long SharedMemoryBytes => 0;
        public State CreateWorkspace(in State source)
        {
            CreatedWorkspaces++;
            return source with { History = new int[7] };
        }
        public long GetWorkspaceBytes(in State state) => 64;
        public void CopyState(in State source, ref State destination)
        {
            var buffer = destination.History;
            source.History.AsSpan(0, source.Step).CopyTo(buffer);
            destination = source;
            destination.History = buffer;
        }
        public Inspection<int> Inspect(in State state, long decisionOrdinal)
            => state.RootAction == 4 && state.Step == 3
                ? Inspection<int>.Stopped(new(SimulationStopReason.UnknownMechanism, 4, state.Step))
                : state.Step == 7 ? Inspection<int>.Terminal(state.Step)
                : Inspection<int>.AtDecision(new(new(1, decisionOrdinal, state.Step), state.Step));
        public SimulationStop? ValidateAction(in State state, in int action)
            => action is >= 0 and <= 4 ? null : new(SimulationStopReason.IllegalAction, action);
        public TransitionResult ApplyAction(ref State state, in int action, ref SimulationRandom random)
        {
            var value = random.NextInt(5);
            var attributes = state.Attributes;
            if (state.Step == 0)
            {
                state.RootAction = action;
                attributes = attributes with { Stamina = attributes.Stamina + (action == 0 ? 0 : 40),
                    Wisdom = attributes.Wisdom + (action == 0 ? 0 : 40), Power = attributes.Power + (action == 2 ? 30 : 0) };
            }
            state.Attributes = attributes with { Speed = attributes.Speed + value,
                Stamina = attributes.Stamina + value, Wisdom = attributes.Wisdom + value,
                Power = attributes.Power + value, Guts = attributes.Guts + action };
            state.History[state.Step++] = value + action;
            return TransitionResult.Settled(state.Step);
        }
        public TransitionResult ResumeAutomatic(ref State state, ref SimulationRandom random) => throw new InvalidOperationException();
        public TerminalMetrics GetOutcome(in State state)
        {
            var points = state.History.Sum();
            var rating = !scoreTerminal ? (int?)null : state.RootAction switch
            {
                0 => 1000,
                1 when uncertainLeader => 1100 + 300 * (state.History[0] - 3),
                3 when uncertainLeader => 1090,
                1 => state.History[0] == 5 ? 5000 : 0,
                2 => stableChallenger ? 1060 : 900,
                _ => 100,
            };
            return new(state.Attributes, Caps, points, new(0, 0, 0, 0, 0), rating,
                EstimatedFutureSkillRating: scoreTerminal ? 0 : null);
        }
    }
}
