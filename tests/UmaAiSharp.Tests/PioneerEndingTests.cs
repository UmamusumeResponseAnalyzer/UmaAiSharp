using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Ending;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerEndingTests
{
    static readonly PioneerEndingContext Context = new(211040, 400011027, 78, 5, 0, [2, 2, 2, 2, 2], false);

    [Fact]
    public void MatchesObservedHighEndingAttributeWindows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-endings.json")));
        var windows = document.RootElement.GetProperty("windows");
        Assert.Equal(215, windows.GetArrayLength());
        foreach (var window in windows.EnumerateArray())
        {
            var before = window.GetProperty("before").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var caps = window.GetProperty("caps").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var expected = window.GetProperty("after").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var result = PioneerEndingSettlement.ApplyCandidate(Context,
                new(before[0], before[1], before[2], before[3], before[4], 50, 1000),
                new(caps[0], caps[1], caps[2], caps[3], caps[4], 100));
            Assert.Equal(new TrainingValues(expected[0], expected[1], expected[2], expected[3], expected[4], 50, 1540), result.After);
            Assert.Equal((21036, 2, 1), (result.HintGroupId, result.HintRarity, result.HintLevel));
        }
    }

    [Fact]
    public void DoesNotInventOtherEndingOrHintMergeRules()
    {
        var before = new TrainingValues(1146, 1200, 1201, 100, 100, 50, 100);
        var caps = new TrainingLimits(1500, 1500, 1500, 1500, 1500, 100);
        Assert.Throws<UnknownMechanismException>(() => PioneerEndingSettlement.ApplyCandidate(Context with { EventId = 211041 }, before, caps));
        Assert.Throws<UnknownMechanismException>(() => PioneerEndingSettlement.ApplyCandidate(Context with { CheckpointResults = [2, 2, 2, 2, 1] }, before, caps));
        Assert.Throws<UnknownMechanismException>(() => PioneerEndingSettlement.ApplyCandidate(Context with { HasEndingHint = true }, before, caps));
        Assert.Throws<ArgumentException>(() => PioneerEndingSettlement.ApplyCandidate(Context, before with { Speed = 1501 }, caps));
    }
}
