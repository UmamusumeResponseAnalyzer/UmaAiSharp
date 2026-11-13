using System.Numerics;

namespace UmaAiSharp.Pioneer.Training;

// Arithmetic needs the fraction's value, not canonical numerator/denominator diagnostics.
// Omitting GCD is exact; quantization replaces the product denominator with a decimal power.
internal readonly struct TrainingFraction(BigInteger numerator, BigInteger denominator) : ITrainingMultiplier<TrainingFraction>
{
    public static TrainingFraction Ratio(long numerator, long denominator) => new(numerator, denominator);
    public static implicit operator TrainingFraction(int value) => new(value, 1);
    public static TrainingFraction operator *(TrainingFraction left, TrainingFraction right)
        => new(left.numerator * right.numerator, left.denominator * right.denominator);
    readonly BigInteger numerator = numerator;
    readonly BigInteger denominator = denominator > 0 ? denominator : throw new ArgumentOutOfRangeException(nameof(denominator));

    public int Truncate() => checked((int)(numerator / denominator));

    public TrainingFraction Quantize(int digits, bool halfUp)
    {
        if (numerator < 0) throw new ArgumentOutOfRangeException(nameof(numerator), "训练收益量化要求非负数。");
        if (numerator.IsZero) return new(0, 1);
        var power = 0;
        var n = numerator;
        var d = denominator;
        while (n >= d * 10) { d *= 10; power++; }
        while (n < d) { n *= 10; power--; }
        var scale = power - digits + 1;
        if (scale >= 0)
        {
            var unit = BigInteger.Pow(10, scale);
            var scaledD = denominator * unit;
            var rounded = halfUp ? (2 * numerator + scaledD) / (2 * scaledD) : numerator / scaledD;
            return new(rounded * unit, 1);
        }
        else
        {
            var unit = BigInteger.Pow(10, -scale);
            var scaledN = numerator * unit;
            var rounded = halfUp ? (2 * scaledN + denominator) / (2 * denominator) : scaledN / denominator;
            return new(rounded, unit);
        }
    }
}
