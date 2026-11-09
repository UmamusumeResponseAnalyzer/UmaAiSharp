using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
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
    object baselineInput = null!;
    Func<object, object> baseline = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ordinary-inputs.json")));
        var raw = document.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == Case)
            .GetProperty("input").GetRawText();
        input = JsonSerializer.Deserialize<TrainingFormulaInput>(raw, options)!;
        compiled = new(input.Supports);
        var root = FindRoot();
        var path = Path.Combine(root, "artifacts", "performance", "engine-baseline-01");
        var context = new AssemblyLoadContext("frozen-training-baseline");
        // 固定历史基线的程序集和类型名保持采集时的名称。
        context.Resolving += (_, name) => name.Name is "UmaSharpAi.Core" or "UmaSharpAi.Pioneer"
            ? context.LoadFromAssemblyPath(Path.Combine(path, name.Name + ".dll")) : null;
        var assembly = context.LoadFromAssemblyPath(Path.Combine(path, "UmaSharpAi.Pioneer.dll"));
        var inputType = assembly.GetType("UmaSharpAi.Pioneer.Training.TrainingFormulaInput", true)!;
        baselineInput = JsonSerializer.Deserialize(raw, inputType, options)!;
        var method = assembly.GetType("UmaSharpAi.Pioneer.Training.TrainingFormulaCandidate", true)!
            .GetMethod("GenerateOrdinary", BindingFlags.Static | BindingFlags.Public)!;
        var parameter = Expression.Parameter(typeof(object));
        baseline = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Call(method,
            Expression.Convert(parameter, inputType)), typeof(object)), parameter).Compile();
        var old = JsonSerializer.SerializeToElement(baseline(baselineInput));
        var current = TrainingFormulaCandidate.GenerateOrdinary(input);
        var numeric = TrainingNumericKernel.GenerateOrdinary(input);
        if (old.GetProperty("Common").Deserialize<TrainingValues>() != numeric.Common
            || old.GetProperty("Scenario").Deserialize<TrainingValues>() != numeric.Scenario
            || current.Common != numeric.Common || current.Scenario != numeric.Scenario)
            throw new InvalidDataException("训练基线、参考核与数值核不一致，拒绝比较性能。");
    }

    [Benchmark(Baseline = true)]
    public object FrozenAuditBaseline() => baseline(baselineInput);

    [Benchmark]
    public TrainingFormulaPrediction ExactAuditReference() => TrainingFormulaCandidate.GenerateOrdinary(input);

    [Benchmark]
    public TrainingNumericResult Numeric() => TrainingNumericKernel.GenerateOrdinary(input);

    [Benchmark]
    public TrainingNumericResult CompiledNumeric() => TrainingNumericKernel.GenerateOrdinary(input, compiled);

    internal static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "UmaAiSharp.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到基准所对应的UmaAiSharp工作区。");
    }
}
