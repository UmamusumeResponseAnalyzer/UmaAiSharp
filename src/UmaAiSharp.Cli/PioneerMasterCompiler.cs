using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Events;

namespace UmaAiSharp.Cli.Evidence;

public sealed class PioneerMasterCompiler
{
    public static PioneerMasterData ConstructionData(SqliteConnection db, string masterSha) => new(masterSha,
        [.. Rows(db, "single_mode_11_facility_kind").Select(row => new FacilityKind(row["facility_id"], row["command_id"]))],
        [.. Rows(db, "single_mode_11_facility_state").Select(row => new FacilityState(row["id"], row["facility_id"], row["facility_lv"], row["icon_id"], row["state_id"]))],
        [.. Rows(db, "single_mode_11_facility_lv_up").Select(row => new FacilityUpgrade(row["id"], row["facility_id"], row["current_state_id"], row["target_state_id"], row["cost"]))],
        [.. Rows(db, "single_mode_11_planning").Select(row => new PlanningPeriod(row["id"], row["deck_id"], row["check_point_type"], row["planning_turn"], row["check_point_turn"], row["target_pt"]))],
        [.. Rows(db, "single_mode_11_gain_training_pt").Select(row => new IslandTicketThreshold(row["id"], row["deck_id"], row["turn_min"], row["turn_max"], row["gain_shima_training_pt"]))]);

    public Dictionary<(int Command, int Level), Dictionary<string, int>> Trainings { get; }
    readonly Dictionary<int, Dictionary<string, int>> cards;
    readonly Dictionary<int, Dictionary<string, int>> uniques;
    readonly Dictionary<int, Dictionary<string, int>[]> effects;
    readonly Dictionary<int, Dictionary<string, int>> characters;
    readonly Dictionary<string, int>[] levels;
    readonly Dictionary<(int Checkpoint, int Result), int> evaluation;
    readonly Dictionary<(int Checkpoint, int Result), int> pointBonuses;
    readonly ImmutableArray<UmaAiSharp.Pioneer.PlanningPeriod> planningPeriods;
    readonly Dictionary<(int Card, int Level), (ImmutableArray<SupportEffect> Panel, ImmutableArray<SupportUniqueEffect> Unique)> supportPanels = [];

    public PioneerMasterCompiler(string path)
    {
        using var connection = EvidenceAudit.OpenReadOnly(path);
        Trainings = Rows(connection, "single_mode_training").ToDictionary(row => (row["command_id"], row["command_level"]));
        cards = Rows(connection, "support_card_data").ToDictionary(row => row["id"]);
        uniques = Rows(connection, "support_card_unique_effect").ToDictionary(row => row["id"]);
        effects = Rows(connection, "support_card_effect_table").GroupBy(row => row["id"]).ToDictionary(group => group.Key, group => group.ToArray());
        characters = Rows(connection, "card_data").ToDictionary(row => row["id"]);
        levels = Rows(connection, "support_card_level");
        evaluation = Rows(connection, "single_mode_11_check_point_eff").Where(row => row["effect_type"] == 200 && row["effect_value_1"] == 0)
            .ToDictionary(row => (row["check_point_type"], row["result_state"]), row => row["effect_value_2"]);
        pointBonuses = Rows(connection, "single_mode_11_check_point_eff").Where(row => row["effect_type"] == 202 && row["effect_value_1"] == 9)
            .ToDictionary(row => (row["check_point_type"], row["result_state"]), row => row["effect_value_2"]);
        planningPeriods = [.. Rows(connection, "single_mode_11_planning").Select(row => new UmaAiSharp.Pioneer.PlanningPeriod(
            row["id"], row["deck_id"], row["check_point_type"], row["planning_turn"], row["check_point_turn"], row["target_pt"]))];
    }

    public PioneerStart.IslandTraining IslandStart(JsonObject frame, PioneerFacilityCompiler facilityCompiler,
        bool goldShipHistoryKnown, GoldShipTrainingBoost? boost)
    {
        var chara = frame["c"] as JsonObject ?? throw new InvalidDataException("岛训练前帧缺少角色c。");
        var pioneer = frame["s"] as JsonObject ?? throw new InvalidDataException("岛训练前帧缺少Pioneer状态s。");
        var home = frame["home"] as JsonObject ?? throw new InvalidDataException("岛训练前帧缺少home。");
        var cardId = Int(chara, "card_id");
        if (CharacterId(cardId) == 1007 && (cardId != 100701 || !goldShipHistoryKnown))
            throw new UnknownMechanismException("黄金船衣装或本回合历史未确认，不能生成岛训练收益。");
        if (cardId != 100701 && boost is not null) throw new InvalidDataException("非已确认黄金船衣装不能携带指定训练加成。");
        if (Int(chara, "state") != 0) throw new InvalidDataException("岛训练执行起点必须是正在育成的明确状态。");
        var command = Objects(Array(home, "command_info_array"), "command_info_array").Single(row => Int(row, "command_id") == 3101);
        var disabled = Integers(Array(home, "disable_command_id_array"), "disable_command_id_array");
        var positions = Objects(Array(chara, "support_card_array"), "support_card_array").Select(row => Int(row, "position")).ToArray();
        var deck = Participants(chara, pioneer, positions).Supports.Select(card => new IslandActionSupport(card.Position,
            card.CardId, SupportType(card.CardId), card.Level, card.Bond)).ToImmutableArray();
        var assignments = BuildIslandAssignments(chara, pioneer);
        var facilities = facilityCompiler.Facilities(pioneer);
        var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
            .ToDictionary(row => Int(row, "check_point_type"), row => Int(row, "result_state"));
        var checkpoint = checkpoints.Count == 0 ? 0 : checkpoints.Keys.Max();
        int? bonus = checkpoint == 5 ? null : checkpoint == 0 ? 0 : pointBonuses[(checkpoint, checkpoints[checkpoint])];
        var environment = Environment(chara, boost);
        var points = pioneer["pioneer_info"] as JsonObject ?? throw new InvalidDataException("缺少pioneer_info。");
        var shima = pioneer["shima_training_info"] as JsonObject ?? throw new InvalidDataException("缺少shima_training_info。");
        var state = new IslandActionState(Int(chara, "turn"), Int(chara, "playing_state"), Int(command, "is_enable") == 1 && !disabled.Contains(3101),
            Objects(Array(frame, "events"), "events").Select(row => Int(row, "story_id")).ToImmutableArray(), environment.Current, environment.Limits,
            Int(points, "pioneer_pt"), Int(shima, "remain_num"), checkpoint, bonus, deck, assignments, facilities,
            [.. Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array")],
            [.. Integers(Array(command, "tips_event_partner_array"), "tips_event_partner_array")]);
        var levels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
            .Where(row => Int(row, "command_id") is 101 or 102 or 103 or 105 or 106)
            .ToDictionary(row => Int(row, "command_id"), row => Int(row, "level"));
        if (levels.Count != 5) throw new UnknownMechanismException("岛训练缺少五项普通训练等级。");
        return new(state, new(environment, [levels[101], levels[105], levels[102], levels[103], levels[106]],
            facilityCompiler.Base(3101, facilities), facilities, assignments));
    }

    public PioneerStart.TrainingTurn TrainingStart(JsonObject frame, bool goldShipHistoryKnown, GoldShipTrainingBoost? boost,
        PioneerFacilityCompiler? facilityCompiler = null)
    {
        var chara = frame["c"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少角色c。");
        var pioneer = frame["s"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少Pioneer状态s。");
        var home = frame["home"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少home。");
        var cardId = Int(chara, "card_id");
        if (CharacterId(cardId) == 1007 && (cardId != 100701 || !goldShipHistoryKnown))
            throw new UnknownMechanismException("黄金船训练起点需要当前衣装及指定训练效果历史。");
        if (cardId != 100701 && boost is not null) throw new InvalidDataException("非黄金船训练起点不能携带指定训练加成。");
        if (Int(chara, "state") != 0) throw new InvalidDataException("训练执行起点必须处于育成中。");
        var positions = Objects(Array(chara, "support_card_array"), "support_card_array").Select(row => Int(row, "position")).ToArray();
        var deck = Participants(chara, pioneer, positions).Supports;
        var disabled = Integers(Array(home, "disable_command_id_array"), "disable_command_id_array");
        var levels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
            .Where(row => Int(row, "command_id") is 101 or 105 or 102 or 103 or 106).ToDictionary(row => Int(row, "command_id"));
        var counts = Objects(Array(pioneer, "training_exec_info_array"), "training_exec_info_array")
            .ToDictionary(row => Int(row, "base_command_id"), row => Int(row, "exec_count"));
        var commands = ImmutableArray.CreateBuilder<TrainingCommandOption>();
        foreach (var row in Objects(Array(home, "command_info_array"), "command_info_array"))
        {
            var commandId = Int(row, "command_id");
            if (commandId is not (101 or 105 or 102 or 103 or 106 or >= 3601 and <= 3605)) continue;
            var ordinaryCommand = TrainingBaseTable.OrdinaryCommand(commandId);
            var input = Input(chara, row, pioneer);
            if (commandId == ordinaryCommand && Int(levels[commandId], "level") != input.Level)
                throw new InvalidDataException("角色训练等级与行动面板不一致。");
            commands.Add(new(commandId, input.Level, counts[ordinaryCommand], input.GuestCount,
                [.. input.Supports.Select(card => card.Position)], [.. Integers(Array(row, "tips_event_partner_array"), "tips_event_partner_array")],
                Int(row, "is_enable") == 1 && !disabled.Contains(commandId), Trainings[(commandId, input.Level)]["failure_rate"],
                input.EvaluationTrainingPercent));
        }
        CampTrainingConfiguration? camp = null;
        if (commands.Any(command => command.CommandId is >= 3601 and <= 3605))
        {
            if (facilityCompiler is null) throw new UnknownMechanismException("合宿训练编译需要设施master与当前设施状态。");
            var facilities = facilityCompiler.Facilities(pioneer);
            camp = new([.. new[] { 101, 105, 102, 103, 106 }.Select(command => Int(levels[command], "level"))],
                [.. Enumerable.Range(3601, 5).Select(command => facilityCompiler.Base(command, facilities))], facilities);
        }
        var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
            .ToDictionary(row => Int(row, "check_point_type"), row => Int(row, "result_state"));
        var checkpoint = checkpoints.Count == 0 ? 0 : checkpoints.Keys.Max();
        int? bonus = checkpoint == 5 ? null : checkpoint == 0 ? 0 : pointBonuses[(checkpoint, checkpoints[checkpoint])];
        var pioneerInfo = pioneer["pioneer_info"] as JsonObject ?? throw new InvalidDataException("缺少pioneer_info。");
        var pending = Objects(Array(frame, "events"), "events").ToArray();
        PioneerEvent? levelEvent = null;
        if (pending.Length == 1 && Int(pending[0], "event_id") is >= 211024 and <= 211028)
            levelEvent = new(Int(pending[0], "event_id"), Int(pending[0], "chara_id"), Int(pending[0], "story_id"), Int(pending[0], "play_timing"));
        PioneerEvent? restEvent = null;
        if (pending.Length == 1 && RestRules.IsSupportedEvent(Int(pending[0], "event_id")))
            restEvent = new(Int(pending[0], "event_id"), Int(pending[0], "chara_id"), Int(pending[0], "story_id"), Int(pending[0], "play_timing"));
        var restCommands = 0;
        foreach (var id in camp is null ? new[] { RestRules.OrdinaryCommand } : [RestRules.OrdinaryCommand, RestRules.CampCommand])
        {
            var row = Objects(Array(home, "command_info_array"), "command_info_array").SingleOrDefault(row => Int(row, "command_id") == id)
                ?? throw new InvalidDataException($"当前面板缺少休息指令{id}的可用状态。");
            var ordinary = id == RestRules.OrdinaryCommand;
            if (Int(row, "command_type") != (ordinary ? 7 : 3) || Int(row, "is_enable") is not (0 or 1))
                throw new InvalidDataException($"休息指令{id}的类型或启用标记无效。");
            if (Int(row, "is_enable") == 1 && !disabled.Contains(id))
                restCommands |= ordinary ? RestRules.OrdinaryAvailable : RestRules.CampAvailable;
        }
        return new(Int(chara, "turn"), Int(chara, "playing_state"), cardId, Environment(chara, boost), deck, commands.ToImmutable(),
            [.. Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array")],
            [.. Objects(Array(frame, "events"), "events").Select(row => Int(row, "story_id"))],
            Int(pioneerInfo, "pioneer_pt"), checkpoint, bonus,
            [.. planningPeriods.Where(period => period.DeckId == Int(pioneerInfo, "deck_id")).OrderBy(period => period.CheckPointType)], camp,
            PendingTrainingLevelEvent: levelEvent, AvailableRestCommands: restCommands, PendingRestEvent: restEvent,
            CharacterId: CharacterId(cardId));
    }

    public TrainingFormulaInput Input(JsonObject chara, JsonObject command, JsonObject pioneer)
    {
        var (supports, guests) = Participants(chara, pioneer, Integers(Array(command, "training_partner_array"), "training_partner_array"));
        var environment = Environment(chara, null);
        var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array").ToArray();
        if (checkpoints.Select(value => Int(value, "check_point_type")).Distinct().Count() != checkpoints.Length)
            throw new InvalidDataException("check_point_info_array包含重复评估会记录。");
        var checkpoint = checkpoints.MaxBy(value => Int(value, "check_point_type"));
        var bonus = checkpoint is null ? 0 : evaluation[(Int(checkpoint, "check_point_type"), Int(checkpoint, "result_state"))];
        return new(Int(command, "command_id"), Int(command, "level"), environment.Motivation, environment.Fans, guests,
            environment.Current, environment.Limits, environment.GrowthPercent, supports, bonus, null);
    }

    public (ImmutableArray<TrainingSupport> Supports, int Guests) Participants(JsonObject chara, JsonObject pioneer, int[] positions)
    {
        var deck = Objects(Array(chara, "support_card_array"), "support_card_array").ToDictionary(card => Int(card, "position"));
        var bonds = Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array").ToDictionary(value => Int(value, "target_id"), value => Int(value, "evaluation"));
        var team = Objects(Array(pioneer, "evaluation_info_array"), "pioneer.evaluation_info_array").ToDictionary(value => Int(value, "target_id"), value => Int(value, "is_pioneer_team"));
        var supports = ImmutableArray.CreateBuilder<TrainingSupport>();
        var guests = 0;
        if (positions.Distinct().Count() != positions.Length)
            throw new InvalidDataException("training_partner_array包含重复位置，拒绝重复计入人数或支援效果。");
        foreach (var position in positions)
        {
            if (!deck.TryGetValue(position, out var support))
            {
                if (team[position] == 1) guests++;
                continue;
            }
            supports.Add(Support(Int(support, "support_card_id"), Int(support, "exp"), position, bonds[position]));
        }
        return (supports.ToImmutable(), guests);
    }

    public TrainingSupport Support(int cardId, int experience, int position, int bond)
    {
        if (!cards.TryGetValue(cardId, out var card))
            throw new InvalidDataException($"master 未收录支援卡{cardId}。");
        if (experience < 0 || position is < 1 or > 6 || bond is < 0 or > 100)
            throw new InvalidDataException("支援配置要求非负经验、1..6卡位及0..100羁绊。");
        if (card["support_card_type"] is not (1 or 2))
            throw new UnknownMechanismException($"支援卡{card["id"]}的support_card_type={card["support_card_type"]}未覆盖，不能按普通卡或友人卡处理。");
        var level = levels.Where(row => row["rarity"] == card["rarity"] && row["total_exp"] <= experience).Max(row => row["level"]);
        if (!supportPanels.TryGetValue((card["id"], level), out var compiled))
        {
            var panel = effects[card["effect_table_id"]].Select(row => new SupportEffect(row["type"], EffectAtLevel(row, level))).ToImmutableArray();
            var uniqueEffects = ImmutableArray.CreateBuilder<SupportUniqueEffect>();
            if (card["unique_effect_id"] != 0)
            {
                var unique = uniques[card["unique_effect_id"]];
                if (level >= unique["lv"])
                    for (var index = 0; index < 2; index++)
                        uniqueEffects.Add(new(unique[$"type_{index}"], unique[$"value_{index}"], unique[$"value_{index}_1"],
                            unique[$"value_{index}_2"], unique[$"value_{index}_3"], unique[$"value_{index}_4"]));
            }
            compiled = (panel, uniqueEffects.ToImmutable());
            supportPanels.Add((card["id"], level), compiled);
        }
        return new(card["id"], level, position, card["command_id"], bond, compiled.Panel, compiled.Unique);
    }

    public TrainingEnvironment Environment(JsonObject chara, GoldShipTrainingBoost? boost)
    {
        var character = characters[Int(chara, "card_id")];
        return new(Int(chara, "motivation"), Int(chara, "fans"),
            new(Int(chara, "speed"), Int(chara, "stamina"), Int(chara, "power"), Int(chara, "guts"), Int(chara, "wiz"), Int(chara, "vital"), Int(chara, "skill_point")),
            new(Int(chara, "max_speed"), Int(chara, "max_stamina"), Int(chara, "max_power"), Int(chara, "max_guts"), Int(chara, "max_wiz"), Int(chara, "max_vital")),
            new(character["talent_speed"], character["talent_stamina"], character["talent_pow"], character["talent_guts"], character["talent_wiz"], 0, 0),
            boost);
    }

    public int CharacterId(int cardId) => characters[cardId]["chara_id"];
    public int SupportType(int cardId) => cards[cardId]["support_card_type"];

    public static ImmutableArray<EventPartner> EventPartners(SqliteConnection connection, JsonObject chara, JsonObject pioneer)
    {
        var identities = new Dictionary<int, (int Character, int Type)>();
        var eventNpcs = new Dictionary<int, int>();
        var characterIds = new HashSet<int>();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT id,chara_id,support_card_type FROM support_card_data";
        using (var rows = query.ExecuteReader())
            while (rows.Read()) identities.Add(rows.GetInt32(0), (rows.GetInt32(1), rows.GetInt32(2)));
        query.CommandText = "SELECT partner_id,chara_id FROM single_mode_unique_chara WHERE scenario_id=11";
        using (var rows = query.ExecuteReader())
            while (rows.Read()) eventNpcs.Add(rows.GetInt32(0), rows.GetInt32(1));
        query.CommandText = "SELECT id FROM chara_data";
        using (var rows = query.ExecuteReader())
            while (rows.Read()) characterIds.Add(rows.GetInt32(0));
        var support = Objects(Array(chara, "support_card_array"), "support_card_array")
            .ToDictionary(row => Int(row, "position"), row => identities[Int(row, "support_card_id")]);
        if (!support.Keys.Order().SequenceEqual([1, 2, 3, 4, 5, 6]) || support.Values.Any(card => card.Type is not (1 or 2)))
            throw new UnknownMechanismException("事件人员映射要求六张普通／友人支援；团卡多角色身份尚未接入。");
        var team = Objects(Array(pioneer, "evaluation_info_array"), "pioneer.evaluation_info_array")
            .Where(row => Int(row, "is_pioneer_team") == 1).Select(row => Int(row, "target_id")).ToHashSet();
        var result = ImmutableArray.CreateBuilder<EventPartner>();
        foreach (var row in Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array"))
        {
            var target = Int(row, "target_id");
            var isSupport = support.TryGetValue(target, out var card);
            var character = isSupport ? card.Character : eventNpcs.GetValueOrDefault(target);
            if (character == 0)
            {
                if (!team.Contains(target) || !characterIds.Contains(target) || Int(row, "training_partner_id") != target)
                    throw new UnknownMechanismException($"事件人员target_id={target}缺少支援、剧本NPC或先锋队身份映射。");
                character = target;
            }
            var outing = Int(row, "is_outing");
            if (outing is not (0 or 1)) throw new InvalidDataException("事件人员is_outing必须为0或1。");
            result.Add(new(target, character, Int(row, "evaluation"), isSupport, outing == 1));
        }
        return result.ToImmutable();
    }

    static int EffectAtLevel(Dictionary<string, int> row, int level)
    {
        var points = new[] { (Level: 1, Value: row["init"]) }
            .Concat(Enumerable.Range(1, 10).Select(index => (Level: index * 5, Value: row[$"limit_lv{index * 5}"])))
            .Where(point => point.Value >= 0).ToArray();
        var below = points.Where(point => point.Level <= level).ToArray();
        if (below.Length == 0) return 0;
        var low = below[^1];
        var above = points.Where(point => point.Level >= level).ToArray();
        if (above.Length == 0 || above[0].Level == low.Level) return low.Value;
        var high = above[0];
        return low.Value + (level - low.Level) * (high.Value - low.Value) / (high.Level - low.Level);
    }

    internal static Dictionary<string, int>[] Rows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + table;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, int>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.GetInt32(index));
            rows.Add(row);
        }
        return rows.ToArray();
    }
    internal static int Int(JsonObject value, string name) => value[name] is JsonValue field && field.TryGetValue<int>(out var number)
        ? number : throw new InvalidDataException($"训练公式输入缺少整数{name}。");
    internal static JsonArray Array(JsonObject value, string name) => value[name] as JsonArray
        ?? throw new InvalidDataException($"训练公式输入缺少数组{name}。");
    internal static IEnumerable<JsonObject> Objects(JsonArray values, string name) => values.Select(value => value as JsonObject
        ?? throw new InvalidDataException($"{name}的每个元素必须为对象，不能丢弃null或标量项。"));
    internal static int[] Integers(JsonArray values, string name) => values.Select(value => value is JsonValue field && field.TryGetValue<int>(out var number)
        ? number : throw new InvalidDataException($"{name}的每个元素必须为整数。")).ToArray();
    internal static TrainingValues Values(JsonObject command)
    {
        var values = Objects(Array(command, "params_inc_dec_info_array"), "params_inc_dec_info_array")
            .ToDictionary(value => Int(value, "target_type"), value => Int(value, "value"));
        if (values.Keys.Any(key => key is not (1 or 2 or 3 or 4 or 5 or 10 or 30)))
            throw new UnknownMechanismException("训练预览包含未支持的属性类型。");
        return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3),
            values.GetValueOrDefault(4), values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
    }
    public ImmutableArray<IslandTrainingAssignment> BuildIslandAssignments(JsonObject chara, JsonObject pioneer)
    {
        var shima = pioneer["shima_training_info"] as JsonObject ?? throw new InvalidDataException("缺少shima_training_info。");
        var assignedPositions = new HashSet<int>();
        return Objects(Array(shima, "shima_training_partner_info_array"), "shima_training_partner_info_array").Select(group =>
        {
            var positions = Integers(Array(group, "training_partner_id_array"), "training_partner_id_array");
            if (positions.Any(position => !assignedPositions.Add(position)))
                throw new InvalidDataException("岛训练人员跨设施或同设施重复，不能重复计入人数和效果。");
            var participants = Participants(chara, pioneer, positions);
            if (participants.Supports.Length + participants.Guests != positions.Length)
                throw new UnknownMechanismException("岛训练分配包含尚未核验的非先锋队NPC。");
            return new IslandTrainingAssignment(Int(group, "facility_id"), participants.Supports, participants.Guests);
        }).ToImmutableArray();
    }

}
