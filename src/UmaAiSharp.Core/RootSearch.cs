using System.Runtime.CompilerServices;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Core;

public readonly record struct RootCandidate<TAction>(int Id, TAction Action) where TAction : notnull;
public sealed record CandidateEvaluation<TAction>(RootCandidate<TAction> Candidate,
    SimulationBatch<TerminalMetrics> Batch, GoalSummary? Summary) where TAction : notnull;
public enum SearchStatus { Completed, Stopped, Cancelled }
public sealed record RootSearchResult<TAction>(DecisionId Decision, SearchStatus Status,
    int? RecommendedCandidateId, int[] Ranking, CandidateEvaluation<TAction>[] Candidates) where TAction : notnull;

public static class RootSearch
{
    // Candidates share sample seeds; their identity and enumeration order do not change future draws.
    public static RootSearchResult<TAction> Evaluate<TState, TDecision, TAction, TCursor>(
        ISimulationScenario<TState, TDecision, TAction, TerminalMetrics> scenario,
        ITrajectoryPolicy<TDecision, TAction, TCursor> continuation, in TState root,
        ReadOnlySpan<RootCandidate<TAction>> candidates, CompiledGoal goal,
        ulong seed, int samplesPerCandidate, int workers, CancellationToken cancellationToken = default,
        long? ownedActionPayloadBytes = null, bool rankByAttributeRating = false)
        where TState : notnull where TDecision : notnull where TAction : notnull
    {
        ExecutionPlatform.EnsureSupported();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerCandidate);
        if (workers is < 1 or > ExecutionMemoryPlan.WorkerCapacity)
            throw new ArgumentOutOfRangeException(nameof(workers), "worker数量必须为1..32。");
        if (candidates.IsEmpty) throw new ArgumentException("搜索至少需要一个候选行动。", nameof(candidates));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TAction>() && ownedActionPayloadBytes is null)
            throw new ArgumentException("引用类型候选行动必须声明其不可变负载容量。", nameof(ownedActionPayloadBytes));
        if (ownedActionPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(ownedActionPayloadBytes));
        var inspection = scenario.Inspect(root, 0);
        if (inspection.Kind != InspectionKind.Decision)
            throw new ArgumentException("搜索起点必须处于待选择的决策。", nameof(root));
        var actions = candidates.ToArray();
        var identities = new HashSet<int>();
        foreach (var candidate in actions)
        {
            if (candidate.Id < 0 || !identities.Add(candidate.Id))
                throw new ArgumentException("候选编号必须非负且唯一。", nameof(candidates));
            if (scenario.ValidateAction(root, candidate.Action) is { } invalid)
                throw new ArgumentException($"候选{candidate.Id}校验失败：{invalid.Reason}，代码{invalid.Code}，位置{invalid.Position}。", nameof(candidates));
        }
        var results = new CandidateEvaluation<TAction>[actions.Length];
        var retainedResultBytes = checked((long)actions.Length * samplesPerCandidate
            * (Unsafe.SizeOf<TrajectoryResult<TerminalMetrics>>() + 128L));
        var sharedBytes = checked(goal.SharedMemoryBytes + (ownedActionPayloadBytes ?? 0) + 128L
            + actions.Length * (64L + Unsafe.SizeOf<RootCandidate<TAction>>()) + retainedResultBytes);
        var initialPolicy = new RootPolicy<TDecision, TAction, TCursor>(continuation, inspection.Decision.Id, actions[0].Action, sharedBytes);
        var (memory, workspaces) = SimulationRunner.CreateBatchWorkspaces(scenario, initialPolicy, root, samplesPerCandidate, workers);
        var status = SearchStatus.Completed;
        for (var index = 0; index < actions.Length; index++)
        {
            var policy = new RootPolicy<TDecision, TAction, TCursor>(continuation, inspection.Decision.Id, actions[index].Action, sharedBytes);
            var batch = SimulationRunner.RunBatch(scenario, policy, root, seed, samplesPerCandidate, memory, workspaces, cancellationToken);
            GoalSummary? summary = null;
            if (Array.TrueForAll(batch.Results, sample => sample.Status == TrajectoryStatus.Completed))
            {
                summary = GoalEvaluator.Summarize(goal, batch);
                if (rankByAttributeRating && summary.SuccessCount != 0 && summary.MeanSuccessfulAttributeRating is null)
                    throw new InvalidDataException($"候选{actions[index].Id}的达标轨迹缺少终局属性评分。");
            }
            else if (Array.Exists(batch.Results, sample => sample.Stop?.Reason == SimulationStopReason.Cancelled))
                status = SearchStatus.Cancelled;
            else if (status != SearchStatus.Cancelled)
                status = SearchStatus.Stopped;
            results[index] = new(actions[index], batch, summary);
        }
        if (status != SearchStatus.Completed)
            return new(inspection.Decision.Id, status, null, [], results);
        var ranking = Enumerable.Range(0, results.Length).ToArray();
        Array.Sort(ranking, (left, right) =>
        {
            var comparison = GoalEvaluator.Compare(results[right].Summary!, results[left].Summary!, rankByAttributeRating);
            return comparison != 0 ? comparison : results[left].Candidate.Id.CompareTo(results[right].Candidate.Id);
        });
        return new(inspection.Decision.Id, status, results[ranking[0]].Candidate.Id,
            Array.ConvertAll(ranking, index => results[index].Candidate.Id), results);
    }

    struct RootCursor<TCursor>
    {
        public bool Selected;
        public TCursor Continuation;
    }

    sealed class RootPolicy<TDecision, TAction, TCursor>(ITrajectoryPolicy<TDecision, TAction, TCursor> continuation,
        DecisionId decision, TAction action, long searchBytes) : ITrajectoryPolicy<TDecision, TAction, RootCursor<TCursor>>
        where TDecision : notnull where TAction : notnull
    {
        public long SharedMemoryBytes => checked(continuation.SharedMemoryBytes + searchBytes);
        public RootCursor<TCursor> CreateCursor() => new() { Continuation = continuation.CreateCursor() };
        public long GetCursorBytes(in RootCursor<TCursor> cursor) => continuation.GetCursorBytes(cursor.Continuation);
        public void ResetCursor(ref RootCursor<TCursor> cursor, int sampleIndex)
        {
            cursor.Selected = false;
            continuation.ResetCursor(ref cursor.Continuation, sampleIndex);
        }
        public void CopyCursor(in RootCursor<TCursor> source, ref RootCursor<TCursor> destination)
        {
            destination.Selected = source.Selected;
            continuation.CopyCursor(source.Continuation, ref destination.Continuation);
        }
        public PolicySelection<TAction> ChooseAction(in DecisionPoint<TDecision> current,
            ref RootCursor<TCursor> cursor, ref SimulationRandom random)
        {
            if (cursor.Selected) return continuation.ChooseAction(current, ref cursor.Continuation, ref random);
            if (current.Id != decision) throw new InvalidOperationException("搜索副本的根决策身份发生变化。");
            cursor.Selected = true;
            return PolicySelection<TAction>.Selected(action);
        }
        public SimulationStop? ValidateCompletion(in RootCursor<TCursor> cursor) => continuation.ValidateCompletion(cursor.Continuation);
    }
}
