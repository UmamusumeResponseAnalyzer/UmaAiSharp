using System.Numerics;

namespace UmaAiSharp.Base.Training;

public static class TrainingFailureRate
{
    public static int Calculate(int masterCoefficient, int vital, IEnumerable<int> reductions, int statusBias)
    {
        var rate = new DisplayRate(masterCoefficient, vital);
        foreach (var reduction in reductions)
        {
            if (reduction is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(reductions));
            rate.Reduce(reduction);
        }
        return rate.WithBias(statusBias);
    }

    // The simulation supplies an existing span of cards; traversing their effects avoids flattening it per preview.
    public static int CalculateForSupports(int masterCoefficient, int vital, ReadOnlySpan<ResolvedTrainingSupport> supports, int statusBias)
    {
        var rate = new DisplayRate(masterCoefficient, vital);
        foreach (var support in supports)
            foreach (var reduction in support.FailureReductions)
            {
                if (reduction is < 0 or > 100) throw new InvalidDataException("训练失败率减免必须在0..100内。");
                rate.Reduce(reduction);
            }
        return rate.WithBias(statusBias);
    }

    struct DisplayRate
    {
        BigInteger numerator;
        BigInteger denominator = 400;

        public DisplayRate(int masterCoefficient, int vital)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(masterCoefficient);
            ArgumentOutOfRangeException.ThrowIfNegative(vital);
            numerator = (long)vital * 10 < masterCoefficient
                ? BigInteger.Clamp(new BigInteger(100L - vital) * (masterCoefficient - 10L * vital), 0, 99 * 400)
                : BigInteger.Zero;
        }

        public void Reduce(int percent)
        {
            numerator *= 100 - percent;
            denominator *= 100;
        }

        public readonly int WithBias(int statusBias)
            => (int)Math.Clamp((long)((numerator + denominator - 1) / denominator) + statusBias, 0, 100);
    }
}
