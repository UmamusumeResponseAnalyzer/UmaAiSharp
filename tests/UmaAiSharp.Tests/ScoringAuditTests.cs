using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Pioneer.Scoring;

namespace UmaAiSharp.Tests;

public sealed class ScoringAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Scoring-" + Guid.NewGuid().ToString("N"));

    public ScoringAuditTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void AuditDeduplicatesInputsButPreservesContradictingObservedScoresAndVersionLimits()
    {
        var packets = Database("packets.sqlite", "CREATE TABLE packets(id INTEGER PRIMARY KEY,direction TEXT,endpoint TEXT,app_ver TEXT,res_ver TEXT,payload BLOB)");
        var master = Database("master.mdb", "CREATE TABLE skill_data(id INTEGER PRIMARY KEY,grade_value INTEGER,tag_id TEXT); INSERT INTO skill_data VALUES(200001,125,'101')");
        var character = Character(19317);
        var current = (JsonObject)character.DeepClone();
        current.Remove("trained_chara_id");
        current.Remove("rank_score");
        AddPacket(packets, new JsonObject { ["data"] = new JsonObject { ["trained_chara"] = new JsonArray(character.DeepClone()) } });
        AddPacket(packets, new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["single_mode_finish_common"] = new JsonObject
                {
                    ["trained_chara_id"] = 47,
                    ["chara_info"] = current,
                    ["trained_chara"] = new JsonArray(character.DeepClone())
                }
            }
        });
        AddPacket(packets, new JsonObject { ["data"] = new JsonObject { ["trained_chara"] = new JsonArray(Character(19318)) } });
        var before = new[] { packets, master }.Select(Hash).ToArray();
        var output = Path.Combine(directory, "report");

        var result = ScoringAudit.Run(packets, master, output);

        Assert.Equal(4, result.CandidateObservations);
        Assert.Equal(2, result.DuplicateObservations);
        Assert.Equal(0, result.IncompleteObservations);
        Assert.Equal(2, result.UniqueInputScorePairs);
        Assert.Equal(2, result.EvaluatedStates);
        Assert.Equal(1, result.ExactMatches);
        Assert.Equal(1, result.ConflictingInputScores);
        Assert.Equal([new ScoreValueCount(0, 1), new ScoreValueCount(1, 1)], result.ResidualDistribution);
        Assert.Equal("Blocked", result.Validation.Status);
        Assert.Contains("CandidateScoreMismatch", result.Validation.Blockers);
        Assert.DoesNotContain("MasterResourceVersionUnverified", result.Validation.Blockers);
        Assert.Contains("MasterResourceVersionUnverified", result.ApplicabilityLimits);
        Assert.Contains("AttributeAndSkillComponentsNotIndependentlyIdentified", result.Validation.Blockers);
        Assert.Equal("Unverified", result.VersionBinding.Status);
        Assert.Equal(before, new[] { packets, master }.Select(Hash).ToArray());
        var samples = File.ReadLines(Path.Combine(output, "scoring-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<ScoringAuditObservation>(line, EvidenceJson.Options)!).ToArray();
        Assert.Equal(2, samples.Length);
        Assert.All(samples, sample => Assert.Equal(112, sample.Candidate!.SkillScore));
        Assert.Throws<InvalidDataException>(() => ScoringAudit.Run(packets, master, output));
    }

    [Fact]
    public void MissingMasterSkillIsUnscoredRatherThanAssignedZero()
    {
        var packets = Database("packets.sqlite", "CREATE TABLE packets(id INTEGER PRIMARY KEY,direction TEXT,endpoint TEXT,app_ver TEXT,res_ver TEXT,payload BLOB)");
        var master = Database("master.mdb", "CREATE TABLE skill_data(id INTEGER PRIMARY KEY,grade_value INTEGER,tag_id TEXT)");
        AddPacket(packets, new JsonObject { ["data"] = new JsonObject { ["trained_chara"] = new JsonArray(Character(19317)) } });

        var result = ScoringAudit.Run(packets, master, Path.Combine(directory, "report"));

        Assert.Equal(0, result.EvaluatedStates);
        Assert.Equal(1, result.UnscoredStates);
        Assert.Equal([200001], result.MissingMasterSkillIds);
        Assert.Empty(result.ResidualDistribution);
    }

    [Fact]
    public void CandidateRoundsEachSkillAfterTagMultipliersAndKeepsSpecialIdRulesExplicit()
    {
        var aptitudes = ImmutableArray.Create(5, 7, 1, 1);
        Assert.Equal(112, ScoringCandidates.SkillToPoint(new(200001, 125, [101]), 1, aptitudes, aptitudes));
        Assert.Equal(138, ScoringCandidates.SkillToPoint(new(200001, 125, [101, 102]), 1, aptitudes, aptitudes));
        Assert.Equal(101, ScoringCandidates.SkillToPoint(new(200001, 125, [101, 201]), 1, aptitudes, aptitudes));
        Assert.Equal(1020, ScoringCandidates.SkillToPoint(new(100001, 125, [101]), 6, aptitudes, aptitudes));
        Assert.Equal(600, ScoringCandidates.SkillToPoint(new(10001, 125, [101]), 5, aptitudes, aptitudes));
        Assert.Equal(0, ScoringCandidates.SkillToPoint(new(1000001, 125, [101]), 1, aptitudes, aptitudes));
    }

    static JsonObject Character(int score) => new()
    {
        ["trained_chara_id"] = 47, ["rank_score"] = score,
        ["speed"] = 1200, ["stamina"] = 1200, ["power"] = 1200, ["guts"] = 1200, ["wiz"] = 1200,
        ["proper_running_style_nige"] = 5, ["proper_running_style_senko"] = 7,
        ["proper_running_style_sashi"] = 1, ["proper_running_style_oikomi"] = 1,
        ["proper_distance_short"] = 7, ["proper_distance_mile"] = 7,
        ["proper_distance_middle"] = 7, ["proper_distance_long"] = 7,
        ["skill_array"] = new JsonArray(new JsonObject { ["skill_id"] = 200001, ["level"] = 1 })
    };

    string Database(string name, string sql)
    {
        var path = Path.Combine(directory, name);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return path;
    }

    static void AddPacket(string path, JsonObject payload)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO packets(direction,endpoint,app_ver,res_ver,payload) VALUES('response','/test','app','res',$payload)";
        command.Parameters.AddWithValue("$payload", MessagePackSerializer.ConvertFromJson(payload.ToJsonString()));
        command.ExecuteNonQuery();
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
