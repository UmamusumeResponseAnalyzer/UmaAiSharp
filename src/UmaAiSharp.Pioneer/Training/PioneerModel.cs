namespace UmaAiSharp.Pioneer.Training;

public abstract class PioneerModel
{
    private protected PioneerModel() { }

    public abstract string Identity { get; }

    public static PioneerModel Resolve(string name) => name switch
    {
        "precise" => PrecisePioneerModel.Instance,
        "fast" => FastPioneerModel.Instance,
        _ => throw new ArgumentException($"Pioneer 数值模型“{name}”无效，可用值为 precise、fast。", nameof(name)),
    };

    internal abstract TrainingActionPreview Preview(in TrainingEnvironment environment, int command, int level, int guests,
        int percent, int failureCoefficient, int failureBias, ReadOnlySpan<int> positions, ReadOnlySpan<int> bonds,
        CompiledTrainingDeck deck, CompiledCampTraining? camp);

    internal abstract TrainingNumericResult Island(IslandTrainingInput input, CompiledTrainingDeck deck,
        in TrainingEnvironment environment, ReadOnlySpan<int> ordinaryLevels, ReadOnlySpan<int> bonds,
        CompiledIslandFacilities? facilities);
}

public sealed class PrecisePioneerModel : PioneerModel
{
    public static PrecisePioneerModel Instance { get; } = new();
    private PrecisePioneerModel() { }

    public override string Identity => "precise/" + TrainingNumericKernel.Version;

    internal override TrainingActionPreview Preview(in TrainingEnvironment environment, int command, int level, int guests,
        int percent, int failureCoefficient, int failureBias, ReadOnlySpan<int> positions, ReadOnlySpan<int> bonds,
        CompiledTrainingDeck deck, CompiledCampTraining? camp)
        => TrainingNumericKernel.Preview<TrainingFraction>(environment, command, level, guests, percent, failureCoefficient,
            failureBias, positions, bonds, deck, camp);

    internal override TrainingNumericResult Island(IslandTrainingInput input, CompiledTrainingDeck deck,
        in TrainingEnvironment environment, ReadOnlySpan<int> ordinaryLevels, ReadOnlySpan<int> bonds,
        CompiledIslandFacilities? facilities)
        => TrainingNumericKernel.Island<TrainingFraction>(input, deck, environment, ordinaryLevels, bonds, facilities);
}

public sealed class FastPioneerModel : PioneerModel
{
    public static FastPioneerModel Instance { get; } = new();
    private FastPioneerModel() { }

    public override string Identity => "fast/pioneer-training-unrounded-v1";

    internal override TrainingActionPreview Preview(in TrainingEnvironment environment, int command, int level, int guests,
        int percent, int failureCoefficient, int failureBias, ReadOnlySpan<int> positions, ReadOnlySpan<int> bonds,
        CompiledTrainingDeck deck, CompiledCampTraining? camp)
        => TrainingNumericKernel.Preview<Multiplier>(environment, command, level, guests, percent, failureCoefficient,
            failureBias, positions, bonds, deck, camp);

    internal override TrainingNumericResult Island(IslandTrainingInput input, CompiledTrainingDeck deck,
        in TrainingEnvironment environment, ReadOnlySpan<int> ordinaryLevels, ReadOnlySpan<int> bonds,
        CompiledIslandFacilities? facilities)
        => TrainingNumericKernel.Island<Multiplier>(input, deck, environment, ordinaryLevels, bonds, facilities);

    readonly struct Multiplier(double value) : ITrainingMultiplier<Multiplier>
    {
        readonly double value = value;

        public static Multiplier Ratio(long numerator, long denominator) => new((double)numerator / denominator);
        public static implicit operator Multiplier(int value) => new(value);
        public static Multiplier operator *(Multiplier left, Multiplier right) => new(left.value * right.value);

        public Multiplier Quantize(int digits, bool halfUp) => value >= 0 ? this
            : throw new ArgumentOutOfRangeException(nameof(value), "训练收益量化要求非负数。");
        public int Truncate() => checked((int)value);
    }
}

internal interface ITrainingMultiplier<T> where T : struct, ITrainingMultiplier<T>
{
    static abstract T Ratio(long numerator, long denominator);
    static abstract implicit operator T(int value);
    static abstract T operator *(T left, T right);
    T Quantize(int digits, bool halfUp);
    int Truncate();
}
