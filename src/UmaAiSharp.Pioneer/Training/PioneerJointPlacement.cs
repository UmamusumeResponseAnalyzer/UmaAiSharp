using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public sealed record PlacementMember(int TargetId, bool Appeared, PioneerPlacementDistribution Distribution);

// Hint flags belong to the turn; capacity rejection redraws positions, not those flags.
public sealed class PioneerJointPlacement
{
    public const string Version = "pioneer-joint-placement-v1";
    public ImmutableArray<PlacementMember> Members { get; }
    public long SharedMemoryBytes { get; }

    public PioneerJointPlacement(ImmutableArray<PlacementMember> members)
    {
        if (members.IsDefault || members.Any(member => member is null || member.Distribution is null || member.TargetId <= 0)
            || members.Select(member => member.TargetId).Distinct().Count() != members.Length)
            throw new InvalidDataException("联合分配需要明确、不重复的参与者身份及分布。");
        Members = [.. members.OrderBy(member => member.TargetId)];
        if (Members.Length < 6 || !Members.Take(6).Select(member => member.TargetId).SequenceEqual([1, 2, 3, 4, 5, 6])
            || Members.Take(6).Any(member => member.Distribution.Kind == PlacementMemberKind.ProjectNpc)
            || Members.Skip(6).Any(member => member.TargetId < 1000 || member.Distribution.Kind != PlacementMemberKind.ProjectNpc))
            throw new InvalidDataException("联合分配要求六个支援卡位，以及明确的PJ成员；其他人员类型尚未接入。");
        SharedMemoryBytes = checked(96L + 48L * Members.Length
            + Members.Select(member => member.Distribution).Distinct().Sum(distribution => distribution.SharedMemoryBytes));
    }

    public TrainingBoard Sample(Span<SupportPlacement> placements, ref SimulationRandom random)
    {
        if (placements.Length != Members.Length)
            throw new ArgumentException("联合分配结果缓冲区必须与按target_id排序的成员数一致。", nameof(placements));
        var hints = 0;
        for (var index = 0; index < 6; index++)
            if (Members[index].Appeared && Members[index].Distribution.SampleHint(ref random)) hints |= 1 << index;
        Span<int> counts = stackalloc int[5];
        while (true)
        {
            var board = new TrainingBoard();
            counts.Clear();
            for (var index = 0; index < Members.Length; index++)
            {
                var member = Members[index];
                var training = member.Distribution.SamplePosition(member.Appeared, ref random);
                var hint = index < 6 && training >= 0 && (hints & (1 << index)) != 0;
                placements[index] = new(training, hint);
                if (training < 0) continue;
                counts[training]++;
                // Finish all draws in a rejected board, with no partial placement committed.
                if (counts[training] > 5) continue;
                var cell = board[training];
                board = board.With(training, index < 6
                    ? cell with { SupportMask = (byte)(cell.SupportMask | (1 << index)), HintMask = (byte)(cell.HintMask | (hint ? 1 << index : 0)) }
                    : cell with { GuestCount = (byte)(cell.GuestCount + 1) });
            }
            if (counts[0] <= 5 && counts[1] <= 5 && counts[2] <= 5 && counts[3] <= 5 && counts[4] <= 5) return board;
        }
    }
}
