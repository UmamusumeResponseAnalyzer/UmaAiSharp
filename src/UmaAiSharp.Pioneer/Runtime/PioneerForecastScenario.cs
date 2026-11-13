using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Scoring;
using UmaAiSharp.Pioneer.Training;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;
using System.Collections.Immutable;
using System.Collections.Frozen;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed class PioneerForecastState
{
    internal int[] Values, History, Scratch;
    internal ForecastNode Node;
    internal bool Root = true, Terminal;
    internal long GainedSkillPoints;
    internal AttributeValues Overflow;
    internal int Steps;
    internal PioneerConstructionPlan? Plan;
    public PioneerForecastState(ForecastNode node, ReadOnlySpan<int> values, ReadOnlySpan<int> history)
    { Node = node; Values = values.ToArray(); History = history.ToArray(); Scratch = new int[values.Length]; }
    public ReadOnlySpan<int> Fields => Values;
    public int Turn => Node.Turn;
}

// The memories are borrowed for this policy call and are never retained by the continuation policy.
public readonly record struct PioneerForecastDecision(ForecastNode Node, ReadOnlyMemory<int> Values,
    ReadOnlyMemory<int> History, bool Root, PioneerConstructionPlan? Plan);

public sealed class PioneerForecastScenario : ISimulationScenario<PioneerForecastState, PioneerForecastDecision, ForecastAction, TerminalMetrics>
{
    public static readonly ImmutableArray<string> BasicFields = ["speed", "stamina", "power", "guts", "wiz", "max_speed", "max_stamina",
        "max_power", "max_guts", "max_wiz", "vital", "max_vital", "motivation", "skill_point", "fans", "pioneer_pt",
        "bond:1", "bond:2", "bond:3", "bond:4", "bond:5", "bond:6", "level:101", "level:105", "level:102", "level:103", "level:106",
        "exec:101", "exec:105", "exec:102", "exec:103", "exec:106"];
    readonly PioneerForecastModel model;
    readonly string configuration, excludedRun;
    readonly PioneerForecastModel.ForecastTraining rootTraining;
    readonly PioneerForecastModel.ForecastTraining[] targetTraining;
    readonly ForecastAction[] rootActions;
    readonly int[] effectFields;
    readonly int charmField;
    readonly (int Field, int Effect)[] failureEffects;
    readonly long[] scales;
    readonly FrozenDictionary<int, (int Position, DeclaredEventRewards[] Rewards, bool HasRemaining)> eventRewards;
    readonly int positiveThinkingField, migraineField;
    readonly long eventMemoryBytes;
    readonly PioneerStart.DeclaredEvent? rootEvent;
    readonly PioneerStart.IslandTraining? rootIsland;
    readonly PioneerDevelopmentState? rootDevelopment;
    readonly PioneerConstructionRules? construction;
    readonly int[] facilityFields;
    public string RulesIdentity { get; }
    public string EmpiricalConfiguration => configuration;
    public long SharedMemoryBytes => model.SharedMemoryBytes + rootTraining.Bytes + 8L * scales.Length + eventMemoryBytes + targetTraining.Length * 8192L;

    public PioneerForecastScenario(PioneerForecastModel model, string configuration, string excludedRun,
        PioneerStart.TrainingTurn root, ForecastAction[] rootActions,
        PioneerStart.DeclaredEvent? rootEvent = null, PioneerStart.IslandTraining? rootIsland = null,
        PioneerDevelopmentState? development = null)
    {
        if (!model.Input.Fields.Take(BasicFields.Length).SequenceEqual(BasicFields))
            throw new InvalidDataException("预测模型基础字段布局不一致。");
        if (root.CharacterId == 1007 || root.Environment.GoldShipBoost is not null)
            throw new UnknownMechanismException("黄金船本回合指定训练效果不能延续到全部未来回合；该角色的未来事件历史与目标生成尚未接入预测。");
        this.model = model; this.excludedRun = excludedRun;
        this.configuration = configuration = model.SelectConfiguration(configuration, root, excludedRun);
        rootTraining = new(root); this.rootActions = (ForecastAction[])rootActions.Clone();
        this.rootEvent = rootEvent; this.rootIsland = rootIsland; rootDevelopment = development;
        construction = model.Input.Construction is { } master ? new(master) : null;
        facilityFields = [.. Enumerable.Range(101, 6).Select(id => model.Input.Fields.IndexOf($"facility:{id}"))];
        targetTraining = [.. model.Input.TrainingTemplates.Select(template => new PioneerForecastModel.ForecastTraining(template with
        {
            CardId = root.CardId, CharacterId = root.CharacterId, Deck = root.Deck,
            Environment = template.Environment with { GrowthPercent = root.Environment.GrowthPercent, GoldShipBoost = null },
            PlanningPeriods = root.PlanningPeriods,
        }, rootTraining.Deck))];
        scales = model.CreateScales(configuration, excludedRun);
        effectFields = [.. model.Input.Fields.Select((field, index) => (field, index))
            .Where(x => x.field.StartsWith("effect:", StringComparison.Ordinal)).Select(x => x.index)];
        charmField = model.Input.Fields.IndexOf("effect:8");
        positiveThinkingField = model.Input.Fields.IndexOf("effect:25");
        migraineField = model.Input.Fields.IndexOf("effect:5");
        failureEffects = [.. effectFields.Select(field => (field, int.Parse(model.Input.Fields[field].AsSpan(7), System.Globalization.CultureInfo.InvariantCulture)))];
        var rewards = new Dictionary<int, (int, DeclaredEventRewards[], bool)>();
        foreach (var item in model.Input.EventRewards)
        {
            var row = model.Input.Transitions[item.TransitionIndex];
            if (row.Run == excludedRun) continue;
            var compiled = new Dictionary<EventEffectScaling, DeclaredEventRewards>();
            var byBond = new DeclaredEventRewards[item.SupportPosition == 0 ? 1 : 101];
            for (var bond = 0; bond < byBond.Length; bond++)
            {
                var scaling = item.SupportPosition == 0 ? default : EventEffectScaling.FromSupport(root.Deck[item.SupportPosition - 1] with { Bond = bond });
                if (!compiled.TryGetValue(scaling, out var value))
                {
                    value = new(new([.. item.Sample.BaseEffects.Select(atom => atom.DisplayId == 1
                        ? atom with { Value1 = scaling.Apply(atom.Value0, atom.Value1) } : atom)]));
                    compiled.Add(scaling, value);
                    eventMemoryBytes += value.SharedMemoryBytes;
                }
                byBond[bond] = value;
            }
            rewards.Add(item.TransitionIndex, (item.SupportPosition, byBond, !item.Sample.RemainingEffects.IsEmpty));
            eventMemoryBytes += 128L + 8L * byBond.Length;
        }
        eventRewards = rewards.ToFrozenDictionary();
        RulesIdentity = PioneerForecastModel.Version + "/" + model.ArtifactSha256 + "/" + model.Input.MasterSha256
            + "/" + TrainingFormulaCandidate.Version + "/" + StatisticalEventRewards.Version + "/" + DeclaredEventRewards.Version;
    }

    public PioneerForecastState CreateWorkspace(in PioneerForecastState source)
    {
        if (source.Values.Length != model.Input.Fields.Length || source.History.Length != model.Input.Stories.Length
            || source.History.AsSpan().ContainsAnyExcept(0, 1) || source.Node.Turn is < 1 or > 78)
            throw new InvalidDataException("预测起点的字段、历史或回合与模型不一致。");
        PioneerForecastModel.ValidateValues(source.Values);
        var result = new PioneerForecastState(source.Node, source.Values, source.History);
        CopyState(source, ref result);
        return result;
    }
    public long GetWorkspaceBytes(in PioneerForecastState state) => 512L + 12L * state.Values.Length + 4L * state.History.Length;
    public void CopyState(in PioneerForecastState source, ref PioneerForecastState destination)
    {
        source.Values.CopyTo(destination.Values, 0); source.History.CopyTo(destination.History, 0);
        destination.Node = source.Node; destination.Root = source.Root; destination.Terminal = source.Terminal;
        destination.Steps = source.Steps; destination.GainedSkillPoints = source.GainedSkillPoints; destination.Overflow = source.Overflow;
        destination.Plan = source.Plan;
    }
    public Inspection<PioneerForecastDecision> Inspect(in PioneerForecastState state, long decisionOrdinal)
        => state.Terminal ? Inspection<PioneerForecastDecision>.Terminal(state.Node.Turn)
            : Inspection<PioneerForecastDecision>.AtDecision(new(new((int)state.Node.Kind, decisionOrdinal,
                ((long)state.Node.Turn << 32) | (uint)state.Node.StoryId), new(state.Node, state.Values, state.History, state.Root, state.Plan)));

    public SimulationStop? ValidateAction(in PioneerForecastState state, in ForecastAction action)
    {
        if (!state.Terminal)
        {
            if (action.Kind == ForecastActionKind.Planning && state.Node.Kind == ForecastActionKind.Planning)
            {
                if (action.Plan is null || construction is null || rootDevelopment is null)
                    return new(SimulationStopReason.UnknownMechanism, 2014, state.Node.Turn);
                try { construction.ValidateForSubmission(Development(state.Node, state.Values, state.Plan), action.Plan); }
                catch (IllegalActionException) { return new(SimulationStopReason.IllegalAction, 2015, state.Node.Turn); }
                return null;
            }
            if (state.Root && rootActions.Contains(action)) return null;
            if (!state.Root)
                foreach (var index in model.GetIndices(configuration, state.Node, action, excludedRun))
                    if (model.Input.Transitions[index].Run != excludedRun && model.Input.Transitions[index].Action == action) return null;
        }
        return new(SimulationStopReason.IllegalAction, 2001, state.Node.Turn);
    }

    public TransitionResult ApplyAction(ref PioneerForecastState state, in ForecastAction action, ref SimulationRandom random)
    {
        if (ValidateAction(state, action) is { } invalid) return TransitionResult.Stopped(false, false, invalid);
        // shortcut: cyclic empirical transitions stop at a fixed computation budget; this is not a game turn limit.
        if (state.Steps >= 2048) return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2004, state.Node.Turn));
        var nextRandom = random;
        var index = model.Select(configuration, state.Node, state.Values, state.History, scales, excludedRun, action, ref nextRandom,
            poolEventChoices: state.Root && rootEvent is not null);
        if (index < 0) return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2002, state.Node.Turn));
        var row = model.Input.Transitions[index];
        var template = state.Root ? rootTraining : row.TrainingTemplate >= 0 ? targetTraining[row.TrainingTemplate] : null;
        if (action.Kind == ForecastActionKind.Training && template is null)
            throw new InvalidOperationException("训练转移缺少已编译数值模板。");
        Span<int> knownSource = stackalloc int[BasicFields.Length];
        Span<int> knownCurrent = stackalloc int[BasicFields.Length];
        knownSource.Clear(); knownCurrent.Clear();
        var overflow = default(AttributeValues);
        EventResourceResult? eventResult = null;
        DeclaredEventSettlement? rootSettlement = null;
        if (state.Root && rootEvent is { } currentEvent)
        {
            var choiceNumber = action.Choice;
            var choice = currentEvent.Choices.Single(item => item.ChoiceNumber == choiceNumber);
            if (choice.Branches.Length != 1)
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2009, state.Node.Turn, action.Id, action.Choice));
            rootSettlement = new(choice.Branches[0], currentEvent.Partners);
            try
            {
                eventResult = rootSettlement.Resources.Apply(Resources(state.Values), Limits(state.Values), state.Values[12],
                    currentEvent.CharacterEffects.Contains(25), currentEvent.CharacterEffects.Contains(5));
            }
            catch (UnknownMechanismException)
            {
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2007, state.Node.Turn, action.Id));
            }
        }
        if (eventResult is null && eventRewards.TryGetValue(index, out var calibrated))
        {
            var migraine = migraineField >= 0 && state.Values[migraineField] == 1;
            if (migraine && calibrated.HasRemaining)
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2008, state.Node.Turn, action.Id));
            try
            {
                eventResult = calibrated.Rewards[calibrated.Position == 0 ? 0 : state.Values[15 + calibrated.Position]]
                    .Apply(Resources(state.Values), Limits(state.Values), state.Values[12],
                        positiveThinkingField >= 0 && state.Values[positiveThinkingField] == 1, migraine);
            }
            catch (UnknownMechanismException)
            {
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2007, state.Node.Turn, action.Id));
            }
        }
        if (action.Kind == ForecastActionKind.Training)
        {
            try
            {
                var preview = Preview(template!, action.Id, state.Values);
                var success = preview.FailureRate == 0 || preview.FailureRate < 100 && nextRandom.NextInt(100) >= preview.FailureRate;
                if (!success && model.HasSimpleTrainingFailure(action.Id, excludedRun))
                {
                    var schedule = row.TrainingSuccess ? index : model.Select(configuration, state.Node, state.Values,
                        state.History, scales, excludedRun, action, ref nextRandom, true);
                    if (schedule < 0 || model.TrainingFailureContinuation(schedule) is not { } continuation)
                        return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2005, state.Node.Turn, action.Id));
                    var turn = state.Node.Turn + continuation.Turn - model.Input.Transitions[schedule].From.Turn;
                    if (turn is < 1 or > 78)
                        return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2003, state.Node.Turn));
                    var primary = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(action.Id)) - 1;
                    var executions = checked(state.Values[27 + primary] + 1);
                    var positiveThinking = positiveThinkingField >= 0 && state.Values[positiveThinkingField] == 1;
                    var (motivation, consumePositiveThinking) = MotivationRules.Apply(state.Values[12], -1, positiveThinking, false);
                    state.Values[27 + primary] = executions;
                    state.Values[primary] = Math.Max(0, state.Values[primary] - 5);
                    state.Values[12] = motivation;
                    if (consumePositiveThinking) state.Values[positiveThinkingField] = 0;
                    state.Node = continuation with { Turn = turn };
                    state.Root = false; state.Steps++;
                    random = nextRandom;
                    return TransitionResult.Settled(turn);
                }
                if (row.TrainingSuccess != success)
                {
                    index = model.Select(configuration, state.Node, state.Values, state.History, scales, excludedRun, action, ref nextRandom, success);
                    if (index < 0) return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2005, state.Node.Turn, action.Id, success ? 1 : 0));
                    row = model.Input.Transitions[index];
                }
                var sourceTemplate = model.Training[row.TrainingTemplate];
                CalculateTraining(sourceTemplate, row.Action.Id, row.Before.Values.AsSpan(), row.From.Turn, success,
                    success ? Preview(sourceTemplate, row.Action.Id, row.Before.Values.AsSpan()) : default, knownSource, out _);
                CalculateTraining(template!, action.Id, state.Values, state.Node.Turn, success, preview, knownCurrent, out overflow);
            }
            catch (UnknownMechanismException)
            {
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2017, state.Node.Turn, action.Id));
            }
        }
        else if (action.Kind == ForecastActionKind.Event && RestRules.IsStory(state.Node.StoryId, (state.Node.StoryId - 500000000) / 1000))
        {
            var character = (state.Node.StoryId - 500000000) / 1000;
            var gain = RestRules.Recovery(new(action.Id, character, state.Node.StoryId, 6), character);
            knownSource[10] = Math.Min(row.Before.Values[11], row.Before.Values[10] + gain) - row.Before.Values[10];
            knownCurrent[10] = Math.Min(state.Values[11], state.Values[10] + gain) - state.Values[10];
        }
        else if (action is { Kind: ForecastActionKind.Rest, Id: RestRules.CampCommand })
        {
            knownSource[10] = Math.Min(row.Before.Values[11], row.Before.Values[10] + 40) - row.Before.Values[10];
            knownCurrent[10] = Math.Min(state.Values[11], state.Values[10] + 40) - state.Values[10];
            knownSource[12] = row.Before.Values[12] < 5 ? 1 : 0;
            knownCurrent[12] = state.Values[12] < 5 ? 1 : 0;
        }
        if (action.Kind == ForecastActionKind.Event && CheckpointSettlement.ObservedEventOutcomes.TryGetValue(state.Node.StoryId, out var checkpoint))
        {
            var bonus = model.Input.CheckpointBonuses.Single(item => item.CheckPointType == checkpoint.Type);
            var targetPeriod = rootTraining.Start.PlanningPeriods.Single(period => period.CheckPointType == checkpoint.Type);
            var sourcePeriod = model.Configurations[row.Configuration].PlanningPeriods.Single(period => period.CheckPointType == checkpoint.Type);
            knownSource[10] = (int)Math.Min(row.Before.Values[11] - row.Before.Values[10], Math.Min(bonus.AddValueMax,
                Math.Max(0L, row.Before.Values[15] - sourcePeriod.TargetPt) * bonus.PtRate / 1000));
            knownCurrent[10] = (int)Math.Min(state.Values[11] - state.Values[10], Math.Min(bonus.AddValueMax,
                Math.Max(0L, state.Values[15] - targetPeriod.TargetPt) * bonus.PtRate / 1000));
            knownSource[15] = -row.Before.Values[15]; knownCurrent[15] = -state.Values[15];
        }
        var nextTurn = row.To is { } next ? checked(state.Node.Turn + next.Turn - row.From.Turn) : state.Node.Turn;
        if (nextTurn is < 1 or > 78 || row.To is null && nextTurn != 78)
            return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2003, state.Node.Turn));
        var oldPoints = state.Values[13];
        var values = state.Scratch;
        state.Values.CopyTo(values, 0);
        // Remove the donor's deterministic training component before applying the current action's component.
        // shortcut: other effects use observed clipped increments; replace these components as their rules are integrated.
        foreach (var change in row.Changes)
        {
            if (eventResult is not null && (change.Field < 5 || change.Field is >= 10 and < 14)) continue;
            if (rootDevelopment is not null && facilityFields.Contains(change.Field)) continue;
            if (rootDevelopment is not null && state.Node.StoryId is 400011107 or 400011108
                && model.Input.Fields[change.Field] == "island_tickets") continue;
            if (change.Assign) { values[change.Field] = change.Delta; continue; }
            if (values[change.Field] == PioneerForecastModel.Unknown) continue;
            var value = change.Delta - (change.Field < knownSource.Length ? knownSource[change.Field] : 0);
            values[change.Field] = checked(values[change.Field] + value);
            if (change.Field < knownSource.Length) knownSource[change.Field] = 0;
        }
        for (var field = 0; field < knownCurrent.Length; field++)
            values[field] = checked(values[field] + knownCurrent[field] - knownSource[field]);
        if (action is { Kind: ForecastActionKind.Race, Id: 1 })
        {
            var race = model.Input.Fields.IndexOf("race_program_id");
            if (race >= 0) values[race] = action.Choice;
        }
        if (eventResult is { } reward)
        {
            values[0] = reward.Resources.Speed; values[1] = reward.Resources.Stamina; values[2] = reward.Resources.Power;
            values[3] = reward.Resources.Guts; values[4] = reward.Resources.Wisdom;
            values[10] = reward.Resources.Vital; values[11] = reward.Limits.Vital;
            values[12] = reward.Motivation; values[13] = reward.Resources.SkillPoints;
            if (positiveThinkingField >= 0) values[positiveThinkingField] = reward.ConsumePositiveThinking ? 0 : state.Values[positiveThinkingField];
            overflow += reward.Overflow;
        }
        if (rootSettlement is { HasPartnerEffects: true } && rootEvent!.Partners is { } partners)
        {
            var partnerValues = new int[partners.Length * 2];
            for (var i = 0; i < partners.Length; i++) { partnerValues[2 * i] = partners[i].Bond; partnerValues[2 * i + 1] = partners[i].OutingUnlocked ? 1 : 0; }
            rootSettlement.ApplyPartners(partnerValues);
            for (var i = 0; i < partners.Length; i++)
            {
                var bondField = model.Input.Fields.IndexOf($"bond:{partners[i].TargetId}");
                var outingField = model.Input.Fields.IndexOf($"outing:{partners[i].TargetId}");
                if (bondField < 0 || outingField < 0) return TransitionResult.Stopped(false, false,
                    new(SimulationStopReason.UnknownMechanism, 2010, state.Node.Turn, partners[i].TargetId));
                values[bondField] = partnerValues[2 * i]; values[outingField] = partnerValues[2 * i + 1];
            }
        }
        if (action.Kind == ForecastActionKind.Island)
        {
            var island = state.Root ? rootIsland : row.Island;
            if (island is null) return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2011, state.Node.Turn));
            try
            {
                var beforeValues = state.Values;
                var assignments = island.Training.Assignments.Select(group => group with
                {
                    Supports = [.. group.Supports.Select(card => rootTraining.Start.Deck[card.Position - 1] with { Bond = beforeValues[15 + card.Position] })]
                }).ToImmutableArray();
                var facilities = CurrentFacilities(state.Values, island.Training.Facilities);
                var currentTraining = island.Training with
                {
                    Environment = rootTraining.Start.Environment with { Current = Resources(state.Values), Limits = Limits(state.Values), Motivation = state.Values[12], Fans = state.Values[14] },
                    OrdinaryLevels = [.. state.Values.AsSpan(22, 5)], Assignments = assignments, Facilities = facilities,
                    Base = model.Input.FacilityBases.IsEmpty ? island.Training.Base : model.FacilityBase(3101, facilities),
                };
                var gains = TrainingNumericKernel.GenerateIsland(currentTraining, rootTraining.Deck);
                var after = TrainingPreviewSettlement.Apply(currentTraining.Environment.Current, currentTraining.Environment.Limits, gains.Common, gains.Scenario);
                values[0] = after.Speed; values[1] = after.Stamina; values[2] = after.Power; values[3] = after.Guts; values[4] = after.Wisdom;
                values[10] = after.Vital; values[13] = after.SkillPoints;
                var ticket = model.Input.Fields.IndexOf("island_tickets");
                if (ticket < 0 || state.Values[ticket] <= 0) return TransitionResult.Stopped(false, false, new(SimulationStopReason.IllegalAction, 2012, state.Node.Turn));
                values[ticket] = state.Values[ticket] - 1;
                var friends = assignments.Any(group => group.Supports.Any(card => card.Bond >= 80 && card.TrainingCommandId == TrainingFacilityRules.Command(group.FacilityId)));
                values[15] = checked(state.Values[15] + UmaAiSharp.Pioneer.IslandTraining.IslandActionRules.DevelopmentPointGain(island.State.CompletedCheckpoint,
                    island.State.CheckpointBonusPercent, assignments.Sum(group => group.Supports.Length + group.GuestCount), friends));
                for (var position = 1; position <= 6; position++) values[15 + position] = Math.Min(100,
                    state.Values[15 + position] + (rootTraining.Start.Deck[position - 1].TrainingCommandId == 0 ? 7 : 10)
                    + (charmField >= 0 && state.Values[charmField] == 1 ? 2 : 0));
                overflow += gains.Overflow;
            }
            catch (UnknownMechanismException) { return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2013, state.Node.Turn)); }
        }
        if (rootDevelopment is not null && construction is not null && state.Node.StoryId is 400011107 or 400011108)
        {
            var currentPlan = action.Plan ?? state.Plan ?? rootDevelopment.Plan;
            var turn = state.Node.Turn;
            var period = rootTraining.Start.PlanningPeriods.LastOrDefault(period => period.PlanningTurn <= turn);
            if (period is not null && turn <= 60)
            {
                var milestone = construction.Data.TicketThresholds.Where(threshold => threshold.DeckId == rootDevelopment.DeckId
                    && threshold.TurnMin <= turn && turn <= threshold.TurnMax
                    && threshold.GainShimaTrainingPt <= values[15]).Select(threshold => threshold.GainShimaTrainingPt).DefaultIfEmpty(0).Max();
                var analysis = construction.Analyze(currentPlan, period.TargetPt);
                foreach (var step in analysis.Steps)
                {
                    var field = facilityFields[step.Step.FacilityId - 101];
                    if (step.CumulativeCost > milestone || values[field] == step.Step.TargetStateId) continue;
                    if (values[field] != step.Step.PreviousStateId)
                        return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2016, state.Node.Turn, step.Step.FacilityId));
                    values[field] = step.Step.TargetStateId;
                }
                if (milestone > 0)
                {
                    var ticket = model.Input.Fields.IndexOf("island_tickets");
                    if (ticket < 0) return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2012, state.Node.Turn));
                    values[ticket] = 1;
                }
            }
        }
        for (var attribute = 0; attribute < 5; attribute++)
        {
            var extra = Math.Max(0, values[attribute] - values[attribute + 5]);
            var amount = new AttributeValues(attribute == 0 ? extra : 0, attribute == 1 ? extra : 0,
                attribute == 2 ? extra : 0, attribute == 3 ? extra : 0, attribute == 4 ? extra : 0);
            overflow += amount;
            values[attribute] = Math.Clamp(values[attribute], 0, values[attribute + 5]);
        }
        values[11] = Math.Clamp(values[11], 1, VitalRules.Maximum);
        values[10] = Math.Clamp(values[10], 0, values[11]);
        values[12] = Math.Clamp(values[12], 1, 5);
        values[13] = Math.Max(0, values[13]);
        for (var i = 16; i < 22; i++) values[i] = Math.Clamp(values[i], 0, 100);
        for (var i = 22; i < 27; i++) values[i] = Math.Clamp(values[i], 1, 5);
        foreach (var field in effectFields)
            if (values[field] != PioneerForecastModel.Unknown) values[field] = Math.Clamp(values[field], 0, 1);
        ReadOnlySpan<int> nonnegative = [14, 15, 27, 28, 29, 30, 31];
        foreach (var field in nonnegative)
            if (values[field] < 0)
                return TransitionResult.Stopped(false, false, new(SimulationStopReason.UnknownMechanism, 2006, state.Node.Turn, field, values[field]));
        state.GainedSkillPoints = checked(state.GainedSkillPoints + Math.Max(0, values[13] - oldPoints));
        state.Overflow += overflow;
        (state.Values, state.Scratch) = (values, state.Values);
        if (row.CompletedStory != 0) state.History[model.StoryIndices[row.CompletedStory]] = 1;
        state.Root = false; state.Steps++;
        if (action.Plan is { } plan) state.Plan = plan;
        state.Terminal = row.To is null;
        if (row.To is { } target) state.Node = target with { Turn = nextTurn };
        random = nextRandom;
        return TransitionResult.Settled(state.Node.Turn);
    }

    TrainingActionPreview Preview(PioneerForecastModel.ForecastTraining template, int commandId, ReadOnlySpan<int> values)
    {
        var primary = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(commandId)) - 1;
        var command = template.Start.Commands[primary];
        if (command.CommandId != commandId) throw new InvalidDataException("预测训练模板的命令顺序或模式不一致。");
        var environment = template.Start.Environment with { Current = Resources(values), Limits = Limits(values), Motivation = values[12], Fans = values[14] };
        var bias = 0;
        foreach (var (field, effect) in failureEffects) if (values[field] == 1) bias += TrainingFailureRate.StatusBias(effect);
        var camp = template.Camp.Length != 0;
        var level = camp ? 5 : values[22 + primary];
        var coefficient = model.Input.FailureCoefficients[camp ? 25 + primary : primary * 5 + level - 1];
        var compiledCamp = template.Camp.Length == 0 ? (CompiledCampTraining?)null : template.Camp[primary];
        if (compiledCamp is not null && !model.Input.Facilities.IsEmpty)
        {
            var facilities = CurrentFacilities(values, template.Start.Camp!.Facilities);
            compiledCamp = TrainingNumericKernel.CompileCamp(commandId, 5, model.FacilityBase(commandId, facilities), facilities);
        }
        return TrainingNumericKernel.Preview(environment, commandId, level,
            command.GuestCount, command.EvaluationTrainingPercent, coefficient, bias,
            command.SupportPositions.AsSpan(), values.Slice(16, 6), template.Deck, compiledCamp);
    }

    ImmutableArray<TrainingFacility> CurrentFacilities(ReadOnlySpan<int> values, ImmutableArray<TrainingFacility> source)
    {
        if (model.Input.Facilities.IsEmpty) return source;
        var result = ImmutableArray.CreateBuilder<TrainingFacility>(6);
        for (var i = 0; i < 6; i++)
        {
            if (facilityFields[i] < 0 || !model.Facilities.TryGetValue((101 + i, values[facilityFields[i]]), out var facility))
                throw new UnknownMechanismException($"设施{101 + i}缺少当前状态或master定义。");
            result.Add(facility);
        }
        return result.MoveToImmutable();
    }

    PioneerDevelopmentState Development(ForecastNode node, ReadOnlySpan<int> values, PioneerConstructionPlan? plan)
    {
        if (rootDevelopment is null || construction is null) throw new UnknownMechanismException("预测缺少当前建设状态与master。");
        var period = rootTraining.Start.PlanningPeriods.SingleOrDefault(period => period.PlanningTurn == node.Turn)
            ?? throw new UnknownMechanismException($"经验调度在回合{node.Turn}进入建设规划，master没有该规划回合。");
        var facilities = ImmutableArray.CreateBuilder<ObservedFacility>(6);
        for (var i = 0; i < 6; i++) facilities.Add(new(101 + i, values[facilityFields[i]]));
        var ticketField = model.Input.Fields.IndexOf("island_tickets");
        return rootDevelopment with { Turn = node.Turn, RawPlayingState = 35, DevelopmentPoints = values[15], PlanningCost = period.TargetPt,
            Facilities = facilities.MoveToImmutable(), Plan = plan ?? rootDevelopment.Plan, PendingStoryIds = [],
            IslandTrainingTickets = ticketField < 0 || values[ticketField] == PioneerForecastModel.Unknown ? null : values[ticketField] };
    }

    void CalculateTraining(PioneerForecastModel.ForecastTraining template, int commandId, ReadOnlySpan<int> values,
        int turn, bool success, TrainingActionPreview preview, Span<int> delta, out AttributeValues overflow)
    {
        delta.Clear(); overflow = default;
        var primary = TrainingFacilityRules.PrimaryTarget(TrainingBaseTable.OrdinaryCommand(commandId)) - 1;
        delta[27 + primary] = 1;
        if (!success) return;
        var command = template.Start.Commands[primary];
        var environment = template.Start.Environment with { Current = Resources(values), Limits = Limits(values), Motivation = values[12], Fans = values[14] };
        var common = preview.Gains.Common with { Vital = Math.Max(-values[10], preview.Gains.Common.Vital) };
        var after = TrainingPreviewSettlement.Apply(environment.Current, environment.Limits, common, preview.Gains.Scenario);
        delta[0] = after.Speed - values[0]; delta[1] = after.Stamina - values[1]; delta[2] = after.Power - values[2];
        delta[3] = after.Guts - values[3]; delta[4] = after.Wisdom - values[4];
        delta[10] = after.Vital - values[10]; delta[13] = after.SkillPoints - values[13];
        delta[15] = turn < template.Start.PlanningPeriods[0].PlanningTurn || template.Start.CompletedCheckpoint == 5 ? 0
            : PioneerPointGain.OrdinaryTraining(command.SupportPositions.Length + command.GuestCount,
                template.Start.CheckpointBonusPercent!.Value, preview.Friendship);
        var mask = 0;
        foreach (var position in command.SupportPositions) mask |= 1 << (position - 1);
        var bondBonus = template.Deck.TrainingBondBonus((byte)mask);
        foreach (var position in command.SupportPositions)
        {
            var card = template.Start.Deck[position - 1];
            var gain = (card.TrainingCommandId == 0 ? 4 : 7) + bondBonus + (charmField >= 0 && values[charmField] == 1 ? 2 : 0);
            delta[15 + position] = Math.Min(100, values[15 + position] + gain) - values[15 + position];
        }
        overflow = preview.Gains.Overflow;
    }

    internal static TrainingValues Resources(ReadOnlySpan<int> values) => new(values[0], values[1], values[2], values[3], values[4], values[10], values[13]);
    internal static TrainingLimits Limits(ReadOnlySpan<int> values) => new(values[5], values[6], values[7], values[8], values[9], values[11]);
    public TransitionResult ResumeAutomatic(ref PioneerForecastState state, ref SimulationRandom random)
        => throw new InvalidOperationException("预测转移没有悬空的自动结算步骤。");
    public TerminalMetrics GetOutcome(in PioneerForecastState state)
    {
        if (!state.Terminal) throw new InvalidOperationException("预测尚未到达终局。");
        var values = state.Values;
        return new(new(values[0], values[1], values[2], values[3], values[4]), new(values[5], values[6], values[7], values[8], values[9]),
            checked((int)state.GainedSkillPoints), null,
            Enumerable.Range(0, 5).Sum(index => ScoringCandidates.StatusToPoint(values[index])));
    }

    public ITrajectoryPolicy<PioneerForecastDecision, ForecastAction, int> CreatePolicy() => new ContinuationPolicy(this);
    sealed class ContinuationPolicy(PioneerForecastScenario scenario)
        : ITrajectoryPolicy<PioneerForecastDecision, ForecastAction, int>
    {
        public long SharedMemoryBytes => 128;
        public int CreateCursor() => 0;
        public long GetCursorBytes(in int cursor) => 0;
        public void ResetCursor(ref int cursor, int sampleIndex) => cursor = 0;
        public void CopyCursor(in int source, ref int destination) => destination = source;
        public SimulationStop? ValidateCompletion(in int cursor) => null;
        public PolicySelection<ForecastAction> ChooseAction(in DecisionPoint<PioneerForecastDecision> current, ref int cursor, ref SimulationRandom random)
        {
            var context = current.Context;
            if (context.Node.Kind == ForecastActionKind.Planning && scenario.construction is { } construction && scenario.rootDevelopment is not null)
            {
                var plan = construction.EnumeratePlans(scenario.Development(context.Node, context.Values.Span, context.Plan)).First();
                cursor++;
                return PolicySelection<ForecastAction>.Selected(new(ForecastActionKind.Planning, 0, Plan: plan));
            }
            var next = random;
            var row = scenario.model.Select(scenario.configuration, context.Node, context.Values.Span, context.History.Span, scenario.scales, scenario.excludedRun, null, ref next);
            if (row < 0) return PolicySelection<ForecastAction>.Stopped(new(SimulationStopReason.UnknownMechanism, 2002, context.Node.Turn));
            random = next; cursor++;
            return PolicySelection<ForecastAction>.Selected(scenario.model.Input.Transitions[row].Action);
        }
    }
}
