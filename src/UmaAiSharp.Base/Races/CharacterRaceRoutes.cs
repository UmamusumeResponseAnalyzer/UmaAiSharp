using System.Collections.Immutable;
using UmaAiSharp.Base.Hints;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;

namespace UmaAiSharp.Base.Races;

public readonly record struct CharacterGoalEdit(int Variant, int RemovedGoal = 0, int AddedGoal = 0, bool BlockRemovedTurn = false);
public readonly record struct CharacterRouteEvent(int? NextGoal = null, bool? StrongBrian = null, TrainingValues Reward = default);

public readonly record struct SpecialRaceGoal(int Character, int Id, int Order, int Turn, int Determine, int Condition, int Target);

public sealed record EspoirRaceRoute(int EscapeLead, int RunningStyle, ImmutableArray<RaceScheduleEdit> Alternatives)
{
    public int GoalAfter(int futureStarts)
        => (EscapeLead + (RunningStyle == 1 ? futureStarts : -futureStarts)) switch { > 0 => 1476, < 0 => 1475, _ => 0 };
}

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
            case 1079:
                return [new(0, 1333, BlockRemovedTurn: true)];
            case 1093:
                return [new(0, 1050, BlockRemovedTurn: true)];
            case 1135:
                return [new(0, 1523, 1524), new(0, AddedGoal: 1529)];
            default:
                return [];
        }
    }

    public static void ProjectFutureGoals(int character, int turn, HashSet<int> goals, IEnumerable<SpecialRaceGoal> special, Action<string> note)
    {
        // Future player choices use the first option; already revealed choices always win.
        switch (character)
        {
            case 1005:
                if (!goals.Overlaps([419, 420, 421, 422]))
                {
                    goals.UnionWith([420, 421]);
                    note("尚未选择经典赛线，模拟按第一选项三冠目标 420、421 继续，省略另一条赛线");
                }
                return;
            case 1022:
                if (!goals.Overlaps([550, 551, 552, 553]))
                {
                    goals.Add(550);
                    note("末战尚未选择，模拟按第一选项秋季天皇赏目标 550 继续，省略目标 551、552、553");
                }
                return;
            case 1056:
                if (turn <= 12 && !goals.Overlaps([90, 292, 293]))
                    note("出道后随机目标 90 / 292 / 293 的实际概率未知，模拟以实包加权 1 / 1 / 7 抽取一场");
                if (turn <= 60 && !goals.Overlaps([95, 294, 295]))
                    note("宝塚纪念后随机目标 95 / 294 / 295 的实际概率未知，模拟以实包加权 4 / 1 / 1 抽取一场");
                return;
            case 1069:
                if (!goals.Overlaps([573, 574]))
                {
                    goals.Add(573);
                    note("资深六月目标尚未选择，模拟按第一选项安田纪念 573 继续，省略目标 574");
                }
                return;
            case 1071:
                if (!goals.Overlaps([584, 585, 589, 590]))
                {
                    goals.UnionWith([584, 589]);
                    note("秋季赛线尚未选择，模拟按第一选项菊花赏路线 584、589 继续，省略目标 585、590");
                }
                return;
            case 1075:
                // Kinko Sho temporarily removes Victoria Mile. The recovery story restores the same race.
                goals.Add(1711);
                return;
            case 1092:
                if (turn <= 57) goals.Add(1498);
                return;
            case 1109:
                if (goals.Remove(1181)) goals.Add(1180);
                return;
            case 1116:
                goals.Add(1262);
                return;
            case 1121:
                if (turn <= 41) goals.Add(1281);
                return;
            case 1132:
                if (goals.Remove(1419)) goals.Add(1420);
                return;
            case 1134:
                if (goals.Remove(1642)) goals.UnionWith([1643, 1644]);
                return;
            case 1144:
                if (goals.Remove(1694)) goals.Add(1695);
                return;
            case 1009 or 1016 or 1031 or 1032 or 1062 or 1079 or 1081 or 1093 or 1135:
                return;
        }
        foreach (var group in special.Where(goal => goal.Target == 1).GroupBy(goal => goal.Order))
        {
            if (group.All(goal => goal.Turn < turn) || group.Any(goal => goal.Determine >= 2 && goals.Contains(goal.Id))) continue;
            note($"忽略尚未确定的目标变更 {string.Join('、', group.Select(goal => $"{goal.Id}（第 {goal.Turn} 回合）"))}，按已揭示赛程模拟");
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
        1017 => [20169], 1022 => [20072, 20134, 20070, 20057, 20190, 20062], 1026 => [20037],
        1031 => [20125, 20054], 1041 => [20128, 20204], 1063 => [20279], 1075 => [20357, 20472, 20444], _ => [],
    };

    public static (int Attribute, int SkillPoints, HintLevel Hint)? VictoryReward(int characterId, int goal, int raceRewardPercent)
        => characterId == NaritaBrian && IsStrongBrianGoal(goal)
            ? (5 * raceRewardPercent / 100, 50 * raceRewardPercent / 100,
                new(new(GoalRaceHintGroups(characterId)[(goal - 378) / 2], 1), 2))
            : null;
}
