using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

// Capture state before the batch runner reuses its workspace, without changing production outcomes.
sealed class PioneerStateAudit(PioneerScenario inner)
    : ISimulationScenario<PioneerState, PioneerDecisionView, PioneerRuntimeAction, PioneerStateAudit.TerminalState>
{
    public readonly record struct TerminalState(TerminalMetrics Metrics, string StateHash);
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public string RulesIdentity => inner.RulesIdentity;
    public long SharedMemoryBytes => inner.SharedMemoryBytes;
    public PioneerState CreateWorkspace(in PioneerState source) => inner.CreateWorkspace(source);
    public long GetWorkspaceBytes(in PioneerState state) => inner.GetWorkspaceBytes(state);
    public void CopyState(in PioneerState source, ref PioneerState destination) => inner.CopyState(source, ref destination);
    public Inspection<PioneerDecisionView> Inspect(in PioneerState state, long ordinal) => inner.Inspect(state, ordinal);
    public SimulationStop? ValidateAction(in PioneerState state, in PioneerRuntimeAction action) => inner.ValidateAction(state, action);
    public TransitionResult ApplyAction(ref PioneerState state, in PioneerRuntimeAction action, ref SimulationRandom random)
        => Capture(inner.ApplyAction(ref state, action, ref random), state);
    public TransitionResult ResumeAutomatic(ref PioneerState state, ref SimulationRandom random)
        => Capture(inner.ResumeAutomatic(ref state, ref random), state);
    public TerminalState GetOutcome(in PioneerState state) => new(inner.GetOutcome(state), Convert.ToHexString(Fingerprint(state, null)));

    public static void AssertDeterministic(PioneerRuntimeData data, ActionScript<PioneerDecisionView, PioneerRuntimeAction> script)
    {
        var root = data.CreateInitialState();
        var before = Fingerprint(root, null);
        var scenario = new PioneerStateAudit(new(data));
        var baseline = SimulationRunner.RunBatch(scenario, script, root, 893, 2049, 1);
        var parallel = SimulationRunner.RunBatch(scenario, script, root, 893, 2049, 32);
        Assert.Equal(baseline.Results, parallel.Results);
        var prefix = SimulationRunner.RunBatch(scenario, script, root, 893, 65, 4);
        Assert.Equal(baseline.Results.Take(65), prefix.Results);
        Assert.Equal(before, Fingerprint(root, null));
    }

    static TransitionResult Capture(TransitionResult result, PioneerState state)
    {
        if (result.Stop is not { } stop) return result;
        var digest = Fingerprint(state, stop);
        return result with { Stop = stop with { Argument0 = BitConverter.ToInt64(digest), Argument1 = BitConverter.ToInt64(digest, 8) } };
    }
    static byte[] Fingerprint(PioneerState state, SimulationStop? stop) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { stop, state.Header, Sections = Enum.GetValues<PioneerStateSection>().Select(section => state.GetSection(section).ToArray()).ToArray() }, Json));
}
