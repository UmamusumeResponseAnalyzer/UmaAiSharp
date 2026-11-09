using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Tests;

public sealed class PioneerPointGainTests
{
    [Theory]
    [InlineData(0, 0, false, 60)]
    [InlineData(2, 5, false, 75)]
    [InlineData(4, 10, true, 109)]
    [InlineData(5, 15, true, 121)]
    public void GeneratesIntegerPointsWithOneFinalFloor(int participants, int bonus, bool friendship, int expected) =>
        Assert.Equal(expected, PioneerPointGain.OrdinaryTraining(participants, bonus, friendship));

    [Fact]
    public void InvalidInputsDoNotBecomeZeroPoints() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PioneerPointGain.OrdinaryTraining(-1, 5, false));
}
