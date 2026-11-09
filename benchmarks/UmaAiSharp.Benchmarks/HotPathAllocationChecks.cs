using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

public readonly record struct HotPathAllocation(string Case, int Samples, long AllocatedBytes, ulong RandomState);

public static class HotPathAllocationChecks
{
    public static HotPathAllocation[] Run()
    {
        ExecutionPlatform.EnsureSupported();
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
            throw new InvalidOperationException("严格分配验收要求在新进程启动前设置 DOTNET_TieredCompilation=0。");
        const int samples = 10_000;
        var rows = new List<HotPathAllocation>();
        var distribution = new PioneerPlacementDistribution(new(PlacementMemberKind.Training, 0, false,
            10, 80, 0, 0, 60, 0, 0, 0, false, 30, []));
        var random = new SimulationRandom(123);
        for (var index = 0; index < samples; index++) distribution.Sample(true, ref random);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < samples; index++) distribution.Sample(true, ref random);
        rows.Add(new("individual-placement", samples, GC.GetAllocatedBytesForCurrentThread() - before, random.State));
        foreach (var npcs in new[] { 0, 6, 12 })
        {
            var benchmark = new PlacementBenchmarks { Npcs = npcs };
            benchmark.Setup();
            for (var index = 0; index < samples; index++) benchmark.GenerateBoard();
            before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < samples; index++) benchmark.GenerateBoard();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            rows.Add(new($"joint-placement-{npcs}", samples, allocated, benchmark.RandomState));
        }
        var reward = new DeclaredEventRewards(new([new(1, 10, 32, 0), new(1, 11, 4, 0), new(1, 20, 1, 0), new(1, 1, 15, 0)]));
        var resources = new TrainingValues(1186, 1400, 1200, 100, 100, 99, 5);
        var limits = new TrainingLimits(2000, 1400, 2000, 2000, 2000, 100);
        for (var index = 0; index < samples; index++) reward.Apply(resources, limits, 3, true, true);
        before = GC.GetAllocatedBytesForCurrentThread();
        var result = default(EventResourceResult);
        for (var index = 0; index < samples; index++) result = reward.Apply(resources, limits, 3, true, true);
        rows.Add(new("event-resources", samples, GC.GetAllocatedBytesForCurrentThread() - before, 0));
        if (result.Resources.Speed != 1201 || result.Resources.Vital != 104 || result.Motivation != 3 || result.ConsumePositiveThinking)
            throw new InvalidDataException("事件分配验收的资源或状态结算不一致。");
        foreach (var row in rows)
            if (row.AllocatedBytes != 0)
                throw new InvalidDataException($"{row.Case} 的 {row.Samples} 次热执行分配了 {row.AllocatedBytes} B，要求为0 B。");
        return [.. rows];
    }
}
