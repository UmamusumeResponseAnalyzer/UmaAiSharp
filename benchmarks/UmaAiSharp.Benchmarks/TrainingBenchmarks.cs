using System.Text.Json;
using BenchmarkDotNet.Attributes;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class TrainingBenchmarks
{
    [Params("ordinary-empty", "ordinary-many", "ordinary-upper")]
    public string Case { get; set; } = "ordinary-empty";
    TrainingFormulaInput input = null!;
    CompiledTrainingDeck compiled = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ordinary-inputs.json")));
        var raw = document.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == Case)
            .GetProperty("input").GetRawText();
        input = JsonSerializer.Deserialize<TrainingFormulaInput>(raw, options)!;
        compiled = new(input.Supports);
        var current = TrainingFormulaCandidate.GenerateOrdinary(input);
        var numeric = TrainingNumericKernel.GenerateOrdinary(input);
        if (current.Common != numeric.Common || current.Scenario != numeric.Scenario)
            throw new InvalidDataException("精确参考核与数值核不一致，拒绝比较性能。");
    }

    [Benchmark(Baseline = true)]
    public TrainingFormulaPrediction ExactAuditReference() => TrainingFormulaCandidate.GenerateOrdinary(input);

    [Benchmark]
    public TrainingNumericResult Numeric() => TrainingNumericKernel.GenerateOrdinary(input);

    [Benchmark]
    public TrainingNumericResult CompiledNumeric() => TrainingNumericKernel.GenerateOrdinary(input, compiled);
}
