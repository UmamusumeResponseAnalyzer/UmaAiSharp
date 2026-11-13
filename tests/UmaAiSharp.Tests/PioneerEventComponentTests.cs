using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;

namespace UmaAiSharp.Tests;

public sealed class PioneerEventComponentTests
{
    static string Evidence([CallerFilePath] string source = "") => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(source)!, "../../src/UmaAiSharp.Pioneer/Development/Evidence"));
    static T[] Read<T>(string name) => File.ReadLines(Path.Combine(Evidence(), name))
        .Select(line => JsonSerializer.Deserialize<T>(line, EvidenceJson.ReadOptions)!).ToArray();

    [Fact]
    public void TuckerResourcesAndFriendProgressMatchAll1496SelectedEvents()
    {
        var fixtures = Read<TuckerFriendFixture>("tucker-friend-observations.jsonl");
        Assert.Equal(1496, fixtures.Length);
        Assert.Equal(7, fixtures.Select(x => x.Context.StoryId).Distinct().Count());
        foreach (var row in fixtures)
        {
            var result = TuckerEventSettlement.ApplyObservedComponents(row.Context, row.Before);
            Assert.Equal(row.After, result.After);
            Assert.Equal(row.SkillPointGain, result.SkillPointGain);
        }
    }

    [Fact]
    public void OutingRecoveryUsesTheNewCapAndThirdOutingIsNotAlwaysFullRecovery()
    {
        var first = new TuckerFriendContext(809049011, 0, 30257, 4, 118185, true);
        var before = new TuckerFriendState(99, 100, 5, 94, true, 0);
        var result = TuckerEventSettlement.ApplyObservedComponents(first, before);
        Assert.Equal(104, result.After.MaxVital);
        Assert.Equal(104, result.After.Vital);
        Assert.Equal(5, result.ActualVitalGain);
        Assert.Equal(100, result.After.Bond);
        Assert.Equal(1, result.After.StoryStep);
        var third = TuckerEventSettlement.ApplyObservedComponents(first with { StoryId = 809049013, ChoiceNumber = 1 },
            before with { Vital = 10, MaxVital = 120, StoryStep = 2 });
        Assert.Equal(90, third.After.Vital);
        Assert.Equal(80, third.VitalGainBeforeCap);
        var declared = new DeclaredEventRewards(new([new(1, 11, 4, 0), new(1, 10, 32, 0)]));
        foreach (var maximum in new[] { 100, 116, 118, 120 })
            foreach (var vital in new[] { 0, maximum - 1, maximum })
            {
                var state = before with { Vital = vital, MaxVital = maximum };
                var outing = TuckerEventSettlement.ApplyObservedComponents(first, state);
                var common = declared.Apply(new(0, 0, 0, 0, 0, vital, 0), new(3000, 3000, 3000, 3000, 3000, maximum), 5, false, false);
                Assert.Equal(Math.Min(120, maximum + 4), outing.After.MaxVital);
                Assert.Equal(Math.Min(outing.After.MaxVital, vital + 32), outing.After.Vital);
                Assert.Equal(common.Resources.Vital, outing.After.Vital);
                Assert.Equal(common.Limits.Vital, outing.After.MaxVital);
                Assert.Equal(outing.After.Vital - vital, outing.ActualVitalGain);
            }
        Assert.Throws<InvalidDataException>(() => TuckerEventSettlement.ApplyObservedComponents(first, before with { MaxVital = 121 }));
    }

    [Fact]
    public void UnlockBelowSixtyBondDoesNotImplyItsOccurrenceProbability()
    {
        var context = new TuckerFriendContext(809049004, 1, 30257, 4, 118185, false);
        var before = new TuckerFriendState(30, 100, 2, 39, false, 0);
        var result = TuckerEventSettlement.ApplyObservedComponents(context, before);
        Assert.True(result.After.OutingUnlocked);
        Assert.Equal(44, result.After.Bond);
        Assert.Equal(13, result.SkillPointGain);
        Assert.Throws<UnknownMechanismException>(() => TuckerEventSettlement.ApplyObservedComponents(context with { ChoiceNumber = 2 }, before));
        Assert.Throws<UnknownMechanismException>(() => TuckerEventSettlement.ApplyObservedComponents(context with { SupportExperience = 82935 }, before));
        Assert.Throws<UnknownMechanismException>(() => TuckerEventSettlement.ApplyObservedComponents(context, before with { OutingUnlocked = true }));
    }

    [Fact]
    public void ExplicitMemberOutcomesGenerateTheirRecordsForAll660Windows()
    {
        var fixtures = Read<ProjectMemberFixture>("project-member-observations.jsonl");
        Assert.Equal(660, fixtures.Length);
        Assert.Equal(221, fixtures.Count(x => x.StoryId == 400011029));
        foreach (var row in fixtures)
        {
            var result = PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, row.ResultState, row.TrainingCharacter,
                row.Supports, row.Before, row.SelectedNpcIds);
            Assert.Equal(row.ExpectedMembers.OrderBy(x => x.TargetId), result.Members.OrderBy(x => x.TargetId));
            Assert.Equal(row.ExpectedNpcs.OrderBy(x => x.TargetId), result.AddedNpcs.OrderBy(x => x.TargetId));
            Assert.All(row.Before, member => Assert.Contains(member, result.Members));
        }
    }

    [Fact]
    public void MissingOrOverlappingNpcOutcomesAreNotSampledOrRepaired()
    {
        var row = Read<ProjectMemberFixture>("project-member-observations.jsonl").First(x => x.StoryId == 400011008);
        Assert.Throws<InvalidDataException>(() => PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, row.ResultState,
            row.TrainingCharacter, row.Supports, row.Before, []));
        Assert.Throws<InvalidDataException>(() => PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, row.ResultState,
            row.TrainingCharacter, row.Supports, row.Before, [row.TrainingCharacter, row.SelectedNpcIds[1], row.SelectedNpcIds[2]]));
        Assert.Throws<InvalidDataException>(() => PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, row.ResultState,
            row.TrainingCharacter, row.Supports, row.Before, [row.Before[0].TargetId, row.SelectedNpcIds[1], row.SelectedNpcIds[2]]));
        Assert.Throws<UnknownMechanismException>(() => PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, 1,
            row.TrainingCharacter, row.Supports, row.Before, row.SelectedNpcIds));
    }

    [Fact]
    public void AlreadyAppliedCheckpointDisplaysAreConfirmedOnceWithoutAnotherReward()
    {
        var fixtures = Read<LateCheckpointFixture>("late-checkpoint-confirmations.jsonl");
        Assert.Equal(220, fixtures.Length);
        Assert.Equal(9, fixtures.Count(x => x.MixedCheckpoint));
        foreach (var row in fixtures)
        {
            var before = LateCheckpointConfirmation.ObserveAppliedBoundary(row.Turn, row.Phase, row.ResultState,
                row.StoryId, row.PlayTiming, row.ChoiceCount);
            var after = LateCheckpointConfirmation.Confirm(before, row.StoryId, row.ChoiceNumber);
            Assert.True(after.Confirmed);
            Assert.False(before.Confirmed);
            Assert.All(row.ObservedDelta, value => Assert.Equal(0, value));
            Assert.True(row.TipsUnchanged && row.SkillsUnchanged && row.MembersUnchanged);
            Assert.Throws<IllegalActionException>(() => LateCheckpointConfirmation.Confirm(after, row.StoryId, row.ChoiceNumber));
        }
        Assert.Throws<UnknownMechanismException>(() => LateCheckpointConfirmation.ObserveAppliedBoundary(60, 5, 2, 501021407, 1, 0));
        Assert.Throws<UnknownMechanismException>(() => LateCheckpointConfirmation.ObserveAppliedBoundary(61, 1, 2, 999, 1, 0));
    }
}
