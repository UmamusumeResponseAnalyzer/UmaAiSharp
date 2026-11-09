using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.Cli;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingConstructionTests
{
    [Fact]
    public void CapturedTrainingCrossingsReachConstructionBeforeUpgradesAndSettleItsKnownEffects()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterCompiler.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var constructions = 0; var pendingUpgrades = 0; var autonomous = 0;
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, row.GetProperty("success").GetBoolean())], "construction-chain", master);
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecutionCommand.JsonOptions), 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 88, 0);
            var rootHeader = root.Header;
            var first = SimulationRunner.Advance(scenario, script, ref execution);
            CheckTraining(row.GetProperty("afterTraining"), execution.State);
            Assert.Equal(row.GetProperty("pendingUpgrade").GetInt32(), execution.State.Header.PendingTrainingLevelEventId);
            Assert.Equal(rootHeader, root.Header);
            if (row.GetProperty("construction").GetBoolean())
            {
                Assert.Equal(TrajectoryStatus.Advanced, first.Status);
                Assert.Equal((1, 1), (first.Progress.ConsumedActions, first.Progress.SettledActions));
                var decision = scenario.Inspect(execution.State, first.Progress.DecisionOrdinal);
                Assert.Equal(new PioneerEvent(211067, 0, 400011107, 6), decision.Decision.Context.Event);
                Assert.Equal(new DecisionId((int)PioneerStage.ConstructionEvent, 1, PioneerScenario.DecisionKey(start.Turn, 211067)), decision.Decision.Id);
                Assert.Equal([400011107], execution.State.GetSection(PioneerStateSection.PendingStories).ToArray());
                Assert.NotNull(scenario.ValidateAction(execution.State, PioneerRuntimeAction.Train(row.GetProperty("command").GetInt32())));
                Assert.NotNull(scenario.ValidateAction(execution.State, PioneerRuntimeAction.Choose(execution.State.Header.Event, 1)));
                var fork = SimulationRunner.CreateExecution(scenario, script, root, 12, 3);
                SimulationRunner.ForkInto(scenario, script, execution, ref fork);
                var middle = execution.State.Header;
                var final = SimulationRunner.Continue(scenario, script, ref execution);
                Assert.Equal(middle, fork.State.Header);
                Assert.Equal((int)PioneerStopCode.ConstructionRemainderUnknown, final.Stop!.Value.Code);
                Assert.Equal((2, 1), (final.Progress.ConsumedActions, final.Progress.SettledActions));
                Assert.Equal(first.RandomState, final.RandomState);
                CheckConstruction(row.GetProperty("afterConstruction"), execution.State);
                Assert.Equal(final, SimulationRunner.Continue(scenario, script, ref fork));
                Assert.Equal(execution.State.Header, fork.State.Header);
                Assert.Equal(final, SimulationRunner.Continue(scenario, script, ref execution));
                Assert.Equal(row.GetProperty("pendingUpgrade").GetInt32(), execution.State.Header.PendingTrainingLevelEventId);
                SimulationRunner.ResetSample(scenario, script, root, ref execution, 88, 0);
                Assert.Equal(rootHeader, execution.State.Header);
                Assert.Equal(final, SimulationRunner.Continue(scenario, script, ref execution));
                if (scenario.PreviewTraining(root, row.GetProperty("command").GetInt32()).FailureRate == 0)
                {
                    var generated = PioneerRuntimeData.Compile(start, [], "autonomous-construction", master);
                    var generatedScenario = new PioneerScenario(generated);
                    var future = SimulationRunner.CreateExecution(generatedScenario, script, generated.CreateInitialState(), 88, 0);
                    Assert.Equal(final, SimulationRunner.Continue(generatedScenario, script, ref future));
                    CheckConstruction(row.GetProperty("afterConstruction"), future.State);
                    Assert.Equal(0, future.State.Header.ConditionalOutcomeCount);
                    autonomous++;
                }
                constructions++;
                if (middle.PendingTrainingLevelEventId > 0) pendingUpgrades++;
            }
            else
            {
                Assert.Equal(TrajectoryStatus.Stopped, first.Status);
                Assert.Equal((int)(row.GetProperty("success").GetBoolean() ? PioneerStopCode.TrainingEventsRequired : PioneerStopCode.TrainingFailureEventRequired), first.Stop!.Value.Code);
            }
            PioneerStateAudit.AssertDeterministic(data, script);
        }
        Assert.Equal(9, constructions);
        Assert.True(pendingUpgrades > 0);
        Assert.True(autonomous > 0);
    }

    [Fact]
    public void MissingFactsStayUnknownAndConflictingDevelopmentIsRejectedBeforeExecution()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases")[0];
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterCompiler.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start, [], "missing-master"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { Development = start.Development! with { DevelopmentPoints = start.DevelopmentPoints + 1 } }, [], "conflict", master));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { Development = start.Development! with { PendingStoryIds = [400011107] } }, [], "future-queue", master));
        var unknown = start with { Development = start.Development! with { IslandTrainingTickets = null } };
        var data = PioneerRuntimeData.Compile(unknown, [new PioneerConditionalOutcome.TrainingResult(0, true)], "unknown-tickets", master);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        Assert.False(root.Header.KnownParts.HasFlag(PioneerKnownParts.Tickets));
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecutionCommand.JsonOptions), 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 7, 0);
        Assert.Equal(TrajectoryStatus.Advanced, SimulationRunner.Advance(scenario, script, ref execution).Status);
        var before = execution.State.Header;
        var facilities = execution.State.GetSection(PioneerStateSection.FacilityStates).ToArray();
        var stopped = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(TrajectoryStatus.Stopped, stopped.Status);
        Assert.Equal((1, 1), (stopped.Progress.ConsumedActions, stopped.Progress.SettledActions));
        Assert.Equal(before, execution.State.Header);
        Assert.Equal(facilities, execution.State.GetSection(PioneerStateSection.FacilityStates).ToArray());
    }

    [Fact]
    public void TrainingAndCurrentConstructionRejectInconsistentPlansAtCompilation()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases")[0];
        var training = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterCompiler.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var state = training.Development!;
        var invalid = new[]
        {
            state with { IslandTrainingThresholds = [1, 2] },
            state with { IslandTrainingThresholds = [] },
            state with { Facilities = state.Facilities.RemoveAt(3) },
            state with { Facilities = state.Facilities.SetItem(3, new(104, 99999)) },
            state with { Facilities = state.Facilities.SetItem(3, state.Facilities[0]) },
            state with { Facilities = state.Facilities.SetItem(0, new(101, 10120)) },
            state with { PlanningCost = 0 },
            state with { PlanningCost = state.PlanningCost + 1 },
        };
        var current = new PioneerEvent(211067, 0, 400011107, 6);
        foreach (var bad in invalid)
        {
            Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(training with { Development = bad }, [], "invalid-training", master));
            var building = bad with { RawPlayingState = 5, DevelopmentPoints = row.GetProperty("afterTraining").GetProperty("points").GetInt32(), PendingStoryIds = [400011107] };
            Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(new PioneerStart.Construction(building, current), [], "invalid-building", master));
        }
    }

    [Fact]
    public void CompiledConstructionKeepsPlanOrderIndependentOfFacilityStorageOrder()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases")[0];
        var training = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterCompiler.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var rules = new PioneerConstructionRules(master);
        var before = training.Development! with { RawPlayingState = 5,
            DevelopmentPoints = row.GetProperty("afterTraining").GetProperty("points").GetInt32(), PendingStoryIds = [400011107] };
        var current = new PioneerEvent(211067, 0, 400011107, 6);
        foreach (var reverse in new[] { false, true })
        for (var rotation = 0; rotation < before.Facilities.Length; rotation++)
        {
            var state = before with
            {
                Facilities = [.. before.Facilities.Skip(rotation), .. before.Facilities.Take(rotation)],
                Plan = reverse ? new(before.Plan.Steps.Reverse().Select((step, index) => step with { Priority = index + 1 })) : before.Plan,
            };
            var expected = rules.ApplySettlementComponentsCandidate(state).After;
            var data = PioneerRuntimeData.Compile(new PioneerStart.Construction(state, current), [], "ordered-construction", master);
            Assert.EndsWith(master.SourceSha256, data.RulesIdentity);
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(current, 0))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 83, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal((int)PioneerStopCode.ConstructionRemainderUnknown, result.Stop!.Value.Code);
            Assert.Equal(expected.Facilities.SelectMany(value => new[] { value.FacilityId, value.StateId }),
                execution.State.GetSection(PioneerStateSection.FacilityStates).ToArray());
            Assert.Equal(expected.IslandTrainingTickets, execution.State.Header.Tickets);
            Assert.Equal(state.Facilities.SelectMany(value => new[] { value.FacilityId, value.StateId }), root.GetSection(PioneerStateSection.FacilityStates).ToArray());
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            PioneerStateAudit.AssertDeterministic(data, script);
        }
    }

    [Fact]
    public void UnconsumedTrainingStopsKeepStateAndRandomStreamAcrossContinueAndFork()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "threshold-300");
        var original = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecutionCommand.JsonOptions)!;
        // The public input accepts this value; it forces the unsupported multiple-threshold boundary.
        var start = original with { CheckpointBonusPercent = 500, Environment = original.Environment with { Current = original.Environment.Current with { Vital = 30 } } };
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterCompiler.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var data = PioneerRuntimeData.Compile(start, [], "pending-random", master);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecutionCommand.JsonOptions), 0);
        var stopped = 0; var consumed = 0;
        for (var seed = 0UL; seed < 32; seed++)
        {
            var execution = SimulationRunner.CreateExecution(scenario, script, root, seed, 0);
            var random = execution.RulesRandom.State;
            var first = SimulationRunner.Advance(scenario, script, ref execution);
            if (first.Progress.ActionConsumed)
            {
                Assert.Equal((int)PioneerStopCode.TrainingFailureEventRequired, first.Stop!.Value.Code);
                Assert.NotEqual(random, first.RandomState.Rules);
                consumed++;
                continue;
            }
            Assert.Equal((int)PioneerStopCode.UnsupportedComponentInput, first.Stop!.Value.Code);
            Assert.Equal(random, first.RandomState.Rules);
            Assert.Equal(root.Header, execution.State.Header);
            foreach (var section in Enum.GetValues<PioneerStateSection>())
                Assert.Equal(root.GetSection(section).ToArray(), execution.State.GetSection(section).ToArray());
            var fork = SimulationRunner.CreateExecution(scenario, script, root, 999, 1);
            SimulationRunner.ForkInto(scenario, script, execution, ref fork);
            Assert.Equal(first, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(first, SimulationRunner.Continue(scenario, script, ref fork));
            stopped++;
        }
        Assert.True(stopped > 0 && consumed > 0);
    }

    static void CheckTraining(JsonElement expected, PioneerState state)
    {
        Assert.Equal(expected.GetProperty("resources").Deserialize<TrainingValues>(PioneerExecutionCommand.JsonOptions), state.Header.Resources);
        Assert.Equal(expected.GetProperty("motivation").GetInt32(), state.Header.Motivation);
        Assert.Equal(expected.GetProperty("points").GetInt32(), state.Header.DevelopmentPoints);
        Assert.Equal(expected.GetProperty("levels").Deserialize<int[]>(), state.GetSection(PioneerStateSection.TrainingLevels).ToArray());
        CheckConstruction(expected, state);
    }

    static void CheckConstruction(JsonElement expected, PioneerState state)
    {
        Assert.Equal(expected.GetProperty("tickets").GetInt32(), state.Header.Tickets);
        var actual = state.GetSection(PioneerStateSection.FacilityStates).ToArray();
        var expectedFacilities = expected.GetProperty("facilities").EnumerateArray()
            .SelectMany(facility => new[] { facility.GetProperty("facility_id").GetInt32(), facility.GetProperty("state_id").GetInt32() }).ToArray();
        Assert.Equal(expectedFacilities, actual);
    }
}
