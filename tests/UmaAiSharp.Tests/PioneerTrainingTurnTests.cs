using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.Base.Races;
using UmaAiSharp.Base.Training;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class PioneerTrainingTurnTests
{
    static readonly PioneerTurnObservation AfterSapporo = new(PioneerArrival.OtherPhase, Turn: 64, RawPlayingState: 5);

    [Theory]
    [InlineData("precise")]
    [InlineData("fast")]
    public void TeamBondUniqueIncludesTheFourAbsentCards(string model)
    {
        var start = Start();
        var card = start.Deck[0] with { CardId = 30099, TrainingCommandId = 103 };
        var total = start.Deck.Sum(support => support.Bond);
        TrainingNumericResult Preview(TrainingSupport support)
        {
            var panel = start with { Deck = start.Deck.SetItem(0, support) };
            var data = PioneerRuntimeData.Compile(panel, [], "team-bond", model: PioneerModel.Resolve(model));
            return new PioneerScenario(data).PreviewTraining(data.CreateInitialState(), 101).Gains;
        }
        var expected = Preview(card with { Effects = card.Effects.Add(new(8, total / 30)) });
        Assert.NotEqual(Preview(card), expected);
        Assert.Equal(expected, Preview(card with { UniqueEffects = [new(109, 8, 30, 0, 0, 0)] }));
    }

    [Theory]
    [InlineData(30021, 1, 40, 0, 3, true)]
    [InlineData(30021, 4, 56, 40, 5, false)]
    [InlineData(10021, 4, 49, 18, 5, false)]
    public void TazunaOutingsUseEquippedCardAndVisibleProgress(int cardId, int step, int recovery, int points, int mood, bool cured)
    {
        var start = Start();
        var ssr = cardId == 30021;
        var friend = start.Deck[5] with { CardId = cardId, Effects = [new(25, ssr ? 60 : 40), new(26, ssr ? 35 : 25)] };
        start = start with { Deck = start.Deck.SetItem(5, friend), CharacterEffects = [4],
            Environment = start.Environment with { Motivation = 3, Current = start.Environment.Current with { Vital = 20 } },
            HomeActions = new(false, false, true, [new(6, 9001, true, step)], [], [], 0) };
        var calibration = new PioneerTurnCalibration([new(701, 1)], [new(PioneerArrivalSource.RestConfirmation, start.Turn, 1, 0, 0, 0, 0, 0)],
            [new(PlacementMemberKind.Training, false, 0, 1, 10), new(PlacementMemberKind.Tucker, false, 0, 1, 10)]);
        var rules = new PioneerTurnRules(calibration, start.Commands.Select(c => (c.CommandId, c.Level, c.FailureCoefficient)), start, 63, [], []);
        var data = PioneerRuntimeData.Compile(start, [], "tazuna-outing", turnRules: rules);
        var state = data.CreateInitialState();
        var random = new SimulationRandom(42);
        new PioneerScenario(data).ApplyAction(ref state, new(PioneerActionKind.Outing, 390, SupportPosition: 6), ref random);
        Assert.Equal(20 + recovery, state.Header.Resources.Vital);
        Assert.Equal(20 + points, state.Header.Resources.SkillPoints);
        Assert.Equal(mood, state.Header.Motivation);
        Assert.Equal(cured, !state.GetSection(PioneerStateSection.CharacterEffects).Contains(4));
    }

    [Fact]
    public void SlowMetabolismSuppressesTrainingSpeedIncludingOverflow()
    {
        var start = Start();
        var data = PioneerRuntimeData.Compile(start with { CharacterEffects = [4] }, [], "slow-metabolism");
        var gains = new PioneerScenario(data).PreviewTraining(data.CreateInitialState(), 101).Gains;
        Assert.Equal(0, gains.Common.Speed + gains.Scenario.Speed);
        Assert.Equal(0, gains.UncappedCommon.Speed + gains.UncappedScenario.Speed);
        Assert.True(gains.Common.Power > 0);
    }

    [Fact]
    public void SapporoKinenReplayUsesTheObservedRewardAcrossSeeds()
    {
        var data = SapporoReplay(2, 549, new PioneerConditionalOutcome.TargetRaceWin(0, 82, 7000, AfterSapporo,
            ExcludedAttribute: TrainingAttribute.Wisdom));
        var scenario = new PioneerScenario(data);
        PioneerState Settle(ulong seed)
        {
            var state = data.CreateInitialState();
            var random = new SimulationRandom(seed);
            var result = scenario.ApplyAction(ref state, new(PioneerActionKind.Race, 82), ref random);
            Assert.True(result.ActionSettled);
            Assert.Equal(seed, random.State);
            return state;
        }
        var first = Settle(5);
        var second = Settle(927);
        Assert.Equal(new TrainingValues(1198, 103, 103, 103, 100, 50, 55), first.Header.Resources);
        Assert.Equal(9000, first.Header.Fans);
        Assert.Equal(82, first.GetSection(PioneerStateSection.RaceWins)[64]);
        Assert.Equal(first.Header, second.Header);
        foreach (var section in Enum.GetValues<PioneerStateSection>())
            Assert.Equal(first.GetSection(section).ToArray(), second.GetSection(section).ToArray());
    }

    [Theory]
    [InlineData(1, 549, null, false, PioneerStopCode.UnsupportedComponentInput, "excludedAttribute")]
    [InlineData(3, 549, null, true, PioneerStopCode.UnsupportedComponentInput, "excludedAttribute")]
    [InlineData(0, 549, TrainingAttribute.Guts, false, PioneerStopCode.UnsupportedComponentInput, "比赛脚质")]
    [InlineData(4, 549, null, false, PioneerStopCode.UnsupportedComponentInput, "追込")]
    [InlineData(2, 0, TrainingAttribute.Wisdom, false, PioneerStopCode.OutcomeIdentityMismatch, "excludedAttribute")]
    public void SapporoKinenReplayStopsBeforeApplyingAnUnverifiedReward(int style, int goal, TrainingAttribute? excluded,
        bool arrivalOnly, PioneerStopCode stop, string detail)
    {
        var data = SapporoReplay(style, goal, arrivalOnly ? new PioneerConditionalOutcome.TurnArrival(0, AfterSapporo)
            : new PioneerConditionalOutcome.TargetRaceWin(0, 82, 7000, AfterSapporo, ExcludedAttribute: excluded));
        var before = data.CreateInitialState();
        var state = data.CreateInitialState();
        var random = new SimulationRandom(5);
        var result = new PioneerScenario(data).ApplyAction(ref state, new(PioneerActionKind.Race, 82), ref random);
        Assert.False(result.ActionConsumed);
        Assert.False(result.ActionSettled);
        Assert.Equal((int)stop, result.Stop!.Value.Code);
        Assert.Contains(detail, result.Stop.Value.Detail);
        Assert.Equal(5ul, random.State);
        Assert.Equal(before.Header, state.Header);
        foreach (var section in Enum.GetValues<PioneerStateSection>())
            Assert.Equal(before.GetSection(section).ToArray(), state.GetSection(section).ToArray());
    }

    [Fact]
    public void SapporoKinenReplayListsTheMandatoryRaceBeforeTheResultIsKnown()
    {
        var data = SapporoReplay(1, 549, null);
        var scenario = new PioneerScenario(data);
        var state = data.CreateInitialState();
        var action = Assert.Single(scenario.Candidates(state)).Action;
        Assert.Equal(new(PioneerActionKind.Race, 82), action);
        Assert.Null(scenario.ValidateAction(state, action));
        var random = new SimulationRandom(5);
        var result = scenario.ApplyAction(ref state, action, ref random);
        Assert.False(result.ActionConsumed);
        Assert.Contains("excludedAttribute", result.Stop!.Value.Detail);
    }

    static PioneerRuntimeData SapporoReplay(int style, int goal, PioneerConditionalOutcome? outcome)
    {
        var race = new RaceOption(82, 200, 7000, true, "札幌記念", 2000, goal);
        var original = Start();
        var start = original with { Turn = 64, CardId = 102201, CharacterId = 1022, CompletedCheckpoint = 5,
            CheckpointBonusPercent = null, Environment = original.Environment with { Fans = 2000 },
            HomeActions = new(false, false, false, [], [race], [new(64, race)], 0) { RunningStyle = style } };
        var calibration = new PioneerTurnCalibration([new(701, 1)], [new(PioneerArrivalSource.Race, 64, 1, 0, 0, 0, 0, 0)],
            [new(PlacementMemberKind.Training, false, 0, 1, 10), new(PlacementMemberKind.Tucker, false, 0, 1, 10)]);
        var rules = new PioneerTurnRules(calibration, start.Commands.Select(c => (c.CommandId, c.Level, c.FailureCoefficient)), start, 63, [], []);
        return PioneerRuntimeData.Compile(start, outcome is null ? [] : [outcome], "sapporo-replay", turnRules: rules);
    }

    [Fact]
    public void ForecastUsesVisibleRootLayerAndKeepsEstimatedLayerWithinCap()
    {
        var start = Start();
        var speed = start.Commands[0];
        var observed = new ObservedTrainingPreview(null, new(5, 0, 0, 0, 0, 0, 0), 37);
        start = start with { Environment = start.Environment with { Limits = start.Environment.Limits with { Speed = 1200 } },
            Commands = start.Commands.SetItem(0, speed with { Observed = observed }) };
        var data = PioneerRuntimeData.Compile(start, [], "observed");
        var root = data.CreateInitialState();
        var forecast = new PioneerScenario(data, autonomous: true).PreviewTraining(root, speed.CommandId);
        Assert.Equal(37, forecast.FailureRate);
        Assert.Equal(0, forecast.Gains.Common.Speed);
        Assert.Equal(5, forecast.Gains.Scenario.Speed);
        Assert.True(new PioneerScenario(data).PreviewTraining(root, speed.CommandId).Gains.Common.Speed > 0);
    }

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
