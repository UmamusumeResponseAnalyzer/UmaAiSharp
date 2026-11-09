using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli;
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
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { Event = start.Event with { EventId = 7 } }, [], "bad"));
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

    [Fact]
    public void RecommendationCliUsesGeneratedTerminalMetricsAndKeepsStoppedCandidates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-recommend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples"));
            JsonNode? baseline = null;
            foreach (var workers in new[] { 1, 4, 16, 32 })
            {
                var output = Path.Combine(directory, $"ending-{workers}.json");
                Assert.Equal(0, Program.Main(["pioneer", "recommend", "--input", Path.Combine(example, "pioneer/character-ending.json"),
                    "--goal", Path.Combine(example, "power-goal.json"), "--candidates", Path.Combine(example, "pioneer/character-ending-candidates.json"),
                    "--seed", "71", "--samples", "131", "--workers", workers.ToString(), "--output", output]));
                var report = JsonNode.Parse(File.ReadAllBytes(output))!;
                Assert.Equal("completed", report["search"]!["status"]!.GetValue<string>());
                Assert.Equal(6, report["search"]!["recommendedCandidateId"]!.GetValue<int>());
                Assert.Equal(131, report["search"]!["candidates"]![0]!["summary"]!["successCount"]!.GetValue<int>());
                Assert.Equal(1m, report["recommendationEvaluation"]!["successRate"]!.GetValue<decimal>());
                if (baseline is not null) Assert.True(JsonNode.DeepEquals(baseline, report["search"]));
                baseline = report["search"]!.DeepClone();
            }
            var unreachableGoal = Path.Combine(directory, "unreachable-goal.json");
            var goal = JsonNode.Parse(File.ReadAllBytes(Path.Combine(example, "power-goal.json")))!;
            goal["constraints"]![0]!["value"] = 3000;
            File.WriteAllText(unreachableGoal, goal.ToJsonString());
            var closestOutput = Path.Combine(directory, "closest.json");
            Assert.Equal(0, Program.Main(["pioneer", "recommend", "--input", Path.Combine(example, "pioneer/character-ending.json"),
                "--goal", unreachableGoal, "--candidates", Path.Combine(example, "pioneer/character-ending-candidates.json"),
                "--seed", "71", "--samples", "65", "--workers", "4", "--output", closestOutput]));
            var closest = JsonNode.Parse(File.ReadAllBytes(closestOutput))!;
            Assert.NotNull(closest["recommendedAction"]);
            var evaluation = closest["recommendationEvaluation"]!;
            Assert.Equal(0, evaluation["successCount"]!.GetValue<int>());
            Assert.Equal(0m, evaluation["successRate"]!.GetValue<decimal>());
            Assert.Null(evaluation["conditionalUtilities"]);
            Assert.True(evaluation["meanConstraintViolation"]!.GetValue<decimal>() > 0);
            Assert.Equal(0.0450422581230137, evaluation["zeroSuccessProbabilityUpperBound95"]!.GetValue<double>(), 12);
            var training = JsonNode.Parse(File.ReadAllBytes(Path.Combine(example, "pioneer/training-turn.json")))!;
            training["mode"] = "autonomous"; training["outcomes"] = new JsonArray(); training["script"] = new JsonArray();
            var input = Path.Combine(directory, "training.json"); File.WriteAllText(input, training.ToJsonString());
            var candidates = Path.Combine(directory, "candidates.json");
            File.WriteAllText(candidates, JsonSerializer.Serialize(new[] { new RootCandidate<PioneerRuntimeAction>(101, PioneerRuntimeAction.Train(101)) }, PioneerExecutionCommand.JsonOptions));
            var stoppedOutput = Path.Combine(directory, "stopped.json");
            Assert.Equal(3, Program.Main(["pioneer", "recommend", "--input", input, "--goal", Path.Combine(example, "power-goal.json"),
                "--candidates", candidates, "--seed", "71", "--samples", "131", "--workers", "4", "--output", stoppedOutput]));
            var stopped = JsonNode.Parse(File.ReadAllBytes(stoppedOutput))!;
            Assert.Null(stopped["recommendedAction"]);
            Assert.Null(stopped["search"]!["recommendedCandidateId"]);
            Assert.Equal(131, stopped["search"]!["candidates"]![0]!["batch"]!["results"]!.AsArray().Count);
            Assert.Null(stopped["search"]!["candidates"]![0]!["summary"]);

            training["start"]!["data"]!["characterEffects"] = new JsonArray(999);
            File.WriteAllText(input, training.ToJsonString());
            foreach (var command in new[] { "run", "recommend" })
            {
                var unknownOutput = Path.Combine(directory, $"unknown-{command}.json");
                var arguments = new List<string> { "pioneer", command, "--input", input, "--seed", "71", "--output", unknownOutput };
                if (command == "recommend") arguments.AddRange(["--goal", Path.Combine(example, "power-goal.json"),
                    "--candidates", candidates, "--samples", "131", "--workers", "4"]);
                Assert.Equal(3, Program.Main([.. arguments]));
                var unknown = JsonNode.Parse(File.ReadAllBytes(unknownOutput))!;
                Assert.Equal("initialization", unknown["phase"]!.GetValue<string>());
                Assert.Equal("unknownMechanism", unknown["reason"]!.GetValue<string>());
                Assert.Equal("stopped", unknown["status"]!.GetValue<string>());
            }
        }
        finally { Directory.Delete(directory, true); }
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
