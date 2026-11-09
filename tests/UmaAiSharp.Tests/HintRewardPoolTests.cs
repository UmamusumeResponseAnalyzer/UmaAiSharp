using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class HintRewardPoolTests
{
    [Fact]
    public void PoolDeduplicatesGroupsAndPreservesUnrecordedLevels()
    {
        var pool = HintRewardPool.Evaluate([new(1, 1), new(1, 1), new(2, 1), new(3, 1), new(4, 1)],
            [new(new(2, 1), 5), new(new(4, 1), 2)], [new(3, 2)]);
        Assert.Equal(4, pool.Length);
        Assert.Equal(new HintPoolEntry(new(1, 1), null, HintPoolExclusion.None), pool[0]);
        Assert.Equal(HintPoolExclusion.MaximumHintLevel, pool[1].Exclusion);
        Assert.Equal(HintPoolExclusion.AlreadyLearned, pool[2].Exclusion);
        Assert.Equal(new HintPoolEntry(new(4, 1), 2, HintPoolExclusion.None), pool[3]);
        Assert.Empty(HintRewardPool.Evaluate([new(1, 1)], [], [new(1, 1)]).Where(entry => entry.Exclusion == HintPoolExclusion.None));
    }

    [Fact]
    public void MissingInputsAndUnverifiedRarityDoNotBecomeAnEmptyPool()
    {
        Assert.Throws<InvalidDataException>(() => HintRewardPool.Evaluate([new(1, 1)], default, []));
        Assert.Throws<InvalidDataException>(() => HintRewardPool.Evaluate([new(1, 1)], [new(new(1, 1), 0)], []));
        Assert.Throws<UnknownMechanismException>(() => HintRewardPool.Evaluate([new(1, 2)], [], [new(1, 1)]));
        Assert.Throws<UnknownMechanismException>(() => HintRewardPool.Evaluate([new(1, 1)], [], [new(1, 3)]));
    }

    [Fact]
    public void AttributeRewardSplitsAt1200AndPreservesCappedGain()
    {
        var before = new TrainingValues(1199, 1200, 1500, 100, 100, 60, 1000);
        var after = HintRewardPool.ApplyAttributeReward(before, new(1800, 1700, 1500, 1600, 1300, 100), new(2, 2, 6, 0, 0, 0, 2));
        Assert.Equal(new TrainingValues(1201, 1201, 1500, 100, 100, 60, 1002), after.Resources);
        Assert.Equal(new AttributeValues(2, 1, 3, 0, 0), after.UncappedGains);
        Assert.Equal(new AttributeValues(0, 0, 3, 0, 0), after.Overflow);
        Assert.Throws<InvalidDataException>(() => HintRewardPool.ApplyAttributeReward(before,
            new(1800, 1700, 1500, 1600, 1300, 100), new(0, 0, 0, 0, 0, 5, 0)));
    }
}
