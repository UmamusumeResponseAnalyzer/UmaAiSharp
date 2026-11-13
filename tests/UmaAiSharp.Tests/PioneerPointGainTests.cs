using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Tests;

public sealed class PioneerPointGainTests
{
    [Theory]
    [InlineData(2, 5, false, 75)]
    [InlineData(4, 10, true, 109)]
    public void GeneratesIntegerPointsWithOneFinalFloor(int participants, int bonus, bool friendship, int expected) =>
        Assert.Equal(expected, PioneerPointGain.OrdinaryTraining(participants, bonus, friendship));

}
