using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Base.Hints;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Base.Scoring;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public sealed record PioneerPanelAdvice(RootSearchResult<PioneerRuntimeAction> Search, int? OutingDevelopmentPoints,
    ImmutableArray<string> IgnoredMechanisms);

public sealed partial class PioneerAutonomousSession
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

    public PioneerEventAdvice EvaluateChoices(JsonObject chara, JsonObject currentEvent, JsonObject pioneer,
        JsonArray rewards, CancellationToken cancellationToken = default)
    {
        var start = PioneerEventCompiler.Compile(chara, currentEvent, rewards, pioneer, rules.EventPartners);
        var data = PioneerRuntimeData.Compile(start, [], identity);
        var scenario = new PioneerScenario(data, autonomous: true);
        var candidates = start.Choices.Select(choice => new RootCandidate<PioneerRuntimeAction>(
            choice.ChoiceNumber, PioneerRuntimeAction.Choose(start.Event, choice.ChoiceNumber))).ToArray();
        return scenario.EvaluateEventChoices(data.CreateInitialState(), candidates, goal.Compiled, cancellationToken);
    }

    internal static JsonObject CompleteOpening(JsonObject common)
    {
        var chara = common["chara_info"]!.AsObject();
        var queue = Array(common, "unchecked_event_array");
        if (Int(chara, "turn") != 1 || queue.Count != 1) return common;
        var opening = queue[0]!.AsObject();
        var storyId = Int(opening, "story_id");
        if (storyId / 1000 != 500000 + Int(opening, "chara_id") || storyId % 1000 is not (100 or 400)) return common;

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
    public PioneerPanelAdvice Evaluate(JsonObject common, JsonObject pioneer,
        PioneerTrainingHistoryPrefix history, ulong seed, int samples, int workers, CancellationToken cancellationToken = default,
        UmaAiSharp.Pioneer.Inheritance.PioneerInheritanceContext? inheritance = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planning = Int(common["chara_info"]!.AsObject(), "playing_state") == 35
            ? ReadPlanningPanel(common, pioneer, compiler) : null;
        var training = (planning?.Continuation ?? ReadTrainingPanel(common, pioneer, compiler, facilities,
            goldShipBoost: history.GoldShipBoost)) with
        {
            SuccessfulOrdinaryTrainingCounts = history.NeedsEstimate ? null : history.ObservedOrdinarySuccesses,
            Inheritance = inheritance,
            ObservedGrants = history.ObservedGrants,
            ObservedStoryHistory = ResolveStories(history.ObservedStories, rules),
            HomeActions = ReadHomeActions(common, rules, history.UnseenFriendMask ?? (history.Turn <= 2 ? 63 : 0), planning is null,
                history.RaceHistory, history.RaceReservations, history.RandomRacePrograms),
        };
        var estimated = history.NeedsEstimate ? new PioneerTrainingHistoryDistribution(training, history, rules.TurnCalibration.TrainingHistory) : null;
        var placement = PioneerPlacementSeed.Observe(common["chara_info"]!.AsObject(), pioneer);
        PioneerStart start = planning is null ? training : planning with { Continuation = training };
        var data = PioneerRuntimeData.Compile(start, [], identity, rules.Construction, checkpoints,
            rules.CompileTurnRules(training, placement), trainingHistory: estimated, model: model,
            raceOnlyCharacterEffects: rules.RaceOnlyCharacterEffects, forecast: true);
        var scenario = new PioneerScenario(data, autonomous: true);
        var root = data.CreateInitialState();
        var candidates = scenario.Candidates(root).ToArray();
        if (candidates.Length == 0) throw new UnknownMechanismException("当前面板没有已接入的合法根行动。");
        var result = RootSearch.Evaluate(scenario, new PioneerContinuationPolicy(), root,
            candidates, goal.Compiled, seed, samples, workers, cancellationToken,
            candidates.Sum(candidate => candidate.Action.OwnedPayloadBytes), rankByTotalRating: true,
            pairedSampling: data.RootSampling);
        var outingPoints = result.RecommendedCandidateId is int id
            ? scenario.PreviewFriendOutingDevelopmentPoints(root, candidates.Single(candidate => candidate.Id == id).Action)
            : null;
        return new(result, outingPoints, data.IgnoredMechanisms);
    }

    internal static ImmutableArray<PioneerStoryOccurrence>? ResolveStories(ImmutableArray<PioneerStoryOccurrence>? stories,
        PioneerRulesSnapshot rules)
    {
        if (stories is not { } observed) return null;
        var progress = rules.StoryProgress.ToDictionary(row => row.StoryId);
        return [.. observed.Select(story => progress.TryGetValue(story.StoryId, out var match)
            ? story with { ProgressStage = match.Stage, ProgressLength = match.Length,
                SupportCardId = story.SupportCardId == 0 ? match.SupportCardId : story.SupportCardId }
            : story)];
    }

    internal static PioneerStart.TrainingTurn ReadTrainingPanel(JsonObject common, JsonObject pioneer, PioneerMasterCompiler compiler,
        PioneerFacilityCompiler? facilities, int? generatedTurn = null, GoldShipTrainingBoost? goldShipBoost = null)
    {
        var chara = common["chara_info"] as JsonObject ?? throw new InvalidDataException("训练面板缺少角色状态。");
        var frame = new JsonObject
        {
            ["c"] = chara.DeepClone(),
            ["s"] = pioneer.DeepClone(),
            ["home"] = common["home_info"]?.DeepClone(),
            ["events"] = Array(common, "unchecked_event_array").DeepClone(),
        };
        var training = compiler.TrainingStart(frame, goldShipBoost, facilities, generatedTurn);
        if (generatedTurn is null && training.RawPlayingState == 1 && training.PendingStoryIds.IsEmpty)
        {
            var home = Objects(Array(frame["home"]!.AsObject(), "command_info_array"), "home commands")
                .Where(row => Int(row, "command_type") == 1).ToDictionary(row => Int(row, "command_id"));
            var scenario = Objects(Array(pioneer, "command_info_array"), "pioneer commands")
                .Where(row => Int(row, "command_type") == 1).ToDictionary(row => Int(row, "command_id"));
            var ignored = training.IgnoredMechanisms.ToHashSet();
            var island = compiler.ReadIslandBoard(frame, ignored);
            ObservedTrainingGains Gains(int id)
            {
                var row = home[id];
                var upper = scenario.GetValueOrDefault(id);
                void Ignore(int target) => ignored.Add($"育成卡 {training.CardId} 的训练 {id}：面板奖励目标 {target} 未纳入收益。");
                return new(row["params_inc_dec_info_array"] is JsonArray ? Values(row, Ignore) : null,
                    upper?["params_inc_dec_info_array"] is JsonArray ? Values(upper, Ignore) : null);
            }
            training = training with { Commands = [.. training.Commands.Select(command =>
            {
                if (!command.Enabled) return command;
                var id = command.CommandId;
                var gains = Gains(id);
                return command with { Observed = new(gains.Common, gains.Scenario, Int(home[id], "failure_rate")) };
            })], ObservedIsland = island, ObservedIslandGains = island is null ? null : Gains(3101),
                IgnoredMechanisms = [.. ignored.Distinct()] };
        }
        var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
        return training with
        {
            Development = development,
            HintProgress = new([.. Objects(Array(chara, "skill_tips_array"), "skill_tips_array").Select(hint => new HintLevel(
                new(Int(hint, "group_id"), Int(hint, "rarity")), Int(hint, "level")))],
                [.. Objects(Array(chara, "skill_array"), "skill_array").Select(skill =>
                    new OwnedScoringSkill(Int(skill, "skill_id"), Int(skill, "level")))],
                [.. Array(chara, "disable_skill_id_array").Select(id => id!.GetValue<int>())],
                Int(chara, "talent_level"), PioneerInheritanceCapture.ReadAptitudes(chara)),
            CheckpointResults = [.. Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
                .OrderBy(row => Int(row, "check_point_type")).Select(row => Int(row, "result_state"))],
            DirectorBond = Int(Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array")
                .Single(row => Int(row, "target_id") == 102), "evaluation"),
            ReporterBond = Int(Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array")
                .Single(row => Int(row, "target_id") == 103), "evaluation"),
        };
    }

    internal static PioneerStart.Planning ReadPlanningPanel(JsonObject common, JsonObject pioneer, PioneerMasterCompiler compiler)
    {
        var turn = Int(common["chara_info"]!.AsObject(), "turn");
        var context = ReadTrainingPanel(common, pioneer, compiler, null, generatedTurn: turn == 3 ? 3 : checked(turn + 1));
        return new(context.Development!, context with { Development = null });
    }

    internal static PioneerTrainingHistoryDistribution? EstimateTrainingHistory(PioneerStart start, PioneerRulesSnapshot rules)
    {
        var training = start as PioneerStart.TrainingTurn ?? (start as PioneerStart.Planning)?.Continuation;
        if (training is not { SuccessfulOrdinaryTrainingCounts: null, PendingStoryIds.IsEmpty: true }) return null;
        var turn = start is PioneerStart.Planning planning ? planning.State.Turn : training.Turn;
        var counts = training.Commands.OrderBy(command => TrainingBaseTable.CommandIndex(command.CommandId));
        var prefix = new PioneerTrainingHistoryPrefix(turn, [.. counts.Select(command => command.ExecutionCount)], [0, 0, 0, 0, 0]);
        return prefix.NeedsEstimate ? new(training, prefix, rules.TurnCalibration.TrainingHistory) : null;
    }
}
