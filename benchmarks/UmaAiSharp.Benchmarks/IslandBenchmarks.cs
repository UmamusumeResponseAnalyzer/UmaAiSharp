using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser]
public class IslandTrainingBenchmarks
{
    [Params("island-1058", "island-3298")] public string Case { get; set; } = "island-1058";
    IslandTrainingInput input = null!;
    CompiledTrainingDeck deck = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fixture = IslandBenchmarkFixture.Load(Case);
        input = fixture.Start.Training;
        deck = new([.. input.Assignments.SelectMany(group => group.Supports)]);
        var reference = TrainingFormulaCandidate.GenerateIsland(input);
        var numeric = TrainingNumericKernel.GenerateIsland(input);
        if ((numeric.Common, numeric.Scenario) != (reference.Common, reference.Scenario)
            || TrainingNumericKernel.GenerateIsland(input, deck) != numeric)
            throw new InvalidOperationException("真实岛训练输入的参考/数值/编成数值不一致，拒绝计时。");
    }

    [Benchmark(Baseline = true)] public TrainingFormulaPrediction ExactAuditReference() => TrainingFormulaCandidate.GenerateIsland(input);
    [Benchmark] public TrainingNumericResult Numeric() => TrainingNumericKernel.GenerateIsland(input);
    [Benchmark] public TrainingNumericResult CompiledNumeric() => TrainingNumericKernel.GenerateIsland(input, deck);
}

[MemoryDiagnoser]
public class IslandSegmentBenchmarks
{
    [Params("island-1058", "island-3298")] public string Case { get; set; } = "island-1058";
    PreparedIslandExecution prepared = null!;
    TrajectoryExecution<PioneerState, PioneerRuntimeAction, ScriptCursor> execution;

    [GlobalSetup]
    public void Setup()
    {
        prepared = IslandBenchmarkFixture.Load(Case).Prepare();
        execution = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 1234, 0);
    }

    [Benchmark]
    public TrajectoryResult<TerminalMetrics> ResetAndExecuteIslandSegment()
    {
        SimulationRunner.ResetSample(prepared.Scenario, prepared.Script, prepared.Root, ref execution, 1234, 0);
        return SimulationRunner.Continue(prepared.Scenario, prepared.Script, ref execution);
    }
}

[MemoryDiagnoser]
public class IslandCopyBenchmarks
{
    [Params("island-1058", "island-3298")] public string Case { get; set; } = "island-1058";
    [Params("root", "settled-component")] public string Boundary { get; set; } = "root";
    PioneerState source = null!, destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        var prepared = IslandBenchmarkFixture.Load(Case).Prepare();
        source = Boundary switch
        {
            "root" => prepared.Root,
            "settled-component" => prepared.AfterKnownComponent,
            _ => throw new ArgumentException("未知岛训练复制边界。")
        };
        destination = source.Clone();
        source.CopyInto(destination);
        if (!source.Header.Equals(destination.Header) || source.WorkspaceBytes != destination.WorkspaceBytes)
            throw new InvalidOperationException("复制后的头部或容量不一致。");
        foreach (var section in Enum.GetValues<PioneerStateSection>())
            if (!source.GetSection(section).SequenceEqual(destination.GetSection(section)))
                throw new InvalidOperationException("复制后的可变区不一致。");
        Console.WriteLine($"IslandCopy case={Case}, boundary={Boundary}, usedIntegers={source.UsedBufferIntegers}, capacityIntegers={source.BufferCapacityIntegers}, mutablePayloadBytes={source.MutablePayloadBytes}, workspaceBytes={source.WorkspaceBytes}");
    }

    [Benchmark(Baseline = true)] public PioneerState AllocateClone() => source.Clone();
    [Benchmark]
    public int CopyIntoReservedWorkspace()
    {
        source.CopyInto(destination);
        return destination.UsedBufferIntegers;
    }
}

// Parallel process allocation is measured by IslandBatchAllocation, not MemoryDiagnoser.
public class IslandBatchBenchmarks
{
    [Params(64, 4096)] public int Samples { get; set; }
    [Params(1, 2, 4, 8, 16, 32)] public int Workers { get; set; }
    PreparedIslandExecution prepared = null!;

    [GlobalSetup]
    public void Setup()
    {
        prepared = IslandBenchmarkFixture.Load("island-3298").Prepare();
        var batch = SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, Samples, Workers);
        _ = IslandBenchmarkFixture.ValidateBatch(batch, 1234);
    }

    [Benchmark]
    public SimulationBatch<TerminalMetrics> ExecuteHeavyIslandBatch()
        => SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 1234, Samples, Workers);
}

public static class IslandBatchAllocation
{
    public static ExecutionAllocationObservation Measure(int samples, int workers)
    {
        var fixture = IslandBenchmarkFixture.Load("island-3298");
        var prepared = fixture.Prepare();
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
        var checksum = IslandBenchmarkFixture.ValidateBatch(batch, 1234);
        return new("island-3298", samples, workers, allocated, elapsed.TotalMilliseconds, batch.Memory.ReservedBytes,
            gc0, gc1, gc2, peakWorkingSet, peakPagedMemory, "ProcessLifetimeIncludingSetupAndWarmup",
            checksum, fixture.FixtureSha256, "IslandActionComponent");
    }
}

sealed record PreparedIslandExecution(PioneerScenario Scenario, PioneerState Root,
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script, PioneerState AfterKnownComponent);

sealed record IslandBenchmarkFixture(PioneerStart.IslandTraining Start, string FixtureSha256)
{
    const string ExpectedFixtureSha256 = "a7b4acd385431f08849a466fb9f1bd2f28ed56e43e9b8acc313988aa3a22c62d";

    public static ulong ValidateBatch(SimulationBatch<TerminalMetrics> batch, ulong seed)
    {
        var checksum = 0UL;
        for (var index = 0; index < batch.Results.Length; index++)
        {
            var result = batch.Results[index];
            if (result.SampleIndex != index || result.Status != TrajectoryStatus.Stopped
                || result.Stop?.Reason != SimulationStopReason.UnknownMechanism
                || result.Stop?.Code != (int)PioneerStopCode.IslandContinuationUnknown
                || result.Progress.ConsumedActions != 1 || result.Progress.SettledActions != 0
                || result.RandomState.Rules != SimulationRandom.DeriveSeed(seed, index, SimulationRandomStream.Rules)
                || result.RandomState.Policy != SimulationRandom.DeriveSeed(seed, index, SimulationRandomStream.Policy))
                throw new InvalidOperationException("岛训练批量基准改变了固定样本、RNG或片段停止边界。");
            checksum = unchecked(checksum * 31 + result.RandomState.Rules + result.RandomState.Policy);
        }
        return checksum;
    }

    public static IslandBenchmarkFixture Load(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "island-inputs.json"));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (fingerprint != ExpectedFixtureSha256)
            throw new InvalidDataException("岛训练派生基准夹具SHA-256与已导出的固定输入不一致。");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1
            || root.GetProperty("sourceFixtureSha256").GetString() != "cba6ac6c73741bd0ee446f277b3d983849fb53faf6781cfa08a5c568d6c7e560"
            || root.GetProperty("scope").GetString() != "BeforeOnlyIslandStarts"
            || root.GetProperty("compiler").GetString() != "PioneerMasterCompiler.IslandStart"
            || root.GetProperty("cases").GetArrayLength() != 7)
            throw new InvalidDataException("岛训练基准输入不符合锁定来源与导出范围。");
        var row = root.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
        };
        var start = row.GetProperty("start").Deserialize<PioneerStart.IslandTraining>(options)
            ?? throw new InvalidDataException("岛训练起点为空。");
        Console.WriteLine($"IslandFixture case={name}, sha256={fingerprint}, beforeRecordId={row.GetProperty("beforeRecordId").GetInt64()}, turn={start.State.ActionTurn}, supports={start.Training.Assignments.Sum(group => group.Supports.Length)}, guests={start.Training.Assignments.Sum(group => group.GuestCount)}");
        return new(start, fingerprint);
    }

    public PreparedIslandExecution Prepare()
    {
        var reference = IslandActionRules.ApplySuccessCandidate(Start.State, Start.Training);
        var data = PioneerRuntimeData.Compile(Start, [], "island-before-fixture/" + FixtureSha256);
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(new((int)PioneerStage.IslandTraining, 0, PioneerScenario.DecisionKey(root.Header.Turn, 3101)), PioneerRuntimeAction.Island())], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 1234, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        if (result.Status != TrajectoryStatus.Stopped || result.Stop?.Code != (int)PioneerStopCode.IslandContinuationUnknown
            || result.Progress.ConsumedActions != 1 || result.Progress.SettledActions != 0
            || execution.State.Header.Resources != reference.ValuesAfter
            || execution.State.Header.DevelopmentPoints != reference.DevelopmentPointsAfter
            || execution.State.Header.Tickets != reference.TicketsAfter
            || execution.State.Header.FutureGainedSkillPoints != reference.ValuesAfter.SkillPoints - Start.State.Values.SkillPoints
            || execution.State.Header.TurnKnown || execution.State.Header.RawPlayingStateKnown
            || execution.State.WorkspaceBytes != root.WorkspaceBytes)
            throw new InvalidOperationException("岛训练执行与参考分量或未知停止边界不一致，拒绝计时。");
        var bonds = reference.DeckAfter.OrderBy(card => card.Position).Select(card => card.Bond).ToArray();
        if (!execution.State.GetSection(PioneerStateSection.SupportBonds).SequenceEqual(bonds))
            throw new InvalidOperationException("岛训练羁绊结果与参考分量不一致。");
        var grants = reference.TemporaryGrants.SelectMany(grant => new[]
            { grant.SourceTurn, (int)grant.Source, grant.StoryId ?? 0, grant.Position, grant.EffectType, grant.Value }).ToArray();
        if (!execution.State.GetSection(PioneerStateSection.TemporaryGrants).SequenceEqual(grants))
            throw new InvalidOperationException("岛训练临时效果原子与参考分量不一致。");
        if (result != SimulationRunner.Continue(scenario, script, ref execution)
            || root.Header.Resources != Start.State.Values || root.Header.Tickets != Start.State.Tickets)
            throw new InvalidOperationException("岛训练停止被重复结算，或修改了原始起点。");
        var cursor = script.CreateCursor();
        var memory = SimulationRunner.GetMemoryPlan(scenario, script, root, cursor);
        Console.WriteLine($"IslandExecution sharedBytes={memory.SharedBytes}, workspaceBytes={memory.WorkspaceBytes}, reservedBytes={memory.ReservedBytes}, scope=IslandActionComponent");
        return new(scenario, root, script, execution.State);
    }
}
