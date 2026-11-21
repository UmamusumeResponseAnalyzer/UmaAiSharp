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
}
