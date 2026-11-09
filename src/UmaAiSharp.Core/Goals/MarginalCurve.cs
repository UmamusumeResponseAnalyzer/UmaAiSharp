using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace UmaAiSharp.Core.Goals;

public readonly record struct CurvePoint(
    [property: JsonRequired] int Value,
    [property: JsonRequired] decimal Weight);

public sealed class MarginalCurve
{
    public ImmutableArray<CurvePoint> Points { get; }

    public MarginalCurve(ImmutableArray<CurvePoint> points)
    {
        if (points.IsDefaultOrEmpty || points[0].Value != 0)
            throw new ArgumentException("边际曲线必须包含从属性值 0 开始的控制点。", nameof(points));
        for (var i = 1; i < points.Length; i++)
            if (points[i].Value <= points[i - 1].Value)
                throw new ArgumentException("边际曲线的属性值必须严格递增，不能重复或倒序。", nameof(points));
        Points = points;
    }

    // 每个整数属性点只计一次；最后一个控制点以后的边际权重保持不变。
    public decimal Evaluate(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var total = 0m;
        for (var i = 0; i < Points.Length - 1 && value > Points[i].Value; i++)
        {
            var left = Points[i];
            var right = Points[i + 1];
            var count = Math.Min(value, right.Value) - left.Value;
            total += count * left.Weight
                + (right.Weight - left.Weight) * count * (count + 1m) / (2m * (right.Value - left.Value));
        }
        if (value > Points[^1].Value)
            total += (value - Points[^1].Value) * Points[^1].Weight;
        return total;
    }
}
