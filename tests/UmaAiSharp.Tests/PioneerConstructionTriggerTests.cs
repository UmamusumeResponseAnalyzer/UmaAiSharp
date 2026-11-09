using System.Runtime.CompilerServices;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Tests;

public sealed class PioneerConstructionTriggerTests
{
    const string MasterHash = "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886";
    sealed record TriggerFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
        int RewardPreviewRequestsBetween, ConstructionProgress Before, int PointGain, ConstructionPointSource SourceKind,
        int[] CrossedThresholds, int[] VisibleEventsAtPointGain, long EventRecordId, int EventTurn, int EventStoryId, int EventRawPlayTiming);

    static string EvidenceDirectory([CallerFilePath] string source = "") => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(source)!, "../../src/UmaAiSharp.Pioneer/Evidence"));

    static ConstructionTriggerRules Rules()
    {
        using var rows = File.OpenText(Path.Combine(EvidenceDirectory(), "construction-master.jsonl"));
        return new(PioneerMasterData.ReadExport(rows, MasterHash));
    }

    static TriggerFixture[] Fixtures()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
        return File.ReadLines(Path.Combine(EvidenceDirectory(), "construction-window-fixtures.jsonl"))
            .Select(line => JsonSerializer.Deserialize<TriggerFixture>(line, options)!).ToArray();
    }

    [Fact]
    public void RecordedPointCrossingsMatchTheLaterObservedEventWindow()
    {
        var rules = Rules();
        var fixtures = Fixtures();
        Assert.Equal(769, fixtures.Length);
        foreach (var fixture in fixtures)
        {
            var result = rules.ApplyPointGainCandidate(fixture.Before, fixture.PointGain, fixture.SourceKind);
            var milestone = Assert.IsType<PendingConstructionMilestone>(result.Milestone);
            Assert.Equal(Assert.Single(fixture.CrossedThresholds), milestone.Threshold);
            Assert.Equal(fixture.EventStoryId, milestone.StoryId);
            Assert.Equal(fixture.EventRawPlayTiming, milestone.RawPlayTiming);
            Assert.Equal(fixture.EventTurn, milestone.NotBeforeTurn);
            Assert.Equal(fixture.Before.DevelopmentPoints + fixture.PointGain, result.AfterPoints.DevelopmentPoints);
            Assert.Equal(fixture.Before.PendingStoryIds, result.AfterPoints.PendingStoryIds);
        }
    }

    [Fact]
    public void EarlierEventsCanDelayVisibilityWithoutLosingTheReachedMilestone()
    {
        var delayed = Fixtures().Where(x => x.EventStoryId == 400011107 && x.EventRecordId != x.AfterRecordId).ToArray();
        Assert.Equal(2, delayed.Length);
        foreach (var fixture in delayed)
        {
            var result = Rules().ApplyPointGainCandidate(fixture.Before, fixture.PointGain, fixture.SourceKind);
            Assert.NotNull(result.Milestone);
            Assert.DoesNotContain(result.Milestone.StoryId, fixture.VisibleEventsAtPointGain);
            Assert.Equal(10, result.Milestone.RawPlayTiming);
            Assert.True(fixture.BeforeRecordId < fixture.RequestRecordId && fixture.RequestRecordId < fixture.AfterRecordId);
            Assert.True(fixture.EventRecordId > fixture.AfterRecordId);
        }
    }

    [Fact]
    public void CampMilestoneWaitsForTurn41WithoutChangingTheVisibleQueue()
    {
        var fixtures = Fixtures().Where(x => x.EventStoryId == 400011108).ToArray();
        Assert.Equal(70, fixtures.Length);
        var fixture = fixtures[0];
        var result = Rules().ApplyPointGainCandidate(fixture.Before, fixture.PointGain, fixture.SourceKind);
        Assert.Equal(41, result.Milestone!.NotBeforeTurn);
        Assert.Equal(fixture.Before.Turn, result.AfterPoints.Turn);
        Assert.Equal(fixture.Before.RawPlayingState, result.AfterPoints.RawPlayingState);
        Assert.Empty(result.AfterPoints.PendingStoryIds);
    }

    [Fact]
    public void AlreadyReachedThresholdDoesNotProduceAnotherMilestone()
    {
        var result = Rules().ApplyPointGainCandidate(new(15, 1, 3, 256, [200, 500], []), 80, ConstructionPointSource.Training);
        Assert.Null(result.Milestone);
        Assert.Equal(336, result.AfterPoints.DevelopmentPoints);
    }

    [Theory]
    [InlineData(295, 22)]
    [InlineData(294, 22)]
    public void LastClassicCampRaceCrossingWaitsForTheReturnWindow(int points, int gain)
    {
        var before = new ConstructionProgress(40, 5, 3, points, [300, 600], [400011105]);
        var result = Rules().ApplyPointGainCandidate(before, gain, ConstructionPointSource.RacePointEvent);
        Assert.Equal(new PendingConstructionMilestone(300, 400011108, 1, 41), result.Milestone);
        Assert.Equal(before.PendingStoryIds, result.AfterPoints.PendingStoryIds);
        Assert.Throws<UnknownMechanismException>(() => Rules().ApplyPointGainCandidate(before with { Turn = 39 }, gain, ConstructionPointSource.RacePointEvent));
    }

    [Fact]
    public void LastCheckpointTurnKeepsTrainingAndRaceConstructionMilestones()
    {
        var rules = Rules();
        var training = rules.ApplyPointGainCandidate(new(60, 1, 3, 753, [400, 800], []), 113, ConstructionPointSource.Training);
        Assert.Equal(new PendingConstructionMilestone(800, 400011107, 6, 60), training.Milestone);
        foreach (var (points, gain) in new[] { (787, 23), (749, 63) })
        {
            var race = rules.ApplyPointGainCandidate(new(60, 5, 3, points, [400, 800], [400011105]), gain, ConstructionPointSource.RacePointEvent);
            Assert.Equal(new PendingConstructionMilestone(800, 400011107, 10, 60), race.Milestone);
        }
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyPointGainCandidate(new(60, 5, 3, 790, [400, 800], [400011111]),
            20, ConstructionPointSource.TuckerPointEvent));
    }

    [Fact]
    public void UnknownWindowsAndMultipleCrossingsStopInsteadOfInventingAQueue()
    {
        var rules = Rules();
        var before = new ConstructionProgress(15, 1, 3, 180, [200, 500], []);
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyPointGainCandidate(before, 400, ConstructionPointSource.Training));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyPointGainCandidate(before with { Thresholds = [250, 500] }, 80, ConstructionPointSource.Training));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyPointGainCandidate(before with { PendingStoryIds = [501038524] }, 80, ConstructionPointSource.IslandTraining));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyPointGainCandidate(before, 80, ConstructionPointSource.TuckerPointEvent));
    }
}
