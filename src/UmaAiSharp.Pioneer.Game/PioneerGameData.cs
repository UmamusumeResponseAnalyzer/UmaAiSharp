using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public static class PioneerGameData
{
    public static ImmutableArray<PioneerRaceHistory>? ReadRaceHistory(JsonObject common)
        => common["race_history"] is JsonArray rows
            ? [.. Objects(rows, "race_history").Select(row => new PioneerRaceHistory(
                Int(row, "turn"), Int(row, "program_id"), Int(row, "result_rank")))] : null;

    public static ImmutableArray<PioneerRaceReservation>? ReadRaceReservations(JsonObject common)
        => common["reserved_race_info"]?["reserved_race_array"] is JsonArray decks
            ? [.. Objects(decks, "reserved_race_array").Where(deck => Int(deck, "deck_num") == 0)
                .SelectMany(deck => Objects(Array(deck, "race_array"), "race_array"))
                .Select(row => new PioneerRaceReservation(Int(row, "year"), Int(row, "program_id")))] : null;

    public static GoldShipTrainingBoost? ReadGoldShipTrainingBoost(JsonObject common)
    {
        if (common["chara_info"]?["card_id"]?.GetValue<int>() != 100701
            || common["gain_parameter_info"]?["effected_command_id_array"] is not JsonArray { Count: 1 } commands)
            return null;
        var id = commands[0]!.GetValue<int>();
        return id is 101 or 105 or 102 or 103 or 106 ? new(id) : null;
    }

    public static ImmutableArray<TemporarySupportGrant> ReadTemporarySpecialty(JsonObject common, string endpoint, int sourceTurn)
    {
        if (sourceTurn < 1 || endpoint is not ("exec_command" or "shima_training_exec" or "check_event")) return [];
        var source = endpoint == "exec_command" ? TemporarySupportGrantSource.TuckerTraining
            : endpoint == "shima_training_exec" ? TemporarySupportGrantSource.IslandTraining : TemporarySupportGrantSource.TuckerEvent;
        if ((common["gain_partner_support_effect_array"] ?? common["gain_parameter_info"]?["gain_partner_support_effect_array"])
            is not JsonArray rows) return [];
        return [.. rows.OfType<JsonObject>().SelectMany(row => Objects(Array(row, "effect_array"), "effect_array")
            .Where(effect => Int(effect, "effect_type") == 19 && Int(row, "training_partner_id") is >= 1 and <= 6)
            .Select(effect => new TemporarySupportGrant(sourceTurn, source, null, Int(row, "training_partner_id"),
                19, Int(effect, "effect_value"))))];
    }

    // Six support bits per training. Only a ready board can witness who was selected.
    public static int? ReadTrainingPartnerMasks(JsonObject common)
    {
        if (common["chara_info"]?["playing_state"]?.GetValue<int>() != 1
            || common["unchecked_event_array"] is not JsonArray { Count: 0 }
            || common["home_info"]?["command_info_array"] is not JsonArray commands)
            return null;
        var masks = 0;
        foreach (var row in commands.OfType<JsonObject>())
        {
            var index = TrainingBaseTable.CommandIndex(Int(row, "command_id"));
            if (index < 0 || Int(row, "is_enable") != 1) continue;
            foreach (var partner in Integers(Array(row, "training_partner_array"), "training_partner_array"))
                if (partner is >= 1 and <= 6) masks |= 1 << (index * 6 + partner - 1);
        }
        return masks;
    }

    public static int[]? ReadTrainingExecutionCounts(JsonObject? pioneer)
    {
        var node = pioneer?["training_exec_info_array"];
        if (node is null) return null;
        if (node is not JsonArray { Count: 5 } array)
            throw new InvalidDataException("训练历史要求五项 training_exec_info_array。");
        var values = new[] { -1, -1, -1, -1, -1 };
        foreach (var row in Objects(array, "training_exec_info_array"))
        {
            var command = Int(row, "base_command_id");
            var index = TrainingBaseTable.CommandIndex(command);
            var count = Int(row, "exec_count");
            if (command >= 3601 || index < 0 || count < 0 || values[index] != -1)
                throw new InvalidDataException("训练历史包含未知、重复指令或负次数。");
            values[index] = count;
        }
        return values;
    }

    public static JsonObject? GetCommon(JsonObject? data, string endpoint)
    {
        var common = endpoint[(endpoint.LastIndexOf('/') + 1)..] switch
        {
            "load" => data?["single_mode_load_common"],
            "start" => data?["single_mode_start_common"],
            "finish" => data?["single_mode_finish_common"],
            _ => data,
        };
        return common switch
        {
            null => null,
            JsonObject value => value,
            _ => throw new InvalidOperationException("育成common必须为对象。"),
        };
    }

    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string RunToken(JsonObject chara) => Hash(Int(chara, "card_id") + "/" + chara["start_time"]!.ToJsonString());
}
