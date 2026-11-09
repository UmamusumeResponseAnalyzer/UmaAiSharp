using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Pioneer;

namespace UmaAiSharp.Tests;

public sealed class PioneerConstructionAuditTests
{
    [Fact]
    public void ExportReplayKeepsObservationMatchesSeparateFromMissingRequestEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-Construction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceHash = new string('a', 64);
            var report = new EvidenceReport("1", DateTimeOffset.UtcNow,
                [new("packets", "fixture", sourceHash, 1), new("master", "fixture", PioneerConstructionRules.ObservedMasterSha256, 1)],
                new("Unverified", null, ["fixture"], "Fixture only"), "Unverified", new([], [], 0),
                new(0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], [], []), [], new("Blocked", ["FixtureOnly"]));
            File.WriteAllText(Path.Combine(directory, "audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options));
            File.WriteAllText(Path.Combine(directory, "master-rows.jsonl"), """
                {"table":"single_mode_11_facility_kind","row":{"facility_id":101,"command_id":101}}
                {"table":"single_mode_11_facility_kind","row":{"facility_id":106,"command_id":0}}
                {"table":"single_mode_11_facility_state","row":{"id":1,"facility_id":101,"facility_lv":0,"icon_id":0,"state_id":10100}}
                {"table":"single_mode_11_facility_state","row":{"id":2,"facility_id":101,"facility_lv":1,"icon_id":0,"state_id":10110}}
                {"table":"single_mode_11_facility_state","row":{"id":3,"facility_id":106,"facility_lv":0,"icon_id":0,"state_id":10600}}
                {"table":"single_mode_11_facility_state","row":{"id":4,"facility_id":106,"facility_lv":1,"icon_id":0,"state_id":10610}}
                {"table":"single_mode_11_facility_lv_up","row":{"id":1,"facility_id":101,"current_state_id":10100,"target_state_id":10110,"cost":100}}
                {"table":"single_mode_11_facility_lv_up","row":{"id":2,"facility_id":106,"current_state_id":10600,"target_state_id":10610,"cost":200}}
                {"table":"single_mode_11_planning","row":{"id":1,"deck_id":3,"check_point_type":2,"planning_turn":12,"check_point_turn":24,"target_pt":500}}
                {"table":"single_mode_11_gain_training_pt","row":{"id":1,"deck_id":3,"turn_min":13,"turn_max":24,"gain_shima_training_pt":250}}
                """);
            File.WriteAllText(Path.Combine(directory, "exchanges.jsonl"), "");
            File.WriteAllText(Path.Combine(directory, "pioneer-actions.jsonl"), "");
            var before = JsonNode.Parse("""
                {"data":{"single_mode_load_common":{"chara_info":{"single_mode_chara_id":1,"turn":15,"playing_state":5},
                "unchecked_event_array":[{"story_id":400011107,"event_id":42}]},"pioneer_data_set":{
                "pioneer_info":{"pioneer_pt":311,"planning_cost":500,"deck_id":3,"gain_shima_training_pt_array":[250,500]},
                "planning_info_array":[{"priority":1,"facility_id":101,"prev_state_id":10100,"target_state_id":10110},
                {"priority":2,"facility_id":106,"prev_state_id":10600,"target_state_id":10610}],
                "facility_info_array":[{"facility_id":101,"state_id":10100},{"facility_id":106,"state_id":10600}],
                "shima_training_info":{"remain_num":0}}}}
                """)!.AsObject();
            var afterData = before["data"]!.DeepClone().AsObject();
            var common = afterData["single_mode_load_common"]!.AsObject();
            afterData["chara_info"] = common["chara_info"]!.DeepClone();
            afterData["unchecked_event_array"] = new JsonArray();
            afterData.Remove("single_mode_load_common");
            afterData["pioneer_data_set"]!["facility_info_array"]![0]!["state_id"] = 10110;
            afterData["pioneer_data_set"]!["shima_training_info"]!["remain_num"] = 1;
            var observations = new[]
            {
                PioneerNormalizer.Normalize(before, "packets", 1, "before", "/umamusume/single_mode_pioneer/load", "fixture", "2026-01-01T00:00:00Z"),
                PioneerNormalizer.Normalize(new JsonObject { ["data"] = afterData }, "packets", 3, "after", "/umamusume/single_mode_pioneer/check_event", "fixture", "2026-01-01T00:00:01Z"),
            };
            File.WriteAllLines(Path.Combine(directory, "pioneer-observations.jsonl"), observations.Select(x => JsonSerializer.Serialize(x, new JsonSerializerOptions(EvidenceJson.Options) { WriteIndented = false })));

            var output = Path.Combine(directory, "output");
            var result = ConstructionAudit.Run(directory, output);
            Assert.Equal(2, result.PlanObservations);
            Assert.Equal(1, result.SettlementObservations);
            Assert.Equal(0, result.RawPointMatches);
            Assert.Equal(1, result.ReachedThresholdMatches);
            Assert.Equal(1, result.CandidateFacilityMatches);
            Assert.Equal(1, result.CandidateTicketMatches);
            Assert.Equal(0, result.MatchedEventSelections);
            Assert.Equal("Unverified", result.VersionBinding.Status);
            Assert.Equal("Blocked", result.Validation.Status);
            Assert.Contains("RequestMissingOrConflicting", File.ReadAllText(Path.Combine(output, "construction-settlements.jsonl")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
