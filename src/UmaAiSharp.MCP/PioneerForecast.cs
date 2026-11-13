using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP;

public static class PioneerForecast
{
    public static ToolResult Run(string capturePath, long packetId, string masterPath, string modelPath,
        string goalPath, ulong seed, int samples, int workers, string outputPath, CancellationToken cancellationToken)
    {
        ToolArguments.ValidateBatch(samples, workers);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new InvalidDataException("推荐报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        var started = Stopwatch.GetTimestamp();
        var session = new PioneerForecastSession(PioneerMasterReader.ReadForecast(masterPath), modelPath, goalPath);
        var revealed = PioneerEventCapture.Read(capturePath, packetId, cancellationToken);
        var captured = PioneerTrainingHistory.Read(capturePath, revealed?.History.StatePacketId ?? packetId, cancellationToken);
        var response = PacketAudit.Decode(captured.Payload)["data"]!.AsObject();
        var common = PioneerNormalizer.GetCommon(response, captured.Endpoint)!;
        var history = ReadHistory(capturePath, captured.History.StartPacketId, packetId, cancellationToken);
        var decision = session.Prepare(common, response["pioneer_data_set"]!.AsObject(), history, revealed?.Rewards);
        var scenario = decision.Scenario;
        var configuration = decision.Configuration;
        var excludedRun = decision.ExcludedRun;
        var compile = Stopwatch.GetElapsedTime(started);
        var allocation = GC.GetTotalAllocatedBytes(true);
        started = Stopwatch.GetTimestamp();
        var result = session.Evaluate(decision, seed, samples, workers, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocation;
        var noFeasibleSamples = result.Status == SearchStatus.Completed && result.Candidates.All(candidate => candidate.Summary!.SuccessCount == 0);
        var sourceRows = session.Model.Input.Transitions.Where(row => row.Configuration == scenario.EmpiricalConfiguration).ToArray();
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
                    "抽到训练失败时，同模式完整样本中的选项1用作经验惩罚，扣当前主属性5和干劲1，增加当前训练执行次数。后续使用本回合经验调度，跳过训练提示与升级。",
                    "未见比赛按同等级、奖励组和场地类型共享结果样本，未模拟赛道过程或证明胜率。",
                    "后续建设策略选取合法枚举中的首项；设施与券在经验调度的建设事件中按当前计划和发展Pt结算。",
                    "普通训练人员、事件连续进度及剩余效果包含经验估计；跨卡事件原子沿用来源卡位，不代表目标卡专属事件。" },
                inputHistory = captured.History,
                coverage = new { runs = eligibleRows.Select(row => row.Run).Distinct().Count(), transitions = eligibleRows.Length,
                    excludedTransitions = sourceRows.Length - eligibleRows.Length },
                recommendation, recommendationEvaluation = selected?.Summary, noFeasibleSamples, summaries, eventRewards = revealed?.History,
                sourcePacket = packetId, turn = decision.Turn, goal = session.Goal.Identity, seed, workers, samplesPerCandidate = samples,
                historyEvents = history.Count, compileMilliseconds = compile.TotalMilliseconds, executionMilliseconds = elapsed.TotalMilliseconds,
                processAllocatedBytes = bytes, search = result }, PioneerExecution.JsonOptions);
        return ToolResult.Report(output, new
        {
            mode = "empiricalTransition", recommendedCandidateId = result.RecommendedCandidateId,
            recommendedAction = recommendation, recommendationEvaluation = selected?.Summary,
            samplesPerCandidate = samples, candidateCount = decision.Actions.Length,
            stops = ToolResult.SearchStops(result)
        }, ToolResult.SearchStatus(result.Status));
    }

    static HashSet<int> ReadHistory(string path, long start, long anchor, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            var route = reader.GetString(1);
            if (!seen.Add(reader.GetString(4) + route)) continue;
            if (route.EndsWith("/check_event", StringComparison.Ordinal))
            {
                if (reader.IsDBNull(3)) throw new InvalidDataException($"事件响应{reader.GetInt64(0)}缺少配对请求，事件历史不完整。");
                var request = PioneerForecastCompiler.RequestBody(PacketAudit.Decode((byte[])reader.GetValue(3)));
                var eventId = Int(request, "event_id");
                var current = pending?.OfType<JsonObject>().SingleOrDefault(row =>
                    Int(row, "event_id") == eventId && Int(row, "chara_id") == Int(request, "chara_id"))
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
