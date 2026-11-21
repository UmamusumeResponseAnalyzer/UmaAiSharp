using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed class PioneerContinuationPolicy : ITrajectoryPolicy<PioneerDecisionView, PioneerRuntimeAction, ScriptCursor>
{
    // Simulation and observed lottery choices use this estimate of raw attributes plus twice the skill Pt.
    public static int InheritanceValue(AttributeValues attributes, int skillPoints)
    {
        static int Raw(int display) => display + Math.Max(0, display - 1200);
        return Raw(attributes.Speed) + Raw(attributes.Stamina) + Raw(attributes.Power)
            + Raw(attributes.Guts) + Raw(attributes.Wisdom) + 2 * skillPoints;
    }

    public long SharedMemoryBytes => 32;
    public ScriptCursor CreateCursor() => default;
    public long GetCursorBytes(in ScriptCursor cursor) => sizeof(int);
    public void ResetCursor(ref ScriptCursor cursor, int sampleIndex) => cursor = default;
    public void CopyCursor(in ScriptCursor source, ref ScriptCursor destination) => destination = source;

    public PolicySelection<PioneerRuntimeAction> ChooseAction(in DecisionPoint<PioneerDecisionView> decision,
        ref ScriptCursor cursor, ref SimulationRandom random)
        => decision.Context.SuggestedAction is { } action ? PolicySelection<PioneerRuntimeAction>.Selected(action)
            : PolicySelection<PioneerRuntimeAction>.Stopped(new(SimulationStopReason.UnknownMechanism,
                (int)PioneerStopCode.UnsupportedComponentInput, (int)decision.Context.Stage, decision.Context.Turn,
                Detail: decision.Context.SuggestionError ?? "没有合法的后续行动。"));

    public SimulationStop? ValidateCompletion(in ScriptCursor cursor) => null;
}
