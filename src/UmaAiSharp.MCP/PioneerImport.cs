using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Events;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP;

public static class PioneerImport
{
    public static ToolResult Run(string sourcePath, string? endpoint, long? packetId, string? masterPath,
        string outputDirectory, CancellationToken cancellationToken = default, string? rulesPath = null)
    {
        if ((masterPath is null) == (rulesPath is null))
            throw new InvalidDataException("回合导入要求提供一个 masterPath 或 rulesPath。");
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("回合导入输出目录非空，拒绝覆盖。");
        CapturedPioneerTraining? captured = null;
        CapturedPioneerEvent? revealed = null;
        if (packetId is { } id)
        {
            try
            {
                revealed = PioneerEventCapture.Read(sourcePath, id, cancellationToken);
                if (revealed is null) captured = PioneerTrainingHistory.Read(sourcePath, id, cancellationToken,
                    includeStoryHistory: rulesPath is not null);
            }
            catch (UnknownMechanismException error)
            {
                return PioneerExecution.WriteInitializationStop(sourcePath, Path.Combine(output, "import.json"), error);
            }
        }
        endpoint = revealed?.Endpoint ?? captured?.Endpoint ?? endpoint;
        if (endpoint is null || !endpoint.StartsWith("/single_mode/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/single_mode_pioneer/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/umamusume/single_mode/", StringComparison.Ordinal)
            && !endpoint.StartsWith("/umamusume/single_mode_pioneer/", StringComparison.Ordinal))
            throw new InvalidDataException("endpoint 要求实际育成响应路径，如/single_mode/load；不接受完整URL或请求端点推测。");
        var packet = revealed?.Payload ?? captured?.Payload ?? File.ReadAllBytes(sourcePath);
        JsonObject response;
        try { response = PacketAudit.Decode(packet); }
        catch (MessagePackSerializationException error) { throw new InvalidDataException("回合响应无法解码为MessagePack对象。", error); }
        var responseData = response["data"] as JsonObject ?? throw new InvalidDataException("响应缺少data对象。");
        JsonObject common;
        try { common = PioneerNormalizer.GetCommon(responseData, endpoint) ?? throw new InvalidDataException("响应缺少当前端点的育成common。"); }
        catch (InvalidOperationException error) { throw new InvalidDataException("响应的育成common必须是对象。", error); }
        var chara = common["chara_info"] as JsonObject ?? throw new InvalidDataException("响应缺少chara_info。");
        if (Int(chara, "scenario_id") != 11) throw new InvalidDataException("回合导入要求Pioneer剧本scenario_id=11。");
        if (rulesPath is not null && endpoint.EndsWith("/start", StringComparison.Ordinal))
        {
            common = PioneerAutonomousSession.CompleteOpening(common);
            chara = common["chara_info"]!.AsObject();
        }
        var events = Objects(Array(common, "unchecked_event_array"), "unchecked_event_array").ToArray();
        var artifact = rulesPath is null ? default : PioneerRulesSnapshot.ReadArtifact(rulesPath);
        var rules = artifact.Snapshot;
        var inheritance = rules is not null && packetId is { } inheritanceAnchor
            ? ReadInheritance(sourcePath, inheritanceAnchor, captured?.History.StartPacketId, common, cancellationToken)
            : null;
        var masterHash = rules?.SourceMasterSha256 ?? PioneerExecution.Fingerprint(masterPath!);
        try
        {
            var construction = rules?.Construction;
            PioneerPlacementSeed? placement = null;
            PioneerStart start;
            string kind;
            RootCandidate<PioneerRuntimeAction>[]? candidates = null;
            if (revealed is not null)
            {
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("事件响应缺少pioneer_data_set。");
                var partners = rules?.EventPartners;
                if (partners is null)
                {
                    using var db = EvidenceAudit.OpenReadOnly(masterPath!);
                    partners = PioneerMasterReader.ReadEventPartners(db);
                }
                var declared = PioneerEventCompiler.Compile(chara, events[0], revealed.Rewards, PioneerMasterCompiler.EventPartners(partners, chara, pioneer));
                start = declared; kind = "declaredEvent";
                candidates = [.. declared.Choices.Select(choice => new RootCandidate<PioneerRuntimeAction>(
                    choice.ChoiceNumber, PioneerRuntimeAction.Choose(declared.Event, choice.ChoiceNumber)))];
            }
            else if (Int(chara, "state") == 0 && (Int(chara, "playing_state") == 1 && events.Length == 0
                || Int(chara, "playing_state") == 5 && events.Length == 1
                    && (Int(events[0], "story_id") == UmaAiSharp.Pioneer.Training.TrainingLevelRules.StoryId
                        || RestRules.IsStory(Int(events[0], "story_id"), Int(events[0], "chara_id")))))
            {
                var compiler = new PioneerMasterCompiler(rules?.Training ?? PioneerMasterReader.ReadTraining(masterPath!));
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("响应缺少pioneer_data_set。");
                var home = common["home_info"] as JsonObject ?? throw new InvalidDataException("响应缺少home_info。");
                var hasCampCommands = Objects(Array(home, "command_info_array"), "command_info_array")
                    .Any(command => Int(command, "command_id") is >= 3601 and <= 3605);
                var training = PioneerAutonomousSession.ReadTrainingPanel(common, pioneer, compiler,
                    hasCampCommands ? new PioneerFacilityCompiler(rules?.Facilities ?? PioneerMasterReader.ReadFacilities(masterPath!)) : null,
                    wholeRun: rules is not null, goldShipBoost: captured?.GoldShipBoost);
                if (rules is not null) placement = PioneerPlacementSeed.Observe(chara, pioneer);
                else
                {
                    using var db = EvidenceAudit.OpenReadOnly(masterPath!);
                    construction = PioneerMasterReader.ConstructionData(db, masterHash);
                }
                start = training with { SuccessfulOrdinaryTrainingCounts = captured?.SuccessfulOrdinaryCounts
                        ?? (rules is not null && training.Commands.All(command => command.ExecutionCount == 0) ? [0, 0, 0, 0, 0] : null),
                    Inheritance = inheritance, ObservedGrants = captured?.ObservedGrants,
                    HomeActions = rules is null ? null : PioneerAutonomousSession.ReadHomeActions(common, rules,
                        captured?.UnseenFriendMask ?? (training.Commands.All(command => command.ExecutionCount == 0) ? 63 : 0), events.Length == 0,
                        captured?.RaceHistory, captured?.RaceReservations),
                    ObservedStoryHistory = rules is null ? null : PioneerAutonomousSession.ResolveStories(captured?.ObservedStories, rules) };
                kind = "trainingTurn";
            }
            else if (Int(chara, "state") == 0 && Int(chara, "playing_state") == 35 && events.Length == 0)
            {
                if (rules is null) throw new InvalidDataException("建设规划推荐要求提供整局规则资源 rulesPath。");
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("规划响应缺少pioneer_data_set。");
                var planning = PioneerAutonomousSession.ReadPlanningPanel(common, pioneer, new(rules.Training));
                var observedCounts = PioneerGameData.ReadTrainingExecutionCounts(pioneer)!;
                var training = planning.Continuation! with
                {
                    Inheritance = inheritance, ObservedGrants = captured?.ObservedGrants,
                    ObservedStoryHistory = PioneerAutonomousSession.ResolveStories(captured?.ObservedStories, rules),
                    SuccessfulOrdinaryTrainingCounts = captured?.SuccessfulOrdinaryCounts
                        ?? (observedCounts.All(count => count == 0) ? [0, 0, 0, 0, 0] : null),
                    HomeActions = PioneerAutonomousSession.ReadHomeActions(common, rules,
                        captured?.UnseenFriendMask ?? (observedCounts.All(count => count == 0) ? 63 : 0), false,
                        captured?.RaceHistory, captured?.RaceReservations),
                };
                placement = PioneerPlacementSeed.Observe(chara, pioneer);
                start = planning with { Continuation = training };
                kind = "planning";
            }
            else if (Int(chara, "state") == 0 && events.Length == 1 && Int(events[0], "story_id") is 400011107 or 400011108)
            {
                var current = events[0];
                var pioneer = responseData["pioneer_data_set"] as JsonObject ?? throw new InvalidDataException("建设响应缺少pioneer_data_set。");
                var development = ConstructionEvidence.ReadObservation(JsonSerializer.SerializeToElement(common), JsonSerializer.SerializeToElement(pioneer));
                if (construction is null)
                {
                    using var db = EvidenceAudit.OpenReadOnly(masterPath!);
                    construction = PioneerMasterReader.ConstructionData(db, masterHash);
                }
                var building = new PioneerStart.Construction(development,
                    new(Int(current, "event_id"), Int(current, "chara_id"), Int(current, "story_id"), Int(current, "play_timing")));
                start = building; kind = "construction";
                candidates = [new(building.Event.StoryId, PioneerRuntimeAction.Choose(building.Event, 0))];
            }
            else if (Int(chara, "state") == 3 && Int(chara, "playing_state") == 5 && events.Length == 1
                && PioneerEvent.IsCharacterEndingStory(Int(events[0], "story_id")))
            {
                var current = events[0];
                var compiler = new PioneerMasterCompiler(rules?.Training ?? PioneerMasterReader.ReadTraining(masterPath!));
                var characterId = compiler.CharacterId(Int(chara, "card_id"));
                if (Int(current, "chara_id") != characterId) throw new InvalidDataException("结局事件角色与master衣装角色不一致。");
                var environment = compiler.Environment(chara, null);
                var ending = new PioneerStart.CharacterEnding(Int(chara, "turn"), Int(chara, "playing_state"), Int(chara, "state"),
                    new(Int(current, "event_id"), characterId, Int(current, "story_id"), Int(current, "play_timing")),
                    environment.Current, environment.Limits, environment.Motivation);
                start = ending; kind = "characterEnding";
                candidates = [new(ending.Event.StoryId, PioneerRuntimeAction.Choose(ending.Event, 0))];
            }
            else throw new UnknownMechanismException($"响应位于回合{Int(chara, "turn")}、角色状态{Int(chara, "state")}、阶段{Int(chara, "playing_state")}，待处理事件{events.Length}个；导入支持普通／合宿就绪态、建设规划、建设、训练升级／休息确认和末尾角色结局。事件选项需以抓包中的get_choice_reward响应为锚点。");

            var input = new PioneerExecutionInput(rules is null ? 1 : 2, artifact.Identity ?? "local-master/" + masterHash, PioneerExecutionMode.Autonomous,
                new(kind, JsonSerializer.SerializeToElement(start, start.GetType(), PioneerExecution.JsonOptions)), [], [], placement);
            var trainingContext = start as PioneerStart.TrainingTurn ?? (start as PioneerStart.Planning)?.Continuation;
            var turnRules = placement is not null ? rules!.CompileTurnRules(trainingContext!, placement) : null;
            var estimated = rules is not null ? PioneerAutonomousSession.EstimateTrainingHistory(start, rules) : null;
            var compiled = PioneerRuntimeData.Compile(start, [], input.RulesIdentity, construction,
                checkpointMaster: turnRules is null ? null : rules!.CompileCheckpoints(), turnRules: turnRules,
                usesStoryDecisions: rules is not null, trainingHistory: estimated);
            var root = compiled.CreateInitialState(); var scenario = new PioneerScenario(compiled);
            var currentProgress = rules?.StoryProgress?.FirstOrDefault(row => row.StoryId == root.Header.Event.StoryId);
            candidates ??= scenario.Candidates(root).ToArray();
            if (candidates.Length == 0) throw new UnknownMechanismException("当前面板没有已接入的合法根行动。");
            foreach (var candidate in candidates)
                if (scenario.ValidateAction(root, candidate.Action) is { } invalid)
                    throw new InvalidOperationException($"已编译根候选{candidate.Id}与状态机不一致：{invalid}。");
            if (masterPath is not null && masterHash != PioneerExecution.Fingerprint(masterPath))
                throw new InvalidDataException("导入期间master发生变化。");
            Directory.CreateDirectory(output);
            using (var stream = new FileStream(Path.Combine(output, "input.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, input, PioneerExecution.JsonOptions);
            using (var stream = new FileStream(Path.Combine(output, "candidates.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, candidates, PioneerExecution.JsonOptions);
            using (var stream = new FileStream(Path.Combine(output, "import.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(stream, new { schemaVersion = 1, status = "completed", endpoint,
                    packetSha256 = Convert.ToHexStringLower(SHA256.HashData(packet)), masterSha256 = masterHash,
                    compiled.RulesIdentity, decision = scenario.Inspect(root, 0).Decision.Id, candidateCount = candidates.Length,
                    trainingHistory = captured?.History, estimatedTrainingHistory = estimated is not null, eventRewards = revealed?.History,
                    inheritance = trainingContext is null || trainingContext.Turn >= 55 ? null
                        : inheritance is { } family ? (family.HasUmaPlan ? "umaPlan" : "ordinary") : "missingPedigree",
                    currentStoryProgress = currentProgress is { StoryId: > 0 } ? currentProgress : null,
                    observedStoryOccurrences = captured?.ObservedStories?.Length,
                    unmatchedStoryConfirmations = captured?.UnmatchedStoryConfirmations }, PioneerExecution.JsonOptions);
            return new("completed", new { turn = root.Header.Turn, stage = root.Header.Stage, candidateCount = candidates.Length },
                ToolResult.Paths(output, ("input", "input.json"), ("candidates", "candidates.json"), ("report", "import.json")));
        }
        catch (UnknownMechanismException error)
        {
            return PioneerExecution.WriteInitializationStop(sourcePath, Path.Combine(output, "import.json"), error);
        }
    }

    static UmaAiSharp.Pioneer.Inheritance.PioneerInheritanceContext? ReadInheritance(string sourcePath, long anchor,
        long? start, JsonObject common, CancellationToken cancellationToken)
    {
        if (start is null) return null;
        using var db = EvidenceAudit.OpenReadOnly(sourcePath);
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT endpoint,payload FROM packets
            WHERE direction='response' AND id<=$start AND viewer_id=(SELECT viewer_id FROM packets WHERE id=$anchor)
              AND (endpoint LIKE '%/load/index' OR endpoint LIKE '%/pre_single_mode/index'
                OR endpoint LIKE '%/pre_single_mode/get_succession_trained_chara' OR endpoint LIKE '%/trained_chara/load'
                OR endpoint LIKE '%/single_mode_pioneer/finish' OR id=$start)
            ORDER BY id
            """;
        query.Parameters.AddWithValue("$anchor", anchor);
        query.Parameters.AddWithValue("$start", start.Value);
        var history = new PioneerInheritanceCapture();
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PacketAudit.Decode((byte[])rows.GetValue(1))["data"] is JsonObject data)
                history.Observe(data, rows.GetString(0));
        }
        return history.Read(common);
    }
}
