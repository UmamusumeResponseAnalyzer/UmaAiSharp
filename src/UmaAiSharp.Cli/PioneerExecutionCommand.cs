using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Cli;

public enum PioneerExecutionMode { Autonomous, Conditional }
public sealed record PioneerPayload(string Kind, JsonElement Data);
public sealed record PioneerExecutionInput(int SchemaVersion, string RulesIdentity, PioneerExecutionMode Mode,
    PioneerPayload Start, ImmutableArray<PioneerPayload> Outcomes,
    ImmutableArray<ScriptedAction<PioneerRuntimeAction>> Script);
public sealed record LoadedPioneerExecution(PioneerRuntimeData Data, PioneerState Root,
    ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script, PioneerExecutionMode Mode, string InputSha256, string? MasterSha256);

public static class PioneerExecutionCommand
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        IncludeFields = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                if (info.Kind == JsonTypeInfoKind.Object && info.Type.IsValueType
                    && info.Type.Namespace?.StartsWith("UmaAiSharp.", StringComparison.Ordinal) == true)
                    foreach (var property in info.Properties)
                        if (property.Set is not null && !(info.Type == typeof(PioneerRuntimeAction) && property.Name == "plan")) property.IsRequired = true;
            } }
        },
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false), new ConstructionPlanConverter() }
    };

    public static int Run(string command, Dictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        if (command == "model") return PioneerForecastCompiler.Build(values);
        if (command == "event-model") return PioneerEventModelCompiler.Run(values);
        if (command == "import") return PioneerImportCommand.Run(values);
        if (command == "recommend") return PioneerRecommendationCommand.Run(values, cancellationToken);
        if (command is not ("run" or "replay" or "batch")) throw new InvalidDataException("未知pioneer子命令；使用--help查看用法。");
        var required = command == "batch" ? new[] { "--input", "--seed", "--samples", "--workers", "--output" }
            : ["--input", "--seed", "--output"];
        if (required.Any(key => !values.ContainsKey(key)) || values.Keys.Any(key => !required.Contains(key) && key != "--master"))
            throw new InvalidDataException("Pioneer执行参数缺失或包含未知项；--master用于建设、评审或带建设状态的训练起点。");
        if (!ulong.TryParse(values["--seed"], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
            throw new InvalidDataException("--seed必须是无符号64位整数。");
        var outputPath = Path.GetFullPath(values["--output"]);
        if (File.Exists(outputPath)) throw new InvalidDataException("执行报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        var compileStart = Stopwatch.GetTimestamp();
        LoadedPioneerExecution loaded;
        try { loaded = Load(values["--input"], values.GetValueOrDefault("--master")); }
        catch (UnknownMechanismException error)
        {
            return WriteInitializationStop(values["--input"], outputPath, error);
        }
        var compileTime = Stopwatch.GetElapsedTime(compileStart);
        if (command == "run" && loaded.Mode != PioneerExecutionMode.Autonomous
            || command == "replay" && loaded.Mode != PioneerExecutionMode.Conditional)
            throw new InvalidDataException("run要求autonomous模式，replay要求conditional模式；不自动切换或忽略条件结果。");
        var scenario = new PioneerScenario(loaded.Data);
        var sampleCount = command == "batch" ? Positive(values["--samples"], "--samples") : 1;
        var workers = command == "batch" ? Positive(values["--workers"], "--workers") : 1;
        SimulationBatch<TerminalMetrics> batch;
        object? finalState = null;
        var beforeAllocation = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        if (command == "batch")
            batch = SimulationRunner.RunBatch(scenario, loaded.Script, loaded.Root, seed, sampleCount, workers, cancellationToken);
        else
        {
            var execution = SimulationRunner.CreateExecution(scenario, loaded.Script, loaded.Root, seed, 0);
            var result = SimulationRunner.Continue(scenario, loaded.Script, ref execution, cancellationToken);
            batch = new(new(scenario.RulesIdentity, seed, SimulationRandom.Algorithm, SimulationRandom.SeedDerivation),
                [result], SimulationRunner.GetMemoryPlan(scenario, loaded.Script, execution.State, execution.Cursor));
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
        var report = new
        {
            schemaVersion = 1, mode = loaded.Mode, scope = "PioneerExecution", initialStage = loaded.Data.InitialStage,
            loaded.InputSha256, loaded.MasterSha256, batch.Provenance, batch.Memory,
            sampleCount, workers, completed, stopped = sampleCount - completed,
            compileMilliseconds = compileTime.TotalMilliseconds, executionMilliseconds = elapsed.TotalMilliseconds,
            processAllocatedBytes = allocated, avx512Available = ExecutionPlatform.HasAvx512,
            results = rows, finalState,
            execution = new { outcomeSource = loaded.Mode.ToString(),
                timingScope = "executionAndScheduling" }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = new FileStream(outputPath, FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, JsonOptions);
        Console.WriteLine($"Pioneer执行：{sampleCount}条，生成终局指标{completed}条，明确停止{sampleCount - completed}条；耗时{elapsed.TotalMilliseconds:F3}ms。");
        Console.WriteLine("报告包含各轨迹的停止位置、结算进度和随机流末态；终局指标用于目标评价。");
        return completed == sampleCount ? 0 : 3;
    }

    internal static int WriteInitializationStop(string input, string output, UnknownMechanismException error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, new { schemaVersion = 1, phase = "initialization", status = "stopped",
            reason = "unknownMechanism", inputSha256 = Fingerprint(input), detail = error.Message }, JsonOptions);
        Console.Error.WriteLine("Pioneer初态编译受阻：" + error.Message);
        return 3;
    }

    public static LoadedPioneerExecution Load(string path, string? masterPath = null)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        RejectNullArrayElements(document.RootElement, "$");
        var input = document.RootElement.Deserialize<PioneerExecutionInput>(JsonOptions) ?? throw new InvalidDataException("Pioneer输入为空。");
        if (input.SchemaVersion != 1 || string.IsNullOrWhiteSpace(input.RulesIdentity) || input.Outcomes.IsDefault || input.Script.IsDefault)
            throw new InvalidDataException("Pioneer输入要求schemaVersion=1、明确规则身份、outcomes和script数组。");
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
        if (start is PioneerStart.Construction or PioneerStart.Planning or PioneerStart.Checkpoint
            or PioneerStart.TrainingTurn { Development: not null })
        {
            if (masterPath is null) throw new InvalidDataException("建设、评审或带建设状态的训练起点必须提供--master。");
            masterSha = Fingerprint(masterPath);
            using var db = EvidenceAudit.OpenReadOnly(masterPath);
            if (start is not PioneerStart.Checkpoint)
                construction = PioneerMasterCompiler.ConstructionData(db, masterSha);
            else
                checkpoints = new(masterSha,
                    PioneerMasterCompiler.Rows(db, "single_mode_11_planning").Select(row => new PlanningPeriod(row["id"],
                        row["deck_id"], row["check_point_type"], row["planning_turn"], row["check_point_turn"], row["target_pt"])),
                    PioneerMasterCompiler.Rows(db, "single_mode_11_pioneer_pt_bonus").Select(row => new CheckpointPointBonus(row["id"], row["check_point_type"], row["group_id"], row["pt_rate"], row["add_value_max"])),
                    PioneerMasterCompiler.Rows(db, "single_mode_11_check_point_eff").Select(row => new CheckpointEffect(row["id"], row["check_point_type"], row["result_state"], row["effect_type"], row["effect_value_1"], row["effect_value_2"], row["effect_value_3"], row["effect_value_4"])));
            if (masterSha != Fingerprint(masterPath)) throw new InvalidDataException("编译期间master发生变化。");
        }
        else if (masterPath is not null) throw new InvalidDataException("该起点已有明确规则输入，不接受未使用的--master。");
        var data = PioneerRuntimeData.Compile(start, outcomes, input.RulesIdentity, construction, checkpoints);
        return new(data, data.CreateInitialState(), new(input.Script, input.Script.Sum(step => step.Action.OwnedPayloadBytes)), input.Mode,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), masterSha);
    }

    static PioneerConditionalOutcome Outcome(PioneerPayload payload)
    {
        switch (payload.Kind)
        {
            case "restEvent":
                var rest = Read<RestEventInput>(payload);
                return new PioneerConditionalOutcome.RestEvent(rest.ActionOrdinal, rest.Event);
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
    internal static int Positive(string value, string option) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
        ? result : throw new InvalidDataException($"{option}必须是正整数。");
    internal static string Fingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    sealed class ConstructionPlanConverter : JsonConverter<PioneerConstructionPlan>
    {
        sealed record Plan(ImmutableArray<PlannedUpgrade> Steps);
        public override PioneerConstructionPlan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Plan>(ref reader, options) ?? throw new JsonException("建设计划为空。");
            if (value.Steps.IsDefault) throw new JsonException("建设计划缺少steps。");
            return new(value.Steps);
        }
        public override void Write(Utf8JsonWriter writer, PioneerConstructionPlan value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new Plan(value.Steps), options);
    }
}
