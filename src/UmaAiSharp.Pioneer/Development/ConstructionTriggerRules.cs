using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Development;

public enum ConstructionPointSource { Training, IslandTraining, RacePointEvent, TuckerPointEvent }
public sealed record ConstructionProgress(int Turn, int RawPlayingState, int DeckId, int DevelopmentPoints,
    ImmutableArray<int> Thresholds, ImmutableArray<int> PendingStoryIds);
public sealed record PendingConstructionMilestone(int Threshold, int StoryId, int RawPlayTiming, int NotBeforeTurn);
public sealed record ConstructionProgressUpdate(ConstructionProgress AfterPoints, PendingConstructionMilestone? Milestone);

public sealed class ConstructionTriggerRules(PioneerMasterData master)
{
    public const string Version = "pioneer-construction-trigger-v3";

    internal static int CrossedThreshold(ReadOnlySpan<int> thresholds, int beforePoints, int afterPoints)
    {
        var crossed = 0;
        foreach (var threshold in thresholds)
        {
            if (beforePoints >= threshold || threshold > afterPoints) continue;
            if (crossed != 0) throw new UnknownMechanismException("单次增量跨过多个建设阈值的事件合并方式尚未核验。");
            crossed = threshold;
        }
        return crossed;
    }

    // The milestone is pending work, not a visible event. A scheduler must reach its window first.
    public ConstructionProgressUpdate ApplyPointGainCandidate(ConstructionProgress before, int pointGain, ConstructionPointSource source)
    {
        if (before.DevelopmentPoints < 0 || pointGain < 0)
            throw new ArgumentException("建设进度和输入 Pt 增量不能为负数。");
        var thresholds = master.TicketThresholds.Where(x => x.DeckId == before.DeckId
            && x.TurnMin <= before.Turn && before.Turn <= x.TurnMax).Select(x => x.GainShimaTrainingPt).Order().ToArray();
        if (before.DeckId != 3 || thresholds.Length != 2 || before.Thresholds.IsDefault
            || !before.Thresholds.Order().SequenceEqual(thresholds))
            throw new UnknownMechanismException($"建设进度证据仅覆盖 deck=3 且与 master 周期一致的两级阈值；收到 deck={before.DeckId}、turn={before.Turn}。");
        if (before.PendingStoryIds.IsDefault)
            throw new InvalidDataException("建设进度缺少明确的当前事件队列；缺失不能补为空。");
        var sourceMatches = source switch
        {
            ConstructionPointSource.Training or ConstructionPointSource.IslandTraining =>
                before.RawPlayingState == 1 && before.PendingStoryIds.Length == 0,
            ConstructionPointSource.RacePointEvent => before.RawPlayingState == 5
                && before.PendingStoryIds.SequenceEqual([400011105]),
            ConstructionPointSource.TuckerPointEvent => before.RawPlayingState == 5
                && before.PendingStoryIds.SequenceEqual([400011111]),
            _ => false,
        };
        if (!sourceMatches)
            throw new UnknownMechanismException($"Pt 来源 {source} 与 phase={before.RawPlayingState} 或待处理事件不符合已核验范围。");
        var afterPoints = checked(before.DevelopmentPoints + pointGain);
        var crossed = CrossedThreshold(thresholds, before.DevelopmentPoints, afterPoints);
        var after = before with { DevelopmentPoints = afterPoints };
        if (crossed == 0) return new(after, null);

        if (before.Turn is >= 37 and <= 40)
        {
            if (source != ConstructionPointSource.Training && !(source == ConstructionPointSource.RacePointEvent && before.Turn == 40))
                throw new UnknownMechanismException("经典合宿期间仅核验训练，以及回合40比赛 Pt 跨阈值后的延后建设。");
            return new(after, new(crossed, 400011108, 1, 41));
        }
        if (before.Turn is < 5 or > 60 || before.Turn == 60 && source is not (ConstructionPointSource.Training or ConstructionPointSource.RacePointEvent))
            throw new UnknownMechanismException($"普通建设阈值触发尚未覆盖 turn={before.Turn}。");
        var timing = source is ConstructionPointSource.Training or ConstructionPointSource.IslandTraining ? 6 : 10;
        return new(after, new(crossed, 400011107, timing, before.Turn));
    }
}
