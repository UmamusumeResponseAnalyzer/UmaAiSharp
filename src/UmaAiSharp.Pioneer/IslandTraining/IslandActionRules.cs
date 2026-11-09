using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.IslandTraining;

public sealed record IslandActionSupport(int Position, int CardId, int Type, int Level, int Bond);
public sealed record IslandActionState(int ActionTurn, int RawPlayingState, bool CommandEnabled,
    ImmutableArray<int> PendingStoryIds, TrainingValues Values, TrainingLimits Limits, int DevelopmentPoints,
    int Tickets, int CompletedCheckpoint, int? CheckpointBonusPercent, ImmutableArray<IslandActionSupport> Deck,
    ImmutableArray<IslandTrainingAssignment> Assignments, ImmutableArray<TrainingFacility> Facilities, ImmutableArray<int> CharacterEffects,
    ImmutableArray<int> HintCandidates);
public enum TemporarySupportGrantSource { IslandTraining, TuckerEvent }
public readonly record struct TemporarySupportGrant(int SourceTurn, TemporarySupportGrantSource Source,
    int? StoryId, int Position, int EffectType, int Value);
public sealed record IslandActionSettlement(int SourceTurn, TrainingValues ValuesAfter, int DevelopmentPointsAfter,
    int DevelopmentPointGain, int TicketsAfter, ImmutableArray<IslandActionSupport> DeckAfter,
    ImmutableArray<TemporarySupportGrant> TemporaryGrants, ImmutableArray<int> HintCandidates,
    TrainingFormulaPrediction TrainingGains);
public readonly record struct TuckerSpecialtyContext(int SourceTurn, int RawPlayingState, int StoryId, int PlayTiming, int ChoiceNumber);

public static class IslandActionRules
{
    public const string Version = "observed-island-success-components-v1";

    public static IslandActionSettlement ApplySuccessCandidate(IslandActionState before, IslandTrainingInput training)
    {
        if (training.Environment.Current != before.Values || training.Environment.Limits != before.Limits
            || training.Assignments.Length != before.Assignments.Length
            || training.Assignments.Zip(before.Assignments).Any(pair => pair.First.FacilityId != pair.Second.FacilityId
                || pair.First.GuestCount != pair.Second.GuestCount || pair.First.Supports.Length != pair.Second.Supports.Length
                || pair.First.Supports.Zip(pair.Second.Supports).Any(cards => cards.First.CardId != cards.Second.CardId
                    || cards.First.Position != cards.Second.Position || cards.First.Level != cards.Second.Level
                    || cards.First.Bond != cards.Second.Bond || cards.First.TrainingCommandId != cards.Second.TrainingCommandId
                    || !cards.First.Effects.SequenceEqual(cards.Second.Effects) || !cards.First.UniqueEffects.SequenceEqual(cards.Second.UniqueEffects)))
            || !training.Facilities.Select(facility => (facility.Id, facility.StateId)).SequenceEqual(before.Facilities.Select(facility => (facility.Id, facility.StateId))))
            throw new InvalidDataException("岛训练生成输入与行动前属性、上限或人员分配不一致。");
        return SettleResolvedSuccess(before, TrainingFormulaCandidate.GenerateIsland(training));
    }

    // Applies resolved gains only. The caller must distinguish generated gains from observed previews in its evidence.
    public static IslandActionSettlement SettleResolvedSuccess(IslandActionState before, TrainingFormulaPrediction gains)
    {
        if (!before.CommandEnabled || before.Tickets <= 0)
            throw new IllegalActionException("岛训练未启用或没有可消费的岛训练券。");
        ValidateInputs(before);
        ValidateGains(gains.Common, gains.Scenario);
        var values = TrainingPreviewSettlement.Apply(before.Values, before.Limits, Parameters(gains.Common), Parameters(gains.Scenario));
        var selected = before.Assignments.SelectMany(group => group.Supports).ToArray();
        var deck = before.Deck.ToDictionary(card => card.Position);
        var participants = before.Assignments.Sum(group => group.Supports.Length + group.GuestCount);
        var friendship = before.Assignments.Any(group => group.Supports.Any(card => card.Bond >= 80
            && card.TrainingCommandId == TrainingFacilityRules.Command(group.FacilityId)));
        var pointGain = DevelopmentPointGain(before.CompletedCheckpoint, before.CheckpointBonusPercent, participants, friendship);
        var extraBond = before.CharacterEffects.Contains(8) ? 2 : 0;
        var afterDeck = before.Deck.Select(card => card with { Bond = Math.Min(100, card.Bond + (card.Type == 1 ? 10 : 7) + extraBond) }).ToImmutableArray();
        var grants = selected.Where(card => deck[card.Position].Type == 1).OrderBy(card => card.Position)
            .Select(card => new TemporarySupportGrant(before.ActionTurn, TemporarySupportGrantSource.IslandTraining, null, card.Position, 19, 60)).ToImmutableArray();
        return new(before.ActionTurn, values, checked(before.DevelopmentPoints + pointGain), pointGain, before.Tickets - 1,
            afterDeck, grants, before.HintCandidates, gains);
    }

    internal static void ValidateInputs(IslandActionState before)
    {
        if (before.ActionTurn is < 6 or > 72 || before.RawPlayingState != 1 || before.PendingStoryIds.IsDefault
            || before.PendingStoryIds.Length != 0 || before.Tickets > 3 || before.DevelopmentPoints < 0)
            throw new UnknownMechanismException("岛训练即时分量要求回合6..72、阶段1、空待事件、券不超过3和非负发展Pt。");
        ValidateDeck(before.Deck);
        TrainingFacilityRules.Validate(before.Facilities);
        if (before.CharacterEffects.IsDefault || before.CharacterEffects.Distinct().Count() != before.CharacterEffects.Length
            || before.CharacterEffects.Any(effect => effect is not (7 or 8 or 9 or 10 or 11 or 12 or 13 or 25 or 26 or 27 or 31 or 36)))
            throw new UnknownMechanismException("岛训练角色效果缺失、重复或包含未实现的羁绊效果。");
        if (before.Assignments.IsDefault) throw new InvalidDataException("岛训练前人员分配缺失。");
        var selected = before.Assignments.SelectMany(group => group.Supports).ToArray();
        if (before.Assignments.Select(group => group.FacilityId).Distinct().Count() != before.Assignments.Length
            || before.Assignments.Any(group => group.FacilityId is < 101 or > 106 || group.GuestCount < 0)
            || selected.Select(card => card.Position).Distinct().Count() != selected.Length)
            throw new InvalidDataException("岛训练前人员分配重复或包含无效设施/人数。");
        var deck = before.Deck.ToDictionary(card => card.Position);
        foreach (var support in selected)
            if (!deck.TryGetValue(support.Position, out var card) || card.CardId != support.CardId || card.Level != support.Level || card.Bond != support.Bond)
                throw new InvalidDataException("本次参加支援与完整deck的身份、等级或羁绊不一致。");
        if (!selected.Any(card => card.CardId == 30257))
            throw new UnknownMechanismException("岛训练参加者中缺少Tucker30257，当前规则未实现该编成的+60效果。");
        if (before.HintCandidates.IsDefault || before.HintCandidates.Distinct().Count() != before.HintCandidates.Length
            || before.HintCandidates.Any(position => !selected.Any(card => card.Position == position) || deck[position].Type != 1))
            throw new InvalidDataException("岛训练Hint候选必须明确、唯一且属于行动前参加的普通支援。");
    }

    internal static void ValidateGains(TrainingValues common, TrainingValues scenario)
    {
        if (common.Vital != 0 || scenario.Vital != 0)
            throw new UnknownMechanismException("岛训练即时体力增量须为0。");
        if (common.Speed is < 0 or > 100 || common.Stamina is < 0 or > 100 || common.Power is < 0 or > 100
            || common.Guts is < 0 or > 100 || common.Wisdom is < 0 or > 100 || common.SkillPoints is < 0 or > 100
            || scenario.Speed is < 0 or > 100 || scenario.Stamina is < 0 or > 100 || scenario.Power is < 0 or > 100
            || scenario.Guts is < 0 or > 100 || scenario.Wisdom is < 0 or > 100 || scenario.SkillPoints is < 0 or > 100)
            throw new UnknownMechanismException("岛训练成功收益每层须为0..100。");
    }

    public static ImmutableArray<TemporarySupportGrant> GrantTuckerSpecialty(TuckerSpecialtyContext context,
        ImmutableArray<IslandActionSupport> deck)
    {
        ValidateDeck(deck);
        if (context.SourceTurn is < 6 or > 72 || context.RawPlayingState != 5 || context.StoryId != 809049032
            || context.PlayTiming != 6 || context.ChoiceNumber != 0)
            throw new UnknownMechanismException("Tucker+20仅覆盖来源回合6..72、阶段5、事件809049032、时机6、选择0。");
        return deck.Where(card => card.Type == 1).OrderBy(card => card.Position)
            .Select(card => new TemporarySupportGrant(context.SourceTurn, TemporarySupportGrantSource.TuckerEvent,
                context.StoryId, card.Position, 19, 20)).ToImmutableArray();
    }

    public static int DevelopmentPointGain(int completedCheckpoint, int? checkpointBonusPercent, int participants, bool friendship)
    {
        if (participants < 0) throw new ArgumentOutOfRangeException(nameof(participants));
        if (completedCheckpoint == 5 && checkpointBonusPercent is null) return 0;
        if (completedCheckpoint is < 0 or > 4 || checkpointBonusPercent is null or < 0
            || completedCheckpoint == 0 && checkpointBonusPercent != 0)
            throw new UnknownMechanismException("岛训练发展Pt需要明确评审期及加成；第五期结算后单独归零，不能由缺预览推零。");
        return checked((int)((60L + 3L * participants) * (100L + checkpointBonusPercent.Value + (friendship ? 20 : 0)) / 100));
    }

    static void ValidateDeck(ImmutableArray<IslandActionSupport> deck)
    {
        if (deck.IsDefault || deck.Length != 6 || deck.Select(card => card.Position).Distinct().Count() != 6
            || deck.Any(card => card.Position is < 1 or > 6 || card.Bond is < 0 or > 100 || card.Type is not (1 or 2)))
            throw new InvalidDataException("岛训练需要六个唯一位置的完整普通/友人支援及0..100羁绊。");
        if (deck.Count(card => card.Type == 1) != 5 || deck.Count(card => card.Type == 2 && card.CardId == 30257 && card.Level == 50) != 1)
            throw new UnknownMechanismException("岛训练羁绊及临时效果仅覆盖五普通支援与Lv50 Tucker30257；其它友人、团卡或无Tucker未核验。");
    }

    static TrainingParameterChange[] Parameters(TrainingValues values) => [new(1, values.Speed), new(2, values.Stamina),
        new(3, values.Power), new(4, values.Guts), new(5, values.Wisdom), new(10, values.Vital), new(30, values.SkillPoints)];
}
