using System.Text.Json;
using UmaAiSharp.MCP;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Tests;

public sealed class TrainingConditionTests
{
    [Fact]
    public void RawConditionPanelsMatchDisplayedRatesAndPreserveConditionState()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/training-condition-panels.json")));
        var commands = 0;
        var panels = 0;
        var checkedConditions = new HashSet<int>();
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var start = row.GetProperty("start").Deserialize<PioneerStart.TrainingTurn>(PioneerExecution.JsonOptions);
            var data = start is null ? null : PioneerRuntimeData.Compile(start, [], "condition-fixture");
            var root = data?.CreateInitialState(); var scenario = data is null ? null : new PioneerScenario(data);
            foreach (var input in row.GetProperty("inputs").EnumerateArray())
            {
                var effects = input.GetProperty("effects").Deserialize<int[]>()!;
                var expected = input.GetProperty("expected").GetInt32();
                Assert.Equal(expected, TrainingFailureRate.Calculate(input.GetProperty("coefficient").GetInt32(),
                    input.GetProperty("vital").GetInt32(), input.GetProperty("reductions").Deserialize<int[]>()!,
                    TrainingFailureRate.StatusBias(effects)));
                if (scenario is not null)
                    Assert.Equal(expected, scenario.PreviewTraining(root!, input.GetProperty("command").GetInt32()).FailureRate);
                commands++;
            }
            if (start is null) continue;
            panels++;
            if (start.CharacterEffects.Where(effect => effect is 3 or 5 or 11 or 27).Count(checkedConditions.Add) == 0) continue;
            var command = start.Commands.First(value => value.Enabled).CommandId;
            var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
                [new(scenario!.Inspect(root!, 0).Decision.Id, PioneerRuntimeAction.Train(command))], 0);
            var execution = SimulationRunner.CreateExecution(scenario, script, root!, 192, 0);
            var result = SimulationRunner.Continue(scenario, script, ref execution);
            Assert.Equal(TrajectoryStatus.Stopped, result.Status);
            Assert.Equal(1, result.Progress.ConsumedActions);
            Assert.Equal(start.CharacterEffects.ToArray(), execution.State.GetSection(PioneerStateSection.CharacterEffects).ToArray());
        }
        Assert.Equal(1455, commands);
        Assert.Equal(267, panels);
        Assert.Equal(new[] { 3, 5, 11, 27 }, checkedConditions.Order());
    }
}
