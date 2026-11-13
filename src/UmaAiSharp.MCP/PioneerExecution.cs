using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP;

public enum PioneerExecutionMode { Autonomous, Conditional }
public sealed record PioneerPayload(string Kind, JsonElement Data);
public sealed record PioneerExecutionInput(int SchemaVersion, string RulesIdentity, PioneerExecutionMode Mode,
    PioneerPayload Start, ImmutableArray<PioneerPayload> Outcomes,
    ImmutableArray<ScriptedAction<PioneerRuntimeAction>> Script,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PioneerPlacementSeed? Placement = null);
public sealed record LoadedPioneerExecution(PioneerRuntimeData Data, PioneerState Root,
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script, PioneerExecutionMode Mode, string InputSha256, string? MasterSha256,
    bool UseContinuation);

public static class PioneerExecution
{
    public static JsonSerializerOptions JsonOptions => PioneerGameJson.Options;

    public static ToolResult Run(string inputPath, ulong seed, string outputPath, PioneerExecutionMode? requiredMode = null,
        int? samples = null, int workers = 1, string? masterPath = null, CancellationToken cancellationToken = default,
        string? rulesPath = null, string? goalPath = null, PioneerModel? model = null)
    {
        var sampleCount = samples ?? 1;
        ToolArguments.ValidateBatch(sampleCount, workers);
        outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath)) throw new InvalidDataException("执行报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        var compileStart = Stopwatch.GetTimestamp();
        LoadedPioneerExecution loaded;
        try { loaded = Load(inputPath, masterPath, rulesPath, model); }
        catch (UnknownMechanismException error)
        {
            return WriteInitializationStop(inputPath, outputPath, error);
        }
        var compileTime = Stopwatch.GetElapsedTime(compileStart);
        if (requiredMode is { } mode && loaded.Mode != mode)
            throw new InvalidDataException(mode == PioneerExecutionMode.Autonomous
                ? "自主执行要求 autonomous 模式。"
                : "条件回放要求 conditional 模式。");
        if (loaded.UseContinuation && goalPath is null) throw new InvalidDataException("自主回合续步需要 goalPath。");
        if (goalPath is not null && JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(goalPath), GoalJson.Options) is null)
            throw new InvalidDataException("育成目标为空。");
        var scenario = new PioneerScenario(loaded.Data, loaded.UseContinuation);
        ITrajectoryPolicy<PioneerDecisionView, PioneerRuntimeAction, ScriptCursor> policy = loaded.UseContinuation
            ? new PioneerContinuationPolicy() : loaded.Script;
        SimulationBatch<TerminalMetrics> batch;
        object? finalState = null;
        var beforeAllocation = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        if (samples.HasValue)
            batch = SimulationRunner.RunBatch(scenario, policy, loaded.Root, seed, sampleCount, workers, cancellationToken);
        else
        {
            var execution = SimulationRunner.CreateExecution(scenario, policy, loaded.Root, seed, 0);
            var result = SimulationRunner.Continue(scenario, policy, ref execution, cancellationToken);
            batch = new(new(scenario.RulesIdentity, seed, SimulationRandom.Algorithm, SimulationRandom.SeedDerivation),
                [result], SimulationRunner.GetMemoryPlan(scenario, policy, execution.State, execution.Cursor));
            finalState = Snapshot(execution.State);
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetTotalAllocatedBytes(true) - beforeAllocation;
        var rows = batch.Results.Select(result => new
        {
            result.SampleIndex, result.Status, result.Progress, result.RandomState, result.Stop,
            outcome = result.Status == TrajectoryStatus.Completed ? (TerminalMetrics?)result.Outcome : null
        });
        var completed = batch.Results.Count(result => result.Status == TrajectoryStatus.Completed);
        var careerFailures = batch.Results.Count(result => result.Status == TrajectoryStatus.Completed
            && result.Outcome.FailedCareerGoalId.HasValue);
        var report = new
        {
            schemaVersion = 1, mode = loaded.Mode, scope = "PioneerExecution", initialStage = loaded.Data.InitialStage,
            loaded.InputSha256, loaded.MasterSha256, batch.Provenance, batch.Memory,
            sampleCount, workers, completed, careerFailures, stopped = sampleCount - completed,
            compileMilliseconds = compileTime.TotalMilliseconds, executionMilliseconds = elapsed.TotalMilliseconds,
            processAllocatedBytes = allocated, avx512Available = ExecutionPlatform.HasAvx512,
            results = rows, finalState,
            execution = new { outcomeSource = loaded.Mode.ToString(),
                timingScope = "executionAndScheduling" }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = new FileStream(outputPath, FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, JsonOptions);
        var status = completed == sampleCount ? "completed"
            : batch.Results.Any(result => result.Stop?.Reason == SimulationStopReason.Cancelled) ? "cancelled" : "stopped";
        var stops = batch.Results.Where(result => result.Stop.HasValue)
            .GroupBy(result => (result.Stop!.Value.Reason, result.Stop.Value.Code))
            .Select(group => new { group.Key.Reason, group.Key.Code, count = group.Count(), firstPosition = group.First().Progress.Position })
            .ToArray();
        return ToolResult.Report(outputPath, new { loaded.Mode, sampleCount, completed, careerFailures, stopped = sampleCount - completed, stops }, status);
    }

    internal static ToolResult WriteInitializationStop(string input, string output, UnknownMechanismException error, string? mode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.CreateNew);
        var summary = new { mode, phase = "initialization", reason = "unknownMechanism", detail = error.Message };
        if (mode is null)
            JsonSerializer.Serialize(stream, new { schemaVersion = 1, summary.phase, status = "stopped",
                summary.reason, inputSha256 = Fingerprint(input), summary.detail }, JsonOptions);
        else
            JsonSerializer.Serialize(stream, new { schemaVersion = 2, mode, summary.phase, status = "stopped",
                summary.reason, summary.detail }, JsonOptions);
        return ToolResult.Report(output, summary, "stopped");
    }

    internal static string Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static LoadedPioneerExecution Load(string path, string? masterPath = null, string? rulesPath = null, PioneerModel? model = null)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        RejectNullArrayElements(document.RootElement, "$");
        var input = document.RootElement.Deserialize<PioneerExecutionInput>(JsonOptions) ?? throw new InvalidDataException("Pioneer输入为空。");
        if (input.SchemaVersion is not (1 or 2) || string.IsNullOrWhiteSpace(input.RulesIdentity) || input.Outcomes.IsDefault || input.Script.IsDefault)
            throw new InvalidDataException("Pioneer输入要求schemaVersion=1或2、明确规则身份、outcomes和script数组。");
        if (input.SchemaVersion == 2 ? rulesPath is null || masterPath is not null : rulesPath is not null)
            throw new InvalidDataException("版本2输入要求同一份 rulesPath，版本1组件输入使用自身规则或 masterPath。");
        PioneerStart start = input.Start.Kind switch
        {
            "characterEnding" => Read<PioneerStart.CharacterEnding>(input.Start),
            "declaredEvent" => Read<PioneerStart.DeclaredEvent>(input.Start),
            "trainingTurn" => Read<PioneerStart.TrainingTurn>(input.Start),
            "failureTraining" => Read<PioneerStart.FailureTraining>(input.Start),
            "islandTraining" => Read<PioneerStart.IslandTraining>(input.Start),
            "construction" => Read<PioneerStart.Construction>(input.Start),
            "planning" => Read<PioneerStart.Planning>(input.Start),
            "hintAttribute" => Read<PioneerStart.HintAttribute>(input.Start),
            "tuckerFriend" => Read<PioneerStart.TuckerFriend>(input.Start),
            "tuckerPoints" => Read<PioneerStart.TuckerPoints>(input.Start),
            "tuckerSpecialty" => Read<PioneerStart.TuckerSpecialty>(input.Start),
            "projectMembers" => Read<PioneerStart.ProjectMembers>(input.Start),
            "checkpoint" => Read<PioneerStart.Checkpoint>(input.Start),
            "checkpointDisplay" => Read<PioneerStart.CheckpointDisplay>(input.Start),
            _ => throw new InvalidDataException($"未支持的Pioneer起点种类：{input.Start.Kind}。")
        };
        if (input.Mode == PioneerExecutionMode.Autonomous && input.Outcomes.Length != 0)
            throw new InvalidDataException("autonomous模式的outcomes须为空；提供条件结果时使用conditional模式。");
        var outcomes = input.Outcomes.Select(Outcome).ToImmutableArray();
        PioneerMasterData? construction = null;
        CheckpointMasterData? checkpoints = null;
        string? masterSha = null;
        PioneerTurnRules? turnRules = null;
        PioneerTrainingHistoryDistribution? estimatedHistory = null;
        if (rulesPath is not null)
        {
            var (rules, identity) = PioneerRulesSnapshot.ReadArtifact(rulesPath);
            if (identity != input.RulesIdentity) throw new InvalidDataException("输入与 rulesPath 的规则 SHA-256 不匹配。");
            construction = rules.Construction;
            masterSha = rules.SourceMasterSha256;
            var training = start as PioneerStart.TrainingTurn ?? (start as PioneerStart.Planning)?.Continuation;
            if (training is not null)
            {
                if (input.Mode == PioneerExecutionMode.Autonomous && training.Turn < 55 && training.Inheritance is null)
                    throw new UnknownMechanismException($"第 {training.Turn} 回合缺少本局开局冻结的亲本因子与胜鞍，请使用包含亲本与开局响应的抓包前缀导入。");
                checkpoints = rules.CompileCheckpoints();
                turnRules = rules.CompileTurnRules(training, input.Placement ?? throw new InvalidDataException("训练起点缺少人员登场状态。"));
                if (input.Mode == PioneerExecutionMode.Autonomous)
                    estimatedHistory = PioneerAutonomousSession.EstimateTrainingHistory(start, rules);
            }
            if (input.Mode == PioneerExecutionMode.Autonomous && !input.Script.IsEmpty)
                throw new InvalidDataException("版本2自主执行从生成状态选动作，script 必须为空。");
        }
        else if (start is PioneerStart.Construction or PioneerStart.Planning or PioneerStart.Checkpoint
            or PioneerStart.TrainingTurn { Development: not null })
        {
            if (masterPath is null) throw new InvalidDataException("建设、评审或带建设状态的训练起点必须提供 masterPath。");
            masterSha = Fingerprint(masterPath);
            using var db = EvidenceAudit.OpenReadOnly(masterPath);
            if (start is not PioneerStart.Checkpoint)
                construction = PioneerMasterReader.ConstructionData(db, masterSha);
            else
                checkpoints = new(masterSha,
                    PioneerMasterReader.Rows(db, "single_mode_11_planning").Select(row => new PlanningPeriod(row["id"],
                        row["deck_id"], row["check_point_type"], row["planning_turn"], row["check_point_turn"], row["target_pt"])),
                    PioneerMasterReader.Rows(db, "single_mode_11_pioneer_pt_bonus").Select(row => new CheckpointPointBonus(row["id"], row["check_point_type"], row["group_id"], row["pt_rate"], row["add_value_max"])),
                    PioneerMasterReader.Rows(db, "single_mode_11_check_point_eff").Select(row => new CheckpointEffect(row["id"], row["check_point_type"], row["result_state"], row["effect_type"], row["effect_value_1"], row["effect_value_2"], row["effect_value_3"], row["effect_value_4"])));
            if (masterSha != Fingerprint(masterPath)) throw new InvalidDataException("编译期间master发生变化。");
        }
        else if (masterPath is not null) throw new InvalidDataException("该起点已有明确规则输入，不接受未使用的 masterPath。");
        var data = PioneerRuntimeData.Compile(start, outcomes, input.RulesIdentity, construction, checkpoints, turnRules,
            usesStoryDecisions: rulesPath is not null, trainingHistory: estimatedHistory, model: model);
        return new(data, data.CreateInitialState(), new(input.Script, input.Script.Sum(step => step.Action.OwnedPayloadBytes)), input.Mode,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), masterSha, input.SchemaVersion == 2 && input.Mode == PioneerExecutionMode.Autonomous);
    }

    static PioneerConditionalOutcome Outcome(PioneerPayload payload)
    {
        switch (payload.Kind)
        {
            case "restEvent":
                var rest = Read<RestEventInput>(payload);
                return new PioneerConditionalOutcome.RestEvent(rest.ActionOrdinal, rest.Event);
            case "turnArrival":
                var arrival = Read<TurnArrivalInput>(payload);
                return new PioneerConditionalOutcome.TurnArrival(arrival.ActionOrdinal, arrival.Arrival, arrival.Board,
                    arrival.AvailableTrainingCommands, arrival.AvailableRestCommands, arrival.Event, arrival.IslandBoard);
            case "eventRewardResult":
                var reward = Read<EventRewardResultInput>(payload);
                return new PioneerConditionalOutcome.EventRewardResult(reward.ActionOrdinal, reward.ChoiceNumber, reward.BranchIndex);
            case "trainingResult":
                var result = Read<TrainingResultInput>(payload);
                return new PioneerConditionalOutcome.TrainingResult(result.ActionOrdinal, result.Success);
            case "trainingFailure":
                var failure = Read<FailureInput>(payload);
                return new PioneerConditionalOutcome.TrainingFailure(failure.ActionOrdinal, failure.Event);
            case "failurePenalty":
                var penalty = Read<PenaltyInput>(payload);
                return new PioneerConditionalOutcome.FailurePenalty(penalty.ActionOrdinal, penalty.Branch);
            case "hintAttributeGroup":
                var hint = Read<HintInput>(payload);
                return new PioneerConditionalOutcome.HintAttributeGroup(hint.ActionOrdinal, hint.GroupIndex);
            case "projectNpcSelection":
                var npc = Read<NpcInput>(payload);
                return new PioneerConditionalOutcome.ProjectNpcSelection(npc.ActionOrdinal, npc.NpcIds);
            default: throw new InvalidDataException($"未支持的条件结果种类：{payload.Kind}。");
        }
    }
    sealed record FailureInput(int ActionOrdinal, UmaAiSharp.Pioneer.Training.TrainingFailureEvent Event);
    sealed record RestEventInput(int ActionOrdinal, PioneerEvent Event);
    sealed record TurnArrivalInput(int ActionOrdinal, PioneerArrival Arrival, UmaAiSharp.Pioneer.Training.TrainingBoard? Board = null,
        int? AvailableTrainingCommands = null, int? AvailableRestCommands = null, PioneerEvent? Event = null,
        PioneerIslandBoard? IslandBoard = null);
    sealed record EventRewardResultInput(int ActionOrdinal, int ChoiceNumber, int BranchIndex);
    sealed record TrainingResultInput(int ActionOrdinal, bool Success);
    sealed record PenaltyInput(int ActionOrdinal, UmaAiSharp.Pioneer.Training.ResolvedFailureBranch Branch);
    sealed record HintInput(int ActionOrdinal, int GroupIndex);
    sealed record NpcInput(int ActionOrdinal, ImmutableArray<int> NpcIds);
    static T Read<T>(PioneerPayload payload) where T : class => payload.Data.Deserialize<T>(JsonOptions)
        ?? throw new InvalidDataException($"Pioneer {payload.Kind} 输入为空。");

    static void RejectNullArrayElements(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                RejectNullArrayElements(property.Value, path + "." + property.Name);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in value.EnumerateArray())
            {
                var elementPath = $"{path}[{index++}]";
                if (element.ValueKind == JsonValueKind.Null)
                    throw new JsonException($"Pioneer数组元素不能为null：{elementPath}。");
                RejectNullArrayElements(element, elementPath);
            }
        }
    }

    static object Snapshot(PioneerState state) => new
    {
        header = state.Header,
        sections = Enum.GetValues<PioneerStateSection>().ToDictionary(section => section.ToString(), section => state.GetSection(section).ToArray())
    };
}
