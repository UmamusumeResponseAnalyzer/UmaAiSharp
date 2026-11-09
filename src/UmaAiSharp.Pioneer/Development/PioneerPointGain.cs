namespace UmaAiSharp.Pioneer.Development;

public static class PioneerPointGain
{
    public const string Version = "training-development-points-v2";

    // Counts support cards and PJ members in ordinary/camp training; director/reporter are excluded.
    public static int OrdinaryTraining(int participants, int checkpointBonusPercent, bool friendshipTraining)
    {
        if (participants < 0 || checkpointBonusPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(participants), "参与人数和评审发展 Pt 加成不能为负数。");
        return checked((int)((60L + 6L * participants) * (100L + checkpointBonusPercent + (friendshipTraining ? 20 : 0)) / 100));
    }
}
