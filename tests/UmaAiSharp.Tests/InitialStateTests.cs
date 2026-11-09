using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class InitialStateTests
{
    static readonly InitialFactor[] Factors =
    [new(101, 1, 1, 1), new(201, 2, 31, 1), new(301, 3, 900, 1), new(302, 3, 900, 3), new(401, 3, 901, 2)];
    static readonly InitialFactorRule[] Rules =
    [new(1, 1, 0, 5), new(1, 2, 0, 12), new(1, 3, 0, 21), new(1, 1, 1, 4), new(1, 2, 1, 9), new(1, 3, 1, 16),
        new(2, 1, 3, 1), new(2, 4, 6, 2), new(2, 7, 9, 3), new(2, 10, 999, 4),
        new(3, 1, 1, 0), new(3, 2, 2, 0), new(3, 3, 4, 0)];
    static InitialStateRules StateRules() => new(Factors, Rules,
        [new(900, 1, 41, 900001, 1), new(900, 4, 41, 900001, 3), new(900, 4, 61, 99, 0), new(901, 2, 41, 900002, 2)],
        [new(900001, 9000, 1), new(900002, 9001, 1)]);
    static InitialCharacter Character() => new(100, 1, new(10, 20, 30, 40, 50), new(1000, 1000, 1000, 1000, 1000),
        new[] { 11, 12, 21, 22, 23, 24, 31, 32, 33, 34 }.Select(group => new InitialAptitude(group, 1)).ToImmutableArray(),
        1, [new(900001, 1)]);
    static ImmutableArray<InheritanceSource> Sources() => Enum.GetValues<InheritanceSlot>()
        .Select(slot => new InheritanceSource(slot, slot == InheritanceSlot.Parent1 ? [101, 201, 302]
            : slot == InheritanceSlot.Parent2 ? [101, 201, 401] : [101, 201, 301], [])).ToImmutableArray();
    static ImmutableArray<InitialSupportCard> Deck() => Enumerable.Range(1, 6)
        .Select(index => new InitialSupportCard(index, index, index == 6 ? 2 : 1, index == 6 ? 0 : 101,
            [new(9, 2)], [])).ToImmutableArray();

    [Fact]
    public void CompositionKeepsInitialGreenHintsSeparateFromMidRunBundlesAndAncestors()
    {
        var result = StateRules().Compose(Character(), new(100, 200, 300, 400, 500), Sources(), Deck());
        Assert.Equal(new InitialAttributes(52, 20, 30, 40, 50), result.Attributes);
        Assert.Equal(new InitialAttributes(1124, 1200, 1300, 1400, 1500), result.Limits);
        Assert.Equal(3, result.Aptitudes.Single(value => value.GroupId == 31).Grade);
        Assert.True(result.ParentHints.SequenceEqual([new InheritedHint(9000, 1, 3), new InheritedHint(9001, 1, 2)]));
        Assert.True(result.Skills.SequenceEqual([new InitialSkill(900001, 1)]));
        Assert.Equal(6, result.SupportBonds.Length);
    }

    [Fact]
    public void MissingExtensionCanBeComparedAsListedComponentsButCannotInitialize()
    {
        var sources = Sources().SetItem(1, Sources()[1] with { ExtendedFactorIds = default });
        Assert.Throws<InvalidDataException>(() => StateRules().Compose(Character(), default, sources, Deck()));
        var listed = sources.Select(source => new ListedInheritanceSource(source.Slot, source.FactorIds)).ToImmutableArray();
        Assert.Equal(52, StateRules().ComposeListedFactors(Character(), default, listed, Deck()).Attributes.Speed);
        Assert.Throws<UnknownMechanismException>(() => StateRules().Compose(Character(), default,
            Sources().SetItem(0, Sources()[0] with { ExtendedFactorIds = [401] }), Deck()));
    }

    [Fact]
    public void DuplicateParentHintDestinationIsNotSilentlyMerged()
    {
        var sources = Sources().SetItem(3, Sources()[3] with { FactorIds = [101, 201, 301] });
        Assert.Throws<UnknownMechanismException>(() => StateRules().Compose(Character(), default, sources, Deck()));
    }

    [Fact]
    public void Type105CountsItsOwnSlotAndFriendGroupBranchesForEverySource()
    {
        var deck = Deck().SetItem(0, Deck()[0] with { UniqueEffects = [new(105, 10, 2, 0, 0, 0)] })
            .SetItem(4, Deck()[4] with { CardType = 3, TrainingCommandId = 0 });
        Assert.Equal(new InitialAttributes(56, 4, 4, 4, 4), InitialSupportRules.Calculate(deck).Attributes);
        var twice = deck.SetItem(1, deck[1] with { UniqueEffects = [new(105, 10, 2, 0, 0, 0)] });
        Assert.Equal(new InitialAttributes(100, 8, 8, 8, 8), InitialSupportRules.Calculate(twice).Attributes);
    }

    [Fact]
    public void UnknownInitialEffectsDoNotBecomeZero()
    {
        Assert.Throws<UnknownMechanismException>(() => InitialSupportRules.Calculate(
            Deck().SetItem(0, Deck()[0] with { Effects = [new(99, 1)] })));
        Assert.Throws<UnknownMechanismException>(() => InitialSupportRules.Calculate(
            Deck().SetItem(0, Deck()[0] with { UniqueEffects = [new(123, 1, 0, 0, 0, 0)] })));
    }

    [Fact]
    public void InitialBondsUseOwnPanelAndDirectUniqueWithoutBroadcasting()
    {
        var deck = Deck().SetItem(0, Deck()[0] with { Effects = [new(14, 25)], UniqueEffects = [new(14, 15, 0, 0, 0, 0)] });
        var bonds = InitialSupportRules.Calculate(deck).Bonds;
        Assert.Equal(40, bonds[0].Bond);
        Assert.All(bonds[1..], bond => Assert.Equal(0, bond.Bond));
        Assert.Throws<UnknownMechanismException>(() => InitialSupportRules.Calculate(
            deck.SetItem(0, deck[0] with { UniqueEffects = [new(115, 14, 5, 0, 0, 0)] })));
        Assert.Throws<UnknownMechanismException>(() => StateRules().Compose(Character() with { TalentLevel = 2 }, default, Sources(), deck));
        Assert.Throws<UnknownMechanismException>(() => StateRules().Compose(Character() with { Rarity = 4 }, default, Sources(), deck));
    }

    [Fact]
    public void RelationGroupsDeduplicateAliasesButRetainG1WhenCharacterTripleRepeats()
    {
        var rules = new InheritanceRelationRules([new(1, 2)], Enumerable.Range(1, 7).Select(id => new RelationMembership(id, 1)),
            [new(10, 100, true), new(11, 100, true), new(12, 200, false)]);
        var sources = Enum.GetValues<InheritanceSlot>().Select((slot, index) =>
            new RelationSource(slot, index + 2, [10, 11, 12])).ToImmutableArray();
        var result = rules.Calculate(1, sources);
        Assert.Equal(29, result.Total); // 14 character points and five common G1 edges of 3.
        var repeated = sources.SetItem(1, sources[1] with { CharacterId = 1 });
        var other = rules.Calculate(1, repeated);
        Assert.Equal(27, other.Total);
        Assert.Equal(new SourceRelation(InheritanceSlot.Parent1Parent1, 0, 3), other.Sources[1]);
        Assert.Throws<UnknownMechanismException>(() => rules.Calculate(1,
            sources.SetItem(0, sources[0] with { WonSaddleIds = [999] })));
    }
}
