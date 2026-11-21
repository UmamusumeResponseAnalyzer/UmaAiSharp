using UmaAiSharp.Core;
using UmaAiSharp.Base.Races;

namespace UmaAiSharp.Tests;

public sealed class RaceRouteTests
{
    static RaceRouteCatalog Catalog(int scenario, int[] groups) => new(
        [new(122, 0, 1135, 1135, 0, 0)],
        [new(1523, 1135, 100, 1, 5, 44, 0, 1, 168, 3, 0, 1, 0, 99, 0, 99),
            new(1524, 1135, 100, 1, 5, 44, 0, 1, 76, 2, 0, 2, 113501, 0, 0, 0),
            new(1530, 1135, 701, 3, 11, 74, 1, 1, 10001, 1, 0, 0, 0, 0, 0, 0),
            new(9999, 9999, 100, 1, 1, 12, 0, 1, 1067, 0, 0, 0, 0, 0, 0, 0)],
        new Dictionary<int, int> { [113501] = 1135, [106001] = 1060 }, scenario, groups);

    [Fact]
    public void SelectedStatePreservesConditionalAlternativeAndFinalRaceGroup()
    {
        var catalog = Catalog(11, [100, 701]);
        var first = catalog.ResolveSelected(113501, 122, [1523, 1530]);
        var second = catalog.ResolveSelected(113501, 122, [1524, 1530]);
        Assert.Equal(new[] { 1523, 1530 }, first.Select(x => x.Id));
        Assert.Equal(113501, second[0].DetermineRaceFlag);
        Assert.Equal(10001, second[1].ConditionId);
        Assert.Equal(1523, first[0].Id);
    }

    [Fact]
    public void SharedCharacterRouteUsesTheCurrentScenariosGoalGroups()
    {
        var catalog = Catalog(10, [10, 100, 601, 1801, 1901]);
        Assert.Equal(1523, Assert.Single(catalog.ResolveSelected(113501, 122, [1523])).Id);
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, [1530]));
    }

    [Fact]
    public void UnavailableOrForeignGoalsNeverBecomeAnAutomaticRoute()
    {
        var catalog = Catalog(11, [100, 701]);
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(106001, 122, [1523]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, [9999]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, [1523, 1523]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, []));
        Assert.Throws<UnknownMechanismException>(() => catalog.ResolveSelected(113501, 122, [9998]));
    }

    [Theory]
    [InlineData(0, 293, 95)]
    [InlineData(3, 292, 294)]
    [InlineData(10, 90, 294)]
    [InlineData(11, 293, 295)]
    public void FukukitaruDrawsReachTheCorrespondingCareerGoals(int seed, int debutGoal, int takarazukaGoal)
    {
        var edits = CharacterRaceRoutes.GoalEdits(1056);
        var debut = new SimulationRandom((ulong)seed);
        var takarazuka = debut;
        Assert.Equal(debutGoal, CharacterRaceRoutes.BranchAfterWin(1056, 89, ref debut));
        Assert.Equal(takarazukaGoal, CharacterRaceRoutes.BranchAfterWin(1056, 94, ref takarazuka));
        Assert.Contains(new CharacterGoalEdit(debutGoal, AddedGoal: debutGoal), edits);
        Assert.Contains(new CharacterGoalEdit(takarazukaGoal, AddedGoal: takarazukaGoal), edits);
    }

    [Fact]
    public void DeterministicCharacterGoalsRespectMotivationCancellationsAndAdditions()
    {
        var random = new SimulationRandom(123);
        Assert.Null(CharacterRaceRoutes.BranchAfterWin(1032, 183, ref random));
        Assert.Equal(123UL, random.State);
        Assert.Equal(new CharacterGoalEdit(296, 184, 296), Assert.Single(CharacterRaceRoutes.GoalEdits(1032)));
        Assert.Equal(new CharacterGoalEdit(0, 627, BlockRemovedTurn: true), Assert.Single(CharacterRaceRoutes.GoalEdits(1062)));
        Assert.Equal([new CharacterGoalEdit(0, 1523, 1524), new(0, AddedGoal: 1529)], CharacterRaceRoutes.GoalEdits(1135).AsSpan().ToArray());
        Assert.Empty(CharacterRaceRoutes.GoalEdits(1001));
    }

    [Theory]
    [InlineData(3, 296)]
    [InlineData(4, null)]
    public void TachyonReceivesWisdomWhenTheReportIsConfirmedAfterSatsuki(int raceMotivation, int? nextGoal)
    {
        var races = new Dictionary<int, RaceOption>();
        Assert.Equal(default, CharacterRaceRoutes.OnTurnStart(1032, 31, raceMotivation, [], races));
        var report = CharacterRaceRoutes.FromStory(501032402, null, raceMotivation);
        Assert.Equal(nextGoal, report.NextGoal);
        Assert.Equal(5, report.Reward.Wisdom);
        Assert.Equal(report, CharacterRaceRoutes.OnTurnStart(1032, 32, raceMotivation, [], races));
        Assert.Equal(5, CharacterRaceRoutes.OnTurnStart(1062, 70, 0, [], races).Reward.Guts);
    }

    [Fact]
    public void BrianQualificationCountsClassicWinsAndTheSelectedRouteRemainsObservable()
    {
        var wins = new int[73];
        wins[24] = wins[49] = 79;
        wins[31] = 163;
        wins[34] = 166;
        wins[48] = 79;
        var races = new Dictionary<int, RaceOption> { [79] = new(79, 100, 0, false, null) };
        Assert.False(CharacterRaceRoutes.OnTurnStart(1016, 49, 0, wins, races).StrongBrian);
        wins[25] = 79;
        Assert.True(CharacterRaceRoutes.OnTurnStart(1016, 49, 0, wins, races).StrongBrian);
        wins[25] = wins[48] = 0;
        wins[44] = 168;
        Assert.True(CharacterRaceRoutes.OnTurnStart(1016, 49, 0, wins, races).StrongBrian);
        Assert.Null(CharacterRaceRoutes.BrianRouteFromSelectedGoals([371, 372]));
        Assert.False(CharacterRaceRoutes.BrianRouteFromSelectedGoals([371, 379]));
        Assert.True(CharacterRaceRoutes.BrianRouteFromSelectedGoals([371, 382]));
        Assert.True(CharacterRaceRoutes.FromStory(501016409, 1, 0).StrongBrian);
        Assert.False(CharacterRaceRoutes.FromStory(501016409, 2, 0).StrongBrian);
        Assert.Null(CharacterRaceRoutes.FromStory(501016409, null, 0).StrongBrian);
        Assert.Null(CharacterRaceRoutes.FromStory(501016408, 1, 0).StrongBrian);
        var prize = CharacterRaceRoutes.VictoryReward(1016, 382, 130)!.Value;
        Assert.Equal((6, 65, 20061, 2), (prize.Attribute, prize.SkillPoints, prize.Hint.Skill.GroupId, prize.Hint.Level));
        Assert.Null(CharacterRaceRoutes.VictoryReward(1016, 383, 130));
    }
}
