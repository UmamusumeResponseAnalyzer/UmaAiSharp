using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace UmaAiSharp.Core;

public readonly record struct ScriptedAction<TAction>([property: JsonRequired] DecisionId DecisionId,
    [property: JsonRequired] TAction Action) where TAction : notnull;
public struct ScriptCursor { public int Position; }

public sealed class ActionScript<TDecision, TAction> : ITrajectoryPolicy<TDecision, TAction, ScriptCursor>
    where TDecision : notnull where TAction : notnull
{
    readonly ImmutableArray<ScriptedAction<TAction>> steps;
    public long SharedMemoryBytes { get; }

    public ActionScript(ImmutableArray<ScriptedAction<TAction>> steps, long? ownedActionPayloadBytes = null)
    {
        if (steps.IsDefault || steps.Any(step => step.DecisionId.Kind < 0 || step.DecisionId.Ordinal < 0 || step.Action is null))
            throw new ArgumentException("动作脚本必须显式提供有效决策身份和动作。", nameof(steps));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TAction>() && ownedActionPayloadBytes is null)
            throw new ArgumentException("引用类型动作必须明确声明其独占不可变负载容量。", nameof(ownedActionPayloadBytes));
        if (ownedActionPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(ownedActionPayloadBytes));
        this.steps = steps;
        SharedMemoryBytes = checked(64L + (long)Unsafe.SizeOf<ScriptedAction<TAction>>() * steps.Length + (ownedActionPayloadBytes ?? 0));
    }

    public ScriptCursor CreateCursor() => default;
    public long GetCursorBytes(in ScriptCursor cursor) => Unsafe.SizeOf<ScriptCursor>();
    public void ResetCursor(ref ScriptCursor cursor, int sampleIndex) => cursor = default;
    public void CopyCursor(in ScriptCursor source, ref ScriptCursor destination) => destination = source;

    public PolicySelection<TAction> ChooseAction(in DecisionPoint<TDecision> decision, ref ScriptCursor cursor, ref SimulationRandom random)
    {
        if (cursor.Position == steps.Length)
            return PolicySelection<TAction>.Stopped(new(SimulationStopReason.ScriptMismatch, 1, cursor.Position, decision.Id.Ordinal));
        var step = steps[cursor.Position];
        if (step.DecisionId != decision.Id)
            return PolicySelection<TAction>.Stopped(new(SimulationStopReason.ScriptMismatch, 2, cursor.Position,
                step.DecisionId.Ordinal, decision.Id.Ordinal));
        cursor.Position++;
        return PolicySelection<TAction>.Selected(step.Action);
    }

    public SimulationStop? ValidateCompletion(in ScriptCursor cursor) => cursor.Position == steps.Length ? null
        : new(SimulationStopReason.ScriptMismatch, 3, cursor.Position, steps.Length - cursor.Position);
}
