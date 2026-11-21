namespace UmaAiSharp.Base.Training;

public static class TrainingBonuses
{
    public const int SupportCardBond = 7;
    public const int FriendCardBond = 4;

    public static long ParticipantPercent(long participantCount) => checked(100 + 5 * participantCount);

    // Standard five-level motivation scale (1..5).
    public static long MotivationPermille(int motivation, long supportBonusPercent)
        => checked(1000 + (motivation - 3) * (100 + supportBonusPercent));

    public static int BondAfterTraining(int bond, bool friendCard, bool charm, int deckBonus)
        => (int)Math.Min(100, (long)bond + (friendCard ? FriendCardBond : SupportCardBond) + (charm ? 2 : 0) + deckBonus);
}
