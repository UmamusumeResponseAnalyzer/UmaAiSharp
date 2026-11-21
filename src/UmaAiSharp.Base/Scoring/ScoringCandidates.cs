using System.Collections.Immutable;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Base.Scoring;

public sealed record OwnedScoringSkill(int Id, int Level);
public sealed record ScoringSkillDefinition(int Id, int Grade, ImmutableArray<int> Tags);
public sealed record CharacterScoringInput(
    int Speed, int Stamina, int Power, int Guts, int Wisdom,
    ImmutableArray<int> RunningStyleAptitudes,
    ImmutableArray<int> DistanceAptitudes,
    ImmutableArray<OwnedScoringSkill> Skills);
public sealed record SkillScoreContribution(int Id, int Level, int Score);
public sealed record CandidateScore(int AttributeScore, int SkillScore, ImmutableArray<SkillScoreContribution> Skills)
{
    public int Total => checked(AttributeScore + SkillScore);
}

// Observed archive coverage and limits of the candidate formulas are recorded in docs/race-route-evidence.md.
public static class ScoringCandidates
{

    public static CandidateScore Evaluate(CharacterScoringInput input, IReadOnlyDictionary<int, ScoringSkillDefinition> master)
    {
        if (input.RunningStyleAptitudes.Length != 4 || input.DistanceAptitudes.Length != 4)
            throw new InvalidDataException("评分候选要求完整的4项脚质适性和4项距离适性。");
        var attributes = AttributeScore(new(input.Speed, input.Stamina, input.Power, input.Guts, input.Wisdom));
        var skills = ImmutableArray.CreateBuilder<SkillScoreContribution>(input.Skills.Length);
        var skillScore = 0;
        foreach (var owned in input.Skills)
        {
            if (!master.TryGetValue(owned.Id, out var definition))
                throw new InvalidDataException($"评分候选缺少技能{owned.Id}的master定义。");
            var score = SkillToPoint(definition, owned.Level, input.RunningStyleAptitudes, input.DistanceAptitudes);
            skills.Add(new(owned.Id, owned.Level, score));
            skillScore = checked(skillScore + score);
        }
        return new(attributes, skillScore, skills.MoveToImmutable());
    }

    public static int AttributeScore(AttributeValues attributes) => checked(StatusToPoint(attributes.Speed) + StatusToPoint(attributes.Stamina)
        + StatusToPoint(attributes.Power) + StatusToPoint(attributes.Guts) + StatusToPoint(attributes.Wisdom));

    public static int StatusToPoint(int status)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(status);
        if (status <= 1200)
        {
            var rates = (ReadOnlySpan<int>)[5, 8, 10, 13, 16, 18, 21, 24, 26, 28, 29, 30,
                31, 33, 34, 35, 39, 41, 42, 43, 52, 55, 66, 68];
            var remaining = status + 1;
            var tenths = 0L;
            for (var band = 0; remaining > 0; band++)
            {
                var count = Math.Min(remaining, band == rates.Length - 1 ? 51 : 50);
                tenths += (long)count * rates[band];
                remaining -= count;
            }
            return checked((int)(tenths / 10));
        }
        if (status <= 2000)
        {
            var remaining = status - 1200;
            var tenths = 38418L;
            for (var band = 0; remaining > 0; band++)
            {
                var count = Math.Min(remaining, band == 0 ? 9 : band == 79 ? 11 : 10);
                tenths += (long)count * ((7910 + 131 * band) / 100);
                remaining -= count;
            }
            return checked((int)(tenths / 10));
        }
        var delta = (long)status - 2000;
        var bands = delta / 25;
        var remainder = delta % 25;
        return checked((int)(14280 + (183 * delta + 25 * bands * (bands - 1) / 2 + bands * remainder + 1) / 10));
    }

    public static int SkillToPoint(ScoringSkillDefinition skill, int level,
        ImmutableArray<int> runningStyleAptitudes, ImmutableArray<int> distanceAptitudes)
        => SkillToPoint(skill, level, runningStyleAptitudes.AsSpan(), distanceAptitudes.AsSpan());

    public static int SkillToPoint(ScoringSkillDefinition skill, int level,
        ReadOnlySpan<int> runningStyleAptitudes, ReadOnlySpan<int> distanceAptitudes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (skill.Id is > 1000000 and < 2000000) return 0;
        if (skill.Id is >= 100000 and < 200000) return checked(170 * level);
        if (skill.Id is >= 10000 and < 100000) return checked(120 * level);
        var score = (decimal)skill.Grade;
        var style = -1;
        var distance = -1;
        foreach (var tag in skill.Tags)
        {
            if (tag is >= 101 and <= 104) style = Math.Max(style, runningStyleAptitudes[tag - 101]);
            if (tag is >= 201 and <= 204) distance = Math.Max(distance, distanceAptitudes[tag - 201]);
        }
        if (style >= 0) score *= Multiplier(style);
        if (distance >= 0) score *= Multiplier(distance);
        return checked((int)Math.Round(score, MidpointRounding.ToEven));
    }

    static decimal Multiplier(int aptitude) => aptitude switch
    {
        8 or 7 => 1.1m,
        6 or 5 => 0.9m,
        4 or 3 or 2 => 0.8m,
        1 => 0.7m,
        _ => throw new InvalidDataException($"评分候选遇到未定义的适性值{aptitude}。")
    };
}
