using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    void SettleScheduledInheritance(PioneerState state, ref SimulationRandom random)
    {
        if (state.Header.Turn is not (31 or 55) || data.TurnRules?.Inheritance is not { } inheritance) return;
        ref var header = ref state.Mutable;
        var before = header.Resources.SkillPoints;
        var lottery = random.At(header.Turn, PioneerDraw.Inheritance);
        inheritance.Apply(ref header.Resources, ref header.Limits, state.MutableSection(PioneerStateSection.InheritanceAptitudes),
            state.MutableSection(PioneerStateSection.HintLevels), ref header.FutureOverflow, ref lottery);
        header.FutureGainedSkillPoints += header.Resources.SkillPoints - before;
        header.CompletedInheritances++;
    }
}
