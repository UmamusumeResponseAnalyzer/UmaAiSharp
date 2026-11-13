using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.Pioneer.Runtime;

public enum ForecastActionKind { Home, Training, Rest, Event, Race, Island, Planning, Checkpoint, Live, Complete, Outing, Treatment }
public readonly record struct ForecastAction(ForecastActionKind Kind, int Id, int Choice = 0, PioneerConstructionPlan? Plan = null);
public readonly record struct ForecastNode(int Turn, ForecastActionKind Kind, int StoryId);
public readonly record struct ForecastChange(int Field, int Delta, bool Assign);
public sealed record ForecastObservation(ImmutableArray<int> Values, ImmutableArray<int> History);
public sealed record ForecastTransition(string Run, string Configuration, ForecastNode From, ForecastNode? To,
    ForecastAction Action, ForecastObservation Before, ImmutableArray<ForecastChange> Changes,
    int CompletedStory, int TrainingTemplate, bool TrainingSuccess, string Source, long PacketId,
    PioneerStart.IslandTraining? Island = null);
public sealed record ForecastEventReward(int TransitionIndex, int SupportPosition, EventRewardSample Sample);
public readonly record struct ForecastFacilityBase(int Command, int RequiredFacility, int Target, int Value);
public readonly record struct ForecastRaceProgram(int Id, int Grade, int RewardSet, int Ground, int Distance, int RequiredFans);
public sealed record ForecastModelInput(int SchemaVersion, string MasterSha256, string EvidenceSha256,
    ImmutableArray<string> Fields, ImmutableArray<int> Stories,
    ImmutableArray<int> FailureCoefficients, ImmutableArray<PioneerStart.TrainingTurn> TrainingTemplates, ImmutableArray<ForecastTransition> Transitions,
    ImmutableArray<ForecastEventReward> EventRewards, ImmutableArray<TrainingFacility> Facilities,
    ImmutableArray<ForecastFacilityBase> FacilityBases, PioneerMasterData? Construction,
    ImmutableArray<CheckpointPointBonus> CheckpointBonuses, ImmutableArray<ForecastRaceProgram> RacePrograms);

// Numeric state and event history condition a sampled transition; only increments are executable.
public sealed class PioneerForecastModel
{
    public const string Version = "pioneer-component-transitions-v5";
    public const int Unknown = int.MinValue;
    public ForecastModelInput Input { get; }
    public string ArtifactSha256 { get; }
    internal FrozenDictionary<(string Configuration, ForecastActionKind Kind, int Story), int[]> Nodes { get; }
    internal FrozenDictionary<(ForecastActionKind Kind, int Story), int[]> AllNodes { get; }
    internal FrozenDictionary<(ForecastActionKind Kind, int Id), int[]> ActionNodes { get; }
    readonly int[] simpleTrainingFailures;
    internal FrozenDictionary<(int Facility, int State), TrainingFacility> Facilities { get; }
    internal FrozenDictionary<int, ForecastRaceProgram> RacePrograms { get; }
    public FrozenDictionary<int, int> StoryIndices { get; }
    internal ForecastTraining[] Training { get; }
    internal FrozenDictionary<string, PioneerStart.TrainingTurn> Configurations { get; }
    public long SharedMemoryBytes { get; }

    public PioneerForecastModel(ForecastModelInput input, string artifactSha256)
    {
        if (input.SchemaVersion != 3 || input.Fields.IsDefaultOrEmpty || input.Stories.IsDefault || input.EventRewards.IsDefault
            || input.TrainingTemplates.IsDefault || input.Transitions.IsDefaultOrEmpty || input.Facilities.IsDefault
            || input.FacilityBases.IsDefault || input.CheckpointBonuses.IsDefault || input.RacePrograms.IsDefault
            || input.FailureCoefficients.IsDefault || input.FailureCoefficients.Length != 30 || input.FailureCoefficients.Any(value => value < 0)
            || input.Fields.Distinct().Count() != input.Fields.Length || input.Stories.Distinct().Count() != input.Stories.Length
            || !input.Fields.Take(PioneerForecastScenario.BasicFields.Length).SequenceEqual(PioneerForecastScenario.BasicFields))
            throw new InvalidDataException("预测模型的版本、字段、事件目录或转移表无效。");
        Input = input;
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);
        ArtifactSha256 = artifactSha256;
        StoryIndices = input.Stories.Select((story, index) => (story, index)).ToFrozenDictionary(x => x.story, x => x.index);
        Nodes = input.Transitions.Select((row, index) => (row, index))
            .GroupBy(x => (x.row.Configuration, x.row.From.Kind, x.row.From.StoryId)).ToFrozenDictionary(g => g.Key, g => g.Select(x => x.index).ToArray());
        AllNodes = input.Transitions.Select((row, index) => (row, index))
            .GroupBy(x => (x.row.From.Kind, x.row.From.StoryId)).ToFrozenDictionary(g => g.Key, g => g.Select(x => x.index).ToArray());
        ActionNodes = input.Transitions.Select((row, index) => (row, index)).Where(x => x.row.Action.Kind != ForecastActionKind.Event)
            .GroupBy(x => (x.row.Action.Kind, x.row.Action.Id)).ToFrozenDictionary(g => g.Key, g => g.Select(x => x.index).ToArray());
        Facilities = input.Facilities.ToFrozenDictionary(facility => (facility.Id, facility.StateId));
        RacePrograms = input.RacePrograms.ToFrozenDictionary(program => program.Id);
        foreach (var row in input.Transitions)
        {
            if (row.Before.Values.Length != input.Fields.Length || row.Before.History.Any(value => (uint)value >= input.Stories.Length)
                || row.Changes.IsDefault || row.Changes.Any(change => (uint)change.Field >= input.Fields.Length)
                || row.From.Turn is < 1 or > 78 || row.To is { } next && (next.Turn < row.From.Turn || next.Turn > row.From.Turn + 1 || next.Turn > 78)
                || row.TrainingTemplate < -1 || row.TrainingTemplate >= input.TrainingTemplates.Length
                || row.Action.Kind == ForecastActionKind.Training && row.TrainingTemplate < 0
                || row.CompletedStory != 0 && !StoryIndices.ContainsKey(row.CompletedStory)
                || row.Changes.Select(change => change.Field).Distinct().Count() != row.Changes.Length
                || string.IsNullOrWhiteSpace(row.Run) || string.IsNullOrWhiteSpace(row.Configuration)
                || !Enum.IsDefined(row.Action.Kind) || !Enum.IsDefined(row.From.Kind)
                || row.To is null && (row.From.Turn != 78 || row.Action is not { Kind: ForecastActionKind.Event, Choice: 0 }
                    || !PioneerEvent.IsCharacterEndingStory(row.From.StoryId)))
                throw new InvalidDataException($"预测转移{row.Source}:{row.PacketId}的维度、回合或训练模板无效。");
            ValidateValues(row.Before.Values.AsSpan());
        }
        simpleTrainingFailures = [.. Enumerable.Range(0, input.Transitions.Length - 1).Where(index =>
        {
            var attempt = input.Transitions[index];
            if (attempt.Action.Kind != ForecastActionKind.Training || attempt.TrainingSuccess) return false;
            var primary = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(attempt.Action.Id)) - 1;
            var choice = input.Transitions[index + 1];
            return attempt.Run == choice.Run && attempt.To == choice.From && attempt.From.Turn == choice.From.Turn
                && choice.Action is { Kind: ForecastActionKind.Event, Choice: 1 } && choice.To?.Turn == choice.From.Turn + 1
                && attempt.Changes.AsSpan().SequenceEqual([new(27 + primary, 1, false)])
                && choice.Changes.AsSpan().SequenceEqual([new(primary, -5, false), new(12, -1, false)]);
        })];
        var rewardIndices = new HashSet<int>();
        foreach (var reward in input.EventRewards)
        {
            if ((uint)reward.TransitionIndex >= input.Transitions.Length || reward.SupportPosition is < 0 or > 6
                || !rewardIndices.Add(reward.TransitionIndex))
                throw new InvalidDataException("事件资源模型的转移身份或支援位置无效。");
            var row = input.Transitions[reward.TransitionIndex];
            if (row.Action.Kind != ForecastActionKind.Event || row.Run != reward.Sample.Run || row.From.StoryId != reward.Sample.StoryId
                || row.Action.Choice != reward.Sample.Family.Choice || row.Action.Id != reward.Sample.Family.EventId)
                throw new InvalidDataException("事件资源模型与关联转移不一致。");
        }
        _ = new StatisticalEventRewards([.. input.EventRewards.Select(reward => reward.Sample)]);
        var decks = new Dictionary<string, CompiledTrainingDeck>();
        Training = [.. input.TrainingTemplates.Select(template =>
        {
            var key = string.Join(';', template.Deck.Select(card => $"{card.Position}:{card.CardId}:{card.Level}"));
            if (!decks.TryGetValue(key, out var deck)) decks.Add(key, deck = new(template.Deck));
            return new ForecastTraining(template, deck);
        })];
        Configurations = input.Transitions.Where(row => row.TrainingTemplate >= 0).GroupBy(row => row.Configuration)
            .ToFrozenDictionary(group => group.Key, group => input.TrainingTemplates[group.First().TrainingTemplate]);
        SharedMemoryBytes = checked(4096L + input.Transitions.Sum(row => 256L + 4L * row.Before.Values.Length
            + 4L * row.Before.History.Length + (long)System.Runtime.CompilerServices.Unsafe.SizeOf<ForecastChange>() * row.Changes.Length
            + 2L * (row.Run.Length + row.Configuration.Length + row.Source.Length)
            + (row.Action.Plan is { } plan ? 128L + 16L * plan.Steps.Length + 2L * plan.SemanticId.Length : 0)
            + (row.Island is null ? 0 : 16384L))
            + Training.Sum(template => template.Bytes)
            + input.EventRewards.Sum(reward => 128L + 16L * (reward.Sample.BaseEffects.Length + reward.Sample.RemainingEffects.Length))
            + 12L * input.Transitions.Length + 4L * simpleTrainingFailures.Length
            + 256L * (Nodes.Count + AllNodes.Count + ActionNodes.Count + StoryIndices.Count + Configurations.Count)
            + input.Facilities.Sum(facility => 128L + 16L * facility.Effects.Length)
            + 32L * input.FacilityBases.Length + 128L * input.RacePrograms.Length + 64L * input.CheckpointBonuses.Length
            + 64L * input.Fields.Length + 2L * input.Fields.Sum(field => field.Length)
            + (input.Construction is { } construction ? 4096L + 256L * (construction.FacilityKinds.Length + construction.FacilityStates.Length
                + construction.FacilityUpgrades.Length + construction.PlanningPeriods.Length + construction.TicketThresholds.Length) : 0));
        new ExecutionMemoryPlan(SharedMemoryBytes, 512L + 12L * input.Fields.Length + 4L * input.Stories.Length).Validate();
    }

    internal bool HasSimpleTrainingFailure(int commandId, string excludedRun)
    {
        var camp = TrainingBaseTable.OrdinaryCommand(commandId) != commandId;
        return simpleTrainingFailures.Any(index =>
        {
            var row = Input.Transitions[index];
            return row.Run != excludedRun && (TrainingBaseTable.OrdinaryCommand(row.Action.Id) != row.Action.Id) == camp;
        });
    }

    internal ForecastNode? TrainingFailureContinuation(int schedule)
    {
        var attempt = Input.Transitions[schedule];
        var next = attempt.To;
        // shortcut: keep the sampled event schedule, skipping success-only hints and upgrades, until same-turn failure schedules are available.
        for (var i = schedule + 1; next is { Kind: ForecastActionKind.Event } && next.Value.Turn == attempt.From.Turn
            && i < Input.Transitions.Length; i++)
        {
            var row = Input.Transitions[i];
            if (row.Run != attempt.Run || row.From != next) return null;
            if (row.From.StoryId is not (PioneerEvent.TrainingHintStory or TrainingLevelRules.StoryId)) break;
            next = row.To;
        }
        return next;
    }

    internal int Select(string configuration, ForecastNode node, ReadOnlySpan<int> values, ReadOnlySpan<int> history,
        ReadOnlySpan<long> scales, string excludedRun, ForecastAction? action, ref SimulationRandom random, bool? success = null,
        bool poolEventChoices = false)
    {
        var indices = GetIndices(configuration, node, action, excludedRun, poolEventChoices, success);
        var ticketField = Input.Fields.IndexOf("island_tickets");
        var raceField = Input.Fields.IndexOf("race_program_id");
        var raceProgram = node.Kind == ForecastActionKind.Race && raceField >= 0 ? values[raceField] : 0;
        if (raceProgram > 0 && ActionNodes.TryGetValue((ForecastActionKind.Race, action?.Id ?? node.StoryId), out var raceRows)) indices = raceRows;
        var visited = 0;
        foreach (var value in history) if (value != 0) visited++;
        var nearestTurn = int.MaxValue;
        var nearestDistance = long.MaxValue;
        var count = 0;
        foreach (var index in indices)
        {
            var row = Input.Transitions[index];
            if (!Eligible(row, excludedRun, action, success, poolEventChoices) || !CalendarMatches(row, node.Turn)
                || row.Action.Kind == ForecastActionKind.Island && ticketField >= 0 && values[ticketField] <= 0
                || raceProgram > 0 && !SameRaceFamily(row.Before.Values[raceField], raceProgram)) continue;
            var turnDistance = Math.Abs(node.Turn - row.From.Turn);
            if (turnDistance > nearestTurn) continue;
            var distance = Distance(row.Before, values, history, scales, visited);
            if (turnDistance < nearestTurn || distance < nearestDistance)
            {
                nearestTurn = turnDistance; nearestDistance = distance; count = 1;
            }
            else if (distance == nearestDistance) count++;
        }
        if (count == 0) return -1;
        var selected = count == 1 ? 0 : random.NextInt(count);
        foreach (var index in indices)
            if (Eligible(Input.Transitions[index], excludedRun, action, success, poolEventChoices) && CalendarMatches(Input.Transitions[index], node.Turn)
                && !(Input.Transitions[index].Action.Kind == ForecastActionKind.Island && ticketField >= 0 && values[ticketField] <= 0)
                && (raceProgram <= 0 || SameRaceFamily(Input.Transitions[index].Before.Values[raceField], raceProgram))
                && Math.Abs(node.Turn - Input.Transitions[index].From.Turn) == nearestTurn
                && Distance(Input.Transitions[index].Before, values, history, scales, visited) == nearestDistance && selected-- == 0) return index;
        throw new InvalidOperationException("条件转移抽样计数不一致。");
    }

    internal int[] GetIndices(string configuration, ForecastNode node, ForecastAction? action, string excludedRun, bool poolEventChoices = false, bool? success = null)
    {
        Nodes.TryGetValue((configuration, node.Kind, node.StoryId), out var local);
        AllNodes.TryGetValue((node.Kind, node.StoryId), out var shared);
        if (local is not null && local.Any(index => Input.Transitions[index].From.Turn == node.Turn && Eligible(Input.Transitions[index], excludedRun, action, success, poolEventChoices))) return local;
        if (shared is not null && shared.Any(index => Input.Transitions[index].From.Turn == node.Turn && Eligible(Input.Transitions[index], excludedRun, action, success, poolEventChoices))) return shared;
        if (local is not null && local.Any(index => CalendarMatches(Input.Transitions[index], node.Turn) && Eligible(Input.Transitions[index], excludedRun, action, success, poolEventChoices))) return local;
        if (shared is not null && shared.Any(index => CalendarMatches(Input.Transitions[index], node.Turn) && Eligible(Input.Transitions[index], excludedRun, action, success, poolEventChoices))) return shared;
        return action is { } selected && ActionNodes.TryGetValue((selected.Kind, selected.Id), out var related) ? related : [];
    }

    static bool CalendarMatches(ForecastTransition row, int turn) => row.From.Turn == turn
        || row.From.Kind != ForecastActionKind.Planning && row.To?.Kind != ForecastActionKind.Planning;

    bool Eligible(ForecastTransition row, string excludedRun, ForecastAction? action, bool? success, bool poolEventChoices = false)
        => row.Run != excludedRun && (action is null || row.Action.Kind == action.Value.Kind
            && (row.Action.Kind == ForecastActionKind.Event || row.Action.Id == action.Value.Id)
            && (row.Action.Kind == ForecastActionKind.Planning || poolEventChoices && row.Action.Kind == ForecastActionKind.Event
                || row.Action.Choice == action.Value.Choice || row.Action is { Kind: ForecastActionKind.Race, Id: 1 }
                    && SameRaceFamily(row.Action.Choice, action.Value.Choice)))
            && (success is null || row.TrainingSuccess == success);

    internal bool SameRaceFamily(int source, int target) => RacePrograms.TryGetValue(source, out var first)
        && RacePrograms.TryGetValue(target, out var second) && first.Grade == second.Grade
        && first.RewardSet == second.RewardSet && first.Ground == second.Ground;

    internal static void ValidateValues(ReadOnlySpan<int> values)
    {
        if (values.Length < PioneerForecastScenario.BasicFields.Length || values[..PioneerForecastScenario.BasicFields.Length].Contains(Unknown))
            throw new InvalidDataException("预测状态缺少属性、上限、体力、羁绊、训练等级或执行次数。");
        for (var attribute = 0; attribute < 5; attribute++)
            if (values[attribute] < 0 || values[attribute + 5] < values[attribute]) throw new InvalidDataException("预测状态的属性与上限无效。");
        if (values[10] < 0 || values[11] < values[10] || values[11] > VitalRules.Maximum || values[12] is < 1 or > 5
            || values[13] < 0 || values[14] < 0 || values[15] < 0)
            throw new InvalidDataException("预测状态的体力、干劲、技能Pt、粉丝或发展Pt无效。");
        for (var i = 16; i < 22; i++) if (values[i] is < 0 or > 100) throw new InvalidDataException("预测状态羁绊必须为0..100。");
        for (var i = 22; i < 27; i++) if (values[i] is < 1 or > 5) throw new InvalidDataException("预测状态训练等级必须为1..5。");
        for (var i = 27; i < 32; i++) if (values[i] < 0) throw new InvalidDataException("预测状态执行次数不能为负数。");
    }

    internal long[] CreateScales(string configuration, string excludedRun)
    {
        var minimum = Enumerable.Repeat(int.MaxValue, Input.Fields.Length).ToArray();
        var maximum = Enumerable.Repeat(int.MinValue, Input.Fields.Length).ToArray();
        foreach (var row in Input.Transitions)
            if (row.Configuration == configuration && row.Run != excludedRun)
                for (var i = 0; i < minimum.Length; i++)
                    if (row.Before.Values[i] != Unknown)
                    {
                        minimum[i] = Math.Min(minimum[i], row.Before.Values[i]);
                        maximum[i] = Math.Max(maximum[i], row.Before.Values[i]);
                    }
        return [.. minimum.Select((value, index) => Math.Max(1, (long)maximum[index] - value))];
    }

    internal string SelectConfiguration(string requested, PioneerStart.TrainingTurn target, string excludedRun)
    {
        var available = Input.Transitions.Where(row => row.Run != excludedRun && row.TrainingTemplate >= 0)
            .GroupBy(row => row.Configuration).Select(group => (Configuration: group.Key, Start: Training[group.First().TrainingTemplate].Start));
        // This distance selects empirical context, not a game rule or an equivalence of card-specific events.
        return available.OrderBy(item => item.Configuration == requested ? 0 : 1)
            .ThenBy(item => item.Start.CharacterId == target.CharacterId ? 0 : 1)
            .ThenBy(item => item.Start.Deck.Zip(target.Deck).Count(pair => pair.First.TrainingCommandId != pair.Second.TrainingCommandId))
            .ThenBy(item => item.Start.Deck.Zip(target.Deck).Count(pair => pair.First.CardId != pair.Second.CardId))
            .ThenBy(item => item.Start.Deck.Zip(target.Deck).Sum(pair => Math.Abs(pair.First.Level - pair.Second.Level)))
            .ThenBy(item => item.Configuration, StringComparer.Ordinal)
            .Select(item => item.Configuration).FirstOrDefault() ?? requested;
    }

    internal TrainingValues FacilityBase(int command, ImmutableArray<TrainingFacility> facilities)
    {
        Span<int> values = stackalloc int[7];
        values.Clear();
        foreach (var row in Input.FacilityBases)
        {
            if (row.Command != command || row.RequiredFacility != 0 && !facilities.Any(facility => facility.Id == row.RequiredFacility && facility.Level > 0)) continue;
            var index = row.Target switch { >= 1 and <= 5 => row.Target - 1, 10 => 5, 30 => 6, _ => throw new InvalidDataException("设施基础收益target无效。") };
            if (values[index] != 0 && values[index] != row.Value) throw new InvalidDataException("设施基础收益冲突。");
            values[index] = row.Value;
        }
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);
    }

    static long Distance(ForecastObservation sample, ReadOnlySpan<int> values, ReadOnlySpan<int> history, ReadOnlySpan<long> scales, int visited)
    {
        var distance = 0L;
        for (var i = 0; i < values.Length; i++)
            if (values[i] != Unknown && sample.Values[i] != Unknown)
                distance += Math.Abs((long)values[i] - sample.Values[i]) * 1024 / scales[i];
        var overlap = 0;
        foreach (var index in sample.History) if (history[index] != 0) overlap++;
        distance += (visited + sample.History.Length - 2L * overlap) * 1024;
        return distance;
    }

    internal sealed class ForecastTraining
    {
        public PioneerStart.TrainingTurn Start { get; }
        public CompiledTrainingDeck Deck { get; }
        public CompiledCampTraining[] Camp { get; }
        public long Bytes => Deck.SharedMemoryBytes + 8192;
        public ForecastTraining(PioneerStart.TrainingTurn start, CompiledTrainingDeck? deck = null)
        {
            int[] ordinary = [101, 105, 102, 103, 106];
            if (!start.Deck.Select(card => card.Position).SequenceEqual(Enumerable.Range(1, 6))
                || !start.Commands.Select(command => command.CommandId).SequenceEqual(start.Camp is null ? ordinary : Enumerable.Range(3601, 5)))
                throw new InvalidDataException("训练模板必须按五属性顺序提供五项训练，并按位置提供六张支援。");
            Start = start;
            Deck = deck ?? new(start.Deck);
            Camp = start.Camp is { } camp ? [.. start.Commands.Select((command, index) =>
                TrainingNumericKernel.CompileCamp(command.CommandId, command.Level, camp.Bases[index], camp.Facilities))] : [];
        }
    }
}
