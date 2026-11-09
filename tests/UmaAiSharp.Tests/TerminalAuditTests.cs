using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;

namespace UmaAiSharp.Tests;

public sealed class TerminalAuditTests
{
    [Fact]
    public void SelectsExactIdentityAndKeepsTotalDistinctFromUnspent()
    {
        var common = Finish();
        var result = TerminalAudit.SelectFinishedCharacter(common);
        Assert.Equal(22, result["trained_chara_id"]!.GetValue<int>());
        Assert.Equal(3000, result["total_skill_pt"]!.GetValue<int>());
        result["speed"] = 5;
        Assert.Equal(1300, common["trained_chara"]![1]!["speed"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("attributes")]
    [InlineData("skills")]
    [InlineData("phase")]
    public void RejectsAmbiguousOrInconsistentResults(string mutation)
    {
        var common = Finish();
        switch (mutation)
        {
            case "ambiguous": ((JsonArray)common["trained_chara"]!).Add(common["trained_chara"]![1]!.DeepClone()); break;
            case "missing": common["trained_chara_id"] = 99; break;
            case "attributes": common["chara_info"]!["speed"] = 1301; break;
            case "skills": common["chara_info"]!["skill_array"] = new JsonArray(); break;
            case "phase": common["chara_info"]!["playing_state"] = 5; break;
        }
        Assert.Throws<InvalidDataException>(() => TerminalAudit.SelectFinishedCharacter(common));
    }

    [Fact]
    public void AuditDoesNotTreatScenarioCompleteAsTerminal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var observations = new[]
            {
                Observation(2, "pioneer_complete", new JsonObject()),
                Observation(4, "finish", new JsonObject { ["single_mode_finish_common"] = Finish() }),
            };
            File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"), observations.Select(x => JsonSerializer.Serialize(x)));
            var exchanges = observations.Select(x => new ExchangeSummary(x.ExchangeKey, x.Endpoint, "Paired",
                [new("fixture", x.PacketId - 1)], [new("fixture", x.PacketId)], 1, 1, 0, [], []));
            File.WriteAllLines(Path.Combine(directory, "exchanges.jsonl"), exchanges.Select(x => JsonSerializer.Serialize(x)));
            var report = TerminalAudit.Run(directory, Path.Combine(directory, "output"));
            Assert.Equal(1, report.Counts["ScenarioCompleteIsNotFinish"]);
            Assert.Equal(1, report.Counts["MatchedFinishResults"]);
            Assert.Equal(1, report.Counts["TotalPtDiffersFromUnspent"]);
            Assert.Empty(report.Issues);
            Assert.Throws<InvalidDataException>(() => TerminalAudit.Run(directory, Path.Combine(directory, "output")));
        }
        finally { Directory.Delete(directory, true); }
    }

    static PioneerObservation Observation(int id, string endpoint, JsonObject visible) => new("fixture", id,
        id.ToString(), "/single_mode_pioneer/" + endpoint, "run", "2026-11-08T00:00:00Z",
        new Dictionary<string, ObservedInteger>(), new Dictionary<string, string>(), [], visible);

    static JsonObject Finish() => JsonNode.Parse("""
        {
          "trained_chara_id":22,
          "chara_info":{"scenario_id":11,"state":4,"playing_state":11,
            "speed":1300,"stamina":700,"power":1201,"guts":850,"wiz":1000,
            "max_speed":1800,"max_stamina":1400,"max_power":1600,"max_guts":1500,"max_wiz":1400,
            "skill_point":2500,"skill_array":[{"skill_id":100011,"level":1}]},
          "trained_chara":[{"trained_chara_id":11},
            {"trained_chara_id":22,"speed":1300,"stamina":700,"power":1201,"guts":850,"wiz":1000,
             "total_skill_pt":3000,"rank_score":20000,"rank":30,
             "skill_array":[{"skill_id":100011,"level":1}],"factor_info_array":[],"factor_extend_array":[],"scenario_data_array":[]}]
        }
        """)!.AsObject();
}
