using UmaAiSharp.Core;
using UmaAiSharp.Core.Goals;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Runtime;

public sealed partial class PioneerScenario
{
    void ApplyOrdinaryStory(PioneerState state, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
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
            var values = header.Resources;
            var caps = header.Limits;
            ReadOnlySpan<int> attributes = [values.Speed, values.Stamina, values.Power, values.Guts, values.Wisdom, 0];
            ReadOnlySpan<int> limits = [caps.Speed, caps.Stamina, caps.Power, caps.Guts, caps.Wisdom, 0];
            var left = DeclaredEventRewards.Attribute(attributes[index], limits[index], 20).Value - attributes[index];
            var right = index == 4 ? SkillValue(20)
                : DeclaredEventRewards.Attribute(attributes[index + 1], limits[index + 1], 20).Value - attributes[index + 1];
            var choice = left >= right ? index : index + 1;
            ApplyProgressionReward(state, new(choice == 0 ? 20 : 0, choice == 1 ? 20 : 0, choice == 2 ? 20 : 0,
                choice == 3 ? 20 : 0, choice == 4 ? 20 : 0, 0, choice == 5 ? 20 : 0));
        }
    }

    // Calendar and rewards from the fixed event windows in the Pioneer capture study.
    void ApplyFixedTurnEvents(PioneerState state, int turn, bool arriving, ref SimulationRandom random)
    {
        ref var header = ref state.Mutable;
        switch (turn, arriving)
        {
            case (25, true):
                // shortcut: the observed Pt option is the fixed policy choice; keep other player choices in conditional replay.
                ApplyProgressionReward(state, 0, 20);
                if (data.TrainingStart?.HomeActions is { } home)
                {
                    foreach (var friend in home.Friends)
                    {
                        if (friend.CharacterId != 9049 || (header.UnlockedFriendMask & (1 << (friend.Position - 1))) == 0
                            || ((header.FriendOutings >> ((friend.Position - 1) * 3)) & 7) >= 5) continue;
                        var scaling = EventEffectScaling.FromSupport(data.TrainingStart.Deck[friend.Position - 1]);
                        ApplyProgressionReward(state, new(scaling.Apply(1, 10), 0, 0, 0, 0, 0, 0));
                        Recover(state, scaling.Apply(10, 15));
                        ApplyMotivationGain(state, 1);
                        var bonds = state.MutableSection(PioneerStateSection.SupportBonds);
                        bonds[friend.Position - 1] = Math.Min(100, bonds[friend.Position - 1] + (header.HasCharm ? 7 : 5));
                    }
                }
                break;
            case (40, true):
                ApplyProgressionReward(state, new(0, 0, 10, 0, 0, 0, 0));
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
                Recover(state, 30);
                break;
            case (49, false):
                // The mandatory New Year lottery's five results, weighted by 148 fit windows.
                var draw = random.At(turn, PioneerDraw.FixedEvent).NextInt(148);
                if (draw < 27)
                {
                    ApplyProgressionReward(state, 10, 0);
                    Recover(state, 30);
                    ApplyMotivationGain(state, 2);
                }
                else if (draw < 72)
                {
                    ApplyProgressionReward(state, 5, 0);
                    Recover(state, 20);
                    ApplyMotivationGain(state, 1);
                }
                else if (draw < 132) Recover(state, 20);
                else
                {
                    var penalty = MotivationRules.Apply(header.Motivation, -1,
                        state.GetSection(PioneerStateSection.CharacterEffects).Contains(25), false);
                    header.Motivation = penalty.Value;
                    if (penalty.ConsumePositiveThinking) state.RemoveCharacterEffect(25);
                }
                break;
            case (51 or 72, true):
                // The observed below-threshold consolation is +20 Pt.
                if (header.Fans < (turn == 51 ? 60_000 : 120_000)) ApplyProgressionReward(state, 0, 20);
                else state.AddCharacterEffect(turn == 51 ? 25 : 10);
                break;
            case (55, true):
                if (header.Fans < 70_000 || header.DirectorBond < 60)
                {
                    ApplyProgressionReward(state, 0, 25);
                    Recover(state, -5);
                }
                else ApplyMotivationGain(state, 1);
                break;
        }

        static void Recover(PioneerState state, int gain)
        {
            ref var current = ref state.Mutable;
            current.Resources = current.Resources with
            {
                Vital = VitalRules.Apply(current.Resources.Vital, current.Limits.Vital, gain, 0).Vital,
            };
        }
    }
}
