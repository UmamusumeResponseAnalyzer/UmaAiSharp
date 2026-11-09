using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Tests;

public sealed class PioneerCheckpointTests
{
    const string MasterHash = "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886";
    sealed record ResourceFixture(string SourceSha256, long BeforeRecordId, long AfterRecordId,
        CheckpointEventContext Context, CheckpointResources Before, CheckpointResources After);

    static string EvidenceDirectory([CallerFilePath] string source = "") => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(source)!, "../../src/UmaAiSharp.Pioneer/Development/Evidence"));

    static CheckpointMasterData Master()
    {
        using var rows = File.OpenText(Path.Combine(EvidenceDirectory(), "checkpoint-master.jsonl"));
        return CheckpointMasterData.ReadExport(rows, MasterHash);
    }

    [Fact]
    public void AllRecordedResourceSettlementsMatchWithoutAdvancingTheEvent()
    {
        var rules = new CheckpointSettlement(Master());
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
        var fixtures = File.ReadLines(Path.Combine(EvidenceDirectory(), "checkpoint-resource-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<ResourceFixture>(line, json)!).ToArray();
        Assert.Equal(391, fixtures.Length);
        Assert.Equal(6, fixtures.Select(x => x.Context.StoryId).Distinct().Count());
        foreach (var fixture in fixtures)
        {
            var actual = rules.ApplyResourcesCandidate(fixture.Context, fixture.Before);
            Assert.True(actual.After == fixture.After,
                $"source={fixture.SourceSha256}, records={fixture.BeforeRecordId}->{fixture.AfterRecordId}");
            Assert.Equal(fixture.Before.MaxVital, actual.After.MaxVital);
        }
    }

    [Fact]
    public void MasterEffectsRemainExactRowsInsteadOfDoublingTheLowerResult()
    {
        var master = Master();
        Assert.Equal(38, master.Effects.Values.Sum(x => x.Length));
        Assert.Equal(20, Assert.Single(master.GetEffects(1, 2).Where(x => x.EffectType == 204)).EffectValue2);
        Assert.Equal(15, Assert.Single(master.GetEffects(1, 1).Where(x => x.EffectType == 204)).EffectValue2);
        Assert.DoesNotContain(master.GetEffects(5, 2), x => x.EffectType == 202);
        Assert.Throws<UnknownMechanismException>(() => master.GetEffects(5, 0));
    }

    [Theory]
    [InlineData(947, 40, 108, 18, 18)]
    [InlineData(947, 100, 108, 18, 8)]
    [InlineData(1600, 40, 108, 30, 30)]
    [InlineData(800, 40, 108, 0, 0)]
    public void RecoveryFloorsMasterRateBeforeApplyingBothCaps(int points, int vital, int maximum, int calculated, int applied)
    {
        var result = new CheckpointSettlement(Master()).ApplyResourcesCandidate(new(60, 5, 3, 5, 2, 400011021, 0), new(points, vital, maximum));
        Assert.Equal(calculated, result.RecoveryBeforeVitalCap);
        Assert.Equal(applied, result.ActualRecovery);
        Assert.Equal(new(0, vital + applied, maximum), result.After);
    }

    [Fact]
    public void LowerObservedResultClearsPointsWithoutInventingRecovery()
    {
        var result = new CheckpointSettlement(Master()).ApplyResourcesCandidate(new(60, 5, 3, 5, 1, 400011022, 0), new(786, 78, 108));
        Assert.Equal(new(0, 78, 108), result.After);
        Assert.Equal(0, result.RecoveryBeforeVitalCap);
    }

    [Theory]
    [InlineData(12, 1, 400)]
    [InlineData(24, 2, 500)]
    [InlineData(36, 3, 600)]
    [InlineData(48, 4, 600)]
    [InlineData(60, 5, 800)]
    public void ReachedTargetEvaluatesOnlyTheSupportedGreatResult(int turn, int type, int target)
    {
        var rules = new CheckpointSettlement(Master());
        var evaluation = rules.EvaluateReachedTargetCandidate(turn, 34, 3, type, target);
        Assert.Equal(2, evaluation.ResultState);
        Assert.Equal(target, evaluation.TargetPoints);
        Assert.All(evaluation.Effects, row => Assert.Equal((type, 2), (row.CheckPointType, row.ResultState)));
        var error = Assert.Throws<UnknownMechanismException>(() => rules.EvaluateReachedTargetCandidate(turn, 34, 3, type, target - 1));
        Assert.Contains("低评价", error.Message);
    }

    [Fact]
    public void EvaluationCannotSkipTheExplicitCheckpointDecisionPhase()
    {
        var rules = new CheckpointSettlement(Master());
        Assert.Throws<UnknownMechanismException>(() => rules.EvaluateReachedTargetCandidate(12, 5, 3, 1, 651));
        Assert.Throws<UnknownMechanismException>(() => rules.EvaluateReachedTargetCandidate(13, 34, 3, 1, 651));
        Assert.Throws<UnknownMechanismException>(() => rules.EvaluateReachedTargetCandidate(12, 34, 2, 1, 651));
    }

    [Fact]
    public void UnverifiedBranchesAndContradictoryResultInputsFailExplicitly()
    {
        var rules = new CheckpointSettlement(Master());
        var context = new CheckpointEventContext(12, 5, 3, 1, 2, 400011004, 0);
        var resources = new CheckpointResources(597, 60, 100);
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyResourcesCandidate(context with { RawPlayingState = 34 }, resources));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyResourcesCandidate(context with { DeckId = 2 }, resources));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyResourcesCandidate(context with { ChoiceNumber = 1 }, resources));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyResourcesCandidate(context with { ResultState = 1, StoryId = 400011005 }, resources));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplyResourcesCandidate(context, resources with { DevelopmentPoints = 399 }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"id\":1,\"check_point_type\":1,\"group_id\":10101,\"add_value_max\":20}")]
    public void MissingMasterValuesAreNotDefaulted(string row)
    {
        var error = Assert.Throws<InvalidDataException>(() => CheckpointMasterData.ReadExport(
            new StringReader("{\"table\":\"single_mode_11_pioneer_pt_bonus\",\"row\":" + row + "}"), MasterHash));
        Assert.Contains("第 1 行", error.Message);
    }

    [Theory]
    [InlineData(4, true, true, 1)]
    [InlineData(2, true, true, 0)]
    [InlineData(6, true, true, 0)]
    [InlineData(4, false, true, 0)]
    [InlineData(4, true, false, 0)]
    public void AuditRequiresExactRequestMembershipAndCaptureOrder(long requestId, bool sameSource, bool member, int accepted)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceHash = new string('a', 64);
            var sourceReport = new EvidenceReport("1", DateTimeOffset.UtcNow,
                [new("packets", "fixture", sourceHash, 1), new("master", "fixture", MasterHash, 1)],
                new("Unverified", null, ["fixture"], "Fixture only"), "Unverified", new([], [], 0),
                new(0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], [], []), [], new("Blocked", ["FixtureOnly"]));
            File.WriteAllText(Path.Combine(directory, "audit.json"), JsonSerializer.Serialize(sourceReport, EvidenceJson.Options));
            File.Copy(Path.Combine(EvidenceDirectory(), "checkpoint-master.jsonl"), Path.Combine(directory, "master-rows.jsonl"));
            const string prefix = "/umamusume/single_mode_pioneer/";
            var before = JsonNode.Parse("""
                {"chara_info":{"single_mode_chara_id":1,"turn":12,"playing_state":34,"vital":60,"max_vital":100},
                "unchecked_event_array":[],"pioneer_data_set":{"pioneer_info":{"deck_id":3,"pioneer_pt":597,"planning_cost":400},
                "check_point_info_array":[]}}
                """)!.AsObject();
            var submitted = before.DeepClone().AsObject();
            submitted["chara_info"]!["playing_state"] = 5;
            submitted["pioneer_data_set"]!["pioneer_info"]!["planning_cost"] = 500;
            submitted["pioneer_data_set"]!["check_point_info_array"] = JsonNode.Parse("""[{"check_point_type":1,"result_state":2}]""");
            submitted["unchecked_event_array"] = JsonNode.Parse("""[{"event_id":123,"story_id":400011004}]""");
            var settled = submitted.DeepClone().AsObject();
            settled["chara_info"]!["playing_state"] = 35;
            settled["chara_info"]!["vital"] = 79;
            settled["pioneer_data_set"]!["pioneer_info"]!["pioneer_pt"] = 0;
            settled["unchecked_event_array"] = new JsonArray();
            var observations = new[]
            {
                PioneerNormalizer.Normalize(new JsonObject { ["data"] = before }, "packets", 1, "before", prefix + "check_event", "fixture", "2026-01-01T00:00:00Z"),
                PioneerNormalizer.Normalize(new JsonObject { ["data"] = submitted }, "packets", 3, "submit", prefix + "check_point", "fixture", "2026-01-01T00:00:01Z"),
                PioneerNormalizer.Normalize(new JsonObject { ["data"] = settled }, "packets", 5, "settle", prefix + "check_event", "fixture", "2026-01-01T00:00:02Z"),
            };
            var requestSource = sameSource ? "packets" : "other";
            var requests = new[]
            {
                new ObservedAction("packets", 2, "submit", prefix + "check_point", "", new JsonObject { ["current_turn"] = 12 }),
                new ObservedAction(requestSource, requestId, "settle", prefix + "check_event", "", JsonNode.Parse("""
                    {"single_mode_check_event_request_common":{"current_turn":12,"event_id":123,"choice_number":0}}
                    """)!.AsObject()),
            };
            var exchanges = new[]
            {
                new ExchangeSummary("submit", prefix + "check_point", "Paired", [new("packets", 2)], [new("packets", 3)], 1, 1, 0, [], []),
                new ExchangeSummary("settle", prefix + "check_event", "Paired", member ? [new(requestSource, requestId)] : [], [new("packets", 5)], 1, 1, 0, [], []),
            };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"), observations.Select(x => JsonSerializer.Serialize(x, json)));
            File.WriteAllLines(Path.Combine(directory, "pioneer-actions.jsonl"), requests.Select(x => JsonSerializer.Serialize(x, json)));
            File.WriteAllLines(Path.Combine(directory, "exchanges.jsonl"), exchanges.Select(x => JsonSerializer.Serialize(x, json)));
            var report = CheckpointAudit.Run(directory, Path.Combine(directory, "result"));
            Assert.Equal(1, report.MatchedSubmissions);
            Assert.Equal(1, report.SubmissionsWithoutResourceChanges);
            Assert.Equal(accepted, report.MatchedEventSelections);
            Assert.Equal(accepted, report.CandidateResourceMatches);
            Assert.Equal(5, report.Inputs.Count);
            Assert.Equal("CandidateOnly", report.Validation.Status);
            if (accepted == 0) Assert.Contains(report.Issues, x => x.Code == "CheckpointPairingOrOrderUnproven");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
