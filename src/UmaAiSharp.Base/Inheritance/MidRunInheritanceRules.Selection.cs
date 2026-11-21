namespace UmaAiSharp.Base.Inheritance;

public sealed partial class MidRunInheritanceRules
{
    static double[][] WinnerWeights(double[][] scoreMass)
    {
        var prefix = new double[scoreMass.Length + 1][];
        prefix[0] = [1];
        for (var group = 0; group < scoreMass.Length; group++)
        {
            var local = scoreMass[group];
            var before = prefix[group];
            if (local.Length == 1)
            {
                prefix[group + 1] = before;
                continue;
            }
            var after = new double[before.Length + local.Length - 1];
            for (var score = 0; score < local.Length; score++)
            {
                var probability = local[score];
                if (probability == 0) continue;
                for (var prior = 0; prior < before.Length; prior++)
                    after[score + prior] += probability * before[prior];
            }
            prefix[group + 1] = after;
        }

        var total = prefix[^1];
        var win = new double[total.Length];
        var below = 0.0;
        for (var score = 0; score < total.Length; score++)
        {
            // A first candidate wins ties, a second candidate wins strictly: P(S<=s) + P(S<s).
            win[score] = 2 * below + total[score];
            below += total[score];
        }

        var weights = new double[scoreMass.Length][];
        for (var group = scoreMass.Length - 1; group >= 0; group--)
        {
            var local = scoreMass[group];
            if (local.Length == 1)
            {
                weights[group] = [1]; // A group with constant zero score is independent of selection.
                continue;
            }
            var before = prefix[group];
            var previousWin = new double[before.Length];
            var localWeights = new double[local.Length];
            for (var score = 0; score < local.Length; score++)
            {
                var probability = local[score];
                if (probability == 0) continue;
                var weight = 0.0;
                for (var prior = 0; prior < before.Length; prior++)
                {
                    var choice = win[score + prior];
                    weight += before[prior] * choice;
                    previousWin[prior] += probability * choice;
                }
                localWeights[score] = weight;
            }
            weights[group] = localWeights;
            win = previousWin;
        }
        return weights;
    }
}
