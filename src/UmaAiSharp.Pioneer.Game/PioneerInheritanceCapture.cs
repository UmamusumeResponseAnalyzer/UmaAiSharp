using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Inheritance;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public readonly record struct PioneerInheritanceLottery(int LotteryId, AttributeValues Attributes, int SkillPoints, int Value);

// One tracker per viewer. A run keeps its opening pedigree when the same parents are strengthened later.
public sealed class PioneerInheritanceCapture
{
    readonly Dictionary<int, PioneerInheritanceParent> archive = [];
    (string Run, PioneerInheritanceParent First, PioneerInheritanceParent Second, bool? UmaPlan)? frozen;

    public void Observe(JsonObject data, string endpoint)
    {
        if (endpoint.EndsWith("/load/index", StringComparison.Ordinal))
            Remember(data["trained_chara"]);
        else if (endpoint.EndsWith("/pre_single_mode/index", StringComparison.Ordinal)
            || endpoint.EndsWith("/pre_single_mode/get_succession_trained_chara", StringComparison.Ordinal))
            Remember(data["succession_trained_chara_data"]?["succession_trained_chara_array"]);
        else if (endpoint.EndsWith("/trained_chara/load", StringComparison.Ordinal))
            Remember(data["trained_chara"]);
        else if (endpoint.EndsWith("/finish", StringComparison.Ordinal))
            Remember(data["single_mode_finish_common"]?["trained_chara"]);
        else if (endpoint.EndsWith("/start", StringComparison.Ordinal)
            && PioneerGameData.GetCommon(data, endpoint) is { } common
            && common["chara_info"] is JsonObject chara && Int(chara, "scenario_id") == 11)
        {
            var run = PioneerGameData.RunToken(chara);
            if (frozen?.Run == run) return;
            var first = Int(chara, "succession_trained_chara_id_1");
            var second = Int(chara, "succession_trained_chara_id_2");
            Remember(common["add_trained_chara_array"]);
            frozen = archive.TryGetValue(first, out var parent1) && archive.TryGetValue(second, out var parent2)
                ? (run, parent1, new(parent2.Factors,
                    [.. parent2.Relations.Select(relation => relation with { Slot = relation.Slot + 3 })]),
                    common["is_umaplan"]?.GetValue<bool>())
                : null;
        }
    }

    public PioneerInheritanceContext? Read(JsonObject common)
    {
        var chara = common["chara_info"]!.AsObject();
        if (frozen is not { } family || family.Run != PioneerGameData.RunToken(chara)) return null;
        return new(family.First, family.Second,
            family.UmaPlan ?? true,
            [.. AptitudeNames.Select(name => Int(chara, name))]);
    }

    public static PioneerInheritanceLottery[] RankLotteries(JsonObject common)
    {
        if (common["unchecked_event_array"] is not JsonArray events
            || events.FirstOrDefault() is not JsonObject current || Int(current, "event_id") is not (7040 or 7041)
            || current["succession_event_info"]?["succession_gain_info_array"] is not JsonArray options)
            return [];
        var currentState = common["chara_info"]!.AsObject();
        var before = new AttributeValues(Int(currentState, "speed"), Int(currentState, "stamina"), Int(currentState, "power"),
            Int(currentState, "guts"), Int(currentState, "wiz"));
        var beforePoints = Int(currentState, "skill_point");
        var beforeValue = PioneerInheritanceRules.LotteryValue(before.Speed, before.Stamina, before.Power,
            before.Guts, before.Wisdom, beforePoints);
        return [.. Objects(options, "succession_gain_info_array").Select(option =>
        {
            var attributes = new AttributeValues(Int(option, "speed"), Int(option, "stamina"), Int(option, "power"),
                Int(option, "guts"), Int(option, "wiz"));
            var points = Int(option, "skill_point");
            return new PioneerInheritanceLottery(Int(option, "lottery_id"), new(
                    attributes.Speed - before.Speed, attributes.Stamina - before.Stamina, attributes.Power - before.Power,
                    attributes.Guts - before.Guts, attributes.Wisdom - before.Wisdom), points - beforePoints,
                PioneerInheritanceRules.LotteryValue(attributes.Speed, attributes.Stamina, attributes.Power,
                    attributes.Guts, attributes.Wisdom, points) - beforeValue);
        }).OrderByDescending(option => option.Value)];
    }

    void Remember(JsonNode? rows)
    {
        if (rows is JsonArray array)
            foreach (var row in array.OfType<JsonObject>())
                Remember(row);
        else if (rows is JsonObject row && row["trained_chara_id"] is not null) Remember(row);
    }

    void Remember(JsonObject row)
    {
        if (row["succession_chara_array"] is JsonArray ancestry && ancestry.Count >= 2
            && row["factor_info_array"] is JsonArray && row["factor_extend_array"] is JsonArray
            && row["win_saddle_id_array"] is JsonArray)
        {
            var members = Objects(ancestry, "succession_chara_array").ToDictionary(member => Int(member, "position_id"));
            members.Add(1, row);
            if (members.ContainsKey(10) && members.ContainsKey(20)
                && members.Values.All(member => member["factor_info_array"] is JsonArray && member["win_saddle_id_array"] is JsonArray))
                archive[Int(row, "trained_chara_id")] = ReadParent(row, members);
        }
    }

    static PioneerInheritanceParent ReadParent(JsonObject parent, Dictionary<int, JsonObject> members)
    {
        return new(new(
            [.. members.Select(member => new PedigreeFactorSlot(member.Key,
                [.. Objects(Array(member.Value, "factor_info_array"), "factor_info_array").Select(factor => Int(factor, "factor_id"))]))],
            [.. Objects(Array(parent, "factor_extend_array"), "factor_extend_array").Select(row => new PedigreeFactorExtension(
                Int(row, "position_id"), Int(row, "base_factor_id"), Int(row, "factor_id"), row["register_time"]?.GetValue<string>()))]),
            [.. new[] { 1, 10, 20 }.Select((position, index) => new RelationSource((InheritanceSlot)index,
                Int(members[position], "card_id") / 100,
                [.. Integers(Array(members[position], "win_saddle_id_array"), "win_saddle_id_array")]))]);
    }

    static readonly string[] AptitudeNames =
    [
        "proper_ground_turf", "proper_ground_dirt",
        "proper_distance_short", "proper_distance_mile", "proper_distance_middle", "proper_distance_long",
        "proper_running_style_nige", "proper_running_style_senko", "proper_running_style_sashi", "proper_running_style_oikomi",
    ];
}
