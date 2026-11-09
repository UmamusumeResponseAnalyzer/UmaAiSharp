using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using System.Text.Json;
using UmaAiSharp.Core;

namespace UmaAiSharp.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args is ["allocation-check", var allocationOutput])
        {
            var rows = HotPathAllocationChecks.Run();
            using var allocationStream = new FileStream(allocationOutput, FileMode.CreateNew);
            JsonSerializer.Serialize(allocationStream, new { rows, avx512 = ExecutionPlatform.HasAvx512 },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            return;
        }
        if (args is ["profile", var caseName, var output])
        {
            using var stream = new FileStream(output, FileMode.CreateNew);
            var cold = caseName == "island" ? null : ExecutionBatchAllocation.MeasureColdCompilation(caseName);
            var rows = (from samples in new[] { 64, 4096 }
                        from workers in new[] { 1, 2, 4, 8, 16, 32 }
                        select caseName == "island" ? IslandBatchAllocation.Measure(samples, workers)
                            : ExecutionBatchAllocation.Measure(caseName, samples, workers)).ToArray();
            foreach (var group in rows.GroupBy(row => row.Samples))
                if (group.Select(row => row.ResultChecksum).Distinct().Count() != 1)
                    throw new InvalidDataException("不同worker数量改变了批量随机流末态。");
            JsonSerializer.Serialize(stream, new { cold, rows, avx512 = ExecutionPlatform.HasAvx512 },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args,
            ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.Default
            .WithArguments([new MsBuildArgument("/p:WarningsNotAsErrors=NU1900")])
            .DontEnforcePowerPlan().AsDefault()));
    }
}
