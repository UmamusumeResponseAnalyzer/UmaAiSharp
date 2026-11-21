using System.Collections.Immutable;
using UmaAiSharp.Base.Inheritance;

namespace UmaAiSharp.Pioneer.Inheritance;

public sealed record PioneerInheritanceContext(InheritanceParent First, InheritanceParent Second, bool HasUmaPlan,
    ImmutableArray<int> Aptitudes) : InheritanceContext(First, Second, HasUmaPlan, Aptitudes);
