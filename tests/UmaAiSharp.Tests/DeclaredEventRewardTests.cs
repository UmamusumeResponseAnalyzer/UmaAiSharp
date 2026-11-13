using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class DeclaredEventRewardTests
{
    [Fact]
    public void RealRevealedChoicesMatchResourceOutcomesWithoutReadingFutureState()
    {
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-event-resources.json")))!.AsObject();
        var templates = fixture["templates"]!.AsArray();
        var mismatches = new List<string>();
        var count = 0;
        foreach (var row in fixture["cases"]!.AsArray())
        {
            var template = templates[row!["template"]!.GetValue<int>()]!;
            var start = PioneerEventCompiler.Compile(row["before"]!.AsObject(), template["event"]!.AsObject(), template["rewards"]!.AsArray());
            var data = PioneerRuntimeData.Compile(start, [], "event-fixture");
            var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
            var decision = scenario.Inspect(root, 0).Decision;
            Assert.Equal(start.Choices.Length, decision.Context.EventChoices.Length);
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(decision.Id, PioneerRuntimeAction.Choose(start.Event, row["choice"]!.GetValue<int>()))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 19, 0);
            var random = execution.RulesRandom.State;
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            var expected = row["after"]!;
            var resources = new TrainingValues(Int("speed"), Int("stamina"), Int("power"), Int("guts"), Int("wiz"), Int("vital"), Int("skill_point"));
            if (execution.State.Header.Resources != resources || execution.State.Header.Motivation != Int("motivation")
                || execution.State.Header.Limits.Vital != Int("max_vital"))
                mismatches.Add($"{row["source"]}:{row["beforePacketId"]}->{row["afterPacketId"]}: {execution.State.Header.Resources} != {resources}");
            Assert.Equal(TrajectoryStatus.Stopped, result.Status);
            Assert.Equal((int)PioneerStopCode.EventRewardRemainderRequired, result.Stop!.Value.Code);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(0, result.Progress.SettledActions);
            Assert.Equal(0, execution.State.Header.EventRewardRemainingEffects);
            Assert.Equal(random, result.RandomState.Rules);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(start.Resources, root.Header.Resources);
            count++;
            int Int(string name) => expected[name]!.GetValue<int>();
        }
        Assert.Equal(1789, count);
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(20)) + $"; total={mismatches.Count}");
    }

    [Fact]
    public void AmbiguousRewardsNeedABranchIdentityAndKeepUnimplementedEffects()
    {
        var start = Start();
        var data = PioneerRuntimeData.Compile(start, [], "event");
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var decision = scenario.Inspect(root, 0).Decision;
        Assert.Equal(1, decision.Context.MinimumChoice); Assert.Equal(2, decision.Context.MaximumChoice);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>([new(decision.Id, PioneerRuntimeAction.Choose(start.Event, 1))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
        var random = execution.RulesRandom.State;
        var missing = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal((int)PioneerStopCode.EventRewardOutcomeRequired, missing.Stop!.Value.Code);
        Assert.Equal(0, missing.Progress.ConsumedActions); Assert.Equal(start.Resources, execution.State.Header.Resources);
        Assert.Equal(random, missing.RandomState.Rules);
        var resolved = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.EventRewardResult(0, 1, 1)], "event");
        var resolvedScenario = new PioneerScenario(resolved); var initial = resolved.CreateInitialState();
        execution = SimulationRunner.CreateExecution(resolvedScenario, script, initial, 71, 0);
        var stopped = SimulationRunner.Continue(resolvedScenario, script, ref execution);
        Assert.Equal(1205, execution.State.Header.Resources.Speed);
        Assert.Equal(1, execution.State.Header.EventRewardRemainingEffects);
        Assert.Equal(1, execution.State.Header.EventRewardResourceMask);
        Assert.Equal(1, execution.State.Header.ConditionalOutcomeCount);
        Assert.Equal(1, stopped.Progress.ConsumedActions); Assert.Equal(0, stopped.Progress.SettledActions);
        var unknown = new DeclaredEventRewards(new([new(15, 1, 0, 0)]));
        Assert.Single(unknown.RemainingEffects); Assert.Equal(0, unknown.ResourceMask);
        var guardedStart = start with { CharacterEffects = [8, 25, 26], Choices = [new(1, [new([new(2, 20, 1, 0)])])] };
        var guardedData = PioneerRuntimeData.Compile(guardedStart, [], "guarded-event");
        var guardedScenario = new PioneerScenario(guardedData); var guardedRoot = guardedData.CreateInitialState();
        execution = SimulationRunner.CreateExecution(guardedScenario, script, guardedRoot, 71, 0);
        _ = SimulationRunner.Continue(guardedScenario, script, ref execution);
        Assert.Equal(3, execution.State.Header.Motivation);
        Assert.Equal(new[] { 8, 26 }, execution.State.GetSection(PioneerStateSection.CharacterEffects).ToArray());
        Assert.Equal(new[] { 8, 25, 26 }, guardedRoot.GetSection(PioneerStateSection.CharacterEffects).ToArray());
    }

    [Fact]
    public void ResourceOrderCapsAndInputRejectionSurviveCliRoundTrip()
    {
        var reward = new DeclaredEventRewards(new([new(1, 10, 32, 0), new(1, 11, 4, 0), new(1, 20, 1, 0), new(1, 1, 15, 0)]));
        var before = new TrainingValues(1186, 1400, 1200, 100, 100, 99, 5);
        var caps = new TrainingLimits(2000, 1400, 2000, 2000, 2000, 100);
        var after = reward.Apply(before, caps, 5, false, false);
        Assert.Equal(104, after.Resources.Vital); Assert.Equal(104, after.Limits.Vital);
        Assert.Equal(1201, after.Resources.Speed); Assert.Equal(5, after.Motivation);
        Assert.Throws<UnknownMechanismException>(() => new DeclaredEventRewards(new([new(2, 1, 10, 0)])).Apply(before, caps, 3, false, false));
        Assert.Throws<InvalidDataException>(() => new DeclaredEventRewards(new([new(1, 10, -1, 0)])));
        var guarded = new DeclaredEventRewards(new([new(2, 20, 1, 0)])).Apply(before, caps, 3, true, false);
        Assert.True(guarded.ConsumePositiveThinking); Assert.Equal(3, guarded.Motivation);

        var start = Start();
        var decision = new DecisionId((int)PioneerStage.DeclaredEvent, 0, PioneerScenario.DecisionKey(start.Turn, start.Event.EventId));
        var input = new PioneerExecutionInput(1, "event-cli", PioneerExecutionMode.Conditional,
            new("declaredEvent", JsonSerializer.SerializeToElement(start, PioneerExecution.JsonOptions)),
            [new("eventRewardResult", JsonSerializer.SerializeToElement(new { actionOrdinal = 0, choiceNumber = 1, branchIndex = 1 }))],
            [new(decision, PioneerRuntimeAction.Choose(start.Event, 1))]);
        var path = Path.Combine(Path.GetTempPath(), "UmaAiSharp-event-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(input, PioneerExecution.JsonOptions));
            var loaded = PioneerExecution.Load(path);
            var execution = SimulationRunner.CreateExecution(new PioneerScenario(loaded.Data), loaded.Script, loaded.Root, 71, 0);
            Assert.Equal(1, SimulationRunner.Continue(new PioneerScenario(loaded.Data), loaded.Script, ref execution).Progress.ConsumedActions);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void RepeatedResourceAtomsStopBeforeChangingTheSelectedBranch(int target, int secondDirection)
    {
        var start = Start() with { CharacterEffects = [25], Choices = [new(1,
            [new([new(1, target, 10, 0), new(secondDirection, target, 5, 0)])])] };
        var data = PioneerRuntimeData.Compile(start, [], "event-sequence");
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, 1))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 19, 0);
        var random = execution.RulesRandom.State;
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(SimulationStopReason.UnknownMechanism, result.Stop!.Value.Reason);
        Assert.Equal(0, result.Progress.ConsumedActions);
        Assert.Equal(root.Header, execution.State.Header);
        Assert.Equal(new[] { 25 }, execution.State.GetSection(PioneerStateSection.CharacterEffects).ToArray());
        Assert.Equal(random, execution.RulesRandom.State);
    }

    [Theory]
    [InlineData(1, false, true, 3, false)]
    [InlineData(1, false, false, 4, false)]
    [InlineData(-1, false, true, 2, false)]
    [InlineData(-1, true, true, 3, true)]
    public void ConditionEffectsControlMotivationWithoutDroppingOtherRewards(int gain, bool positive, bool migraine,
        int expected, bool consumed)
    {
        var start = Start() with
        {
            CharacterEffects = [.. migraine ? new[] { 5 } : [], .. positive ? new[] { 25 } : []],
            Choices = [new(1, [new([new(gain < 0 ? 2 : 1, 20, Math.Abs(gain), 0), new(1, 30, 7, 0)])])]
        };
        var data = PioneerRuntimeData.Compile(start, [], "condition-motivation");
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, 1))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 93, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(expected, execution.State.Header.Motivation);
        Assert.Equal(start.Resources.SkillPoints + 7, execution.State.Header.Resources.SkillPoints);
        Assert.Equal(positive && !consumed, execution.State.GetSection(PioneerStateSection.CharacterEffects).Contains(25));
        Assert.Equal(migraine, execution.State.GetSection(PioneerStateSection.CharacterEffects).Contains(5));
        Assert.Equal(1, result.Progress.ConsumedActions);
        Assert.Equal(0, result.Progress.SettledActions);
        Assert.Equal(3, root.Header.Motivation);
    }

    [Fact]
    public void MigraineWithUnresolvedConditionOrderStopsBeforeAnyReward()
    {
        var reward = new DeclaredEventRewards(new([new(1, 30, 7, 0), new(1, 20, 1, 0), new(10, 5, 0, 0)]));
        var start = Start();
        Assert.Throws<UnknownMechanismException>(() => reward.Apply(start.Resources, start.Limits, 3, false, true));
    }

    static PioneerStart.DeclaredEvent Start() => new(32, 5, new(10000, 1011, 501011101, 1),
        new(1190, 100, 100, 100, 100, 60, 100), new(2000, 2000, 2000, 2000, 2000, 100), 3, [],
        [new(1, [new([new(1, 1, 10, 0)]), new([new(1, 1, 20, 0), new(9, 26, 0, 0)])]), new(2, [new([new(1, 10, 20, 0)])])]);
}
