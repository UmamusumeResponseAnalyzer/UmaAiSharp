using System.Runtime.CompilerServices;

namespace UmaAiSharp.Core;

public static class SimulationRunner
{
    public const int LogicalBlockSize = 64;

    public static TrajectoryExecution<TState, TAction, TCursor> CreateExecution<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState root, ulong seed, int sampleIndex)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.RulesIdentity);
        var cursor = policy.CreateCursor();
        var memory = GetMemoryPlan(scenario, policy, root, cursor);
        memory.Validate();
        var execution = new TrajectoryExecution<TState, TAction, TCursor>
        {
            State = scenario.CreateWorkspace(root), Cursor = cursor, PendingAction = default!
        };
        ResetSample(scenario, policy, root, ref execution, seed, sampleIndex);
        ValidateWorkspace(scenario, policy, execution, memory);
        return execution;
    }

    public static void ResetSample<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState root,
        ref TrajectoryExecution<TState, TAction, TCursor> execution, ulong seed, int sampleIndex)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        scenario.CopyState(root, ref execution.State);
        policy.ResetCursor(ref execution.Cursor, sampleIndex);
        execution.RulesRandom = new(SimulationRandom.DeriveSeed(seed, sampleIndex, SimulationRandomStream.Rules));
        execution.PolicyRandom = new(SimulationRandom.DeriveSeed(seed, sampleIndex, SimulationRandomStream.Policy));
        execution.SampleIndex = sampleIndex;
        execution.RootSeed = seed;
        execution.PendingAction = default!;
        execution.Progress = default;
        execution.Stage = ExecutionStage.Ready;
    }

    public static void ForkInto<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        in TrajectoryExecution<TState, TAction, TCursor> source,
        ref TrajectoryExecution<TState, TAction, TCursor> destination)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        scenario.CopyState(source.State, ref destination.State);
        policy.CopyCursor(source.Cursor, ref destination.Cursor);
        destination.RulesRandom = source.RulesRandom;
        destination.PolicyRandom = source.PolicyRandom;
        destination.PendingAction = source.PendingAction;
        destination.Stage = source.Stage;
        destination.Progress = source.Progress;
        destination.SampleIndex = source.SampleIndex;
        destination.RootSeed = source.RootSeed;
    }

    public static TrajectoryResult<TOutcome> Advance<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        ref TrajectoryExecution<TState, TAction, TCursor> execution, CancellationToken cancellationToken = default)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        return AdvanceCore(scenario, policy, ref execution, cancellationToken);
    }

    public static TrajectoryResult<TOutcome> Continue<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        ref TrajectoryExecution<TState, TAction, TCursor> execution, CancellationToken cancellationToken = default)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        return ContinueCore(scenario, policy, ref execution, cancellationToken);
    }

    public static SimulationBatch<TOutcome> RunBatch<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState root, ulong seed, int samples,
        int workerCount, CancellationToken cancellationToken = default)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        var (memory, workspaces) = CreateBatchWorkspaces(scenario, policy, root, samples, workerCount);
        return RunBatch(scenario, policy, root, seed, samples, memory, workspaces, cancellationToken);
    }

    internal static (ExecutionMemoryPlan Memory, TrajectoryExecution<TState, TAction, TCursor>[] Workspaces)
        CreateBatchWorkspaces<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState root, int samples, int workerCount)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        if (workerCount is < 1 or > ExecutionMemoryPlan.WorkerCapacity)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "worker数量必须为1..32。");
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.RulesIdentity);
        var firstCursor = policy.CreateCursor();
        var memory = GetMemoryPlan(scenario, policy, root, firstCursor);
        memory.Validate();
        var blocks = (samples - 1) / LogicalBlockSize + 1;
        var activeWorkers = Math.Min(workerCount, blocks);
        var workspaces = new TrajectoryExecution<TState, TAction, TCursor>[activeWorkers];
        for (var i = 0; i < workspaces.Length; i++)
        {
            workspaces[i] = new()
            {
                State = scenario.CreateWorkspace(root), Cursor = i == 0 ? firstCursor : policy.CreateCursor(), PendingAction = default!
            };
            ValidateWorkspace(scenario, policy, workspaces[i], memory);
        }
        return (memory, workspaces);
    }

    // The previous batch has joined before these exclusively owned workspaces are reused.
    internal static SimulationBatch<TOutcome> RunBatch<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState root, ulong seed, int samples,
        ExecutionMemoryPlan memory, TrajectoryExecution<TState, TAction, TCursor>[] workspaces,
        CancellationToken cancellationToken)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        var blocks = (samples - 1) / LogicalBlockSize + 1;
        var slots = new TrajectoryResult<TOutcome>[samples];
        var source = root;
        var nextBlock = -1;
        Parallel.For(0, workspaces.Length, new ParallelOptions { MaxDegreeOfParallelism = workspaces.Length }, worker =>
        {
            var execution = workspaces[worker];
            while (true)
            {
                var block = Interlocked.Increment(ref nextBlock);
                if (block >= blocks) break;
                var first = block * LogicalBlockSize;
                var end = (int)Math.Min(samples, (long)first + LogicalBlockSize);
                for (var index = first; index < end; index++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        slots[index] = new(TrajectoryStatus.Stopped, index, default, default!, new(SimulationStopReason.Cancelled, 0),
                            new(SimulationRandom.DeriveSeed(seed, index, SimulationRandomStream.Rules),
                                SimulationRandom.DeriveSeed(seed, index, SimulationRandomStream.Policy)));
                        continue;
                    }
                    ResetSample(scenario, policy, source, ref execution, seed, index);
                    slots[index] = ContinueCore(scenario, policy, ref execution, cancellationToken);
                }
            }
            ValidateWorkspace(scenario, policy, execution, memory);
            workspaces[worker] = execution;
        });
        return new(new(scenario.RulesIdentity, seed, SimulationRandom.Algorithm, SimulationRandom.SeedDerivation), slots, memory);
    }

    static TrajectoryResult<TOutcome> ContinueCore<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        ref TrajectoryExecution<TState, TAction, TCursor> execution, CancellationToken cancellationToken)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        while (true)
        {
            var result = AdvanceCore(scenario, policy, ref execution, cancellationToken);
            if (result.Status != TrajectoryStatus.Advanced) return result;
        }
    }

    static TrajectoryResult<TOutcome> AdvanceCore<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        ref TrajectoryExecution<TState, TAction, TCursor> execution, CancellationToken cancellationToken)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        if (cancellationToken.IsCancellationRequested)
            return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, new(SimulationStopReason.Cancelled, 0, execution.Progress.Position));
        if (execution.Stage is ExecutionStage.Ready or ExecutionStage.Terminal)
        {
            var inspection = scenario.Inspect(execution.State, execution.Progress.DecisionOrdinal);
            execution.Progress = execution.Progress with { Position = inspection.Position };
            if (inspection.Kind == InspectionKind.Stopped)
                return Stopped<TState, TAction, TCursor, TOutcome>(ref execution,
                    inspection.Stop ?? throw new InvalidOperationException("停止检查缺少原因。"));
            if (inspection.Stop is not null) throw new InvalidOperationException("非停止检查不能携带停止原因。");
            if (inspection.Kind == InspectionKind.Terminal)
            {
                if (policy.ValidateCompletion(execution.Cursor) is { } incomplete)
                    return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, incomplete);
                var outcome = scenario.GetOutcome(execution.State);
                execution.Stage = ExecutionStage.Terminal;
                return cancellationToken.IsCancellationRequested
                    ? Stopped<TState, TAction, TCursor, TOutcome>(ref execution, new(SimulationStopReason.Cancelled, 0, execution.Progress.Position))
                    : new(TrajectoryStatus.Completed, execution.SampleIndex, execution.Progress, outcome, null,
                        new(execution.RulesRandom.State, execution.PolicyRandom.State));
            }
            if (inspection.Kind != InspectionKind.Decision || execution.Stage == ExecutionStage.Terminal
                || inspection.Decision.Id.Ordinal != execution.Progress.DecisionOrdinal || inspection.Decision.Id.Kind < 0)
                throw new InvalidOperationException("决策检查的阶段或轨迹局部序号不一致。");
            var selection = policy.ChooseAction(inspection.Decision, ref execution.Cursor, ref execution.PolicyRandom);
            if (selection.Stop is { } policyStop)
                return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, policyStop);
            execution.PendingAction = selection.Action;
            execution.Stage = ExecutionStage.SelectedAction;
            execution.Progress = execution.Progress with { ActionConsumed = false, ActionSettled = false };
        }
        if (cancellationToken.IsCancellationRequested)
            return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, new(SimulationStopReason.Cancelled, 0, execution.Progress.Position));
        var automatic = execution.Stage == ExecutionStage.AutomaticSettlement;
        if (!automatic)
        {
            if (execution.Stage != ExecutionStage.SelectedAction) throw new InvalidOperationException("执行阶段无效。");
            if (scenario.ValidateAction(execution.State, execution.PendingAction) is { } invalid)
                return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, invalid);
            if (cancellationToken.IsCancellationRequested)
                return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, new(SimulationStopReason.Cancelled, 0, execution.Progress.Position));
        }
        var transition = automatic
            ? scenario.ResumeAutomatic(ref execution.State, ref execution.RulesRandom)
            : scenario.ApplyAction(ref execution.State, execution.PendingAction, ref execution.RulesRandom);
        if (transition.ActionSettled && !transition.ActionConsumed || automatic && !transition.ActionConsumed
            || transition.Stop is null && !transition.ActionSettled)
            throw new InvalidOperationException("动作消费/结算状态与停止结果矛盾。");
        var progress = execution.Progress;
        execution.Progress = new(
            checked(progress.DecisionOrdinal + (!automatic && transition.ActionConsumed ? 1 : 0)),
            checked(progress.ConsumedActions + (!automatic && transition.ActionConsumed ? 1 : 0)),
            checked(progress.SettledActions + (transition.ActionSettled ? 1 : 0)), transition.Position,
            transition.ActionConsumed, transition.ActionSettled);
        execution.Stage = transition.ActionSettled ? ExecutionStage.Ready
            : transition.ActionConsumed ? ExecutionStage.AutomaticSettlement : ExecutionStage.SelectedAction;
        if (transition.ActionSettled) execution.PendingAction = default!;
        if (transition.Stop is { } stopped)
            return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, stopped);
        if (cancellationToken.IsCancellationRequested)
            return Stopped<TState, TAction, TCursor, TOutcome>(ref execution, new(SimulationStopReason.Cancelled, 0, execution.Progress.Position));
        return new(TrajectoryStatus.Advanced, execution.SampleIndex, execution.Progress, default!, null,
            new(execution.RulesRandom.State, execution.PolicyRandom.State));
    }

    static TrajectoryResult<TOutcome> Stopped<TState, TAction, TCursor, TOutcome>(
        ref TrajectoryExecution<TState, TAction, TCursor> execution, SimulationStop stop)
        where TState : notnull where TAction : notnull
    {
        execution.Progress = execution.Progress with { Position = stop.Position };
        return new(TrajectoryStatus.Stopped, execution.SampleIndex, execution.Progress, default!, stop,
            new(execution.RulesRandom.State, execution.PolicyRandom.State));
    }

    public static ExecutionMemoryPlan GetMemoryPlan<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy, in TState state, in TCursor cursor)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        var stateBytes = scenario.GetWorkspaceBytes(state);
        var cursorBytes = policy.GetCursorBytes(cursor);
        if (stateBytes < 0 || cursorBytes < 0 || scenario.SharedMemoryBytes < 0 || policy.SharedMemoryBytes < 0)
            throw new InvalidOperationException("场景与策略的容量声明不能为负数。");
        return new(checked(scenario.SharedMemoryBytes + policy.SharedMemoryBytes),
            checked(stateBytes + cursorBytes + Unsafe.SizeOf<TrajectoryExecution<TState, TAction, TCursor>>()));
    }

    static void ValidateWorkspace<TState, TDecision, TAction, TOutcome, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TOutcome> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> policy,
        in TrajectoryExecution<TState, TAction, TCursor> execution, ExecutionMemoryPlan reserved)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        var actual = GetMemoryPlan(scenario, policy, execution.State, execution.Cursor);
        if (actual.SharedBytes > reserved.SharedBytes || actual.WorkspaceBytes > reserved.WorkspaceBytes)
            throw new InvalidOperationException("执行期间工作区或共享数据超出预先声明的容量；禁止隐式扩容。");
    }
}
