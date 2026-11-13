using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public enum PioneerArrivalSource { Training, CampTraining, RestConfirmation, CampRest, TrainingLevel, TurnConfirmation, TrainingWithLevel, PlanningSubmission, Treatment, Outing, Race, IslandTraining }
public enum PioneerArrival { Ready, TurnConfirmation, TrainingLevel, SameTurnEvent, NextTurnEvent, OtherPhase }

public readonly record struct PioneerRestFrequency(int StoryEpisode, int Count);
public readonly record struct PioneerUraFanFrequency(int Turn, int Percent, int Count);
public readonly record struct PioneerOrdinaryStoryProgress(int First, int Second, int Third, int Trainee);
public readonly record struct PioneerTuckerRepeatFrequency(bool Island, int Opportunities, int Triggered,
    ImmutableArray<PioneerTuckerStaminaFrequency> StaminaRewards);
public readonly record struct PioneerTuckerStaminaFrequency(int Gain, int Count);
public readonly record struct PioneerDirectorFrequency(bool Camp, int Boards, int Appearances);
public readonly record struct PioneerOrdinaryEventFrequency(int Count, TrainingValues Gains, int Motivation, int MaxVital, int Bond);
public readonly record struct PioneerArrivalFrequency(PioneerArrivalSource Source, int Turn,
    int Ready, int TurnConfirmation, int TrainingLevel, int SameTurnEvent, int NextTurnEvent, int OtherPhase,
    ImmutableArray<PioneerOrdinaryEventFrequency> Ordinary = default);
public sealed record PioneerTurnCalibration(ImmutableArray<PioneerRestFrequency> OrdinaryRest,
    ImmutableArray<PioneerArrivalFrequency> Arrivals, ImmutableArray<PioneerPlacementFrequency> Placement,
    ImmutableArray<PioneerUraFanFrequency> UraFans = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<PioneerTrainingHistoryFrequency> TrainingHistory = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<PioneerTuckerRepeatFrequency> TuckerRepeat = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<PioneerDirectorFrequency> Director = default);

internal sealed record PioneerOrdinaryEventEffects(int Count, DeclaredEventRewards Resources, int Bond);
internal sealed record PioneerBoardRules(PioneerJointPlacement Placement, ImmutableArray<CompiledCampTraining> Camp,
    ImmutableArray<TrainingFacility> Facilities, int ConditionalSpecialties);

public sealed class PioneerTurnRules
{
    readonly ImmutableArray<PioneerRestFrequency> rest;
    readonly int restCount;
    internal double ExpectedOrdinaryRest { get; }
    readonly FrozenDictionary<(PioneerArrivalSource Source, int Turn), (int NoReward,
        PioneerOrdinaryEventEffects[] Ordinary, int Total)> arrivals;
    readonly FrozenDictionary<(int Command, int Level), int> trainingCoefficients;
    readonly FrozenDictionary<(PlacementMemberKind Kind, bool Camp, int SeaHousePercent), PioneerPlacementFrequency> placements;
    readonly FrozenDictionary<(int Id, int StateId), TrainingFacility> facilityStates;
    readonly (int OpportunityWeight, int NoEventWeight, ImmutableArray<PioneerTuckerStaminaFrequency> Rewards)[] tuckerRepeat = new
        (int, int, ImmutableArray<PioneerTuckerStaminaFrequency>)[2];
    readonly ImmutableArray<PioneerDirectorFrequency> director;
    readonly ImmutableArray<ForecastFacilityBase> facilityBases;
    readonly FrozenDictionary<int, (PioneerUraFanFrequency[] Frequencies, int Total)> uraFanAdjustments;
    readonly int appearedSupportMask, ordinarySupportMask, tuckerMask;
    readonly ImmutableArray<(int Position, SupportUniqueEffect Effect)> conditionalSpecialties;
    internal int InitialProjectNpcCount { get; }
    internal int OrdinarySupportMask => ordinarySupportMask;
    readonly ImmutableArray<int> uraFirstPlaceFans;
    readonly PioneerRaceOption?[] scheduledRaces = new PioneerRaceOption?[79];
    readonly ImmutableArray<PioneerRaceOption>[] raceCalendar;
    readonly FrozenDictionary<int, PioneerRaceOption> racesByProgram;
    readonly ulong blockedRaceTurns;
    internal ImmutableArray<PioneerCareerGoal> CareerGoals { get; }
    internal ImmutableArray<int> InitialCareerCounts { get; }
    internal int InitialGrade { get; }
    internal int RaceRewardPercent { get; }
    internal int FanRewardPercent { get; }
    internal bool WholeRun => !uraFirstPlaceFans.IsDefaultOrEmpty;
    internal PioneerInheritanceRules? Inheritance { get; }
    internal PioneerHintRules? Hints { get; }
    internal PioneerOrdinaryStoryProgress InitialStories { get; }
    internal const int BoardWorkspaceBytes = 8192;

    public PioneerTurnRules(PioneerTurnCalibration calibration, IEnumerable<(int Command, int Level, int Failure)> coefficients,
        PioneerStart.TrainingTurn start, int appearedSupportMask, ImmutableArray<int> appearedProjectMembers,
        ImmutableArray<TrainingFacility> facilities, ImmutableArray<ForecastFacilityBase> facilityBases = default,
        ImmutableArray<int> uraFirstPlaceFans = default, PioneerInheritanceRules? inheritance = null, PioneerHintRules? hints = null,
        PioneerOrdinaryStoryProgress initialStories = default, ImmutableArray<PioneerRaceOption>[]? raceCalendar = null)
    {
        if (calibration.OrdinaryRest.IsDefaultOrEmpty || calibration.Arrivals.IsDefaultOrEmpty || calibration.Placement.IsDefaultOrEmpty
            || calibration.OrdinaryRest.Any(row => row.StoryEpisode is not (701 or 718) || row.Count <= 0)
            || calibration.OrdinaryRest.Select(row => row.StoryEpisode).Distinct().Count() != calibration.OrdinaryRest.Length
            || calibration.Arrivals.Any(row => !Enum.IsDefined(row.Source) || row.Turn is < 1 or > 77
                || row.Ready < 0 || row.TurnConfirmation < 0 || row.TrainingLevel < 0 || row.SameTurnEvent < 0 || row.NextTurnEvent < 0 || row.OtherPhase < 0
                || (row.Turn != 13 || row.Source == PioneerArrivalSource.TurnConfirmation) && row.TurnConfirmation != 0
                || row.Source != PioneerArrivalSource.TrainingWithLevel && row.TrainingLevel != 0
                || row.Source == PioneerArrivalSource.TrainingWithLevel && (row.Ready != 0 || row.NextTurnEvent != 0 || row.OtherPhase != 0 || !row.Ordinary.IsDefaultOrEmpty)
                || (long)row.Ready + row.TurnConfirmation + row.TrainingLevel + row.SameTurnEvent + row.NextTurnEvent + row.OtherPhase
                    + (row.Ordinary.IsDefaultOrEmpty ? 0 : row.Ordinary.Sum(effect => (long)effect.Count)) is <= 0 or > int.MaxValue))
            throw new InvalidDataException("Pioneer 回合拟合缺少有效的休息或续步计数。");
        rest = calibration.OrdinaryRest;
        restCount = rest.Sum(row => row.Count);
        ExpectedOrdinaryRest = rest.Sum(row => (long)row.Count * (row.StoryEpisode == 701 ? 70 : 50)) / (double)restCount;
        var rewards = calibration.Arrivals.Where(row => row.Source != PioneerArrivalSource.TrainingWithLevel).ToDictionary(row => (row.Source, row.Turn), row =>
        {
            var ordinary = row.Ordinary.IsDefaultOrEmpty ? [] : row.Ordinary.Select(CompileOrdinary).ToArray();
            var noReward = row.Ready + row.TurnConfirmation + row.SameTurnEvent + row.NextTurnEvent + row.OtherPhase;
            return (NoReward: noReward, Ordinary: ordinary, Total: noReward + ordinary.Sum(effect => effect.Count));
        });
        foreach (var source in rewards.ToLookup(row => row.Key.Source))
            rewards.Add((source.Key, 0), (source.Sum(row => row.Value.NoReward),
                [.. source.SelectMany(row => row.Value.Ordinary)], source.Sum(row => row.Value.Total)));
        arrivals = rewards.ToFrozenDictionary();
        trainingCoefficients = coefficients.ToFrozenDictionary(row => (row.Command, row.Level), row => row.Failure);
        this.appearedSupportMask = appearedSupportMask;
        ordinarySupportMask = start.Deck.Where(card => card.TrainingCommandId != 0).Sum(card => 1 << (card.Position - 1));
        tuckerMask = start.Deck.Where(card => card.CardId == 30257).Sum(card => 1 << (card.Position - 1));
        conditionalSpecialties = [.. start.Deck.SelectMany(card => card.UniqueEffects
            .Where(effect => effect.Type == 101 && (effect.Parameter1 == 19 || effect.Parameter3 == 19))
            .Select(effect => (card.Position, effect)))];
        InitialProjectNpcCount = appearedProjectMembers.Length;
        this.facilityBases = facilityBases;
        this.uraFirstPlaceFans = uraFirstPlaceFans;
        Inheritance = inheritance;
        Hints = hints;
        InitialStories = initialStories;
        if (!calibration.TuckerRepeat.IsDefault)
            foreach (var row in calibration.TuckerRepeat)
            {
                var outcomes = row.StaminaRewards.Sum(reward => reward.Count);
                tuckerRepeat[row.Island ? 1 : 0] = (checked(row.Opportunities * outcomes),
                    checked((row.Opportunities - row.Triggered) * outcomes),
                    [.. row.StaminaRewards.Select(reward => reward with { Count = checked(reward.Count * row.Triggered) })]);
            }
        director = calibration.Director;
        this.raceCalendar = raceCalendar ?? new ImmutableArray<PioneerRaceOption>[79];
        var homeActions = start.HomeActions;
        InitialGrade = homeActions?.Grade ?? 3;
        blockedRaceTurns = homeActions?.BlockedRaceTurns ?? 0;
        CareerGoals = homeActions?.CareerGoals ?? [];
        racesByProgram = (homeActions?.Races ?? []).Concat(this.raceCalendar.Where(day => !day.IsDefaultOrEmpty).SelectMany(day => day))
            .Concat((homeActions?.ScheduledRaces ?? []).Select(race => race.Race))
            .DistinctBy(race => race.ProgramId).ToFrozenDictionary(race => race.ProgramId);
        InitialCareerCounts = [.. CareerGoals.Select(goal => (homeActions?.RaceHistory ?? []).Count(race =>
            race.Turn >= goal.FirstTurn && race.Turn <= goal.Deadline && race.Rank <= goal.MaximumRank
            && racesByProgram.TryGetValue(race.ProgramId, out var program) && program.Grade <= goal.MaximumGrade))];
        if (start.HomeActions is { } home)
            foreach (var race in home.ScheduledRaces)
            {
                if (race.Turn <= 0 || race.Turn >= scheduledRaces.Length || scheduledRaces[race.Turn] is not null)
                    throw new InvalidDataException("未来必经赛的回合无效或重复。");
                scheduledRaces[race.Turn] = race.Race;
            }
        uraFanAdjustments = (calibration.UraFans.IsDefault ? [] : calibration.UraFans).GroupBy(row => row.Turn)
            .ToFrozenDictionary(group => group.Key, group => (group.ToArray(), group.Sum(row => row.Count)));
        if (WholeRun && (uraFanAdjustments.Count != 3 || !new[] { 74, 76, 78 }.All(uraFanAdjustments.ContainsKey)
            || calibration.UraFans.Any(row => row.Percent <= 0 || row.Count <= 0)))
            throw new InvalidDataException("整局规则需要三轮 URA 第一名的粉丝倍率频数。");
        RaceRewardPercent = 100 + start.Deck.Sum(card => card.Effects.Where(effect => effect.Type == 15).Sum(effect => effect.Value)
            + card.UniqueEffects.Where(effect => effect.Type == 15).Sum(effect => effect.Value));
        FanRewardPercent = 100 + start.Deck.Sum(card => card.Effects.Where(effect => effect.Type == 16).Sum(effect => effect.Value)
            + card.UniqueEffects.Where(effect => effect.Type == 16).Sum(effect => effect.Value));
        placements = calibration.Placement.ToFrozenDictionary(row => (row.Kind, row.Camp, row.SeaHousePercent));
        facilityStates = facilities.ToFrozenDictionary(facility => (facility.Id, facility.StateId));
    }

    internal PioneerRaceOption? ScheduledRace(int turn) => scheduledRaces[turn];
    internal PioneerRaceOption RaceOption(int turn, int programId)
    {
        if (scheduledRaces[turn] is { } scheduled && scheduled.ProgramId == programId) return scheduled;
        return racesByProgram[programId];
    }
    internal bool RaceBlocked(int turn)
        => turn is >= 13 and <= 72 && (blockedRaceTurns & (1UL << (turn - 13))) != 0;

    internal ImmutableArray<PioneerRaceOption> OptionalRaces(int turn, int grade)
    {
        if (turn is < 13 or > 72 || RaceBlocked(turn) || grade < 2 || raceCalendar[turn].IsDefault) return [];
        return raceCalendar[turn];
    }

    internal long SharedMemoryBytes => 4096L + (Inheritance?.SharedMemoryBytes ?? 0) + (Hints?.SharedMemoryBytes ?? 0)
        + 128L * (arrivals.Count + trainingCoefficients.Count)
        + facilityStates.Values.Sum(facility => 96L + 32L * facility.Effects.Length) + 32L * (facilityBases.IsDefault ? 0 : facilityBases.Length)
        + raceCalendar.Sum(day => day.IsDefault ? 0L : 48L * day.Length) + 48L * racesByProgram.Count
        + 64L * placements.Count + uraFanAdjustments.Values.Sum(group => 64L + 16L * group.Frequencies.Length)
        + arrivals.Values.Sum(entry => 32L + entry.Ordinary.Sum(row => 48L + row.Resources.SharedMemoryBytes));

    static PioneerOrdinaryEventEffects CompileOrdinary(PioneerOrdinaryEventFrequency sample)
    {
        var gain = sample.Gains;
        if (sample.Count <= 0 || sample.Bond < 0 || sample.MaxVital < 0 || gain.Speed < 0 || gain.Stamina < 0
            || gain.Power < 0 || gain.Guts < 0 || gain.Wisdom < 0 || gain.SkillPoints < 0)
            throw new InvalidDataException("普通事件拟合含无效频数或未接入的负收益。");
        ReadOnlySpan<int> targets = [1, 2, 3, 4, 5, 10, 30, 20, 11];
        ReadOnlySpan<int> values = [gain.Speed, gain.Stamina, gain.Power, gain.Guts, gain.Wisdom,
            gain.Vital, gain.SkillPoints, sample.Motivation, sample.MaxVital];
        var atoms = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
        for (var i = 0; i < values.Length; i++)
            if (values[i] != 0) atoms.Add(new(values[i] < 0 ? 2 : 1, targets[i], Math.Abs(values[i]), 0));
        return new(sample.Count, new(new(atoms.ToImmutable())), sample.Bond);
    }

    internal void ApplyOrdinaryBond(PioneerState state, int gain, ref SimulationRandom random)
    {
        if (gain == 0) return;
        Span<int> positions = stackalloc int[6];
        var length = TrainingBoard.Positions((byte)(AppearedMask(state.Header) & ordinarySupportMask), positions);
        if (length == 0) return;
        var index = positions[random.NextInt(length)] - 1;
        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
        bonds[index] = Math.Min(100, bonds[index] + gain);
    }

    internal int DrawRestRecovery(ref SimulationRandom random)
    {
        var draw = random.NextInt(restCount);
        foreach (var row in rest)
        {
            if (draw < row.Count)
                return row.StoryEpisode == 701 ? 70 : 50;
            draw -= row.Count;
        }
        throw new InvalidOperationException("休息频数与抽样总数不一致。");
    }

    internal PioneerOrdinaryEventEffects? DrawOrdinaryEvents(PioneerArrivalSource source, int turn, ref SimulationRandom random)
    {
        // shortcut: unresolved windows add zero modeled reward; expand the profiles when paired effects become available.
        if (!arrivals.TryGetValue((source, turn), out var entry) && !arrivals.TryGetValue((source, 0), out entry)) return null;
        var (noReward, ordinaryRows, total) = entry;
        if (ordinaryRows.Length == 0) return null;
        var draw = random.NextInt(total) - noReward;
        if (draw < 0) return null;
        foreach (var outcome in ordinaryRows)
        {
            if (draw < outcome.Count) return outcome;
            draw -= outcome.Count;
        }
        throw new InvalidOperationException("普通事件频数与抽样总数不一致。");
    }

    internal int FailureCoefficient(int command, int level) => trainingCoefficients[(command, level)];

    internal int DrawUraFans(int turn, ref SimulationRandom random)
    {
        var (frequencies, total) = uraFanAdjustments[turn];
        var draw = random.NextInt(total);
        var fans = uraFirstPlaceFans[(turn - 74) / 2] * FanRewardPercent / 100;
        foreach (var row in frequencies)
        {
            if (draw < row.Count) return checked((fans * row.Percent + 50) / 100);
            draw -= row.Count;
        }
        throw new InvalidOperationException("URA 粉丝倍率频数与抽样总数不一致。");
    }

    int AppearedMask(in PioneerStateHeader header)
        => appearedSupportMask | (WholeRun && header.Turn >= 3 ? tuckerMask : 0);

    internal TrainingBoard DrawBoard(PioneerState state, bool camp, ref SimulationRandom random)
    {
        var conditional = 0;
        var bonds = state.GetSection(PioneerStateSection.SupportBonds);
        for (var i = 0; i < conditionalSpecialties.Length; i++)
        {
            var (position, effect) = conditionalSpecialties[i];
            if (bonds[position - 1] >= effect.Value) conditional |= 1 << i;
        }
        if (state.TurnBoard is not { } rules || rules.ConditionalSpecialties != conditional)
            state.TurnBoard = rules = CompileBoard(state, camp, conditional);
        var layout = rules.Placement;
        Span<SupportPlacement> positions = stackalloc SupportPlacement[layout.Members.Length];
        Span<int> localHint = stackalloc int[5];
        localHint.Clear();
        var hasLocalHint = false;
        if (camp)
            foreach (var facility in rules.Facilities)
                foreach (var effect in facility.Effects)
                    if (effect is { Type: 204, Condition: 2 })
                    {
                        localHint[effect.Target - 3601] += effect.Value;
                        hasLocalHint = true;
                    }
        var board = layout.Sample(positions, ref random, hasLocalHint ? localHint : []);
        if (!director.IsDefaultOrEmpty)
        {
            Span<int> free = stackalloc int[5];
            foreach (var row in director)
            {
                if (row.Camp != camp || random.NextInt(row.Boards) >= row.Appearances) continue;
                var count = 0;
                for (var i = 0; i < 5; i++)
                    if (board[i].SupportCount + board[i].GuestCount < 5) free[count++] = i;
                if (count == 0) continue;
                var slot = free[random.NextInt(count)];
                board = board.With(slot, board[slot] with { Director = true });
            }
        }
        if (camp)
            foreach (var facility in rules.Facilities)
                foreach (var effect in facility.Effects)
                    if (effect is { Type: 101, Condition: 2 })
                    {
                        var index = effect.Target - 3601;
                        board = board.With(index, board[index] with { HintMask = (byte)(board[index].SupportMask & ordinarySupportMask) });
                    }
        return board;
    }

    internal int DrawTuckerRepeat(bool island, ref SimulationRandom random)
    {
        var (total, noEvent, rewards) = tuckerRepeat[island ? 1 : 0];
        if (total == 0) return 0;
        var draw = random.NextInt(total) - noEvent;
        if (draw < 0) return 0;
        foreach (var reward in rewards)
            if ((draw -= reward.Count) < 0) return reward.Gain;
        throw new InvalidOperationException("Tucker 同训耐力频数与抽样总数不一致。");
    }

    internal TrainingFacility Facility(int id, int stateId) => facilityStates[(id, stateId)];

    internal ImmutableArray<TrainingFacility> CurrentFacilities(PioneerState state)
    {
        if (state.TurnBoard is { } board) return board.Facilities;
        var packed = state.GetSection(PioneerStateSection.FacilityStates);
        var facilities = ImmutableArray.CreateBuilder<TrainingFacility>(packed.Length / 2);
        for (var index = 0; index < packed.Length; index += 2)
            facilities.Add(facilityStates[(packed[index], packed[index + 1])]);
        return facilities.MoveToImmutable();
    }

    internal TrainingValues IslandBase(ImmutableArray<TrainingFacility> facilities)
        => TrainingBaseTable.FromFacilityRows(3101, facilityBases, facilities);

    internal void DrawIslandBoard(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var layout = CurrentFacilities(state);
        var built = 0;
        var houseLevel = 0;
        foreach (var facility in layout)
            if (facility.Id == 106) houseLevel = facility.Level;
            else if (facility.Level != 0) built |= 1 << (facility.Id - 101);

        var outdoor = new TrainingBoard();
        var used = 0;
        var hints = 0;
        var guests = header.ProjectNpcCount;
        for (var i = 0; i < 5; i++)
        {
            var group = header.TrainingBoard[i];
            hints |= group.HintMask;
            if ((built & (1 << i)) == 0) continue;
            outdoor = outdoor.With(i, group with { Director = false });
            used |= group.SupportMask;
            guests -= group.GuestCount;
        }

        var houseMask = 0;
        var houseGuests = 0;
        if (houseLevel != 0)
        {
            // shortcut: five seats are measured at Lv1; check the same capacity on Lv2/Lv3 when captured.
            var rest = AppearedMask(header) & ~used;
            houseMask = rest & ~ordinarySupportMask;
            Span<int> candidates = stackalloc int[6];
            var remaining = TrainingBoard.Positions((byte)(rest & ordinarySupportMask), candidates);
            var capacity = 5 - System.Numerics.BitOperations.PopCount((uint)houseMask);
            if (remaining <= capacity) houseMask |= rest & ordinarySupportMask;
            else
                for (var count = 0; count < capacity; count++)
                {
                    var draw = random.NextInt(remaining);
                    houseMask |= 1 << (candidates[draw] - 1);
                    candidates[draw] = candidates[--remaining];
                }
            houseGuests = Math.Min(guests, 5 - System.Numerics.BitOperations.PopCount((uint)houseMask));
        }
        foreach (var facility in layout)
        {
            if (facility.Id == 106) continue;
            var index = facility.Id - 101;
            var group = outdoor[index];
            foreach (var effect in facility.Effects)
            {
                if (effect is { Type: 101, Condition: 3 })
                    group = group with { HintMask = (byte)(group.SupportMask & ordinarySupportMask) };
                if (effect is { Type: 204, Condition: 3 })
                {
                    // Preserve normal-panel Hint flags. Add the conditional probability mass of the island bonus.
                    var mask = group.HintMask;
                    for (var slot = 0; slot < 6; slot++)
                    {
                        var bit = 1 << slot;
                        if ((group.SupportMask & ordinarySupportMask & bit) == 0 || (mask & bit) != 0) continue;
                        var distribution = state.TurnBoard!.Placement.Members[slot].Distribution;
                        if (distribution.SampleExtraHint(effect.Value, ref random)) mask |= (byte)bit;
                    }
                    group = group with { HintMask = mask };
                }
            }
            outdoor = outdoor.With(index, group);
        }
        header.IslandBoard = new(outdoor, new((byte)houseMask, (byte)(houseMask & hints), (byte)houseGuests));
        header.KnownParts |= PioneerKnownParts.IslandAssignments;
    }

    internal PioneerJointPlacement TrainingPolicyPlacement(PioneerState state)
        => CompileBoard(state, false, 0, projection: true).Placement;

    PioneerBoardRules CompileBoard(PioneerState state, bool camp, int conditional, bool projection = false)
    {
        var layout = projection ? ImmutableArray<TrainingFacility>.Empty : CurrentFacilities(state);
        var effects = layout.SelectMany(facility => facility.Effects).ToArray();
        var seaHouse = effects.Where(effect => effect is { Type: 205, Condition: 9 }).Sum(effect => effect.Value);
        var scope = camp ? 2 : 0;
        var turn = state.Header.Turn;
        var mask = projection ? appearedSupportMask | tuckerMask : AppearedMask(state.Header);
        Span<int> conditionalBonuses = stackalloc int[6];
        conditionalBonuses.Clear();
        for (var i = 0; i < conditionalSpecialties.Length; i++)
        {
            if (!projection && (conditional & (1 << i)) == 0) continue;
            var (position, effect) = conditionalSpecialties[i];
            conditionalBonuses[position - 1] += (effect.Parameter1 == 19 ? effect.Parameter2 : 0)
                + (effect.Parameter3 == 19 ? effect.Parameter4 : 0);
        }
        var start = state.Data.TrainingStart!;
        var temporary = new ImmutableArray<TemporarySpecialtyEffect>.Builder?[6];
        var grants = projection ? [] : state.GetSection(PioneerStateSection.TemporaryGrants);
        for (var i = 0; i < grants.Length; i += 6)
            if (grants[i + 4] == 19)
                (temporary[grants[i + 3] - 1] ??= ImmutableArray.CreateBuilder<TemporarySpecialtyEffect>())
                    .Add(new(grants[i], grants[i + 5]));
        var hint = 0;
        if (state.Header.CompletedCheckpoint > 0 && state.Data.Checkpoints is { } checkpoints)
            hint = checkpoints.Master.GetEffects(state.Header.CompletedCheckpoint, state.Header.CheckpointResult)
                .Single(effect => effect is { EffectType: 204, EffectValue1: 9 }).EffectValue2;
        var guests = projection ? 9 : state.Header.ProjectNpcCount;
        var members = ImmutableArray.CreateBuilder<PlacementMember>(6 + guests);
        foreach (var card in start.Deck)
        {
            var friend = card.TrainingCommandId == 0;
            var kind = friend ? PlacementMemberKind.Tucker : PlacementMemberKind.Training;
            var specialty = card.TrainingCommandId;
            members.Add(new(card.Position, (mask & (1 << (card.Position - 1))) != 0,
                new(new(kind, friend ? -1 : TrainingFacilityRules.PrimaryTarget(specialty) - 1, camp, seaHouse,
                    card.Effects.Where(effect => effect.Type == 19).Sum(effect => effect.Value),
                    card.UniqueEffects.Where(effect => effect.Type == 19).Sum(effect => effect.Value) + conditionalBonuses[card.Position - 1],
                    effects.Where(effect => effect.Type == 201 && effect.Condition == scope && effect.Target == specialty).Sum(effect => effect.Value),
                    card.Effects.Where(effect => effect.Type == 18).Sum(effect => effect.Value),
                    card.UniqueEffects.Where(effect => effect.Type == 18).Sum(effect => effect.Value), hint,
                    effects.Where(effect => effect is { Type: 204, Condition: 9 }).Sum(effect => effect.Value), false, turn,
                    temporary[card.Position - 1]?.ToImmutable() ?? []), Frequency(kind))));
        }
        var distribution = guests == 0 ? null : new PioneerPlacementDistribution(
            new(PlacementMemberKind.ProjectNpc, -1, camp, seaHouse, 0, 0, 0, 0, 0, 0, 0, false, turn, []), Frequency(PlacementMemberKind.ProjectNpc));
        for (var i = 0; i < guests; i++) members.Add(new(1000 + i, true, distribution!));
        var placement = new PioneerJointPlacement(members.MoveToImmutable());
        return new(placement, !camp ? [] : !WholeRun ? state.Data.CampCommands : [.. Enumerable.Range(3601, 5)
            .Select(command => TrainingNumericKernel.CompileCamp(command, 5, TrainingBaseTable.FromFacilityRows(command, facilityBases, layout), layout))],
            layout, conditional);

        PioneerPlacementFrequency Frequency(PlacementMemberKind kind)
        {
            if (placements.TryGetValue((kind, camp, seaHouse), out var measured)) return measured;
            // shortcut: reuse measured same-mode frequencies where a facility tier has no fit samples.
            var pooled = placements.Values.Where(row => row.Kind == kind && row.Camp == camp).ToArray();
            if (pooled.Length == 0) throw new UnknownMechanismException($"缺少 {kind}、合宿={camp} 的人员分布样本。");
            return new(kind, camp, seaHouse, pooled.Sum(row => row.Absent), pooled.Sum(row => row.Other));
        }
    }
}
