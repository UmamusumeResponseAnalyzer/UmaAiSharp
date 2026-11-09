using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser, DisassemblyDiagnoser(maxDepth: 2)]
public class TrainingTurnDecisionBenchmarks
{
    [Params("ordinary", "camp")] public string Case { get; set; } = "ordinary";
    PreparedExecution prepared = null!;
    TrajectoryExecution<PioneerState, PioneerRuntimeAction, ScriptCursor> execution;
    int command;

    [GlobalSetup]
    public void Setup()
    {
        (prepared, command) = TrainingTurnFixture.Prepare(Case);
        execution = SimulationRunner.CreateExecution(prepared.Scenario, prepared.Script, prepared.Root, 71, 0);
    }

    [Benchmark] public TrainingActionPreview Preview() => prepared.Scenario.PreviewTraining(prepared.Root, command);

    [Benchmark]
    public TrajectoryResult<TerminalMetrics> Execute()
    {
        SimulationRunner.ResetSample(prepared.Scenario, prepared.Script, prepared.Root, ref execution, 71, 0);
        return SimulationRunner.Continue(prepared.Scenario, prepared.Script, ref execution);
    }
}

public class TrainingTurnBatchBenchmarks
{
    [Params("ordinary", "camp")] public string Case { get; set; } = "ordinary";
    [Params(64, 4096)] public int Samples { get; set; }
    [Params(1, 2, 4, 8, 16, 32)] public int Workers { get; set; }
    PreparedExecution prepared = null!;
    [GlobalSetup] public void Setup() => prepared = TrainingTurnFixture.Prepare(Case).Execution;
    [Benchmark] public SimulationBatch<TerminalMetrics> ExecuteBatch()
        => SimulationRunner.RunBatch(prepared.Scenario, prepared.Script, prepared.Root, 71, Samples, Workers);
}

static class TrainingTurnFixture
{
    sealed record Fixture(string Name, int SourcePacket, PioneerStart.TrainingTurn Start, int Command,
        TrainingValues Common, TrainingValues Scenario, int DevelopmentPointGain);

    public static (PreparedExecution Execution, int Command) Prepare(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "training-turns.json"));
        const string hash = "c4fa4d7b4851f7222ded04bc766cb93aeb0e7264f30390b841315c59f3cad468";
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("训练行动基准夹具与冻结来源不一致。");
        var fixture = JsonSerializer.Deserialize<Fixture[]>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Single(value => value.Name == name);
        var data = PioneerRuntimeData.Compile(fixture.Start, [new PioneerConditionalOutcome.TrainingResult(0, true)], hash);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Train(fixture.Command))], 0);
        var preview = scenario.PreviewTraining(root, fixture.Command);
        if (preview.Gains.Common != fixture.Common || preview.Gains.Scenario != fixture.Scenario)
            throw new InvalidOperationException("训练行动基准预览与实包不一致。");
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        if (result.Stop?.Code != (int)PioneerStopCode.TrainingEventsRequired || result.Progress.ConsumedActions != 1
            || execution.State.Header.DevelopmentPoints != root.Header.DevelopmentPoints + fixture.DevelopmentPointGain
            || execution.State.Header.FutureOverflow != preview.Gains.Overflow)
            throw new InvalidOperationException("训练行动基准改变了结算或停止范围。");
        return (new(scenario, root, script), fixture.Command);
    }
}
