namespace UmaAiSharp.Base.Training;

public static class AttributeGain
{
    // Convert a positive training or event reward to displayed units before applying the scenario's cap.
    public static int FromRaw(int before, int gain)
    {
        var below = Math.Min(gain, Math.Max(0, 1200 - before));
        var above = gain - below;
        return below + (above > 0 ? Math.Max(1, above / 2) : 0);
    }
}
