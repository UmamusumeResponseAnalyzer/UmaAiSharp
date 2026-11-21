using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Scoring;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    void ApplyOrdinaryStory(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        if (!header.TuckerSideStorySeen && header.Turn is >= 4 and <= 71 && !IsCamp(header.Turn)
            && data.TurnRules!.TuckerMask != 0)
            ApplyTuckerSideStory(state, ref random);

        // shortcut: one UmaAi-rust lottery on the Pioneer calendar. Separate additional windows when paired data identifies them.
        if (header.Turn is 1 or 3 or 25 or 31 or >= 37 and <= 40 or 49 or 51 or 55 or >= 61 and <= 64 or >= 72
            || DrawFriendUnlock(state, ref random)) return;

        ref var progress = ref header.OrdinaryStories;
        var story = random.At(header.Turn, PioneerDraw.OrdinaryStory);
        var draw = story.NextInt(100);
        if (draw < 35 && data.TurnRules!.OrdinarySupportMask != 0)
        {
            Span<int> eligible = stackalloc int[3];
            var count = 0;
            if (progress.First < 5) eligible[count++] = 1;
            if (progress.Second < progress.First) eligible[count++] = 2;
            if (progress.Third < progress.Second) eligible[count++] = 3;
            if (count == 0) return;
            switch (eligible[story.NextScaled(count)])
            {
                case 1:
                    progress = progress with { First = progress.First + 1 };
                    ApplyProgressionReward(state, 4, 0);
                    break;
                case 2:
                    progress = progress with { Second = progress.Second + 1 };
                    ApplyProgressionReward(state, new(8, 6, 8, 6, 8, 0, 20));
                    break;
                case 3:
                    progress = progress with { Third = progress.Third + 1 };
                    ApplyProgressionReward(state, 8, 40);
                    break;
            }
        }
        else if (draw is >= 35 and < 60 && progress.Trainee < 7)
        {
            Span<int> eligible = stackalloc int[7];
            var count = 0;
            for (var i = 0; i < 7; i++)
                if ((header.TraineeStoryMask & (1 << i)) == 0) eligible[count++] = i;
            var index = eligible[story.NextScaled(count)];
            progress = progress with { Trainee = progress.Trainee + 1 };
            header.TraineeStoryMask |= 1 << index;
            if (index >= 5)
            {
                ApplyProgressionReward(state, index == 5 ? 0 : 5, 30);
                return;
            }
            ApplyCalendarChoice(state, [TrainingAttributeReward(index, 20), index == 4
                ? new(0, 0, 0, 0, 0, 0, 20) : TrainingAttributeReward(index + 1, 20)]);
        }
    }

    // Calendar and rewards from the fixed event windows in the Pioneer capture study.
    void ApplyFixedTurnEvents(PioneerState state, int turn, bool arriving, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        switch (turn, arriving)
        {
            case (25, true):
                var start = data.TrainingStart!;
                var tuckerSlot = -1;
                if (start.HomeActions is { } home)
                {
                    foreach (var friend in home.Friends)
                    {
                        var slot = friend.Position - 1;
                        if (friend.CharacterId != 9049 || (header.UnlockedFriendMask & (1 << slot)) == 0
                            || ((header.FriendOutings >> (slot * 3)) & 7) >= 5) continue;
                        tuckerSlot = slot;
                        break;
                    }
                }
                var scaling = tuckerSlot >= 0 ? EventEffectScaling.FromSupport(start.Deck[tuckerSlot]) : default;
                var tuckerRecovery = tuckerSlot >= 0 ? scaling.Apply(10, 15) : 0;
                Span<TrainingValues> newYearChoices = [default, new(0, 0, 0, 0, 0, 20, 0), new(0, 0, 0, 0, 0, 0, 20)];
                if (ClassicNewYearAttribute(start.CharacterId ?? header.CardId / 100) is int attribute)
                    newYearChoices[0] = TrainingAttributeReward(attribute, 25);
                else
                    newYearChoices = newYearChoices[1..];
                ApplyCalendarChoice(state, newYearChoices, tuckerRecovery);
                if (tuckerSlot >= 0)
                {
                    ApplyProgressionReward(state, TrainingAttributeReward(0, scaling.Apply(1, 10)));
                    RecoverVital(state, tuckerRecovery);
                    ApplyMotivationGain(state, 1);
                    GainFriendBond(state, tuckerSlot, 5);
                    GainHint(state, data.TurnRules!.TuckerNewYearHintSlot, 3);
                }
                break;
            case (40, true):
                ApplyCalendarChoice(state, [TrainingAttributeReward(2, 10), TrainingAttributeReward(3, 10)]);
                break;
            case (40 or 64, false):
                // Camp ending gives +5 to three distinct attributes. Use uniform groups as a prior.
                var camp = random.At(turn, PioneerDraw.FixedEvent);
                var excluded = camp.NextInt(5);
                var other = camp.NextInt(4);
                if (other >= excluded) other++;
                ApplyProgressionReward(state, new(Gain(0), Gain(1), Gain(2), Gain(3), Gain(4), 0, 0));
                int Gain(int index) => index == excluded || index == other ? 0 : 5;
                break;
            case (49, true):
                ApplyCalendarChoice(state, [new(0, 0, 0, 0, 0, 30, 0), new(8, 8, 8, 8, 8, 0, 0),
                    new(0, 0, 0, 0, 0, 0, 35)]);
                if (data.TrainingStart!.CharacterId == 1016)
                    header.StrongBrianRoute = HasBrianRouteQualification(state);
                break;
            case (49, false):
                // Fit item counts 24/14/61/79/18. Both top prizes settle the same resources.
                var draw = random.At(turn, PioneerDraw.FixedEvent).NextInt(196);
                if (draw < 38)
                {
                    ApplyProgressionReward(state, 10, 0);
                    RecoverVital(state, 30);
                    ApplyMotivationGain(state, 2);
                }
                else if (draw < 99)
                {
                    ApplyProgressionReward(state, 5, 0);
                    RecoverVital(state, 20);
                    ApplyMotivationGain(state, 1);
                }
                else if (draw < 178) RecoverVital(state, 20);
                else
                {
                    var penalty = MotivationRules.Apply(header.Motivation, -1,
                        state.GetSection(PioneerStateSection.CharacterEffects).Contains(25), false);
                    header.Motivation = penalty.Value;
                    if (penalty.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
                }
                break;
            case (51 or 55 or 72, true):
                var festival = turn == 55;
                var threshold = UniqueUpgradeFanThreshold(turn, data.TrainingStart!.CharacterId);
                if (header.Fans < threshold || festival && header.DirectorBond < 60)
                {
                    ApplyProgressionReward(state, 0, festival ? 25 : 20);
                    if (festival) RecoverVital(state, -5);
                }
                else
                {
                    header.FutureSkillUpgradeRating += data.TurnRules!.UniqueSkillLevelRating;
                    if (festival) ApplyMotivationGain(state, 1);
                }
                break;
            case (65, false):
                SettleSeniorIslandHint(state);
                break;
            case (70, true) when (data.TrainingStart!.CharacterId ?? header.CardId / 100) == 1062:
                ApplyProgressionReward(state, TrainingAttributeReward(3, 5));
                break;
        }
    }

    // Event 3001's first option is bound to the character's shared story, independent of outfit and current aptitudes.
    internal static int? ClassicNewYearAttribute(int character) => character switch
    {
        1002 or 1004 or 1008 or 1010 or 1020 or 1022 or 1031 or 1037 or 1038 or 1039
            or 1040 or 1041 or 1042 or 1046 or 1050 or 1051 or 1054 or 1064 or 1068 or 1078
            or 1082 or 1085 or 1087 or 1094 or 1099 or 1104 or 1107 or 1120 or 1124 => 0,
        1001 or 1007 or 1013 or 1021 or 1024 or 1025 or 1045 or 1048 or 1067 or 1074
            or 1076 or 1077 or 1089 or 1102 or 1106 or 1114 or 1132 or 1134 or 1135 => 1,
        1006 or 1012 or 1014 or 1015 or 1016 or 1018 or 1019 or 1026 or 1027 or 1028
            or 1033 or 1034 or 1043 or 1044 or 1065 or 1072 or 1083 or 1084 or 1088 or 1091
            or 1108 or 1109 or 1113 or 1115 or 1116 or 1121 or 1131 or 1141 or 1143 => 2,
        1003 or 1029 or 1030 or 1035 or 1052 or 1053 or 1055 or 1058 or 1061 or 1062
            or 1063 or 1066 or 1070 or 1073 or 1079 or 1081 or 1090 or 1092 or 1093 or 1095
            or 1100 or 1111 or 1112 or 1117 or 1127 or 1129 or 1130 or 1136 or 1137 or 1144 or 1149 => 3,
        1005 or 1009 or 1011 or 1017 or 1023 or 1032 or 1036 or 1047 or 1049 or 1056
            or 1057 or 1059 or 1060 or 1069 or 1071 or 1080 or 1086 or 1096 or 1097 or 1098
            or 1103 or 1105 or 1110 or 1118 or 1119 or 1133 or 1145 => 4,
        _ => null,
    };

    static int UniqueUpgradeFanThreshold(int turn, int? character)
    {
        // Base dirt A and turf E–G in the Japanese master. Inherited aptitudes don't define this group.
        var dirt = character is 1043 or 1046 or 1052 or 1079 or 1080 or 1081 or 1098 or 1099 or 1100;
        return (turn, dirt) switch
        {
            (51, true) => 40_000, (51, false) => 60_000,
            (55, true) => 60_000, (55, false) => 70_000,
            (72, true) => 80_000, (72, false) => 120_000,
            _ => throw new ArgumentOutOfRangeException(nameof(turn)),
        };
    }

    void SettleSeniorIslandHint(PioneerState state)
    {
        var rules = data.TurnRules!;
        var hints = state.MutableSection(PioneerStateSection.HintLevels);
        var aptitudes = state.GetSection(PioneerStateSection.InheritanceAptitudes);
        var discount = FutureSkillRating.PurchaseDiscount(state.GetSection(PioneerStateSection.CharacterEffects));
        var bestSlot = rules.SeniorIslandHintSlots[0];
        var bestValue = double.NegativeInfinity;
        foreach (var slot in rules.SeniorIslandHintSlots)
        {
            var level = Math.Min(5, Math.Max(0, hints[slot]) + 1);
            var value = rules.SkillRating!.HintValue(slot, level, hints, aptitudes, state.Header.Resources, discount);
            if (value <= bestValue) continue;
            bestSlot = slot;
            bestValue = value;
        }
        GainHint(state, bestSlot, 1);
    }

    static void GainHint(PioneerState state, int slot, int levels)
    {
        ref var current = ref state.MutableSection(PioneerStateSection.HintLevels)[slot];
        current = Math.Min(5, Math.Max(0, current) + levels);
    }

    static void RecoverVital(PioneerState state, int gain)
    {
        ref var current = ref state.Mutable;
        current.Resources = current.Resources with
        {
            Vital = VitalRules.Apply(current.Resources.Vital, current.Limits.Vital, gain, 0).Vital,
        };
    }

    static int NpcTrainingReward(int bond) => bond switch { >= 90 => 5, >= 70 => 4, >= 40 => 3, _ => 2 };

    static TrainingValues TrainingAttributeReward(int training, int amount) =>
        new(training == 0 ? amount : 0, training == 1 ? amount : 0, training == 2 ? amount : 0,
            training == 3 ? amount : 0, training == 4 ? amount : 0, 0, 0);

    void ApplyTrainingNpcEvents(PioneerState state, int training, bool withDirector, bool withReporter,
        bool camp, ref SimulationRandom random)
    {
        ref var current = ref state.Mutable;
        var bondGain = current.HasAttention ? 7 : 5;
        if (withDirector)
        {
            ApplyProgressionReward(state, 0, NpcTrainingReward(current.DirectorBond));
            current.DirectorBond = Math.Min(100, current.DirectorBond + bondGain);
        }
        if (withReporter)
            ApplyReporterTrainingEvent(state, training);
        // 379/7034 paired ordinary successes revealed extra practice.
        if (camp || random.At(current.Turn, PioneerDraw.ExtraPractice).NextInt(7034) >= 379) return;
        ReadOnlySpan<TrainingValues> choices = [TrainingAttributeReward(training, 5) with { Vital = -5 },
            new(0, 0, 0, 0, 0, 5, 0)];
        var directorValue = DirectorBondValue(current, bondGain);
        var choice = SelectResourceReward(state, choices, otherValues: [directorValue, 0]);
        ApplyProgressionReward(state, choices[choice]);
        if (choice == 0) current.DirectorBond = Math.Min(100, current.DirectorBond + bondGain);
    }

    static void ApplyReporterTrainingEvent(PioneerState state, int training)
    {
        ref var header = ref state.Mutable;
        var attention = header.HasAttention ? 2 : 0;
        ApplyProgressionReward(state, TrainingAttributeReward(training, NpcTrainingReward(header.ReporterBond) + attention));
        header.ReporterBond = Math.Min(100, header.ReporterBond + 7 + attention);
    }
}
