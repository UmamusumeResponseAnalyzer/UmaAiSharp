using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed class PioneerContinuationPolicy : ITrajectoryPolicy<PioneerDecisionView, PioneerRuntimeAction, ScriptCursor>
{
    public long SharedMemoryBytes => 32;
    public ScriptCursor CreateCursor() => default;
    public long GetCursorBytes(in ScriptCursor cursor) => sizeof(int);
    public void ResetCursor(ref ScriptCursor cursor, int sampleIndex) => cursor = default;
    public void CopyCursor(in ScriptCursor source, ref ScriptCursor destination) => destination = source;

    public PolicySelection<PioneerRuntimeAction> ChooseAction(in DecisionPoint<PioneerDecisionView> decision,
        ref ScriptCursor cursor, ref SimulationRandom random)
        => decision.Context.SuggestedAction is { } action ? PolicySelection<PioneerRuntimeAction>.Selected(action)
            : PolicySelection<PioneerRuntimeAction>.Stopped(new(SimulationStopReason.UnknownMechanism,
                (int)PioneerStopCode.UnsupportedComponentInput, (int)decision.Context.Stage));

    public SimulationStop? ValidateCompletion(in ScriptCursor cursor) => null;
}
