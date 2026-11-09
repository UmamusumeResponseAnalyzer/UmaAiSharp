using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Events;
using static UmaAiSharp.Cli.Evidence.PioneerMasterCompiler;

namespace UmaAiSharp.Cli;

public static class PioneerImportCommand
{
    public static int Run(Dictionary<string, string> values)
    {
        var fromCapture = values.ContainsKey("--packets");
        string[] required = fromCapture ? ["--packets", "--packet-id", "--master", "--output"]
            : ["--packet", "--endpoint", "--master", "--output"];
        if (values.Count != required.Length || required.Any(key => !values.ContainsKey(key)))
            throw new InvalidDataException("回合导入需要--master、--output，以及--packet/--endpoint或--packets/--packet-id；output为新目录。");
        var output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("回合导入输出目录非空，拒绝覆盖。");
        CapturedPioneerTraining? captured = null;
        CapturedPioneerEvent? revealed = null;
        if (fromCapture)
        {
            if (!long.TryParse(values["--packet-id"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidDataException("--packet-id必须是正整数响应编号。");
            try
            {
                revealed = PioneerEventCapture.Read(values["--packets"], id);
                if (revealed is null) captured = PioneerTrainingHistory.Read(values["--packets"], id);
            }
            catch (UnknownMechanismException error)
            {
                return PioneerExecutionCommand.WriteInitializationStop(values["--packets"], Path.Combine(output, "import.json"), error);
            }
        }
        var endpoint = revealed?.Endpoint ?? captured?.Endpoint ?? values["--endpoint"];
        if (!endpoint.StartsWith("/single_mode/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/single_mode_pioneer/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/umamusume/single_mode/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/umamusume/single_mode_pioneer/", StringComparison.Ordinal))
            throw new InvalidDataException("--endpoint要求实际育成响应路径，如/single_mode/load；不接受完整URL或请求端点推测。");
        var packet = revealed?.Payload ?? captured?.Payload ?? File.ReadAllBytes(values["--packet"]);
        JsonObject response;
        try { response = PacketAudit.Decode(packet); }
        catch (MessagePackSerializationException error) { throw new InvalidDataException("回合响应无法解码为MessagePack对象。", error); }
        var responseData = response["data"] as JsonObject ?? throw new InvalidDataException("响应缺少data对象。");
        JsonObject common;
        try { common = PioneerNormalizer.GetCommon(responseData, endpoint) ?? throw new InvalidDataException("响应缺少当前端点的育成common。"); }
        catch (InvalidOperationException error) { throw new InvalidDataException("响应的育成common必须是对象。", error); }
        var chara = common["chara_info"] as JsonObject ?? throw new InvalidDataException("响应缺少chara_info。");
        if (Int(chara, "scenario_id") != 11) throw new InvalidDataException("回合导入要求Pioneer剧本scenario_id=11。");
        var events = Objects(Array(common, "unchecked_event_array"), "unchecked_event_array").ToArray();
        var masterHash = PioneerExecutionCommand.Fingerprint(values["--master"]);
        try
        {
            PioneerMasterData? construction = null;
            PioneerStart start;
            string kind;
            RootCandidate<PioneerRuntimeAction>[] candidates;
            if (revealed is not null)
            {
                using var db = EvidenceAudit.OpenReadOnly(values["--master"]);
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("事件响应缺少pioneer_data_set。");
                var declared = PioneerEventCompiler.Compile(chara, events[0], revealed.Rewards, PioneerMasterCompiler.EventPartners(db, chara, pioneer));
                start = declared; kind = "declaredEvent";
                candidates = [.. declared.Choices.Select(choice => new RootCandidate<PioneerRuntimeAction>(
                    choice.ChoiceNumber, PioneerRuntimeAction.Choose(declared.Event, choice.ChoiceNumber)))];
            }
            else if (Int(chara, "state") == 0 && (Int(chara, "playing_state") == 1 && events.Length == 0
                || Int(chara, "playing_state") == 5 && events.Length == 1
                    && (Int(events[0], "event_id") is >= 211024 and <= 211028 || RestRules.IsSupportedEvent(Int(events[0], "event_id")))))
            {
                var compiler = new PioneerMasterCompiler(values["--master"]);
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("响应缺少pioneer_data_set。");
                var home = common["home_info"] as JsonObject ?? throw new InvalidDataException("响应缺少home_info。");
                var frame = new JsonObject { ["c"] = chara.DeepClone(), ["s"] = pioneer.DeepClone(),
                    ["home"] = home.DeepClone(), ["events"] = new JsonArray(events.Select(value => (JsonNode)value.DeepClone()).ToArray()) };
                var hasCampCommands = Objects(Array(home, "command_info_array"), "command_info_array")
                    .Any(command => Int(command, "command_id") is >= 3601 and <= 3605);
                var training = compiler.TrainingStart(frame, false, null,
                    hasCampCommands ? new PioneerFacilityCompiler(values["--master"]) : null);
                var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
                using (var db = EvidenceAudit.OpenReadOnly(values["--master"]))
                    construction = PioneerMasterCompiler.ConstructionData(db, masterHash);
                start = training with { SuccessfulOrdinaryTrainingCounts = captured?.SuccessfulOrdinaryCounts,
                    Development = development }; kind = "trainingTurn";
                if ((training.PendingTrainingLevelEvent ?? training.PendingRestEvent) is { } currentEvent)
                    candidates = [new(currentEvent.EventId, PioneerRuntimeAction.Choose(currentEvent, 0))];
                else
                {
                    var available = training.Commands.Where(command => command.Enabled).OrderBy(command => command.CommandId)
                        .Select(command => new RootCandidate<PioneerRuntimeAction>(command.CommandId, PioneerRuntimeAction.Train(command.CommandId))).ToList();
                    if (training.AvailableRestCommands is > 0)
                    {
                        var command = training.AvailableRestCommands == RestRules.OrdinaryAvailable ? RestRules.OrdinaryCommand : RestRules.CampCommand;
                        available.Add(new(command, PioneerRuntimeAction.Rest(command)));
                    }
                    candidates = [.. available];
                }
            }
            else if (Int(chara, "state") == 0 && events.Length == 1 && Int(events[0], "story_id") is 400011107 or 400011108)
            {
                var current = events[0];
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("建设响应缺少pioneer_data_set。");
                var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
                using (var db = EvidenceAudit.OpenReadOnly(values["--master"]))
                    construction = PioneerMasterCompiler.ConstructionData(db, masterHash);
                var building = new PioneerStart.Construction(development,
                    new(Int(current, "event_id"), Int(current, "chara_id"), Int(current, "story_id"), Int(current, "play_timing")));
                start = building; kind = "construction";
                candidates = [new(building.Event.EventId, PioneerRuntimeAction.Choose(building.Event, 0))];
            }
            else if (Int(chara, "state") == 3 && Int(chara, "playing_state") == 5 && events.Length == 1
                && Int(events[0], "event_id") == 6)
            {
                var current = events[0];
                var compiler = new PioneerMasterCompiler(values["--master"]);
                var characterId = compiler.CharacterId(Int(chara, "card_id"));
                if (Int(current, "chara_id") != characterId) throw new InvalidDataException("结局事件角色与master衣装角色不一致。");
                var environment = compiler.Environment(chara, null);
                var ending = new PioneerStart.CharacterEnding(Int(chara, "turn"), Int(chara, "playing_state"), Int(chara, "state"),
                    new(Int(current, "event_id"), characterId, Int(current, "story_id"), Int(current, "play_timing")),
                    environment.Current, environment.Limits, environment.Motivation);
                start = ending; kind = "characterEnding";
                candidates = [new(ending.Event.EventId, PioneerRuntimeAction.Choose(ending.Event, 0))];
            }
            else throw new UnknownMechanismException($"响应位于回合{Int(chara, "turn")}、角色状态{Int(chara, "state")}、阶段{Int(chara, "playing_state")}，待处理事件{events.Length}个；导入支持普通／合宿就绪态、建设、训练升级／休息确认和末尾角色结局。事件选项需以抓包中的get_choice_reward响应为锚点。");

            var input = new PioneerExecutionInput(1, "local-master/" + masterHash, PioneerExecutionMode.Autonomous,
                new(kind, JsonSerializer.SerializeToElement(start, start.GetType(), PioneerExecutionCommand.JsonOptions)), [], []);
            var compiled = PioneerRuntimeData.Compile(start, [], input.RulesIdentity, construction);
            var root = compiled.CreateInitialState(); var scenario = new PioneerScenario(compiled);
            if (candidates.Length == 0) throw new UnknownMechanismException("当前面板没有已接入的合法根行动。");
            foreach (var candidate in candidates)
                if (scenario.ValidateAction(root, candidate.Action) is { } invalid)
                    throw new InvalidOperationException($"已编译根候选{candidate.Id}与状态机不一致：{invalid}。");
            if (masterHash != PioneerExecutionCommand.Fingerprint(values["--master"]))
                throw new InvalidDataException("导入期间master发生变化。");
            Directory.CreateDirectory(output);
            using (var stream = new FileStream(Path.Combine(output, "input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, input, PioneerExecutionCommand.JsonOptions);
            using (var stream = new FileStream(Path.Combine(output, "candidates.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, candidates, PioneerExecutionCommand.JsonOptions);
            using (var stream = new FileStream(Path.Combine(output, "import.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, new { schemaVersion = 1, status = "completed", endpoint,
                    packetSha256 = Convert.ToHexStringLower(SHA256.HashData(packet)), masterSha256 = masterHash,
                    compiled.RulesIdentity, decision = scenario.Inspect(root, 0).Decision.Id, candidateCount = candidates.Length,
                    trainingHistory = captured?.History, eventRewards = revealed?.History }, PioneerExecutionCommand.JsonOptions);
            Console.WriteLine($"已导入回合{root.Header.Turn}、阶段{root.Header.Stage}，生成{candidates.Length}个合法根候选。");
            return 0;
        }
        catch (UnknownMechanismException error)
        {
            return PioneerExecutionCommand.WriteInitializationStop(values[fromCapture ? "--packets" : "--packet"], Path.Combine(output, "import.json"), error);
        }
    }
}
