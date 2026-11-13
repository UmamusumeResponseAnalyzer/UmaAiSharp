namespace UmaAiSharp.Pioneer.Scoring;

internal static class FutureSkillRating
{
    const int HistoricalPoolSize = 31;
    const int ManualPoolSize = 45;

    // SkillTips purchase scores above the owned-skill baseline for 12 historical menus and one manual menu.
    static readonly (int Budget, int Historical, int Manual)[] curve =
    [
        (0, 0, 0),
        (1000, 2542, 2998),
        (2000, 4593, 5496),
        (3000, 6382, 7738),
        (4000, 7948, 9921),
        (5000, 9142, 12010),
        (6000, 9768, 14005),
        (7000, 9922, 15874),
        (8000, 9936, 17627),
        (10000, 9936, 20584),
        (12000, 9936, 21042),
    ];

    internal static int Estimate(int startingBudget, int gainedPoints, int supportPoolSize)
    {
        // shortcut: support Hint pool size proxies menu breadth, pending complete terminal purchase menus.
        var size = Math.Clamp(supportPoolSize, HistoricalPoolSize, ManualPoolSize);
        return RatingAt(checked(startingBudget + gainedPoints), size) - RatingAt(startingBudget, size);
    }

    static int RatingAt(int budget, int poolSize)
    {
        var right = 1;
        while (right < curve.Length - 1 && budget > curve[right].Budget) right++;
        var before = curve[right - 1];
        var after = curve[right];
        var width = after.Budget - before.Budget;
        var spent = Math.Min(budget, after.Budget) - before.Budget;
        var historical = before.Historical * width + (after.Historical - before.Historical) * spent;
        var manual = before.Manual * width + (after.Manual - before.Manual) * spent;
        return (historical * (ManualPoolSize - poolSize) + manual * (poolSize - HistoricalPoolSize))
            / (width * (ManualPoolSize - HistoricalPoolSize));
    }
}
