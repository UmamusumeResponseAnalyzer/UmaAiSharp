using System.Text.Json.Serialization;

namespace UmaAiSharp.Core.Goals;

public readonly record struct AttributeValues(
    [property: JsonRequired] int Speed,
    [property: JsonRequired] int Stamina,
    [property: JsonRequired] int Power,
    [property: JsonRequired] int Guts,
    [property: JsonRequired] int Wisdom)
{
    public static AttributeValues operator +(AttributeValues left, AttributeValues right) => new(
        checked(left.Speed + right.Speed), checked(left.Stamina + right.Stamina), checked(left.Power + right.Power),
        checked(left.Guts + right.Guts), checked(left.Wisdom + right.Wisdom));

    public int this[TrainingAttribute attribute] => attribute switch
    {
        TrainingAttribute.Speed => Speed,
        TrainingAttribute.Stamina => Stamina,
        TrainingAttribute.Power => Power,
        TrainingAttribute.Guts => Guts,
        TrainingAttribute.Wisdom => Wisdom,
        _ => throw new ArgumentOutOfRangeException(nameof(attribute))
    };
}

// 终局属性与上限使用游戏显示单位。
public sealed record TerminalMetrics(
    AttributeValues Attributes,
    AttributeValues Caps,
    int? FutureGainedSkillPoints,
    AttributeValues? FutureOverflow,
    int? VerifiedAttributeRating,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FailedCareerGoalId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EstimatedFutureSkillRating = null);
