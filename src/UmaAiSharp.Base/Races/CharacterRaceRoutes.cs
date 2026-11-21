using System.Collections.Immutable;
using UmaAiSharp.Base.Hints;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;

namespace UmaAiSharp.Base.Races;

public readonly record struct CharacterGoalEdit(int Variant, int RemovedGoal = 0, int AddedGoal = 0, bool BlockRemovedTurn = false);
public readonly record struct CharacterRouteEvent(int? NextGoal = null, bool? StrongBrian = null, TrainingValues Reward = default);

public static class CharacterRaceRoutes
{
    public const int NaritaBrian = 1016;
    public const int BrianChoiceTurn = 49;
    public const int StrongBrianVariant = 1;
    public const int TachyonMileCupGoal = 296;

    static readonly ImmutableArray<int> FukukitaruDebutGoals = [90, 292, 293];
    static readonly ImmutableArray<int> FukukitaruTakarazukaGoals = [294, 295, 95];

    public static ImmutableArray<CharacterGoalEdit> GoalEdits(int characterId)
    {
        switch (characterId)
        {
            case NaritaBrian:
                var edits = ImmutableArray.CreateBuilder<CharacterGoalEdit>(8);
                for (var strong = 378; strong <= 384; strong += 2)
                {
                    edits.Add(new(0, AddedGoal: strong + 1));
                    edits.Add(new(StrongBrianVariant, strong + 1, strong));
                }
                return edits.MoveToImmutable();
            case 1032:
                return [new(TachyonMileCupGoal, 184, TachyonMileCupGoal)];
            case 1056:
                return [.. FukukitaruDebutGoals.Concat(FukukitaruTakarazukaGoals)
                    .Select(goal => new CharacterGoalEdit(goal, AddedGoal: goal))];
            case 1062:
                return [new(0, 627, BlockRemovedTurn: true)];
            case 1135:
                return [new(0, 1523, 1524), new(0, AddedGoal: 1529)];
            default:
                return [];
        }
    }

    public static int? BranchAfterWin(int characterId, int goalId, ref SimulationRandom random)
        => (characterId, goalId) switch
        {
            // Fitted weights: 1:1:7 after debut and 1:1:4 after Takarazuka.
            // All six observed debut draws picked 293; the other two retain one prior count.
            (1056, 89) => FukukitaruDebutGoals[Math.Min(random.NextScaled(9), 2)],
            (1056, 94) => FukukitaruTakarazukaGoals[Math.Min(random.NextScaled(6), 2)],
            _ => null,
        };

    public static CharacterRouteEvent OnTurnStart(int characterId, int turn, int lastTargetRaceMotivation,
        ReadOnlySpan<int> winsByTurn, IReadOnlyDictionary<int, RaceOption> races)
        => (characterId, turn) switch
        {
            (1032, 32) => FromStory(501032402, null, lastTargetRaceMotivation),
            (1062, 70) => FromStory(501062406, null, lastTargetRaceMotivation),
            (NaritaBrian, BrianChoiceTurn) => new(StrongBrian: QualifiesForStrongBrianRoute(winsByTurn, races)),
            _ => default,
        };

    public static CharacterRouteEvent FromStory(int storyId, int? choice, int lastTargetRaceMotivation)
        => storyId switch
        {
            501032402 when choice is null or 0 => new(NextGoal: lastTargetRaceMotivation is >= 1 and <= 3 ? TachyonMileCupGoal : null,
                Reward: new(0, 0, 0, 0, 5, 0, 0)),
            501062406 when choice is null or 0 => new(Reward: new(0, 0, 0, 5, 0, 0, 0)),
            501016409 => new(StrongBrian: choice switch { 1 => true, 2 => false, _ => null }),
            _ => default,
        };

    static bool QualifiesForStrongBrianRoute(ReadOnlySpan<int> winsByTurn, IReadOnlyDictionary<int, RaceOption> races)
    {
        var points = 0;
        foreach (var programId in winsByTurn[25..BrianChoiceTurn])
            if (programId is 163 or 166 or 168) points += 2;
            else if (races.TryGetValue(programId, out var race) && race.Grade == 100) points++;
        return points >= 6;
    }

    public static bool IsStrongBrianGoal(int goalId) => goalId is 378 or 380 or 382 or 384;

    public static bool? BrianRouteFromSelectedGoals(IEnumerable<int> selectedGoalIds)
    {
        bool? strong = null;
        foreach (var id in selectedGoalIds)
        {
            if (IsStrongBrianGoal(id)) return true;
            if (id is 379 or 381 or 383 or 385) strong = false;
        }
        return strong;
    }

    public static ReadOnlySpan<int> GoalRaceHintGroups(int characterId) => characterId switch
    {
        1003 => [20190], 1008 => [20049], NaritaBrian => [20135, 20049, 20061, 20036],
        1017 => [20169], 1026 => [20037], 1041 => [20128, 20204], 1063 => [20279], _ => [],
    };

    public static (int Attribute, int SkillPoints, HintLevel Hint)? VictoryReward(int characterId, int goal, int raceRewardPercent)
        => characterId == NaritaBrian && IsStrongBrianGoal(goal)
            ? (5 * raceRewardPercent / 100, 50 * raceRewardPercent / 100,
                new(new(GoalRaceHintGroups(characterId)[(goal - 378) / 2], 1), 2))
            : null;
}
