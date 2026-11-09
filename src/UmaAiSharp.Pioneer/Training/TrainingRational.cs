using System.Numerics;

namespace UmaAiSharp.Pioneer.Training;

internal readonly record struct TrainingRational
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }

    public TrainingRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public static implicit operator TrainingRational(int value) => new(value, 1);
    public static TrainingRational operator *(TrainingRational left, TrainingRational right)
        => new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);
    public static TrainingRational operator +(TrainingRational left, TrainingRational right)
        => new(left.Numerator * right.Denominator + right.Numerator * left.Denominator, left.Denominator * right.Denominator);
    public int Truncate() => checked((int)(Numerator / Denominator));

    // Significant decimal digits, with exact comparisons even immediately below a power of ten.
    public TrainingRational Quantize(int digits, bool halfUp)
    {
        if (Numerator < 0) throw new ArgumentOutOfRangeException(nameof(Numerator), "训练收益量化要求非负数。");
        if (Numerator.IsZero) return this;
        var power = 0;
        var n = Numerator;
        var d = Denominator;
        while (n >= d * 10) { d *= 10; power++; }
        while (n < d) { n *= 10; power--; }
        var scale = power - digits + 1;
        var unit = scale >= 0 ? new TrainingRational(BigInteger.Pow(10, scale), 1)
            : new TrainingRational(1, BigInteger.Pow(10, -scale));
        var scaledN = Numerator * unit.Denominator;
        var scaledD = Denominator * unit.Numerator;
        var rounded = halfUp ? (2 * scaledN + scaledD) / (2 * scaledD) : scaledN / scaledD;
        return new(rounded * unit.Numerator, unit.Denominator);
    }
}
