using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Races;

namespace UmaAiSharp.Tests;

public sealed class PioneerDevelopmentComponentTests
{
    sealed record RaceFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
        RacePointContext Context, int BeforePoints, int AfterPoints);
    sealed record TuckerFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
        TuckerEventContext Context, PioneerPointResources Before, PioneerPointResources After);
    sealed record ProgressFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
        CheckpointEventContext Context, CheckpointProgress Before, CheckpointProgress After, int AddedMembers, int RemovedMembers);
    static string Evidence([CallerFilePath] string source = "") => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(source)!, "../../src/UmaAiSharp.Pioneer/Development/Evidence"));
    static CheckpointMasterData Master()
    {
        using var input = File.OpenText(Path.Combine(Evidence(), "checkpoint-master.jsonl"));
        return CheckpointMasterData.ReadExport(input, "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886");
    }
    static T[] Fixtures<T>(string name) => File.ReadLines(Path.Combine(Evidence(), name))
        .Select(x => JsonSerializer.Deserialize<T>(x, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { RespectRequiredConstructorParameters = true })!).ToArray();

    [Fact]
    public void FixturesRetainTheRecordedInputAndOutputFingerprints()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Evidence(), "component-provenance.json")));
        foreach (var file in manifest.RootElement.GetProperty("outputs").EnumerateArray())
        {
            using var stream = File.OpenRead(Path.Combine(Evidence(), file.GetProperty("name").GetString()!));
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    [Fact]
    public void RacePointsMatchEveryWindowAgreedByBothIndependentClassifications()
    {
        var rules = new PioneerRacePointGain(Master());
        var fixtures = Fixtures<RaceFixture>("race-point-observations.jsonl");
        Assert.Equal(902, fixtures.Length);
        Assert.Equal(519, fixtures.Count(x => x.Context.IsTargetRace));
        foreach (var row in fixtures)
            Assert.True(row.BeforePoints + rules.CalculateObservedEvent(row.Context) == row.AfterPoints,
                $"source={row.SourceSha256}, records={row.BeforeRecordId}/{row.RequestRecordId}/{row.AfterRecordId}");
    }

    [Theory]
    [InlineData(12, 0, 0, true, 60)]
    [InlineData(13, 1, 2, true, 61)]
    [InlineData(25, 2, 2, false, 22)]
    [InlineData(49, 4, 2, true, 63)]
    public void TargetBonusIsAddedAfterScalingTheOrdinaryRacePoints(int turn, int checkpoint, int result, bool target, int expected)
    {
        Assert.Equal(expected, new PioneerRacePointGain(Master()).CalculateObservedEvent(
            new(turn, 5, 3, 400011105, 0, target, checkpoint, result)));
    }

    [Fact]
    public void UnverifiedRaceBranchesDoNotInventZeroOrVictoryRewards()
    {
        var rules = new PioneerRacePointGain(Master());
        var context = new RacePointContext(13, 5, 3, 400011105, 0, true, 1, 2);
        Assert.Throws<UnknownMechanismException>(() => rules.CalculateObservedEvent(context with { CompletedCheckpointResult = 1 }));
        Assert.Throws<UnknownMechanismException>(() => rules.CalculateObservedEvent(context with { RawPlayingState = 4 }));
        Assert.Throws<UnknownMechanismException>(() => rules.CalculateObservedEvent(context with { Turn = 61 }));
        Assert.Throws<UnknownMechanismException>(() => rules.CalculateObservedEvent(context with
            { Turn = 12, CompletedCheckpointType = 0, CompletedCheckpointResult = 0, IsTargetRace = false }));
    }

    [Fact]
    public void TuckerResourcesMatchTheRecordedChoiceWithoutAdvancingTheEvent()
    {
        var fixtures = Fixtures<TuckerFixture>("tucker-point-observations.jsonl");
        Assert.Equal(376, fixtures.Length);
        foreach (var row in fixtures)
            Assert.Equal(row.After, TuckerPointSettlement.ApplyObservedChoiceOne(row.Context, row.Before));
        var context = fixtures[0].Context;
        Assert.Throws<UnknownMechanismException>(() => TuckerPointSettlement.ApplyObservedChoiceOne(context with { ChoiceNumber = 2 }, new(0, 0)));
        Assert.Throws<UnknownMechanismException>(() => TuckerPointSettlement.ApplyObservedChoiceOne(context with { SupportExperience = 82935 }, new(0, 0)));
        Assert.Throws<UnknownMechanismException>(() => TuckerPointSettlement.ApplyObservedChoiceOne(context with { DeckId = 0 }, new(0, 0)));
    }

    [Fact]
    public void HighCheckpointProgressMatchesLevelsBondAndMemberCountsWithoutInventingIdentities()
    {
        var rules = new CheckpointSettlement(Master());
        var fixtures = Fixtures<ProgressFixture>("checkpoint-progress-observations.jsonl");
        Assert.Equal(389, fixtures.Length);
        Assert.Equal(6, fixtures.Count(x => x.Before.HasAttentionEffect && x.Before.DirectorBond < 100));
        foreach (var row in fixtures)
        {
            var actual = rules.ApplyHighEvaluationProgress(row.Context, row.Before);
            Assert.Equal(row.After.TrainingLevels, actual.TrainingLevels);
            Assert.Equal(row.After.DirectorBond, actual.DirectorBond);
            Assert.Equal(row.AddedMembers, actual.AddedProjectMemberCount);
            Assert.Equal(0, row.RemovedMembers);
        }
    }

    [Fact]
    public void ProgressCapsDoNotEraseTheAttentionEffectOrAcceptLowEvaluation()
    {
        var rules = new CheckpointSettlement(Master());
        var context = new CheckpointEventContext(24, 5, 3, 2, 2, 400011008, 0);
        var before = new CheckpointProgress(new(5, 4, 3, 2, 1), 94, true);
        var actual = rules.ApplyHighEvaluationProgress(context, before);
        Assert.Equal(new(5, 5, 4, 3, 2), actual.TrainingLevels);
        Assert.Equal(100, actual.DirectorBond);
        Assert.Equal(3, actual.AddedProjectMemberCount);
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyHighEvaluationProgress(
            new(60, 5, 3, 5, 1, 400011022, 0), before));
    }

    [Fact]
    public void AuditRejectsChangedNormalizedInputsBeforeUsingResearchAliases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Development-" + Guid.NewGuid().ToString("N"));
        var research = Path.Combine(directory, "research", "construction-checkpoints", "run-02");
        Directory.CreateDirectory(research);
        try
        {
            File.WriteAllText(Path.Combine(directory, "audit.json"), "{}");
            File.WriteAllText(Path.Combine(research, "provenance.json"), JsonSerializer.Serialize(new
            {
                inputs = new Dictionary<string, string> { ["audit.json"] = new('0', 64) }
            }));
            var error = Assert.Throws<InvalidDataException>(() => DevelopmentComponentAudit.Run(
                directory, Path.Combine(directory, "research"), Path.Combine(directory, "output")));
            Assert.Contains("完整 SHA-256 不匹配", error.Message);
            Assert.False(Directory.Exists(Path.Combine(directory, "output")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExpandedRaceStudyRequiresItsOwnExactNormalizedInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-RaceStudy-" + Guid.NewGuid().ToString("N"));
        var study = Path.Combine(directory, "study", "run-01");
        Directory.CreateDirectory(study);
        try
        {
            File.WriteAllText(Path.Combine(directory, "audit.json"), "{}");
            File.WriteAllText(Path.Combine(study, "summary.json"), JsonSerializer.Serialize(new
            {
                inputs = new[] { new { path = "previous/audit.json", sha256 = new string('0', 64) } }
            }));
            var error = Assert.Throws<InvalidDataException>(() => DevelopmentComponentAudit.RunRaceStudy(directory,
                Path.Combine(directory, "study"), Path.Combine(directory, "master.mdb"), Path.Combine(directory, "output")));
            Assert.Contains("完整输入SHA-256不一致", error.Message);
            Assert.False(Directory.Exists(Path.Combine(directory, "output")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
