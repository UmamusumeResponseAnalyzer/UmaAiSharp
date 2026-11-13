using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UmaAiSharp.MCP.Evidence;

namespace UmaAiSharp.MCP;

[McpServerToolType]
public static class EvidenceTools
{
    [McpServerTool(Name = "evidence_audit", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("清点抓包与 master，生成配对交换和标准化 Pioneer 观测。")]
    public static ToolResult Audit(string packetsPath, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = EvidenceAudit.Run(packetsPath, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.Packets.Packets, report.Packets.Observations, report.Packets.PairedExchanges },
            ToolResult.Paths(outputDirectory, ("report", "audit.json"), ("masterRows", "master-rows.jsonl"), ("exchanges", "exchanges.jsonl"),
                ("observations", "pioneer-observations.jsonl"), ("actions", "pioneer-actions.jsonl")));
    }

    [McpServerTool(Name = "evidence_scoring", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("以实包和 master 核查属性评分候选。")]
    public static ToolResult Scoring(string packetsPath, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = ScoringAudit.Run(packetsPath, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.EvaluatedStates, report.ExactMatches, report.UnscoredStates },
            ToolResult.Paths(outputDirectory, ("report", "scoring-audit.json"), ("observations", "scoring-observations.jsonl")));
    }

    [McpServerTool(Name = "evidence_training", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查已给定训练预览的局部结算。")]
    public static ToolResult Training(string evidenceDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = TrainingPreviewAudit.Run(evidenceDirectory, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Exclusions.Count, report.EligibleTransitions, report.ExactMatches, report.VitalUpperClips },
            ToolResult.Paths(outputDirectory, ("report", "training-audit.json"), ("transitions", "training-transitions.jsonl")));
    }

    [McpServerTool(Name = "evidence_terminal", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核对已发生的 finish 对象与可观察终局指标。")]
    public static ToolResult Terminal(string evidenceDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = TerminalAudit.Run(evidenceDirectory, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "terminal-audit.json"), ("observations", "terminal-observations.jsonl")));
    }

    [McpServerTool(Name = "evidence_checkpoint", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查评审提交、资源结算和进度分量。")]
    public static ToolResult Checkpoint(string evidenceDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = CheckpointAudit.Run(evidenceDirectory, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.MatchedSubmissions, report.MatchedEventSelections, report.CandidateResourceMatches },
            ToolResult.Paths(outputDirectory, ("report", "checkpoint-audit.json"), ("submissions", "checkpoint-submissions.jsonl"),
                ("settlements", "checkpoint-settlements.jsonl"), ("progress", "checkpoint-progress.jsonl")));
    }

    [McpServerTool(Name = "evidence_construction", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查建设计划、触发阈值、设施与发券结算。")]
    public static ToolResult Construction(string evidenceDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = ConstructionAudit.Run(evidenceDirectory, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.PlanObservations, report.ImmediatePointMismatches, report.SettlementObservations },
            ToolResult.Paths(outputDirectory, ("report", "construction-audit.json"), ("observations", "construction-observations.jsonl"),
                ("settlements", "construction-settlements.jsonl"), ("triggers", "construction-triggers.jsonl"),
                ("componentChanges", "construction-component-changes.jsonl")));
    }

    [McpServerTool(Name = "evidence_training_formula", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查训练收益及面板失败率公式候选。")]
    public static ToolResult TrainingFormula(string evidenceDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = TrainingFormulaAudit.Run(evidenceDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = new { gains = report.GainStatus, failurePreview = report.FailurePreviewStatus }, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "training-formula-audit.json"), ("samples", "training-formula-samples.jsonl"),
                ("counterexamples", "training-formula-counterexamples.jsonl")));
    }

    [McpServerTool(Name = "evidence_training_modes", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按普通、合宿、岛训练复算可见收益。")]
    public static ToolResult TrainingModes(string evidenceDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = TrainingModesAudit.Run(evidenceDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = "CandidateOnly", issueCount = report.Issues.Count, report.Counts, report.Limits },
            ToolResult.Paths(outputDirectory, ("report", "training-modes-audit.json"), ("samples", "training-modes-samples.jsonl"),
                ("counterexamples", "training-modes-counterexamples.jsonl")));
    }

    [McpServerTool(Name = "evidence_point_gain", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查普通训练发展 Pt 预览。")]
    public static ToolResult PointGain(string evidenceDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = PointGainAudit.Run(evidenceDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Exclusions.Count, report.Previews, report.ExactMatches },
            ToolResult.Paths(outputDirectory, ("report", "point-gain-audit.json"), ("previews", "point-gain-previews.jsonl")));
    }

    [McpServerTool(Name = "evidence_placement_hint", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("统计人员分布与 Hint 的观察机会和频数。")]
    public static ToolResult PlacementHint(string evidenceDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = PlacementHintAudit.Run(evidenceDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "placement-hint-audit.json"), ("opportunities", "placement-opportunities.jsonl")));
    }

    [McpServerTool(Name = "evidence_race_route", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查已选育成比赛目标与 master 路线对应关系。")]
    public static ToolResult RaceRoute(string evidenceDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = RaceRouteAudit.Run(evidenceDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "race-route-audit.json"), ("observations", "race-route-observations.jsonl")));
    }

    [McpServerTool(Name = "evidence_handoff", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核对交接包内的源动作帧、同回合转移和资源增量。")]
    public static ToolResult Handoff(string handoffDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = HandoffAudit.Run(handoffDirectory, outputDirectory, cancellationToken);
        return new("completed", new { validation = "ObservedOnly", runCount = report.Runs.Count, frames = report.Runs.Sum(run => run.Frames), report.Limitations },
            ToolResult.Paths(outputDirectory, ("report", "handoff-audit.json"), ("frames", "handoff-frames.jsonl")));
    }

    [McpServerTool(Name = "evidence_development_components", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查比赛与 Tucker 事件的独立发展 Pt 分量。")]
    public static ToolResult DevelopmentComponents(string evidenceDirectory, string researchDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = DevelopmentComponentAudit.Run(evidenceDirectory, researchDirectory, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, report.RaceMatches, report.RaceSelections, report.TuckerMatches, report.TuckerSelections },
            ToolResult.Paths(outputDirectory, ("report", "development-component-audit.json"), ("raceComponents", "race-point-components.jsonl"),
                ("tuckerComponents", "tucker-point-components.jsonl")));
    }

    [McpServerTool(Name = "evidence_race_points", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按比赛研究定位复算发展 Pt 事件。")]
    public static ToolResult RacePoints(string evidenceDirectory, string studyDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = DevelopmentComponentAudit.RunRaceStudy(evidenceDirectory, studyDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.LocatedWindows, report.CheckedWindows, report.MatchedWindows },
            ToolResult.Paths(outputDirectory, ("report", "race-point-study-audit.json"), ("cases", "race-point-study-cases.jsonl")));
    }

    [McpServerTool(Name = "evidence_hint_pool", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查 Hint 有效奖池和已知属性奖励。")]
    public static ToolResult HintPool(string researchDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = HintPoolAudit.Run(researchDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = "CandidateOnly", issueCount = report.Issues.Count, report.Counts, report.Limitations },
            ToolResult.Paths(outputDirectory, ("report", "hint-pool-audit.json"), ("samples", "hint-pool-samples.jsonl")));
    }

    [McpServerTool(Name = "evidence_island_action", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查岛训练即时结算、扣券、羁绊和效果原子。")]
    public static ToolResult IslandAction(string researchDirectory, string framesPath, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = IslandActionAudit.Run(researchDirectory, framesPath, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = "CandidateOnly", issueCount = report.Issues.Count, report.Counts, report.Limits },
            ToolResult.Paths(outputDirectory, ("report", "island-action-audit.json"), ("actions", "island-actions.jsonl"),
                ("counterexamples", "island-action-counterexamples.jsonl"), ("eventGrants", "island-event-grants.jsonl")));
    }

    [McpServerTool(Name = "evidence_event_components", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("核查 Tucker、PJ 成员与评审确认的已知事件分量。")]
    public static ToolResult EventComponents(string evidenceDirectory, string fixturesDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = PioneerEventComponentAudit.Run(evidenceDirectory, fixturesDirectory, outputDirectory, cancellationToken);
        return new("completed", new { report.Validation, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "pioneer-event-component-audit.json"), ("cases", "event-component-cases.jsonl")));
    }

    [McpServerTool(Name = "evidence_initial_state", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("以开局研究定位重新读取实包，核查亲本与开局状态分量。")]
    public static ToolResult InitialState(string studyDirectory, string evidenceDirectory, string captureDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = InitialStateAudit.Run(studyDirectory, evidenceDirectory, captureDirectory, masterPath, outputDirectory, cancellationToken);
        return new("completed", new { validation = report.Status, issueCount = report.Issues.Count, report.Counts },
            ToolResult.Paths(outputDirectory, ("report", "initial-state-audit.json"), ("samples", "initial-state-samples.jsonl")));
    }

    [McpServerTool(Name = "evidence_validate", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("读取证据清单的核验状态和阻塞项。清单无法认证手工改写的通过状态。")]
    public static ToolResult Validate(string reportPath)
    {
        var report = JsonSerializer.Deserialize<EvidenceReport>(File.ReadAllText(reportPath), EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("报告为空。");
        if (report.SchemaVersion != "1") throw new InvalidDataException("不支持该证据报告版本。");
        if (report.Validation.Status == "Passed" && report.Validation.Blockers.Count == 0)
            throw new InvalidDataException("当前证据清单不能认证手工改写的 Passed 报告。");
        return ToolResult.Report(reportPath, new { report.Validation, issueCount = report.Issues.Count });
    }
}
