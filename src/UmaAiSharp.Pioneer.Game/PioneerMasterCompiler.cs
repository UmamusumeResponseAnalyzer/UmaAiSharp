using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Events;

namespace UmaAiSharp.Pioneer.Game;

public sealed class PioneerMasterCompiler
{
    public IReadOnlyDictionary<(int Command, int Level), PioneerTrainingRule> Trainings { get; }
    readonly Dictionary<int, PioneerSupportCardRule> cards;
    readonly Dictionary<int, PioneerUniqueEffectRule> uniques;
    readonly Dictionary<int, PioneerSupportEffectRule[]> effects;
    readonly Dictionary<int, PioneerCharacterRule> characters;
    readonly ImmutableArray<PioneerSupportLevelRule> levels;
    readonly Dictionary<(int Checkpoint, int Result), int> evaluation;
    readonly Dictionary<(int Checkpoint, int Result), int> pointBonuses;
    readonly ImmutableArray<PlanningPeriod> planningPeriods;
    readonly Dictionary<(int Card, int Level), (ImmutableArray<SupportEffect> Panel, ImmutableArray<SupportUniqueEffect> Unique)> supportPanels = [];

    public PioneerMasterCompiler(PioneerTrainingRulesData rules)
    {
        Trainings = rules.Commands.ToDictionary(row => (row.CommandId, row.Level));
        cards = rules.SupportCards.ToDictionary(row => row.Id);
        uniques = rules.UniqueEffects.ToDictionary(row => row.Id);
        effects = rules.SupportEffects.GroupBy(row => row.TableId).ToDictionary(group => group.Key, group => group.ToArray());
        characters = rules.Characters.ToDictionary(row => row.Id);
        levels = rules.SupportLevels;
        evaluation = rules.CheckpointEffects.Where(row => row.Type == 200 && row.Target == 0)
            .ToDictionary(row => (row.Checkpoint, row.Result), row => row.Value);
        pointBonuses = rules.CheckpointEffects.Where(row => row.Type == 202 && row.Target == 9)
            .ToDictionary(row => (row.Checkpoint, row.Result), row => row.Value);
        planningPeriods = rules.PlanningPeriods;
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
        PioneerFacilityCompiler? facilityCompiler = null, int? generatedTurn = null, bool wholeRun = false)
    {
        var chara = frame["c"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少角色c。");
        var pioneer = frame["s"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少Pioneer状态s。");
        var home = frame["home"] as JsonObject;
        var cardId = Int(chara, "card_id");
        if (!wholeRun && CharacterId(cardId) == 1007 && (cardId != 100701 || !goldShipHistoryKnown))
            throw new UnknownMechanismException("黄金船训练起点需要当前衣装及指定训练效果历史。");
        if (cardId != 100701 && boost is not null) throw new InvalidDataException("非黄金船训练起点不能携带指定训练加成。");
        if (Int(chara, "state") != 0) throw new InvalidDataException("训练执行起点必须处于育成中。");
        var positions = Objects(Array(chara, "support_card_array"), "support_card_array").Select(row => Int(row, "position")).ToArray();
        var deck = Participants(chara, pioneer, positions).Supports.Sort((left, right) => left.Position.CompareTo(right.Position));
        var disabled = generatedTurn is null
            ? Integers(Array(home ?? throw new InvalidDataException("训练前帧缺少home。"), "disable_command_id_array"), "disable_command_id_array") : [];
        var levels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
            .Where(row => Int(row, "command_id") is 101 or 105 or 102 or 103 or 106).ToDictionary(row => Int(row, "command_id"));
        var counts = Objects(Array(pioneer, "training_exec_info_array"), "training_exec_info_array")
            .ToDictionary(row => Int(row, "base_command_id"), row => Int(row, "exec_count"));
        var commands = ImmutableArray.CreateBuilder<TrainingCommandOption>();
        IEnumerable<JsonObject> visibleCommands = generatedTurn is null
            ? Objects(Array(home!, "command_info_array"), "command_info_array") : [];
        foreach (var row in visibleCommands)
        {
            var commandId = Int(row, "command_id");
            if (commandId is not (101 or 105 or 102 or 103 or 106 or >= 3601 and <= 3605)) continue;
            var ordinaryCommand = TrainingBaseTable.OrdinaryCommand(commandId);
            var input = Input(chara, row, pioneer);
            if (commandId == ordinaryCommand && Int(levels[commandId], "level") != input.Level)
                throw new InvalidDataException("角色训练等级与行动面板不一致。");
            commands.Add(new(commandId, input.Level, counts[ordinaryCommand], input.GuestCount,
                [.. input.Supports.Select(card => card.Position)], [.. Integers(Array(row, "tips_event_partner_array"), "tips_event_partner_array")],
                Int(row, "is_enable") == 1 && !disabled.Contains(commandId), Trainings[(commandId, input.Level)].FailureRate,
                input.EvaluationTrainingPercent, Integers(Array(row, "training_partner_array"), "training_partner_array").Contains(102)));
        }
        CampTrainingConfiguration? camp = null;
        if (commands.Any(command => command.CommandId is >= 3601 and <= 3605))
        {
            if (wholeRun) boost = null;
            if (facilityCompiler is null) throw new UnknownMechanismException("合宿训练编译需要设施master与当前设施状态。");
            var facilities = facilityCompiler.Facilities(pioneer);
            camp = new([.. new[] { 101, 105, 102, 103, 106 }.Select(command => Int(levels[command], "level"))],
                [.. Enumerable.Range(3601, 5).Select(command => facilityCompiler.Base(command, facilities))], facilities);
        }
        var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
            .ToDictionary(row => Int(row, "check_point_type"), row => Int(row, "result_state"));
        var checkpoint = checkpoints.Count == 0 ? 0 : checkpoints.Keys.Max();
        int? bonus = checkpoint == 5 ? null : checkpoint == 0 ? 0 : pointBonuses[(checkpoint, checkpoints[checkpoint])];
        if (generatedTurn.HasValue)
        {
            var evaluationPercent = checkpoint == 0 ? 0 : evaluation[(checkpoint, checkpoints[checkpoint])];
            foreach (var (command, row) in levels.OrderBy(pair => pair.Key))
            {
                var level = Int(row, "level");
                commands.Add(new(command, level, counts[command], 0, [], [], false,
                    Trainings[(command, level)].FailureRate, evaluationPercent));
            }
        }
        var pioneerInfo = pioneer["pioneer_info"] as JsonObject ?? throw new InvalidDataException("缺少pioneer_info。");
        var pending = Objects(Array(frame, "events"), "events").ToArray();
        PioneerEvent? levelEvent = null;
        if (pending.Length == 1 && Int(pending[0], "story_id") == TrainingLevelRules.StoryId)
            levelEvent = new(Int(pending[0], "event_id"), Int(pending[0], "chara_id"), Int(pending[0], "story_id"), Int(pending[0], "play_timing"));
        PioneerEvent? restEvent = null;
        if (pending.Length == 1 && RestRules.IsStory(Int(pending[0], "story_id"), CharacterId(cardId)))
            restEvent = new(Int(pending[0], "event_id"), Int(pending[0], "chara_id"), Int(pending[0], "story_id"), Int(pending[0], "play_timing"));
        var restCommands = 0;
        int[] restIds = generatedTurn.HasValue ? [] : camp is null
            ? [RestRules.OrdinaryCommand] : [RestRules.OrdinaryCommand, RestRules.CampCommand];
        foreach (var id in restIds)
        {
            var row = visibleCommands.SingleOrDefault(row => Int(row, "command_id") == id);
            if (row is null)
            {
                if (wholeRun && id == RestRules.CampCommand && Int(chara, "turn") is 74 or 76 or 78
                    && commands.All(command => !command.Enabled)) continue;
                throw new InvalidDataException($"当前面板缺少休息指令{id}的可用状态。");
            }
            var ordinary = id == RestRules.OrdinaryCommand;
            if (Int(row, "command_type") != (ordinary ? 7 : 3) || Int(row, "is_enable") is not (0 or 1))
                throw new InvalidDataException($"休息指令{id}的类型或启用标记无效。");
            if (Int(row, "is_enable") == 1 && !disabled.Contains(id))
                restCommands |= ordinary ? RestRules.OrdinaryAvailable : RestRules.CampAvailable;
        }
        return new(generatedTurn ?? Int(chara, "turn"), generatedTurn.HasValue ? 1 : Int(chara, "playing_state"),
            cardId, Environment(chara, boost), deck, commands.ToImmutable(),
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
            var card = Support(Int(support, "support_card_id"), Int(support, "exp"), position, bonds[position]);
            if (card.UniqueEffects.Any(effect => effect.Type == 120))
                card = card with { UniqueEffects = BindDeckBonuses(card, deck.Values) };
            supports.Add(card);
        }
        return (supports.ToImmutable(), guests);
    }

    ImmutableArray<SupportUniqueEffect> BindDeckBonuses(TrainingSupport support, IEnumerable<JsonObject> deck)
    {
        Span<int> counts = stackalloc int[6];
        counts.Clear();
        foreach (var row in deck)
        {
            var card = cards[Int(row, "support_card_id")];
            var index = card.SupportType is 2 or 3 ? 5 : TrainingBaseTable.CommandIndex(card.CommandId);
            if (index < 0) throw new UnknownMechanismException($"支援卡{card.Id}的编成类型无法用于固有120。");
            counts[index]++;
        }
        ReadOnlySpan<int> targets = [3, 4, 5, 6, 7, 30];
        var effects = ImmutableArray.CreateBuilder<SupportUniqueEffect>();
        foreach (var effect in support.UniqueEffects)
        {
            if (effect.Type != 120)
            {
                effects.Add(effect);
                continue;
            }
            if (effect.Value != 1 || effect.Parameter1 != 80 || effect.Parameter2 != 1 || effect.Parameter3 != 2 || effect.Parameter4 != 0)
                throw new UnknownMechanismException($"支援卡{support.CardId}的编成固有120参数尚未接入。");
            // The deck is fixed, so only the bond threshold remains a runtime condition.
            for (var index = 0; index < targets.Length; index += 2)
                effects.Add(new(101, effect.Parameter1, targets[index], Math.Min(effect.Parameter3, counts[index]),
                    targets[index + 1], Math.Min(effect.Parameter3, counts[index + 1])));
        }
        return effects.ToImmutable();
    }

    public TrainingSupport Support(int cardId, int experience, int position, int bond)
    {
        if (!cards.TryGetValue(cardId, out var card))
            throw new InvalidDataException($"master 未收录支援卡{cardId}。");
        if (experience < 0 || position is < 1 or > 6 || bond is < 0 or > 100)
            throw new InvalidDataException("支援配置要求非负经验、1..6卡位及0..100羁绊。");
        if (card.SupportType is not (1 or 2))
            throw new UnknownMechanismException($"支援卡{card.Id}的support_card_type={card.SupportType}未覆盖，不能按普通卡或友人卡处理。");
        var level = levels.Where(row => row.Rarity == card.Rarity && row.TotalExperience <= experience).Max(row => row.Level);
        if (!supportPanels.TryGetValue((card.Id, level), out var compiled))
        {
            var panel = effects[card.EffectTableId].Select(row => new SupportEffect(row.Type, EffectAtLevel(row.Values, level))).ToImmutableArray();
            var uniqueEffects = ImmutableArray<SupportUniqueEffect>.Empty;
            if (card.UniqueEffectId != 0)
            {
                var unique = uniques[card.UniqueEffectId];
                if (level >= unique.Level) uniqueEffects = unique.Effects;
            }
            compiled = (panel, uniqueEffects);
            supportPanels.Add((card.Id, level), compiled);
        }
        return new(card.Id, level, position, card.CommandId, bond, compiled.Panel, compiled.Unique);
    }

    public TrainingEnvironment Environment(JsonObject chara, GoldShipTrainingBoost? boost)
    {
        var character = characters[Int(chara, "card_id")];
        return new(Int(chara, "motivation"), Int(chara, "fans"),
            new(Int(chara, "speed"), Int(chara, "stamina"), Int(chara, "power"), Int(chara, "guts"), Int(chara, "wiz"), Int(chara, "vital"), Int(chara, "skill_point")),
            new(Int(chara, "max_speed"), Int(chara, "max_stamina"), Int(chara, "max_power"), Int(chara, "max_guts"), Int(chara, "max_wiz"), Int(chara, "max_vital")),
            character.GrowthPercent,
            boost);
    }

    public int CharacterId(int cardId) => characters[cardId].CharacterId;
    public int SupportType(int cardId) => cards[cardId].SupportType;

    public static ImmutableArray<EventPartner> EventPartners(PioneerEventPartnersData data, JsonObject chara, JsonObject pioneer)
    {
        var identities = data.Supports.ToDictionary(row => row.CardId);
        var eventNpcs = data.Npcs.ToDictionary(row => row.PartnerId, row => row.CharacterId);
        var characterIds = data.CharacterIds.ToHashSet();
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
            var character = isSupport ? card.CharacterId : eventNpcs.GetValueOrDefault(target);
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

    static int EffectAtLevel(ImmutableArray<int> values, int level)
    {
        var points = values.Select((value, index) => (Level: index == 0 ? 1 : index * 5, Value: value))
            .Where(point => point.Value >= 0).ToArray();
        var below = points.Where(point => point.Level <= level).ToArray();
        if (below.Length == 0) return 0;
        var low = below[^1];
        var above = points.Where(point => point.Level >= level).ToArray();
        if (above.Length == 0 || above[0].Level == low.Level) return low.Value;
        var high = above[0];
        return low.Value + (level - low.Level) * (high.Value - low.Value) / (high.Level - low.Level);
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

    internal PioneerIslandBoard? ReadIslandBoard(JsonObject frame)
    {
        var home = frame["home"]!.AsObject();
        var command = Objects(Array(home, "command_info_array"), "command_info_array")
            .SingleOrDefault(row => Int(row, "command_id") == 3101);
        if (command is null || Int(command, "is_enable") != 1
            || Integers(Array(home, "disable_command_id_array"), "disable_command_id_array").Contains(3101))
            return null;
        var chara = frame["c"]!.AsObject();
        var pioneer = frame["s"]!.AsObject();
        var hints = Integers(Array(command, "tips_event_partner_array"), "tips_event_partner_array");
        Span<TrainingPlacement> placements = stackalloc TrainingPlacement[6];
        placements.Clear();
        var seen = 0;
        foreach (var group in BuildIslandAssignments(chara, pioneer))
        {
            var index = group.FacilityId - 101;
            if ((uint)index >= 6 || (seen & (1 << index)) != 0)
                throw new InvalidDataException("岛训练面板的设施编号须为互异的101..106。");
            seen |= 1 << index;
            var supports = 0;
            var hintMask = 0;
            foreach (var support in group.Supports)
            {
                supports |= 1 << (support.Position - 1);
                if (hints.Contains(support.Position)) hintMask |= 1 << (support.Position - 1);
            }
            placements[index] = new((byte)supports, (byte)hintMask, checked((byte)group.GuestCount));
        }
        return new(new(placements[0], placements[1], placements[2], placements[3], placements[4]), placements[5]);
    }

}
