using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Base.Races;
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
        CharacterRaceRoutes.ProjectFutureGoals(character, firstDecisionTurn, currentGoals, special, Note);
        history ??= PioneerGameData.ReadRaceHistory(common);
        reservations ??= PioneerGameData.ReadRaceReservations(common);
        randomPrograms ??= PioneerGameData.ReadRandomRacePrograms(common);
        EspoirRaceRoute? espoir = null;
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
                var projected = new EspoirRaceRoute(lead, style, []);
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
        var fixedGoals = new List<FixedRaceGoal>();
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
        var races = ImmutableArray<RaceOption>.Empty;
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
        if (espoir is not null)
        {
            var alternatives = fixedGoals.Where(goal => goal.Id is 1475 or 1476).ToArray();
            espoir = alternatives.Length == 2 ? espoir with
            {
                Alternatives = [.. alternatives.Select(goal => new RaceScheduleEdit(goal.Id, 0, goal.Schedule(raceById[goal.ProgramId].Option(true)), 0))],
            } : null;
            fixedGoals.RemoveAll(goal => goal.Id is 1475 or 1476);
        }
        var selected = fixedGoals.Where(goal => goal.Turn > currentTurn || !ready);
        var scheduled = CharacterRaceSchedule.FromSelectedGoals(selected, id => raceById[id].Option(true), AvailableReservations(),
            group => Note($"第 {group.Key} 回合有多个互斥目标 {string.Join('、', group.Select(goal => goal.Id))}，已忽略该日目标"));
        var unknownJbc = new[] { 45, 69 }.Where(turn => turn >= firstDecisionTurn && (turn != currentTurn || !ready)
            && !scheduled.Any(race => race.Turn == turn) && (blocked & (1UL << (turn - 13))) == 0
            && !(randomPrograms ?? []).Any(program => program.Turn == turn)).ToArray();
        if (unknownJbc.Length > 0)
            Note($"第 {string.Join('、', unknownJbc)} 回合的 JBC 自选赛缺少本局场地，只计组内已知一致的奖励和报名门槛");
        return new(enabled.Contains(801), enabled.Contains(301), enabled.Contains(390), friends.ToImmutable(), races,
            scheduled, unseen, grade, blocked,
            [.. careerGoals], history, CharacterRaceRoutes.BrianRouteFromSelectedGoals(currentGoals))
        {
            IgnoredMechanisms = ignored.ToImmutable(), RandomRacePrograms = randomPrograms ?? [], EspoirRoute = espoir,
            RunningStyle = runningStyle,
        };

        IEnumerable<ScheduledRace> AvailableReservations()
        {
            foreach (var reservation in reservations ?? [])
            {
                if (!raceById.TryGetValue(reservation.ProgramId, out var race))
                {
                    Note($"预约比赛 {reservation.ProgramId} 不在本剧本比赛表内，已忽略该项预约");
                    continue;
                }
                var turn = progression.RaceCalendar.FirstOrDefault(date =>
                    date.Year == reservation.Year && date.Month == race.Month && date.Half == race.Half).Turn;
                if (turn >= firstDecisionTurn && (!ready || turn > currentTurn || races.Any(option => option.ProgramId == race.ProgramId)))
                    yield return new(turn, race.Option(false));
            }
            var reservedId = chara["reserve_race_program_id"]?.GetValue<int>() ?? 0;
            if (reservedId > 0 && races.FirstOrDefault(race => race.ProgramId == reservedId) is { ProgramId: > 0 } reserved)
                yield return new(currentTurn, reserved);
        }

        void Note(string detail) => ignored.Add($"育成卡 {traineeCard}，路线 {route}：{detail}。");
    }
}
