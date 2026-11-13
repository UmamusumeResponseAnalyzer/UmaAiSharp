using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public sealed class PioneerForecastSession
{
    readonly PioneerRulesSnapshot rules;
    readonly FrozenDictionary<int, PioneerRaceRule> races;
    readonly PioneerMasterCompiler compiler;
    readonly PioneerFacilityCompiler facilities;
    internal PioneerForecastModel Model { get; }
    public GoalDefinition Goal { get; }

    public IReadOnlyDictionary<int, PioneerRaceRule> Races => races;

    public PioneerForecastSession(PioneerRulesSnapshot rules, string modelPath, string goalPath)
    {
        ExecutionPlatform.EnsureSupported();
        this.rules = rules;
        Goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(goalPath), GoalJson.Options)
            ?? throw new InvalidDataException("育成目标为空。");
        if (Goal.Priorities.SelectMany(priority => priority.Terms).Any(term => term.Metric is AttributeOverflow))
            throw new InvalidDataException("经验事件只记录截断后的属性变化，无法恢复真实累计溢出；此预测入口不支持overflow目标。");
        using var file = File.OpenRead(modelPath);
        var input = JsonSerializer.Deserialize<ForecastModelInput>(file, PioneerGameJson.Options)
            ?? throw new InvalidDataException("预测模型为空。");
        if (!string.Equals(rules.SourceMasterSha256, input.MasterSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pioneer 规则资源与模型的源 master SHA-256 不一致。请使用从同一份 master 生成的规则和模型。");
        Model = new PioneerForecastModel(input, PioneerGameData.HashFile(modelPath));
        compiler = new(rules.Training);
        facilities = new(rules.Facilities);
        races = rules.Races.ToFrozenDictionary(race => race.ProgramId);
    }

    // The master compiler caches support panels. Prepare decisions on one worker per session.
    public PioneerForecastDecisionInput Prepare(JsonObject common, JsonObject pioneer, IReadOnlySet<int> history,
        JsonArray? revealedRewards = null)
    {
        var chara = common["chara_info"]!.AsObject();
        var pending = Objects(Array(common, "unchecked_event_array"), "unchecked_event_array").ToArray();
        if (pending.Length > 1) throw new UnknownMechanismException("当前输入同时包含多个事件，尚未确认选择顺序。");
        var frame = new JsonObject { ["c"] = chara.DeepClone(), ["s"] = pioneer.DeepClone(), ["home"] = common["home_info"]!.DeepClone(), ["events"] = new JsonArray() };
        var training = compiler.TrainingStart(frame, false, null, facilities);
        var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
        var actions = new List<ForecastAction>();
        var node = new ForecastNode(training.Turn, ForecastActionKind.Home, 0);
        PioneerStart.DeclaredEvent? currentEvent = null;
        PioneerStart.IslandTraining? island = null;
        if (pending.Length == 1)
        {
            var current = pending[0];
            node = node with { Kind = ForecastActionKind.Event, StoryId = Int(current, "story_id") };
            if (revealedRewards is not null)
                currentEvent = PioneerEventCompiler.Compile(chara, current, revealedRewards, EventPartners(rules.EventPartners, chara, pioneer));
            var choices = currentEvent?.Choices.Select(choice => choice.ChoiceNumber).ToArray()
                ?? Enumerable.Range(1, Array(current["event_contents_info"]!.AsObject(), "choice_array").Count).ToArray();
            if (choices.Length == 0) choices = [0];
            actions.AddRange(choices.Select(choice => new ForecastAction(ForecastActionKind.Event, Int(current, "event_id"), choice)));
        }
        else if (Int(chara, "playing_state") == 35)
        {
            node = node with { Kind = ForecastActionKind.Planning };
            var construction = new PioneerConstructionRules(Model.Input.Construction ?? throw new InvalidDataException("模型缺少建设master。"));
            actions.AddRange(construction.EnumeratePlans(development).Select(plan => new ForecastAction(ForecastActionKind.Planning, 0, Plan: plan)));
        }
        else if (Int(chara, "playing_state") == 1)
        {
            actions.AddRange(training.Commands.Where(command => command.Enabled).Select(command => new ForecastAction(ForecastActionKind.Training, command.CommandId)));
            if ((training.AvailableRestCommands & RestRules.OrdinaryAvailable) != 0) actions.Add(new(ForecastActionKind.Rest, RestRules.OrdinaryCommand));
            if ((training.AvailableRestCommands & RestRules.CampAvailable) != 0) actions.Add(new(ForecastActionKind.Rest, RestRules.CampCommand));
            var home = common["home_info"]!.AsObject();
            var disabled = Integers(Array(home, "disable_command_id_array"), "disable_command_id_array");
            var enabled = Objects(Array(home, "command_info_array"), "command_info_array")
                .Where(command => Int(command, "is_enable") == 1 && !disabled.Contains(Int(command, "command_id")))
                .Select(command => Int(command, "command_id")).ToHashSet();
            if (enabled.Contains(3101))
            {
                island = compiler.IslandStart(frame, facilities, false, null);
                actions.Add(new(ForecastActionKind.Island, 3101));
            }
            if (enabled.Contains(301)) actions.Add(new(ForecastActionKind.Outing, 0));
            if (enabled.Contains(390))
            {
                var partners = EventPartners(rules.EventPartners, chara, pioneer);
                foreach (var partner in partners.Where(partner => partner.IsSupport && partner.OutingUnlocked))
                    if (training.Deck[partner.TargetId - 1].TrainingCommandId == 0)
                        actions.Add(new(ForecastActionKind.Outing, 0, partner.CharacterId));
            }
            if (enabled.Contains(801)) actions.Add(new(ForecastActionKind.Treatment, 801));
            if (enabled.Contains(401))
            {
                foreach (var race in Objects(Array(common, "race_condition_array"), "race_condition_array"))
                {
                    var id = Int(race, "program_id");
                    if (!races.TryGetValue(id, out var program)) throw new UnknownMechanismException($"Pioneer 规则数据缺少可见比赛{id}。");
                    if (Int(chara, "fans") >= program.RequiredFans) actions.Add(new(ForecastActionKind.Race, 1, id));
                }
            }
        }
        else throw new UnknownMechanismException($"阶段{Int(chara, "playing_state")}的根决策尚未接入。");
        if (actions.Count == 0) throw new InvalidDataException("当前决策没有可用候选。");
        var configuration = PioneerGameData.Configuration(chara);
        var excludedRun = PioneerGameData.RunToken(chara);
        var facts = PioneerGameData.ReadValues(chara, pioneer);
        var root = new PioneerForecastState(node,
            Model.Input.Fields.Select(name => PioneerGameData.FieldValue(facts, name)).ToArray(),
            Model.Input.Stories.Select(story => history.Contains(story) ? 1 : 0).ToArray());
        var scenario = new PioneerForecastScenario(Model, configuration, excludedRun, training, [.. actions], currentEvent, island, development);
        return new(training.Turn, configuration, excludedRun, scenario, root, [.. actions]);
    }

    public RootSearchResult<ForecastAction> Evaluate(PioneerForecastDecisionInput input, ulong seed, int samples, int workers,
        CancellationToken cancellationToken = default)
        => RootSearch.Evaluate(input.Scenario, input.Scenario.CreatePolicy(), input.Root,
            input.Actions.Select((action, index) => new RootCandidate<ForecastAction>(index, action)).ToArray(),
            Goal.Compiled, seed, samples, workers, cancellationToken,
            input.Actions.Sum(action => action.Plan is { } plan ? 128L + 16L * plan.Steps.Length + 2L * plan.SemanticId.Length : 0));
}

public sealed class PioneerForecastDecisionInput
{
    internal PioneerForecastDecisionInput(int turn, string configuration, string excludedRun,
        PioneerForecastScenario scenario, PioneerForecastState root, ForecastAction[] actions)
    {
        Turn = turn;
        Configuration = configuration;
        ExcludedRun = excludedRun;
        Scenario = scenario;
        Root = root;
        Actions = actions;
    }

    public int Turn { get; }
    internal string Configuration { get; }
    internal string ExcludedRun { get; }
    internal PioneerForecastScenario Scenario { get; }
    internal PioneerForecastState Root { get; }
    internal ForecastAction[] Actions { get; }
}
