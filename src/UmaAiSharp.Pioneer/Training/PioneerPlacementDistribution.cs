using System.Collections.Immutable;
using System.Numerics;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public enum PlacementMemberKind { Training, Tucker, ProjectNpc }
public readonly record struct SupportPlacement(int TrainingIndex, bool HasHint);
public readonly record struct TemporarySpecialtyEffect(int SourceActionTurn, int Value);
public sealed record PlacementInput(PlacementMemberKind Kind, int SpecialtyIndex, bool Camp, int SeaHousePercent,
    int SpecialtyPercent, int UniqueSpecialtyPercent, int FacilitySpecialtyPercent,
    int HintPercent, int UniqueHintPercent, int CheckpointHintPercent, int FacilityHintPercent, bool GuaranteedHint,
    int Turn, ImmutableArray<TemporarySpecialtyEffect> TemporaryEffects);

// Tables are compiled for a card and its current effects, then shared by trajectory workers.
public sealed class PioneerPlacementDistribution
{
    public const string Version = "pioneer-placement-20261108-v2";
    public PlacementMemberKind Kind { get; }
    public ImmutableArray<ulong> Weights { get; }
    public ulong TotalWeight { get; }
    public ulong HintNumerator { get; }
    public ulong HintDenominator { get; }
    public long SharedMemoryBytes => 160;

    public PioneerPlacementDistribution(PlacementInput input)
    {
        Kind = input.Kind;
        if (!Enum.IsDefined(input.Kind) || input.Turn < 1 || input.TemporaryEffects.IsDefault
            || input.SpecialtyPercent < 0 || input.UniqueSpecialtyPercent < 0 || input.FacilitySpecialtyPercent < 0
            || input.HintPercent < 0 || input.UniqueHintPercent < 0 || input.CheckpointHintPercent < 0 || input.FacilityHintPercent < 0)
            throw new ArgumentException("人员分布输入要求明确的卡类型、回合、非负效果及临时效果数组。", nameof(input));
        if (input.Kind == PlacementMemberKind.Training ? input.SpecialtyIndex is < 0 or > 4 : input.SpecialtyIndex != -1)
            throw new ArgumentException("普通支援的得意位置为0..4，Tucker及PJ成员使用-1。", nameof(input));
        if (input.Kind == PlacementMemberKind.ProjectNpc && (input.SpecialtyPercent != 0 || input.UniqueSpecialtyPercent != 0
            || input.FacilitySpecialtyPercent != 0 || input.HintPercent != 0 || input.UniqueHintPercent != 0
            || input.CheckpointHintPercent != 0 || input.FacilityHintPercent != 0 || input.GuaranteedHint || !input.TemporaryEffects.IsEmpty))
            throw new ArgumentException("PJ成员位置分布不接受支援固有、Hint或临时得意率效果。", nameof(input));

        // Exact conditional-frequency estimates: 400*absent/non-specialty, or 500*absent/present for Tucker.
        var (absent, other, scale) = (input.Kind, input.Camp, input.SeaHousePercent) switch
        {
            (PlacementMemberKind.Training, false, 0) => (571, 3998, 400),
            (PlacementMemberKind.Training, false, 10) => (3547, 28099, 400),
            (PlacementMemberKind.Training, true, 10) => (357, 4811, 400),
            (PlacementMemberKind.Tucker, false, 0) => (140, 662, 500),
            (PlacementMemberKind.Tucker, false, 10) => (1634, 7534, 500),
            (PlacementMemberKind.Tucker, true, 10) => (205, 1380, 500),
            (PlacementMemberKind.ProjectNpc, false, 0) => (492, 4416, 500),
            (PlacementMemberKind.ProjectNpc, false, 10) => (9739, 87848, 500),
            (PlacementMemberKind.ProjectNpc, true, 10) => (1584, 24255, 500),
            _ => throw new UnknownMechanismException($"人员分布参数表缺少{input.Kind}、合宿={input.Camp}、海之家={input.SeaHousePercent}的分层。")
        };
        BigInteger specialtyNumerator = (100L + input.SpecialtyPercent) * (100L + input.UniqueSpecialtyPercent);
        BigInteger specialtyDenominator = 100;
        specialtyNumerator *= 100L + input.FacilitySpecialtyPercent;
        specialtyDenominator *= 100;
        foreach (var effect in input.TemporaryEffects)
        {
            if (effect.SourceActionTurn < 1 || effect.SourceActionTurn > input.Turn || effect.Value < 0)
                throw new ArgumentException("临时得意率来源行动不得晚于当前回合，增量须非负。", nameof(input));
            if (effect.SourceActionTurn != input.Turn - 1) continue;
            specialtyNumerator *= 100L + effect.Value;
            specialtyDenominator *= 100;
        }
        var weights = new BigInteger[6];
        for (var index = 0; index < 5; index++) weights[index] = 100 * specialtyDenominator * other;
        if (input.Kind == PlacementMemberKind.Training) weights[input.SpecialtyIndex] = specialtyNumerator * other;
        weights[5] = scale * absent * specialtyDenominator;
        var divisor = weights.Aggregate(BigInteger.GreatestCommonDivisor);
        Weights = [.. weights.Select(weight => checked((ulong)(weight / divisor)))];
        var total = 0UL;
        foreach (var weight in Weights) total = checked(total + weight);
        TotalWeight = total;

        var hintNumerator = input.Kind != PlacementMemberKind.Training ? BigInteger.Zero : input.GuaranteedHint ? 20_000_000
            : (5000 + (BigInteger)(100L + input.HintPercent) * (100L + input.UniqueHintPercent))
                * (100L + input.CheckpointHintPercent + input.FacilityHintPercent);
        BigInteger hintDenominator = 20_000_000;
        hintNumerator = BigInteger.Min(hintNumerator, hintDenominator);
        var hintDivisor = BigInteger.GreatestCommonDivisor(hintNumerator, hintDenominator);
        HintNumerator = checked((ulong)(hintNumerator / hintDivisor));
        HintDenominator = checked((ulong)(hintDenominator / hintDivisor));
    }

    public SupportPlacement Sample(bool appeared, ref SimulationRandom random)
    {
        var index = SamplePosition(appeared, ref random);
        return new(index, index >= 0 && SampleHint(ref random));
    }

    internal int SamplePosition(bool appeared, ref SimulationRandom random)
    {
        if (!appeared) return -1;
        var draw = random.NextUInt64(TotalWeight);
        var index = 0;
        while (draw >= Weights[index]) draw -= Weights[index++];
        return index == 5 ? -1 : index;
    }

    internal bool SampleHint(ref SimulationRandom random) => HintNumerator == HintDenominator
        || HintNumerator > 0 && random.NextUInt64(HintDenominator) < HintNumerator;
}
