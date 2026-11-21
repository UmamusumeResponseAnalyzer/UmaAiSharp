using UmaAiSharp.Core;
using System.Text.Json.Nodes;
using UmaAiSharp.Pioneer.Game;
using UmaAiSharp.Pioneer.Races;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class RaceRouteTests
{
    [Fact]
    public void JbcVenuesRemainBoundToTheirYearAndMissingHistoryRemainsUnknown()
    {
        // Haru Urara's opening and current-race packets, packets-0004 #27821 and #28415.
        var opening = JsonNode.Parse("""{"race_random_program_array":[{"turn":45,"program_id":1116},{"turn":45,"program_id":1117},{"turn":45,"program_id":1118},{"turn":69,"program_id":1113},{"turn":69,"program_id":1114},{"turn":69,"program_id":1115}]}""")!.AsObject();
        var schedule = PioneerGameData.ReadRandomRacePrograms(opening)!.Value;
        Assert.Contains(new PioneerScheduledProgram(69, 1114), schedule);
        Assert.DoesNotContain(new PioneerScheduledProgram(45, 1114), schedule);
        Assert.Null(PioneerGameData.ReadRandomRacePrograms(new JsonObject()));
    }

    [Theory]
    [InlineData(1, 2, 0, 1476)]
    [InlineData(1, 2, 1, 0)]
    [InlineData(1, 2, 2, 1475)]
    [InlineData(-1, 1, 2, 1476)]
    public void EspoirFinalGoalUsesAllStartsThroughNambuHai(int escapeLead, int style, int futureStarts, int expected)
        => Assert.Equal(expected, new PioneerEspoirRoute(escapeLead, style, []).GoalAfter(futureStarts));

    static RaceRouteCatalog Catalog() => new(
        [new(122, 0, 1135, 1135, 0, 0)],
        [new(1523, 1135, 100, 1, 5, 44, 0, 1, 168, 3, 0, 1, 0, 99, 0, 99),
            new(1524, 1135, 100, 1, 5, 44, 0, 1, 76, 2, 0, 2, 113501, 0, 0, 0),
            new(1530, 1135, 701, 3, 11, 74, 1, 1, 10001, 1, 0, 0, 0, 0, 0, 0),
            new(9999, 9999, 100, 1, 1, 12, 0, 1, 1067, 0, 0, 0, 0, 0, 0, 0)],
        new Dictionary<int, int> { [113501] = 1135, [106001] = 1060 }, [100, 701]);

    [Fact]
    public void SelectedStatePreservesConditionalAlternativeAndFinalRaceGroup()
    {
        var catalog = Catalog();
        var first = catalog.ResolveSelected(113501, 122, [1523, 1530]);
        var second = catalog.ResolveSelected(113501, 122, [1524, 1530]);
        Assert.Equal(new[] { 1523, 1530 }, first.Select(x => x.Id));
        Assert.Equal(113501, second[0].DetermineRaceFlag);
        Assert.Equal(10001, second[1].ConditionId);
        Assert.Equal(1523, first[0].Id);
    }

    [Fact]
    public void UnavailableOrForeignGoalsNeverBecomeAnAutomaticRoute()
    {
        var catalog = Catalog();
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(106001, 122, [1523]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, [9999]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, [1523, 1523]));
        Assert.Throws<InvalidDataException>(() => catalog.ResolveSelected(113501, 122, []));
        Assert.Throws<UnknownMechanismException>(() => catalog.ResolveSelected(113501, 122, [9998]));
    }
}
