using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser]
public class ExecutionCompilationBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    ExecutionFixture fixture = null!;
    [GlobalSetup] public void Setup() => fixture = ExecutionFixture.Load(Case);
    [Benchmark] public PioneerRuntimeData CompileConfiguration()
        => PioneerRuntimeData.Compile(fixture.Start, fixture.Outcomes, ExecutionFixture.Identity);
}

[MemoryDiagnoser]
public class ExecutionCopyBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    PioneerState source = null!, destination = null!;
    [GlobalSetup]
    public void Setup()
    {
        var prepared = ExecutionFixture.Load(Case).Prepare();
        source = prepared.Root;
        destination = source.Clone();
    }
    [Benchmark(Baseline = true)] public PioneerState AllocateClone() => source.Clone();
    [Benchmark]
    public int CopyIntoReservedWorkspace()
    {
        source.CopyInto(destination);
        return destination.Header.Turn;
    }
}

[MemoryDiagnoser]
public class ExecutionForkBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    PreparedExecution prepared = null!;
    TrajectoryExecution<PioneerState, PioneerRuntimeAction, ScriptCursor> source, destination;
    [GlobalSetup]
    public void Setup()
    {
        prepared = ExecutionFixture.Load(Case).Prepare();
        source = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 1234, 0);
        destination = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 0, 0);
        var result = SimulationRunner.Advance(prepared.Scenario, prepared.Script, ref source);
        if (result.Status != TrajectoryStatus.Advanced || source.State.Header.Stage != PioneerStage.FailureChoice)
            throw new InvalidOperationException("分叉基准没有停在两个真实决策之间。");
    }
    [Benchmark]
    public RandomStreamState ExactForkIntoReservedWorkspace()
    {
        SimulationRunner.ForkInto(prepared.Scenario, prepared.Script, source, ref destination);
        return new(destination.RulesRandom.State, destination.PolicyRandom.State);
    }
}

[MemoryDiagnoser]
public class ExecutionSegmentBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    PreparedExecution prepared = null!;
    TrajectoryExecution<PioneerState, PioneerRuntimeAction, ScriptCursor> execution;
    [GlobalSetup]
    public void Setup()
    {
        prepared = ExecutionFixture.Load(Case).Prepare();
        execution = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 1234, 0);
    }
    [Benchmark]
    public TrajectoryResult<TerminalMetrics> ResetAndExecuteTwoDecisions()
    {
        SimulationRunner.ResetSample(prepared.Scenario, prepared.Script, prepared.Root, ref execution, 1234, 0);
        return SimulationRunner.Continue(prepared.Scenario, prepared.Script, ref execution);
    }
}

// MemoryDiagnoser does not measure the whole parallel process. Use ExecutionBatchAllocation separately.
public class ExecutionSmallBatchBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    [Params(1, 2, 4, 8, 16, 32)] public int Workers { get; set; }
    PreparedExecution prepared = null!;
    [GlobalSetup] public void Setup() => prepared = ExecutionFixture.Load(Case).Prepare();
    [Benchmark]
    public SimulationBatch<TerminalMetrics> Batch64()
        => SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, 64, Workers);
}

public class ExecutionLargeBatchBenchmarks
{
    [Params("camp", "ordinary")] public string Case { get; set; } = "camp";
    [Params(1, 2, 4, 8, 16, 32)] public int Workers { get; set; }
    PreparedExecution prepared = null!;
    [GlobalSetup] public void Setup() => prepared = ExecutionFixture.Load(Case).Prepare();
    [Benchmark]
    public SimulationBatch<TerminalMetrics> Batch4096()
        => SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, 4096, Workers);
}

public sealed record ExecutionAllocationObservation(string Case, int Samples, int Workers, long AllocatedBytes,
    double ElapsedMilliseconds, long ReservedBytes, int Gc0Delta, int Gc1Delta, int Gc2Delta,
    long PeakWorkingSet64, long PeakPagedMemorySize64, string PeakScope,
    ulong ResultChecksum, string FixtureSha256, string Scope);

public sealed record ColdCompileObservation(string Case, long AllocatedBytes, double ElapsedMilliseconds,
    long SharedBytes, long WorkspaceBytes, long ReservedBytes, long BudgetLimitBytes,
    int Gc0Delta, int Gc1Delta, int Gc2Delta, long PeakWorkingSet64, long PeakPagedMemorySize64,
    string PeakScope, string FixtureSha256, string Scope);

public static class ExecutionBatchAllocation
{
    // Run in a dedicated process; the counter includes unrelated allocations if other work shares that process.
    public static ExecutionAllocationObservation Measure(string caseName, int samples, int workers)
    {
        var prepared = ExecutionFixture.Load(caseName).Prepare();
        _ = SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, samples, workers);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        var batch = SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, samples, workers);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        gc0 = GC.CollectionCount(0) - gc0; gc1 = GC.CollectionCount(1) - gc1; gc2 = GC.CollectionCount(2) - gc2;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;
        var peakPagedMemory = process.PeakPagedMemorySize64;
        var checksum = 0UL;
        for (var index = 0; index < batch.Results.Length; index++)
        {
            var result = batch.Results[index];
            if (result.SampleIndex != index || result.Status != TrajectoryStatus.Stopped
                || result.Stop?.Code != (int)PioneerStopCode.NextTurnEventsUnknown
                || result.Progress.ConsumedActions != 2 || result.Progress.SettledActions != 1)
                throw new InvalidOperationException("批量基准改变了固定任务或片段停止边界。");
            checksum = unchecked(checksum * 31 + result.RandomState.Rules + result.RandomState.Policy);
        }
        return new(caseName, samples, workers, allocated, elapsed.TotalMilliseconds, batch.Memory.ReservedBytes,
            gc0, gc1, gc2, peakWorkingSet, peakPagedMemory, "ProcessLifetimeIncludingSetupAndWarmup",
            checksum, ExecutionFixture.FixtureSha256, "TwoConditionalFailureDecisions");
    }

    public static ColdCompileObservation MeasureColdCompilation(string caseName)
    {
        var fixture = ExecutionFixture.Load(caseName);
        ExecutionPlatform.EnsureSupported();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        var prepared = fixture.Compile();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        gc0 = GC.CollectionCount(0) - gc0; gc1 = GC.CollectionCount(1) - gc1; gc2 = GC.CollectionCount(2) - gc2;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cursor = prepared.Script.CreateCursor();
        var memory = SimulationRunner.GetMemoryPlan(prepared.Scenario, prepared.Script, prepared.Root, cursor);
        memory.Validate();
        return new(caseName, allocated, elapsed.TotalMilliseconds, memory.SharedBytes, memory.WorkspaceBytes,
            memory.ReservedBytes, ExecutionMemoryPlan.LimitBytes, gc0, gc1, gc2,
            process.PeakWorkingSet64, process.PeakPagedMemorySize64, "ProcessLifetimeIncludingFixtureParsing",
            ExecutionFixture.FixtureSha256, "FirstRuntimeAndScriptCompilationInCurrentProcess");
    }
}

sealed record PreparedExecution(PioneerScenario Scenario, PioneerState Root,
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script);

sealed record ExecutionFixture(PioneerStart.TrainingTurn Start, int CommandId, string StartSha256,
    ImmutableArray<PioneerConditionalOutcome> Outcomes, TrainingFailureEvent FailureEvent, TrainingValues ImmediateResources, TrainingValues AfterResources,
    int AfterTurn, int AfterMotivation, int AfterExecutionCount)
{
    public const string FixtureSha256 = "ed26048117b2327287cc3704007430e86250ba0ea3d03cf79caa40b8f018863f";
    public const string Identity = "raw-failure-fixtures/" + FixtureSha256;

    public static ExecutionFixture Load(string caseName)
    {
        var index = caseName switch { "camp" => 0, "ordinary" => 1, _ => throw new ArgumentException("未知真实失败基准样本。", nameof(caseName)) };
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "training-failure-snapshots.json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != FixtureSha256)
            throw new InvalidDataException("失败基准夹具SHA-256与锁定原包投影不一致。");
        using var document = JsonDocument.Parse(bytes);
        var window = document.RootElement.GetProperty("windows")[index];
        var before = window.GetProperty("before");
        var immediate = window.GetProperty("immediateAfter");
        var after = window.GetProperty("choiceAfter");
        var item = window.GetProperty("failureEvent");
        var failure = new TrainingFailureEvent(Int(item, "eventId"), Int(item, "characterId"), Int(item, "storyId"), Int(item, "playTiming"));
        var rootBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", caseName + "-failure-replay.json"));
        var rootHash = Convert.ToHexStringLower(SHA256.HashData(rootBytes));
        var expectedHash = caseName == "camp" ? "f284798eab090774cbb812b0eadf988e2697cb1539cfbb1d1dbcc0a138547e49"
            : "e1d029c65b3e76e16b246e3678d323d5d87f5243c0e55ce35bda62dfe40114af";
        if (rootHash != expectedHash) throw new InvalidDataException("完整训练前态基准与冻结来源不一致。");
        using var rootDocument = JsonDocument.Parse(rootBytes);
        var state = rootDocument.RootElement.GetProperty("start").GetProperty("data")
            .Deserialize<PioneerStart.TrainingTurn>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (state.Environment.Current != Resources(before) || state.Turn != Int(before, "turn")
            || state.CardId != Int(before, "cardId") || state.Environment.Motivation != Int(before, "motivation"))
            throw new InvalidDataException("完整训练前态与失败窗口不一致。");
        return new(state, Int(window, "commandId"), rootHash,
            [new PioneerConditionalOutcome.TrainingFailure(0, failure),
             new PioneerConditionalOutcome.FailurePenalty(1, ResolvedFailureBranch.StaminaMinusFiveMotivationMinusOne)],
            failure, Resources(immediate), Resources(after), Int(after, "turn"), Int(after, "motivation"), Int(after, "staminaExecutionCount"));
    }

    public PreparedExecution Compile()
    {
        var data = PioneerRuntimeData.Compile(Start, Outcomes, Identity + "/" + StartSha256);
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var current = new PioneerEvent(FailureEvent.EventId, FailureEvent.CharacterId, FailureEvent.StoryId, FailureEvent.PlayTiming);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
        [
            new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Train(CommandId)),
            new(new((int)PioneerStage.FailureChoice, 1, PioneerScenario.DecisionKey(root.Header.Turn, current.EventId)),
                PioneerRuntimeAction.Choose(current, 1))
        ], 0);
        return new(scenario, root, script);
    }

    public PreparedExecution Prepare()
    {
        var prepared = Compile();
        var execution = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 1234, 0);
        var immediate = SimulationRunner.Advance(prepared.Scenario, prepared.Script, ref execution);
        if (immediate.Status != TrajectoryStatus.Advanced || execution.State.Header.Resources != ImmediateResources)
            throw new InvalidOperationException("真实失败基准的立即结算与原包不符。");
        var result = SimulationRunner.Continue(prepared.Scenario, prepared.Script, ref execution);
        if (result.Status != TrajectoryStatus.Stopped || result.Stop?.Code != (int)PioneerStopCode.NextTurnEventsUnknown
            || result.Progress.ConsumedActions != 2 || result.Progress.SettledActions != 1
            || execution.State.Header.Resources != AfterResources || execution.State.Header.Turn != AfterTurn
            || execution.State.Header.Motivation != AfterMotivation || execution.State.Header.StaminaExecutionCount != AfterExecutionCount)
            throw new InvalidOperationException("真实失败基准的选择结算或停止位置与原包不符。");
        return prepared;
    }

    static int Int(JsonElement value, string key) => value.GetProperty(key).GetInt32();
    static TrainingValues Resources(JsonElement value)
    {
        var resources = value.GetProperty("resources");
        return new(Int(resources, "speed"), Int(resources, "stamina"), Int(resources, "power"), Int(resources, "guts"),
            Int(resources, "wisdom"), Int(resources, "vital"), Int(resources, "skillPoints"));
    }
}
