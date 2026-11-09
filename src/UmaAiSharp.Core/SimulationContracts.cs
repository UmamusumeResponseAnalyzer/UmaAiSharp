using System.Text.Json.Serialization;

namespace UmaAiSharp.Core;

public readonly record struct DecisionId([property: JsonRequired] int Kind,
    [property: JsonRequired] long Ordinal, [property: JsonRequired] long Key = 0);

// Context is borrowed until the next state mutation. Policies must copy retained history.
public readonly record struct DecisionPoint<TDecision>(DecisionId Id, TDecision Context) where TDecision : notnull;
public enum InspectionKind { Decision, Terminal, Stopped }
public enum SimulationStopReason { UnknownMechanism, IllegalAction, ScriptMismatch, Cancelled }
public readonly record struct SimulationStop(SimulationStopReason Reason, int Code, int Position = 0,
    long Argument0 = 0, long Argument1 = 0);

public readonly record struct Inspection<TDecision>(InspectionKind Kind, DecisionPoint<TDecision> Decision,
    int Position, SimulationStop? Stop) where TDecision : notnull
{
    public static Inspection<TDecision> AtDecision(DecisionPoint<TDecision> decision, int position = 0)
        => new(InspectionKind.Decision, decision, position, null);
    public static Inspection<TDecision> Terminal(int position = 0) => new(InspectionKind.Terminal, default, position, null);
    public static Inspection<TDecision> Stopped(SimulationStop stop) => new(InspectionKind.Stopped, default, stop.Position, stop);
}

public readonly record struct TransitionResult(bool ActionConsumed, bool ActionSettled, int Position, SimulationStop? Stop)
{
    public static TransitionResult Settled(int position = 0) => new(true, true, position, null);
    public static TransitionResult Stopped(bool consumed, bool settled, SimulationStop stop)
        => new(consumed, settled, stop.Position, stop);
}

public readonly record struct PolicySelection<TAction>(TAction Action, SimulationStop? Stop) where TAction : notnull
{
    public static PolicySelection<TAction> Selected(TAction action) => new(action, null);
    public static PolicySelection<TAction> Stopped(SimulationStop stop) => new(default!, stop);
}

public interface ISimulationScenario<TState, TDecision, TAction, TOutcome>
    where TState : notnull where TDecision : notnull where TAction : notnull
{
    string RulesIdentity { get; }
    long SharedMemoryBytes { get; }
    TState CreateWorkspace(in TState source);
    long GetWorkspaceBytes(in TState state);
    // Copies all future-relevant state into exclusively owned storage, including active lengths.
    void CopyState(in TState source, ref TState destination);
    Inspection<TDecision> Inspect(in TState state, long decisionOrdinal);
    SimulationStop? ValidateAction(in TState state, in TAction action);
    TransitionResult ApplyAction(ref TState state, in TAction action, ref SimulationRandom random);
    // Resumes a consumed action without consuming it again.
    TransitionResult ResumeAutomatic(ref TState state, ref SimulationRandom random);
    // Outcomes own their data; they must not reference reusable workspace buffers.
    TOutcome GetOutcome(in TState state);
}

public interface ITrajectoryPolicy<TDecision, TAction, TCursor>
    where TDecision : notnull where TAction : notnull
{
    long SharedMemoryBytes { get; }
    TCursor CreateCursor();
    long GetCursorBytes(in TCursor cursor);
    void ResetCursor(ref TCursor cursor, int sampleIndex);
    void CopyCursor(in TCursor source, ref TCursor destination);
    // A stopped selection leaves cursor/RNG unchanged; selected actions own immutable data.
    PolicySelection<TAction> ChooseAction(in DecisionPoint<TDecision> decision, ref TCursor cursor, ref SimulationRandom random);
    SimulationStop? ValidateCompletion(in TCursor cursor);
}

public enum ExecutionStage { Ready, SelectedAction, AutomaticSettlement, Terminal }
public readonly record struct ExecutionProgress(long DecisionOrdinal, int ConsumedActions, int SettledActions,
    int Position, bool ActionConsumed, bool ActionSettled);

// Caller ownership permits exact continuation. New independent samples use ResetSample.
public struct TrajectoryExecution<TState, TAction, TCursor> where TState : notnull where TAction : notnull
{
    public TState State;
    public TCursor Cursor;
    public SimulationRandom RulesRandom;
    public SimulationRandom PolicyRandom;
    public TAction PendingAction;
    public ExecutionStage Stage;
    public ExecutionProgress Progress;
    public int SampleIndex;
    public ulong RootSeed;
}

public sealed record SimulationProvenance(string RulesIdentity, ulong RootSeed, string RandomAlgorithm, string SeedDerivation);
public enum TrajectoryStatus { Advanced, Completed, Stopped }
public readonly record struct RandomStreamState(ulong Rules, ulong Policy);
public readonly record struct TrajectoryResult<TOutcome>(TrajectoryStatus Status, int SampleIndex,
    ExecutionProgress Progress, TOutcome Outcome, SimulationStop? Stop, RandomStreamState RandomState);
public sealed record SimulationBatch<TOutcome>(SimulationProvenance Provenance, TrajectoryResult<TOutcome>[] Results,
    ExecutionMemoryPlan Memory);

// Independently callable rule components retain boundary exceptions; the runner uses statuses.
public sealed class UnknownMechanismException(string message) : Exception(message);
public sealed class IllegalActionException(string message) : Exception(message);
public sealed class ScriptMismatchException(string message) : Exception(message);
