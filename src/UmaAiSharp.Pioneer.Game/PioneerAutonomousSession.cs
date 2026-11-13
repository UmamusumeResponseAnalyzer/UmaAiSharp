using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public sealed class PioneerAutonomousSession
{
    readonly PioneerRulesSnapshot rules;
    readonly string identity;
    readonly PioneerMasterCompiler compiler;
    readonly PioneerFacilityCompiler facilities;
    readonly CheckpointMasterData checkpoints;
    readonly GoalDefinition goal;
    readonly PioneerModel model;
    readonly FrozenDictionary<int, PioneerRaceRule> races;

    public PioneerAutonomousSession(Stream rulesResource, string goalPath, PioneerModel? model = null)
    {
        ExecutionPlatform.EnsureSupported();
        this.model = model ?? PrecisePioneerModel.Instance;
        (rules, identity) = PioneerRulesSnapshot.ReadArtifact(rulesResource);
        compiler = new(rules.Training);
        facilities = new(rules.Facilities);
        checkpoints = rules.CompileCheckpoints();
        races = rules.Races.ToFrozenDictionary(race => race.ProgramId);
        goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(goalPath), GoalJson.Options)
            ?? throw new InvalidDataException("育成目标为空。");
    }

    public string? RaceName(int programId) => races[programId].Name;

    internal static JsonObject CompleteOpening(JsonObject common)
    {
        var chara = common["chara_info"]!.AsObject();
        var queue = Array(common, "unchecked_event_array");
        if (Int(chara, "turn") != 1 || queue.Count != 1 || Int(queue[0]!.AsObject(), "event_id") != 3000)
            return common;

        // Project the fixed opening effects. The revealed start board is retained for this boundary.
        var result = (JsonObject)common.DeepClone();
        var projected = result["chara_info"]!.AsObject();
        projected["skill_point"] = checked(Int(projected, "skill_point") + 120);
        var director = Objects(Array(projected, "evaluation_info_array"), "evaluation_info_array")
            .Single(row => Int(row, "target_id") == 102);
        director["is_appear"] = 1;
        result["unchecked_event_array"] = new JsonArray();
        return result;
    }

    // Prepare and evaluate one live panel at a time because the compiler caches support panels.
    public RootSearchResult<PioneerRuntimeAction> Evaluate(JsonObject common, JsonObject pioneer,
        PioneerTrainingHistoryPrefix history, ulong seed, int samples, int workers, CancellationToken cancellationToken = default,
        UmaAiSharp.Pioneer.Inheritance.PioneerInheritanceContext? inheritance = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var turn = Int(common["chara_info"]!.AsObject(), "turn");
        if (turn < 55 && inheritance is null)
            throw new UnknownMechanismException($"第 {turn} 回合缺少本局开局冻结的亲本因子与胜鞍，无法模拟后续继承。");
        var planning = Int(common["chara_info"]!.AsObject(), "playing_state") == 35
            ? ReadPlanningPanel(common, pioneer, compiler) : null;
        var training = (planning?.Continuation ?? ReadTrainingPanel(common, pioneer, compiler, facilities, true,
            goldShipBoost: history.GoldShipBoost)) with
        {
            SuccessfulOrdinaryTrainingCounts = history.NeedsEstimate ? null : history.ObservedOrdinarySuccesses,
            Inheritance = inheritance,
            ObservedGrants = history.ObservedGrants,
            ObservedStoryHistory = ResolveStories(history.ObservedStories, rules),
            HomeActions = ReadHomeActions(common, rules, history.UnseenFriendMask ?? (history.Turn <= 2 ? 63 : 0), planning is null,
                history.RaceHistory, history.RaceReservations),
        };
        var estimated = history.NeedsEstimate ? new PioneerTrainingHistoryDistribution(training, history, rules.TurnCalibration!.TrainingHistory) : null;
        var placement = PioneerPlacementSeed.Observe(common["chara_info"]!.AsObject(), pioneer);
        PioneerStart start = planning is null ? training : planning with { Continuation = training };
        var data = PioneerRuntimeData.Compile(start, [], identity, rules.Construction, checkpoints,
            rules.CompileTurnRules(training, placement), usesStoryDecisions: true, trainingHistory: estimated, model: model);
        var scenario = new PioneerScenario(data, autonomous: true);
        var root = data.CreateInitialState();
        var candidates = scenario.Candidates(root).ToArray();
        if (candidates.Length == 0) throw new UnknownMechanismException("当前面板没有已接入的合法根行动。");
        return RootSearch.Evaluate(scenario, new PioneerContinuationPolicy(), root,
            candidates, goal.Compiled, seed, samples, workers, cancellationToken,
            candidates.Sum(candidate => candidate.Action.OwnedPayloadBytes), rankByAttributeRating: true);
    }

    internal static ImmutableArray<PioneerStoryOccurrence>? ResolveStories(ImmutableArray<PioneerStoryOccurrence>? stories,
        PioneerRulesSnapshot rules)
    {
        if (stories is not { } observed) return null;
        var progress = rules.StoryProgress!.Value.ToDictionary(row => row.StoryId);
        return [.. observed.Select(story => progress.TryGetValue(story.StoryId, out var match)
            ? story with { ProgressStage = match.Stage, ProgressLength = match.Length,
                SupportCardId = story.SupportCardId == 0 ? match.SupportCardId : story.SupportCardId }
            : story)];
    }

    internal static PioneerStart.TrainingTurn ReadTrainingPanel(JsonObject common, JsonObject pioneer, PioneerMasterCompiler compiler,
        PioneerFacilityCompiler? facilities, bool wholeRun, int? generatedTurn = null, GoldShipTrainingBoost? goldShipBoost = null)
    {
        var chara = common["chara_info"] as JsonObject ?? throw new InvalidDataException("训练面板缺少角色状态。");
        var frame = new JsonObject
        {
            ["c"] = chara.DeepClone(),
            ["s"] = pioneer.DeepClone(),
            ["home"] = common["home_info"]?.DeepClone(),
            ["events"] = Array(common, "unchecked_event_array").DeepClone(),
        };
        var training = compiler.TrainingStart(frame, goldShipBoost is not null, goldShipBoost, facilities, generatedTurn, wholeRun);
        var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
        return !wholeRun ? training with { Development = development } : training with
        {
            Development = development,
            HintProgress = new([.. Objects(Array(chara, "skill_tips_array"), "skill_tips_array").Select(hint => new HintLevel(
                new(Int(hint, "group_id"), Int(hint, "rarity")), Int(hint, "level")))],
                [.. Objects(Array(chara, "skill_array"), "skill_array").Select(skill => Int(skill, "skill_id"))]),
            ObservedIsland = generatedTurn is null && training.RawPlayingState == 1 && training.PendingStoryIds.IsEmpty
                ? compiler.ReadIslandBoard(frame) : null,
            CheckpointResults = [.. Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
                .OrderBy(row => Int(row, "check_point_type")).Select(row => Int(row, "result_state"))],
            DirectorBond = Int(Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array")
                .Single(row => Int(row, "target_id") == 102), "evaluation"),
        };
    }

    internal static PioneerStart.Planning ReadPlanningPanel(JsonObject common, JsonObject pioneer, PioneerMasterCompiler compiler)
    {
        var turn = Int(common["chara_info"]!.AsObject(), "turn");
        var context = ReadTrainingPanel(common, pioneer, compiler, null, true, generatedTurn: turn == 3 ? 3 : checked(turn + 1));
        return new(context.Development!, context with { Development = null });
    }

    internal static PioneerTrainingHistoryDistribution? EstimateTrainingHistory(PioneerStart start, PioneerRulesSnapshot rules)
    {
        var training = start as PioneerStart.TrainingTurn ?? (start as PioneerStart.Planning)?.Continuation;
        if (training is not { SuccessfulOrdinaryTrainingCounts: null, PendingStoryIds.IsEmpty: true }) return null;
        var turn = start is PioneerStart.Planning planning ? planning.State.Turn : training.Turn;
        var counts = training.Commands.OrderBy(command => TrainingBaseTable.CommandIndex(command.CommandId));
        var prefix = new PioneerTrainingHistoryPrefix(turn, [.. counts.Select(command => command.ExecutionCount)], [0, 0, 0, 0, 0]);
        return prefix.NeedsEstimate ? new(training, prefix, rules.TurnCalibration!.TrainingHistory) : null;
    }

    internal static PioneerHomeActions ReadHomeActions(JsonObject common, PioneerRulesSnapshot rules, int unseenFriendMask, bool ready,
        ImmutableArray<PioneerRaceHistory>? history = null, ImmutableArray<PioneerRaceReservation>? reservations = null)
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
        var currentGoals = (chara["route_race_id_array"] as JsonArray ?? []).Select(id => id!.GetValue<int>()).ToHashSet();
        var fixedGoals = rules.Progression!.FixedRaceGoals.Where(goal => goal.Turn >= firstDecisionTurn && currentGoals.Contains(goal.Id)).ToArray();
        var careerGoals = rules.Progression.CareerGoals.Where(goal => currentGoals.Contains(goal.Id) && goal.Deadline >= firstDecisionTurn).ToArray();
        history ??= PioneerGameData.ReadRaceHistory(common);
        reservations ??= PioneerGameData.ReadRaceReservations(common);
        if (history is null && careerGoals.Any(goal => goal.RaceCount > 0 && goal.FirstTurn < currentTurn))
            throw new UnknownMechanismException($"第 {currentTurn} 回合的累计出赛目标需要本局比赛历史，请从包含 race_history 的响应或抓包前缀导入。");
        var raceById = rules.Races.ToDictionary(race => race.ProgramId);
        var disabledRaces = home?["disable_race_turn_array"] as JsonArray ?? [];
        ulong blocked = 0;
        foreach (var turn in disabledRaces.Select(node => node!.GetValue<int>()))
            if (turn is >= 13 and <= 72) blocked |= 1UL << (turn - 13);
        var races = ImmutableArray<PioneerRaceOption>.Empty;
        if (enabled.Contains(401) && (home!["race_entry_restriction"]?.GetValue<int>() ?? 0) == 0
            && !disabledRaces.Any(turn => turn!.GetValue<int>() == currentTurn)
            && common["race_condition_array"] is JsonArray raceConditions)
        {
            var target = !enabled.Any(id => TrainingBaseTable.CommandIndex(id) >= 0);
            var currentIds = raceConditions.OfType<JsonObject>().Select(row => Int(row, "program_id")).ToHashSet();
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
        foreach (var group in fixedGoals.Where(goal => goal.Turn > currentTurn || !ready).GroupBy(goal => goal.Turn))
        {
            var programs = group.Select(goal => goal.ProgramId).Distinct().ToArray();
            if (programs.Length == 1)
                scheduled.Add(group.Key, raceById[programs[0]].Option(true) with { CareerGoalId = group.First().Id });
        }
        foreach (var reservation in reservations ?? [])
        {
            var race = raceById[reservation.ProgramId];
            var turn = rules.Progression.RaceCalendar.FirstOrDefault(date =>
                date.Year == reservation.Year && date.Month == race.Month && date.Half == race.Half).Turn;
            if (turn >= firstDecisionTurn && (!ready || turn > currentTurn || races.Any(option => option.ProgramId == race.ProgramId)))
                scheduled.TryAdd(turn, race.Option(false));
        }
        var reservedId = chara["reserve_race_program_id"]?.GetValue<int>() ?? 0;
        if (reservedId > 0 && races.FirstOrDefault(race => race.ProgramId == reservedId) is { ProgramId: > 0 } reserved)
            scheduled.TryAdd(currentTurn, reserved);
        return new(enabled.Contains(801), enabled.Contains(301), enabled.Contains(390), friends.ToImmutable(), races,
            [.. scheduled.Select(pair => new PioneerScheduledRace(pair.Key, pair.Value))], unseen, grade, blocked,
            [.. careerGoals], history);
    }
}
