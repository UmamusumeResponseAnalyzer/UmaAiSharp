using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Hints;

public readonly record struct HintSkill(int GroupId, int Rarity);
public readonly record struct HintLevel(HintSkill Skill, int Level);
public enum HintPoolExclusion { None, MaximumHintLevel, AlreadyLearned }
public sealed record HintPoolEntry(HintSkill Skill, int? ObservedLevel, HintPoolExclusion Exclusion);
public readonly record struct HintAttributeReward(TrainingValues Resources, AttributeValues UncappedGains, AttributeValues Overflow);

public static class HintRewardPool
{
    public const string Version = "jp-pioneer-white-hint-effective-pool-v2";

    // This describes the effective reward pool, not the server's filtering time or draw probabilities.
    public static ImmutableArray<HintPoolEntry> Evaluate(ImmutableArray<HintSkill> supportSkills,
        ImmutableArray<HintLevel> recordedHints, ImmutableArray<HintSkill> learnedSkills)
    {
        if (supportSkills.IsDefault || recordedHints.IsDefault || learnedSkills.IsDefault)
            throw new InvalidDataException("Hint 奖池需要明确的支援技能、已记录 Hint 和已学技能数组。");
        if (supportSkills.Concat(learnedSkills).Concat(recordedHints.Select(hint => hint.Skill))
            .Any(skill => skill.GroupId <= 0 || skill.Rarity <= 0))
            throw new InvalidDataException("Hint 技能组与稀有度必须为正整数。");
        if (recordedHints.Any(hint => hint.Level is < 0 or > 5)
            || recordedHints.Select(hint => hint.Skill).Distinct().Count() != recordedHints.Length)
            throw new InvalidDataException("已记录 Hint 等级必须为0..5，同一组和稀有度不能重复。");
        if (supportSkills.Any(skill => skill.Rarity != 1))
            throw new UnknownMechanismException("有效奖池过滤仅核验白技能候选；其他稀有度候选尚未确认。");
        var hints = recordedHints.ToDictionary(hint => hint.Skill, hint => hint.Level);
        var pool = ImmutableArray.CreateBuilder<HintPoolEntry>();
        foreach (var skill in supportSkills.Distinct().OrderBy(skill => skill.GroupId).ThenBy(skill => skill.Rarity))
        {
            var level = hints.TryGetValue(skill, out var observed) ? (int?)observed : null;
            var owned = learnedSkills.Where(learned => learned.GroupId == skill.GroupId).ToArray();
            if (owned.Any(learned => learned.Rarity is not (1 or 2)))
                throw new UnknownMechanismException("同组已学技能的稀有度超出白/金技能过滤证据。");
            var exclusion = level == 5 ? HintPoolExclusion.MaximumHintLevel
                : owned.Length > 0 ? HintPoolExclusion.AlreadyLearned : HintPoolExclusion.None;
            pool.Add(new(skill, level, exclusion));
        }
        return pool.ToImmutable();
    }

    // The caller supplies one identified master reward group; multiple groups are not added together.
    public static HintAttributeReward ApplyAttributeReward(TrainingValues before, TrainingLimits limits, TrainingValues reward)
    {
        if (reward.Vital != 0 || reward.SkillPoints < 0 || before.SkillPoints < 0
            || before.Vital < 0 || before.Vital > limits.Vital)
            throw new InvalidDataException("Hint 属性奖励包含未支持的体力分量或无效资源。");
        var speed = Apply(before.Speed, limits.Speed, reward.Speed);
        var stamina = Apply(before.Stamina, limits.Stamina, reward.Stamina);
        var power = Apply(before.Power, limits.Power, reward.Power);
        var guts = Apply(before.Guts, limits.Guts, reward.Guts);
        var wisdom = Apply(before.Wisdom, limits.Wisdom, reward.Wisdom);
        return new(new(speed.Value, stamina.Value, power.Value, guts.Value, wisdom.Value,
                before.Vital, checked(before.SkillPoints + reward.SkillPoints)),
            new(speed.Gain, stamina.Gain, power.Gain, guts.Gain, wisdom.Gain),
            new(speed.Overflow, stamina.Overflow, power.Overflow, guts.Overflow, wisdom.Overflow));
    }

    static (int Value, int Gain, int Overflow) Apply(int before, int cap, int reward)
    {
        if (before < 0 || before > cap || reward < 0) throw new InvalidDataException("Hint 奖励的属性、上限或增量无效。");
        var lower = Math.Min(reward, Math.Max(0, 1200 - before));
        var upper = reward - lower;
        var gain = checked(lower + (upper > 0 ? Math.Max(1, upper / 2) : 0));
        var actual = Math.Min(cap - before, gain);
        return (before + actual, gain, gain - actual);
    }
}
