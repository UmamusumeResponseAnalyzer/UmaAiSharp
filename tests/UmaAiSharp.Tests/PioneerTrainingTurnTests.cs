using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingTurnTests
{
    [Fact]
    public void TrainingBondsIncludeDeckEffectsPresenceCharmAndCaps() => VerifyBondSettlement(Start());

    internal static void VerifyBondSettlement(PioneerStart.TrainingTurn original)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/training-bond-support.json")));
        var friend = fixture.RootElement.GetProperty("support").Deserialize<TrainingSupport>(PioneerExecution.JsonOptions)! with { Bond = 50 };
        foreach (var location in new[] { -1, 0, 1 })
        foreach (var charm in new[] { false, true })
        foreach (var success in new[] { false, true })
        {
            var deck = original.Deck.SetItem(0, original.Deck[0] with { Bond = 50 })
                .SetItem(1, original.Deck[1] with { Bond = 98 }).SetItem(5, friend);
            var start = original with { Deck = deck, CharacterEffects = charm ? [8] : [],
                Commands = [.. original.Commands.Select((command, index) => command with
                {
                    SupportPositions = index == 0 ? location == 0 ? [1, 2, 6] : [1, 2]
                        : index == 1 && location == 1 ? [6] : [],
                    HintPositions = [], GuestCount = 0
                })] };
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, success)], "training-bonds");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = Script(root, start.Commands[0].CommandId);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
            var random = execution.RulesRandom.State;
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            var after = execution.State.GetSection(PioneerStateSection.SupportBonds);
            var extra = 1 + (location == 0 ? 2 : 0) + (charm ? 2 : 0);
            Assert.Equal(success ? 57 + extra : 50, after[0]);
            Assert.Equal(success ? 100 : 98, after[1]);
            Assert.Equal(success && location == 0 ? 54 + extra : 50, after[5]);
            Assert.Equal(root.GetSection(PioneerStateSection.SupportBonds)[2..5].ToArray(), after[2..5].ToArray());
            Assert.Equal(random, result.RandomState.Rules);
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(50, root.GetSection(PioneerStateSection.SupportBonds)[0]);
        }
        var absent = original with { Deck = original.Deck.SetItem(5, friend with { UniqueEffects = [new(999, 1, 0, 0, 0, 0)] }),
            Commands = [.. original.Commands.Select(command => command with
            { SupportPositions = [.. command.SupportPositions.Where(position => position != 6)], HintPositions = [] })] };
        Assert.Throws<UnknownMechanismException>(() => PioneerRuntimeData.Compile(absent, [], "unknown-deck-effect"));
        Assert.Throws<UnknownMechanismException>(() => PioneerRuntimeData.Compile(absent with
        { Deck = absent.Deck.SetItem(5, friend with { UniqueEffects = [new(121, 1, 2, 1, 0, 0)] }) }, [], "unknown-bond-parameters"));
    }

    [Fact]
    public void RandomTrainingResultsAreStableAcrossWorkersAndForcedFailurePreservesResources()
    {
        var data = PioneerRuntimeData.Compile(Start(), [], "training-random");
        var root = data.CreateInitialState();
        var script = Script(root, 101);
        PioneerStateAudit.AssertDeterministic(data, script);
        var batch = SimulationRunner.RunBatch(new PioneerScenario(data), script, root, 893, 2049, 4);
        Assert.Contains(batch.Results, result => result.Stop!.Value.Code == (int)PioneerStopCode.TrainingEventsRequired);
        Assert.Contains(batch.Results, result => result.Stop!.Value.Code == (int)PioneerStopCode.TrainingFailureEventRequired);
        var forced = PioneerRuntimeData.Compile(Start(), [new PioneerConditionalOutcome.TrainingResult(0, false)], "training-forced");
        var scenario = new PioneerScenario(forced); var initial = forced.CreateInitialState();
        var execution = SimulationRunner.CreateExecution(scenario, script, initial, 893, 0);
        var random = execution.RulesRandom.State;
        var stopped = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.False(execution.State.Header.TrainingSucceeded);
        Assert.Equal(initial.Header.Resources, execution.State.Header.Resources);
        Assert.Equal(initial.Header.DevelopmentPoints, execution.State.Header.DevelopmentPoints);
        Assert.Equal(initial.GetSection(PioneerStateSection.SupportBonds).ToArray(), execution.State.GetSection(PioneerStateSection.SupportBonds).ToArray());
        Assert.Equal(4, execution.State.GetSection(PioneerStateSection.TrainingExecutionCounts)[0]);
        Assert.Equal(random, stopped.RandomState.Rules);
    }

    [Fact]
    public void DevelopmentPointsStartAtPlanningOpeningAndOverflowRemainsAPathMetric()
    {
        foreach (var turn in new[] { 1, 2, 3, 15 })
        {
            var original = Start();
            var start = original with { Turn = turn, CompletedCheckpoint = turn < 12 ? 0 : 1, CheckpointBonusPercent = turn < 12 ? 0 : 10,
                Environment = original.Environment with { Current = original.Environment.Current with { Speed = original.Environment.Limits.Speed } } };
            var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, true)], "training-metrics");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var preview = scenario.PreviewTraining(root, 101);
            Assert.Equal(0, preview.Gains.Common.Speed);
            Assert.True(preview.Gains.Overflow.Speed > 0);
            var script = Script(root, 101);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 71, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(turn < 3 ? 0 : turn == 3 ? 93 : 101, execution.State.Header.DevelopmentPoints - root.Header.DevelopmentPoints);
            Assert.Equal(preview.Gains.Overflow, execution.State.Header.FutureOverflow);
            Assert.True(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.FutureOverflow));
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
            Assert.Equal(preview.Gains.Overflow, execution.State.Header.FutureOverflow);
            Assert.Equal(default, root.Header.FutureOverflow);
        }
    }

    [Fact]
    public void TrainingBoundaryRejectsMissingFactsAndDisabledActionsAndRoundTripsThroughCli()
    {
        var start = Start();
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { PendingStoryIds = [12] }, [], "bad"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { CheckpointBonusPercent = null }, [], "bad"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { CompletedCheckpoint = 5, CheckpointBonusPercent = null }, [], "bad"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { PlanningPeriods = [] }, [], "bad"));
        foreach (var period in start.PlanningPeriods)
        {
            var count = period.CheckPointType - 1;
            _ = PioneerRuntimeData.Compile(start with { Turn = period.CheckPointTurn, CompletedCheckpoint = count,
                CheckpointBonusPercent = count == 0 ? 0 : 10 }, [], "before-checkpoint");
            count++;
            _ = PioneerRuntimeData.Compile(start with { Turn = period.CheckPointTurn + 1, CompletedCheckpoint = count,
                CheckpointBonusPercent = count == 5 ? null : 10 }, [], "after-checkpoint");
        }
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with { Commands = start.Commands.RemoveAt(0) }, [], "bad"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { Environment = start.Environment with { GoldShipBoost = new(101) } }, [], "bad"));
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { Commands = start.Commands.SetItem(1, start.Commands[1] with { SupportPositions = [1, 2] }) }, [], "bad"));
        _ = PioneerRuntimeData.Compile(start with
        { Commands = start.Commands.SetItem(0, start.Commands[0] with { GuestCount = 3 }) }, [], "five-participants");
        Assert.Throws<InvalidDataException>(() => PioneerRuntimeData.Compile(start with
        { Commands = start.Commands.SetItem(0, start.Commands[0] with { GuestCount = 4 }) }, [], "six-participants"));
        _ = PioneerRuntimeData.Compile(start with
        { CardId = 100701, Environment = start.Environment with { GoldShipBoost = new(101) } }, [], "goldship");
        start = start with { Commands = start.Commands.SetItem(0, start.Commands[0] with { Enabled = false }) };
        var data = PioneerRuntimeData.Compile(start, [], "disabled"); var root = data.CreateInitialState();
        Assert.NotNull(new PioneerScenario(data).ValidateAction(root, PioneerRuntimeAction.Train(101)));
        var directory = Path.Combine(Path.GetTempPath(), "UmaAiSharp-training-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = new PioneerExecutionInput(1, "training-cli", PioneerExecutionMode.Conditional,
                new("trainingTurn", JsonSerializer.SerializeToElement(start, PioneerExecution.JsonOptions)),
                [new("trainingResult", JsonSerializer.SerializeToElement(new { actionOrdinal = 0, success = true }))],
                [new(new((int)PioneerStage.TrainingTurn, 0, PioneerScenario.DecisionKey(start.Turn, 0)), PioneerRuntimeAction.Train(105))]);
            var path = Path.Combine(directory, "input.json"); var output = Path.Combine(directory, "output.json");
            File.WriteAllText(path, JsonSerializer.Serialize(input, PioneerExecution.JsonOptions));
            Assert.Equal("stopped", PioneerExecution.Run(path, 77, output, PioneerExecutionMode.Conditional).Status);
            using var report = JsonDocument.Parse(File.ReadAllText(output));
            Assert.True(report.RootElement.GetProperty("finalState").GetProperty("header").GetProperty("trainingSucceeded").GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }

    static ActionScript<PioneerDecisionView, PioneerRuntimeAction> Script(PioneerState root, int command)
        => new([new(new((int)PioneerStage.TrainingTurn, 0, PioneerScenario.DecisionKey(root.Header.Turn, 0)), PioneerRuntimeAction.Train(command))], 0);

    [Fact]
    public void GeneratedAssignmentsUseTheSameTrainingExecutionAndRemainValueOwned()
    {
        var start = Start();
        var members = start.Deck.Select(card => new PlacementMember(card.Position, true,
            new(new(card.TrainingCommandId == 0 ? PlacementMemberKind.Tucker : PlacementMemberKind.Training,
                card.TrainingCommandId == 0 ? -1 : Array.IndexOf(new[] { 101, 105, 102, 103, 106 }, card.TrainingCommandId),
                false, 10, 0, 0, 0, 0, 0, 0, 0, false, start.Turn, [])))).ToList();
        var npc = new PioneerPlacementDistribution(new(PlacementMemberKind.ProjectNpc, -1, false, 10,
            0, 0, 0, 0, 0, 0, 0, false, start.Turn, []));
        members.AddRange(Enumerable.Range(1001, 6).Select(id => new PlacementMember(id, true, npc)));
        var model = new PioneerJointPlacement([.. members]);
        var random = new SimulationRandom(821);
        var board = model.Sample(new SupportPlacement[members.Count], ref random);
        start = start with { Commands = [.. start.Commands.Select((command, index) => command with
        {
            SupportPositions = [.. Enumerable.Range(1, 6).Where(position => (board[index].SupportMask & (1 << (position - 1))) != 0)],
            HintPositions = [.. Enumerable.Range(1, 6).Where(position => (board[index].HintMask & (1 << (position - 1))) != 0)],
            GuestCount = board[index].GuestCount
        })] };
        var data = PioneerRuntimeData.Compile(start, [new PioneerConditionalOutcome.TrainingResult(0, true)], "joint-action");
        var scenario = new PioneerScenario(data);
        var root = data.CreateInitialState();
        var visible = scenario.Inspect(root, 0).Decision.Context;
        Assert.Equal(board, root.Header.TrainingBoard);
        Assert.Equal(board, visible.TrainingBoard);
        Assert.True(visible.KnownParts.HasFlag(PioneerKnownParts.TrainingBoard));
        var preview = scenario.PreviewTraining(root, 101);
        var script = Script(root, 101);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 77, 0);
        var fork = SimulationRunner.CreateExecution(scenario, script, root, 78, 0);
        SimulationRunner.ForkInto(scenario, script, execution, ref fork);
        Assert.Equal(SimulationRunner.Continue(scenario, script, ref execution), SimulationRunner.Continue(scenario, script, ref fork));
        Assert.Equal(start.Environment.Current.Speed + preview.Gains.Common.Speed + preview.Gains.Scenario.Speed, execution.State.Header.Resources.Speed);
        Assert.Equal(board, visible.TrainingBoard);
        Assert.Equal(board, root.Header.TrainingBoard);
        Assert.Equal(start.Environment.Current, root.Header.Resources);
    }

    static PioneerStart.TrainingTurn Start()
    {
        int[] commands = [101, 105, 102, 103, 106];
        var deck = commands.Select((command, index) => new TrainingSupport(30001 + index, 50, index + 1, command, 80,
            [new(1, 20), new(2, 20)], [])).Append(new(30257, 50, 6, 0, 50, [new(27, 20)], [])).ToImmutableArray();
        var options = commands.Select((command, index) => new TrainingCommandOption(command, 1, 3, 1,
            command == 101 ? [1, 6] : [index + 1], command == 101 ? [1] : [], true, 720, 10)).ToImmutableArray();
        return new(15, 1, 100101, new(5, 0, new(1195, 100, 100, 100, 100, 50, 20), new(1800, 1800, 1800, 1800, 1800, 100), new(), null),
            deck, options, [8], [], 600, 1, 10,
            [new(16, 3, 1, 3, 12, 400), new(17, 3, 2, 12, 24, 500), new(18, 3, 3, 24, 36, 600),
                new(19, 3, 4, 36, 48, 600), new(20, 3, 5, 48, 60, 800)]);
    }
}
