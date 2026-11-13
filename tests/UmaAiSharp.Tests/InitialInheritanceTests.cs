using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Inheritance;

namespace UmaAiSharp.Tests;

public sealed class InitialInheritanceTests
{
    static InitialInheritanceRules Rules() => new(
        [new(101, 1, 1, 1), new(102, 1, 1, 2), new(103, 1, 1, 3), new(201, 1, 2, 1),
            new(3101, 2, 31, 1), new(3102, 2, 31, 2), new(3103, 2, 31, 3), new(1101, 2, 11, 1), new(9001, 5, 900, 1)],
        [new(1, 1, 0, 5), new(1, 2, 0, 12), new(1, 3, 0, 21), new(1, 1, 1, 4), new(1, 2, 1, 9), new(1, 3, 1, 16),
            new(2, 1, 3, 1), new(2, 4, 6, 2), new(2, 7, 9, 3), new(2, 10, 999, 4)]);
    static ImmutableArray<InitialAptitude> Aptitudes(int shortGrade = 1) => new[] { 11, 12, 21, 22, 23, 24, 31, 32, 33, 34 }
        .Select(group => new InitialAptitude(group, group == 31 ? shortGrade : 1)).ToImmutableArray();
    static ImmutableArray<InheritanceSource> Sources() => Enum.GetValues<InheritanceSlot>()
        .Select(slot => new InheritanceSource(slot, [101, 3101, 9001], [])).ToImmutableArray();

    [Fact]
    public void EachPedigreeSlotContributesEvenWhenFactorIdsRepeatBetweenAncestors()
    {
        var result = Rules().Evaluate(Sources(), Aptitudes());
        Assert.Equal(new BlueInheritanceContribution(1, 30, 24), result.Blue[0]);
        Assert.All(result.Blue[1..], value => Assert.Equal(0, value.AttributePoints + value.LimitPoints));
        var red = Assert.Single(result.Red, value => value.AptitudeGroupId == 31);
        Assert.Equal(new RedInheritanceContribution(31, 6, 2, 1, 3), red);
        Assert.Equal(6, result.UncalculatedFactors.Length);
        Assert.Equal(6, result.UncalculatedFactors.Select(value => value.Slot).Distinct().Count());
    }

    [Theory]
    [InlineData(3, 3101, 3, 1)]
    [InlineData(4, 3101, 4, 2)]
    [InlineData(3, 3103, 9, 3)]
    [InlineData(5, 3102, 10, 4)]
    public void RedStarsUseMasterIntervals(int shortSources, int shortFactor, int stars, int levels)
    {
        var sources = Sources().Select((source, index) => source with { FactorIds = [101, index < shortSources ? shortFactor : 1101] }).ToImmutableArray();
        var result = Rules().Evaluate(sources, Aptitudes());
        var red = Assert.Single(result.Red, value => value.AptitudeGroupId == 31);
        Assert.Equal(stars, red.TotalStars);
        Assert.Equal(levels, red.UncappedLevels);
        Assert.Equal(levels, red.AppliedLevels);
    }

    [Theory]
    [InlineData(6, 7, 1)]
    [InlineData(8, 8, 0)]
    public void InitialRedStopsAtAWithoutLoweringExistingHigherGrade(int before, int after, int actualIncrease)
    {
        var result = Rules().Evaluate(Sources(), Aptitudes(before));
        var red = Assert.Single(result.Red, value => value.AptitudeGroupId == 31);
        Assert.Equal(after, red.After);
        Assert.Equal(actualIncrease, red.AppliedLevels);
    }

    [Fact]
    public void BlueStarsUseIndependentAttributeAndLimitComponents()
    {
        var sources = Sources().Select((source, index) => source with { FactorIds = [101 + index % 3, 3101] }).ToImmutableArray();
        var result = Rules().Evaluate(sources, Aptitudes());
        Assert.Equal(new BlueInheritanceContribution(1, 76, 58), result.Blue[0]);
    }

    [Fact]
    public void MissingSourcesUnknownExtensionsAndMalformedFactorListsAreNotDefaulted()
    {
        var rules = Rules();
        Assert.Throws<InvalidDataException>(() => rules.Evaluate(Sources()[..5], Aptitudes()));
        Assert.Throws<InvalidDataException>(() => rules.Evaluate(Sources().SetItem(1, Sources()[0]), Aptitudes()));
        Assert.Throws<InvalidDataException>(() => rules.Evaluate(Sources(), Aptitudes()[..9]));
        Assert.Throws<UnknownMechanismException>(() => rules.Evaluate(Sources().SetItem(0, Sources()[0] with { ExtendedFactorIds = [103] }), Aptitudes()));
        Assert.Throws<UnknownMechanismException>(() => rules.Evaluate(Sources().SetItem(0, Sources()[0] with { FactorIds = [101] }), Aptitudes()));
        Assert.Throws<UnknownMechanismException>(() => rules.Evaluate(Sources().SetItem(0, Sources()[0] with { FactorIds = [9999, 3101] }), Aptitudes()));
        Assert.Throws<InvalidDataException>(() => rules.Evaluate(Sources().SetItem(0, Sources()[0] with { FactorIds = [101, 101, 3101] }), Aptitudes()));
        Assert.Throws<InvalidDataException>(() => rules.Evaluate(Sources().SetItem(0, Sources()[0] with { ExtendedFactorIds = default }), Aptitudes()));
    }
}
