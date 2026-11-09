using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

// Values use the observed protocol units. No display/internal conversion is inferred here.
public readonly record struct TrainingValues(int Speed, int Stamina, int Power, int Guts, int Wisdom, int Vital, int SkillPoints);
public readonly record struct TrainingLimits(int Speed, int Stamina, int Power, int Guts, int Wisdom, int Vital);
public readonly record struct TrainingParameterChange(int TargetType, int Value);

public static class TrainingPreviewSettlement
{
    public const string Version = "observed-training-preview-addition-v1";

    // This settles supplied gains; it does not generate gains, resolve failure/events, or advance the turn.
    public static TrainingValues Apply(TrainingValues before, TrainingLimits limits,
        IEnumerable<TrainingParameterChange> commonPreview, IEnumerable<TrainingParameterChange> scenarioPreview)
    {
        var gains = new TrainingValues();
        foreach (var change in commonPreview.Concat(scenarioPreview))
        {
            gains = change.TargetType switch
            {
                1 => gains with { Speed = checked(gains.Speed + change.Value) },
                2 => gains with { Stamina = checked(gains.Stamina + change.Value) },
                3 => gains with { Power = checked(gains.Power + change.Value) },
                4 => gains with { Guts = checked(gains.Guts + change.Value) },
                5 => gains with { Wisdom = checked(gains.Wisdom + change.Value) },
                10 => gains with { Vital = checked(gains.Vital + change.Value) },
                30 => gains with { SkillPoints = checked(gains.SkillPoints + change.Value) },
                _ => throw new UnknownMechanismException($"训练预览包含未核验的target_type={change.TargetType}。")
            };
        }

        return Apply(before, limits, gains);
    }

    public static TrainingValues Apply(TrainingValues before, TrainingLimits limits,
        TrainingValues common, TrainingValues scenario) => Apply(before, limits, new TrainingValues(
            checked(common.Speed + scenario.Speed), checked(common.Stamina + scenario.Stamina),
            checked(common.Power + scenario.Power), checked(common.Guts + scenario.Guts),
            checked(common.Wisdom + scenario.Wisdom), checked(common.Vital + scenario.Vital),
            checked(common.SkillPoints + scenario.SkillPoints)));

    static TrainingValues Apply(TrainingValues before, TrainingLimits limits, TrainingValues gains)
    {
        if (before.Vital < 0 || before.Vital > limits.Vital || before.SkillPoints < 0)
            throw new ArgumentException("训练前体力或技能Pt不符合输入范围。", nameof(before));
        var vital = checked(before.Vital + gains.Vital);
        var skillPoints = checked(before.SkillPoints + gains.SkillPoints);
        if (vital < 0 || skillPoints < 0)
            throw new UnknownMechanismException("训练预览结算涉及尚未核验的体力或技能Pt下限行为。");
        return new(
            ApplyAttribute(before.Speed, gains.Speed, limits.Speed, "speed"),
            ApplyAttribute(before.Stamina, gains.Stamina, limits.Stamina, "stamina"),
            ApplyAttribute(before.Power, gains.Power, limits.Power, "power"),
            ApplyAttribute(before.Guts, gains.Guts, limits.Guts, "guts"),
            ApplyAttribute(before.Wisdom, gains.Wisdom, limits.Wisdom, "wiz"),
            Math.Min(vital, limits.Vital), skillPoints);
    }

    static int ApplyAttribute(int before, int gain, int limit, string name)
    {
        if (before < 0 || before > limit)
            throw new ArgumentException($"训练前{name}={before}不在0..{limit}范围内。");
        var result = checked(before + gain);
        if (result < 0 || result > limit)
            throw new UnknownMechanismException($"{name}预览结算{before}+{gain}超出0..{limit}；当前样本未验证属性截断，不猜测截断量。");
        return result;
    }
}
