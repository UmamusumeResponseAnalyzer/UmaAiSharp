using System.Numerics;

namespace UmaAiSharp.Base.Inheritance;

public sealed partial class MidRunInheritanceRules
{
    readonly record struct RewardBranch(double Probability, int Score, ulong TriggeredGroup, Effect[] Effects);
    sealed record RewardRoll(int RequiredGroups, ulong Groups, RewardBranch[] Branches)
    {
        static readonly RewardBranch[] NoReward = [new(1, 0, 0, [])];

        public RewardBranch[] Eligible(ulong triggered)
            => BitOperations.PopCount(triggered & Groups) >= RequiredGroups ? Branches : NoReward;
    }

    static RewardRoll CompileRoll(Bundle[] bundles, double probability, ulong triggeredGroup, int requiredGroups = 0, ulong groups = 0)
    {
        var totalWeight = bundles.Sum(bundle => bundle.Weight);
        var branches = new List<RewardBranch>(bundles.Length + 1);
        if (probability < 1) branches.Add(new(1 - probability, 0, 0, []));
        foreach (var bundle in bundles)
        {
            var mass = probability * bundle.Weight / totalWeight;
            if (mass == 0) continue;
            var score = bundle.Effects.Sum(effect => effect.Target switch
            {
                6 => 2 * effect.Value,
                >= 1 and <= 7 => effect.Value,
                _ => 0
            });
            branches.Add(new(mass, score, triggeredGroup, bundle.Effects));
        }
        return new(requiredGroups, groups, [.. branches]);
    }

    static double[] ScoreMass(RewardRoll[] rolls)
    {
        if (rolls.Length == 1)
        {
            var branches = rolls[0].Eligible(0);
            var mass = new double[branches.Max(branch => branch.Score) + 1];
            foreach (var branch in branches) mass[branch.Score] += branch.Probability;
            return mass;
        }
        var states = new Dictionary<(int Score, ulong Groups), double> { [(0, 0)] = 1 };
        foreach (var roll in rolls)
        {
            var next = new Dictionary<(int, ulong), double>();
            foreach (var (state, probability) in states)
                foreach (var branch in roll.Eligible(state.Groups))
                {
                    var key = (state.Score + branch.Score, state.Groups | branch.TriggeredGroup);
                    next[key] = next.GetValueOrDefault(key) + probability * branch.Probability;
                }
            states = next;
        }
        var distribution = new double[states.Keys.Max(state => state.Score) + 1];
        foreach (var (state, probability) in states) distribution[state.Score] += probability;
        return distribution;
    }

    void AddWeightedRewards(RewardRoll[] rolls, double[]? scoreWeights, double conversionMean)
    {
        var slots = rolls.SelectMany(roll => roll.Branches).SelectMany(branch => branch.Effects)
            .Where(effect => effect.Target == 41 && !effect.Owned).Select(effect => effect.HintIndex)
            .Distinct().DefaultIfEmpty(-1).ToArray();
        if (rolls.Length == 1)
        {
            foreach (var branch in rolls[0].Eligible(0))
                for (var h = 0; h < slots.Length; h++)
                    Accumulate(branch.Effects, branch.Probability * (scoreWeights?[branch.Score] ?? 1), h, -1);
            return;
        }
        var remaining = new Dictionary<(int Index, int Score, ulong Groups), double>();

        double WinnerWeight(int index, int score, ulong triggered)
        {
            if (scoreWeights is null) return 1;
            if (index == rolls.Length) return scoreWeights[score];
            var key = (index, score, triggered);
            if (remaining.TryGetValue(key, out var weight)) return weight;
            foreach (var branch in rolls[index].Eligible(triggered))
                weight += branch.Probability * WinnerWeight(index + 1, score + branch.Score, triggered | branch.TriggeredGroup);
            remaining.Add(key, weight);
            return weight;
        }

        // Only repeated grants to the same skill interact. Track one skill's accumulated levels
        // at a time, sharing the other factors' conditional winner weights across skills.
        for (var h = 0; h < slots.Length; h++)
        {
            var states = new Dictionary<(int Score, ulong Groups, int Granted), double> { [(0, 0, -1)] = 1 };
            for (var i = 0; i < rolls.Length; i++)
            {
                var next = new Dictionary<(int, ulong, int), double>();
                foreach (var (state, probability) in states)
                {
                    foreach (var branch in rolls[i].Eligible(state.Groups))
                    {
                        var score = scoreWeights is null ? 0 : state.Score + branch.Score;
                        var triggered = state.Groups | branch.TriggeredGroup;
                        var mass = probability * branch.Probability;
                        var selected = mass * WinnerWeight(i + 1, score, triggered);
                        var granted = Accumulate(branch.Effects, selected, h, state.Granted);
                        var key = (score, triggered, granted);
                        next[key] = next.GetValueOrDefault(key) + mass;
                    }
                }
                states = next;
            }
        }

        int Accumulate(Effect[] effects, double mass, int h, int granted)
        {
            foreach (var effect in effects)
            {
                if (effect.Target == 41 && !effect.Owned)
                {
                    if (effect.HintIndex != slots[h]) continue;
                    var hint = hints[Array.BinarySearch(hintSlots, effect.HintIndex)];
                    if (granted < 0) hint.Unlock += mass;
                    granted = Math.Max(0, granted);
                    for (var initial = 0; initial <= 5; initial++)
                    {
                        var room = 5 - initial - granted;
                        if (room <= 0) hint.Conversions[initial] += mass;
                        else hint.Gains[initial] += mass * Math.Min(room, effect.Count);
                    }
                    granted = Math.Min(5, granted + effect.Count);
                }
                else if (h == 0)
                {
                    switch (effect.Target)
                    {
                        case >= 1 and <= 5: attributes[effect.Target - 1] += mass * effect.Value; break;
                        case 6: skillPoints += mass * effect.Value; break;
                        case 7 or 41:
                            var gain = mass * (effect.Target == 7 ? effect.Value : conversionMean) / 5;
                            for (var a = 0; a < 5; a++) attributes[a] += gain;
                            break;
                        case >= 61 and <= 65: caps[effect.Target - 61] += mass * effect.Value; break;
                        default: aptitudes[Aptitude.GroupIds.IndexOf(effect.Target)] += mass * effect.Value; break;
                    }
                }
            }
            return granted;
        }
    }
}
