using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Base.Races;

public readonly record struct CharacterGoalEdit(int Variant, int RemovedGoal = 0, int AddedGoal = 0, bool BlockRemovedTurn = false);

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

    public static int? BranchAfterWin(int characterId, int goalId, int raceMotivation, ref SimulationRandom random)
        => (characterId, goalId) switch
        {
            (1032, 183) when raceMotivation <= 3 => TachyonMileCupGoal,
            // Fitted weights: 1:1:7 after debut and 1:1:4 after Takarazuka.
            // All six observed debut draws picked 293; the other two retain one prior count.
            (1056, 89) => FukukitaruDebutGoals[Math.Min(random.NextScaled(9), 2)],
            (1056, 94) => FukukitaruTakarazukaGoals[Math.Min(random.NextScaled(6), 2)],
            _ => null,
        };

    public static bool QualifiesForStrongBrianRoute(ReadOnlySpan<int> winsByTurn, Func<int, bool> isGradeOne)
    {
        var points = 0;
        foreach (var programId in winsByTurn[25..BrianChoiceTurn])
            if (programId is 163 or 166 or 168) points += 2;
            else if (isGradeOne(programId)) points++;
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

    public static bool? BrianRouteFromStory(int storyId, int? choice)
        => storyId == 501016409 ? choice == 1 : null;
}
