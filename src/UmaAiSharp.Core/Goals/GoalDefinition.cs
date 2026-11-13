using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmaAiSharp.Core.Goals;

public enum TrainingAttribute { Speed, Stamina, Power, Guts, Wisdom }
public enum AttributeComparison { LessThan, AtMost, Equal, AtLeast, GreaterThan }

public sealed record AttributeConstraint(TrainingAttribute Attribute, AttributeComparison Comparison, int Value)
{
    public long Violation(AttributeValues attributes)
    {
        var actual = (long)attributes[Attribute];
        return Comparison switch
        {
            AttributeComparison.LessThan => Math.Max(0, actual - Value + 1),
            AttributeComparison.AtMost => Math.Max(0, actual - Value),
            AttributeComparison.Equal => Math.Abs(actual - Value),
            AttributeComparison.AtLeast => Math.Max(0, Value - actual),
            AttributeComparison.GreaterThan => Math.Max(0, Value - actual + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(Comparison))
        };
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AttributeUtility), "attribute")]
[JsonDerivedType(typeof(AttributeDeficit), "deficit")]
[JsonDerivedType(typeof(AttributeOverflow), "overflow")]
[JsonDerivedType(typeof(AttributeRating), "rating")]
[JsonDerivedType(typeof(GainedSkillPoints), "skillPoints")]
public abstract record GoalMetric;
public sealed record AttributeUtility(TrainingAttribute Attribute, MarginalCurve Curve) : GoalMetric;
public sealed record AttributeDeficit(TrainingAttribute Attribute) : GoalMetric;
public sealed record AttributeOverflow(TrainingAttribute Attribute) : GoalMetric;
public sealed record AttributeRating : GoalMetric;
public sealed record GainedSkillPoints : GoalMetric;
public sealed record WeightedMetric(GoalMetric Metric, decimal Weight);
public sealed record GoalPriority(ImmutableArray<WeightedMetric> Terms);

public sealed class GoalDefinition
{
    public ImmutableArray<AttributeConstraint> Constraints { get; }
    public ImmutableArray<GoalPriority> Priorities { get; }
    [JsonIgnore] public string Identity { get; }
    [JsonIgnore] public CompiledGoal Compiled { get; }

    public GoalDefinition(ImmutableArray<AttributeConstraint> constraints, ImmutableArray<GoalPriority> priorities)
    {
        if (constraints.IsDefault || priorities.IsDefault)
            throw new ArgumentException("目标必须显式提供条件和优先级数组；没有条件时使用空数组。");
        foreach (var constraint in constraints)
            if (constraint is null || !Enum.IsDefined(constraint.Attribute) || !Enum.IsDefined(constraint.Comparison))
                throw new ArgumentException("目标条件包含未知属性或比较符。", nameof(constraints));
        foreach (var priority in priorities)
        {
            if (priority is null || priority.Terms.IsDefaultOrEmpty)
                throw new ArgumentException("每个优先级必须至少包含一个目标项。", nameof(priorities));
            foreach (var term in priority.Terms)
            {
                var valid = term?.Metric switch
                {
                    AttributeUtility x => Enum.IsDefined(x.Attribute) && x.Curve is not null,
                    AttributeDeficit x => Enum.IsDefined(x.Attribute),
                    AttributeOverflow x => Enum.IsDefined(x.Attribute),
                    AttributeRating or GainedSkillPoints => true,
                    _ => false
                };
                if (!valid)
                    throw new ArgumentException("目标项的类型、属性或曲线不完整。", nameof(priorities));
            }
        }
        Constraints = constraints;
        Priorities = priorities;
        Identity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
        Compiled = new(this);
    }
}
