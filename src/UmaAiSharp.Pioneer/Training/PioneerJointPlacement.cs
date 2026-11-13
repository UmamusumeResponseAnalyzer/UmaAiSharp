using System.Collections.Immutable;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Training;

public sealed record PlacementMember(int TargetId, bool Appeared, PioneerPlacementDistribution Distribution);

// Hint flags belong to the turn; capacity rejection redraws positions, not those flags.
public sealed class PioneerJointPlacement
{
    public const string Version = "pioneer-joint-placement-v1";
    public ImmutableArray<PlacementMember> Members { get; }
    public long SharedMemoryBytes => checked(96L + 48L * Members.Length
        + Members.Select(member => member.Distribution).Distinct().Sum(distribution => distribution.SharedMemoryBytes));

    public PioneerJointPlacement(ImmutableArray<PlacementMember> members)
    {
        if (members.IsDefault)
            throw new InvalidDataException("联合分配需要明确、不重复的参与者身份及分布。");

        var ordered = true;
        var previous = 0;
        foreach (var member in members)
        {
            if (member is null || member.Distribution is null || member.TargetId <= 0)
                throw new InvalidDataException("联合分配需要明确、不重复的参与者身份及分布。");
            ordered &= member.TargetId > previous;
            previous = member.TargetId;
        }
        Members = ordered ? members : members.Sort(static (left, right) => left.TargetId.CompareTo(right.TargetId));
        if (Members.Length < 6)
            throw new InvalidDataException("联合分配要求六个支援卡位，以及明确的PJ成员；其他人员类型尚未接入。");
        for (var i = 0; i < Members.Length; i++)
        {
            var member = Members[i];
            if (!ordered && i > 0 && member.TargetId == Members[i - 1].TargetId)
                throw new InvalidDataException("联合分配需要明确、不重复的参与者身份及分布。");
            var npc = member.Distribution.Kind == PlacementMemberKind.ProjectNpc;
            if (i < 6 ? member.TargetId != i + 1 || npc : member.TargetId < 1000 || !npc)
                throw new InvalidDataException("联合分配要求六个支援卡位，以及明确的PJ成员；其他人员类型尚未接入。");
        }
    }

    public TrainingBoard Sample(Span<SupportPlacement> placements, ref SimulationRandom random,
        ReadOnlySpan<int> facilityHintPercent = default, bool paired = false)
    {
        if (placements.Length != Members.Length)
            throw new ArgumentException("联合分配结果缓冲区必须与按target_id排序的成员数一致。", nameof(placements));
        var hints = 0;
        var hintRandom = paired ? random.Fork(1) : random;
        if (facilityHintPercent.IsEmpty)
            for (var index = 0; index < 6; index++)
            {
                var member = Members[index];
                if (!member.Appeared) continue;
                var hint = paired ? hintRandom.Fork((ulong)index) : default;
                ref var draw = ref (paired ? ref hint : ref random);
                if (member.Distribution.SampleHint(ref draw, paired: paired)) hints |= 1 << index;
            }
        Span<int> counts = stackalloc int[5];
        var attempt = 0UL;
        while (true)
        {
            var positions = paired ? random.Fork(2 + attempt++) : random;
            var board = new TrainingBoard();
            counts.Clear();
            for (var index = 0; index < Members.Length; index++)
            {
                var member = Members[index];
                var position = paired ? positions.Fork((ulong)member.TargetId) : default;
                ref var draw = ref (paired ? ref position : ref random);
                var training = member.Distribution.SamplePosition(member.Appeared, ref draw, paired);
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
            if (counts[0] > 5 || counts[1] > 5 || counts[2] > 5 || counts[3] > 5 || counts[4] > 5) continue;
            if (!facilityHintPercent.IsEmpty)
                for (var index = 0; index < 6; index++)
                {
                    var training = placements[index].TrainingIndex;
                    if (training < 0) continue;
                    var hint = paired ? hintRandom.Fork((ulong)index) : default;
                    ref var draw = ref (paired ? ref hint : ref random);
                    if (!Members[index].Distribution.SampleHint(ref draw, facilityHintPercent[training], paired)) continue;
                    placements[index] = placements[index] with { HasHint = true };
                    board = board.With(training, board[training] with { HintMask = (byte)(board[training].HintMask | (1 << index)) });
                }
            return board;
        }
    }
}
