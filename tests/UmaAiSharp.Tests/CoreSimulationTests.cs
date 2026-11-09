using UmaAiSharp.Core;
using System.Text.Json;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Tests;

public sealed class CoreSimulationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"decisionId\":{\"kind\":1,\"ordinal\":0},\"action\":0}")]
    [InlineData("{\"decisionId\":{\"kind\":1,\"ordinal\":0,\"key\":0}}")]
    public void ScriptJsonDoesNotDefaultMissingIdentityOrAction(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScriptedAction<int>>(json, GoalJson.Options));

    [Fact]
    public void RandomAlgorithmAndDrawConsumptionKeepTheirGoldenVectors()
    {
        var random = new SimulationRandom(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, random.NextUInt64());
        Assert.Equal(0x54ECD91C36EDB8D8UL, SimulationRandom.DeriveSeed(1234, 0, SimulationRandomStream.Rules));
        Assert.Equal(0x377D7D85D73B0FBCUL, SimulationRandom.DeriveSeed(1234, 12, SimulationRandomStream.Policy));
        var restored = random;
        Assert.Equal(random.NextUInt64(), restored.NextUInt64());
        Assert.Equal(random.NextInt(7), restored.NextInt(7));
        Assert.Equal(random.NextDouble(), restored.NextDouble());
        var beforeOne = random;
        Assert.Equal(0, random.NextInt(1));
        beforeOne.NextUInt64();
        Assert.Equal(beforeOne.State, random.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(0));
    }

    [Fact]
    public void StructStateChangesSurviveAndRootAndWorkspaceBuffersAreIndependent()
    {
        var scenario = new TestScenario();
        var policy = new CounterPolicy(3);
        var root = Root();
        var execution = SimulationRunner.CreateExecution(scenario, policy, root, 1234, 0);
        var result = SimulationRunner.Continue(scenario, policy, ref execution);
        Assert.Equal(TrajectoryStatus.Completed, result.Status);
        Assert.Equal(6, execution.State.Step);
        Assert.Equal(6, result.Progress.ConsumedActions);
        Assert.Equal(6, result.Progress.SettledActions);
        Assert.Equal(0, root.Step);
        Assert.Equal(0, root.Count);
        Assert.NotSame(root.Values, execution.State.Values);
        Assert.All(root.Values, value => Assert.Equal(0UL, value));
    }

    [Fact]
    public void BatchSlotsRandomStatesAndPrefixDoNotDependOnWorkers()
    {
        var root = Root();
        var scenario = new TestScenario();
        var policy = new CounterPolicy(3);
        var one = SimulationRunner.RunBatch(scenario, policy, root, 1234, 257, 1);
        var four = SimulationRunner.RunBatch(scenario, policy, root, 1234, 257, 4);
        var thirtyTwo = SimulationRunner.RunBatch(scenario, policy, root, 1234, 257, 32);
        var prefix = SimulationRunner.RunBatch(scenario, policy, root, 1234, 65, 4);
        Assert.Equal(one.Results, four.Results);
        Assert.Equal(one.Results, thirtyTwo.Results);
        Assert.Equal(one.Results.Take(65), prefix.Results);
        Assert.Equal(Enumerable.Range(0, 257), one.Results.Select(result => result.SampleIndex));
        Assert.Equal(12, scenario.WorkspacesCreated);
        Assert.Equal(12, policy.CursorsCreated);
        Assert.All(one.Results, result => Assert.Equal(TrajectoryStatus.Completed, result.Status));
    }

    [Fact]
    public void PolicyDrawsCannotChangeRuleRandomStream()
    {
        var root = Root();
        var scenario = new TestScenario();
        var first = SimulationRunner.RunBatch(scenario, new CounterPolicy(0), root, 4567, 64, 4);
        var second = SimulationRunner.RunBatch(scenario, new CounterPolicy(100), root, 4567, 64, 4);
        for (var i = 0; i < first.Results.Length; i++)
        {
            Assert.Equal(first.Results[i].Outcome, second.Results[i].Outcome);
            Assert.Equal(first.Results[i].RandomState.Rules, second.Results[i].RandomState.Rules);
            Assert.NotEqual(first.Results[i].RandomState.Policy, second.Results[i].RandomState.Policy);
        }
    }

    [Fact]
    public void ExactForkCopiesStatePolicyHistoryAndBothRandomStreams()
    {
        var root = Root();
        var scenario = new TestScenario();
        var policy = new CounterPolicy(3);
        var source = SimulationRunner.CreateExecution(scenario, policy, root, 12, 8);
        Assert.Equal(TrajectoryStatus.Advanced, SimulationRunner.Advance(scenario, policy, ref source).Status);
        var destination = SimulationRunner.CreateExecution(scenario, policy, root, 999, 100);
        SimulationRunner.ForkInto(scenario, policy, source, ref destination);
        Assert.NotSame(source.State.Values, destination.State.Values);
        Assert.NotSame(source.Cursor.History, destination.Cursor.History);
        var expected = SimulationRunner.Continue(scenario, policy, ref source);
        var actual = SimulationRunner.Continue(scenario, policy, ref destination);
        Assert.Equal(expected, actual);
        Assert.Equal(source.Cursor.History, destination.Cursor.History);
        SimulationRunner.ResetSample(scenario, policy, root, ref destination, 12, 9);
        Assert.Equal(0, destination.Progress.ConsumedActions);
        Assert.Equal(0, destination.Cursor.Count);
        Assert.NotEqual(source.RulesRandom.State, destination.RulesRandom.State);
    }

    [Fact]
    public void ConsumedUnsettledStopResumesWithoutChoosingOrConsumingAgain()
    {
        var root = Root(1);
        var scenario = new TestScenario(partial: true);
        var policy = new CounterPolicy(2);
        var source = SimulationRunner.CreateExecution(scenario, policy, root, 12, 8);
        var stopped = SimulationRunner.Continue(scenario, policy, ref source);
        Assert.Equal(TrajectoryStatus.Stopped, stopped.Status);
        Assert.Equal(SimulationStopReason.UnknownMechanism, stopped.Stop!.Value.Reason);
        Assert.True(stopped.Progress.ActionConsumed);
        Assert.False(stopped.Progress.ActionSettled);
        Assert.Equal(1, stopped.Progress.ConsumedActions);
        Assert.Equal(0, stopped.Progress.SettledActions);
        Assert.Equal(ExecutionStage.AutomaticSettlement, source.Stage);
        var fork = SimulationRunner.CreateExecution(scenario, policy, root, 0, 0);
        SimulationRunner.ForkInto(scenario, policy, source, ref fork);
        var resumed = SimulationRunner.Continue(scenario, policy, ref source);
        Assert.Equal(resumed, SimulationRunner.Continue(scenario, policy, ref fork));
        Assert.Equal(1, resumed.Progress.ConsumedActions);
        Assert.Equal(1, resumed.Progress.SettledActions);
        Assert.Equal(2, resumed.Outcome.Count);
        Assert.Equal(1, source.Cursor.Count);
    }

    [Fact]
    public void WorkspaceReuseResetsActiveLengthsCursorAndRandomHistory()
    {
        var scenario = new TestScenario();
        var policy = new CounterPolicy(1);
        var longRoot = Root(8);
        var shortRoot = Root(1);
        var execution = SimulationRunner.CreateExecution(scenario, policy, longRoot, 2, 3);
        SimulationRunner.Continue(scenario, policy, ref execution);
        SimulationRunner.ResetSample(scenario, policy, shortRoot, ref execution, 2, 3);
        var reused = SimulationRunner.Continue(scenario, policy, ref execution);
        var fresh = SimulationRunner.CreateExecution(scenario, policy, shortRoot, 2, 3);
        Assert.Equal(SimulationRunner.Continue(scenario, policy, ref fresh), reused);
        Assert.Equal(1, execution.State.Count);
        Assert.Equal(1, execution.Cursor.Count);
    }

    [Fact]
    public void CancellationKeepsEveryRequestedSlotAndInitialRandomIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var root = Root();
        var scenario = new TestScenario();
        var result = SimulationRunner.RunBatch(scenario, new CounterPolicy(0), root, 7, 129, 4, cancellation.Token);
        Assert.Equal(129, result.Results.Length);
        for (var i = 0; i < result.Results.Length; i++)
        {
            Assert.Equal(i, result.Results[i].SampleIndex);
            Assert.Equal(SimulationStopReason.Cancelled, result.Results[i].Stop!.Value.Reason);
            Assert.Equal(0, result.Results[i].Progress.ConsumedActions);
            Assert.Equal(SimulationRandom.DeriveSeed(7, i, SimulationRandomStream.Rules), result.Results[i].RandomState.Rules);
        }
        Assert.Equal(0, scenario.ApplyCalls);
    }

    [Fact]
    public void CancellationAfterSelectionKeepsPendingActionForExactContinuation()
    {
        using var cancellation = new CancellationTokenSource();
        var root = Root(1);
        var scenario = new TestScenario();
        var policy = new CounterPolicy(3, cancellation);
        var execution = SimulationRunner.CreateExecution(scenario, policy, root, 123, 0);
        var result = SimulationRunner.Continue(scenario, policy, ref execution, cancellation.Token);
        Assert.Equal(SimulationStopReason.Cancelled, result.Stop!.Value.Reason);
        Assert.Equal(ExecutionStage.SelectedAction, execution.Stage);
        Assert.Equal(0, scenario.ApplyCalls);
        Assert.Equal(1, execution.Cursor.Count);
        var randomState = execution.PolicyRandom.State;
        Assert.Equal(TrajectoryStatus.Completed, SimulationRunner.Continue(scenario, policy, ref execution).Status);
        Assert.Equal(1, execution.Cursor.Count);
        Assert.Equal(randomState, execution.PolicyRandom.State);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void CompiledScriptUsesIndependentCursorsAndChecksTerminalLength(int length, bool complete)
    {
        var root = Root();
        var scenario = new TestScenario();
        var script = new ActionScript<int, int>([.. Enumerable.Range(0, length)
            .Select(i => new ScriptedAction<int>(new(1, i, i), i + 1))]);
        var result = SimulationRunner.RunBatch(scenario, script, root, 123, 128, 4);
        Assert.All(result.Results, item => Assert.Equal(complete ? TrajectoryStatus.Completed : TrajectoryStatus.Stopped, item.Status));
        if (!complete) Assert.All(result.Results, item => Assert.Equal(SimulationStopReason.ScriptMismatch, item.Stop!.Value.Reason));
    }

    [Fact]
    public void IllegalActionAndScriptIdentityMismatchAreStructuredStops()
    {
        var root = Root();
        var scenario = new TestScenario();
        var illegal = new ActionScript<int, int>([new(new(1, 0, 0), 0)]);
        var mismatched = new ActionScript<int, int>([new(new(1, 0, 99), 1)]);
        var first = SimulationRunner.RunBatch(scenario, illegal, root, 1, 1, 1).Results[0];
        var second = SimulationRunner.RunBatch(scenario, mismatched, root, 1, 1, 1).Results[0];
        Assert.Equal(SimulationStopReason.IllegalAction, first.Stop!.Value.Reason);
        Assert.Equal(SimulationStopReason.ScriptMismatch, second.Stop!.Value.Reason);
        Assert.Equal(0, scenario.ApplyCalls);
    }

    [Fact]
    public void BudgetIsChargedFor32WorkspacesAndRejectedBeforeStateAllocation()
    {
        new ExecutionMemoryPlan(0, ExecutionMemoryPlan.LimitBytes / 32).Validate();
        Assert.Throws<InvalidOperationException>(() => new ExecutionMemoryPlan(1, ExecutionMemoryPlan.LimitBytes / 32).Validate());
        var root = Root();
        var scenario = new TestScenario(sharedBytes: ExecutionMemoryPlan.LimitBytes);
        Assert.Throws<InvalidOperationException>(() => SimulationRunner.RunBatch(scenario, new CounterPolicy(0), root, 1, 1, 1));
        Assert.Equal(0, scenario.WorkspacesCreated);
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulationRunner.RunBatch(new TestScenario(), new CounterPolicy(0), root, 1, 1, 33));
    }

    [Fact]
    public void ProgrammingErrorsAreNeverDisguisedAsUnknownMechanisms()
    {
        var root = Root();
        var scenario = new TestScenario(broken: true);
        var policy = new CounterPolicy(0);
        var execution = SimulationRunner.CreateExecution(scenario, policy, root, 1, 0);
        Assert.Throws<InvalidOperationException>(() => SimulationRunner.Continue(scenario, policy, ref execution));
    }

    static TestState Root(int limit = 6) => new() { Limit = limit, Values = new ulong[16] };
    struct TestState
    {
        public int Step;
        public int Limit;
        public int Count;
        public ulong[] Values;
        public bool Pending;
    }
    readonly record struct TestOutcome(int Step, int Count, ulong Checksum);
    struct TestCursor { public int Count; public int[] History; }

    sealed class TestScenario(bool partial = false, bool broken = false, long sharedBytes = 0)
        : ISimulationScenario<TestState, int, int, TestOutcome>
    {
        int workspaces;
        int applyCalls;
        public int WorkspacesCreated => workspaces;
        public int ApplyCalls => applyCalls;
        public string RulesIdentity => "synthetic-kernel/v2";
        public long SharedMemoryBytes => sharedBytes;
        public long GetWorkspaceBytes(in TestState state) => 64 + state.Values.Length * sizeof(ulong);
        public TestState CreateWorkspace(in TestState source)
        {
            Interlocked.Increment(ref workspaces);
            return new() { Values = new ulong[source.Values.Length] };
        }
        public void CopyState(in TestState source, ref TestState destination)
        {
            var buffer = destination.Values;
            source.Values.AsSpan(0, source.Count).CopyTo(buffer);
            destination = source;
            destination.Values = buffer;
        }
        public Inspection<int> Inspect(in TestState state, long ordinal) => state.Step == state.Limit
            ? Inspection<int>.Terminal(3) : Inspection<int>.AtDecision(new(new(1, ordinal, state.Step), state.Step));
        public SimulationStop? ValidateAction(in TestState state, in int action) => action == state.Step + 1 ? null
            : new(SimulationStopReason.IllegalAction, 1, 0, action, state.Step);
        public TransitionResult ApplyAction(ref TestState state, in int action, ref SimulationRandom random)
        {
            if (broken) throw new InvalidOperationException("implementation failure");
            Interlocked.Increment(ref applyCalls);
            state.Values[state.Count++] = random.NextUInt64();
            if (partial)
            {
                state.Pending = true;
                return TransitionResult.Stopped(true, false, new(SimulationStopReason.UnknownMechanism, 10, 1));
            }
            state.Step++;
            return TransitionResult.Settled(2);
        }
        public TransitionResult ResumeAutomatic(ref TestState state, ref SimulationRandom random)
        {
            if (!state.Pending) throw new InvalidOperationException("No pending settlement.");
            state.Values[state.Count++] = random.NextUInt64();
            state.Pending = false;
            state.Step++;
            return TransitionResult.Settled(2);
        }
        public TestOutcome GetOutcome(in TestState state)
        {
            var sum = 0UL;
            foreach (var value in state.Values.AsSpan(0, state.Count)) sum = unchecked(sum + value);
            return new(state.Step, state.Count, sum);
        }
    }

    sealed class CounterPolicy(int draws, CancellationTokenSource? cancellation = null) : ITrajectoryPolicy<int, int, TestCursor>
    {
        int cursors;
        public int CursorsCreated => cursors;
        public long SharedMemoryBytes => 0;
        public TestCursor CreateCursor()
        {
            Interlocked.Increment(ref cursors);
            return new() { History = new int[16] };
        }
        public long GetCursorBytes(in TestCursor cursor) => 32 + cursor.History.Length * sizeof(int);
        public void ResetCursor(ref TestCursor cursor, int sampleIndex) => cursor.Count = 0;
        public void CopyCursor(in TestCursor source, ref TestCursor destination)
        {
            source.History.AsSpan(0, source.Count).CopyTo(destination.History);
            destination.Count = source.Count;
        }
        public PolicySelection<int> ChooseAction(in DecisionPoint<int> decision, ref TestCursor cursor, ref SimulationRandom random)
        {
            for (var i = 0; i < draws; i++) random.NextUInt64();
            cursor.History[cursor.Count++] = decision.Context;
            cancellation?.Cancel();
            return PolicySelection<int>.Selected(decision.Context + 1);
        }
        public SimulationStop? ValidateCompletion(in TestCursor cursor) => null;
    }
}
