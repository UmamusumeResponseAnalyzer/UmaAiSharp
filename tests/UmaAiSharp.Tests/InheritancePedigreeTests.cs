using System.Collections.Immutable;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Inheritance;

namespace UmaAiSharp.Tests;

public sealed class InheritancePedigreeTests
{
    static InheritancePedigree Rules() => new([new(101, 1, 1, 1), new(103, 1, 1, 3), new(201, 2, 31, 1),
        new(203, 2, 31, 3), new(503, 4, 50, 3)]);
    static ParentFactorSnapshot Parent() => new([new(1, [101, 201]), new(10, [101, 201]), new(20, [101, 201]),
        new(11, [101, 201]), new(12, [101, 201]), new(21, [101, 201]), new(22, [101, 201])], []);

    [Theory]
    [InlineData(InheritanceSlot.Parent1)]
    [InlineData(InheritanceSlot.Parent2)]
    public void ExplicitTopLevelEmptyArrayCoversThreeRuntimeSources(InheritanceSlot parentSlot)
    {
        var sources = Rules().MapParent(Parent(), parentSlot);
        Assert.True(sources.Select(source => (int)source.Slot).SequenceEqual(Enumerable.Range((int)parentSlot, 3)));
        Assert.All(sources, source => Assert.True(source.ExtendedFactorIds.IsEmpty && !source.ExtendedFactorIds.IsDefault));
    }

    [Fact]
    public void FlatPositionsMapOnlyTheSelectedParentsThreeGenerationsWithoutChangingBaseFactors()
    {
        var parent = Parent() with { FlatExtensions = [new(1, 101, 103), new(10, 201, 203), new(20, 0, 503), new(11, 101, 103)] };
        var sources = Rules().MapParent(parent, InheritanceSlot.Parent2);
        Assert.True(sources[0].ExtendedFactorIds.SequenceEqual([103]));
        Assert.True(sources[1].ExtendedFactorIds.SequenceEqual([203]));
        Assert.True(sources[2].ExtendedFactorIds.SequenceEqual([503]));
        Assert.All(sources, source => Assert.True(source.FactorIds.SequenceEqual([101, 201])));
        // Mapping is structural: nonempty extension effects are not accepted by the initialization calculator.
        var initializer = new InitialInheritanceRules([new(101, 1, 1, 1), new(201, 2, 31, 1)],
            [new(1, 1, 0, 5), new(1, 2, 0, 12), new(1, 3, 0, 21), new(1, 1, 1, 4), new(1, 2, 1, 9), new(1, 3, 1, 16),
                new(2, 1, 3, 1), new(2, 4, 6, 2), new(2, 7, 9, 3), new(2, 10, 999, 4)]);
        var six = Rules().MapParent(Parent(), InheritanceSlot.Parent1).AddRange(sources);
        var aptitudes = new[] { 11, 12, 21, 22, 23, 24, 31, 32, 33, 34 }.Select(group => new InitialAptitude(group, 1)).ToImmutableArray();
        Assert.Throws<UnknownMechanismException>(() => initializer.Evaluate(six, aptitudes));
    }

    [Fact]
    public void MissingTopLevelOrConflictingReplacementDoesNotBecomeEmptyOrLastWriteWins()
    {
        Assert.Throws<InvalidDataException>(() => Rules().MapParent(Parent() with { FlatExtensions = default }, InheritanceSlot.Parent1));
        Assert.Throws<InvalidDataException>(() => Rules().MapParent(Parent() with { FlatExtensions = [new(10, 101, 103), new(10, 101, 103)] }, InheritanceSlot.Parent1));
        Assert.Throws<InvalidDataException>(() => Rules().MapParent(Parent() with { FlatExtensions = [new(10, 201, 103)] }, InheritanceSlot.Parent1));
        Assert.Throws<InvalidDataException>(() => Rules().MapParent(Parent() with { FlatExtensions = [new(10, 103, 101)] }, InheritanceSlot.Parent1));
        Assert.Throws<UnknownMechanismException>(() => Rules().MapParent(Parent() with { FlatExtensions = [new(10, 0, 999)] }, InheritanceSlot.Parent1));
    }
}
