using UmaAiSharp.Base.Inheritance;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    void SettleScheduledInheritance(PioneerState state)
    {
        if (state.Header.Turn is not (MidRunInheritanceRules.ClassicTurn or MidRunInheritanceRules.SeniorTurn)
            || data.TurnRules?.Inheritance is not { } inheritance) return;
        ref var header = ref state.Mutable;
        var before = header.Resources.SkillPoints;
        inheritance.Apply(ref header.Resources, ref header.Limits, state.MutableSection(PioneerStateSection.InheritanceAptitudes),
            state.MutableSection(PioneerStateSection.HintLevels), ref header.FutureOverflow, header.SampleIndex, header.Turn);
        header.FutureGainedSkillPoints += header.Resources.SkillPoints - before;
        header.CompletedInheritances++;
    }
}
