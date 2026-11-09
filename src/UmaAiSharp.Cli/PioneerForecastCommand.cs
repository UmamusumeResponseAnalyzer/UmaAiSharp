using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer;
using static UmaAiSharp.Cli.Evidence.PioneerMasterCompiler;

namespace UmaAiSharp.Cli;

public static class PioneerForecastCommand
{
    public static int Run(Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        string[] required = ["--packets", "--packet-id", "--master", "--model", "--goal", "--seed", "--samples", "--workers", "--output"];
        if (options.Count != required.Length || required.Any(key => !options.ContainsKey(key)))
            throw new InvalidDataException("条件模型推荐需要--packets、--packet-id、--master、--model、--goal、--seed、--samples、--workers和--output。");
        if (!long.TryParse(options["--packet-id"], NumberStyles.None, CultureInfo.InvariantCulture, out var packetId) || packetId <= 0
            || !ulong.TryParse(options["--seed"], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
            throw new InvalidDataException("packet-id须为正整数，seed须为无符号64位整数。");
        var samples = PioneerExecutionCommand.Positive(options["--samples"], "--samples");
        var workers = PioneerExecutionCommand.Positive(options["--workers"], "--workers");
        var output = Path.GetFullPath(options["--output"]);
        if (File.Exists(output)) throw new InvalidDataException("推荐报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        var started = Stopwatch.GetTimestamp();
        var input = JsonSerializer.Deserialize<ForecastModelInput>(File.ReadAllBytes(options["--model"]), PioneerExecutionCommand.JsonOptions)
            ?? throw new InvalidDataException("预测模型为空。");
        if (PioneerForecastCompiler.HashFile(options["--master"]) != input.MasterSha256)
            throw new InvalidDataException("预测模型与本次master的哈希不一致，请用当前master重新编译模型。");
        var model = new PioneerForecastModel(input, PioneerForecastCompiler.HashFile(options["--model"]));
        var revealed = PioneerEventCapture.Read(options["--packets"], packetId);
        var captured = PioneerTrainingHistory.Read(options["--packets"], revealed?.History.StatePacketId ?? packetId);
        var response = PacketAudit.Decode(captured.Payload)["data"]!.AsObject();
        var common = PioneerNormalizer.GetCommon(response, captured.Endpoint)!;
        var chara = common["chara_info"]!.AsObject();
        var pioneer = response["pioneer_data_set"]!.AsObject();
        var pending = Objects(Array(common, "unchecked_event_array"), "unchecked_event_array").ToArray();
        if (pending.Length > 1) throw new UnknownMechanismException("当前输入同时包含多个事件，尚未确认选择顺序。");
        var compiler = new PioneerMasterCompiler(options["--master"]);
        var frame = new JsonObject { ["c"] = chara.DeepClone(), ["s"] = pioneer.DeepClone(), ["home"] = common["home_info"]!.DeepClone(), ["events"] = new JsonArray() };
        var training = compiler.TrainingStart(frame, false, null, new PioneerFacilityCompiler(options["--master"]));
        var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
        var actions = new List<ForecastAction>();
        var node = new ForecastNode(training.Turn, ForecastActionKind.Home, 0);
        PioneerStart.DeclaredEvent? currentEvent = null;
        PioneerStart.IslandTraining? island = null;
        using var master = EvidenceAudit.OpenReadOnly(options["--master"]);
        if (pending.Length == 1)
        {
            var current = pending[0];
            node = node with { Kind = ForecastActionKind.Event, StoryId = Int(current, "story_id") };
            if (revealed is not null)
                currentEvent = PioneerEventCompiler.Compile(chara, current, revealed.Rewards, EventPartners(master, chara, pioneer));
            var choices = currentEvent?.Choices.Select(choice => choice.ChoiceNumber).ToArray()
                ?? Enumerable.Range(1, Array(current["event_contents_info"]!.AsObject(), "choice_array").Count).ToArray();
            if (choices.Length == 0) choices = [0];
            actions.AddRange(choices.Select(choice => new ForecastAction(ForecastActionKind.Event, Int(current, "event_id"), choice)));
        }
        else if (Int(chara, "playing_state") == 35)
        {
            node = node with { Kind = ForecastActionKind.Planning };
            var rules = new PioneerConstructionRules(input.Construction ?? throw new InvalidDataException("模型缺少建设master。"));
            actions.AddRange(rules.EnumeratePlans(development).Select(plan => new ForecastAction(ForecastActionKind.Planning, 0, Plan: plan)));
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
                island = compiler.IslandStart(frame, new PioneerFacilityCompiler(options["--master"]), false, null);
                actions.Add(new(ForecastActionKind.Island, 3101));
            }
            if (enabled.Contains(301)) actions.Add(new(ForecastActionKind.Outing, 0));
            if (enabled.Contains(390))
            {
                var partners = EventPartners(master, chara, pioneer);
                foreach (var partner in partners.Where(partner => partner.IsSupport && partner.OutingUnlocked))
                    if (training.Deck[partner.TargetId - 1].TrainingCommandId == 0)
                        actions.Add(new(ForecastActionKind.Outing, 0, partner.CharacterId));
            }
            if (enabled.Contains(801)) actions.Add(new(ForecastActionKind.Treatment, 801));
            if (enabled.Contains(401))
            {
                var programs = Rows(master, "single_mode_program").ToDictionary(row => row["id"]);
                foreach (var race in Objects(Array(common, "race_condition_array"), "race_condition_array"))
                {
                    var id = Int(race, "program_id");
                    if (!programs.TryGetValue(id, out var program)) throw new UnknownMechanismException($"master缺少可见比赛{id}。");
                    if (Int(chara, "fans") >= program["need_fan_count"]) actions.Add(new(ForecastActionKind.Race, 1, id));
                }
            }
        }
        else throw new UnknownMechanismException($"阶段{Int(chara, "playing_state")}的根决策尚未接入。");
        if (actions.Count == 0) throw new InvalidDataException("当前决策没有可用候选。");
        var configuration = PioneerForecastCompiler.Configuration(chara);
        var excludedRun = PioneerForecastCompiler.RunToken(chara);
        var facts = PioneerForecastCompiler.ReadValues(chara, pioneer);
        var history = ReadHistory(options["--packets"], captured.History.StartPacketId, packetId);
        var root = new PioneerForecastState(node,
            input.Fields.Select(name => PioneerForecastCompiler.FieldValue(facts, name)).ToArray(),
            input.Stories.Select(story => history.Contains(story) ? 1 : 0).ToArray());
        var scenario = new PioneerForecastScenario(model, configuration, excludedRun, training, [.. actions], currentEvent, island, development);
        var goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(options["--goal"]), GoalJson.Options)
            ?? throw new InvalidDataException("育成目标为空。");
        if (goal.Priorities.SelectMany(priority => priority.Terms).Any(term => term.Metric is AttributeOverflow))
            throw new InvalidDataException("经验事件只记录截断后的属性变化，无法恢复真实累计溢出；此预测入口不支持overflow目标。");
        var compile = Stopwatch.GetElapsedTime(started);
        var allocation = GC.GetTotalAllocatedBytes(true);
        started = Stopwatch.GetTimestamp();
        var result = RootSearch.Evaluate(scenario, scenario.CreatePolicy(), root,
            actions.Select((action, index) => new RootCandidate<ForecastAction>(index, action)).ToArray(),
            goal.Compiled, seed, samples, workers, cancellationToken,
            actions.Sum(action => action.Plan is { } plan ? 128L + 16L * plan.Steps.Length + 2L * plan.SemanticId.Length : 0));
        var elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocation;
        var noFeasibleSamples = result.Status == SearchStatus.Completed && result.Candidates.All(candidate => candidate.Summary!.SuccessCount == 0);
        var sourceRows = input.Transitions.Where(row => row.Configuration == scenario.EmpiricalConfiguration).ToArray();
        var eligibleRows = sourceRows.Where(row => row.Run != excludedRun).ToArray();
        var selected = result.RecommendedCandidateId is { } recommended
            ? result.Candidates.Single(candidate => candidate.Candidate.Id == recommended) : null;
        var recommendation = selected?.Candidate.Action;
        var summaries = result.Candidates.Select(candidate => new
        {
            action = candidate.Candidate.Action,
            complete = candidate.Batch.Results.Count(sample => sample.Status == TrajectoryStatus.Completed),
            requested = samples,
            distinctCompletedOutcomes = candidate.Batch.Results.Where(sample => sample.Status == TrajectoryStatus.Completed).Select(sample => sample.Outcome).Distinct().Count(),
            stops = candidate.Batch.Results.Where(sample => sample.Stop is not null).GroupBy(sample => sample.Stop!.Value.Code)
                .Select(group => new { code = group.Key, count = group.Count(), firstTurn = group.Min(sample => sample.Stop!.Value.Position),
                    description = group.Key switch { 2001 => "行动不属于当前决策", 2002 => "当前条件没有可用转移", 2003 => "模型转移不能到达合法回合或终局",
                        2004 => "达到2048步预测预算", 2005 => "按失败率抽到的训练分支没有对应样本", 2006 => "经验增量导致资源不足",
                        2007 => "当前事件资源结算尚未支持", 2008 => "片头痛与事件剩余效果的顺序未确认", 2009 => "已揭示选项的多个结果缺少条件概率",
                        2010 => "模型缺少当前事件人员字段", 2011 => "岛训练缺少生成模板", 2012 => "岛训练券不足或未知",
                        2013 => "当前岛训练条件尚未解释", 2014 => "缺少完整建设状态", 2015 => "建设计划不合法", 2016 => "设施与当前计划前态不符",
                        2017 => "当前卡组或训练条件包含未解释效果", _ => "执行器停止" } })
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var file = new FileStream(output, FileMode.CreateNew))
            JsonSerializer.Serialize(file, new { schemaVersion = 2, mode = "empiricalTransition", model = scenario.RulesIdentity, configuration,
                empiricalConfiguration = scenario.EmpiricalConfiguration, transfersConfiguration = configuration != scenario.EmpiricalConfiguration, excludedRun,
                executionBoundary = "事件已校准资源、当前六卡训练和岛训练数值重算；未解析后果、事件时机、继承和比赛结果使用经验转移。不是完整机制模拟。",
                assumptions = new[] { "无同配置样本时按角色、支援类型、卡号和等级选择经验上下文；后续可使用其他配置的同回合转移。",
                    "已揭示单结果事件使用当前奖励；后续调度汇集该事件各选项，不假定未选过的选项没有奖励。",
                    "未见比赛按同等级、奖励组和场地类型共享结果样本，未模拟赛道过程或证明胜率。",
                    "后续建设策略选取合法枚举中的首项；设施与券在经验调度的建设事件中按当前计划和发展Pt结算。",
                    "普通训练人员、事件连续进度及剩余效果包含经验估计；跨卡事件原子沿用来源卡位，不代表目标卡专属事件。" },
                inputHistory = captured.History,
                coverage = new { runs = eligibleRows.Select(row => row.Run).Distinct().Count(), transitions = eligibleRows.Length,
                    excludedTransitions = sourceRows.Length - eligibleRows.Length },
                recommendation, recommendationEvaluation = selected?.Summary, noFeasibleSamples, summaries, eventRewards = revealed?.History,
                sourcePacket = packetId, turn = training.Turn, goal = goal.Identity, seed, workers, samplesPerCandidate = samples,
                historyEvents = history.Count, compileMilliseconds = compile.TotalMilliseconds, executionMilliseconds = elapsed.TotalMilliseconds,
                processAllocatedBytes = bytes, search = result }, PioneerExecutionCommand.JsonOptions);
        Console.WriteLine(selected is not null
            ? $"回合{training.Turn}建议候选{selected.Candidate.Id}：{selected.Candidate.Action.Kind switch
                { ForecastActionKind.Training => "训练", ForecastActionKind.Rest => "休息", ForecastActionKind.Event => "事件",
                  ForecastActionKind.Race => "比赛", ForecastActionKind.Island => "岛训练", ForecastActionKind.Planning => "建设规划",
                  ForecastActionKind.Outing => "外出", ForecastActionKind.Treatment => "保健室", _ => throw new InvalidOperationException("根候选类型无显示名称。") }}，行动{selected.Candidate.Action.Id}，选项／比赛{selected.Candidate.Action.Choice}，每候选{samples}条终局轨迹。"
            : "候选模拟存在停止轨迹，停止位置已写入报告。");
        Console.WriteLine("经验过渡预测：剩余经验分量及配置迁移已写入报告，不代表完整机制模拟。");
        if (noFeasibleSamples) Console.WriteLine("全部候选均无达标样本，建议按平均条件总缺口、目标优先级选择最接近目标的行动。");
        if (selected is not null) Console.WriteLine(GoalEvaluation.ProbabilityText(selected.Summary!));
        return result.Status == SearchStatus.Completed ? 0 : 3;
    }

    static HashSet<int> ReadHistory(string path, long start, long anchor)
    {
        using var db = EvidenceAudit.OpenReadOnly(path);
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT r.id,r.endpoint,r.payload,q.payload,r.request_id FROM packets r
            LEFT JOIN packets q ON q.request_id=r.request_id AND q.endpoint=r.endpoint AND q.viewer_id=r.viewer_id AND q.direction='request' AND q.id<r.id
            WHERE r.direction='response' AND r.id>=$start AND r.id<=$end
                AND r.viewer_id=(SELECT viewer_id FROM packets WHERE id=$end) ORDER BY r.id,q.id
            """;
        query.Parameters.AddWithValue("$start", start); query.Parameters.AddWithValue("$end", anchor);
        using var reader = query.ExecuteReader();
        var seen = new HashSet<string>();
        var stories = new HashSet<int>();
        JsonArray? pending = null;
        while (reader.Read())
        {
            var route = reader.GetString(1);
            if (!seen.Add(reader.GetString(4) + route)) continue;
            if (route.EndsWith("/check_event", StringComparison.Ordinal))
            {
                if (reader.IsDBNull(3)) throw new InvalidDataException($"事件响应{reader.GetInt64(0)}缺少配对请求，事件历史不完整。");
                var request = PioneerForecastCompiler.RequestBody(PacketAudit.Decode((byte[])reader.GetValue(3)));
                var eventId = Int(request, "event_id");
                var current = pending?.OfType<JsonObject>().SingleOrDefault(row => Int(row, "event_id") == eventId)
                    ?? throw new InvalidDataException($"事件响应{reader.GetInt64(0)}无法与此前事件对应，历史不完整。");
                stories.Add(Int(current, "story_id"));
            }
            var data = PacketAudit.Decode((byte[])reader.GetValue(2))["data"] as JsonObject;
            var common = PioneerNormalizer.GetCommon(data, route);
            if (common?["unchecked_event_array"] is JsonArray next) pending = next;
        }
        return stories;
    }
}
