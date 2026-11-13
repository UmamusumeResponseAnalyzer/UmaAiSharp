using UmaAiSharp.Pioneer.Game;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using UmaAiSharp.MCP;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;

namespace UmaAiSharp.Tests;

public sealed class PioneerEventCaptureTests
{
    [Fact]
    public void CurrentRewardQueryImportsAllChoicesWithoutReadingFutureOrRequiringOpeningHistory()
    {
        using var fixture = new Capture();
        fixture.Populate();
        var before = PioneerEventCapture.Read(fixture.Path, 14)!;
        Assert.Equal((10L, 11L, 14L, 5), (before.History.StatePacketId, before.History.RequestPacketId,
            before.History.RewardPacketId, before.History.CapturedPackets));
        fixture.Add(15, "request", "check_event", 15, new JsonObject { ["event_id"] = 9999 });
        fixture.Add(16, "response", "check_event", 15, new JsonObject { ["invalid_future"] = true });
        var after = PioneerEventCapture.Read(fixture.Path, 14)!;
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.Payload, after.Payload);
        Assert.True(JsonNode.DeepEquals(before.Rewards, after.Rewards));
        Assert.Null(PioneerEventCapture.Read(fixture.Path, 10));

        Assert.Equal("completed", PioneerImport.Run(fixture.Path, null, 14, fixture.Master, fixture.Output).Status);
        var inputPath = System.IO.Path.Combine(fixture.Output, "input.json");
        var input = JsonSerializer.Deserialize<PioneerExecutionInput>(File.ReadAllBytes(inputPath), PioneerExecution.JsonOptions)!;
        Assert.Equal("declaredEvent", input.Start.Kind);
        var start = input.Start.Data.Deserialize<PioneerStart.DeclaredEvent>(PioneerExecution.JsonOptions)!;
        var candidates = JsonSerializer.Deserialize<RootCandidate<PioneerRuntimeAction>[]>(
            File.ReadAllBytes(System.IO.Path.Combine(fixture.Output, "candidates.json")), PioneerExecution.JsonOptions)!;
        Assert.Equal(start.Choices.Length, candidates.Length);
        var data = PioneerRuntimeData.Compile(start, [], input.RulesIdentity);
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, candidates[0].Action)], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 77, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(1, result.Progress.ConsumedActions);
        Assert.Equal(0, result.Progress.SettledActions);
        Assert.Equal((int)PioneerStopCode.EventRewardRemainderRequired, result.Stop!.Value.Code);
        Assert.Equal(root.Header, data.CreateInitialState().Header);
    }

    [Theory]
    [InlineData("missingRequest")]
    [InlineData("requestAfterMutation")]
    [InlineData("eventMismatch")]
    [InlineData("multipleEvents")]
    public void UnclosedWindowsCannotUseAnEarlierEvent(string fault)
    {
        using var fixture = new Capture();
        fixture.Populate(fault);
        Assert.Throws<UnknownMechanismException>(() => PioneerEventCapture.Read(fixture.Path, 14));
    }

    [Theory]
    [InlineData("conflictingRequest")]
    [InlineData("conflictingResponse")]
    public void DuplicateIdentityRequiresTheSamePayload(string fault)
    {
        using var fixture = new Capture();
        fixture.Populate(fault);
        Assert.Throws<InvalidDataException>(() => PioneerEventCapture.Read(fixture.Path, 14));
    }

    [Fact]
    public void ChoiceRewardKeysAreNotConfusedWithVisibleChoiceNumbers()
    {
        using var fixture = new Capture();
        var options = fixture.State["data"]!["unchecked_event_array"]![0]!["event_contents_info"]!["choice_array"]!.AsArray();
        for (var i = 0; i < options.Count; i++) options[i]!["gain_select_id_index"] = 20 + i;
        var declarations = fixture.Rewards["data"]!["choice_reward_array"]!.AsArray();
        foreach (var reward in declarations) reward!["select_index"] = 19 + reward["select_index"]!.GetValue<int>();
        declarations.Add(declarations[0]!.DeepClone());
        fixture.Populate();
        var captured = PioneerEventCapture.Read(fixture.Path, 14)!;
        var common = PacketAudit.Decode(captured.Payload)["data"]!.AsObject();
        var start = PioneerEventCompiler.Compile(common["chara_info"]!.AsObject(), common["unchecked_event_array"]![0]!.AsObject(), captured.Rewards);
        Assert.Equal(Enumerable.Range(1, options.Count), start.Choices.Select(choice => choice.ChoiceNumber));
        Assert.Equal(2, start.Choices[0].Branches.Length);
        var data = PioneerRuntimeData.Compile(start, [], "captured-event");
        var root = data.CreateInitialState(); var scenario = new PioneerScenario(data);
        var script = new ActionScript<PioneerDecisionView, PioneerRuntimeAction>(
            [new(scenario.Inspect(root, 0).Decision.Id, PioneerRuntimeAction.Choose(start.Event, 1))], 0);
        var execution = SimulationRunner.CreateExecution(scenario, script, root, 55, 0);
        var result = SimulationRunner.Continue(scenario, script, ref execution);
        Assert.Equal(0, result.Progress.ConsumedActions);
        Assert.Equal((int)PioneerStopCode.EventRewardOutcomeRequired, result.Stop!.Value.Code);
    }

    internal sealed class Capture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UmaAiSharp-event-" + Guid.NewGuid().ToString("N") + ".sqlite");
        public string Master => Path + ".master";
        public string Output => Path + ".output";
        public JsonObject State { get; }
        public JsonObject Rewards { get; }
        readonly SqliteConnection db;

        public Capture()
        {
            var source = JsonNode.Parse(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "pioneer-event-resources.json")))!;
            var row = source["cases"]![0]!;
            var template = source["templates"]![row["template"]!.GetValue<int>()]!;
            var chara = row["before"]!.DeepClone().AsObject(); chara["state"] = 0; chara["scenario_id"] = 11;
            chara["support_card_array"] = new JsonArray(Enumerable.Range(1, 6).Select(position => (JsonNode)new JsonObject
                { ["position"] = position, ["support_card_id"] = position + 100 }).ToArray());
            chara["evaluation_info_array"] = new JsonArray(Enumerable.Range(1, 6).Select(position => (JsonNode)new JsonObject
                { ["target_id"] = position, ["training_partner_id"] = position, ["evaluation"] = 30, ["is_outing"] = 0 }).ToArray());
            State = new() { ["data"] = new JsonObject { ["chara_info"] = chara,
                ["unchecked_event_array"] = new JsonArray(template["event"]!.DeepClone()),
                ["pioneer_data_set"] = new JsonObject { ["evaluation_info_array"] = new JsonArray() } } };
            Rewards = new() { ["data"] = new JsonObject { ["choice_reward_array"] = template["rewards"]!.DeepClone() } };
            using (var master = new SqliteConnection($"Data Source={Master};Pooling=False"))
            {
                master.Open();
                using var schema = master.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE support_card_data(id INTEGER,chara_id INTEGER,support_card_type INTEGER);
                    CREATE TABLE single_mode_unique_chara(partner_id INTEGER,chara_id INTEGER,scenario_id INTEGER);
                    CREATE TABLE chara_data(id INTEGER);
                    """;
                schema.ExecuteNonQuery();
                foreach (var position in Enumerable.Range(1, 6))
                {
                    schema.CommandText = $"INSERT INTO support_card_data VALUES({position + 100},{position + 2000},1)";
                    schema.ExecuteNonQuery();
                }
            }
            db = new SqliteConnection($"Data Source={Path};Pooling=False"); db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "CREATE TABLE packets(id INTEGER PRIMARY KEY,direction TEXT,endpoint TEXT,request_id TEXT,viewer_id TEXT,payload BLOB,app_ver TEXT,res_ver TEXT)";
            query.ExecuteNonQuery();
        }

        public void Populate(string? fault = null)
        {
            var current = State["data"]!["unchecked_event_array"]![0]!;
            var request = new JsonObject { ["event_id"] = current["event_id"]!.GetValue<int>() };
            if (fault == "eventMismatch") request["event_id"] = -1;
            if (fault == "multipleEvents") State["data"]!["unchecked_event_array"]!.AsArray().Add(current.DeepClone());
            if (fault != "missingState") Add(10, "response", "check_event", 10, State);
            if (fault != "missingRequest") Add(11, "request", "get_choice_reward", 11, request);
            if (fault == "conflictingRequest") request["event_id"] = -1;
            if (fault is "requestAfterMutation" or "responseBeforeMutation" or "unrelatedResponse")
                Add(12, fault == "requestAfterMutation" ? "request" : "response", "check_event", 12,
                    fault == "responseBeforeMutation" ? State : new JsonObject { ["data"] = new JsonObject() });
            else if (fault != "missingRequest") Add(12, "request", "get_choice_reward", 11, request);
            Add(13, "response", "get_choice_reward", 11, Rewards);
            if (fault == "conflictingResponse") Rewards["different"] = true;
            Add(14, "response", "get_choice_reward", 11, Rewards);
        }

        public void Add(long id, string direction, string endpoint, int request, JsonObject payload)
        {
            using var query = db.CreateCommand();
            query.CommandText = "INSERT INTO packets VALUES($id,$direction,$endpoint,$request,'actor',$payload,'app','res')";
            query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$direction", direction);
            query.Parameters.AddWithValue("$endpoint", "/umamusume/single_mode_pioneer/" + endpoint);
            query.Parameters.AddWithValue("$request", request.ToString("x32"));
            query.Parameters.AddWithValue("$payload", MessagePackSerializer.ConvertFromJson(payload.ToJsonString()));
            query.ExecuteNonQuery();
        }

        public void Dispose()
        {
            db.Dispose(); File.Delete(Path); File.Delete(Master);
            if (!Directory.Exists(Output)) return;
            foreach (var name in new[] { "input.json", "candidates.json", "import.json" }) File.Delete(System.IO.Path.Combine(Output, name));
            Directory.Delete(Output);
        }
    }
}
