namespace UmaAiSharp.Pioneer.Training;

public static class MotivationRules
{
    public static (int Value, bool ConsumePositiveThinking) Apply(int current, int gain, bool positiveThinking, bool migraine)
    {
        var consume = gain < 0 && positiveThinking;
        return (consume || gain > 0 && migraine ? current : (int)Math.Clamp((long)current + gain, 1, 5), consume);
    }
}
