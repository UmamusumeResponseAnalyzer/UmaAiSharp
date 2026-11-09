using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Races;

public sealed record RaceRoute(int Id, int ScenarioId, int CharaId, int RaceSetId, int ConditionSetId, int Priority);
public sealed record RaceGoal(int Id, int RaceSetId, int ScenarioGroupId, int TargetType, int SortId, int Turn,
    int RaceType, int ConditionType, int ConditionId, int ConditionValue1, int ConditionValue2,
    int DetermineRace, int DetermineRaceFlag, int AltDetermineRace, int AltDetermineRaceFlag, int DetermineRaceForGenerate);

// A route's master rows include alternatives. The selected goal IDs are part of cultivation state.
public sealed class RaceRouteCatalog(
    IEnumerable<RaceRoute> routes, IEnumerable<RaceGoal> goals,
    IEnumerable<KeyValuePair<int, int>> cardCharacters, IEnumerable<int> pioneerScenarioGroups)
{
    public FrozenDictionary<int, RaceRoute> Routes { get; } = routes.ToFrozenDictionary(x => x.Id);
    public FrozenDictionary<int, RaceGoal> Goals { get; } = goals.ToFrozenDictionary(x => x.Id);
    readonly FrozenDictionary<int, int> characters = cardCharacters.ToFrozenDictionary();
    readonly FrozenSet<int> groups = pioneerScenarioGroups.ToFrozenSet();

    public ImmutableArray<RaceGoal> ResolveSelected(int cardId, int routeId, IEnumerable<int> selectedGoalIds)
    {
        if (!characters.TryGetValue(cardId, out var charaId) || !Routes.TryGetValue(routeId, out var route))
            throw new UnknownMechanismException($"master 未收录育成卡 {cardId} 或路线 {routeId}。");
        if (route.CharaId != charaId || route.ScenarioId is not (0 or 11))
            throw new InvalidDataException($"路线 {routeId} 不适用于 Pioneer 育成卡 {cardId}。");
        var selected = selectedGoalIds.ToArray();
        if (selected.Length == 0 || selected.Distinct().Count() != selected.Length)
            throw new InvalidDataException("已选比赛目标必须非空且编号不重复。");
        var result = ImmutableArray.CreateBuilder<RaceGoal>(selected.Length);
        foreach (var id in selected)
        {
            if (!Goals.TryGetValue(id, out var goal))
                throw new UnknownMechanismException($"master 未收录比赛目标 {id}。");
            if (goal.RaceSetId != route.RaceSetId || !groups.Contains(goal.ScenarioGroupId))
                throw new InvalidDataException($"比赛目标 {id} 不属于路线 {routeId} 的 Pioneer 目标集合。");
            result.Add(goal);
        }
        return result.MoveToImmutable();
    }
}
