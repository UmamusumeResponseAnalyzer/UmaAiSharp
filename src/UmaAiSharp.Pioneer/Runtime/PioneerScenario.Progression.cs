using System.Collections.Immutable;
using UmaAiSharp.Base.Training;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Ending;
using UmaAiSharp.Base.Events;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    static ReadOnlySpan<int> HighCheckpointStories => [400011004, 400011008, 400011012, 400011017, 400011021];
    static ReadOnlySpan<int> HighCheckpointAttributes => [10, 15, 20, 25, 35];
    static ReadOnlySpan<int> HighCheckpointSkillPoints => [60, 120, 150, 200, 300];
    // shortcut: the first four low grades use a prior anchored by the observed fifth low grade.
    static ReadOnlySpan<int> LowCheckpointAttributes => [5, 10, 15, 20, 25];
    static ReadOnlySpan<int> LowCheckpointSkillPoints => [30, 60, 75, 100, 150];

    bool CampCommands(in PioneerStateHeader header) => data.TurnRules?.WholeRun == true
        && (data.Start is PioneerStart.Planning || header.Turn != header.SourceTurn)
        ? IsCamp(header.Turn) : !data.CampCommands.IsEmpty;

    int TrainingCommand(int index, in PioneerStateHeader header) => CampCommands(header) ? 3601 + index
        : TrainingBaseTable.OrdinaryCommand(data.TrainingCommands[index].CommandId);

    PioneerDevelopmentState CurrentPlanning(PioneerState state)
    {
        if (state.Header.ActionOrdinal == 0 && data.Start is PioneerStart.Planning imported) return imported.State;
        ref readonly var header = ref state.Header;
        var facilities = state.GetSection(PioneerStateSection.FacilityStates);
        var layout = ImmutableArray.CreateBuilder<ObservedFacility>(facilities.Length / 2);
        for (var i = 0; i < facilities.Length; i += 2) layout.Add(new(facilities[i], facilities[i + 1]));
        return new(header.Turn, 35, header.DevelopmentPoints, header.PlanningCost, header.DeckId,
            [.. state.GetSection(PioneerStateSection.Thresholds)], header.Tickets, layout.MoveToImmutable(), new([]), []);
    }

    TransitionResult BeginPlanning(PioneerState state, PlanningPeriod period)
    {
        ref var header = ref state.Mutable;
        var deckId = header.DeckId;
        var firstTrainingTurn = period.PlanningTurn == 3 ? 3 : period.PlanningTurn + 1;
        var thresholds = data.Construction!.Data.TicketThresholds.Where(row => row.DeckId == deckId
            && row.TurnMin <= firstTrainingTurn && firstTrainingTurn <= row.TurnMax)
            .Select(row => row.GainShimaTrainingPt).Order().ToImmutableArray();
        state.SetSection(PioneerStateSection.PendingStories, []);
        state.SetSection(PioneerStateSection.Thresholds, thresholds.AsSpan());
        header.Turn = period.PlanningTurn;
        header.RawPlayingState = 35;
        header.Stage = PioneerStage.ConstructionPlanning;
        header.PlanningCost = period.TargetPt;
        header.KnownParts |= PioneerKnownParts.Planning;
        header.KnownParts &= ~PioneerKnownParts.EventIdentity;
        header.Event = default;
        header.ActionOrdinal++;
        if (firstTrainingTurn == 3)
        {
            header.ProjectNpcCount = 6;
            state.TurnBoard = null;
        }
        return TransitionResult.Settled((int)header.Stage);
    }

    void SettleCheckpoint(PioneerState state, PlanningPeriod period, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var rules = data.Checkpoints!;
        var index = period.CheckPointType - 1;
        var high = header.DevelopmentPoints >= period.TargetPt;
        var result = high ? 2 : 1;
        var effects = rules.Master.GetEffects(period.CheckPointType, result);
        if (high)
        {
            var context = new CheckpointEventContext(header.Turn, 5, header.DeckId, period.CheckPointType,
                result, HighCheckpointStories[index], 0);
            var levels = state.MutableSection(PioneerStateSection.TrainingLevels);
            var progress = rules.ApplyHighEvaluationProgress(context,
                new(new(levels[0], levels[1], levels[2], levels[3], levels[4]), header.DirectorBond, header.HasAttention));
            var updated = progress.TrainingLevels;
            levels[0] = updated.Speed;
            levels[1] = updated.Stamina;
            levels[2] = updated.Power;
            levels[3] = updated.Guts;
            levels[4] = updated.Wisdom;
            header.DirectorBond = progress.DirectorBond;
            header.ProjectNpcCount += progress.AddedProjectMemberCount;
        }
        var resources = rules.SettleResources(period, new(header.DevelopmentPoints, header.Resources.Vital, header.Limits.Vital));
        header.DevelopmentPoints = resources.After.DevelopmentPoints;
        header.Resources = header.Resources with { Vital = resources.After.Vital };
        var attr = high ? HighCheckpointAttributes[index] : LowCheckpointAttributes[index];
        var skill = high ? HighCheckpointSkillPoints[index] : LowCheckpointSkillPoints[index];
        ApplyProgressionReward(state, attr, skill);
        var turnRules = data.TurnRules!;
        if (period.CheckPointType == 5)
        {
            GainHint(state, high ? turnRules.HighFinalReviewHintSlot : turnRules.LowFinalReviewHintSlot, high ? 1 : 3);
        }
        if ((high || period.CheckPointType == 5) && turnRules.OrdinarySupportMask != 0)
        {
            // shortcut: packets identify the deck Hint pool, but not the source card weights.
            Span<int> positions = stackalloc int[6];
            var count = TrainingBoard.Positions((byte)turnRules.OrdinarySupportMask, positions);
            var draw = random.At(header.Turn, PioneerDraw.CheckpointHint);
            var position = positions[draw.NextScaled(count)] - 1;
            var bonus = turnRules.Hints!.Draw(position, state.GetSection(PioneerStateSection.SupportBonds)[position],
                state.MutableSection(PioneerStateSection.HintLevels), ref draw);
            if (bonus != default) ApplyProgressionReward(state, bonus);
        }

        header.CompletedCheckpoint = period.CheckPointType;
        header.CheckpointResult = result;
        state.MutableSection(PioneerStateSection.CheckpointResults)[index] = result;
        header.EvaluationTrainingPercent = effects.Single(effect => effect is { EffectType: 200, EffectValue1: 0 }).EffectValue2;
        header.HasCheckpointBonus = period.CheckPointType < 5;
        header.CheckpointBonusPercent = header.HasCheckpointBonus
            ? effects.Single(effect => effect is { EffectType: 202, EffectValue1: 9 }).EffectValue2 : 0;
        header.KnownParts |= PioneerKnownParts.CheckpointResult | PioneerKnownParts.CheckpointProgress;
        if (header.HasCheckpointBonus) header.KnownParts |= PioneerKnownParts.CheckpointBonusPercent;
        else header.KnownParts &= ~PioneerKnownParts.CheckpointBonusPercent;
        state.TurnBoard = null;
    }

    void SettleUraRace(PioneerState state, int turn, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        var round = (turn - 74) / 2;
        if (!header.PioneerCompleted || header.CompletedUraRaces != round)
            throw new InvalidOperationException("URA 决赛须在剧本完成后依次结算预赛、半决赛和决赛。");
        var rules = data.TurnRules!;
        ApplyProgressionReward(state, 10 * rules.RaceRewardPercent / 100, (40 + 20 * round) * rules.RaceRewardPercent / 100);
        var fans = random.At(turn, PioneerDraw.RaceFans);
        header.Fans += rules.DrawUraFans(turn, ref fans);
        header.CompletedUraRaces++;
    }

    TransitionResult FinishAutonomousEnding(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        header.Turn = 78;
        header.TurnKnown = true;
        header.Event = default;
        header.KnownParts &= ~PioneerKnownParts.EventIdentity;
        ApplyReporterEnding(state);
        var results = state.GetSection(PioneerStateSection.CheckpointResults);
        var high = results.Count(2);
        var (attribute, skill) = high switch
        {
            5 => (PioneerEndingSettlement.HighAttribute, PioneerEndingSettlement.HighSkillPoints),
            >= 3 => (45, 420),
            _ => (35, 300), // shortcut: lower grade prior until captures cover multiple low evaluations.
        };
        ApplyProgressionReward(state, attribute, skill);
        if (high == 5)
        {
            ref var hint = ref state.MutableSection(PioneerStateSection.HintLevels)[data.TurnRules!.EndingHintSlot];
            hint = Math.Max(hint, PioneerEndingSettlement.HighHint.Level);
        }
        else if (high == 4 && results[^1] == 1)
        {
            GainHint(state, data.TurnRules!.LowEndingHintSlot, PioneerEndingSettlement.LowFinalReviewHint.Level);
        }

        var start = data.TrainingStart!;
        foreach (var card in start.Deck)
        {
            if (card.CardId != 30257) continue;
            var lower = ((header.FriendOutings >> ((card.Position - 1) * 3)) & 7) < 5;
            var bond = state.GetSection(PioneerStateSection.SupportBonds)[card.Position - 1];
            var scaling = EventEffectScaling.FromSupport(card with { Bond = bond });
            var gains = scaling.Apply(1, lower ? 10 : 15);
            ApplyProgressionReward(state, new(gains, gains, 0, gains, 0, 0, scaling.Apply(30, lower ? 40 : 50)));
        }
        return FinishCharacterEnding(state);
    }

    static void ApplyReporterEnding(PioneerState state)
    {
        ref readonly var header = ref state.Header;
        var (attribute, pt) = ReporterEndingReward(header.ReporterBond, header.HasAttention);
        ApplyProgressionReward(state, attribute, pt);
    }

    static (int Attribute, int SkillPoints) ReporterEndingReward(int bond, bool attention)
    {
        // shortcut: the upper two attention tiers use one matched packet each (bonds 75 and 100).
        return (bond, attention) switch
        {
            (>= 90, true) => (11, 42), (>= 70, true) => (9, 30),
            (>= 40, true) => (0, 20), (_, true) => (0, 10),
            (>= 90, false) => (5, 20), (>= 70, false) => (3, 10),
            (>= 40, false) => (0, 10), _ => (0, 5),
        };
    }

    static void ApplyProgressionReward(PioneerState state, int attribute, int skill)
        => ApplyProgressionReward(state, new(attribute, attribute, attribute, attribute, attribute, 0, skill));

    static void ApplyProgressionReward(PioneerState state, TrainingValues gains)
    {
        ref var header = ref state.Mutable;
        var reward = new DeclaredEventRewards(new([
            new(1, 1, gains.Speed, 0), new(1, 2, gains.Stamina, 0), new(1, 3, gains.Power, 0),
            new(1, 4, gains.Guts, 0), new(1, 5, gains.Wisdom, 0), new(1, 30, gains.SkillPoints, 0),
        ]));
        var after = reward.Apply(header.Resources, header.Limits, header.Motivation, false, false);
        header.Resources = after.Resources;
        header.FutureGainedSkillPoints += gains.SkillPoints;
        header.FutureOverflow += after.Overflow;
        if (gains.Vital != 0) RecoverVital(state, gains.Vital);
    }
}
