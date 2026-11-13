using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
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
        var master = PioneerMasterReader.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var constructions = 0; var pendingUpgrades = 0; var autonomous = 0;
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, row.GetProperty("success").GetBoolean())], "construction-chain", master);
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecution.JsonOptions), 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 88, 0);
            var rootHeader = root.Header;
            var expectedCommand = row.GetProperty("pendingUpgrade").GetInt32() == 0 ? 0 : row.GetProperty("command").GetInt32();
            var first = SimulationRunner.Advance(scenario, script, ref execution);
            CheckTraining(row.GetProperty("afterTraining"), execution.State);
            Assert.Equal(expectedCommand, execution.State.Header.PendingTrainingLevelCommandId);
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
                Assert.Equal(expectedCommand, execution.State.Header.PendingTrainingLevelCommandId);
                SimulationRunner.ResetSample(scenario, script, root, ref execution, 88, 0);
                Assert.Equal(rootHeader, execution.State.Header);
                Assert.Equal(final, SimulationRunner.Continue(scenario, script, ref execution));
                var command = row.GetProperty("command").GetInt32();
                if (scenario.PreviewTraining(root, command).FailureRate == 0
                    && start.PlanningPeriods.All(period => period.CheckPointTurn != start.Turn))
                {
                    var calibration = new PioneerTurnCalibration([new(701, 1)],
                        [new(PioneerArrivalSource.Training, start.Turn, 0, 0, 0, 1, 0, 0)],
                        [new(PlacementMemberKind.Training, false, 0, 1, 10), new(PlacementMemberKind.Tucker, false, 0, 1, 10)]);
                    var turnRules = new PioneerTurnRules(calibration,
                        start.Commands.Select(item => (item.CommandId, item.Level, item.FailureCoefficient)),
                        start, 63, [], [.. master.FacilityStates.Select(facility => new TrainingFacility(
                            facility.FacilityId, facility.StateId, facility.FacilityLv, []))]);
                    var generated = PioneerRuntimeData.Compile(start, [], "autonomous-construction", master, turnRules: turnRules);
                    var generatedScenario = new PioneerScenario(generated, autonomous: true);
                    var future = generated.CreateInitialState();
                    var rulesRandom = new SimulationRandom(88);
                    var result = generatedScenario.ApplyAction(ref future, PioneerRuntimeAction.Train(command), ref rulesRandom);
                    Assert.True(result.ActionSettled, result.Stop.ToString());
                    CheckConstruction(row.GetProperty("afterConstruction"), future);
                    Assert.Equal(start.Turn + 1, future.Header.Turn);
                    Assert.Equal(0, future.Header.PendingTrainingLevelCommandId);
                    Assert.Empty(future.GetSection(PioneerStateSection.PendingStories).ToArray());
                    var levels = root.GetSection(PioneerStateSection.TrainingLevels).ToArray();
                    if (expectedCommand != 0) levels[Array.IndexOf(new[] { 101, 105, 102, 103, 106 }, expectedCommand)]++;
                    Assert.Equal(levels, future.GetSection(PioneerStateSection.TrainingLevels).ToArray());
                    autonomous++;
                }
                constructions++;
                if (middle.PendingTrainingLevelCommandId > 0) pendingUpgrades++;
            }
            else
            {
                Assert.Equal(TrajectoryStatus.Stopped, first.Status);
                Assert.Equal((int)(row.GetProperty("success").GetBoolean() ? PioneerStopCode.TrainingEventsRequired : PioneerStopCode.TrainingFailureEventRequired), first.Stop!.Value.Code);
            }
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
        var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterReader.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
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
            row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecution.JsonOptions), 0);
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
    public void CompiledConstructionKeepsPlanOrderIndependentOfFacilityStorageOrder()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases")[0];
        var training = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterReader.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
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
        }
    }

    [Fact]
    public void UnconsumedTrainingStopsKeepStateAndRandomStreamAcrossContinueAndFork()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-construction.json")));
        var row = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "threshold-300");
        var original = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions)!;
        // The public input accepts this value; it forces the unsupported multiple-threshold boundary.
        var start = original with { CheckpointBonusPercent = 500, Environment = original.Environment with { Current = original.Environment.Current with { Vital = 30 } } };
        using var database = new PioneerRuntimeComponentTests.FixtureMaster(PioneerRuntimeComponentTests.ConstructionTables());
        using var db = EvidenceAudit.OpenReadOnly(database.Path);
        var master = PioneerMasterReader.ConstructionData(db, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database.Path))));
        var data = PioneerRuntimeData.Compile(start, [], "pending-random", master);
        var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            row.GetProperty("script").Deserialize<ImmutableArray<ScriptedAction<PioneerRuntimeAction>>>(PioneerExecution.JsonOptions), 0);
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
        Assert.Equal(expected.GetProperty("resources").Deserialize<TrainingValues>(PioneerExecution.JsonOptions), state.Header.Resources);
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
