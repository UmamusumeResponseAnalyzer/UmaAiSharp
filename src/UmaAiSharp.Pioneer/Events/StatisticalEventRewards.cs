using System.Collections.Frozen;
using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Events;

public readonly record struct EventRewardFamily(int OwnerKind, int TrainingCommand, int EventId, int PlayTiming,
    int Year, int Progress, int Choice);
public sealed record EventRewardSample(string Run, int StoryId, EventRewardFamily Family,
    ImmutableArray<DeclaredEventEffect> BaseEffects, ImmutableArray<DeclaredEventEffect> RemainingEffects);
public sealed record EventRewardEstimate(int StoryId, EventRewardFamily Family,
    int SampleCount, int RunCount, ImmutableArray<WeightedEventReward> Outcomes);
public sealed record WeightedEventReward(int Count, ImmutableArray<DeclaredEventEffect> BaseEffects,
    ImmutableArray<DeclaredEventEffect> RemainingEffects);
public sealed record SampledEventRewards(DeclaredEventBranch Resources, ImmutableArray<DeclaredEventEffect> RemainingEffects);

// Calibration data contains neutral reward atoms, never a donor's post-state or clipped delta.
public sealed class StatisticalEventRewards
{
    public const string Version = "pioneer-neutral-event-rewards-v2";
    readonly FrozenDictionary<(int Story, int Choice), ImmutableArray<EventRewardSample>> stories;

    public StatisticalEventRewards(ImmutableArray<EventRewardSample> samples)
    {
        if (samples.IsDefault) throw new InvalidDataException("事件统计模型缺少明确样本集合。");
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Run) || sample.StoryId <= 0 || sample.BaseEffects.IsDefault || sample.RemainingEffects.IsDefault)
                throw new InvalidDataException("事件统计样本缺少育成身份、故事或基础奖励。");
            foreach (var atom in sample.BaseEffects)
                if (atom.DisplayId is not (1 or 2) || atom.Value0 is not (1 or 2 or 3 or 4 or 5 or 10 or 20 or 30)
                    || atom.Value1 < 0 || atom.Value2 != 0)
                    throw new InvalidDataException("中性事件模型只接收明确的属性、体力、干劲和技能Pt基础奖励。");
        }
        stories = samples.GroupBy(sample => (sample.StoryId, sample.Family.Choice))
            .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
    }

    public EventRewardEstimate Estimate(int storyId, EventRewardFamily family, string excludedRun)
    {
        // Legacy model files carry the observed protocol number alongside the reward context.
        var key = family with { EventId = 0 };
        var selected = stories.TryGetValue((storyId, family.Choice), out var rows)
            ? rows.Where(row => row.Run != excludedRun && (row.Family with { EventId = 0 }) == key).ToArray() : [];
        if (selected.Length == 0)
            throw new UnknownMechanismException($"故事{storyId}、选项{family.Choice}缺少与当前上下文匹配的基础奖励样本。");
        var outcomes = new List<WeightedEventReward>();
        foreach (var sample in selected)
        {
            var index = outcomes.FindIndex(outcome => outcome.BaseEffects.AsSpan().SequenceEqual(sample.BaseEffects.AsSpan())
                && outcome.RemainingEffects.AsSpan().SequenceEqual(sample.RemainingEffects.AsSpan()));
            if (index < 0) outcomes.Add(new(1, sample.BaseEffects, sample.RemainingEffects));
            else outcomes[index] = outcomes[index] with { Count = outcomes[index].Count + 1 };
        }
        return new(storyId, family, selected.Length, selected.Select(row => row.Run).Distinct().Count(), [.. outcomes]);
    }

}

public sealed class CompiledStatisticalEventRewards
{
    readonly SampledEventRewards[] branches;
    readonly int[] counts;
    readonly int total;
    public EventRewardEstimate Estimate { get; }

    public CompiledStatisticalEventRewards(EventRewardEstimate estimate, EventEffectScaling scaling)
    {
        if (estimate.Outcomes.IsDefaultOrEmpty || estimate.Outcomes.Any(outcome => outcome.Count <= 0)
            || estimate.Outcomes.Sum(outcome => outcome.Count) != estimate.SampleCount)
            throw new InvalidDataException("事件概率分母与结果频数不一致。");
        Estimate = estimate;
        total = estimate.SampleCount;
        counts = [.. estimate.Outcomes.Select(outcome => outcome.Count)];
        branches = [.. estimate.Outcomes.Select(outcome => new SampledEventRewards(new([.. outcome.BaseEffects.Select(atom => atom.DisplayId == 1
            ? atom with { Value1 = scaling.Apply(atom.Value0, atom.Value1) } : atom)]), outcome.RemainingEffects))];
    }

    public SampledEventRewards Sample(ref SimulationRandom random)
    {
        var draw = branches.Length == 1 ? 0 : random.NextInt(total);
        for (var index = 0; index < branches.Length; index++)
        {
            if (draw < counts[index]) return branches[index];
            draw -= counts[index];
        }
        throw new InvalidOperationException("事件抽样索引越界。");
    }
}
