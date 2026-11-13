using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP;

public static class PioneerRecommendation
{
    public static ToolResult Run(string inputPath, string goalPath, string candidatesPath, ulong seed, int samples,
        int workers, string outputPath, string? masterPath = null, CancellationToken cancellationToken = default, string? rulesPath = null,
        PioneerModel? model = null)
    {
        ToolArguments.ValidateBatch(samples, workers);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new InvalidDataException("推荐报告已存在，拒绝覆盖。");
        ExecutionPlatform.EnsureSupported();
        LoadedPioneerExecution loaded;
        try { loaded = PioneerExecution.Load(inputPath, masterPath, rulesPath, model); }
        catch (UnknownMechanismException error)
        {
            return PioneerExecution.WriteInitializationStop(inputPath, output, error);
        }
        if (loaded.Mode != PioneerExecutionMode.Autonomous)
            throw new InvalidDataException("推荐入口使用autonomous模式；条件结果使用 pioneer_replay。");
        var goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllBytes(goalPath), GoalJson.Options)
            ?? throw new InvalidDataException("目标文件不能为null。");
        var candidateBytes = File.ReadAllBytes(candidatesPath);
        var candidates = JsonSerializer.Deserialize<RootCandidate<PioneerRuntimeAction>[]>(candidateBytes, PioneerExecution.JsonOptions)
            ?? throw new InvalidDataException("候选文件必须是明确的动作数组。");
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.GetTimestamp();
        var compiledGoal = loaded.Data.InitialStage == PioneerStage.CharacterEnding
            ? GoalEvaluator.Compile(goal, new(loaded.Root.Header.Limits.Speed, loaded.Root.Header.Limits.Stamina,
                loaded.Root.Header.Limits.Power, loaded.Root.Header.Limits.Guts, loaded.Root.Header.Limits.Wisdom))
            : goal.Compiled;
        ITrajectoryPolicy<PioneerDecisionView, PioneerRuntimeAction, ScriptCursor> policy = loaded.UseContinuation
            ? new PioneerContinuationPolicy() : loaded.Script;
        var result = RootSearch.Evaluate(new PioneerScenario(loaded.Data, loaded.UseContinuation), policy, loaded.Root, candidates,
            compiledGoal, seed, samples, workers, cancellationToken,
            candidates.Sum(candidate => candidate.Action.OwnedPayloadBytes),
            rankByAttributeRating: loaded.UseContinuation && loaded.Data.IsWholeRun);
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
            JsonSerializer.Serialize(stream, report, PioneerExecution.JsonOptions);
        return ToolResult.Report(output, new
        {
            recommendedCandidateId = result.RecommendedCandidateId,
            recommendedAction = action, recommendationEvaluation = evaluation,
            samplesPerCandidate = samples, candidateCount = candidates.Length,
            stops = ToolResult.SearchStops(result)
        }, ToolResult.SearchStatus(result.Status));
    }
}
