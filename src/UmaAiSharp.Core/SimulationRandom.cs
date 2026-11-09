namespace UmaAiSharp.Core;

public enum SimulationRandomStream
{
    Rules,
    Policy
}

public struct SimulationRandom(ulong seed)
{
    public const string Algorithm = "splitmix64-v1";
    public const string SeedDerivation = "sample-domain-mix-v1";

    public ulong State { get; private set; } = seed;

    public ulong NextUInt64()
    {
        State = unchecked(State + 0x9E3779B97F4A7C15UL);
        return Mix(State);
    }

    public int NextInt(int exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);
        return (int)NextUInt64((ulong)exclusiveUpperBound);
    }

    public ulong NextUInt64(ulong exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(exclusiveUpperBound);
        var bound = exclusiveUpperBound;
        var threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value < threshold);

        return value % bound;
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public static ulong DeriveSeed(ulong rootSeed, int sampleIndex, SimulationRandomStream stream)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleIndex);
        var domain = stream switch
        {
            SimulationRandomStream.Rules => 0xA0761D6478BD642FUL,
            SimulationRandomStream.Policy => 0xE7037ED1A0B428DBUL,
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "未知随机流。")
        };
        return Mix(rootSeed ^ Mix(unchecked((ulong)sampleIndex + 0xD1B54A32D192ED03UL)) ^ domain);
    }

    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }
}
