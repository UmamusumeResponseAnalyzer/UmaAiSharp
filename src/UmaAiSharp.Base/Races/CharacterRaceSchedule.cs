using System.Collections.Frozen;
using System.Collections.Immutable;

namespace UmaAiSharp.Base.Races;

public readonly record struct RaceOption(int ProgramId, int Grade, int FirstPlaceFans, bool Target, string? Name,
    int RequiredFans = 0, int CareerGoalId = 0)
{
    public bool CanEnter(int fans, int grade)
        => fans >= RequiredFans && (Target || Grade switch { 900 => grade == 1, 800 => grade == 2, _ => grade >= 3 });
}

public readonly record struct ScheduledRace(int Turn, RaceOption Race);
public readonly record struct FixedRaceGoal(int Id, int Turn, int ProgramId)
{
    public ScheduledRace Schedule(RaceOption program) => new(Turn, program with { Target = true, CareerGoalId = Id });
}
public readonly record struct RaceScheduleEdit(int Variant, int RemovedGoal, ScheduledRace? Added, int BlockedTurn);

public sealed class CharacterRaceSchedule
{
    readonly FrozenDictionary<int, RaceOption?[]> schedules;
    readonly bool[] blocked;
    public ImmutableArray<int> Variants => schedules.Keys;

    public static ImmutableArray<RaceScheduleEdit> ResolveEdits(int characterId, Func<int, ScheduledRace> resolveGoal)
        => [.. CharacterRaceRoutes.GoalEdits(characterId).Select(edit => new RaceScheduleEdit(edit.Variant,
            edit.RemovedGoal, edit.AddedGoal == 0 ? null : resolveGoal(edit.AddedGoal),
            edit.BlockRemovedTurn ? resolveGoal(edit.RemovedGoal).Turn : 0))];

    public static ImmutableArray<ScheduledRace> FromSelectedGoals(IEnumerable<FixedRaceGoal> selected,
        Func<int, RaceOption> resolveProgram, IEnumerable<ScheduledRace> reservations,
        Action<IGrouping<int, FixedRaceGoal>>? reportAmbiguous = null)
    {
        var byTurn = new SortedDictionary<int, ScheduledRace>();
        foreach (var group in selected.GroupBy(goal => goal.Turn))
        {
            var goal = group.First();
            if (group.All(other => other.ProgramId == goal.ProgramId))
                byTurn.Add(group.Key, goal.Schedule(resolveProgram(goal.ProgramId)));
            else reportAmbiguous?.Invoke(group);
        }
        foreach (var race in reservations)
            byTurn.TryAdd(race.Turn, race);
        return [.. byTurn.Values];
    }

    public CharacterRaceSchedule(int startTurn, int lastTurn, IEnumerable<ScheduledRace> observed,
        IEnumerable<int> blockedTurns, ImmutableArray<RaceScheduleEdit> edits)
    {
        var initial = new RaceOption?[lastTurn + 1];
        foreach (var race in observed)
        {
            if (race.Turn <= 0 || race.Turn > lastTurn || initial[race.Turn].HasValue)
                throw new InvalidDataException("未来必经赛的回合无效或重复。");
            initial[race.Turn] = race.Race;
        }
        blocked = new bool[initial.Length];
        foreach (var turn in blockedTurns)
            if (turn <= lastTurn) blocked[turn] = true;
        foreach (var edit in edits)
            if (edit.BlockedTurn > 0 && edit.BlockedTurn <= lastTurn) blocked[edit.BlockedTurn] = true;

        ApplyEdits(initial, 0);
        var variants = new Dictionary<int, RaceOption?[]> { [0] = initial };
        foreach (var variant in edits.Select(edit => edit.Variant).Where(variant => variant != 0).Distinct())
        {
            var calendar = (RaceOption?[])initial.Clone();
            ApplyEdits(calendar, variant);
            variants.Add(variant, calendar);
        }
        schedules = variants.ToFrozenDictionary();

        void ApplyEdits(RaceOption?[] calendar, int variant)
        {
            foreach (var edit in edits)
            {
                if (edit.Variant != variant) continue;
                if (edit.RemovedGoal != 0)
                    for (var day = startTurn; day <= lastTurn; day++)
                        if (calendar[day] is { Target: true } race && race.CareerGoalId == edit.RemovedGoal)
                            calendar[day] = null;
                if (edit.Added is { } added && added.Turn > startTurn && added.Turn <= lastTurn
                    && calendar[added.Turn] is not { Target: true })
                    calendar[added.Turn] = added.Race;
            }
        }
    }

    public bool IsBlocked(int turn) => (uint)turn < (uint)blocked.Length && blocked[turn];
    public bool HasTarget(int turn) => schedules.Values.Any(calendar => calendar[turn] is { Target: true });
    public RaceOption? RaceAt(int turn, int variant)
        => schedules[variant][turn] is { } race && (race.Target || !blocked[turn]) ? race : null;
}
