using System.Collections.Immutable;
using System.Numerics;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public sealed record TrainingFormulaInput(int CommandId, int Level, int Motivation, int Fans, int GuestCount,
    TrainingValues Current, TrainingLimits Limits, TrainingValues GrowthPercent,
    ImmutableArray<TrainingSupport> Supports, int EvaluationTrainingPercent, GoldShipTrainingBoost? GoldShipBoost);
public sealed record ExactTrainingGain(int TargetType, string Numerator, string Denominator, int TruncatedBeforeLimit);
public sealed record TrainingFormulaPrediction(TrainingValues Common, TrainingValues Scenario,
    ImmutableArray<ExactTrainingGain> RawGains);

public static class TrainingFailureRate
{
    public const string Version = "jp-failure-preview-v2";

    public static int StatusBias(IEnumerable<int> effects)
    {
        var bias = 0;
        foreach (var effect in effects)
            bias += StatusBias(effect);
        return bias;
    }

    public static int StatusBias(int effect) => effect switch
    {
        10 or 13 => -2,
        11 => -4,
        12 => 2,
        1 or 3 or 5 or 7 or 8 or 9 or 25 or 26 or 27 or 31 or 36 => 0,
        _ => throw new UnknownMechanismException($"角色效果{effect}不在失败率预览已核验集合中。")
    };

    // Produces the displayed percentage; actual outcome probabilities and failure penalties are separate rules.
    public static int Calculate(int masterCoefficient, int vital, IEnumerable<int> reductions, int statusBias)
    {
        if (masterCoefficient < 0 || vital < 0) throw new ArgumentOutOfRangeException(nameof(masterCoefficient));
        var numerator = BigInteger.Zero;
        var denominator = new BigInteger(400);
        if ((long)vital * 10 < masterCoefficient)
            numerator = BigInteger.Max(0, BigInteger.Min(99 * denominator,
                new BigInteger(100L - vital) * (masterCoefficient - 10L * vital)));
        foreach (var reduction in reductions)
        {
            if (reduction is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(reductions));
            numerator *= 100 - reduction;
            denominator *= 100;
        }
        var rate = (int)((numerator + denominator - 1) / denominator);
        return (int)Math.Clamp((long)rate + statusBias, 0, 100);
    }

    internal static int CalculateForSupports(int coefficient, int vital, ReadOnlySpan<ResolvedTrainingSupport> supports, int bias)
    {
        var numerator = (long)vital * 10 < coefficient
            ? BigInteger.Clamp(new BigInteger(100L - vital) * (coefficient - 10L * vital), 0, 99 * 400) : BigInteger.Zero;
        var denominator = new BigInteger(400);
        foreach (var support in supports)
            foreach (var reduction in support.FailureReductions)
            {
                if (reduction is < 0 or > 100) throw new InvalidDataException("训练失败率减免必须在0..100内。");
                numerator *= 100 - reduction;
                denominator *= 100;
            }
        return (int)Math.Clamp((long)((numerator + denominator - 1) / denominator) + bias, 0, 100);
    }
}

public static class TrainingFormulaCandidate
{
    public const string Version = "jp-training-refit14-d6x3-r5-candidate-v5";

    public static TrainingFormulaPrediction GenerateOrdinary(TrainingFormulaInput input)
    {
        var environment = new TrainingEnvironment(input.Motivation, input.Fans, input.Current, input.Limits,
            input.GrowthPercent, input.GoldShipBoost);
        Validate(environment);
        if (input.GuestCount < 0 || input.EvaluationTrainingPercent < 0)
            throw new ArgumentException("训练人数和评估会加成不能为负数。");
        var basis = TrainingBaseTable.GetOrdinary(input.CommandId, input.Level);
        var supports = Resolve(input.Supports, input.CommandId, input.Level, input.Fans);
        var training = (long)supports.Sum(card => card.TrainingEffect)
            + (input.GoldShipBoost?.CommandId == input.CommandId ? 20 : 0);
        var factors = OrdinaryFactors(environment, basis, supports, input.GuestCount, training, 0);
        return Predict(environment, factors, Enumerable.Repeat(input.EvaluationTrainingPercent, 6).ToArray(),
            false, Vital(basis.Vital, supports));
    }

    public static TrainingFormulaPrediction GenerateCamp(CampTrainingInput input)
    {
        Validate(input.Environment);
        TrainingFacilityRules.Validate(input.Facilities);
        if (input.Environment.GoldShipBoost is not null)
            throw new UnknownMechanismException("黄金船指定训练效果在合宿中的适用方式没有核验。");
        if (input.GuestCount < 0 || input.Level != 5)
            throw new UnknownMechanismException("合宿训练要求已核验的level=5及非负队员人数。");
        var command = input.CommandId switch
        {
            3601 => 101, 3602 => 105, 3603 => 102, 3604 => 103, 3605 => 106,
            _ => throw new UnknownMechanismException($"不是已核验的合宿训练command_id={input.CommandId}。")
        };
        var correspondingFacility = input.CommandId - 3500;
        if (input.Facilities.Single(facility => facility.Id == correspondingFacility).Level == 0)
            throw new UnknownMechanismException("合宿对应设施未建设的收益没有当前完整预览核验覆盖。");
        var supports = Resolve(input.Supports, command, input.Level, input.Environment.Fans);
        var active = input.Facilities.SelectMany(facility => facility.Effects).ToArray();
        var bases = Attributes(input.Base);
        for (var i = 0; i < 6; i++)
            if (bases[i] > 0) bases[i] = checked(bases[i] + active.Where(effect => effect.Type == 1 && effect.Target == Target(i)).Sum(effect => effect.Value));
        var factors = OrdinaryFactors(input.Environment, Values(bases, input.Base.Vital), supports,
            input.GuestCount, supports.Sum(card => card.TrainingEffect),
            (long)supports.Count(card => card.FriendshipEligible) * active.Where(effect => effect.Type == 103).Sum(effect => effect.Value));
        var percent = active.Where(effect => effect.Type == 2 && effect.Condition == 2 && effect.Target == input.CommandId)
            .Sum(effect => effect.Value);
        return Predict(input.Environment, factors, Enumerable.Repeat(percent, 6).ToArray(), true, Vital(input.Base.Vital, supports));
    }

    public static TrainingFormulaPrediction GenerateIsland(IslandTrainingInput input)
    {
        Validate(input.Environment);
        TrainingFacilityRules.Validate(input.Facilities);
        if (input.OrdinaryLevels.Length != 5 || input.OrdinaryLevels.Any(level => level is < 1 or > 5))
            throw new ArgumentException("岛训练需要五项普通训练的明确等级。");
        if (input.Assignments.Select(group => group.FacilityId).Distinct().Count() != input.Assignments.Length
            || input.Assignments.Any(group => group.FacilityId is < 101 or > 106 || group.GuestCount < 0))
            throw new ArgumentException("岛训练设施分配重复或范围无效。");
        var positions = input.Assignments.SelectMany(group => group.Supports).Select(card => card.Position).ToArray();
        if (positions.Distinct().Count() != positions.Length)
            throw new ArgumentException("同一支援位置不能重复参加岛训练。");
        var members = input.Assignments.SelectMany(group => Resolve(group.Supports,
            TrainingFacilityRules.Command(group.FacilityId), input.OrdinaryLevels.Max(), input.Environment.Fans)
            .Select(card => (group.FacilityId, Card: card))).ToArray();
        var active = input.Facilities.SelectMany(facility => facility.Effects).ToArray();
        var friendCount = members.Count(member => member.Card.FriendshipEligible);
        var friendFacilities = members.Where(member => member.Card.FriendshipEligible).Select(member => member.FacilityId).Distinct().Count();
        var heads = new TrainingRational(100L + input.Assignments.Sum(group => group.FacilityId == 106
            ? group.Supports.Length : 5L * group.Supports.Length + 2L * group.GuestCount), 100);
        var house = new TrainingRational(10000L + (long)friendCount * active.Where(effect => effect.Type == 103).Sum(effect => effect.Value), 10000);
        var bases = Attributes(input.Base);
        var growth = Attributes(input.Environment.GrowthPercent);
        var factors = new TrainingFactors[6];
        var percents = new int[6];
        for (var i = 0; i < 6; i++)
        {
            var target = Target(i);
            var basis = checked(bases[i] + active.Where(effect => effect.Type == 1 && effect.Target == target).Sum(effect => effect.Value)
                + members.Sum(member => Attributes(member.Card.BaseBonuses)[i]));
            TrainingRational friendship = 1;
            var training = 0L;
            var motivation = 0L;
            foreach (var member in members)
            {
                var scale = TrainingFacilityRules.EffectPercent(member.FacilityId, i);
                if (member.Card.FriendshipEligible)
                    foreach (var bonus in member.Card.FriendshipBonuses)
                        friendship *= new TrainingRational(100L + (long)bonus * scale / 100, 100);
                training = checked(training + member.Card.TrainingEffectParts.Sum(value => (long)value * scale / 100));
                motivation = checked(motivation + member.Card.MotivationEffectParts.Sum(value => (long)value * scale / 100));
            }
            if (input.Environment.GoldShipBoost is { } boost && TrainingFacilityRules.PrimaryTarget(boost.CommandId) == target)
                training += 20;
            factors[i] = new(basis, new(100L + growth[i], 100), friendship, new(100 + training, 100), heads, house,
                Motivation(input.Environment.Motivation, motivation));
            percents[i] = input.Facilities.Sum(facility => facility.Effects.Where(effect =>
                effect.Type == 2 && effect.Condition == 0 && TrainingFacilityRules.HasTarget(facility.Id, target)
                || effect.Type == 104 && effect.Condition == 0 && effect.Target == friendFacilities).Sum(effect => effect.Value));
        }
        return Predict(input.Environment, factors, percents, true, 0);
    }

    static TrainingFactors[] OrdinaryFactors(TrainingEnvironment environment, TrainingValues basis,
        ResolvedTrainingSupport[] supports, int guests, long training, long houseBasisPoints)
    {
        TrainingRational friendship = 1;
        foreach (var bonus in supports.Where(card => card.FriendshipEligible).SelectMany(card => card.FriendshipBonuses))
            friendship *= new TrainingRational(100L + bonus, 100);
        var bases = Attributes(basis);
        var growth = Attributes(environment.GrowthPercent);
        return Enumerable.Range(0, 6).Select(index => new TrainingFactors(
            bases[index] == 0 ? 0 : checked(bases[index] + supports.Sum(card => Attributes(card.BaseBonuses)[index])),
            new(100L + growth[index], 100), friendship, new(100 + training, 100),
            new(100L + 5L * ((long)guests + supports.Length), 100), new(10000 + houseBasisPoints, 10000),
            Motivation(environment.Motivation, supports.Sum(card => card.MotivationEffect)))).ToArray();
    }

    static TrainingRational Motivation(int motivation, long bonus) => new(checked(1000 + (motivation - 3) * (100 + bonus)), 1000);

    static TrainingFormulaPrediction Predict(TrainingEnvironment environment, TrainingFactors[] factors,
        int[] percent, bool capExtra, int vital)
    {
        var current = Attributes(environment.Current);
        var caps = new[] { environment.Limits.Speed, environment.Limits.Stamina, environment.Limits.Power, environment.Limits.Guts, environment.Limits.Wisdom };
        var common = new int[6];
        var scenario = new int[6];
        var exact = ImmutableArray.CreateBuilder<ExactTrainingGain>();
        for (var index = 0; index < 6; index++)
        {
            var f = factors[index];
            var raw = f.Base * f.Growth * f.Friendship * f.Training * f.Heads * f.House * f.Motivation;
            var value = (f.Base * f.Growth * f.Friendship * f.Training).Quantize(6, false);
            value = (value * f.Heads).Quantize(6, false);
            value = (value * f.House).Quantize(6, false);
            var integer = (value * f.Motivation).Quantize(5, true).Truncate();
            exact.Add(new(Target(index), raw.Numerator.ToString(), raw.Denominator.ToString(), integer));
            var lower = Math.Min(100, integer);
            var extra = checked((int)((long)lower * percent[index] / 100));
            if (capExtra) extra = Math.Min(100, extra);
            common[index] = index < 5 ? ConvertAttribute(current[index], caps[index], lower) : lower;
            scenario[index] = index < 5 ? ConvertAttribute(current[index] + common[index], caps[index], extra) : extra;
        }
        return new(Values(common, vital), Values(scenario, 0), exact.ToImmutable());
    }

    static ResolvedTrainingSupport[] Resolve(ImmutableArray<TrainingSupport> cards, int command, int level, int fans)
    {
        if (cards.Select(card => card.Position).Distinct().Count() != cards.Length)
            throw new ArgumentException("训练支援位置不能重复。");
        return cards.Select(card => TrainingSupportEffects.Resolve(card, command, level, fans)).ToArray();
    }

    static int Vital(int basis, ResolvedTrainingSupport[] supports)
    {
        if (basis > 0) return checked(basis + supports.Where(card => card.FriendshipEligible).Sum(card => card.WisdomRecovery));
        TrainingRational result = basis;
        foreach (var reduction in supports.SelectMany(card => card.VitalReductions))
            result *= new TrainingRational(100 - reduction, 100);
        return result.Truncate();
    }

    static void Validate(TrainingEnvironment environment)
    {
        var current = Attributes(environment.Current);
        var limits = environment.Limits;
        var caps = new[] { limits.Speed, limits.Stamina, limits.Power, limits.Guts, limits.Wisdom };
        if (environment.Motivation is < 1 or > 5 || environment.Fans < 0
            || Enumerable.Range(0, 5).Any(index => current[index] < 0 || current[index] > caps[index])
            || environment.Current.Vital < 0 || environment.Current.Vital > limits.Vital || environment.Current.SkillPoints < 0
            || Attributes(environment.GrowthPercent).Any(value => value < 0)
            || environment.GrowthPercent.SkillPoints != 0 || environment.GrowthPercent.Vital != 0)
            throw new ArgumentException("训练状态的干劲、粉丝、属性、体力或成长率超出范围。");
        if (environment.GoldShipBoost is { } boost) _ = TrainingFacilityRules.PrimaryTarget(boost.CommandId);
    }

    static int ConvertAttribute(int before, int cap, int gain)
    {
        var below = Math.Min(gain, Math.Max(0, 1200 - before));
        var above = gain - below;
        return Math.Min(below + (above > 0 ? Math.Max(1, above / 2) : 0), cap - before);
    }

    static int Target(int index) => index < 5 ? index + 1 : 30;
    static int[] Attributes(TrainingValues values) => [values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom, values.SkillPoints];
    static TrainingValues Values(int[] values, int vital) => new(values[0], values[1], values[2], values[3], values[4], vital, values[5]);
    readonly record struct TrainingFactors(TrainingRational Base, TrainingRational Growth, TrainingRational Friendship,
        TrainingRational Training, TrainingRational Heads, TrainingRational House, TrainingRational Motivation);
}
