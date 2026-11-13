using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Runtime;

internal enum PioneerDraw
{
    TrainingFailure = 1,
    TrainingPenalty,
    Rest,
    Outing,
    RaceAttribute,
    RaceFans,
    RaceBonus,
    TuckerRepeat,
    FriendUnlock,
    OrdinaryStory,
    HintSelection,
    HintReward,
    FixedEvent,
    Inheritance,
    Board,
    IslandBoard,
}

internal static class PioneerRandom
{
    // The autonomous rules RNG stays at the sample seed. These streams do not consume it.
    internal static SimulationRandom At(this SimulationRandom sample, int turn, PioneerDraw mechanism, int slot = 0)
        => sample.Fork(((ulong)turn << 40) | ((ulong)mechanism << 32) | (uint)slot);
}
