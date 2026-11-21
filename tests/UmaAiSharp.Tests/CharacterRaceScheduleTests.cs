using UmaAiSharp.Base.Races;

namespace UmaAiSharp.Tests;

public sealed class CharacterRaceScheduleTests
{
    [Fact]
    public void ImportedGoalsAndReservationsRetainTheirPriorityAndIdentity()
    {
        var reserved = new RaceOption(74, 100, 1000, false, null);
        var races = CharacterRaceSchedule.FromSelectedGoals(
            [new(1523, 44, 168), new(1524, 44, 76), new(184, 34, 166), new(999, 34, 166)],
            id => new(id, 100, 2000, true, null),
            [new(44, reserved), new(34, reserved), new(44, reserved with { ProgramId = 79 })]);
        Assert.Collection(races,
            race => Assert.Equal(new ScheduledRace(34, new(166, 100, 2000, true, null, CareerGoalId: 184)), race),
            race => Assert.Equal(new ScheduledRace(44, reserved), race));
    }

    [Fact]
    public void AlternativeCalendarsReplaceFutureGoalsWithoutOverridingOtherSelectedTargets()
    {
        var derby = new ScheduledRace(34, new(166, 100, 0, true, null, CareerGoalId: 184));
        var nhk = new ScheduledRace(33, new(164, 100, 0, true, null, CareerGoalId: 296));
        var reserved = new ScheduledRace(33, new(75, 100, 1000, false, null));
        var edits = CharacterRaceSchedule.ResolveEdits(1032, id => id == 296 ? nhk : throw new InvalidDataException());
        var schedule = new CharacterRaceSchedule(31, 72, [derby, reserved], [], edits);
        Assert.Equal(derby.Race, schedule.RaceAt(34, 0));
        Assert.Equal(reserved.Race, schedule.RaceAt(33, 0));
        Assert.Null(schedule.RaceAt(34, 296));
        Assert.Equal(nhk.Race, schedule.RaceAt(33, 296));
        Assert.True(schedule.HasTarget(33));

        var other = reserved with { Race = reserved.Race with { Target = true, CareerGoalId = 1000 } };
        Assert.Equal(other.Race, new CharacterRaceSchedule(31, 72, [derby, other], [], edits).RaceAt(33, 296));
        Assert.Equal(reserved.Race, new CharacterRaceSchedule(33, 72, [derby, reserved], [], edits).RaceAt(33, 296));
    }

    [Fact]
    public void CharacterCancellationBlocksOptionalRacesWhileObservedTargetsRemainMandatory()
    {
        var cup = new ScheduledRace(70, new(79, 100, 0, true, null, CareerGoalId: 627));
        var optional = new ScheduledRace(70, new(100, 200, 1000, false, null));
        var target = new ScheduledRace(68, new(77, 100, 2000, true, null, RequiredFans: 50, CareerGoalId: 626));
        var edits = CharacterRaceSchedule.ResolveEdits(1062, id => id == 627 ? cup : throw new InvalidDataException());
        var schedule = new CharacterRaceSchedule(50, 72, [target, cup], [68], edits);
        Assert.Null(schedule.RaceAt(70, 0));
        Assert.True(schedule.IsBlocked(70));
        Assert.Equal(target.Race, schedule.RaceAt(68, 0));
        Assert.False(target.Race.CanEnter(49, 1));
        Assert.True(target.Race.CanEnter(50, 1));
        Assert.False(optional.Race.CanEnter(50, 2));
        Assert.Null(new CharacterRaceSchedule(50, 72, [optional], [], edits).RaceAt(70, 0));

        var shortCalendar = new CharacterRaceSchedule(50, 60, [], [68], edits);
        Assert.Null(shortCalendar.RaceAt(60, 0));
        Assert.False(shortCalendar.IsBlocked(70));
        Assert.Throws<IndexOutOfRangeException>(() => shortCalendar.RaceAt(61, 0));
    }
}
