using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Cli;

public static class PioneerRecommendationCommand
{
    public static int Run(Dictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        if (values.ContainsKey("--model")) return PioneerForecastCommand.Run(values, cancellationToken);
        string[] required = ["--input", "--goal", "--candidates", "--seed", "--samples", "--workers", "--output"];
        if (required.Any(key => !values.ContainsKey(key)) || values.Keys.Any(key => !required.Contains(key) && key != "--master"))
            throw new InvalidDataException("推荐需要--input、--goal、--candidates、--seed、--samples、--workers和--output；建设、评审或带建设状态的训练起点另提供--master。");
        if (!ulong.TryParse(values["--seed"], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
            throw new InvalidDataException("--seed必须是无符号64位整数。");
        var samples = PioneerExecutionCommand.Positive(values["--samples"], "--samples");
        var workers = PioneerExecutionCommand.Positive(values["--workers"], "--workers");
        var output = Path.GetFullPath(values["--output"]);
        if (File.Exists(output)) throw new InvalidDataException("推荐报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        LoadedPioneerExecution loaded;
        try { loaded = PioneerExecutionCommand.Load(values["--input"], values.GetValueOrDefault("--master")); }
        catch (UnknownMechanismException error)
        {
            return PioneerExecutionCommand.WriteInitializationStop(values["--input"], output, error);
        }
        if (loaded.Mode != PioneerExecutionMode.Autonomous)
            throw new InvalidDataException("推荐入口使用autonomous模式；条件结果通过pioneer replay执行。");
        var goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(values["--goal"]), GoalJson.Options)
            ?? throw new InvalidDataException("目标文件不能为null。");
        var candidateBytes = File.ReadAllBytes(values["--candidates"]);
        var candidates = JsonSerializer.Deserialize<RootCandidate<PioneerRuntimeAction>[]>(candidateBytes, PioneerExecutionCommand.JsonOptions)
            ?? throw new InvalidDataException("候选文件必须是明确的动作数组。");
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.GetTimestamp();
        var compiledGoal = loaded.Data.InitialStage == PioneerStage.CharacterEnding
            ? GoalEvaluator.Compile(goal, new(loaded.Root.Header.Limits.Speed, loaded.Root.Header.Limits.Stamina,
                loaded.Root.Header.Limits.Power, loaded.Root.Header.Limits.Guts, loaded.Root.Header.Limits.Wisdom))
            : goal.Compiled;
        var result = RootSearch.Evaluate(new PioneerScenario(loaded.Data), loaded.Script, loaded.Root, candidates,
            compiledGoal, seed, samples, workers, cancellationToken,
            candidates.Sum(candidate => candidate.Action.OwnedPayloadBytes));
        var elapsed = Stopwatch.GetElapsedTime(timer);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var action = result.RecommendedCandidateId is { } recommended
            ? candidates.Single(candidate => candidate.Id == recommended).Action : (PioneerRuntimeAction?)null;
        var evaluation = result.RecommendedCandidateId is { } selectedId
            ? result.Candidates.Single(candidate => candidate.Candidate.Id == selectedId).Summary : null;
        var report = new
        {
            schemaVersion = 1, initialStage = loaded.Data.InitialStage, loaded.InputSha256, loaded.MasterSha256,
            candidatesSha256 = Convert.ToHexStringLower(SHA256.HashData(candidateBytes)), goalIdentity = goal.Identity,
            samplesPerCandidate = samples, workers, recommendedAction = action, recommendationEvaluation = evaluation, search = result,
            executionMilliseconds = elapsed.TotalMilliseconds, processAllocatedBytes = bytes,
            avx512Available = ExecutionPlatform.HasAvx512
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew))
            JsonSerializer.Serialize(stream, report, PioneerExecutionCommand.JsonOptions);
        if (action is { } selected)
        {
            Console.WriteLine($"建议候选{result.RecommendedCandidateId}：动作{selected.Kind}，每候选{samples}个样本。");
            if (evaluation!.SuccessCount == 0) Console.WriteLine("全部候选均无达标样本，建议按平均条件总缺口、目标优先级选择最接近目标的行动。");
            Console.WriteLine(GoalEvaluation.ProbabilityText(evaluation));
        }
        else Console.WriteLine("候选轨迹尚未全部到达终局指标；停止位置已写入报告。");
        return result.Status == SearchStatus.Completed ? 0 : 3;
    }
}
