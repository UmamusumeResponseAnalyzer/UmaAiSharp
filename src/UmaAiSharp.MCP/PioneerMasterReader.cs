using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Pioneer.Game;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Offline;

public static class PioneerMasterReader
{
    public static PioneerRulesSnapshot ReadForecast(string path, Runtime.PioneerTurnCalibration? calibration = null,
        PioneerInheritanceCalibration? inheritance = null)
    {
        using var db = OpenReadOnly(path);
        using var source = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(source));
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT p.id,p.need_fan_count,t.text,r.grade,COALESCE(f.fan_count,0),p.program_group,p.race_permission,p.month,p.half
            FROM single_mode_program p
            JOIN race_instance i ON i.id=p.race_instance_id
            JOIN race r ON r.id=i.race_id
            LEFT JOIN single_mode_fan_count f ON f.fan_set_id=p.fan_set_id AND f."order"=1
            LEFT JOIN text_data t ON t.category=28 AND t."index"=p.race_instance_id
            ORDER BY p.id
            """;
        var races = ImmutableArray.CreateBuilder<PioneerRaceRule>();
        using (var rows = query.ExecuteReader())
            while (rows.Read()) races.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.IsDBNull(2) ? null : rows.GetString(2),
                rows.GetInt32(3), rows.GetInt32(4), rows.GetInt32(5), rows.GetInt32(6), rows.GetInt32(7), rows.GetInt32(8)));
        ImmutableArray<PioneerStoryProgress>? storyProgress = null;
        ImmutableArray<int> traineeRandomStories = default;
        if (calibration is not null)
        {
            query.CommandText = """
                SELECT story_id,MIN(show_progress_1),MIN(show_progress_2),
                    CASE WHEN MIN(support_card_id)=MAX(support_card_id) THEN MIN(support_card_id) ELSE 0 END
                FROM single_mode_story_data
                GROUP BY story_id
                HAVING MIN(show_progress_1)>0 AND MIN(show_progress_2)>=MIN(show_progress_1)
                    AND MIN(show_progress_1)=MAX(show_progress_1) AND MIN(show_progress_2)=MAX(show_progress_2)
                ORDER BY story_id
                """;
            var progress = ImmutableArray.CreateBuilder<PioneerStoryProgress>();
            using (var rows = query.ExecuteReader())
                while (rows.Read()) progress.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetInt32(2), rows.GetInt32(3)));
            storyProgress = progress.ToImmutable();
            traineeRandomStories = [.. ReadIntegers(db, """
                SELECT DISTINCT story_id FROM single_mode_story_data
                WHERE (card_chara_id>0 OR card_id>0) AND race_event_flag=0 AND story_id<>501007525
                    AND (gallery_list_id BETWEEN 42001 AND 42009 OR gallery_list_id IN (52001,52002,52003,80001,82011,82012,82013,82014,82015)
                        OR (card_id>0 AND gallery_list_id BETWEEN 1 AND 3))
                ORDER BY story_id
                """).Select(row => row["story_id"])];
        }
        return new(calibration is null ? PioneerRulesSnapshot.StaticVersion : PioneerRulesSnapshot.CurrentVersion,
            sha, ReadTraining(db), ReadFacilities(db), ReadEventPartners(db), races.ToImmutable(),
            calibration is null ? null : ConstructionData(db, sha), calibration, storyProgress,
            calibration is null ? null : ReadProgression(db),
            calibration is null ? null : ReadInheritance(db, inheritance ?? throw new InvalidDataException("整局规则缺少继承校准。")),
            calibration is null ? default : ReadHintPools(db), traineeRandomStories);
    }

    static ImmutableArray<PioneerCardHints> ReadHintPools(SqliteConnection db)
    {
        var rows = ReadIntegers(db, """
            SELECT h.support_card_id card,h.hint_gain_type type,h.hint_group prize,h.hint_value_1 target,
                h.hint_value_2 amount,COALESCE(s.group_id,0) skill,COALESCE(s.rarity,0) rarity
            FROM single_mode_hint_gain h
            LEFT JOIN skill_data s ON h.hint_gain_type=0 AND s.id=h.hint_value_1
            ORDER BY h.support_card_id,h.hint_gain_type,h.hint_group,h.hint_value_1
            """);
        return [.. rows.GroupBy(row => row["card"]).Select(group =>
        {
            var skills = group.Where(row => row["type"] == 0).Select(row => new HintSkill(row["skill"], row["rarity"])).Distinct();
            var attributes = group.Where(row => row["type"] == 1).ToArray();
            if (attributes.Select(row => row["prize"]).Distinct().Count() != 1
                || group.Any(row => row["type"] is not (0 or 1))
                || attributes.Any(row => row["target"] is not (>= 1 and <= 5 or 30)))
                throw new InvalidDataException($"支援卡 {group.Key} 的 Hint 奖励必须有一组已知的属性或 Pt 效果。");
            var gains = attributes.ToLookup(row => row["target"], row => row["amount"]);
            return new PioneerCardHints(group.Key, [.. skills],
                new(gains[1].Sum(), gains[2].Sum(), gains[3].Sum(), gains[4].Sum(), gains[5].Sum(), 0, gains[30].Sum()));
        })];
    }

    static PioneerInheritanceData ReadInheritance(SqliteConnection db, PioneerInheritanceCalibration calibration) => new(
        [.. ReadIntegers(db, "SELECT factor_id,factor_type,factor_group_id,rarity,effect_group_id FROM succession_factor ORDER BY factor_id")
            .Select(row => new PioneerInheritanceFactor(row["factor_id"], row["factor_type"], row["factor_group_id"],
                row["rarity"], row["effect_group_id"]))],
        [.. ReadIntegers(db, "SELECT factor_group_id,effect_id,target_type,value_1,value_2 FROM succession_factor_effect ORDER BY id")
            .Select(row => new PioneerInheritanceEffect(row["factor_group_id"], row["effect_id"], row["target_type"],
                row["value_1"], row["value_2"]))],
        [.. ReadIntegers(db, "SELECT id,group_id,rarity FROM skill_data ORDER BY id")
            .Select(row => new InheritanceSkill(row["id"], row["group_id"], row["rarity"]))],
        [.. ReadIntegers(db, "SELECT relation_type,relation_point FROM succession_relation ORDER BY relation_type")
            .Select(row => new RelationPoint(row["relation_type"], row["relation_point"]))],
        [.. ReadIntegers(db, "SELECT chara_id,relation_type FROM succession_relation_member ORDER BY chara_id,relation_type")
            .Select(row => new RelationMembership(row["chara_id"], row["relation_type"]))],
        [.. ReadIntegers(db, """
            SELECT s.id,s.relation_group_id,CASE WHEN s.win_saddle_type=3 AND s.condition=0 AND r.grade=100 THEN 1 ELSE 0 END eligible
            FROM single_mode_wins_saddle s LEFT JOIN race_instance i ON i.id=s.race_instance_id_1 LEFT JOIN race r ON r.id=i.race_id
            ORDER BY s.id
            """).Select(row => new RelationSaddle(row["id"], row["relation_group_id"], row["eligible"] == 1))],
        [.. Rows(db, "succession_factor_addon_effect").OrderBy(row => row["id"]).Select(row =>
        {
            var values = Enumerable.Range(1, 6).Select(index => row[$"condition_value_{index}"]);
            return row["condition_type"] switch
            {
                1 => new PioneerInheritanceAddon(row["factor_id"], values.Count(value => value != 0),
                    [.. values.Where(value => value != 0)]),
                2 => new PioneerInheritanceAddon(row["factor_id"], row["condition_value_1"],
                    [.. values.Skip(1).Where(value => value != 0)]),
                _ => throw new InvalidDataException($"未知继承附加因子条件 {row["condition_type"]}。"),
            };
        })],
        calibration);

    static PioneerProgressionData ReadProgression(SqliteConnection db) => new(
        [.. Rows(db, "single_mode_11_pioneer_pt_bonus").OrderBy(row => row["id"]).Select(row => new CheckpointPointBonus(
            row["id"], row["check_point_type"], row["group_id"], row["pt_rate"], row["add_value_max"]))],
        [.. Rows(db, "single_mode_11_check_point_eff").OrderBy(row => row["id"]).Select(row => new CheckpointEffect(
            row["id"], row["check_point_type"], row["result_state"], row["effect_type"], row["effect_value_1"],
            row["effect_value_2"], row["effect_value_3"], row["effect_value_4"]))],
        [.. ReadIntegers(db, """
            SELECT g.race_group_id,MIN(f.fan_count) AS fans
            FROM single_mode_race_group g
            JOIN single_mode_program p ON p.id=g.race_program_id
            JOIN single_mode_fan_count f ON f.fan_set_id=p.fan_set_id AND f."order"=1
            WHERE g.race_group_id BETWEEN 10001 AND 10003
            GROUP BY g.race_group_id HAVING MIN(f.fan_count)=MAX(f.fan_count)
            ORDER BY g.race_group_id
            """).Select(row => row["fans"])],
        [.. ReadIntegers(db, """
            SELECT id,turn,condition_id FROM single_mode_route_race
            WHERE target_type=1 AND race_type=0 AND condition_type=1 AND turn BETWEEN 1 AND 72
            ORDER BY id
            """).Select(row => new PioneerFixedRaceGoal(row["id"], row["turn"], row["condition_id"]))],
        [.. ReadIntegers(db, """
            SELECT g.id,g.turn,g.condition_type,g.condition_id,g.condition_value_1,g.condition_value_2,
                COALESCE(MAX(p.turn),12)+1 AS first_turn
            FROM single_mode_route_race g
            LEFT JOIN single_mode_route_race p ON p.race_set_id=g.race_set_id AND p.scenario_group_id=g.scenario_group_id
                AND p.sort_id<g.sort_id AND p.turn<g.turn AND p.determine_race IN (0,1,4)
            WHERE g.scenario_group_id=100 AND g.target_type=1 AND g.race_type=0 AND g.condition_type IN (2,3)
            GROUP BY g.id ORDER BY g.turn,g.id
            """).Select(row => row["condition_type"] == 3
                ? new PioneerCareerGoal(row["id"], 13, row["turn"], row["condition_value_1"], 0, 0, 0)
                : new PioneerCareerGoal(row["id"], row["first_turn"], row["turn"], 0, row["condition_id"],
                    row["condition_value_1"], row["condition_value_2"]))],
        [.. ReadIntegers(db, """
            SELECT turn,year,month,half FROM single_mode_turn WHERE turn_set_id=11 AND race_entry_type=1 AND turn BETWEEN 12 AND 72
            ORDER BY turn
            """).Select(row => new PioneerRaceDate(row["turn"], row["year"], row["month"], row["half"]))],
        [.. ReadIntegers(db, "SELECT chara_id,program_group,program_group_2 FROM single_mode_chara_program WHERE chara_id>0 ORDER BY id")
            .Select(row => new PioneerCharacterRaceProgram(row["chara_id"], row["program_group"], row["program_group_2"]))]);

    public static PioneerTrainingRulesData ReadTraining(string path)
    {
        using var db = OpenReadOnly(path);
        return ReadTraining(db);
    }

    static PioneerTrainingRulesData ReadTraining(SqliteConnection db) => new(
        [.. Rows(db, "single_mode_training").OrderBy(row => row["command_id"]).ThenBy(row => row["command_level"])
            .Select(row => new PioneerTrainingRule(row["command_id"], row["command_level"], row["base_command_id"], row["failure_rate"]))],
        [.. Rows(db, "support_card_data").OrderBy(row => row["id"]).Select(row => new PioneerSupportCardRule(
            row["id"], row["rarity"], row["effect_table_id"], row["unique_effect_id"], row["command_id"], row["support_card_type"]))],
        [.. Rows(db, "support_card_level").OrderBy(row => row["rarity"]).ThenBy(row => row["level"])
            .Select(row => new PioneerSupportLevelRule(row["rarity"], row["level"], row["total_exp"]))],
        [.. Rows(db, "support_card_effect_table").OrderBy(row => row["id"]).Select(row => new PioneerSupportEffectRule(row["id"], row["type"],
            [row["init"], .. Enumerable.Range(1, 10).Select(index => row[$"limit_lv{index * 5}"])]))],
        [.. Rows(db, "support_card_unique_effect").OrderBy(row => row["id"]).Select(row => new PioneerUniqueEffectRule(row["id"], row["lv"],
            [.. Enumerable.Range(0, 2).Select(index => new SupportUniqueEffect(row[$"type_{index}"], row[$"value_{index}"],
                row[$"value_{index}_1"], row[$"value_{index}_2"], row[$"value_{index}_3"], row[$"value_{index}_4"]))]))],
        [.. Rows(db, "card_data").OrderBy(row => row["id"]).Select(row => new PioneerCharacterRule(row["id"], row["chara_id"],
            new(row["talent_speed"], row["talent_stamina"], row["talent_pow"], row["talent_guts"], row["talent_wiz"], 0, 0)))],
        [.. Rows(db, "single_mode_11_check_point_eff")
            .Where(row => (row["effect_type"], row["effect_value_1"]) is (200, 0) or (202, 9))
            .OrderBy(row => row["check_point_type"]).ThenBy(row => row["result_state"]).ThenBy(row => row["effect_type"])
            .Select(row => new PioneerCheckpointTrainingEffect(row["check_point_type"], row["result_state"],
                row["effect_type"], row["effect_value_1"], row["effect_value_2"]))],
        [.. Rows(db, "single_mode_11_planning").OrderBy(row => row["id"]).Select(Planning)]);

    public static PioneerFacilityRulesData ReadFacilities(string path)
    {
        using var db = OpenReadOnly(path);
        return ReadFacilities(db);
    }

    static PioneerFacilityRulesData ReadFacilities(SqliteConnection db)
    {
        var effects = Rows(db, "single_mode_11_facility_effect").GroupBy(row => row["state_id"])
            .ToDictionary(group => group.Key, group => group.Select(row => new TrainingFacilityEffect(row["effect_type"],
                row["effect_value_1"], row["effect_value_2"], row["effect_value_3"])).ToImmutableArray());
        return new(
            [.. Rows(db, "single_mode_11_facility_state").OrderBy(row => row["state_id"]).Select(row => new TrainingFacility(
                row["facility_id"], row["state_id"], row["facility_lv"], effects.GetValueOrDefault(row["state_id"], [])))],
            [.. Rows(db, "single_mode_11_shima_effect").Where(row => row["result_state"] == 2)
                .Select(row => new Runtime.ForecastFacilityBase(row["command_id"], row["required_facility_id"], row["target_type"], row["effect_value"]))]);
    }

    public static PioneerEventPartnersData ReadEventPartners(SqliteConnection db) => new(
        [.. ReadIntegers(db, "SELECT id,chara_id,support_card_type FROM support_card_data ORDER BY id")
            .Select(row => new PioneerSupportIdentity(row["id"], row["chara_id"], row["support_card_type"]))],
        [.. ReadIntegers(db, "SELECT partner_id,chara_id FROM single_mode_unique_chara WHERE scenario_id=11 ORDER BY partner_id")
            .Select(row => new PioneerNpcIdentity(row["partner_id"], row["chara_id"]))],
        [.. ReadIntegers(db, "SELECT id FROM chara_data ORDER BY id").Select(row => row["id"])]);

    public static PioneerMasterData ConstructionData(SqliteConnection db, string masterSha) => new(masterSha,
        [.. Rows(db, "single_mode_11_facility_kind").Select(row => new FacilityKind(row["facility_id"], row["command_id"]))],
        [.. Rows(db, "single_mode_11_facility_state").Select(row => new FacilityState(row["id"], row["facility_id"], row["facility_lv"], row["icon_id"], row["state_id"]))],
        [.. Rows(db, "single_mode_11_facility_lv_up").Select(row => new FacilityUpgrade(row["id"], row["facility_id"], row["current_state_id"], row["target_state_id"], row["cost"]))],
        [.. Rows(db, "single_mode_11_planning").Select(Planning)],
        [.. Rows(db, "single_mode_11_gain_training_pt").Select(row => new IslandTicketThreshold(row["id"], row["deck_id"], row["turn_min"], row["turn_max"], row["gain_shima_training_pt"]))]);

    static PlanningPeriod Planning(Dictionary<string, int> row) => new(row["id"], row["deck_id"], row["check_point_type"],
        row["planning_turn"], row["check_point_turn"], row["target_pt"]);

    public static Dictionary<string, int>[] Rows(SqliteConnection connection, string table)
        => ReadIntegers(connection, "SELECT * FROM " + table);

    static Dictionary<string, int>[] ReadIntegers(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, int>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.GetInt32(index));
            rows.Add(row);
        }
        return [.. rows];
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("输入数据库不存在。");
        // A detached main file is not a complete WAL snapshot.
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
            throw new InvalidDataException("输入存在非空WAL；请提供包含已提交状态的稳定数据库快照，不能仅审计主文件。");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
