using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    internal static ReadOnlySpan<int> GoalRaceHintGroups(int character) => character switch
    {
        1003 => [20190], 1008 => [20049], 1016 => [20135, 20049, 20061, 20036],
        1017 => [20169], 1022 => [20072, 20134, 20070, 20057, 20190, 20062], 1026 => [20037],
        1031 => [20125, 20054], 1041 => [20128, 20204], 1063 => [20279], 1075 => [20357, 20472, 20444], _ => [],
    };

    bool HasBrianRouteQualification(PioneerState state)
    {
        var wins = state.GetSection(PioneerStateSection.RaceWins);
        var points = 0;
        // The Classic triple-crown races count twice. Other Classic G1 wins count once.
        for (var turn = 25; turn <= 48; turn++)
            if (wins[turn] is 163 or 166 or 168) points += 2;
            else if (data.TurnRules!.IsGradeOne(wins[turn])) points++;
        return points >= 6;
    }

    static bool GoldShipRepeatWin(PioneerState state, PioneerRaceOption race)
        => race.CareerGoalId == 273 && state.GetSection(PioneerStateSection.RaceWins)[36] == 74;

    void ApplyCareerRouteAfterWin(PioneerState state, int goal, int raceMotivation, ref SimulationRandom random)
    {
        ref var current = ref state.Mutable;
        switch (goal)
        {
            case 183 when raceMotivation <= 3:
                current.RaceBranch = 296;
                ApplyProgressionReward(state, TrainingAttributeReward(4, 5));
                break;
            case 89:
                // shortcut: six observed draws picked 293; add one count per possible result until manual openings cover the other two.
                var debut = random.At(current.Turn, PioneerDraw.CareerRoute).NextScaled(9);
                current.RaceBranch = debut switch { 0 => 90, 1 => 292, _ => 293 };
                break;
            case 94:
                var takarazuka = random.At(current.Turn, PioneerDraw.CareerRoute).NextScaled(6);
                current.RaceBranch = takarazuka switch { 0 => 294, 1 => 295, _ => 95 };
                break;
            case 1474 when data.TrainingStart!.HomeActions?.EspoirRoute is { } route:
                var starts = 1; // Include today's Nambu Hai, recorded after settling its reward.
                foreach (var program in state.GetSection(PioneerStateSection.RaceWins)[data.TrainingStart.Turn..current.Turn])
                    if (program > 0) starts++;
                current.RaceBranch = route.GoalAfter(starts);
                break;
        }
    }

    void ApplyCareerGoalReward(PioneerState state, PioneerRaceOption race = default, int excludedAttribute = -1)
    {
        ref var header = ref state.Mutable;
        var character = data.TrainingStart!.CharacterId ?? header.CardId / 100;
        var effects = state.GetSection(PioneerStateSection.CharacterEffects);
        var percent = data.TurnRules!.RaceRewardPercent;
        var goal = race.CareerGoalId;
        var wins = state.GetSection(PioneerStateSection.RaceWins);
        var hintGroups = GoalRaceHintGroups(character);
        var baseAttribute = 3;
        var basePoints = 45;
        var attention = header.HasAttention;

        if (race.Target && effects.Contains(31))
        {
            (baseAttribute, basePoints) = (8, 52);
            if (character == 1063 && goal == 1156) Hint(hintGroups[0], 1);
        }
        else if (character == 1016 && goal is 378 or 380 or 382 or 384)
        {
            (baseAttribute, basePoints) = (5, 50);
            Hint(hintGroups[(goal - 378) / 2], 2);
        }
        else if (character == 1050 && goal == 350)
        {
            var badPractice = effects.Contains(6);
            // shortcut: the untreated branch has no Pioneer sample; use the measured clean branch and published branch difference.
            (baseAttribute, basePoints) = badPractice ? (20, 73) : (25, 80);
            if (badPractice) state.RemoveCharacterEffect(6);
            else
            {
                if (effects.Contains(10)) state.RemoveCharacterEffect(10);
                state.AddCharacterEffect(11);
            }
            ApplyMotivationGain(state, 3);
        }
        else if (character == 1075 && goal is 1708 or 1712)
            basePoints = 75;
        else if (character == 1022 && goal == 549)
            basePoints = 35;

        var attribute = Scale(baseAttribute);
        if (excludedAttribute == -1) ApplyProgressionReward(state, attribute, Scale(basePoints));
        else ApplyProgressionReward(state, new(
            excludedAttribute == 0 ? 0 : attribute, excludedAttribute == 1 ? 0 : attribute,
            excludedAttribute == 2 ? 0 : attribute, excludedAttribute == 3 ? 0 : attribute,
            excludedAttribute == 4 ? 0 : attribute, 0, Scale(basePoints)));
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
