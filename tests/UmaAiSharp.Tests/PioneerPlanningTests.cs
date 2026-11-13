using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class PioneerPlanningTests
{
    [Fact]
    public void CapturedPlansRunThroughValidationScriptForkAndSubmission()
    {
        var master = Master();
        var rules = new PioneerConstructionRules(master);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "pioneer-planning-snapshots.json")));
        foreach (var window in fixture.RootElement.GetProperty("windows").EnumerateArray())
        {
            var before = window.GetProperty("before").Deserialize<PioneerDevelopmentState>(PioneerExecution.JsonOptions)!;
            var expected = window.GetProperty("after").Deserialize<PioneerDevelopmentState>(PioneerExecution.JsonOptions)!;
            var plan = window.GetProperty("submittedPlan").Deserialize<PioneerConstructionPlan>(PioneerExecution.JsonOptions)!;
            rules.ValidateForSubmission(before, plan);
            var candidates = rules.EnumeratePlans(before).ToArray();
            Assert.Contains(candidates, candidate => candidate.SemanticId == plan.SemanticId);
            Assert.Equal(candidates.Length, candidates.Select(candidate => candidate.SemanticId).Distinct().Count());
            foreach (var candidate in candidates) rules.ValidateForSubmission(before, candidate);
            // Repeated enumeration must preserve candidate order.
            var plans = rules.EnumeratePlans(before);
            Assert.Equal(candidates[0].SemanticId, plans.First().SemanticId);
            Assert.Equal(candidates.Select(candidate => candidate.SemanticId), plans.Select(candidate => candidate.SemanticId));

            var data = PioneerRuntimeData.Compile(new PioneerStart.Planning(before), [], master.SourceSha256, master);
            var scenario = new PioneerScenario(data);
            var root = data.CreateInitialState();
            var decision = scenario.Inspect(root, 0).Decision;
            Assert.Equal(before, decision.Context.Planning);
            var action = PioneerRuntimeAction.Submit(plan);
            Assert.Null(scenario.ValidateAction(root, action));
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>([new(decision.Id, action)], action.OwnedPayloadBytes);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 123, 0);
            var fork = SimulationRunner.CreateExecution(scenario, script, root, 999, 1);
            SimulationRunner.ForkInto(scenario, script, execution, ref fork);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref fork));
            Assert.Equal(execution.State.Header, fork.State.Header);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(1, result.Progress.SettledActions);
            Assert.Equal((int)PioneerStopCode.PlanningContinuationRequired, result.Stop!.Value.Code);
            Assert.Equal(expected.Turn, execution.State.Header.Turn);
            Assert.Equal(expected.RawPlayingState, execution.State.Header.RawPlayingState);
            Assert.Equal(expected.DevelopmentPoints, execution.State.Header.DevelopmentPoints);
            if (expected.IslandTrainingTickets is { } tickets) Assert.Equal(tickets, execution.State.Header.Tickets);
            Assert.Equal(expected.Facilities.SelectMany(value => new[] { value.FacilityId, value.StateId }),
                execution.State.GetSection(PioneerStateSection.FacilityStates).ToArray());
            Assert.Equal(plan.Steps.SelectMany(value => new[] { value.Priority, value.FacilityId, value.PreviousStateId, value.TargetStateId }),
                execution.State.GetSection(PioneerStateSection.ConstructionSteps).ToArray());
            Assert.Equal(before.Plan.Steps.SelectMany(value => new[] { value.Priority, value.FacilityId, value.PreviousStateId, value.TargetStateId }),
                root.GetSection(PioneerStateSection.ConstructionSteps).ToArray());
            Assert.Equal(PioneerStage.ConstructionPlanning, root.Header.Stage);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.True(execution.State.Header.TurnKnown && execution.State.Header.RawPlayingStateKnown);
        }
    }

    [Fact]
    public void SubmissionRejectsInvalidEdgesBudgetsAndRepeatedFacilities()
    {
        var master = Master();
        var rules = new PioneerConstructionRules(master);
        var before = new PioneerDevelopmentState(3, 35, 0, 400, 3, [200, 400], 0,
            [.. master.FacilityKinds.Select(facility => new ObservedFacility(facility.FacilityId, facility.FacilityId * 100))], new([]), []);
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(before, new([])));
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(before, new([new(1, 101, 10100, 10120)])));
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(before, new([new(1, 101, 10110, 10120)])));
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(before,
            new([new(1, 101, 10100, 10110), new(2, 101, 10100, 10110)])));
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(before,
            new(Enumerable.Range(101, 5).Select((id, index) => new PlannedUpgrade(index + 1, id, id * 100, id * 100 + 10)))));
        Assert.Throws<IllegalActionException>(() => rules.ValidatePlanningState(before with { RawPlayingState = 1 }));

        // The selected model permits skipping affordable skilled-route extensions.
        var skilled = before with
        {
            Turn = 48, PlanningCost = 800,
            Facilities = [new(101, 10142), new(102, 10251), new(103, 10351), new(104, 10451), new(105, 10551), new(106, 10630)]
        };
        rules.ValidateForSubmission(skilled, new([]));
        var optional = rules.EnumeratePlans(skilled).ToArray();
        Assert.Equal(2, optional.Length);
        Assert.Empty(optional[0].Steps);
        Assert.Equal(10152, Assert.Single(optional[1].Steps).TargetStateId);
        Assert.Throws<IllegalActionException>(() => rules.ValidateForSubmission(skilled with
        { Facilities = skilled.Facilities.SetItem(0, new(101, 10141)) }, new([])));
    }

    static PioneerMasterData Master()
    {
        using var reader = File.OpenText(Path.Combine(Repository(), "src", "UmaAiSharp.Pioneer", "Evidence", "construction-master.jsonl"));
        return PioneerMasterData.ReadExport(reader, PioneerConstructionRules.ObservedMasterSha256);
    }
    static string Repository([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
}
