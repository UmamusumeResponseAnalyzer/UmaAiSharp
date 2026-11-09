using BenchmarkDotNet.Attributes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Benchmarks;

[MemoryDiagnoser]
public class PlacementBenchmarks
{
    [Params(0, 6, 12)] public int Npcs { get; set; }
    PioneerJointPlacement model = null!;
    SupportPlacement[] placements = null!;
    SimulationRandom random;
    internal ulong RandomState => random.State;

    [GlobalSetup]
    public void Setup()
    {
        ExecutionPlatform.EnsureSupported();
        var members = Enumerable.Range(1, 5).Select(position => new PlacementMember(position, true,
            new(new(PlacementMemberKind.Training, position - 1, false, 10, 80, 0, 0, 60, 0, 0, 0, false, 30, [])))).ToList();
        members.Add(new(6, true, new(new(PlacementMemberKind.Tucker, -1, false, 10, 0, 0, 0, 0, 0, 0, 0, false, 30, []))));
        var npc = new PioneerPlacementDistribution(new(PlacementMemberKind.ProjectNpc, -1, false, 10, 0, 0, 0, 0, 0, 0, 0, false, 30, []));
        members.AddRange(Enumerable.Range(1001, Npcs).Select(id => new PlacementMember(id, true, npc)));
        model = new([.. members]); placements = new SupportPlacement[members.Count]; random = new(821);
        for (var sample = 0; sample < 100; sample++)
        {
            var board = model.Sample(placements, ref random);
            for (var training = 0; training < 5; training++)
                if (board[training].SupportCount + board[training].GuestCount > 5)
                    throw new InvalidOperationException("基准面板超过训练人数上限。");
        }
    }

    [Benchmark] public TrainingBoard GenerateBoard() => model.Sample(placements, ref random);
}
