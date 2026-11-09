using System.Text.Json;
using UmaAiSharp.Cli.Evidence;

namespace UmaAiSharp.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("evidence audit --packets <SQLite文件或目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence handoff --input <handoff目录> --output <新目录>");
            Console.WriteLine("evidence scoring --packets <SQLite文件或目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence training --input <标准化证据目录> --output <新目录>");
            Console.WriteLine("evidence training-formula --input <标准化证据目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence training-modes --input <标准化证据目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence terminal --input <标准化证据目录> --output <新目录>");
            Console.WriteLine("evidence hint-pool --research <云端Hint研究目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence island-action --input <岛训练行动研究目录> --frames <对应帧.jsonl> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence event-components --input <标准化证据目录> --fixtures <事件夹具目录> --output <新目录>");
            Console.WriteLine("evidence initial-state --study <继承研究目录> --input <标准化证据目录> --packets <包库目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence point-gain --input <标准化证据目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence development-components --input <标准化证据目录> --research <阻塞项研究目录> --output <新目录>");
            Console.WriteLine("evidence race-points --input <标准化证据目录> --study <比赛研究目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence placement-hint --input <标准化证据目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence race-route --input <标准化证据目录> --master <master.mdb> --output <新目录>");
            Console.WriteLine("evidence checkpoint --input <标准化证据目录> --output <新目录>");
            Console.WriteLine("evidence construction --input <证据审计目录> --output <新目录>");
            Console.WriteLine("evidence validate --report <audit.json>");
            Console.WriteLine("goals evaluate --goal <目标.json> --outcomes <终局指标数组.json> --output <新报告.json>");
            Console.WriteLine("pioneer run --input <自主起点与脚本.json> --seed <种子> --output <新报告.json> [--master <master.mdb>]");
            Console.WriteLine("pioneer replay --input <条件回放.json> --seed <种子> --output <新报告.json> [--master <master.mdb>]");
            Console.WriteLine("pioneer batch --input <明确模式的起点与脚本.json> --seed <种子> --samples <样本数> --workers <1..32> --output <新报告.json> [--master <master.mdb>]");
            Console.WriteLine("pioneer recommend --input <当前起点与后续脚本.json> --goal <目标.json> --candidates <候选动作.json> --seed <种子> --samples <每候选样本数> --workers <1..32> --output <新报告.json> [--master <master.mdb>]");
            Console.WriteLine("pioneer model --evidence <规范化证据目录> --events <event-transitions.jsonl> --master <master.mdb> --output <新目录>");
            Console.WriteLine("pioneer event-model --events <event-transitions.jsonl> --model <源model.json> --output <新报告.json>");
            Console.WriteLine("pioneer recommend --packets <抓包.sqlite> --packet-id <当前响应编号> --master <master.mdb> --model <model.json> --goal <目标.json> --seed <种子> --samples <每候选样本数> --workers <1..32> --output <新报告.json>");
            Console.WriteLine("pioneer import --packet <MessagePack响应文件> --endpoint <响应路径> --master <master.mdb> --output <新目录>");
            Console.WriteLine("pioneer import --packets <抓包SQLite> --packet-id <当前状态或奖励响应编号> --master <master.mdb> --output <新目录>");
            return 0;
        }
        try
        {
            return Run(args);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or Microsoft.Data.Sqlite.SqliteException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine(CliDiagnostics.Describe(error));
            return 2;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("evidence" or "goals" or "pioneer")) throw new InvalidDataException("未知命令；使用--help查看用法。");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(args[i], args[i + 1])) throw new InvalidDataException("参数必须成对且不能重复。");
        }
        if (args[0] == "pioneer")
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try { return PioneerExecutionCommand.Run(args[1], values, cancellation.Token); }
            finally { Console.CancelKeyPress -= cancel; }
        }
        if (args[0] == "goals")
        {
            if (args[1] != "evaluate") throw new InvalidDataException("未知goals子命令。");
            Require(values, ["--goal", "--outcomes", "--output"]);
            var summary = GoalEvaluation.Run(values["--goal"], values["--outcomes"], values["--output"]);
            Console.WriteLine($"评价完成：{summary.SuccessCount}/{summary.SampleCount} 条轨迹同时达标。");
            if (summary.SuccessCount == 0) Console.WriteLine("本次输入未找到达标结果；条件效用未定义。");
            return 0;
        }
        if (args[1] == "handoff")
        {
            Require(values, ["--input", "--output"]);
            var report = HandoffAudit.Run(values["--input"], values["--output"]);
            Console.WriteLine($"交接包核验：{report.Runs.Count}局，{report.Runs.Sum(x => x.Frames)}个源动作帧，{report.Runs.Sum(x => x.SameTurnActions)}个同回合转移。");
            Console.WriteLine("已保留原始帧顺序和回合标签；派生增量自洽不代表规则验证或完整育成。");
            return 3;
        }
        if (args[1] == "audit")
        {
            Require(values, ["--packets", "--master", "--output"]);
            var report = EvidenceAudit.Run(values["--packets"], values["--master"], values["--output"]);
            Console.WriteLine($"审计完成：{report.Sources.Count - 1}个包库，{report.Packets.Packets}条记录，{report.Packets.Observations}条Pioneer观测，{report.Master.Tables.Count}张master表。");
            Console.WriteLine($"配对{report.Packets.PairedExchanges}组；孤包{report.Packets.OrphanExchanges}组；冲突{report.Packets.ConflictingExchanges}组；重复捕获{report.Packets.DuplicateCaptures}条。");
            Console.WriteLine("验证受阻：" + string.Join("、", report.Validation.Blockers));
            Console.WriteLine("报告已写入指定输出目录；未证明规则完整性或版本匹配。");
            return 3;
        }
        if (args[1] == "training")
        {
            Require(values, ["--input", "--output"]);
            var report = TrainingPreviewAudit.Run(values["--input"], values["--output"]);
            Console.WriteLine($"训练预览结算核验：{report.TrainingCommandResponses}条指令响应，{report.EligibleTransitions}个适用转移，{report.ExactMatches}个完整匹配，{report.VitalUpperClips}个体力上限截断。");
            Console.WriteLine("范围：给定预览的局部结算；不包含收益生成、失败、事件或回合推进。");
            return report.Status == "ObservedTransitionsMatched" ? 0 : 3;
        }
        if (args[1] == "point-gain")
        {
            Require(values, ["--input", "--master", "--output"]);
            var report = PointGainAudit.Run(values["--input"], values["--master"], values["--output"]);
            Console.WriteLine($"发展 Pt 预览核验：{report.Previews}项，{report.ExactMatches}项匹配。");
            Console.WriteLine("范围：普通训练的人数、友情资格和评审加成；不包含人员分布、岛训练、比赛或整局结算。");
            return report.Status == "ObservedPreviewsMatched" ? 0 : 3;
        }
        if (args[1] == "training-formula")
        {
            Require(values, ["--input", "--master", "--output"]);
            var report = TrainingFormulaAudit.Run(values["--input"], values["--master"], values["--output"]);
            Console.WriteLine($"训练公式核验：收益={report.GainStatus}，失败率预览={report.FailurePreviewStatus}。");
            Console.WriteLine("范围：收益候选与失败率显示公式；未验证失败结果分布、失败后果或全部支援效果。");
            return 3;
        }
        if (args[1] == "development-components")
        {
            Require(values, ["--input", "--research", "--output"]);
            var report = DevelopmentComponentAudit.Run(values["--input"], values["--research"], values["--output"]);
            Console.WriteLine($"发展 Pt 分量核验：比赛 {report.RaceMatches}/{report.RaceSelections}，Tucker 事件 {report.TuckerMatches}/{report.TuckerSelections}。");
            Console.WriteLine("范围：两份研究一致分类的比赛与明确事件选择；不生成比赛结果或事件调度。");
            return 3;
        }
        if (args[1] == "race-points")
        {
            Require(values, ["--input", "--study", "--master", "--output"]);
            var report = DevelopmentComponentAudit.RunRaceStudy(values["--input"], values["--study"], values["--master"], values["--output"]);
            Console.WriteLine($"比赛发展 Pt 核验：{report.MatchedWindows}/{report.LocatedWindows}；{report.Issues.Count} 个问题。");
            Console.WriteLine("范围：明确比赛结果之后的独立Pt事件；不生成名次、粉丝或重试。");
            return 3;
        }
        if (args[1] == "terminal")
        {
            Require(values, ["--input", "--output"]);
            var report = TerminalAudit.Run(values["--input"], values["--output"]);
            Console.WriteLine($"终局对象核验：{report.Counts.GetValueOrDefault("MatchedFinishResults")} 个唯一结果；{report.Issues.Count} 个输入问题。");
            Console.WriteLine("范围：已发生 finish 的对象与属性一致性；不生成终局奖励、因子或未来累计量。");
            return 3;
        }
        if (args[1] == "hint-pool")
        {
            Require(values, ["--research", "--master", "--output"]);
            var report = HintPoolAudit.Run(values["--research"], values["--master"], values["--output"]);
            Console.WriteLine($"Hint 有效奖池核验：{report.Counts.GetValueOrDefault("VerifiedRewards")}/{report.Counts.GetValueOrDefault("LocatedRewards")}；{report.Issues.Count} 个问题。");
            Console.WriteLine("范围：冻结研究定位与原始导出中的有效奖池和属性奖励；不生成Hint概率或抽签时点。");
            return 3;
        }
        if (args[1] == "island-action")
        {
            Require(values, ["--input", "--frames", "--master", "--output"]);
            var report = IslandActionAudit.Run(values["--input"], values["--frames"], values["--master"], values["--output"]);
            Console.WriteLine($"岛训练行动分量核验：{report.RuleVersion}；{report.Issues.Count} 个问题。");
            Console.WriteLine("范围：成功分支的立即结算；不推进后续事件或回合，不生成Hint抽签。");
            return 3;
        }
        if (args[1] == "event-components")
        {
            Require(values, ["--input", "--fixtures", "--output"]);
            var report = PioneerEventComponentAudit.Run(values["--input"], values["--fixtures"], values["--output"]);
            Console.WriteLine($"Tucker／项目成员／评审确认分量核验：{report.Issues.Count} 个问题。");
            Console.WriteLine("范围：明确事件与已识别分支；不生成事件机会或随机成员身份。");
            return 3;
        }
        if (args[1] == "training-modes")
        {
            Require(values, ["--input", "--master", "--output"]);
            var report = TrainingModesAudit.Run(values["--input"], values["--master"], values["--output"]);
            Console.WriteLine($"三模式训练候选核验：{report.CandidateVersion}；{report.Issues.Count} 个输入问题。");
            Console.WriteLine("范围：普通、合宿和岛训练的可见收益；候选拟合一致不代表服务端公式唯一或随机机制通过。");
            return 3;
        }
        if (args[1] == "initial-state")
        {
            Require(values, ["--study", "--input", "--packets", "--master", "--output"]);
            var report = InitialStateAudit.Run(values["--study"], values["--input"], values["--packets"], values["--master"], values["--output"]);
            Console.WriteLine($"开局分量核验：{report.Status}；{report.Issues.Count} 个输入问题。");
            Console.WriteLine("范围：完整亲本扩展映射及已列开局分量；未支持的非空扩展、才能及独特效果明确停止。");
            return 3;
        }
        if (args[1] == "placement-hint")
        {
            Require(values, ["--input", "--master", "--output"]);
            var report = PlacementHintAudit.Run(values["--input"], values["--master"], values["--output"]);
            Console.WriteLine($"人员与 Hint 机会统计：{report.Status}；报告保留条件分层及未解释的动态支援效果。");
            Console.WriteLine("范围：机会与观察频数；尚未认证随机生成模型。");
            return 3;
        }
        if (args[1] == "race-route")
        {
            Require(values, ["--input", "--master", "--output"]);
            var report = RaceRouteAudit.Run(values["--input"], values["--master"], values["--output"]);
            Console.WriteLine($"比赛路线映射：{report.Counts.GetValueOrDefault("MappedRouteStates")} 个路线状态；{report.Issues.Count} 个输入问题。");
            Console.WriteLine("范围：已选目标与 master 的对应；条件路线选择、参赛条件和比赛结果尚未实现。");
            return 3;
        }
        if (args[1] == "checkpoint")
        {
            Require(values, ["--input", "--output"]);
            var report = CheckpointAudit.Run(values["--input"], values["--output"]);
            Console.WriteLine($"评审核验：{report.MatchedSubmissions}个关联提交，{report.MatchedEventSelections}个明确事件选择，{report.CandidateResourceMatches}个 Pt／体力分量匹配。");
            Console.WriteLine("范围：评审提交与事件资源分量；完整奖励和阶段推进尚未实现。");
            return 3;
        }
        if (args[1] == "scoring")
        {
            Require(values, ["--packets", "--master", "--output"]);
            var report = ScoringAudit.Run(values["--packets"], values["--master"], values["--output"]);
            Console.WriteLine($"评分候选审计完成：{report.ResponsePackets}条响应，{report.UniqueInputScorePairs}个去重输入与评分组合；已计算{report.EvaluatedStates}个，完全匹配{report.ExactMatches}个。");
            Console.WriteLine($"评分字段不完整{report.IncompleteObservations}条；完整输入未计算{report.UnscoredStates}个；同输入不同评分{report.ConflictingInputScores}组。");
            Console.WriteLine("验证受阻：" + string.Join("、", report.Validation.Blockers));
            return 3;
        }
        if (args[1] == "construction")
        {
            Require(values, ["--input", "--output"]);
            var report = ConstructionAudit.Run(values["--input"], values["--output"]);
            Console.WriteLine($"建设核验完成：{report.PlanObservations}条计划观测，{report.ImmediatePointMismatches}条即时建设反例。");
            Console.WriteLine($"局部结算{report.SettlementObservations}条，阈值候选匹配{report.ReachedThresholdMatches}条，明确请求选择匹配{report.MatchedEventSelections}条。");
            Console.WriteLine("完整模拟尚未完成：" + string.Join("、", report.Validation.Blockers));
            return 3;
        }
        if (args[1] == "validate")
        {
            Require(values, ["--report"]);
            var report = JsonSerializer.Deserialize<EvidenceReport>(File.ReadAllText(values["--report"]), EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("报告为空。");
            if (report.SchemaVersion != "1") throw new InvalidDataException("不支持该证据报告版本。");
            // Local master and packet versions remain provenance metadata, not a research prerequisite.
            if (report.Validation.Status != "Passed" || report.Validation.Blockers.Count != 0)
            {
                Console.WriteLine("验证受阻：" + string.Join("、", report.Validation.Blockers));
                return 3;
            }
            throw new InvalidDataException("当前工具只生成证据清单，不能认证手工改写的Passed报告。");
        }
        throw new InvalidDataException("未知evidence子命令。");
    }

    static void Require(Dictionary<string, string> values, string[] required)
    {
        if (values.Count != required.Length || required.Any(key => !values.ContainsKey(key)))
            throw new InvalidDataException("参数缺失或包含未知项；使用--help查看用法。");
    }
}
