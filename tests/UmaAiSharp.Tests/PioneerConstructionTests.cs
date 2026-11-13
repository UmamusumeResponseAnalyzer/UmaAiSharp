using System.Collections.Immutable;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;

namespace UmaAiSharp.Tests;

public sealed class PioneerConstructionTests
{
    static readonly string MasterHash = "4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886";

    static PioneerMasterData Data() => new(MasterHash,
        [new(101, 101), new(102, 105), new(106, 0)],
        [new(1, 101, 0, 0, 10100), new(2, 101, 1, 0, 10110), new(3, 101, 2, 0, 10120),
            new(4, 102, 0, 0, 10200), new(5, 102, 1, 0, 10210),
            new(6, 106, 0, 0, 10600), new(7, 106, 1, 0, 10610)],
        [new(1, 101, 10100, 10110, 100), new(2, 101, 10110, 10120, 100),
            new(3, 102, 10200, 10210, 100), new(4, 106, 10600, 10610, 200)],
        [new(1, 3, 1, 3, 12, 400)],
        [new(1, 3, 3, 12, 200)]);

    static PioneerConstructionPlan FirstPlan() => new(
    [
        new(2, 101, 10100, 10110),
        new(3, 102, 10200, 10210),
        new(1, 106, 10600, 10610),
    ]);

    [Fact]
    public void OrderUsesPriorityInsteadOfResponseArrayOrder()
    {
        var plan = FirstPlan();
        var analysis = new PioneerConstructionRules(Data()).Analyze(plan, 400);

        Assert.Equal([106, 101, 102], plan.Steps.Select(x => x.FacilityId));
        Assert.Equal([200, 300, 400], analysis.Steps.Select(x => x.CumulativeCost));
        Assert.Equal(400, analysis.TotalCost);
        Assert.Equal(plan.SemanticId, new PioneerConstructionPlan(plan.Steps.Reverse()).SemanticId);
    }

    [Fact]
    public void LowCostPlanIsNotRejectedForLeavingBudgetUnallocated()
    {
        var rules = new PioneerConstructionRules(Data());
        var plan = new PioneerConstructionPlan([new(1, 106, 10600, 10610)]);
        var analysis = rules.Analyze(plan, 400);
        var observation = new PioneerDevelopmentState(3, 35, 0, 400, 3,
            [200, 400], 0, [new(101, 10120), new(102, 10210), new(106, 10600)], new([]), []);

        Assert.Equal(200, analysis.UnallocatedCost);
        rules.ValidateForSubmission(observation, plan);
        Assert.Equal(plan.SemanticId, Assert.Single(rules.EnumeratePlans(observation)).SemanticId);
    }

    [Fact]
    public void UnknownEdgeIsNotSilentlyNormalizedOrCalledIllegal()
    {
        var plan = new PioneerConstructionPlan([new(1, 106, 0, 10610)]);
        Assert.Throws<UnknownMechanismException>(() => new PioneerConstructionRules(Data()).Analyze(plan, 400));
    }

    [Fact]
    public void RepeatedFacilityIsReportedWithoutInventingAProhibition()
    {
        var plan = new PioneerConstructionPlan([new(1, 101, 10100, 10110), new(2, 101, 10110, 10120)]);
        var analysis = new PioneerConstructionRules(Data()).Analyze(plan, 200);
        Assert.True(analysis.HasRepeatedFacility);
        Assert.Equal(200, analysis.TotalCost);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    public void AmbiguousPrioritiesCannotBecomeAnOrderedPlan(int first, int second)
        => Assert.Throws<InvalidDataException>(() => new PioneerConstructionPlan(
            [new(first, 101, 10100, 10110), new(second, 106, 10600, 10610)]));

    [Fact]
    public void ThresholdProjectionRecordsPendingCompletionWithoutMutatingObservedFacilities()
    {
        var state = new PioneerDevelopmentState(6, 5, 258, 400, 3,
            [200, 400], 0, [new(101, 10100), new(102, 10200), new(106, 10600)], FirstPlan(), [400011107]);
        var evidence = new EvidenceReference(new string('a', 64), 42);
        var report = ConstructionEvidence.CheckImmediateCompletion(new(Data()), state, evidence);

        var difference = Assert.Single(report.ImmediateCompletionDifferences);
        Assert.Equal(new(106, 10600, 10610), difference);
        Assert.Equal(10600, state.Facilities.Single(x => x.FacilityId == 106).StateId);
        Assert.Equal([400011107], report.PendingStoryIds!.Value.ToArray());
        Assert.Equal(evidence, report.Source);
    }

    [Fact]
    public void ObservedSettlementSeparatesCurrentPointsFromReachedTicketThreshold()
    {
        var plan = new PioneerConstructionPlan([new(1, 101, 10100, 10110), new(2, 106, 10600, 10610)]);
        var before = new PioneerDevelopmentState(15, 5, 311, 500, 3,
            [250, 500], 0, [new(101, 10100), new(106, 10600)], plan, [400011107]);
        var after = before with { Facilities = [new(101, 10110), new(106, 10600)], PendingStoryIds = [] };
        var result = ConstructionEvidence.CheckObservedSettlement(new(Data()), before, after,
            new(new string('a', 64), 100), new(new string('a', 64), 102));

        Assert.Equal(250, result.ReachedThreshold);
        Assert.Equal(new(106, 10600, 10610), Assert.Single(result.RawPointDifferences));
        Assert.Empty(result.ReachedThresholdDifferences);
        Assert.Equal(10100, before.Facilities[0].StateId);
    }

    [Fact]
    public void MissingEventQueueCannotBeInterpretedAsCompletedSettlement()
    {
        var before = new PioneerDevelopmentState(6, 5, 258, 400, 3,
            [200, 400], 0, [new(101, 10100), new(102, 10200), new(106, 10600)], FirstPlan(), [400011107]);
        var after = before with { PendingStoryIds = null };

        Assert.Throws<InvalidDataException>(() => ConstructionEvidence.CheckObservedSettlement(new(Data()), before, after,
            new(new string('a', 64), 100), new(new string('a', 64), 102)));
    }

    [Theory]
    [InlineData(15, 5, 400011107, 1)]
    [InlineData(41, 1, 400011108, 0)]
    public void FacilityComponentChangesFacilitiesWithoutClaimingToCompleteTheEvent(int turn, int phase, int story, int tickets)
    {
        var plan = new PioneerConstructionPlan([new(1, 101, 10100, 10110), new(2, 106, 10600, 10610)]);
        var before = new PioneerDevelopmentState(turn, phase, 311, 500, 3,
            [250, 500], tickets, [new(101, 10100), new(106, 10600)], plan, [story]);
        var rules = new PioneerConstructionRules(Data());
        var receipt = rules.ApplySettlementComponentsCandidate(before);
        var after = receipt.After;
        Assert.Equal(1, receipt.IssuedTickets);
        Assert.Equal(before.IslandTrainingTickets, receipt.DiscardedTickets);

        Assert.Equal([new ObservedFacility(101, 10110), new(106, 10600)], after.Facilities.ToArray());
        Assert.Equal(before.PendingStoryIds!.Value.ToArray(), after.PendingStoryIds!.Value.ToArray());
        Assert.Equal(before.DevelopmentPoints, after.DevelopmentPoints);
        Assert.Equal(before.Turn, after.Turn);
        Assert.Equal(1, after.IslandTrainingTickets);
        Assert.Equal(tickets, before.IslandTrainingTickets);
        Assert.Equal(10100, before.Facilities[0].StateId);
        Assert.Equal(after.Facilities.ToArray(), rules.ApplySettlementComponentsCandidate(after).After.Facilities.ToArray());
        Assert.Throws<UnknownMechanismException>(() => rules.ApplySettlementComponentsCandidate(before with { DeckId = 0 }));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplySettlementComponentsCandidate(before with { PendingStoryIds = [] }));
        Assert.Throws<UnknownMechanismException>(() => rules.ApplySettlementComponentsCandidate(before with { IslandTrainingTickets = null }));
    }

    [Fact]
    public void PartialObservationCannotInventMissingThresholds()
    {
        using var json = JsonDocument.Parse("""
            {"chara_info":{"turn":3,"playing_state":35},"pioneer_data_set":{
            "pioneer_info":{"pioneer_pt":0,"planning_cost":400,"deck_id":3},
            "planning_info_array":[],"facility_info_array":[],"shima_training_info":[]},"unchecked_event_array":[]}
            """);
        var error = Assert.Throws<InvalidDataException>(() => ConstructionEvidence.ReadObservation(json.RootElement,
            json.RootElement.GetProperty("pioneer_data_set")));
        Assert.Contains("gain_shima_training_pt_array", error.Message);
    }

    [Fact]
    public void MissingMasterColumnDoesNotBecomeZero()
    {
        var source = """{"table":"single_mode_11_facility_lv_up","row":{"id":1,"facility_id":101,"current_state_id":10100,"target_state_id":10110}}""";
        var error = Assert.Throws<InvalidDataException>(() => PioneerMasterData.ReadExport(new StringReader(source), MasterHash));
        Assert.Contains("第 1 行", error.Message);
    }

}
