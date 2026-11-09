using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser, DisassemblyDiagnoser(maxDepth: 2)]
public class ConstructionBenchmarks
{
    [Params("threshold-200", "threshold-800")] public string Case { get; set; } = "threshold-200";
    PioneerStart.TrainingTurn start = null!;
    PioneerMasterData master = null!;
    PioneerScenario scenario = null!;
    PioneerState root = null!;
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> script = null!;
    readonly ImmutableArray<PioneerConditionalOutcome> outcomes = [new PioneerConditionalOutcome.TrainingResult(0, true)];
    TrajectoryExecution<PioneerState, PioneerRuntimeAction, ScriptCursor> middle, execution;

    [GlobalSetup]
    public void Setup()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("name").GetString() == Case);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false), new PlanConverter() },
        };
        start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(options)!;
        using var masterRows = File.OpenText(Path.Combine(AppContext.BaseDirectory, "Fixtures/construction-master.jsonl"));
        master = PioneerMasterData.ReadExport(masterRows, fixture.RootElement.GetProperty("masterSha256").GetString()!);
        var data = Compile();
        scenario = new(data); root = data.CreateInitialState();
        script = new(row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(options), 0);
        middle = SimulationRunner.CreateExecution(scenario, script, root, 83, 0);
        execution = SimulationRunner.CreateExecution(scenario, script, root, 83, 0);
        var first = SimulationRunner.Advance(scenario, script, ref middle);
        if (first.Status != TrajectoryStatus.Advanced || middle.State.Header.Stage != PioneerStage.ConstructionEvent)
            throw new InvalidDataException("建设基准未到达第二次决策。");
        var result = FullSegment();
        var expected = row.GetProperty("afterConstruction");
        if (result.Stop?.Code != (int)PioneerStopCode.ConstructionRemainderUnknown
            || result.Progress.ConsumedActions != 2 || result.Progress.SettledActions != 1
            || execution.State.Header.Tickets != expected.GetProperty("tickets").GetInt32()
            || !execution.State.GetSection(PioneerStateSection.FacilityStates).SequenceEqual(expected.GetProperty("facilities").EnumerateArray()
                .SelectMany(value => new[] { value.GetProperty("facility_id").GetInt32(), value.GetProperty("state_id").GetInt32() }).ToArray()))
            throw new InvalidDataException("建设基准与实包设施、票数或停止位置不一致。");
    }

    [Benchmark]
    public PioneerRuntimeData Compile() => PioneerRuntimeData.Compile(start, outcomes, "construction-benchmark", master);

    [Benchmark]
    public TrajectoryResult<TerminalMetrics> ConstructionStep()
    {
        SimulationRunner.ForkInto(scenario, script, middle, ref execution);
        return SimulationRunner.Continue(scenario, script, ref execution);
    }

    [Benchmark]
    public TrajectoryResult<TerminalMetrics> FullSegment()
    {
        SimulationRunner.ResetSample(scenario, script, root, ref execution, 83, 0);
        return SimulationRunner.Continue(scenario, script, ref execution);
    }

    sealed class PlanConverter : JsonConverter<PioneerConstructionPlan>
    {
        public override PioneerConstructionPlan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            return new(value.RootElement.GetProperty("steps").Deserialize<PlannedUpgrade[]>(options)!);
        }
        public override void Write(Utf8JsonWriter writer, PioneerConstructionPlan value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
