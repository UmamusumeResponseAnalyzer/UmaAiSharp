namespace UmaAiSharp.Pioneer.Training;

internal static class VitalRules
{
    public const int Maximum = 120;

    public static (int Vital, int Limit) Apply(int vital, int limit, int gain, int limitGain)
    {
        var maximum = (int)Math.Min(Maximum, (long)limit + limitGain);
        return ((int)Math.Clamp((long)vital + gain, 0, maximum), maximum);
    }
}
