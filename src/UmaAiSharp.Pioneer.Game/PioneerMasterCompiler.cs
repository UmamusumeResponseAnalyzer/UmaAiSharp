using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Base.Training;
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
        var ignored = new HashSet<string>();
        if (CharacterId(cardId) == 1007 && (cardId != 100701 || !goldShipHistoryKnown))
        {
            ignored.Add($"黄金船（育成卡 {cardId}）故事 501007525：缺少已核验的衣装或回合历史，省略指定岛训练加成。");
            boost = null;
        }
        if (cardId != 100701 && boost is not null) throw new InvalidDataException("非黄金船不能携带指定训练加成。");
        if (Int(chara, "state") != 0) throw new InvalidDataException("岛训练执行起点必须是正在育成的明确状态。");
        var command = Objects(Array(home, "command_info_array"), "command_info_array").Single(row => Int(row, "command_id") == 3101);
        var disabled = Integers(Array(home, "disable_command_id_array"), "disable_command_id_array");
        var positions = Objects(Array(chara, "support_card_array"), "support_card_array").Select(row => Int(row, "position")).ToArray();
        var deck = Participants(chara, pioneer, positions, ignored).Supports.Select(card => new IslandActionSupport(card.Position,
            card.CardId, SupportType(card.CardId), card.Level, card.Bond)).ToImmutableArray();
        var assignments = BuildIslandAssignments(chara, pioneer, ignored);
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
            SupportedHints(command, [.. assignments.SelectMany(group => group.Supports)], ignored));
        var levels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
            .Where(row => Int(row, "command_id") is 101 or 102 or 103 or 105 or 106)
            .ToDictionary(row => Int(row, "command_id"), row => Int(row, "level"));
        if (levels.Count != 5) throw new UnknownMechanismException("岛训练缺少五项普通训练等级。");
        return new(state, new(environment, [levels[101], levels[105], levels[102], levels[103], levels[106]],
            facilityCompiler.Base(3101, facilities), facilities, assignments)) { IgnoredMechanisms = [.. ignored] };
    }

    public PioneerStart.TrainingTurn TrainingStart(JsonObject frame, GoldShipTrainingBoost? boost = null,
        PioneerFacilityCompiler? facilityCompiler = null, int? generatedTurn = null)
    {
        var chara = frame["c"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少角色c。");
        var pioneer = frame["s"] as JsonObject ?? throw new InvalidDataException("训练前帧缺少Pioneer状态s。");
        var home = frame["home"] as JsonObject;
        var cardId = Int(chara, "card_id");
        if (CharacterId(cardId) != 1007 && boost is not null) throw new InvalidDataException("非黄金船训练起点不能携带指定训练加成。");
        if (Int(chara, "state") != 0) throw new InvalidDataException("训练执行起点必须处于育成中。");
        var positions = Objects(Array(chara, "support_card_array"), "support_card_array").Select(row => Int(row, "position")).ToArray();
        var ignored = new HashSet<string>();
        if (cardId != 100701 && boost is { } outfitBoost)
        {
            ignored.Add($"黄金船（育成卡 {cardId}）故事 501007525：指令 {outfitBoost.CommandId} 的衣装倍率未核验，省略公式加成。");
            boost = null;
        }
        var deck = Participants(chara, pioneer, positions, ignored).Supports.Sort((left, right) => left.Position.CompareTo(right.Position));
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
            var input = Input(chara, row, pioneer, ignored);
            if (commandId == ordinaryCommand && Int(levels[commandId], "level") != input.Level)
                throw new InvalidDataException("角色训练等级与行动面板不一致。");
            var partners = Integers(Array(row, "training_partner_array"), "training_partner_array");
            commands.Add(new(commandId, input.Level, counts[ordinaryCommand], input.GuestCount,
                [.. input.Supports.Select(card => card.Position)], SupportedHints(row, input.Supports, ignored),
                Int(row, "is_enable") == 1 && !disabled.Contains(commandId), Trainings[(commandId, input.Level)].FailureRate,
                input.EvaluationTrainingPercent, partners.Contains(102), partners.Contains(103)));
        }
        CampTrainingConfiguration? camp = null;
        if (commands.Any(command => command.CommandId is >= 3601 and <= 3605))
        {
            if (boost is { } campBoost)
                ignored.Add($"黄金船（育成卡 {cardId}）故事 501007525：指令 {campBoost.CommandId} 的合宿倍率未核验，省略公式加成。");
            boost = null;
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
            if (row is null) continue;
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
            CharacterId: CharacterId(cardId)) { IgnoredMechanisms = [.. ignored] };
    }

    static ImmutableArray<int> SupportedHints(JsonObject command, ImmutableArray<TrainingSupport> supports, ISet<string> ignored)
    {
        var result = ImmutableArray.CreateBuilder<int>();
        foreach (var position in Integers(Array(command, "tips_event_partner_array"), "tips_event_partner_array"))
        {
            var support = supports.FirstOrDefault(card => card.Position == position);
            if (support is { TrainingCommandId: 0 })
                ignored.Add($"指令 {Int(command, "command_id")} 卡位 {position} 支援卡 {support.CardId}：特殊提示奖励未计入。");
            else result.Add(position);
        }
        return result.ToImmutable();
    }

    public TrainingFormulaInput Input(JsonObject chara, JsonObject command, JsonObject pioneer, ISet<string>? ignored = null)
    {
        var (supports, guests) = Participants(chara, pioneer, Integers(Array(command, "training_partner_array"), "training_partner_array"), ignored);
        var environment = Environment(chara, null);
        var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array").ToArray();
        if (checkpoints.Select(value => Int(value, "check_point_type")).Distinct().Count() != checkpoints.Length)
            throw new InvalidDataException("check_point_info_array包含重复评估会记录。");
        var checkpoint = checkpoints.MaxBy(value => Int(value, "check_point_type"));
        var bonus = checkpoint is null ? 0 : evaluation[(Int(checkpoint, "check_point_type"), Int(checkpoint, "result_state"))];
        return new(Int(command, "command_id"), Int(command, "level"), environment.Motivation, environment.Fans, guests,
            environment.Current, environment.Limits, environment.GrowthPercent, supports, bonus, null, environment.TotalSupportBond);
    }

    public (ImmutableArray<TrainingSupport> Supports, int Guests) Participants(JsonObject chara, JsonObject pioneer, int[] positions,
        ISet<string>? ignored = null, int? islandFacility = null)
    {
        var deck = Objects(Array(chara, "support_card_array"), "support_card_array").ToDictionary(card => Int(card, "position"));
        var bonds = Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array").ToDictionary(value => Int(value, "target_id"), value => Int(value, "evaluation"));
        var team = Objects(Array(pioneer, "evaluation_info_array"), "pioneer.evaluation_info_array").ToDictionary(value => Int(value, "target_id"), value => Int(value, "is_pioneer_team"));
        var supports = ImmutableArray.CreateBuilder<TrainingSupport>();
        var guests = 0;
        if (positions.Distinct().Count() != positions.Length)
            throw new InvalidDataException("training_partner_array包含重复位置，拒绝重复计入人数或支援效果。");
        var deckTypes = deck.Values.Select(row =>
        {
            var member = cards[Int(row, "support_card_id")];
            var command = member.CommandId is >= 3601 and <= 3605
                ? TrainingBaseTable.OrdinaryCommand(member.CommandId) : member.CommandId;
            return (member.Id, member.SupportType, command);
        });
        foreach (var position in positions)
        {
            if (!deck.TryGetValue(position, out var support))
            {
                if (team.TryGetValue(position, out var isTeam) && isTeam == 1) guests++;
                else if (islandFacility is { } facility && ignored is not null)
                    ignored.Add($"育成卡 {Int(chara, "card_id")} 岛训练设施 {facility} 的人员 {position} 身份未确认，省略该人员分量。");
                else if (!team.ContainsKey(position))
                    throw new InvalidDataException($"训练人员 {position} 缺少先锋队身份记录。");
                continue;
            }
            var card = Support(Int(support, "support_card_id"), Int(support, "exp"), position, bonds[position]);
            if (ignored is not null)
                card = SupportedEffects(card, ignored);
            supports.Add(TrainingSupportEffects.BindDeckBonuses(card, deckTypes));
        }
        return (supports.ToImmutable(), guests);
    }

    TrainingSupport SupportedEffects(TrainingSupport card, ISet<string> ignored)
    {
        var label = $"卡位 {card.Position} 支援卡 {card.CardId}（Lv{card.Level}）";
        if (cards[card.CardId].SupportType == 3)
            ignored.Add($"{label}：当前面板按已揭示的情热区判定友情；后续情热区触发及持续、成员外出和专属事件未计入，出场率沿用友人样本。");
        if (card.CardId is 10021 or 30021)
            ignored.Add($"{label}：駿川たづな的后续同训、外出解锁、固定事件与末段 Hint 未抽取；多个异常同时存在时省略外出治疗。后续出场率沿用友人样本。");
        else if (cards[card.CardId].SupportType == 2 && card.CardId is not (30257 or 10128))
            ignored.Add($"{label}：友人专属同训、外出解锁与各段奖励未计入；后续出场率沿用塔克样本。");
        bool KnownAtom(int type, string source)
        {
            if (type is >= 0 and <= 19 or 25 or 26 or 27 or 28 or 30 or 31 or 32 or 33 or 41) return true;
            ignored.Add($"{label}：{source}效果 {type} 未计入。");
            return false;
        }
        var panel = card.Effects.Where(effect => KnownAtom(effect.Type, "面板")).ToImmutableArray();
        var unique = ImmutableArray.CreateBuilder<SupportUniqueEffect>();
        foreach (var raw in card.UniqueEffects)
        {
            var effect = raw;
            if (effect.Type < 100 && !KnownAtom(effect.Type, "固有")) continue;
            if (effect.Type == 101)
            {
                if (!KnownAtom(effect.Parameter1, "固有 101 的条件")) effect = effect with { Parameter1 = 0, Parameter2 = 0 };
                if (!KnownAtom(effect.Parameter3, "固有 101 的条件")) effect = effect with { Parameter3 = 0, Parameter4 = 0 };
            }
            var missing = effect.Type switch
            {
                102 when effect.Value != 80 => "未核验羁绊条件的非得意训练加成",
                106 => "累计友情训练次数加成",
                107 => "按当前体力变化的友情加成",
                109 when effect.Value != 8 || effect.Parameter1 <= 0 => "未知参数的全队羁绊训练效果",
                112 when effect.Value is < 0 or > 100 || effect.Parameter1 != 0 || effect.Parameter2 != 0
                    || effect.Parameter3 != 0 || effect.Parameter4 != 0 => "未知参数的失败率归零效果",
                116 => "按已持技能类别计数的加成",
                117 => "按训练等级总和的加成",
                118 => "后续回合双位置分布",
                119 => "全队出场率加成",
                113 when effect.Value is not (2 or 8 or 28) || effect.Parameter1 < 0 || effect.Value == 28 && effect.Parameter1 > 100
                    || effect.Parameter2 != 0 || effect.Parameter3 != 0 || effect.Parameter4 != 0 => "未核验的友情训练加成",
                120 when effect.Value != 1 || effect.Parameter1 != 80 || effect.Parameter2 != 1 || effect.Parameter3 != 2 || effect.Parameter4 != 0
                    => "未核验的编成加成",
                121 when effect.Value < 0 || effect.Parameter1 < 0 || effect.Parameter2 != 0 || effect.Parameter3 != 0 || effect.Parameter4 != 0
                    => "未核验的全队羁绊加成",
                100 or > 122 => "未知效果",
                _ => null,
            };
            if (missing is not null)
                ignored.Add($"{label}：固有 {effect.Type} 的{missing}未计入。");
            // The observed board can still use both slots of a type-118 card.
            if (missing is null || effect.Type == 118) unique.Add(effect);
        }
        return card with { Effects = panel, UniqueEffects = unique.ToImmutable() };
    }

    public TrainingSupport Support(int cardId, int experience, int position, int bond)
    {
        if (!cards.TryGetValue(cardId, out var card))
            throw new InvalidDataException($"master 未收录支援卡{cardId}。");
        if (experience < 0 || position is < 1 or > 6 || bond is < 0 or > 100)
            throw new InvalidDataException("支援配置要求非负经验、1..6卡位及0..100羁绊。");
        if (card.SupportType is not (1 or 2 or 3))
            throw new UnknownMechanismException($"支援卡{card.Id}的support_card_type={card.SupportType}未覆盖。");
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
            boost, Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array")
                .Where(row => Int(row, "target_id") is >= 1 and <= 6).Sum(row => Int(row, "evaluation")));
    }

    public int CharacterId(int cardId) => characters[cardId].CharacterId;
    public int SupportType(int cardId) => cards[cardId].SupportType;

    public static ImmutableArray<EventPartner> EventPartners(PioneerEventPartnersData data, JsonObject chara, JsonObject pioneer,
        List<int>? unknownNpcTargets = null)
    {
        var identities = data.Supports.ToDictionary(row => row.CardId);
        var eventNpcs = data.Npcs.ToDictionary(row => row.PartnerId, row => row.CharacterId);
        var characterIds = data.CharacterIds.ToHashSet();
        var support = Objects(Array(chara, "support_card_array"), "support_card_array")
            .ToDictionary(row => Int(row, "position"), row => identities[Int(row, "support_card_id")]);
        if (!support.Keys.Order().SequenceEqual([1, 2, 3, 4, 5, 6]))
            throw new InvalidDataException("事件人员映射要求完整的六个支援卡位。");
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
                {
                    if (!isSupport && unknownNpcTargets is not null)
                    {
                        unknownNpcTargets.Add(target);
                        continue;
                    }
                    throw new UnknownMechanismException($"事件人员target_id={target}缺少支援、剧本NPC或先锋队身份映射。");
                }
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
    internal static TrainingValues Values(JsonObject command, Action<int>? ignoredTarget = null)
    {
        var values = Objects(Array(command, "params_inc_dec_info_array"), "params_inc_dec_info_array")
            .ToDictionary(value => Int(value, "target_type"), value => Int(value, "value"));
        foreach (var target in values.Keys.Where(key => key is not (1 or 2 or 3 or 4 or 5 or 10 or 30)))
        {
            if (ignoredTarget is null) throw new UnknownMechanismException($"训练预览包含未支持的属性类型 {target}。");
            ignoredTarget(target);
        }
        return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3),
            values.GetValueOrDefault(4), values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
    }
    public ImmutableArray<IslandTrainingAssignment> BuildIslandAssignments(JsonObject chara, JsonObject pioneer, ISet<string>? ignored = null)
    {
        var shima = pioneer["shima_training_info"] as JsonObject ?? throw new InvalidDataException("缺少shima_training_info。");
        var assignedPositions = new HashSet<int>();
        return Objects(Array(shima, "shima_training_partner_info_array"), "shima_training_partner_info_array").Select(group =>
        {
            var positions = Integers(Array(group, "training_partner_id_array"), "training_partner_id_array");
            var participants = Participants(chara, pioneer, positions, ignored, Int(group, "facility_id"));
            foreach (var position in positions)
            {
                if (assignedPositions.Add(position)) continue;
                var card = participants.Supports.FirstOrDefault(member => member.Position == position);
                if (ignored is null || card is null || !card.UniqueEffects.Any(effect => effect.Type == 118 && card.Bond >= effect.Parameter1))
                    throw new InvalidDataException($"岛训练人员 {position} 重复分配。");
                ignored.Add($"卡位 {position} 支援卡 {card.CardId}：岛训练固有 118 跨设施分身仅按首个设施结算。");
                participants.Supports = participants.Supports.Remove(card);
            }
            if (ignored is null && participants.Supports.Length + participants.Guests != positions.Length)
                throw new UnknownMechanismException("岛训练分配包含尚未核验的非先锋队NPC。");
            return new IslandTrainingAssignment(Int(group, "facility_id"), participants.Supports, participants.Guests);
        }).ToImmutableArray();
    }

    internal PioneerIslandBoard? ReadIslandBoard(JsonObject frame, ISet<string> ignored)
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
        foreach (var group in BuildIslandAssignments(chara, pioneer, ignored))
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
                if (hints.Contains(support.Position) && support.TrainingCommandId != 0) hintMask |= 1 << (support.Position - 1);
                else if (hints.Contains(support.Position))
                    ignored.Add($"指令 3101 卡位 {support.Position} 支援卡 {support.CardId}：特殊提示奖励未计入。");
            }
            placements[index] = new((byte)supports, (byte)hintMask, checked((byte)group.GuestCount));
        }
        return new(new(placements[0], placements[1], placements[2], placements[3], placements[4]), placements[5]);
    }

}
