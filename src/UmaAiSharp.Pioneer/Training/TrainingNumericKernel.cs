using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Pioneer.Training;

// Uncapped values are display units after the 1200 conversion, before attribute limits.
public readonly record struct TrainingNumericResult(TrainingValues Common, TrainingValues Scenario,
    TrainingValues UncappedCommon, TrainingValues UncappedScenario)
{
    public AttributeValues Overflow => new(
        checked(UncappedCommon.Speed - Common.Speed + UncappedScenario.Speed - Scenario.Speed),
        checked(UncappedCommon.Stamina - Common.Stamina + UncappedScenario.Stamina - Scenario.Stamina),
        checked(UncappedCommon.Power - Common.Power + UncappedScenario.Power - Scenario.Power),
        checked(UncappedCommon.Guts - Common.Guts + UncappedScenario.Guts - Scenario.Guts),
        checked(UncappedCommon.Wisdom - Common.Wisdom + UncappedScenario.Wisdom - Scenario.Wisdom));
}
public readonly record struct TrainingActionPreview(TrainingNumericResult Gains, int FailureRate, bool Friendship);
internal readonly record struct CompiledCampTraining(TrainingValues Base, int HouseBonus, int ScenarioPercent);

internal sealed record CompiledIslandFacilities(TrainingValues BaseExtra, int HouseBonus, TrainingValues[] ScenarioPercents)
{
    public const int SharedMemoryBytes = 320;
}

// Uses the same quantization and effect order as the audit kernel.
public static class TrainingNumericKernel
{
    public const string Version = TrainingFormulaCandidate.Version;

    public static TrainingNumericResult GenerateOrdinary(TrainingFormulaInput input) => Ordinary(input, null);

    public static TrainingNumericResult GenerateOrdinary(TrainingFormulaInput input, CompiledTrainingDeck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return Ordinary(input, deck);
    }

    static TrainingNumericResult Ordinary(TrainingFormulaInput input, CompiledTrainingDeck? deck)
    {
        var environment = new TrainingEnvironment(input.Motivation, input.Fans, input.Current, input.Limits,
            input.GrowthPercent, input.GoldShipBoost);
        Validate(environment);
        if (input.GuestCount < 0 || input.EvaluationTrainingPercent < 0)
            throw new ArgumentException("训练人数和评估会加成不能为负数。");
        var basis = TrainingBaseTable.GetOrdinary(input.CommandId, input.Level);
        var supports = Resolve(input.Supports, input.CommandId, input.Level, input.Fans, deck);
        return Ordinary(environment, basis, supports, input.GuestCount,
            input.GoldShipBoost?.CommandId == input.CommandId ? 20 : 0, 0, input.EvaluationTrainingPercent, false);
    }

    public static TrainingNumericResult GenerateCamp(CampTrainingInput input) => Camp(input, null);

    public static TrainingNumericResult GenerateCamp(CampTrainingInput input, CompiledTrainingDeck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return Camp(input, deck);
    }

    static TrainingNumericResult Camp(CampTrainingInput input, CompiledTrainingDeck? deck)
    {
        Validate(input.Environment);
        if (input.Environment.GoldShipBoost is not null)
            throw new UnknownMechanismException("黄金船指定训练效果在合宿中的适用方式没有核验。");
        if (input.GuestCount < 0) throw new ArgumentException("合宿训练队员人数不能为负数。");
        var compiled = CompileCamp(input.CommandId, input.Level, input.Base, input.Facilities);
        var command = TrainingBaseTable.OrdinaryCommand(input.CommandId);
        var supports = Resolve(input.Supports, command, input.Level, input.Environment.Fans, deck);
        var friends = 0;
        foreach (var support in supports) if (support.FriendshipEligible) friends++;
        return Ordinary(input.Environment, compiled.Base, supports, input.GuestCount,
            0, (long)friends * compiled.HouseBonus, compiled.ScenarioPercent, true);
    }

    internal static CompiledCampTraining CompileCamp(int command, int level, TrainingValues basis,
        ImmutableArray<TrainingFacility> facilities)
    {
        TrainingFacilityRules.Validate(facilities);
        if (command is < 3601 or > 3605 || level != 5)
            throw new UnknownMechanismException("合宿训练要求command_id=3601..3605及level=5。");
        var house = 0;
        var percent = 0;
        foreach (var current in facilities)
        {
            foreach (var effect in current.Effects)
            {
                if (effect.Type == 103) house = checked(house + effect.Value);
                if (effect.Type == 2 && effect.Condition == 2 && effect.Target == command)
                    percent = checked(percent + effect.Value);
            }
        }
        Span<int> values = stackalloc int[6];
        for (var i = 0; i < 6; i++)
        {
            values[i] = Attribute(basis, i);
            if (values[i] <= 0) continue;
            var extra = 0;
            foreach (var current in facilities)
                foreach (var effect in current.Effects)
                    if (effect.Type == 1 && effect.Target == Target(i)) extra = checked(extra + effect.Value);
            values[i] = checked(values[i] + extra);
        }
        return new(Values(values, basis.Vital), house, percent);
    }

    public static TrainingNumericResult GenerateIsland(IslandTrainingInput input) => Island(input, null);

    public static TrainingNumericResult GenerateIsland(IslandTrainingInput input, CompiledTrainingDeck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return Island(input, deck);
    }

    [InlineArray(6)]
    struct IslandMembers { public (int Facility, int Position, ResolvedTrainingSupport Support) First; }

    [InlineArray(6)]
    struct TrainingMembers { public ResolvedTrainingSupport First; }

    internal static TrainingActionPreview Preview(in TrainingEnvironment environment, int command, int level, int guests,
        int percent, int failureCoefficient, int failureBias, ReadOnlySpan<int> positions, ReadOnlySpan<int> bonds,
        CompiledTrainingDeck deck, CompiledCampTraining? camp)
    {
        var storage = new TrainingMembers();
        var supports = ((Span<ResolvedTrainingSupport>)storage)[..positions.Length];
        var friends = 0;
        var ordinaryCommand = TrainingBaseTable.OrdinaryCommand(command);
        for (var i = 0; i < positions.Length; i++)
        {
            supports[i] = deck.Resolve(positions[i], bonds[positions[i] - 1], ordinaryCommand, level, environment.Fans);
            if (supports[i].FriendshipEligible) friends++;
        }
        var failure = TrainingFailureRate.CalculateForSupports(failureCoefficient, environment.Current.Vital, supports, failureBias);
        var gains = Ordinary(environment, camp?.Base ?? TrainingBaseTable.GetOrdinary(command, level), supports, guests,
            environment.GoldShipBoost?.CommandId == command ? 20 : 0, (long)friends * (camp?.HouseBonus ?? 0),
            camp?.ScenarioPercent ?? percent, camp.HasValue);
        return new(gains, failure, friends != 0);
    }

    static TrainingNumericResult Island(IslandTrainingInput input, CompiledTrainingDeck? deck)
        => Island(input, deck, input.Environment, input.OrdinaryLevels.AsSpan(), []);

    internal static CompiledIslandFacilities CompileIslandFacilities(ImmutableArray<TrainingFacility> facilities)
    {
        TrainingFacilityRules.Validate(facilities);
        var house = 0;
        foreach (var facility in facilities)
            foreach (var effect in facility.Effects)
                if (effect.Type == 103) house = checked(house + effect.Value);
        Span<int> extras = stackalloc int[6];
        Span<int> percentages = stackalloc int[6];
        var rows = new TrainingValues[7];
        for (var friends = 0; friends < rows.Length; friends++)
        {
            for (var index = 0; index < 6; index++)
            {
                var target = Target(index);
                var extra = 0;
                var percent = 0;
                foreach (var facility in facilities)
                {
                    var facilityPercent = 0;
                    foreach (var effect in facility.Effects)
                    {
                        if (effect.Type == 1 && effect.Target == target) extra = checked(extra + effect.Value);
                        if (effect.Type == 2 && effect.Condition == 0 && TrainingFacilityRules.HasTarget(facility.Id, target)
                            || effect.Type == 104 && effect.Condition == 0 && effect.Target == friends)
                            facilityPercent = checked(facilityPercent + effect.Value);
                    }
                    percent = checked(percent + facilityPercent);
                }
                extras[index] = extra;
                percentages[index] = percent;
            }
            rows[friends] = Values(percentages, 0);
        }
        return new(Values(extras, 0), house, rows);
    }

    internal static TrainingNumericResult Island(IslandTrainingInput input, CompiledTrainingDeck? deck,
        in TrainingEnvironment environment, ReadOnlySpan<int> ordinaryLevels, ReadOnlySpan<int> bonds,
        CompiledIslandFacilities? compiledFacilities = null)
    {
        Validate(environment);
        if (compiledFacilities is null) TrainingFacilityRules.Validate(input.Facilities);
        if (ordinaryLevels.Length != 5)
            throw new ArgumentException("岛训练需要五项普通训练的明确等级。");
        var level = 1;
        foreach (var value in ordinaryLevels)
        {
            if (value is < 1 or > 5) throw new ArgumentException("岛训练需要五项普通训练的明确等级。");
            level = Math.Max(level, value);
        }
        var participants = 0;
        var weightedHeads = 0L;
        for (var g = 0; g < input.Assignments.Length; g++)
        {
            var group = input.Assignments[g];
            if (group.FacilityId is < 101 or > 106 || group.GuestCount < 0)
                throw new ArgumentException("岛训练设施分配重复或范围无效。");
            for (var previous = 0; previous < g; previous++)
                if (input.Assignments[previous].FacilityId == group.FacilityId)
                    throw new ArgumentException("岛训练设施分配重复或范围无效。");
            participants = checked(participants + group.Supports.Length);
            weightedHeads = checked(weightedHeads + (group.FacilityId == 106
                ? group.Supports.Length : 5L * group.Supports.Length + 2L * group.GuestCount));
        }
        if (participants > 6) throw new ArgumentException("岛训练最多包含六张编成支援。");
        var storage = new IslandMembers();
        var members = ((Span<(int Facility, int Position, ResolvedTrainingSupport Support)>)storage)[..participants];
        var offset = 0;
        var friendCount = 0;
        var friendFacilityMask = 0;
        foreach (var group in input.Assignments)
            foreach (var support in group.Supports)
            {
                for (var previous = 0; previous < offset; previous++)
                    if (members[previous].Position == support.Position)
                        throw new ArgumentException("同一支援位置不能重复参加岛训练。");
                var command = TrainingFacilityRules.Command(group.FacilityId);
                var resolved = !bonds.IsEmpty
                    ? deck!.Resolve(support.Position, bonds[support.Position - 1], command, level, environment.Fans)
                    : deck is null ? TrainingSupportEffects.Resolve(support, command, level, environment.Fans)
                    : deck.Resolve(support, command, level, environment.Fans);
                members[offset++] = (group.FacilityId, support.Position, resolved);
                if (!resolved.FriendshipEligible) continue;
                friendCount++;
                friendFacilityMask |= 1 << (group.FacilityId - 101);
            }
        var friendFacilities = System.Numerics.BitOperations.PopCount((uint)friendFacilityMask);
        var houseBonus = compiledFacilities?.HouseBonus ?? 0;
        if (compiledFacilities is null)
            foreach (var facility in input.Facilities)
                foreach (var effect in facility.Effects)
                    if (effect.Type == 103) houseBonus = checked(houseBonus + effect.Value);
        var heads = new TrainingFraction(100 + weightedHeads, 100);
        var house = new TrainingFraction(10000L + (long)friendCount * houseBonus, 10000);
        Span<int> common = stackalloc int[6];
        Span<int> scenario = stackalloc int[6];
        Span<int> uncappedCommon = stackalloc int[6];
        Span<int> uncappedScenario = stackalloc int[6];
        for (var i = 0; i < 6; i++)
        {
            var target = Target(i);
            var baseExtra = compiledFacilities is null ? 0 : Attribute(compiledFacilities.BaseExtra, i);
            var percent = compiledFacilities is null ? 0 : Attribute(compiledFacilities.ScenarioPercents[friendFacilities], i);
            if (compiledFacilities is null)
                foreach (var facility in input.Facilities)
                {
                    var facilityPercent = 0;
                    foreach (var effect in facility.Effects)
                    {
                        if (effect.Type == 1 && effect.Target == target) baseExtra = checked(baseExtra + effect.Value);
                        if (effect.Type == 2 && effect.Condition == 0 && TrainingFacilityRules.HasTarget(facility.Id, target)
                            || effect.Type == 104 && effect.Condition == 0 && effect.Target == friendFacilities)
                            facilityPercent = checked(facilityPercent + effect.Value);
                    }
                    percent = checked(percent + facilityPercent);
                }
            var cardBase = 0;
            TrainingFraction friendship = 1;
            var training = 0L;
            var motivation = 0L;
            foreach (var member in members)
            {
                var support = member.Support;
                cardBase = checked(cardBase + Attribute(support.BaseBonuses, i));
                var scale = TrainingFacilityRules.EffectPercent(member.Facility, i);
                if (support.FriendshipEligible)
                    foreach (var bonus in support.FriendshipBonuses)
                        friendship *= new TrainingFraction(100L + (long)bonus * scale / 100, 100);
                foreach (var value in support.TrainingEffectParts) training = checked(training + (long)value * scale / 100);
                foreach (var value in support.MotivationEffectParts) motivation = checked(motivation + (long)value * scale / 100);
            }
            if (environment.GoldShipBoost is { } boost && TrainingFacilityRules.PrimaryTarget(boost.CommandId) == target)
                training += 20;
            var basis = checked(Attribute(input.Base, i) + baseExtra + cardBase);
            var integer = Compute(basis, new(100L + Attribute(environment.GrowthPercent, i), 100), friendship,
                new(100 + training, 100), heads, house, Motivation(environment.Motivation, motivation));
            Convert(environment, i, integer, percent, true, common, scenario, uncappedCommon, uncappedScenario);
        }
        return new(Values(common, 0), Values(scenario, 0), Values(uncappedCommon, 0), Values(uncappedScenario, 0));
    }

    static TrainingNumericResult Ordinary(TrainingEnvironment environment, TrainingValues basis,
        ReadOnlySpan<ResolvedTrainingSupport> supports, int guests, int extraTraining, long houseBonus, int percent, bool capExtra)
    {
        TrainingFraction friendship = 1;
        var training = 0;
        var motivation = 0;
        var recovery = 0;
        TrainingFraction vitalFactor = 1;
        foreach (var support in supports)
        {
            training = checked(training + support.TrainingEffect);
            motivation = checked(motivation + support.MotivationEffect);
            if (support.FriendshipEligible)
            {
                foreach (var bonus in support.FriendshipBonuses) friendship *= new TrainingFraction(100L + bonus, 100);
                if (basis.Vital > 0) recovery = checked(recovery + support.WisdomRecovery);
            }
            if (basis.Vital <= 0)
                foreach (var reduction in support.VitalReductions) vitalFactor *= new TrainingFraction(100 - reduction, 100);
        }
        var vital = basis.Vital > 0 ? checked(basis.Vital + recovery) : (basis.Vital * vitalFactor).Truncate();
        var train = new TrainingFraction(100L + training + extraTraining, 100);
        var heads = new TrainingFraction(100L + 5L * ((long)guests + supports.Length), 100);
        var house = new TrainingFraction(10000 + houseBonus, 10000);
        var mood = Motivation(environment.Motivation, motivation);
        Span<int> common = stackalloc int[6];
        Span<int> scenario = stackalloc int[6];
        Span<int> uncappedCommon = stackalloc int[6];
        Span<int> uncappedScenario = stackalloc int[6];
        for (var i = 0; i < 6; i++)
        {
            var currentBase = Attribute(basis, i);
            if (currentBase != 0)
            {
                var bonus = 0;
                foreach (var support in supports) bonus = checked(bonus + Attribute(support.BaseBonuses, i));
                currentBase = checked(currentBase + bonus);
            }
            var integer = Compute(currentBase, new(100L + Attribute(environment.GrowthPercent, i), 100),
                friendship, train, heads, house, mood);
            Convert(environment, i, integer, percent, capExtra, common, scenario, uncappedCommon, uncappedScenario);
        }
        return new(Values(common, vital), Values(scenario, 0), Values(uncappedCommon, vital), Values(uncappedScenario, 0));
    }

    static int Compute(int basis, TrainingFraction growth, TrainingFraction friendship, TrainingFraction training,
        TrainingFraction heads, TrainingFraction house, TrainingFraction motivation)
    {
        var value = (basis * growth * friendship * training).Quantize(6, false);
        value = (value * heads).Quantize(6, false);
        value = (value * house).Quantize(6, false);
        return (value * motivation).Quantize(5, true).Truncate();
    }

    static void Convert(TrainingEnvironment environment, int index, int integer, int percent, bool capExtra,
        Span<int> common, Span<int> scenario, Span<int> uncappedCommon, Span<int> uncappedScenario)
    {
        var lower = Math.Min(100, integer);
        var extra = checked((int)((long)lower * percent / 100));
        if (capExtra) extra = Math.Min(100, extra);
        var current = Attribute(environment.Current, index);
        var limit = Limit(environment.Limits, index);
        uncappedCommon[index] = index < 5 ? ConvertAttribute(current, lower) : lower;
        common[index] = index < 5 ? Math.Min(uncappedCommon[index], limit - current) : lower;
        // The scenario layer starts at the actual, capped common-layer state.
        uncappedScenario[index] = index < 5 ? ConvertAttribute(current + common[index], extra) : extra;
        scenario[index] = index < 5 ? Math.Min(uncappedScenario[index], limit - current - common[index]) : extra;
    }

    static ResolvedTrainingSupport[] Resolve(ImmutableArray<TrainingSupport> supports, int command, int level, int fans, CompiledTrainingDeck? deck)
    {
        var result = new ResolvedTrainingSupport[supports.Length];
        for (var i = 0; i < supports.Length; i++)
        {
            for (var previous = 0; previous < i; previous++)
                if (supports[previous].Position == supports[i].Position) throw new ArgumentException("训练支援位置不能重复。");
            result[i] = deck is null ? TrainingSupportEffects.Resolve(supports[i], command, level, fans)
                : deck.Resolve(supports[i], command, level, fans);
        }
        return result;
    }

    static void Validate(TrainingEnvironment environment)
    {
        var invalid = environment.Motivation is < 1 or > 5 || environment.Fans < 0
            || environment.Current.Vital < 0 || environment.Current.Vital > environment.Limits.Vital || environment.Current.SkillPoints < 0
            || environment.GrowthPercent.SkillPoints != 0 || environment.GrowthPercent.Vital != 0;
        for (var i = 0; i < 5; i++)
            invalid |= Attribute(environment.Current, i) < 0 || Attribute(environment.Current, i) > Limit(environment.Limits, i)
                || Attribute(environment.GrowthPercent, i) < 0;
        if (invalid) throw new ArgumentException("训练状态的干劲、粉丝、属性、体力或成长率超出范围。");
        if (environment.GoldShipBoost is { } boost) _ = TrainingFacilityRules.PrimaryTarget(boost.CommandId);
    }

    static TrainingFraction Motivation(int motivation, long bonus) => new(checked(1000 + (motivation - 3) * (100 + bonus)), 1000);
    static int ConvertAttribute(int before, int gain)
    {
        var below = Math.Min(gain, Math.Max(0, 1200 - before));
        var above = gain - below;
        return below + (above > 0 ? Math.Max(1, above / 2) : 0);
    }
    static int Target(int index) => index < 5 ? index + 1 : 30;
    static int Attribute(TrainingValues values, int index) => index switch
    { 0 => values.Speed, 1 => values.Stamina, 2 => values.Power, 3 => values.Guts, 4 => values.Wisdom, _ => values.SkillPoints };
    static int Limit(TrainingLimits limits, int index) => index switch
    { 0 => limits.Speed, 1 => limits.Stamina, 2 => limits.Power, 3 => limits.Guts, 4 => limits.Wisdom, _ => 0 };
    static TrainingValues Values(ReadOnlySpan<int> values, int vital) => new(values[0], values[1], values[2], values[3], values[4], vital, values[5]);
}
