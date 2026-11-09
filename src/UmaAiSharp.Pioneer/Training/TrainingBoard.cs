using System.Numerics;

namespace UmaAiSharp.Pioneer.Training;

public readonly record struct TrainingPlacement(byte SupportMask, byte HintMask, byte GuestCount)
{
    public int SupportCount => BitOperations.PopCount((uint)SupportMask);
}

public readonly record struct TrainingBoard(TrainingPlacement Speed, TrainingPlacement Stamina,
    TrainingPlacement Power, TrainingPlacement Guts, TrainingPlacement Wisdom)
{
    public TrainingPlacement this[int index] => index switch
    {
        0 => Speed, 1 => Stamina, 2 => Power, 3 => Guts, 4 => Wisdom,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal TrainingBoard With(int index, TrainingPlacement value) => index switch
    {
        0 => this with { Speed = value }, 1 => this with { Stamina = value }, 2 => this with { Power = value },
        3 => this with { Guts = value }, 4 => this with { Wisdom = value },
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal static int Positions(byte mask, Span<int> destination)
    {
        var length = 0;
        var remaining = (uint)mask;
        while (remaining != 0)
        {
            destination[length++] = BitOperations.TrailingZeroCount(remaining) + 1;
            remaining &= remaining - 1;
        }
        return length;
    }
}
