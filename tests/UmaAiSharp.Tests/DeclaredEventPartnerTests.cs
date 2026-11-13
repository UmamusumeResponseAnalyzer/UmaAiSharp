using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class DeclaredEventPartnerTests
{
    [Fact]
    public void DeclaredPartnersMatchRealBondsOutingFlagsAndOffDeckNoops()
    {
        var fixture = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-event-partners.json")))!;
        var rows = fixture["cases"]!.AsArray();
        Assert.Equal(8, rows.Count);
        foreach (var row in rows)
        {
            var partners = row!["partners"].Deserialize<ImmutableArray<EventPartner>>(PioneerExecution.JsonOptions);
            var start = PioneerEventCompiler.Compile(row["before"]!.AsObject(), row["event"]!.AsObject(), row["rewards"]!.AsArray(), partners);
            var data = PioneerRuntimeData.Compile(start, [], "partner-fixture");
            var scenario = new PioneerScenario(data); var root = data.CreateInitialState();
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, row["choice"]!.GetValue<int>()))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root, 53, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(0, result.Progress.SettledActions);
            Assert.True(execution.State.Header.KnownParts.HasFlag(PioneerKnownParts.PartnerEvaluations));
            var expected = row["afterPartners"]!.AsArray().SelectMany(partner => new[] {
                partner!["bond"]!.GetValue<int>(), partner["outingUnlocked"]!.GetValue<bool>() ? 1 : 0 }).ToArray();
            Assert.Equal(expected, execution.State.GetSection(PioneerStateSection.PartnerEvaluations).ToArray());
            Assert.Equal(root.GetSection(PioneerStateSection.PartnerEvaluations).ToArray(), data.CreateInitialState().GetSection(PioneerStateSection.PartnerEvaluations).ToArray());
            Assert.Equal(result, SimulationRunner.Continue(scenario, script, ref execution));
        }
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(int.MaxValue, 100)]
    public void DeclaredBondAlreadyIncludesConditionBonusesAndCapsWithoutOverflow(int gain, int expected)
    {
        var start = Start([new(4, 2001, gain, 0), new(4, 9002, gain, 0), new(5, 3000, gain, 0),
            new(5, 9999, gain, 0), new(11, 2006, 0, 0)]);
        var data = PioneerRuntimeData.Compile(start, [], "declared-bond");
        var scenario = new PioneerScenario(data); var state = data.CreateInitialState();
        var random = new SimulationRandom(59);
        var beforeRandom = random.State;
        var result = scenario.ApplyAction(ref state, PioneerRuntimeAction.Choose(start.Event, 1), ref random);
        Assert.True(result.ActionConsumed); Assert.False(result.ActionSettled);
        var evaluations = state.GetSection(PioneerStateSection.PartnerEvaluations);
        Assert.Equal(expected, evaluations[0]); Assert.Equal(expected, evaluations[12]);
        Assert.Equal(0, evaluations[14]); // The same character is present as a project NPC, not an equipped support.
        Assert.Equal(1, evaluations[11]);
        Assert.Equal(0, state.Header.EventRewardRemainingEffects);
        Assert.Equal(beforeRandom, random.State);
    }

    [Fact]
    public void MissingRosterKeepsUnknownEffectsAndUnsupportedResourcesCommitNothing()
    {
        var start = Start([new(4, 2001, 5, 0), new(11, 2006, 0, 0)]) with { Partners = null };
        var data = PioneerRuntimeData.Compile(start, [], "unknown-partners");
        var scenario = new PioneerScenario(data); var state = data.CreateInitialState();
        var random = new SimulationRandom(59);
        Assert.False(scenario.ApplyAction(ref state, PioneerRuntimeAction.Choose(start.Event, 1), ref random).ActionConsumed);
        Assert.False(state.Header.KnownParts.HasFlag(PioneerKnownParts.PartnerEvaluations));

        start = Start([new(4, 2001, 5, 0), new(11, 2006, 0, 0), new(1, 10, 5, 0), new(2, 10, 5, 0)]);
        data = PioneerRuntimeData.Compile(start, [], "unknown-resource-order");
        scenario = new(data); state = data.CreateInitialState();
        var before = state.Clone();
        var result = scenario.ApplyAction(ref state, PioneerRuntimeAction.Choose(start.Event, 1), ref random);
        Assert.False(result.ActionConsumed);
        Assert.Equal(before.Header, state.Header);
        Assert.Equal(before.GetSection(PioneerStateSection.PartnerEvaluations).ToArray(), state.GetSection(PioneerStateSection.PartnerEvaluations).ToArray());
    }

    static PioneerStart.DeclaredEvent Start(ImmutableArray<DeclaredEventEffect> effects) => new(32, 5,
        new(10000, 1011, 501011101, 1), new(100, 100, 100, 100, 100, 60, 100), new(2000, 2000, 2000, 2000, 2000, 100),
        3, [8, 9], [new(1, [new(effects)])], [.. Enumerable.Range(1, 6).Select(position => new EventPartner(position, 2000 + position, 0, true, false)),
            new(102, 9002, 0, false, false), new(3000, 3000, 0, false, false)]);
}
