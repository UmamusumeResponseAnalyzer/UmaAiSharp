using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Base.Inheritance;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP;

public enum PioneerExecutionMode { Autonomous, Conditional }
public sealed record PioneerPayload(string Kind, JsonElement Data);
public sealed record PioneerExecutionInput(string RulesIdentity, PioneerExecutionMode Mode,
    PioneerPayload Start, ImmutableArray<PioneerPayload> Outcomes,
    ImmutableArray<ScriptedAction<PioneerRuntimeAction>> Script,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PioneerPlacementSeed? Placement = null);
public sealed record LoadedPioneerExecution(PioneerRuntimeData Data, PioneerState Root,
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script, PioneerExecutionMode Mode, string InputSha256, string MasterSha256);

public static class PioneerExecution
{
    public static JsonSerializerOptions JsonOptions => PioneerGameJson.Options;

    public static ToolResult Run(string inputPath, ulong seed, string outputPath, string rulesPath,
        PioneerExecutionMode? requiredMode = null, int? samples = null, int workers = 1,
        CancellationToken cancellationToken = default, string? goalPath = null, PioneerModel? model = null)
    {
        var sampleCount = samples ?? 1;
        ToolArguments.ValidateBatch(sampleCount, workers);
        outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath)) throw new InvalidDataException("执行报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        var compileStart = Stopwatch.GetTimestamp();
        LoadedPioneerExecution loaded;
        try { loaded = Load(inputPath, rulesPath, model); }
        catch (UnknownMechanismException error)
        {
            return WriteInitializationStop(inputPath, outputPath, error);
        }
        var compileTime = Stopwatch.GetElapsedTime(compileStart);
        if (requiredMode is { } mode && loaded.Mode != mode)
            throw new InvalidDataException(mode == PioneerExecutionMode.Autonomous
                ? "自主执行要求 autonomous 模式。"
                : "条件回放要求 conditional 模式。");
        var autonomous = loaded.Mode == PioneerExecutionMode.Autonomous;
        if (autonomous && goalPath is null) throw new InvalidDataException("自主回合续步需要 goalPath。");
        if (goalPath is not null && JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(goalPath), GoalJson.Options) is null)
            throw new InvalidDataException("育成目标为空。");
        var scenario = new PioneerScenario(loaded.Data, autonomous);
        ITrajectoryPolicy<PioneerDecisionView, PioneerRuntimeAction, ScriptCursor> policy = autonomous
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
            mode = loaded.Mode, scope = "PioneerExecution", initialStage = loaded.Data.InitialStage,
            loaded.InputSha256, loaded.MasterSha256, batch.Provenance, batch.Memory,
            sampleCount, workers, completed, careerFailures, stopped = sampleCount - completed,
            compileMilliseconds = compileTime.TotalMilliseconds, executionMilliseconds = elapsed.TotalMilliseconds,
            processAllocatedBytes = allocated, avx512Available = ExecutionPlatform.HasAvx512,
            results = rows, finalState, loaded.Data.IgnoredMechanisms,
            execution = new { outcomeSource = loaded.Mode.ToString(),
                timingScope = "executionAndScheduling" }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = new FileStream(outputPath, FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, JsonOptions);
        var status = completed == sampleCount ? "completed"
            : batch.Results.Any(result => result.Stop?.Reason == SimulationStopReason.Cancelled) ? "cancelled" : "stopped";
        var stops = ToolResult.Stops(batch.Results);
        return ToolResult.Report(outputPath, new { loaded.Mode, sampleCount, completed, careerFailures, stopped = sampleCount - completed, stops, loaded.Data.IgnoredMechanisms }, status);
    }

    internal static ToolResult WriteInitializationStop(string input, string output, UnknownMechanismException error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.CreateNew);
        var summary = new { phase = "initialization", reason = "unknownMechanism", detail = error.Message };
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        JsonSerializer.Serialize(stream, new { summary.phase, status = "stopped",
            summary.reason, inputSha256 = Convert.ToHexStringLower(SHA256.HashData(source)), summary.detail }, JsonOptions);
        return ToolResult.Report(output, summary, "stopped");
    }

    public static LoadedPioneerExecution Load(string path, string rulesPath, PioneerModel? model = null)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        RejectNullArrayElements(document.RootElement, "$");
        var input = document.RootElement.Deserialize<PioneerExecutionInput>(JsonOptions) ?? throw new InvalidDataException("Pioneer输入为空。");
        if (string.IsNullOrWhiteSpace(input.RulesIdentity) || input.Outcomes.IsDefault || input.Script.IsDefault)
            throw new InvalidDataException("Pioneer 输入要求规则身份、outcomes 和 script 数组。");
        PioneerStart start = input.Start.Kind switch
        {
            "characterEnding" => Read<PioneerStart.CharacterEnding>(input.Start),
            "reporterEnding" => Read<PioneerStart.ReporterEnding>(input.Start),
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
        var autonomous = input.Mode == PioneerExecutionMode.Autonomous;
        if (autonomous && (!input.Outcomes.IsEmpty || !input.Script.IsEmpty))
            throw new InvalidDataException("自主执行要求 outcomes 和 script 为空。带观测结果或动作脚本时使用 conditional 模式。");
        var outcomes = input.Outcomes.Select(Outcome).ToImmutableArray();
        var (rules, identity) = PioneerRulesSnapshot.ReadArtifact(rulesPath);
        if (identity != input.RulesIdentity) throw new InvalidDataException("输入与 rulesPath 的规则 SHA-256 不匹配，请重新导入。");
        var training = start as PioneerStart.TrainingTurn ?? (start as PioneerStart.Planning)?.Continuation;
        var checkpoints = training is not null || start is PioneerStart.Checkpoint ? rules.CompileCheckpoints() : null;
        PioneerTurnRules? turnRules = null;
        if (training is not null)
        {
            var observedHints = outcomes.OfType<PioneerConditionalOutcome.ObservedEventResult>()
                .SelectMany(outcome => outcome.Changes?.Hints ?? []).Select(hint => hint.Skill);
            turnRules = rules.CompileTurnRules(training, input.Placement ?? throw new InvalidDataException("训练起点缺少人员登场状态。"),
                observedHints);
        }
        var estimatedHistory = autonomous ? PioneerAutonomousSession.EstimateTrainingHistory(start, rules) : null;
        var data = PioneerRuntimeData.Compile(start, outcomes, identity, rules.Construction, checkpoints, turnRules,
            trainingHistory: estimatedHistory, model: model, raceOnlyCharacterEffects: rules.RaceOnlyCharacterEffects, forecast: autonomous);
        return new(data, data.CreateInitialState(), new(input.Script, input.Script.Sum(step => step.Action.OwnedPayloadBytes)), input.Mode,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), rules.SourceMasterSha256);
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
                return new PioneerConditionalOutcome.TurnArrival(arrival.ActionOrdinal,
                    new(arrival.Arrival, arrival.Board, arrival.AvailableTrainingCommands, arrival.AvailableRestCommands,
                        arrival.Event, arrival.IslandBoard, arrival.Turn, arrival.RawPlayingState, arrival.ChoiceCount));
            case "eventRewardResult":
                var reward = Read<EventRewardResultInput>(payload);
                return new PioneerConditionalOutcome.EventRewardResult(reward.ActionOrdinal, reward.ChoiceNumber, reward.BranchIndex);
            case "trainingResult":
                var result = Read<TrainingResultInput>(payload);
                return new PioneerConditionalOutcome.TrainingResult(result.ActionOrdinal, result.Success, result.Next);
            case "observedEventResult":
                var observed = Read<ObservedEventResultInput>(payload);
                return new PioneerConditionalOutcome.ObservedEventResult(observed.ActionOrdinal, observed.Event,
                    observed.ChoiceNumber, observed.Changes, observed.Next);
            case "targetRaceWin":
                var race = Read<TargetRaceWinInput>(payload);
                return new PioneerConditionalOutcome.TargetRaceWin(race.ActionOrdinal, race.ProgramId, race.FansGained,
                    race.Next, race.PostRaceChoice, race.ExcludedAttribute);
            case "trainingFailure":
                var failure = Read<FailureInput>(payload);
                return new PioneerConditionalOutcome.TrainingFailure(failure.ActionOrdinal, failure.Event);
            case "failurePenalty":
                var penalty = Read<PenaltyInput>(payload);
                return new PioneerConditionalOutcome.FailurePenalty(penalty.ActionOrdinal, penalty.Branch, penalty.Next);
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
        PioneerIslandBoard? IslandBoard = null, int? Turn = null, int? RawPlayingState = null, int? ChoiceCount = null);
    sealed record EventRewardResultInput(int ActionOrdinal, int ChoiceNumber, int BranchIndex);
    sealed record TrainingResultInput(int ActionOrdinal, bool Success, PioneerTurnObservation? Next = null);
    sealed record ObservedEventResultInput(int ActionOrdinal, PioneerEvent Event, int ChoiceNumber,
        PioneerObservedEventChanges? Changes, PioneerTurnObservation Next);
    sealed record TargetRaceWinInput(int ActionOrdinal, int ProgramId, int FansGained, PioneerTurnObservation Next,
        int PostRaceChoice = 0, TrainingAttribute? ExcludedAttribute = null);
    sealed record PenaltyInput(int ActionOrdinal, UmaAiSharp.Pioneer.Training.ResolvedFailureBranch Branch, PioneerTurnObservation? Next = null);
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
        hintLevels = state.ReadHintLevels(),
        sections = Enum.GetValues<PioneerStateSection>().ToDictionary(section => section.ToString(), section => state.GetSection(section).ToArray())
    };
}
