using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

// This is the resolved effect of story 501007525, not its occurrence or target-selection probability.
public sealed record GoldShipTrainingBoost(int CommandId);
public readonly record struct TrainingEnvironment(int Motivation, int Fans, TrainingValues Current, TrainingLimits Limits,
    TrainingValues GrowthPercent, GoldShipTrainingBoost? GoldShipBoost);
public readonly record struct TrainingFacilityEffect(int Type, int Condition, int Value, int Target);
public sealed record TrainingFacility(int Id, int StateId, int Level, ImmutableArray<TrainingFacilityEffect> Effects);
public sealed record CampTrainingInput(int CommandId, int Level, int GuestCount, TrainingEnvironment Environment,
    ImmutableArray<TrainingSupport> Supports, TrainingValues Base, ImmutableArray<TrainingFacility> Facilities);
public sealed record IslandTrainingAssignment(int FacilityId, ImmutableArray<TrainingSupport> Supports, int GuestCount);
public sealed record IslandTrainingInput(TrainingEnvironment Environment, ImmutableArray<int> OrdinaryLevels,
    TrainingValues Base, ImmutableArray<TrainingFacility> Facilities, ImmutableArray<IslandTrainingAssignment> Assignments);

internal static class TrainingFacilityRules
{
    public static void Validate(ImmutableArray<TrainingFacility> facilities)
    {
        var mask = 0;
        if (facilities.Length == 6)
            foreach (var facility in facilities)
                if (facility.Id is >= 101 and <= 106) mask |= 1 << (facility.Id - 101);
        if (mask != 63)
            throw new ArgumentException("需要六个明确的设施状态，未建设也必须保留level=0状态。");
        foreach (var facility in facilities)
            foreach (var effect in facility.Effects)
                if (effect.Type is not (1 or 2 or 101 or 102 or 103 or 104 or 201 or 204 or 205 or 206))
                    throw new UnknownMechanismException($"设施{facility.Id}的效果type={effect.Type}没有训练候选定义。");
    }

    public static int Command(int facility) => facility switch
    {
        101 => 101, 102 => 105, 103 => 102, 104 => 103, 105 => 106, 106 => -1,
        _ => throw new UnknownMechanismException($"未知岛训练设施{facility}。")
    };
    public static int PrimaryTarget(int command) => command switch
    {
        101 => 1, 105 => 2, 102 => 3, 103 => 4, 106 => 5,
        _ => throw new UnknownMechanismException($"黄金船效果包含未核验的训练目标{command}。")
    };
    static readonly int[][] EffectPercents = [[60, 0, 40, 0, 0, 50], [0, 50, 0, 30, 0, 70],
        [0, 30, 60, 0, 0, 70], [30, 0, 30, 50, 0, 70], [45, 0, 0, 0, 100, 70], [20, 15, 20, 15, 15, 70]];
    public static int EffectPercent(int facility, int component) => EffectPercents[facility - 101][component];
    public static bool HasTarget(int facility, int target) => facility switch
    {
        101 => target is 1 or 3 or 30, 102 => target is 2 or 4 or 30,
        103 => target is 2 or 3 or 30, 104 => target is 1 or 3 or 4 or 30,
        105 => target is 1 or 5 or 30, 106 => target is 1 or 2 or 3 or 4 or 5 or 30,
        _ => throw new UnknownMechanismException($"未知岛训练设施{facility}。")
    };
}
