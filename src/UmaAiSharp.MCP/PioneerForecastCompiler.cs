using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using static UmaAiSharp.Pioneer.Game.PioneerGameData;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;
using static UmaAiSharp.Pioneer.Offline.PioneerMasterReader;

namespace UmaAiSharp.MCP;

public static class PioneerForecastCompiler
{
    sealed class Run
    {
        public JsonObject? Common, Pioneer;
        public Dictionary<string, int> Values = [];
        public HashSet<int> History = [];
        public List<Row> Rows = [];
        public HashSet<string> Exchanges = [];
        public bool Started, Finished, Invalid;
        public string Token = "", Configuration = "";
    }
    sealed record Row(string Run, string Configuration, ForecastNode From, ForecastAction Action,
        Dictionary<string, int> Before, int[] History, Dictionary<string, int> Delta,
        int Story, int Template, bool Success, string Source, long PacketId, PioneerStart.IslandTraining? Island);

    public static ToolResult Build(string evidenceDirectory, string eventsPath, string masterPath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidDataException("模型输出目录必须为空。");
        var directory = Path.GetFullPath(evidenceDirectory);
        var paired = File.ReadLines(Path.Combine(directory, "exchanges.jsonl")).Select(line => JsonNode.Parse(line)!.AsObject())
            .Where(row => row["status"]!.GetValue<string>() == "Paired")
            .Select(row => row["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var actions = File.ReadLines(Path.Combine(directory, "pioneer-actions.jsonl")).Select(line => JsonNode.Parse(line)!.AsObject())
            .Where(row => paired.Contains(row["exchangeKey"]!.GetValue<string>()))
            .GroupBy(row => row["exchangeKey"]!.GetValue<string>()).ToDictionary(group => group.Key, group => group.First()["fields"]!.AsObject());
        var compiler = new PioneerMasterCompiler(PioneerMasterReader.ReadTraining(masterPath));
        var facilities = new PioneerFacilityCompiler(PioneerMasterReader.ReadFacilities(masterPath));
        var runs = new Dictionary<string, Run>();
        var templates = new List<PioneerStart.TrainingTurn>();
        var issues = new List<object>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "pioneer-observations.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonNode.Parse(line)!.AsObject();
            if (observation["runKey"] is not JsonValue runValue) continue;
            var runKey = runValue.GetValue<string>();
            if (!runs.TryGetValue(runKey, out var run)) runs.Add(runKey, run = new());
            var route = observation["endpoint"]!.GetValue<string>();
            var endpoint = route[(route.LastIndexOf('/') + 1)..];
            var visible = observation["visible"]!.AsObject();
            var common = PioneerNormalizer.GetCommon(visible, route);
            var pioneer = visible["pioneer_data_set"] as JsonObject;
            var afterChara = common?["chara_info"] as JsonObject;
            var packet = observation["packetId"]!.GetValue<long>();
            var source = observation["source"]!.GetValue<string>();
            var exchange = observation["exchangeKey"]!.GetValue<string>();
            if (!run.Exchanges.Add(exchange)) continue;
            if (endpoint == "start") run.Started = afterChara is not null && Int(afterChara, "turn") == 1;
            if (endpoint == "finish") run.Finished = true;
            var before = run.Common;
            var oldValues = run.Values;
            var nextValues = afterChara is null ? oldValues : ReadValues(afterChara, pioneer, oldValues);
            if (before is not null && !IsAction(endpoint) && endpoint != "finish"
                && nextValues.Any(pair => oldValues.TryGetValue(pair.Key, out var old) && old != pair.Value))
            {
                run.Invalid = true;
                issues.Add(new { source, packet, detail = $"非行动端点{endpoint}出现状态变化，不能跨过缺失结算编译转移。" });
            }
            if (before?["chara_info"] is JsonObject chara && IsAction(endpoint))
            {
                if (!actions.TryGetValue(exchange, out var fields))
                {
                    run.Invalid = true;
                    issues.Add(new { source, packet, detail = "行动缺少唯一配对请求。" });
                }
                else if (!run.Invalid)
                {
                    try
                    {
                        var request = RequestBody(fields);
                        var (action, story) = Action(endpoint, request, before);
                        if (action.Plan is { } plan)
                            action = action with { Plan = new(plan.Steps.Select(step => step.PreviousStateId == 0
                                ? step with { PreviousStateId = oldValues[$"facility:{step.FacilityId}"] } : step)) };
                        var kind = action.Kind is ForecastActionKind.Training or ForecastActionKind.Rest or ForecastActionKind.Outing or ForecastActionKind.Treatment
                            || endpoint == "race_entry" || endpoint == "shima_training_exec" ? ForecastActionKind.Home : action.Kind;
                        var node = new ForecastNode(Int(chara, "turn"), kind,
                            kind == ForecastActionKind.Event ? story : kind == ForecastActionKind.Race ? action.Id : 0);
                        var template = -1;
                        PioneerStart.IslandTraining? island = null;
                        var success = true;
                        if (action.Kind == ForecastActionKind.Training)
                        {
                            var frame = new JsonObject { ["c"] = chara.DeepClone(), ["s"] = run.Pioneer!.DeepClone(),
                                ["home"] = before["home_info"]!.DeepClone(), ["events"] = before["unchecked_event_array"]!.DeepClone() };
                            var training = compiler.TrainingStart(frame, false, null, facilities);
                            template = templates.Count;
                            templates.Add(training);
                            success = Int(common!["command_result"]!.AsObject(), "result_state") == 2;
                        }
                        if (action.Kind == ForecastActionKind.Island)
                        {
                            var frame = new JsonObject { ["c"] = chara.DeepClone(), ["s"] = run.Pioneer!.DeepClone(),
                                ["home"] = before["home_info"]!.DeepClone(), ["events"] = before["unchecked_event_array"]!.DeepClone() };
                            island = compiler.IslandStart(frame, facilities, false, null);
                        }
                        var delta = new Dictionary<string, int>();
                        foreach (var (field, value) in nextValues)
                        {
                            var prior = oldValues.GetValueOrDefault(field);
                            if (prior != value) delta.Add(field, checked(value - prior));
                        }
                        run.Rows.Add(new(run.Token, run.Configuration, node, action, oldValues,
                            [.. run.History.Order()], delta, story, template, success, source, packet, island));
                        if (story != 0) run.History.Add(story);
                    }
                    catch (Exception error) when (error is UnknownMechanismException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
                    {
                        run.Invalid = true;
                        issues.Add(new { source, packet, detail = error.Message });
                    }
                }
            }
            if (afterChara is not null)
            {
                run.Common = common; run.Values = nextValues;
                if (pioneer is not null) run.Pioneer = pioneer;
                if (run.Token.Length == 0) { run.Token = RunToken(afterChara); run.Configuration = Configuration(afterChara); }
            }
        }
        var accepted = runs.Values.Where(run => run.Started && run.Finished && !run.Invalid && run.Rows.Count != 0
            && run.Rows[^1].From.Turn == 78 && run.Rows[^1].Action is { Kind: ForecastActionKind.Event, Choice: 0 }
            && PioneerEvent.IsCharacterEndingStory(run.Rows[^1].From.StoryId)).ToArray();
        if (accepted.Length < 2) throw new InvalidDataException($"可用于条件预测的完整育成只有{accepted.Length}局；首批排除原因：{JsonSerializer.Serialize(issues.Take(5))}");
        var names = PioneerForecastScenario.BasicFields.Concat(accepted.SelectMany(run => run.Rows)
            .SelectMany(row => row.Before.Keys.Concat(row.Delta.Keys)).Except(PioneerForecastScenario.BasicFields).Distinct().Order()).ToImmutableArray();
        var indices = names.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index);
        var stories = accepted.SelectMany(run => run.History).Distinct().Order().ToImmutableArray();
        var storyIndices = stories.Select((story, index) => (story, index)).ToDictionary(x => x.story, x => x.index);
        var transitions = ImmutableArray.CreateBuilder<ForecastTransition>();
        var usedTemplates = accepted.SelectMany(run => run.Rows).Where(row => row.Template >= 0).Select(row => row.Template).Distinct().Order().ToArray();
        var templateIndices = usedTemplates.Select((old, index) => (old, index)).ToDictionary(x => x.old, x => x.index);
        foreach (var run in accepted)
            for (var i = 0; i < run.Rows.Count; i++)
            {
                var row = run.Rows[i];
                transitions.Add(new(row.Run, row.Configuration, row.From, i + 1 == run.Rows.Count ? null : run.Rows[i + 1].From,
                    row.Action, new([.. names.Select(name => FieldValue(row.Before, name))], [.. row.History.Select(story => storyIndices[story])]),
                    [.. row.Delta.Select(value => new ForecastChange(indices[value.Key], IsCategory(value.Key)
                        ? checked(row.Before.GetValueOrDefault(value.Key) + value.Value) : value.Value, IsCategory(value.Key))).OrderBy(value => value.Field)],
                    row.Story, row.Template < 0 ? -1 : templateIndices[row.Template], row.Success, row.Source, row.PacketId, row.Island));
            }
        var sourceHashes = new[] { "pioneer-observations.jsonl", "pioneer-actions.jsonl", "exchanges.jsonl" }
            .ToDictionary(name => name, name => HashFile(Path.Combine(directory, name)));
        var sourceHash = PioneerNormalizer.Hash(string.Join(';', sourceHashes.Select(pair => pair.Key + ":" + pair.Value)));
        var coefficients = new[] { 101, 105, 102, 103, 106 }.SelectMany(command => Enumerable.Range(1, 5).Select(level => compiler.Trainings[(command, level)].FailureRate))
            .Concat(Enumerable.Range(3601, 5).Select(command => compiler.Trainings[(command, 5)].FailureRate)).ToImmutableArray();
        using var master = EvidenceAudit.OpenReadOnly(masterPath);
        var facilityStates = Rows(master, "single_mode_11_facility_state");
        var facilityEffects = Rows(master, "single_mode_11_facility_effect").GroupBy(row => row["state_id"])
            .ToDictionary(group => group.Key, group => group.Select(row => new UmaAiSharp.Pioneer.Training.TrainingFacilityEffect(
                row["effect_type"], row["effect_value_1"], row["effect_value_2"], row["effect_value_3"])).ToImmutableArray());
        var racePrograms = ImmutableArray.CreateBuilder<ForecastRaceProgram>();
        using (var query = master.CreateCommand())
        {
            query.CommandText = "SELECT p.id,r.grade,p.reward_set_id,c.ground,c.distance,p.need_fan_count FROM single_mode_program p JOIN race_instance i ON i.id=p.race_instance_id JOIN race r ON r.id=i.race_id JOIN race_course_set c ON c.id=r.course_set ORDER BY p.id";
            using var reader = query.ExecuteReader();
            while (reader.Read()) racePrograms.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
        }
        var model = new ForecastModelInput(3, HashFile(masterPath), sourceHash, names, stories, coefficients,
            [.. usedTemplates.Select(index => templates[index])], transitions.ToImmutable(), [],
            [.. facilityStates.Select(row => new UmaAiSharp.Pioneer.Training.TrainingFacility(row["facility_id"], row["state_id"], row["facility_lv"],
                facilityEffects.GetValueOrDefault(row["state_id"], [])))],
            [.. Rows(master, "single_mode_11_shima_effect").Where(row => row["result_state"] == 2).Select(row => new ForecastFacilityBase(
                row["command_id"], row["required_facility_id"], row["target_type"], row["effect_value"]))],
            ConstructionData(master, HashFile(masterPath)),
            [.. Rows(master, "single_mode_11_pioneer_pt_bonus").Select(row => new UmaAiSharp.Pioneer.Development.CheckpointPointBonus(
                row["id"], row["check_point_type"], row["group_id"], row["pt_rate"], row["add_value_max"]))], racePrograms.ToImmutable());
        var eventCalibration = PioneerEventModelCompiler.Compile(eventsPath, model, cancellationToken);
        model = model with { EventRewards = eventCalibration.Bindings };
        Directory.CreateDirectory(output);
        using (var file = new FileStream(Path.Combine(output, "model.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, model, new JsonSerializerOptions(PioneerExecution.JsonOptions) { WriteIndented = false });
        using (var file = new FileStream(Path.Combine(output, "build.json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, new { version = PioneerForecastModel.Version, runs = accepted.Length, transitions = model.Transitions.Length,
                fields = names.Length, stories = stories.Length, configurations = accepted.Select(run => run.Configuration).Distinct().Count(),
                excludedRuns = runs.Count - accepted.Length, issues, sourceHashes, model.MasterSha256, model.EvidenceSha256,
                eventCalibration.Counts, eventsSha256 = HashFile(eventsPath),
                modelSha256 = HashFile(Path.Combine(output, "model.json")),
                coverage = accepted.GroupBy(run => run.Configuration).Select(group => new { configuration = group.Key, runs = group.Count(),
                    transitions = group.Sum(run => run.Rows.Count), sources = group.Select(run => run.Rows[0].Source).Distinct().ToArray() }) }, PioneerExecution.JsonOptions);
        return new("completed", new { runs = accepted.Length, transitions = model.Transitions.Length },
            ToolResult.Paths(output, ("model", "model.json"), ("manifest", "build.json")));
    }

    internal static bool IsAction(string endpoint) => endpoint is "exec_command" or "check_event" or "race_entry" or "race_start"
        or "race_end" or "race_out" or "shima_training_exec" or "planning" or "check_point" or "pioneer_live" or "pioneer_complete";

    internal static JsonObject RequestBody(JsonObject fields)
        => fields.FirstOrDefault(pair => pair.Key.EndsWith("_request_common", StringComparison.Ordinal)).Value as JsonObject ?? fields;

    static (ForecastAction Action, int Story) Action(string endpoint, JsonObject request, JsonObject before)
    {
        if (endpoint == "check_event")
        {
            var id = Int(request, "event_id");
            var current = Objects(Array(before, "unchecked_event_array"), "unchecked_event_array").SingleOrDefault(row =>
                Int(row, "event_id") == id && Int(row, "chara_id") == Int(request, "chara_id"))
                ?? throw new InvalidDataException($"事件{id}未出现在此前可见队列中。");
            return (new(ForecastActionKind.Event, id, Int(request, "choice_number")), Int(current, "story_id"));
        }
        if (endpoint == "exec_command")
        {
            var id = Int(request, "command_id");
            var kind = (id, Int(request, "command_type")) switch
            {
                (101 or 105 or 102 or 103 or 106 or >= 3601 and <= 3605, 1) => ForecastActionKind.Training,
                (701, 7) or (3606, 3) => ForecastActionKind.Rest,
                (0, 3) => ForecastActionKind.Outing,
                (801, 8) => ForecastActionKind.Treatment,
                _ => throw new InvalidDataException($"尚未识别行动{id}的类型{Int(request, "command_type")}。")
            };
            return (new(kind, id, request["select_id"]?.GetValue<int>() ?? 0), 0);
        }
        return (endpoint switch
        {
            "race_entry" => new(ForecastActionKind.Race, 1, Int(request, "program_id")), "race_start" => new(ForecastActionKind.Race, 2),
            "race_end" => new(ForecastActionKind.Race, 3), "race_out" => new(ForecastActionKind.Race, 4),
            "shima_training_exec" => new(ForecastActionKind.Island, 3101), "planning" => new(ForecastActionKind.Planning, 0, Plan:
                new(Objects(Array(request, "planning_info_array"), "planning_info_array").Select(row => new UmaAiSharp.Pioneer.PlannedUpgrade(
                    Int(row, "priority"), Int(row, "facility_id"), Int(row, "prev_state_id"), Int(row, "target_state_id"))))),
            "check_point" => new(ForecastActionKind.Checkpoint, 0), "pioneer_live" => new(ForecastActionKind.Live, 0),
            "pioneer_complete" => new(ForecastActionKind.Complete, 0),
            _ => throw new InvalidDataException($"未知动作端点{endpoint}。")
        }, 0);
    }

}
