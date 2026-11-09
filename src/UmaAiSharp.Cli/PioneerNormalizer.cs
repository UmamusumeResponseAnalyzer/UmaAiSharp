using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UmaAiSharp.Cli.Evidence;

public static class PioneerNormalizer
{
    static readonly string[] ScalarNames =
    [
        "turn", "scenario_id", "playing_state", "state", "card_id", "rarity", "talent_level",
        "speed", "stamina", "power", "guts", "wiz", "max_speed", "max_stamina", "max_power", "max_guts", "max_wiz",
        "vital", "max_vital", "motivation", "skill_point", "fans",
    ];
    static readonly string[] CommonSections =
    [
        "chara_info", "home_info", "unchecked_event_array", "select_index_info_array", "race_start_info",
        "race_history", "race_condition_array", "gain_parameter_info", "not_up_parameter_info",
        "not_down_parameter_info", "gain_partner_support_effect_array", "event_effected_factor_array",
        "effected_factor_array", "race_reward_info", "race_add_reward_info",
    ];

    public static PioneerObservation Normalize(JsonObject root, string source, long packetId,
        string exchangeKey, string endpoint, string? actorKey, string capturedAtUtc)
    {
        var data = root["data"] switch
        {
            null => null,
            JsonObject value => value,
            _ => throw new InvalidOperationException("data必须为对象。"),
        };
        var common = GetCommon(data, endpoint);
        var chara = common?["chara_info"] switch
        {
            null => null,
            JsonObject value => value,
            _ => throw new InvalidOperationException("chara_info必须为对象。"),
        };
        var fields = ScalarNames.ToDictionary(name => name, name => Integer(chara, name), StringComparer.Ordinal);
        var runId = Integer(chara, "single_mode_chara_id");
        var runKey = actorKey is not null && runId.Value is { } id ? Hash($"{actorKey}\n{id}") : null;
        var presence = new Dictionary<string, string>(StringComparer.Ordinal);
        // Preserve every response-data section, including fields newer than this projection.
        // Scalar presence and decision evidence remain explicit rather than inferred from defaults.
        var visible = Redact(data) as JsonObject ?? new JsonObject();
        foreach (var section in CommonSections)
        {
            presence[section] = Presence(common, section);
        }
        foreach (var section in new[] { "pioneer_data_set", "auto_planning_info" })
        {
            presence[section] = Presence(data, section);
        }
        var evidence = new List<string>();
        if (common?["unchecked_event_array"] is JsonArray { Count: > 0 }) evidence.Add("PendingEventsObserved");
        if (common?["select_index_info_array"] is JsonArray { Count: > 0 }) evidence.Add("SelectionIndicesObserved");
        if (common?["race_start_info"] is JsonObject) evidence.Add("RaceStartObserved");
        if (common?["home_info"]?["command_info_array"] is JsonArray { Count: > 0 }) evidence.Add("HomeCommandPreviewsObserved");
        if (data?["pioneer_data_set"]?["planning_info_array"] is JsonArray { Count: > 0 }) evidence.Add("ExistingConstructionPlanObserved");
        if (data?["pioneer_data_set"]?["shima_training_info"] is JsonObject) evidence.Add("IslandTrainingInfoObserved");
        return new(source, packetId, exchangeKey, endpoint, runKey, capturedAtUtc, fields, presence, evidence, visible);
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

    public static JsonObject ActionFields(JsonObject root)
    {
        return (JsonObject)Redact(root)!;
    }

    public static ObservedInteger Integer(JsonObject? value, string name)
    {
        if (value is null || !value.TryGetPropertyValue(name, out var field)) return new("Missing", null);
        if (field is null) return new("Null", null);
        return field is JsonValue scalar && scalar.TryGetValue<long>(out var integer)
            ? new("Present", integer) : new("InvalidType", null);
    }

    static string Presence(JsonObject? parent, string key) => parent is null || !parent.ContainsKey(key)
        ? "Missing" : parent[key] is null ? "Null" : "Present";

    public static JsonNode? Redact(JsonNode? node) => node switch
    {
        JsonObject map => new JsonObject(map.Where(pair => !Sensitive(pair.Key))
            .Select(pair => KeyValuePair.Create(pair.Key, Redact(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Redact).ToArray()),
        _ => node?.DeepClone(),
    };

    static bool Sensitive(string key) => key.Contains("viewer", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("session", StringComparison.OrdinalIgnoreCase)
        || key.Contains("device", StringComparison.OrdinalIgnoreCase)
        || key is "sid" or "udid" or "user_name" or "trainer_name" or "single_mode_chara_id"
            or "ip_address" or "keychain" or "steam_id";

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
