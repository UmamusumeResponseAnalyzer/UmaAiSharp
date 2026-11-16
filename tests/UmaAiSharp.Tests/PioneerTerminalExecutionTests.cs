using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Scoring;

namespace UmaAiSharp.Tests;

public sealed class PioneerTerminalExecutionTests
{
    [Fact]
    public void CharacterEndingGeneratesTheObservedFinishMetricsFromBeforeOnly()
    {
        var rows = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-character-endings.json")))!["cases"]!.AsArray();
        Assert.Equal(215, rows.Count);
        foreach (var row in rows)
        {
            var start = Start(row!);
            var data = PioneerRuntimeData.Compile(start, [], "character-ending");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, 0))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 19, 0);
            var rules = execution.RulesRandom.State;
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(TrajectoryStatus.Completed, result.Status);
            Assert.Equal(1, result.Progress.ConsumedActions); Assert.Equal(1, result.Progress.SettledActions);
            var finish = row!["finish"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
            var actual = result.Outcome;
            Assert.Equal(new(finish[0], finish[1], finish[2], finish[3], finish[4]), actual.Attributes);
            Assert.Equal(new(start.Limits.Speed, start.Limits.Stamina, start.Limits.Power, start.Limits.Guts, start.Limits.Wisdom), actual.Caps);
            Assert.Equal(20, actual.FutureGainedSkillPoints);
            Assert.Equal(finish[5], execution.State.Header.Resources.Vital);
            Assert.Equal(finish[6], execution.State.Header.Resources.SkillPoints);
            Assert.Equal(finish.Take(5).Sum(ScoringCandidates.StatusToPoint), actual.VerifiedAttributeRating);
            Assert.NotNull(actual.FutureOverflow);
            Assert.Equal(rules, result.RandomState.Rules);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(start.Resources, root.Header.Resources);
            Assert.Throws<InvalidOperationException>(() => scenario.GetOutcome(root));
            Assert.NotNull(scenario.ValidateAction(execution.State, PioneerRuntimeAction.Choose(start.Event, 0)));
        }
    }

    [Fact]
    public void EndingForksAndFixedSamplesKeepTerminalMetricsAndPathOverflow()
    {
        var start = new PioneerStart.CharacterEnding(78, 5, 3, new(6, 1060, 501060111, 3),
            new(1198, 1200, 1500, 100, 1399, 50, 100), new(1200, 1201, 1500, 2000, 1400, 100), 3);
        var data = PioneerRuntimeData.Compile(start, [], "ending-path"); var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, 0))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
        var fork = SimulationRunner.CreateExecution(scenario, script, root, 0, 0);
        SimulationRunner.ForkInto(scenario, script, execution, ref fork);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref fork));
        Assert.Equal(new AttributeValues(1, 1, 2, 0, 1), result.Outcome.FutureOverflow);
        Assert.Equal(execution.State.Header, fork.State.Header);
        Assert.Equal(PioneerStage.CharacterEnding, root.Header.Stage);
        PioneerStateAudit.AssertDeterministic(data, script);
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { CharacterState = 0 }, [], "bad"));
        var alias = start with { Event = start.Event with { EventId = 7 } };
        var aliased = PioneerRuntimeData.Compile(alias, [], "ending-alias");
        var aliasScenario = new PioneerScenario(aliased);
        var aliasRoot = aliased.CreateInitialState();
        Assert.Equal(PioneerScenario.DecisionKey(78, start.Event.StoryId), aliasScenario.Inspect(aliasRoot, 0).Decision.Id.Key);
        Assert.Null(aliasScenario.ValidateAction(aliasRoot, PioneerRuntimeAction.Choose(alias.Event, 0)));
        Assert.Null(aliasScenario.ValidateAction(aliasRoot, PioneerRuntimeAction.Choose(start.Event, 0)));
        Assert.NotNull(aliasScenario.ValidateAction(aliasRoot, PioneerRuntimeAction.Choose(alias.Event with { StoryId = 501060110 }, 0)));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { Event = start.Event with { CharacterId = 1011 } }, [], "bad"));
    }

    [Fact]
    public void TerminalRequiresCompleteActionAndConditionScripts()
    {
        var start = new PioneerStart.CharacterEnding(78, 5, 3, new(6, 1060, 501060111, 3),
            new(100, 100, 100, 100, 100, 50, 100), new(1800, 1800, 1800, 1800, 1800, 100), 3);
        foreach (var unusedCondition in new[] { false, true })
        {
            var data = PioneerRuntimeData.Compile(start,
                unusedCondition ? [new PioneerConditionalOutcome.TrainingResult(1, true)] : [], "ending-completion");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var first = new ScriptedAction<PioneerRuntimeAction>(scenario.Inspect(root, 0).Decision.Id,
                PioneerRuntimeAction.Choose(start.Event, 0));
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                unusedCondition ? [first] : [first, first with { DecisionId = first.DecisionId with { Ordinal = 1 } }], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(TrajectoryStatus.Stopped, result.Status);
            Assert.Equal(SimulationStopReason.ScriptMismatch, result.Stop!.Value.Reason);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(default, result.Outcome);
            if (unusedCondition)
            {
                Assert.Equal((int)PioneerStopCode.OutcomeIdentityMismatch, result.Stop.Value.Code);
                Assert.Throws<InvalidOperationException>(() => scenario.GetOutcome(execution.State));
            }
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
        }
    }

    static PioneerStart.CharacterEnding Start(JsonNode row)
    {
        var before = row["before"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        var caps = row["caps"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        var current = row["event"]!;
        return new(78, 5, 3, new(current["event_id"]!.GetValue<int>(), current["chara_id"]!.GetValue<int>(),
                current["story_id"]!.GetValue<int>(), current["play_timing"]!.GetValue<int>()),
            new(before[0], before[1], before[2], before[3], before[4], before[5], before[6]),
            new(caps[0], caps[1], caps[2], caps[3], caps[4], caps[5]), row["motivation"]!.GetValue<int>());
    }
}
