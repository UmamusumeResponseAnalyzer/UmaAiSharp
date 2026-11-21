using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using UmaAiSharp.Base.Events;
using UmaAiSharp.Base.Scoring;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Events;

namespace UmaAiSharp.Pioneer.Runtime;

public readonly record struct PioneerDeclaredHint(int SkillId, int Levels);

public sealed record PioneerEventBranchPreview(int BranchIndex, EventResourceResult? After,
    ImmutableArray<EventPartner> ChangedPartners, ImmutableArray<int>? CharacterEffects, [property: JsonIgnore] GoalSummary? Goal)
{
    public ImmutableArray<PioneerDeclaredHint> GrantedHints { get; init; } = [];
    public ImmutableArray<TrainingAttribute> RandomAttributes { get; init; } = [];
    public int GrantedFans { get; init; }
    public int GrantedDevelopmentPoints { get; init; }
    public decimal? ConstraintViolation => Goal?.MeanConstraintViolation;
    public ImmutableArray<decimal>? Utilities => Goal?.UnconditionalUtilities;
}

public sealed record PioneerEventChoicePreview(RootCandidate<PioneerRuntimeAction> Candidate,
    ImmutableArray<PioneerEventBranchPreview> Branches);

public sealed record PioneerEventAdvice(PioneerEvent Event, int SupportCardId, ImmutableArray<PioneerEventChoicePreview> Choices,
    ImmutableArray<int> ContenderIds, ImmutableArray<string> IgnoredMechanisms)
{
    public int? RecommendedCandidateId => ContenderIds.Length == 1 ? ContenderIds[0] : null;
}

public sealed partial class PioneerScenario
{
    public PioneerEventAdvice EvaluateEventChoices(in PioneerState root, IReadOnlyList<RootCandidate<PioneerRuntimeAction>> candidates,
        CompiledGoal goal, CancellationToken cancellationToken = default)
    {
        if (data.Start is not PioneerStart.DeclaredEvent start || root.Header.Stage != PioneerStage.DeclaredEvent)
            throw new InvalidOperationException("即时选项评价要求已揭示奖励的事件起点。");
        if (candidates.Count == 0) throw new ArgumentException("至少需要一个候选选项。", nameof(candidates));
        var choices = ImmutableArray.CreateBuilder<PioneerEventChoicePreview>(candidates.Count);
        var ignored = data.IgnoredMechanisms.ToBuilder();
        var ids = new HashSet<int>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Id < 0 || !ids.Add(candidate.Id) || ValidateAction(root, candidate.Action) is not null)
                throw new ArgumentException($"候选编号 {candidate.Id} 重复、为负，或选项与当前事件不符。", nameof(candidates));
            var choiceIndex = 0;
            while (start.Choices[choiceIndex].ChoiceNumber != candidate.Action.ChoiceNumber) choiceIndex++;
            var branches = ImmutableArray.CreateBuilder<PioneerEventBranchPreview>();
            var source = start.SupportCardId > 0 ? $"支援卡 {start.SupportCardId}" : $"角色 {start.Event.CharacterId}";
            var choice = $"{source}，故事 {start.Event.StoryId}，选项 {candidate.Action.ChoiceNumber}";
            foreach (var (branchIndex, attributesDrawn, settlement) in PossibleRewards(start, choiceIndex))
            {
                if (settlement.MissingDeclaration)
                {
                    ignored.Add($"{choice}，分支 {branchIndex} 缺少奖励声明，无法估值。");
                    branches.Add(new(branchIndex, null, [], null, null));
                    continue;
                }
                var issues = new List<string>();
                var reward = settlement.Resources;
                var hints = ImmutableArray.CreateBuilder<PioneerDeclaredHint>();
                var fans = 0;
                var development = 0;
                foreach (var effect in reward.RemainingEffects)
                    switch (effect)
                    {
                        case { DisplayId: 6, Value0: > 0, Value1: > 0, Value2: 0 }:
                            hints.Add(new(effect.Value0, effect.Value1));
                            issues.Add($"技能 {effect.Value0} 的 Hint +{effect.Value1}，后续购技收益未计入即时目标。");
                            break;
                        case { DisplayId: 3, Value0: >= 0, Value1: 0, Value2: 0 }:
                            fans = checked(fans + effect.Value0);
                            break;
                        case { DisplayId: 45, Value0: >= 0, Value1: 0, Value2: 0 }:
                            development = checked(development + effect.Value0);
                            break;
                        case { DisplayId: 40, Value0: > 0, Value1: > 0, Value2: 0 }:
                            issues.Add($"状态 {effect.Value1} 阻止获得状态 {effect.Value0}，未计入保护状态的消耗（display_id=40）。");
                            break;
                        default:
                            issues.Add($"未计入奖励 display_id={effect.DisplayId}，参数 ({effect.Value0}, {effect.Value1}, {effect.Value2})。");
                            break;
                    }
                var after = reward.Apply(start.Resources, start.Limits, start.Motivation,
                    start.CharacterEffects.Contains(25), start.CharacterEffects.Contains(5), issues);
                foreach (var issue in issues)
                    ignored.Add($"{choice}，分支 {branchIndex}：{issue}");
                var state = root.Clone();
                if (after.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
                settlement.ApplyConditions(state);
                var updated = state.MutableSection(PioneerStateSection.PartnerEvaluations);
                settlement.ApplyPartners(updated);
                var partners = ImmutableArray.CreateBuilder<EventPartner>();
                if (start.Partners is { } roster)
                    for (var i = 0; i < roster.Length; i++)
                    {
                        var partner = roster[i] with { Bond = updated[i * 2], OutingUnlocked = updated[i * 2 + 1] != 0 };
                        if (partner != roster[i]) partners.Add(partner);
                    }
                var values = after.Resources;
                var attributes = new AttributeValues(values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom);
                var metrics = new TerminalMetrics(attributes, new(start.Limits.Speed, start.Limits.Stamina, start.Limits.Power,
                    start.Limits.Guts, start.Limits.Wisdom), Math.Max(0, values.SkillPoints - start.Resources.SkillPoints),
                    after.Overflow, ScoringCandidates.AttributeScore(attributes));
                branches.Add(new(branchIndex, after, partners.ToImmutable(), [.. state.GetSection(PioneerStateSection.CharacterEffects)],
                    GoalEvaluator.Summarize(goal, [metrics])) { GrantedHints = hints.ToImmutable(), GrantedFans = fans,
                    GrantedDevelopmentPoints = development, RandomAttributes = attributesDrawn });
            }
            if (branches.Count > 1) ignored.Add($"{choice} 有 {branches.Count} 个可能结果，发生概率未知，逐分支比较。");
            choices.Add(new(candidate, branches.ToImmutable()));
        }
        // A choice is eliminated only when every result of a competitor beats every one of its results.
        var contenders = choices.Where(choice => !choices.Any(other => other.Candidate.Id != choice.Candidate.Id &&
            other.Branches.All(winner => winner.Goal is { } upper && choice.Branches.All(loser => loser.Goal is { } lower
                && GoalEvaluator.Compare(upper, lower) > 0))));
        return new(start.Event, start.SupportCardId, choices.ToImmutable(), [.. contenders.Select(choice => choice.Candidate.Id)],
            [.. ignored.Distinct()]);
    }

    IEnumerable<(int Index, ImmutableArray<TrainingAttribute> Drawn, DeclaredEventSettlement Settlement)>
        PossibleRewards(PioneerStart.DeclaredEvent start, int choiceIndex)
    {
        var branches = start.Choices[choiceIndex].Branches;
        for (var index = 0; index < branches.Length; index++)
        {
            var effects = branches[index].Effects;
            var draws = effects.Where(effect => effect.DisplayId is 14 or 34).ToArray();
            if (draws is not [{ Value0: >= 1 and <= 5, Value1: > 0, Value2: 0 } draw])
            {
                yield return (index, [], data.EventRewards[choiceIndex][index]);
                continue;
            }
            for (var mask = 1; mask < 32; mask++)
            {
                if (BitOperations.PopCount((uint)mask) != draw.Value0) continue;
                var drawn = Enumerable.Range(0, 5).Where(i => (mask & (1 << i)) != 0).Select(i => (TrainingAttribute)i).ToImmutableArray();
                var attributes = drawn.Select(i => new DeclaredEventEffect(draw.DisplayId == 14 ? 1 : 2, 1 + (int)i, draw.Value1, 0));
                var variant = new DeclaredEventBranch([.. effects.Where(effect => effect != draw), .. attributes]);
                yield return (index, drawn, new DeclaredEventSettlement(variant, start.Partners));
            }
        }
    }
}
