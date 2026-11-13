using UmaAiSharp.Pioneer.Offline;
using UmaAiSharp.Pioneer.Game;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP.Evidence;

public sealed record TrainingModesReport(string CandidateVersion, IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<TrainingFormulaCoverage> Coverage,
    IReadOnlyList<EvidenceIssue> Issues, IReadOnlyList<string> Limits);

public static class TrainingModesAudit
{
    public static TrainingModesReport Run(string inputDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("三模式训练核验输出目录非空，拒绝覆盖。");
        var paths = new[] { (Path.Combine(inputDirectory, "pioneer-observations.jsonl"), "observations"),
            (Path.Combine(inputDirectory, "exchanges.jsonl"), "exchanges"), (masterPath, "master"),
            (Path.Combine(inputDirectory, "pioneer-actions.jsonl"), "actions") };
        var fingerprints = paths.Select(path => Fingerprint(path.Item1, path.Item2)).ToArray();
        var exchanges = File.ReadLines(paths[1].Item1).Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("exchange不能为空。")).ToDictionary(value => value.Key);
        var eventRequests = File.ReadLines(paths[3].Item1)
            .Select(line => JsonSerializer.Deserialize<ObservedAction>(line, EvidenceJson.ReadOptions) ?? throw new InvalidDataException("action不能为空。"))
            .Where(action => action.Endpoint.EndsWith("/check_event", StringComparison.Ordinal))
            .Select(action =>
            {
                var request = action.Fields["single_mode_check_event_request_common"] as JsonObject
                    ?? throw new InvalidDataException("check_event请求缺少common。");
                return new EventRequest(action.ExchangeKey, action.Endpoint, action.Source, action.PacketId, Int(request, "current_turn"),
                    Int(request, "event_id"), Int(request, "choice_number"));
            }).GroupBy(request => request.ExchangeKey).ToDictionary(group => group.Key, group => group.ToArray());
        var master = new PioneerMasterCompiler(PioneerMasterReader.ReadTraining(masterPath));
        var facilityMaster = new PioneerFacilityCompiler(PioneerMasterReader.ReadFacilities(masterPath));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var coverage = new Dictionary<(string Dimension, string Value), int>();
        var issues = new List<EvidenceIssue>();
        var history = new Dictionary<string, EventFrame>(StringComparer.Ordinal);
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        void Cover(string dimension, object value)
        {
            var key = (dimension, value.ToString()!);
            coverage[key] = coverage.GetValueOrDefault(key) + 1;
        }
        Directory.CreateDirectory(outputDirectory);
        using var samples = new StreamWriter(Path.Combine(outputDirectory, "training-modes-samples.jsonl"), false, new UTF8Encoding(false));
        using var counterexamples = new StreamWriter(Path.Combine(outputDirectory, "training-modes-counterexamples.jsonl"), false, new UTF8Encoding(false));
        foreach (var line in File.ReadLines(paths[0].Item1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("observation不能为空。");
            Count("Frames");
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint
                || exchange.DistinctRequestBodies != 1 || exchange.DistinctResponseBodies != 1
                || !exchange.Responses.Contains(new PacketReference(observation.Source, observation.PacketId))
                || !exchange.Requests.Any(request => request.Source == observation.Source && request.Id < observation.PacketId))
            {
                history.Remove(observation.Source);
                Count("UnpairedFrames");
                continue;
            }
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (common?["chara_info"] is not JsonObject chara)
            {
                history.Remove(observation.Source);
                Count("MissingCharacter");
                continue;
            }
            EventFrame frame;
            try
            {
                history.TryGetValue(observation.Source, out var previous);
                eventRequests.TryGetValue(observation.ExchangeKey, out var requests);
                frame = ObserveEvent(observation, common, chara, master.CharacterId(Int(chara, "card_id")), previous, exchange, requests);
                history[observation.Source] = frame;
                if (frame.Signal) Count("GoldShipSignals");
            }
            catch (Exception error) when (IsInputError(error))
            {
                history.Remove(observation.Source);
                Count("InvalidEventState");
                issues.Add(new("TrainingEventState", observation.Source, observation.PacketId, error.Message));
                continue;
            }
            if (common["home_info"] is not JsonObject home || observation.Visible["pioneer_data_set"] is not JsonObject pioneer)
            {
                Count("MissingHomeOrScenario");
                continue;
            }
            if (home["disable_command_id_array"] is not JsonArray disabledArray)
            {
                Count("MissingOrMalformedDisableList");
                issues.Add(new("TrainingAvailabilityUnknown", observation.Source, observation.PacketId,
                    "home.disable_command_id_array缺失、null或不是数组，不能默认为无禁用命令。"));
                continue;
            }
            var disabled = Integers(disabledArray, "disable_command_id_array").ToHashSet();
            var scenarioCommands = Objects(Array(pioneer, "command_info_array"), "pioneer.command_info_array")
                .ToDictionary(command => Int(command, "command_id"));
            foreach (var command in Objects(Array(home, "command_info_array"), "home.command_info_array"))
            {
                var id = Int(command, "command_id");
                var kind = id switch { 101 or 102 or 103 or 105 or 106 => "ordinary", >= 3601 and <= 3605 => "camp", 3101 => "island", _ => null };
                if (kind is null) continue;
                if (Int(command, "is_enable") != 1 || disabled.Contains(id)) { Count("DisabledCommands"); continue; }
                Count(kind + ".Enabled");
                try
                {
                    if (!frame.Known) throw new UnknownMechanismException("黄金船本回合事件历史不完整；缺少信号不能当作无加成。");
                    var lower = command["params_inc_dec_info_array"] is JsonArray ? Values(command) : (TrainingValues?)null;
                    scenarioCommands.TryGetValue(id, out var upperCommand);
                    var upper = upperCommand?["params_inc_dec_info_array"] is JsonArray ? Values(upperCommand) : (TrainingValues?)null;
                    if (lower is null && upper is null) { Count(kind + ".NoVisiblePreview"); continue; }
                    var environment = master.Environment(chara, frame.Boost);
                    object input;
                    TrainingFormulaPrediction predicted;
                    ImmutableArray<TrainingSupport> supports;
                    ImmutableArray<TrainingFacility> facilities = [];
                    if (kind == "ordinary")
                    {
                        var ordinary = master.Input(chara, command, pioneer) with { GoldShipBoost = frame.Boost };
                        input = ordinary;
                        supports = ordinary.Supports;
                        predicted = TrainingFormulaCandidate.GenerateOrdinary(ordinary);
                    }
                    else
                    {
                        facilities = facilityMaster.Facilities(pioneer);
                        var basis = facilityMaster.Base(id, facilities);
                        if (kind == "camp")
                        {
                            var participants = master.Participants(chara, pioneer, Integers(Array(command, "training_partner_array"), "training_partner_array"));
                            var camp = new CampTrainingInput(id, Int(command, "level"), participants.Guests,
                                environment, participants.Supports, basis, facilities);
                            supports = camp.Supports;
                            input = camp;
                            predicted = TrainingFormulaCandidate.GenerateCamp(camp);
                        }
                        else
                        {
                            var ordinaryLevels = Objects(Array(chara, "training_level_info_array"), "training_level_info_array")
                                .Where(value => Int(value, "command_id") is 101 or 102 or 103 or 105 or 106)
                                .ToDictionary(value => Int(value, "command_id"), value => Int(value, "level"));
                            if (ordinaryLevels.Count != 5) throw new UnknownMechanismException("岛训练缺少五项普通训练等级。");
                            var assignments = master.BuildIslandAssignments(chara, pioneer);
                            var island = new IslandTrainingInput(environment,
                                [ordinaryLevels[101], ordinaryLevels[105], ordinaryLevels[102], ordinaryLevels[103], ordinaryLevels[106]],
                                basis, facilities, assignments);
                            supports = assignments.SelectMany(group => group.Supports).ToImmutableArray();
                            input = island;
                            predicted = TrainingFormulaCandidate.GenerateIsland(island);
                        }
                    }
                    var numeric = input switch
                    {
                        TrainingFormulaInput ordinary => TrainingNumericKernel.GenerateOrdinary(ordinary),
                        CampTrainingInput camp => TrainingNumericKernel.GenerateCamp(camp),
                        IslandTrainingInput island => TrainingNumericKernel.GenerateIsland(island),
                        _ => throw new InvalidOperationException("训练核验缺少明确模式输入。")
                    };
                    if (numeric.Common != predicted.Common || numeric.Scenario != predicted.Scenario)
                        throw new InvalidDataException("数值热核与精确审计参考核不一致。");
                    Count(kind + ".NumericReferenceMatched");
                    var matches = (lower is null || predicted.Common == lower) && (upper is null || predicted.Scenario == upper);
                    var actionable = Int(chara, "state") == 0 && Int(chara, "playing_state") == 1
                        && common["unchecked_event_array"] is JsonArray { Count: 0 };
                    Count(kind + ".Checked");
                    Count(kind + (matches ? ".Matched" : ".Mismatch"));
                    Count(kind + (lower is null ? ".UpperOnly" : upper is null ? ".LowerOnly" : ".BothLayers"));
                    if (actionable) Count(kind + ".DecisionView");
                    if (actionable || observation.Endpoint.EndsWith("/check_event", StringComparison.Ordinal)) Count(kind + ".PrimaryScope");
                    if (frame.Boost is not null) Count(kind + ".ActiveGoldShipState");
                    var result = new { observation.Source, observation.PacketId, observation.RunKey, Turn = Int(chara, "turn"),
                        Command = id, Kind = kind, Actionable = actionable, Boost = frame.Boost, ObservedCommon = lower,
                        ObservedScenario = upper, Prediction = predicted, Matches = matches };
                    samples.WriteLine(JsonSerializer.Serialize(result, EvidenceJson.LineOptions));
                    if (!matches) counterexamples.WriteLine(JsonSerializer.Serialize(new { Result = result, Input = input }, EvidenceJson.LineOptions));
                    Cover("Command", id);
                    Cover("Motivation", environment.Motivation);
                    Cover("CharacterCard", Int(chara, "card_id"));
                    foreach (var support in supports) Cover("SupportCardLevel", $"{support.CardId}:{support.Level}");
                    foreach (var facility in facilities) Cover("FacilityState", facility.StateId);
                }
                catch (Exception error) when (IsInputError(error))
                {
                    Count(kind + ".Unsupported");
                    issues.Add(new("TrainingModesUnsupported", observation.Source, observation.PacketId, $"command={id}: {error.Message}"));
                }
            }
        }
        foreach (var path in paths)
            if (Fingerprint(path.Item1, path.Item2).Sha256 != fingerprints.Single(value => value.Alias == path.Item2).Sha256)
                throw new InvalidDataException("训练核验期间输入文件发生变化。");
        var report = new TrainingModesReport(TrainingFormulaCandidate.Version, fingerprints, counts,
            coverage.OrderBy(value => value.Key.Dimension, StringComparer.Ordinal).ThenBy(value => value.Key.Value, StringComparer.Ordinal)
                .Select(value => new TrainingFormulaCoverage(value.Key.Dimension, value.Key.Value, value.Value)).ToArray(), issues,
            ["Refit14CandidateSelectedOnSameCorpusNotIndependentHoldout", "MultipleQuantizationOrdersRemainObservationallyEquivalent",
                "LocalMasterAcceptedWithoutStrictResourceVersionBinding", "UnknownInputRejectedRatherThanFilled",
                "MissingPreviewLayerNotReconstructedAsObservation", "EnabledCommandIsNotProofOfWholeFrameActionLegality",
                "SupportAndFacilityCoverageIsFinite", "PlacementEventGenerationFailureOutcomesAndRawOverflowNotImplemented",
                "GoldShipStateRequiresObservedHistoryAndDoesNotDefineOccurrenceOrTargetProbabilities",
                "GoldShipKnownMeansWithinCapturedAdjacentExchangeWindowNotProofThatAllGameExchangesWereCaptured",
                "LoadOrMissingOrUnpairedFrameBreaksGoldShipHistoryNoCrossSourceInheritance"]);
        File.WriteAllText(Path.Combine(outputDirectory, "training-modes-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static bool IsInputError(Exception error) => error is UnknownMechanismException or InvalidDataException or InvalidOperationException
        or ArgumentException or OverflowException or KeyNotFoundException;

    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, alias == "master" ? "LocalGameMaster" : "NormalizedPioneerEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

    sealed record EventFrame(string? Run, int Turn, long PacketId, bool Known, GoldShipTrainingBoost? Boost,
        int? PendingGoldShipEventId, bool Signal);
    sealed record EventRequest(string ExchangeKey, string Endpoint, string Source, long PacketId, int Turn, int EventId, int Choice);

    static EventFrame ObserveEvent(PioneerObservation observation, JsonObject common, JsonObject chara, int characterId, EventFrame? previous,
        ExchangeSummary exchange, EventRequest[]? requests)
    {
        if (previous is not null && previous.PacketId >= observation.PacketId)
            throw new InvalidDataException("同一来源的训练观测未按packet_id严格递增。");
        var turn = Int(chara, "turn");
        var card = Int(chara, "card_id");
        if (characterId == 1007 && card != 100701)
            throw new UnknownMechanismException($"黄金船育成卡{card}的指定训练事件适用性未核验，不能假定无效果。");
        var sameRun = observation.RunKey is not null && previous?.Run == observation.RunKey;
        var sameTurn = sameRun && previous!.Turn == turn;
        var adjacent = previous is not null && observation.PacketId == previous.PacketId + 2
            && exchange.Requests.Contains(new PacketReference(observation.Source, previous.PacketId + 1));
        var load = observation.Endpoint.EndsWith("/load", StringComparison.Ordinal);
        var known = card != 100701 || observation.Endpoint.EndsWith("/start", StringComparison.Ordinal)
            || !load && adjacent && previous!.Known && (sameTurn || sameRun && previous.Turn + 1 == turn);
        var boost = known && sameTurn ? previous!.Boost : null;
        var affected = observation.Visible["gain_parameter_info"] is JsonObject gain && gain["effected_command_id_array"] is JsonArray array
            ? Integers(array, "effected_command_id_array") : [];
        if (affected.Length > 0)
        {
            if (card != 100701 || !observation.Endpoint.EndsWith("/check_event", StringComparison.Ordinal)
                || !sameTurn || !adjacent || previous!.PendingGoldShipEventId is null || affected.Length != 1
                || requests is not { Length: 1 } || requests[0].Source != observation.Source
                || requests[0].Endpoint != observation.Endpoint
                || !exchange.Requests.Contains(new PacketReference(requests[0].Source, requests[0].PacketId))
                || requests[0].EventId != previous.PendingGoldShipEventId || requests[0].Turn != turn || requests[0].Choice != 0
                || affected[0] is not (101 or 102 or 103 or 105 or 106))
                throw new UnknownMechanismException("指定训练信号缺少同育成同回合的黄金船501007525事件证据。");
            boost = new(affected[0]);
            known = true;
        }
        var first = common["unchecked_event_array"] is JsonArray events ? Objects(events, "unchecked_event_array").FirstOrDefault() : null;
        var pending = first is not null && Int(first, "story_id") == 501007525 ? Int(first, "event_id") : (int?)null;
        return new(observation.RunKey, turn, observation.PacketId, known, boost, pending, affected.Length > 0);
    }

}
