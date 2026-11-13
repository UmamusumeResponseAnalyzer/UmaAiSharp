using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingFailureTests
{
    [Fact]
    public void UnknownFailureEffectsDoNotSilentlyUseTheObservedBranch()
    {
        var initial = new TrainingFailureState(104503, 30, 5, new(100, 535, 100, 100, 100, 65, 300),
            1, TrainingFailureStage.TrainingDecision, null);
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureSettlement.BeginResolvedFailure(initial, [99, 100, 100, 100, 100, 100], 105, new(7043, 1045, 501045713, 6)));
        Assert.Throws<UnknownMechanismException>(() => TrainingFailureSettlement.BeginResolvedFailure(initial, [100, 100, 100, 100, 100, 100],
            106, new(7043, 1045, 501045713, 6)));
    }

}
