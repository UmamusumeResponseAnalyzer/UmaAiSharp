using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP.Evidence;

public sealed record IslandActionAuditReport(string RuleVersion, IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<EvidenceIssue> Issues, IReadOnlyList<string> Limits);

public static class IslandActionAudit
{
    public static IslandActionAuditReport Run(string researchDirectory, string framesPath, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("岛训练行动核验输出目录非空，拒绝覆盖。");
        var immediatePath = Path.Combine(researchDirectory, "chain-04", "immediate.jsonl");
        var chainsPath = Path.Combine(researchDirectory, "chain-04", "chains.jsonl");
        var manifestPath = Path.Combine(researchDirectory, "chain-04", "manifest.json");
        var paths = new[] { (immediatePath, "immediate"), (chainsPath, "chains"), (manifestPath, "selection-manifest"), (framesPath, "frames"), (masterPath, "master") };
        var fingerprints = paths.Select(item => Fingerprint(item.Item1, item.Item2)).ToArray();
        var manifest = ReadObject(File.ReadAllText(manifestPath));
        var expectedFrames = Objects(Array(manifest, "inputs"), "manifest.inputs").Single(row =>
            row["path"]!.GetValue<string>().Replace('\\', '/').EndsWith("/expansion-20261108/extract-01/frames.jsonl", StringComparison.Ordinal));
        if (expectedFrames["sha256"]!.GetValue<string>() != fingerprints.Single(item => item.Alias == "frames").Sha256)
            throw new InvalidDataException("frames指纹与严格行动窗口研究输入不一致，不能按包别名盲连。");
        var immediates = File.ReadLines(immediatePath).Select(ReadObject).ToArray();
        var chains = File.ReadLines(chainsPath).Select(ReadObject).ToDictionary(row => Reference(row["action"]));
        var wanted = immediates.SelectMany(row => new[] { Reference(row["before"]), Reference(row["after"]) }).ToHashSet();
        foreach (var chain in chains.Values)
        {
            wanted.Add(Reference(chain["lastRef"]));
            foreach (var step in Objects(Array(chain, "steps"), "steps"))
            {
                wanted.Add(Reference(step["before"]));
                wanted.Add(Reference(step["after"]));
            }
        }
        var frames = new Dictionary<(string Source, long Id), JsonObject>();
        foreach (var line in File.ReadLines(framesPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var reference = document.RootElement.GetProperty("ref");
            var key = (reference[0].GetString()!, reference[1].GetInt64());
            if (wanted.Contains(key)) frames.Add(key, ReadObject(line));
        }
        if (frames.Count != wanted.Count) throw new InvalidDataException("岛训练严格窗口引用的前后帧不完整。");
        var master = new PioneerMasterCompiler(PioneerMasterReader.ReadTraining(masterPath));
        var facilities = new PioneerFacilityCompiler(PioneerMasterReader.ReadFacilities(masterPath));
        Dictionary<(int Type, int Result), int> pointBonuses;
        using (var connection = EvidenceAudit.OpenReadOnly(masterPath))
            pointBonuses = PioneerMasterReader.Rows(connection, "single_mode_11_check_point_eff")
                .Where(row => row["effect_type"] == 202 && row["effect_value_1"] == 9)
                .ToDictionary(row => (row["check_point_type"], row["result_state"]), row => row["effect_value_2"]);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        Directory.CreateDirectory(outputDirectory);
        using var output = new StreamWriter(Path.Combine(outputDirectory, "island-actions.jsonl"));
        using var errors = new StreamWriter(Path.Combine(outputDirectory, "island-action-counterexamples.jsonl"));
        using var eventGrants = new StreamWriter(Path.Combine(outputDirectory, "island-event-grants.jsonl"));
        foreach (var immediate in immediates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var beforeRef = Reference(immediate["before"]);
            var afterRef = Reference(immediate["after"]);
            Count("SelectedImmediateActions");
            try
            {
                var before = frames[beforeRef];
                var after = frames[afterRef];
                var chara = Object(before, "c");
                var pioneer = Object(before, "s");
                var home = Object(before, "home");
                var command = Objects(Array(home, "command_info_array"), "command_info_array").Single(row => Int(row, "command_id") == 3101);
                var disabled = Integers(Array(home, "disable_command_id_array"), "disable_command_id_array");
                if (beforeRef.Source != afterRef.Source || before["run"] is null || !JsonNode.DeepEquals(before["run"], after["run"])
                    || after["endpoint"]!.GetValue<string>() != "shima_training_exec"
                    || !after["paired"]!.GetValue<bool>() || !after["adjacentState"]!.GetValue<bool>() || !after["windowPairsComplete"]!.GetValue<bool>()
                    || !JsonNode.DeepEquals(after["previousRef"], immediate["before"])
                    || Object(after, "windowEndpoints").Any(pair => pair.Key != "shima_training_exec")
                    || Int(Object(after, "windowEndpoints"), "shima_training_exec") != 2
                    || Reference(immediate["requestRef"]).Source != afterRef.Source
                    || Reference(immediate["requestRef"]).Id != Int(after, "requestId")
                    || Int(Object(after, "request"), "current_turn") != Int(chara, "turn") || Int(chara, "state") != 0)
                    throw new InvalidDataException("岛训练前态、请求或严格单动作窗口不匹配。");
                var result = Object(Object(after, "result"), "command_result");
                if (Int(result, "command_id") != 3101 || Int(result, "sub_id") != 1 || Int(result, "result_state") != 2)
                    throw new UnknownMechanismException("岛训练核验仅覆盖3101/sub1/result2成功结果。");
                var deckRows = Objects(Array(chara, "support_card_array"), "support_card_array").ToArray();
                var deckCards = master.Participants(chara, pioneer, deckRows.Select(row => Int(row, "position")).ToArray()).Supports;
                var deck = deckCards.Select(card => new IslandActionSupport(card.Position, card.CardId,
                    master.SupportType(card.CardId), card.Level, card.Bond)).ToImmutableArray();
                var assignments = master.BuildIslandAssignments(chara, pioneer);
                var facilityState = facilities.Facilities(pioneer);
                var checkpoints = Objects(Array(pioneer, "check_point_info_array"), "check_point_info_array")
                    .ToDictionary(row => Int(row, "check_point_type"), row => Int(row, "result_state"));
                var checkpoint = checkpoints.Count == 0 ? 0 : checkpoints.Keys.Max();
                int? bonus = checkpoint == 5 ? null : checkpoint == 0 ? 0 : pointBonuses[(checkpoint, checkpoints[checkpoint])];
                var environment = master.Environment(chara, null);
                var state = new IslandActionState(Int(chara, "turn"), Int(chara, "playing_state"), Int(command, "is_enable") == 1 && !disabled.Contains(3101),
                    Objects(Array(before, "events"), "events").Select(row => Int(row, "story_id")).ToImmutableArray(), environment.Current, environment.Limits,
                    Int(Object(pioneer, "pioneer_info"), "pioneer_pt"), Int(Object(pioneer, "shima_training_info"), "remain_num"), checkpoint, bonus, deck,
                    assignments, facilityState, Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array").ToImmutableArray(),
                    Integers(Array(command, "tips_event_partner_array"), "tips_event_partner_array").ToImmutableArray());
                var upper = Objects(Array(pioneer, "command_info_array"), "pioneer.command_info_array").Single(row => Int(row, "command_id") == 3101);
                var observedGains = new TrainingFormulaPrediction(Values(command), Values(upper), []);
                var settled = IslandActionRules.SettleResolvedSuccess(state, observedGains);
                var afterChara = Object(after, "c");
                var afterPioneer = Object(after, "s");
                var afterBonds = Objects(Array(afterChara, "evaluation_info_array"), "evaluation_info_array")
                    .ToDictionary(row => Int(row, "target_id"), row => Int(row, "evaluation"));
                var observedGrants = Grants(Object(after, "result"));
                var matches = settled.ValuesAfter == master.Environment(afterChara, null).Current
                    && settled.DevelopmentPointsAfter == Int(Object(afterPioneer, "pioneer_info"), "pioneer_pt")
                    && settled.TicketsAfter == Int(Object(afterPioneer, "shima_training_info"), "remain_num")
                    && settled.DeckAfter.All(card => afterBonds[card.Position] == card.Bond)
                    && settled.TemporaryGrants.Select(grant => (grant.Position, grant.EffectType, grant.Value)).Order().SequenceEqual(observedGrants);
                Count("ResolvedPreviewSettlements");
                Count(matches ? "ResolvedPreviewMatched" : "ResolvedPreviewMismatch");
                Count("ImmediateTurnDelta:" + (Int(afterChara, "turn") - state.ActionTurn));
                Count("CompletedCheckpoint:" + checkpoint);
                bool? generatedMatches = null;
                string? generatedExclusion = null;
                try
                {
                    if (master.CharacterId(Int(chara, "card_id")) == 1007)
                        throw new UnknownMechanismException("独立行动快照不能证明黄金船本回合是否已有指定效果；此处不回填历史。");
                    var levels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
                        .Where(row => Int(row, "command_id") is 101 or 102 or 103 or 105 or 106)
                        .ToDictionary(row => Int(row, "command_id"), row => Int(row, "level"));
                    if (levels.Count != 5) throw new InvalidDataException("岛训练缺少五项普通训练等级。");
                    var input = new IslandTrainingInput(environment, levels.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToImmutableArray(),
                        facilities.Base(3101, facilityState), facilityState, assignments);
                    var generated = IslandActionRules.ApplySuccessCandidate(state, input);
                    generatedMatches = generated.TrainingGains.Common == observedGains.Common && generated.TrainingGains.Scenario == observedGains.Scenario
                        && generated.ValuesAfter == settled.ValuesAfter && generated.DevelopmentPointsAfter == settled.DevelopmentPointsAfter;
                    Count("GeneratedSettlements");
                    Count(generatedMatches.Value ? "GeneratedMatched" : "GeneratedMismatch");
                }
                catch (Exception error) when (InputError(error)) { Count("GeneratedInputUnknown"); generatedExclusion = error.Message; }
                var row = new { Source = afterRef.Source, BeforePacketId = beforeRef.Id, AfterPacketId = afterRef.Id, SourceTurn = state.ActionTurn, ResponseTurn = Int(afterChara, "turn"),
                    Settlement = settled, ResolvedPreviewMatches = matches, GeneratedMatches = generatedMatches, GeneratedExclusion = generatedExclusion };
                output.WriteLine(JsonSerializer.Serialize(row, EvidenceJson.LineOptions));
                if (!matches || generatedMatches == false) errors.WriteLine(JsonSerializer.Serialize(new { Row = row, Input = state }, EvidenceJson.LineOptions));
                var chain = chains[afterRef];
                if (Reference(chain["before"]) != beforeRef || Int(chain, "turn") != state.ActionTurn)
                    throw new InvalidDataException("后续事件链没有连接到同一岛训练前态及原请求回合。");
                if (!chain["complete"]!.GetValue<bool>()) { Count("IncompleteFollowupChains"); continue; }
                Count("CompleteFollowupChains");
                var end = frames[Reference(chain["lastRef"])];
                if (Array(end, "events").Count != 0) throw new InvalidDataException("研究链终点的待事件并非明确为空。");
                var endChara = Object(end, "c");
                Count($"EmptyQueueEnd:{Int(endChara, "turn") - state.ActionTurn}:{Int(endChara, "playing_state")}");
                foreach (var step in Objects(Array(chain, "steps"), "steps"))
                {
                    var selectedEvent = Object(step, "selectedEvent");
                    var story = Int(selectedEvent, "story_id");
                    if (story == 400011107 && Int(Object(step, "ticketGrant"), "gain_num") == 1)
                    {
                        var tickets = Integers(Array(step, "tickets"), "tickets");
                        if (state.Tickets != 1 || settled.TicketsAfter != 0 || !tickets.SequenceEqual([0, 1]))
                            throw new InvalidDataException("建设后发券窗口与本次岛训练先扣券的证据不一致。");
                        Count("SeparateConstructionRegrant");
                    }
                    if (story != 809049032) continue;
                    var eventBefore = frames[Reference(step["before"])];
                    var eventAfter = frames[Reference(step["after"])];
                    var request = Object(step, "request");
                    var first = Objects(Array(eventBefore, "events"), "events").First();
                    if (!JsonNode.DeepEquals(first, selectedEvent) || Int(first, "event_id") != Int(request, "event_id")
                        || Int(first, "chara_id") != Int(request, "chara_id") || Int(Object(eventBefore, "c"), "turn") != Int(request, "current_turn")
                        || Int(Object(first, "event_contents_info"), "support_card_id") != 30257 || Array(step, "exclusions").Count != 0)
                        throw new InvalidDataException("Tucker事件选择与请求前队首不匹配。");
                    var grants = IslandActionRules.GrantTuckerSpecialty(new(Int(request, "current_turn"), Int(Object(eventBefore, "c"), "playing_state"),
                        story, Int(first, "play_timing"), Int(request, "choice_number")), deck);
                    if (!grants.Select(grant => (grant.Position, grant.EffectType, grant.Value)).Order().SequenceEqual(Grants(Object(eventAfter, "result"))))
                        throw new InvalidDataException("Tucker+20效果原子与实际响应不匹配。");
                    Count("TuckerGrantsMatched");
                    eventGrants.WriteLine(JsonSerializer.Serialize(new { Source = afterRef.Source, ActionPacketId = afterRef.Id,
                        EventPacketId = Reference(step["after"]).Id, ResponseTurn = Int(Object(eventAfter, "c"), "turn"), Grants = grants }, EvidenceJson.LineOptions));
                    if (Int(Object(eventAfter, "c"), "turn") != Int(request, "current_turn")) Count("TuckerResponseAlreadyNextTurn");
                }
            }
            catch (Exception error) when (InputError(error))
            {
                Count("UnsupportedOrInvalidAction");
                issues.Add(new("IslandActionUnsupported", afterRef.Source, afterRef.Id, error.Message));
            }
        }
        foreach (var item in paths)
            if (Fingerprint(item.Item1, item.Item2).Sha256 != fingerprints.Single(value => value.Alias == item.Item2).Sha256)
                throw new InvalidDataException("岛训练行动核验期间输入改变。");
        var report = new IslandActionAuditReport(IslandActionRules.Version, fingerprints, counts, issues,
            ["SelectionAndFramesReuseExistingCorpusNotIndependentHoldout", "ResolvedPreviewSettlementsDoNotValidateGainGeneration",
                "GeneratedSettlementsUseOnlyActionBeforeState", "ObservedFutureEventStepsAreAuditOutputsNotSimulatorInputs",
                "NoTurnOrPhaseTransitionInNumericalSettlement", "NoTemporaryEffectActivationExpiryOrStackingRule",
                "AllObservedDecksContainLevel50TuckerNoGeneralizationToOtherFriends", "NoRandomHouseSubsetOrHintDraw",
                "NicknameChangesAndOtherScenarioLogChangesNotGenerated", "SevenUncoveredFacilityStatesRemainRejected"]);
        File.WriteAllText(Path.Combine(outputDirectory, "island-action-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options));
        return report;
    }

    static (int Position, int Type, int Value)[] Grants(JsonObject result)
    {
        var direct = Array(result, "gain_partner_support_effect_array");
        var nested = Array(Object(result, "gain_parameter_info"), "gain_partner_support_effect_array");
        if (!JsonNode.DeepEquals(direct, nested)) throw new InvalidDataException("顶层与嵌套临时效果不一致，不能相加或任选一路。");
        var grants = Objects(direct, "gain_partner_support_effect_array").SelectMany(row => Objects(Array(row, "effect_array"), "effect_array")
            .Select(effect => (Position: Int(row, "training_partner_id"), Type: Int(effect, "effect_type"), Value: Int(effect, "effect_value")))).Order().ToArray();
        if (grants.Distinct().Count() != grants.Length) throw new InvalidDataException("临时效果增量包含重复原子。");
        return grants;
    }

    static bool InputError(Exception error) => error is UnknownMechanismException or IllegalActionException or InvalidDataException
        or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException;
    static JsonObject ReadObject(string json) => JsonSerializer.Deserialize<JsonObject>(json, EvidenceJson.ReadOptions)
        ?? throw new InvalidDataException("证据对象不能为null。");
    static JsonObject Object(JsonObject parent, string name) => parent[name] as JsonObject ?? throw new InvalidDataException($"缺少对象{name}。");
    static (string Source, long Id) Reference(JsonNode? node)
    {
        var array = node as JsonArray ?? throw new InvalidDataException("缺少包引用数组。");
        return (array[0]!.GetValue<string>(), array[1]!.GetValue<long>());
    }
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "IslandActionEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
