using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.Tests;

public sealed class TrainingFormulaAuditTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-TrainingFormula-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Paired", "valid", 1, 0)]
    [InlineData("ConflictingCapture", "valid", 0, 0)]
    [InlineData("Paired", "group-card", 0, 1)]
    public void AuditRequiresUnambiguousCaptureAndCompleteTypedInputs(string exchangeStatus, string inputKind, int checkedSamples, int unsupported)
    {
        Directory.CreateDirectory(directory);
        var masterPath = Path.Combine(directory, "master.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = masterPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE single_mode_training(command_id INTEGER, command_level INTEGER, base_command_id INTEGER, failure_rate INTEGER);
                INSERT INTO single_mode_training VALUES(101,1,101,520);
                CREATE TABLE support_card_data(id INTEGER, rarity INTEGER, effect_table_id INTEGER, unique_effect_id INTEGER, command_id INTEGER, support_card_type INTEGER);
                INSERT INTO support_card_data VALUES(123,2,123,0,101,$cardType);
                CREATE TABLE support_card_unique_effect(id INTEGER);
                CREATE TABLE support_card_effect_table(id INTEGER,type INTEGER,init INTEGER,limit_lv5 INTEGER,limit_lv10 INTEGER,limit_lv15 INTEGER,limit_lv20 INTEGER,limit_lv25 INTEGER,limit_lv30 INTEGER,limit_lv35 INTEGER,limit_lv40 INTEGER,limit_lv45 INTEGER,limit_lv50 INTEGER);
                INSERT INTO support_card_effect_table VALUES(123,19,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1);
                CREATE TABLE support_card_level(rarity INTEGER,level INTEGER,total_exp INTEGER);
                INSERT INTO support_card_level VALUES(2,1,0);
                CREATE TABLE card_data(id INTEGER,chara_id INTEGER,talent_speed INTEGER,talent_stamina INTEGER,talent_pow INTEGER,talent_guts INTEGER,talent_wiz INTEGER);
                INSERT INTO card_data VALUES(100001,1000,0,0,0,0,0);
                CREATE TABLE single_mode_11_check_point_eff(effect_type INTEGER,effect_value_1 INTEGER,effect_value_2 INTEGER,check_point_type INTEGER,result_state INTEGER);
                CREATE TABLE single_mode_11_planning(id INTEGER,deck_id INTEGER,check_point_type INTEGER,planning_turn INTEGER,check_point_turn INTEGER,target_pt INTEGER);
                """;
            command.Parameters.AddWithValue("$cardType", inputKind == "group-card" ? 3 : 1);
            command.ExecuteNonQuery();
        }
        var visible = JsonNode.Parse("""
            {
              "chara_info":{"card_id":100001,"turn":5,"motivation":3,"fans":0,"speed":100,"stamina":100,"power":100,"guts":100,"wiz":100,"vital":43,"skill_point":0,
                "max_speed":1800,"max_stamina":1800,"max_power":1800,"max_guts":1800,"max_wiz":1800,"max_vital":100,"chara_effect_id_array":[],
                "support_card_array":[{"position":1,"support_card_id":123,"exp":0}],"evaluation_info_array":[{"target_id":1,"evaluation":80}]},
              "home_info":{"command_info_array":[{"command_type":1,"command_id":101,"level":1,"is_enable":1,"failure_rate":13,"training_partner_array":[1],
                "params_inc_dec_info_array":[{"target_type":1,"value":12},{"target_type":3,"value":1},{"target_type":30,"value":6},{"target_type":10,"value":-20}]}]},
              "pioneer_data_set":{"command_info_array":[{"command_type":1,"command_id":101,"params_inc_dec_info_array":[]}],
                "evaluation_info_array":[{"target_id":1,"is_pioneer_team":1}],"check_point_info_array":[]}
            }
            """)!.AsObject();
        var training = visible["home_info"]!["command_info_array"]![0]!.AsObject();
        if (inputKind == "malformed-preview") training["params_inc_dec_info_array"] = new JsonArray((JsonNode?)null);
        if (inputKind == "duplicate-partner") training["training_partner_array"] = new JsonArray(1, 1);
        var observation = new PioneerObservation("fixture", 2, "exchange", "/single_mode/check_event", "run", "2026-11-05T00:00:00Z",
            new Dictionary<string, ObservedInteger>(), new Dictionary<string, string>(), [], visible);
        var exchange = new ExchangeSummary("exchange", observation.Endpoint, exchangeStatus, [new("fixture", 1)], [new("fixture", 2)], 1, 1, 0, [], []);
        File.WriteAllText(Path.Combine(directory, "pioneer-observations.jsonl"), JsonSerializer.Serialize(observation));
        File.WriteAllText(Path.Combine(directory, "exchanges.jsonl"), JsonSerializer.Serialize(exchange));

        var report = TrainingFormulaAudit.Run(directory, masterPath, Path.Combine(directory, "report"));

        Assert.Equal(checkedSamples, report.Counts.GetValueOrDefault("FailureChecked"));
        Assert.Equal(checkedSamples, report.Counts.GetValueOrDefault("CommonMatched"));
        Assert.Equal(unsupported, report.Counts.GetValueOrDefault("UnsupportedInput"));
        Assert.Equal(unsupported, report.Issues.Count);
        if (checkedSamples == 0) Assert.Equal("NoEligibleSamples", report.FailurePreviewStatus);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
