using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public sealed partial class PioneerAutonomousSession
{
    internal static PioneerHomeActions ReadHomeActions(JsonObject common, PioneerRulesSnapshot rules, int unseenFriendMask, bool ready,
        ImmutableArray<PioneerRaceHistory>? history = null, ImmutableArray<PioneerRaceReservation>? reservations = null,
        ImmutableArray<PioneerScheduledProgram>? randomPrograms = null)
    {
        var chara = common["chara_info"]!.AsObject();
        var home = common["home_info"] as JsonObject;
        var disabled = home?["disable_command_id_array"] as JsonArray ?? [];
        var enabled = ready && home?["command_info_array"] is JsonArray commands
            ? commands.OfType<JsonObject>().Where(row => Int(row, "is_enable") == 1).Select(row => Int(row, "command_id"))
                .Except(disabled.Select(row => row!.GetValue<int>())).ToHashSet()
            : [];
        var evaluations = Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array").ToDictionary(row => Int(row, "target_id"));
        var friends = ImmutableArray.CreateBuilder<PioneerFriendProgress>();
        var unseen = 0;
        foreach (var card in Objects(Array(chara, "support_card_array"), "support_card_array"))
        {
            var identity = rules.EventPartners.Supports.First(row => row.CardId == Int(card, "support_card_id"));
            if (identity.Type != 2) continue;
            var position = Int(card, "position");
            var evaluation = evaluations[position];
            var unlocked = Int(evaluation, "is_outing") == 1;
            var outings = Int(evaluation, "story_step");
            if (!unlocked && (unseenFriendMask & (1 << (position - 1))) != 0)
                unseen |= 1 << (position - 1);
            friends.Add(new(position, identity.CharacterId, unlocked, outings));
        }

        var currentTurn = Int(chara, "turn");
        var firstDecisionTurn = Int(chara, "playing_state") == 35 && currentTurn != 3 ? currentTurn + 1 : currentTurn;
        var grade = Int(chara, "chara_grade");
        var traineeCard = Int(chara, "card_id");
        var character = traineeCard / 100;
        var route = Int(chara, "route_id");
        var runningStyle = chara["race_running_style"]?.GetValue<int>() ?? 0;
        var ignored = ImmutableArray.CreateBuilder<string>();
        var currentGoals = Integers(Array(chara, "route_race_id_array"), "route_race_id_array").ToHashSet();
        var progression = rules.Progression;
        var special = progression.SpecialGoals.Where(goal => goal.Character == character).ToArray();
        var missing = currentGoals.Except(progression.FixedRaceGoals.Select(goal => goal.Id))
            .Except(progression.CareerGoals.Select(goal => goal.Id)).Except(special.Select(goal => goal.Id)).Order().ToArray();
        if (missing.Length != 0) Note($"规则未收录目标 {string.Join('、', missing)}，已忽略其结算");
        ProjectCareerBranches(character, firstDecisionTurn, currentGoals, special, Note);
        history ??= PioneerGameData.ReadRaceHistory(common);
        reservations ??= PioneerGameData.ReadRaceReservations(common);
        randomPrograms ??= PioneerGameData.ReadRandomRacePrograms(common);
        PioneerEspoirRoute? espoir = null;
        if (character == 1081 && firstDecisionTurn <= 71 && !currentGoals.Overlaps([1475, 1476]))
        {
            var style = runningStyle;
            var pastStarts = (history ?? []).Where(race => race.Turn <= 67).ToArray();
            if (style is 1 or 2 && (firstDecisionTurn <= 12 || history is not null)
                && pastStarts.All(race => race.RunningStyle is 1 or 2))
            {
                var lead = pastStarts.Sum(race => race.RunningStyle == 1 ? 1 : -1);
                var minimum = progression.FixedRaceGoals.Count(goal => goal.Turn >= firstDecisionTurn && goal.Turn <= 67 && currentGoals.Contains(goal.Id));
                var maximum = Math.Max(0, 68 - Math.Max(12, firstDecisionTurn));
                var projected = new PioneerEspoirRoute(lead, style, []);
                var earliest = projected.GoalAfter(minimum);
                if (earliest != 0 && earliest == projected.GoalAfter(maximum))
                    currentGoals.Add(earliest);
                else
                {
                    if (firstDecisionTurn <= 67)
                    {
                        currentGoals.UnionWith([1475, 1476]);
                        espoir = projected;
                    }
                    Note("南部杯结束时如逃与先行出赛次数相同，忽略尚未确定的末战目标 1475 / 1476");
                }
            }
            else Note("末战目标 1475 / 1476 依赖截至南部杯的逃、先行出赛次数；缺少完整脚质或包含未验证脚质，已忽略此次分支");
        }
        var careerGoals = progression.CareerGoals.Where(goal => currentGoals.Contains(goal.Id) && goal.Deadline >= firstDecisionTurn)
            .Select(goal =>
            {
                if (history.HasValue || goal.RaceCount == 0 || goal.FirstTurn >= firstDecisionTurn) return goal;
                Note($"目标 {goal.Id} 缺少第 {goal.FirstTurn} 至 {firstDecisionTurn - 1} 回合的比赛历史；次数无法确定时忽略该目标的奖惩");
                return goal with { UnknownPastTurns = firstDecisionTurn - goal.FirstTurn };
            }).ToArray();
        var raceById = rules.Races.ToDictionary(race => race.ProgramId);
        if (character == 1022 && firstDecisionTurn <= 64)
        {
            if (runningStyle == 4) Note("目标 549 札幌纪念追込胜利时，各属性增量的分布未知，五维只计基础 +3");
            else if (runningStyle == 0) Note("目标 549 札幌纪念缺少脚质，只计四项基础属性、技能 Pt 和羁绊");
        }
        if (character == 1075 && firstDecisionTurn <= 72)
            Note("七场指定比赛全胜的隐藏事件 501075121，攻略记载的触发回合有分歧，已忽略该事件");
        var currentIds = common["race_condition_array"]?.AsArray().OfType<JsonObject>()
            .Select(row => Int(row, "program_id")).ToHashSet() ?? [];
        var fixedGoals = new List<PioneerFixedRaceGoal>();
        foreach (var goal in progression.FixedRaceGoals.Where(goal => goal.Turn >= firstDecisionTurn && currentGoals.Contains(goal.Id)))
        {
            if (raceById.ContainsKey(goal.ProgramId)) { fixedGoals.Add(goal); continue; }
            var members = progression.RaceGroups.Where(member => member.GroupId == goal.ProgramId)
                .Select(member => raceById[member.ProgramId]).ToArray();
            var revealed = members.Where(race => (goal.Turn == currentTurn && currentIds.Contains(race.ProgramId))
                || randomPrograms?.Contains(new(goal.Turn, race.ProgramId)) == true).ToArray();
            if (revealed.Length == 1) { fixedGoals.Add(goal with { ProgramId = revealed[0].ProgramId }); continue; }
            if (revealed.Length > 1) throw new InvalidDataException($"目标 {goal.Id} 的赛场组 {goal.ProgramId} 同时匹配多个已揭示比赛。");
            if (members.Length != 0 && !(goal.Turn == currentTurn && ready)
                && members.DistinctBy(race => race.RewardTerms).Count() == 1)
            {
                raceById.Add(goal.ProgramId, members[0] with { ProgramId = goal.ProgramId, Name = null });
                fixedGoals.Add(goal);
                Note($"目标 {goal.Id}（第 {goal.Turn} 回合、赛场组 {goal.ProgramId}）缺少本局场地，按各场相同的名次奖励及报名门槛结算");
            }
            else Note($"目标 {goal.Id}（第 {goal.Turn} 回合、赛场组 {goal.ProgramId}）无法确定有效比赛，已忽略");
        }
        var disabledRaces = home?["disable_race_turn_array"] as JsonArray ?? [];
        ulong blocked = 0;
        foreach (var turn in disabledRaces.Select(node => node!.GetValue<int>()))
            if (turn is >= 13 and <= 72) blocked |= 1UL << (turn - 13);
        foreach (var restriction in progression.RaceRestrictions.Where(row => row.Character == character && row.Turn >= firstDecisionTurn))
        {
            if (ready && restriction.Turn == currentTurn) continue;
            if (character == 1124)
                Note($"第 {restriction.Turn} 回合禁赛分支 {restriction.GainId} 的触发条件未知，模拟仅采用面板已揭示的禁赛");
            else
                blocked |= 1UL << (restriction.Turn - 13);
        }
        var races = ImmutableArray<PioneerRaceOption>.Empty;
        if (enabled.Contains(401) && (home!["race_entry_restriction"]?.GetValue<int>() ?? 0) == 0
            && !disabledRaces.Any(turn => turn!.GetValue<int>() == currentTurn)
            && common["race_condition_array"] is JsonArray)
        {
            var target = !enabled.Any(id => TrainingBaseTable.CommandIndex(id) >= 0);
            var fans = Int(chara, "fans");
            races = [.. rules.Races.Where(race => currentIds.Contains(race.ProgramId) && fans >= race.RequiredFans
                && (race.Grade switch { 900 => grade == 1, 800 => grade == 2, _ => grade >= 3 }))
                .Select(race =>
                {
                    var goal = fixedGoals.FirstOrDefault(goal => goal.Turn == currentTurn && goal.ProgramId == race.ProgramId);
                    return race.Option(goal.Id != 0 || target && (race.Grade == 900 || fixedGoals.All(goal => goal.Turn != currentTurn)))
                        with { CareerGoalId = goal.Id };
                })];
        }
        var scheduled = new SortedDictionary<int, PioneerRaceOption>();
        if (espoir is not null)
        {
            var alternatives = fixedGoals.Where(goal => goal.Id is 1475 or 1476).ToArray();
            espoir = alternatives.Length == 2 ? espoir with
            {
                Alternatives = [.. alternatives.Select(goal => new PioneerRaceScheduleEdit(goal.Id, Added: new(goal.Turn,
                    raceById[goal.ProgramId].Option(true) with { CareerGoalId = goal.Id })))],
            } : null;
            fixedGoals.RemoveAll(goal => goal.Id is 1475 or 1476);
        }
        foreach (var group in fixedGoals.Where(goal => goal.Turn > currentTurn || !ready).GroupBy(goal => goal.Turn))
        {
            var programs = group.Select(goal => goal.ProgramId).Distinct().ToArray();
            if (programs.Length == 1)
                scheduled.Add(group.Key, raceById[programs[0]].Option(true) with { CareerGoalId = group.First().Id });
            else Note($"第 {group.Key} 回合有多个互斥目标 {string.Join('、', group.Select(goal => goal.Id))}，已忽略该日目标");
        }
        foreach (var reservation in reservations ?? [])
        {
            if (!raceById.TryGetValue(reservation.ProgramId, out var race))
            {
                Note($"预约比赛 {reservation.ProgramId} 不在本剧本比赛表内，已忽略该项预约");
                continue;
            }
            var turn = rules.Progression.RaceCalendar.FirstOrDefault(date =>
                date.Year == reservation.Year && date.Month == race.Month && date.Half == race.Half).Turn;
            if (turn >= firstDecisionTurn && (!ready || turn > currentTurn || races.Any(option => option.ProgramId == race.ProgramId)))
                scheduled.TryAdd(turn, race.Option(false));
        }
        var reservedId = chara["reserve_race_program_id"]?.GetValue<int>() ?? 0;
        if (reservedId > 0 && races.FirstOrDefault(race => race.ProgramId == reservedId) is { ProgramId: > 0 } reserved)
            scheduled.TryAdd(currentTurn, reserved);
        var unknownJbc = new[] { 45, 69 }.Where(turn => turn >= firstDecisionTurn && (turn != currentTurn || !ready)
            && !scheduled.ContainsKey(turn) && (blocked & (1UL << (turn - 13))) == 0
            && !(randomPrograms ?? []).Any(program => program.Turn == turn)).ToArray();
        if (unknownJbc.Length > 0)
            Note($"第 {string.Join('、', unknownJbc)} 回合的 JBC 自选赛缺少本局场地，只计组内已知一致的奖励和报名门槛");
        bool? strongBrianRoute = currentGoals.Any(id => id is 378 or 380 or 382 or 384) ? true
            : currentGoals.Any(id => id is 379 or 381 or 383 or 385) ? false : null;
        return new(enabled.Contains(801), enabled.Contains(301), enabled.Contains(390), friends.ToImmutable(), races,
            [.. scheduled.Select(pair => new PioneerScheduledRace(pair.Key, pair.Value))], unseen, grade, blocked,
            [.. careerGoals], history, strongBrianRoute)
        {
            IgnoredMechanisms = ignored.ToImmutable(), RandomRacePrograms = randomPrograms ?? [], EspoirRoute = espoir,
            RunningStyle = runningStyle,
        };

        void Note(string detail) => ignored.Add($"育成卡 {traineeCard}，路线 {route}：{detail}。");
    }

    static void ProjectCareerBranches(int character, int turn, HashSet<int> goals, PioneerSpecialGoal[] special, Action<string> note)
    {
        // Future player choices use the first option; already revealed choices always win.
        switch (character)
        {
            case 1005:
                if (!goals.Overlaps([419, 420, 421, 422]))
                {
                    goals.UnionWith([420, 421]);
                    note("尚未选择经典赛线，模拟按第一选项三冠目标 420、421 继续，省略另一条赛线");
                }
                return;
            case 1022:
                if (!goals.Overlaps([550, 551, 552, 553]))
                {
                    goals.Add(550);
                    note("末战尚未选择，模拟按第一选项秋季天皇赏目标 550 继续，省略目标 551、552、553");
                }
                return;
            case 1056:
                if (turn <= 12 && !goals.Overlaps([90, 292, 293]))
                    note("出道后随机目标 90 / 292 / 293 的实际概率未知，模拟以实包加权 1 / 1 / 7 抽取一场");
                if (turn <= 60 && !goals.Overlaps([95, 294, 295]))
                    note("宝塚纪念后随机目标 95 / 294 / 295 的实际概率未知，模拟以实包加权 4 / 1 / 1 抽取一场");
                return;
            case 1069:
                if (!goals.Overlaps([573, 574]))
                {
                    goals.Add(573);
                    note("资深六月目标尚未选择，模拟按第一选项安田纪念 573 继续，省略目标 574");
                }
                return;
            case 1071:
                if (!goals.Overlaps([584, 585, 589, 590]))
                {
                    goals.UnionWith([584, 589]);
                    note("秋季赛线尚未选择，模拟按第一选项菊花赏路线 584、589 继续，省略目标 585、590");
                }
                return;
            case 1075:
                // Kinko Sho temporarily removes Victoria Mile. The recovery story restores the same race.
                goals.Add(1711);
                return;
            case 1092:
                if (turn <= 57) goals.Add(1498);
                return;
            case 1109:
                if (goals.Remove(1181)) goals.Add(1180);
                return;
            case 1116:
                goals.Add(1262);
                return;
            case 1121:
                if (turn <= 41) goals.Add(1281);
                return;
            case 1132:
                if (goals.Remove(1419)) goals.Add(1420);
                return;
            case 1134:
                if (goals.Remove(1642)) goals.UnionWith([1643, 1644]);
                return;
            case 1144:
                if (goals.Remove(1694)) goals.Add(1695);
                return;
            case 1009 or 1016 or 1031 or 1032 or 1062 or 1079 or 1081 or 1093 or 1135:
                return;
        }
        foreach (var group in special.Where(goal => goal.Target == 1).GroupBy(goal => goal.Order))
        {
            if (group.All(goal => goal.Turn < turn) || group.Any(goal => goal.Determine >= 2 && goals.Contains(goal.Id))) continue;
            note($"忽略尚未确定的目标变更 {string.Join('、', group.Select(goal => $"{goal.Id}（第 {goal.Turn} 回合）"))}，按已揭示赛程模拟");
        }
    }
}
