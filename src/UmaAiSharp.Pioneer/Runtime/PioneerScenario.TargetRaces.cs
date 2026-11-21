using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Base.Races;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    static bool GoldShipRepeatWin(PioneerState state, RaceOption race)
        => race.CareerGoalId == 273 && state.GetSection(PioneerStateSection.RaceWins)[36] == 74;

    void ApplyCareerRouteAfterWin(PioneerState state, int goal, ref SimulationRandom random)
    {
        ref var current = ref state.Mutable;
        var start = data.TrainingStart!;
        var character = start.CharacterId ?? current.CardId / 100;
        if (goal == 1474 && start.HomeActions?.EspoirRoute is { } route)
        {
            var starts = 1; // Today's Nambu Hai is recorded after its reward is settled.
            foreach (var program in state.GetSection(PioneerStateSection.RaceWins)[start.Turn..current.Turn])
                if (program > 0) starts++;
            current.RaceBranch = route.GoalAfter(starts);
            return;
        }
        var draw = random.At(current.Turn, PioneerDraw.CareerRoute);
        if (CharacterRaceRoutes.BranchAfterWin(character, goal, ref draw) is { } branch)
            current.RaceBranch = branch;
    }

    void ApplyCareerGoalReward(PioneerState state, RaceOption race = default, TrainingAttribute? excludedAttribute = null)
    {
        ref var header = ref state.Mutable;
        var character = data.TrainingStart!.CharacterId ?? header.CardId / 100;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var percent = data.TurnRules!.RaceRewardPercent;
        var goal = race.CareerGoalId;
        var wins = state.GetSection(PioneerStateSection.RaceWins);
        var hintGroups = CharacterRaceRoutes.GoalRaceHintGroups(character);
        var attribute = Scale(3);
        var points = Scale(45);
        var attention = header.HasAttention;

        if (race.Target && effects.Contains(31))
        {
            (attribute, points) = (Scale(8), Scale(52));
            if (character == 1063 && goal == 1156) Hint(hintGroups[0], 1);
        }
        else if (CharacterRaceRoutes.VictoryReward(character, goal, percent) is { } prize)
        {
            (attribute, points) = (prize.Attribute, prize.SkillPoints);
            Hint(prize.Hint.Skill.GroupId, prize.Hint.Level);
        }
        else if (character == 1050 && goal == 350)
        {
            var badPractice = effects.Contains(6);
            // shortcut: the untreated branch has no Pioneer sample; use the measured clean branch and published branch difference.
            (attribute, points) = badPractice ? (Scale(20), Scale(73)) : (Scale(25), Scale(80));
            if (badPractice) state.RemoveCharacterEffect(6);
            else
            {
                if (effects.Contains(10)) state.RemoveCharacterEffect(10);
                state.AddCharacterEffect(11);
            }
            ApplyMotivationGain(state, 3);
        }
        else if (character == 1075 && goal is 1708 or 1712)
            points = Scale(75);
        else if (character == 1022 && goal == 549)
            points = Scale(35);

        if (excludedAttribute is null) ApplyProgressionReward(state, attribute, points);
        else ApplyProgressionReward(state, new(
            excludedAttribute == TrainingAttribute.Speed ? 0 : attribute, excludedAttribute == TrainingAttribute.Stamina ? 0 : attribute,
            excludedAttribute == TrainingAttribute.Power ? 0 : attribute, excludedAttribute == TrainingAttribute.Guts ? 0 : attribute,
            excludedAttribute == TrainingAttribute.Wisdom ? 0 : attribute, 0, points));
        switch (character, goal)
        {
            case (1022, 549) when data.TrainingStart!.HomeActions?.RunningStyle == 4:
                Hint(hintGroups[5], 1);
                break;
            case (1022, >= 550 and <= 553):
                Hint(hintGroups[goal - 550], 2);
                if (goal == 553) Hint(hintGroups[4], 2);
                break;
            case (1031, 684):
                Hint(hintGroups[0], 1);
                break;
            case (1031, 690):
                Hint(hintGroups[1], 2);
                break;
            case (1075, 1707):
                Hint(hintGroups[0], 1);
                break;
            case (1075, 1708 or 1712):
                Hint(hintGroups[goal == 1708 ? 1 : 2], 3);
                break;
            case (1063, 1146):
                state.AddCharacterEffect(31);
                break;
            case (1003, 82) or (1017, 197) or (1026, 314) when wins[31] == 163 && wins[34] == 166:
                ApplyProgressionReward(state, TrainingAttributeReward(3, Scale(character == 1026 ? 25 : 15)));
                Hint(hintGroups[0], 2);
                break;
            case (1007, 273) when GoldShipRepeatWin(state, race):
                // All five captures chose option one; it has two separately displayed attribute awards.
                ApplyProgressionReward(state, new(0, Scale(15), Scale(15), 0, 0, 0, 0));
                ApplyMotivationGain(state, 1);
                break;
            case (1008, 262) when wins[35] == 73:
                ApplyProgressionReward(state, TrainingAttributeReward(2, Scale(20)) with { SkillPoints = Scale(20) });
                Hint(hintGroups[0], 1);
                var alreadyExcellent = effects.Contains(11);
                if (effects.Contains(6)) state.RemoveCharacterEffect(6);
                if (!alreadyExcellent) state.AddCharacterEffect(10);
                break;
            case (1018, 173) when wins[31] == 162 && wins[34] == 165:
                // shortcut: +3 as a separate attribute award fits the two observed race-bonus rates.
                ApplyProgressionReward(state, Scale(3), Scale(20));
                break;
            case (1041, 238):
                Hint(hintGroups[0], 1);
                if (wins[42] != 75) break;
                // shortcut: +15 fits the four speed observations above 1200 across three rates.
                ApplyProgressionReward(state, TrainingAttributeReward(0, Scale(15)));
                Hint(hintGroups[1], 1);
                state.AddCharacterEffect(9);
                header.HasAttention = true;
                break;
            case (1073, 1347):
                // shortcut: a separate +5 matches all four Arima captures at +28% and +30%.
                ApplyProgressionReward(state, Scale(5), 0);
                break;
        }

        if (data.IsWholeRun)
            header.DirectorBond = Math.Min(100, header.DirectorBond + 4 + (attention ? 2 : 0));

        int Scale(int points) => points * percent / 100;
        void Hint(int group, int levels)
        {
            if (data.TurnRules.Hints is { } hints)
                GainHint(state, hints.FindSlot(new(group, 1)), levels);
        }
    }
}
