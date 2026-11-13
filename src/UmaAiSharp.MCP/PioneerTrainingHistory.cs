using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Game;
using UmaAiSharp.Pioneer.IslandTraining;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP;

public sealed record TrainingHistoryProvenance(long StartPacketId, long AnchorPacketId, int CapturedPackets,
    int TrainingActions, int CampActions, int FailedActions, string PrefixSha256);
public sealed record CapturedPioneerTraining(byte[] Payload, string Endpoint,
    ImmutableArray<int> SuccessfulOrdinaryCounts, TrainingHistoryProvenance History,
    ImmutableArray<PioneerStoryOccurrence>? ObservedStories, int UnmatchedStoryConfirmations, int UnseenFriendMask,
    ImmutableArray<TemporarySupportGrant> ObservedGrants, GoldShipTrainingBoost? GoldShipBoost,
    ImmutableArray<PioneerRaceHistory>? RaceHistory, ImmutableArray<PioneerRaceReservation>? RaceReservations);

public static class PioneerTrainingHistory
{
    sealed record Frame(long Id, string Endpoint, string RequestId, int Turn, int[]? Counts, int? Command, int? Result,
        ImmutableArray<PendingStory> PendingStories, int? PartnerMasks, ImmutableArray<TemporarySupportGrant> Grants,
        GoldShipTrainingBoost? GoldShipSignal);
    readonly record struct Request(long Id, int Turn, int Command, int Type);
    readonly record struct StoryRequest(long Id, int Turn, int EventId, int CharacterId, int Choice);
    readonly record struct PendingStory(int EventId, int StoryId, int CharacterId, int Timing, int Phase, int SupportCardId, int PartnerId);

    public static CapturedPioneerTraining Read(string path, long anchorId, CancellationToken cancellationToken = default,
        bool includeStoryHistory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorId);
        using var db = EvidenceAudit.OpenReadOnly(path);
        using var transaction = db.BeginTransaction(deferred: true);
        byte[] payload;
        string endpoint, actor;
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT direction,endpoint,viewer_id,payload FROM packets WHERE id=$id";
            query.Parameters.AddWithValue("$id", anchorId);
            using var row = query.ExecuteReader();
            if (!row.Read() || row.GetString(0) != "response" || row.IsDBNull(2))
                throw new InvalidDataException($"包{anchorId}不是带明确viewer_id的响应。");
            endpoint = row.GetString(1); actor = row.GetString(2); payload = (byte[])row.GetValue(3);
        }
        if (string.IsNullOrWhiteSpace(actor)) throw new InvalidDataException("锚点viewer_id为空，不能关联育成历史。");
        var anchor = Decode(payload, anchorId)["data"] as JsonObject ?? throw new InvalidDataException("锚点缺少data对象。");
        var chara = PioneerNormalizer.GetCommon(anchor, endpoint)?["chara_info"] as JsonObject
            ?? throw new InvalidDataException("锚点缺少chara_info。");
        if (Int(chara, "scenario_id") != 11) throw new InvalidDataException("训练历史要求Pioneer锚点。");
        var run = RunId(chara);
        var anchorTurn = Int(chara, "turn");
        var frames = new Dictionary<(string Request, string Endpoint), Frame>();
        var requests = new Dictionary<(string Request, string Endpoint), Request>();
        var islandRequests = new Dictionary<(string Request, string Endpoint), int>();
        var storyRequests = new Dictionary<(string Request, string Endpoint), StoryRequest>();
        var captures = new Dictionary<(string Request, string Endpoint, string Direction), string>();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var packetCount = 0;
        long startId = 0;
        ImmutableArray<PioneerRaceHistory>? races = null;
        ImmutableArray<PioneerRaceReservation>? reservations = null;
        ImmutableArray<PioneerRaceReservation>? pendingReservations = null;
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id,direction,endpoint,request_id,payload,app_ver,res_ver FROM packets
                WHERE id<=$anchor AND viewer_id=$actor AND endpoint LIKE '%/single_mode%/%' ORDER BY id DESC
                """;
            query.Parameters.AddWithValue("$anchor", anchorId);
            query.Parameters.AddWithValue("$actor", actor);
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = rows.GetInt64(0); var direction = rows.GetString(1); var route = rows.GetString(2);
                var request = rows.GetString(3); var bytes = (byte[])rows.GetValue(4);
                if (direction is not ("request" or "response") || !Guid.TryParseExact(request, "N", out _))
                    throw new InvalidDataException($"包{id}的方向或request_id无效。");
                var identity = Convert.ToHexStringLower(SHA256.HashData(bytes)) + ":"
                    + (rows.IsDBNull(5) ? "" : rows.GetString(5)) + ":" + (rows.IsDBNull(6) ? "" : rows.GetString(6));
                var key = (request, route);
                var capture = (request, route, direction);
                if (captures.TryGetValue(capture, out var previous) && previous != identity)
                    throw new InvalidDataException($"包{id}与同一请求关联的{direction}内容冲突，不能合并历史。");
                captures[capture] = identity;
                digest.AppendData(Encoding.UTF8.GetBytes($"{id}\n{direction}\n{route}\n{identity}\n"));
                packetCount++;
                var root = Decode(bytes, id);
                if (direction == "request")
                {
                    if (includeStoryHistory && route.EndsWith("/shima_training_exec", StringComparison.Ordinal))
                        islandRequests[key] = Int(root, "current_turn");
                    if (includeStoryHistory && route.EndsWith("/check_event", StringComparison.Ordinal)
                        && root["single_mode_check_event_request_common"] is JsonObject choice)
                        storyRequests[key] = new(id, Int(choice, "current_turn"), Int(choice, "event_id"),
                            Int(choice, "chara_id"), Int(choice, "choice_number"));
                    if (!route.EndsWith("/exec_command", StringComparison.Ordinal)) continue;
                    var action = root["single_mode_exec_command_request_common"] as JsonObject
                        ?? throw new InvalidDataException($"动作请求{id}缺少single_mode_exec_command_request_common。");
                    requests[key] = new(id, Int(action, "current_turn"), Int(action, "command_id"), Int(action, "command_type"));
                    continue;
                }
                var data = root["data"] as JsonObject;
                var common = PioneerNormalizer.GetCommon(data, route);
                if (includeStoryHistory && route.EndsWith("/multi_race_reserve", StringComparison.Ordinal) && data is not null)
                    pendingReservations ??= PioneerGameData.ReadRaceReservations(data);
                if (common?["chara_info"] is not JsonObject current || RunId(current) != run) continue;
                if (includeStoryHistory)
                {
                    races ??= PioneerGameData.ReadRaceHistory(common);
                    reservations ??= pendingReservations ?? PioneerGameData.ReadRaceReservations(common);
                }
                var counts = Counts(data);
                var result = route.EndsWith("/exec_command", StringComparison.Ordinal) ? common["command_result"] as JsonObject : null;
                ImmutableArray<PendingStory> pending = [];
                if (includeStoryHistory && common["unchecked_event_array"] is JsonArray queue)
                    pending = [.. Objects(queue, "unchecked_event_array").Select(item =>
                    {
                        var contents = item["event_contents_info"] as JsonObject;
                        return new PendingStory(Int(item, "event_id"), Int(item, "story_id"), Int(item, "chara_id"),
                            Int(item, "play_timing"), Int(current, "playing_state"),
                            contents?["support_card_id"]?.GetValue<int>() ?? 0, contents?["tips_training_partner_id"]?.GetValue<int>() ?? 0);
                    })];
                var responseTurn = Int(current, "turn");
                var grants = includeStoryHistory && responseTurn >= anchorTurn - 2
                    ? PioneerGameData.ReadTemporarySpecialty(common, route[(route.LastIndexOf('/') + 1)..], responseTurn) : [];
                // Descending reads retain the earliest identical capture, preserving action order.
                frames[key] = new(id, route, request, responseTurn, counts,
                    result is null ? null : Int(result, "command_id"), result is null ? null : Int(result, "result_state"), pending,
                    includeStoryHistory ? PioneerGameData.ReadTrainingPartnerMasks(common) : null, grants,
                    responseTurn == anchorTurn && route.EndsWith("/check_event", StringComparison.Ordinal)
                        ? PioneerGameData.ReadGoldShipTrainingBoost(common) : null);
                if (route.EndsWith("/start", StringComparison.Ordinal) && startId == 0)
                {
                    using var first = db.CreateCommand();
                    first.Transaction = transaction;
                    first.CommandText = "SELECT MIN(id) FROM packets WHERE id<=$id AND request_id=$request AND endpoint=$endpoint AND viewer_id=$actor AND direction='response'";
                    first.Parameters.AddWithValue("$id", id); first.Parameters.AddWithValue("$request", request);
                    first.Parameters.AddWithValue("$endpoint", route); first.Parameters.AddWithValue("$actor", actor);
                    startId = (long)first.ExecuteScalar()!;
                }
                if (id == startId) break;
            }
        }
        if (startId == 0) throw new UnknownMechanismException($"包{anchorId}之前没有捕获本局开局，成功普通训练次数未知。");
        var ordered = frames.Values.OrderBy(frame => frame.Id).ToArray();
        if (ordered[0].Turn != 1 || ordered[0].Counts is not { } initial || initial.Any(value => value != 0))
            throw new UnknownMechanismException($"开局包{startId}未明确给出五项零训练次数。");
        var observed = new int[5]; var successful = new int[5];
        var actions = 0; var camps = 0; var failures = 0;
        var turn = 1;
        var stories = ImmutableArray.CreateBuilder<PioneerStoryOccurrence>();
        var unmatched = 0;
        var unseen = 63;
        var activeGrants = ImmutableArray.CreateBuilder<TemporarySupportGrant>();
        GoldShipTrainingBoost? goldShipBoost = null;
        Frame? preceding = null;
        foreach (var frame in ordered)
        {
            if (!frame.Grants.IsEmpty && preceding is { } previousFrame)
            {
                var key = (frame.RequestId, frame.Endpoint);
                var sourceTurn = requests.TryGetValue(key, out var action) ? action.Turn
                    : storyRequests.TryGetValue(key, out var choice) ? choice.Turn
                    : islandRequests.GetValueOrDefault(key, previousFrame.Turn);
                if (sourceTurn >= anchorTurn - 1 && sourceTurn <= anchorTurn)
                    activeGrants.AddRange(frame.Grants.Select(grant => grant with { SourceTurn = sourceTurn }));
            }
            if (includeStoryHistory && frame.Endpoint.EndsWith("/check_event", StringComparison.Ordinal))
            {
                if (preceding is not null && storyRequests.TryGetValue((frame.RequestId, frame.Endpoint), out var request)
                    && preceding.Id < request.Id && request.Id < frame.Id && request.Turn == preceding.Turn)
                {
                    var matched = preceding.PendingStories.Where(story => story.EventId == request.EventId
                        && story.CharacterId == request.CharacterId).Take(2).ToArray();
                    if (matched.Length == 1)
                    {
                        var story = matched[0];
                        stories.Add(new(story.StoryId, request.Turn, story.Phase, story.Timing, story.CharacterId,
                            story.SupportCardId, story.PartnerId, request.Choice));
                        if (story.StoryId == 501007525 && request is { Choice: 0 } && request.Turn == frame.Turn
                            && preceding.PendingStories[0] == story && frame.GoldShipSignal is { } boost)
                            goldShipBoost = boost;
                    }
                    else unmatched++;
                }
                else unmatched++;
            }
            if (frame.Command is { } command && command is (101 or 105 or 102 or 103 or 106 or >= 3601 and <= 3605))
            {
                if (!requests.TryGetValue((frame.RequestId, frame.Endpoint), out var request) || request.Id >= frame.Id)
                    throw new UnknownMechanismException($"训练响应{frame.Id}缺少之前配对的动作请求。");
                if (request.Type != 1 || request.Command != command || request.Turn != turn)
                    throw new UnknownMechanismException($"训练响应{frame.Id}的动作或回合与请求及前态不一致。");
                if (frame.Result is not (1 or 2) || frame.Counts is null)
                    throw new UnknownMechanismException($"训练响应{frame.Id}缺少已识别的成功/失败结果或五项次数。");
                var index = TrainingBaseTable.CommandIndex(command);
                observed[index]++;
                actions++;
                if (command >= 3601) camps++;
                if (frame.Result == 1) failures++;
                else
                {
                    if (command < 3601) successful[index]++;
                    if (includeStoryHistory)
                        unseen = preceding?.PartnerMasks is { } masks ? unseen & ~(masks >> (index * 6) & 63) : 0;
                }
            }
            if (frame.Counts is { } current && !current.AsSpan().SequenceEqual(observed))
                throw new UnknownMechanismException($"包{frame.Id}的训练次数与此前配对动作不一致，存在断流、重放或未识别训练。");
            turn = frame.Turn;
            preceding = frame;
        }
        if (Counts(anchor) is not { } last || !last.AsSpan().SequenceEqual(observed))
            throw new UnknownMechanismException("锚点五项训练次数缺失或与恢复历史不一致。");
        return new(payload, endpoint, [.. successful], new(startId, anchorId, packetCount, actions, camps, failures,
            Convert.ToHexStringLower(digest.GetHashAndReset())), includeStoryHistory ? stories.ToImmutable() : null, unmatched, unseen,
            [.. activeGrants.OrderBy(grant => grant.SourceTurn)], goldShipBoost, races, reservations);
    }

    static JsonObject Decode(byte[] bytes, long id)
    {
        try { return PacketAudit.Decode(bytes); }
        catch (MessagePackSerializationException error) { throw new InvalidDataException($"包{id}不是有效MessagePack对象。", error); }
    }

    static long RunId(JsonObject chara) => chara["single_mode_chara_id"] is JsonValue value && value.TryGetValue<long>(out var id) && id > 0
        ? id : throw new InvalidDataException("历史帧缺少single_mode_chara_id整数。");

    static int[]? Counts(JsonObject? data)
    {
        if (data?["pioneer_data_set"] is null) return null;
        if (data["pioneer_data_set"] is not JsonObject pioneer) throw new InvalidDataException("历史帧的pioneer_data_set必须为对象。");
        return PioneerGameData.ReadTrainingExecutionCounts(pioneer);
    }
}
