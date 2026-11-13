using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MessagePack;
using UmaAiSharp.MCP.Evidence;
using UmaAiSharp.Core;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.MCP;

public sealed record EventRewardProvenance(long StatePacketId, long RequestPacketId, long RewardPacketId,
    int CapturedPackets, string WindowSha256);
public sealed record CapturedPioneerEvent(byte[] Payload, string Endpoint, JsonArray Rewards, EventRewardProvenance History);

public static class PioneerEventCapture
{
    public static CapturedPioneerEvent? Read(string path, long anchorId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorId);
        using var db = EvidenceAudit.OpenReadOnly(path);
        using var transaction = db.BeginTransaction(deferred: true);
        string endpoint, requestId, actor;
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT direction,endpoint,request_id,viewer_id FROM packets WHERE id=$id";
            query.Parameters.AddWithValue("$id", anchorId);
            using var row = query.ExecuteReader();
            if (!row.Read() || row.GetString(0) != "response" || row.IsDBNull(3))
                throw new InvalidDataException($"包{anchorId}不是带明确viewer_id的响应。");
            endpoint = row.GetString(1);
            if (!endpoint.EndsWith("/get_choice_reward", StringComparison.Ordinal)) return null;
            if (endpoint is not ("/single_mode/get_choice_reward" or "/umamusume/single_mode/get_choice_reward"
                or "/single_mode_pioneer/get_choice_reward" or "/umamusume/single_mode_pioneer/get_choice_reward"))
                throw new InvalidDataException("奖励响应需要实际育成端点路径。");
            requestId = row.GetString(2); actor = row.GetString(3);
        }
        if (string.IsNullOrWhiteSpace(actor)) throw new InvalidDataException("奖励响应viewer_id为空，不能关联当前事件。");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var captures = new Dictionary<(string Request, string Endpoint, string Direction), string>();
        var rewardRequests = new List<int>();
        JsonArray? rewards = null;
        long requestPacketId = 0;
        var packetCount = 0;
        using var window = db.CreateCommand();
        window.Transaction = transaction;
        window.CommandText = """
            SELECT id,direction,endpoint,request_id,payload,app_ver,res_ver FROM packets
            WHERE id<=$anchor AND viewer_id=$actor AND endpoint LIKE '%/single_mode%/%' ORDER BY id DESC
            """;
        window.Parameters.AddWithValue("$anchor", anchorId);
        window.Parameters.AddWithValue("$actor", actor);
        using var rows = window.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = rows.GetInt64(0); var direction = rows.GetString(1); var route = rows.GetString(2);
            var request = rows.GetString(3); var payload = (byte[])rows.GetValue(4);
            if (direction is not ("request" or "response") || !Guid.TryParseExact(request, "N", out _))
                throw new InvalidDataException($"包{id}的方向或request_id无效。");
            var identity = Convert.ToHexStringLower(SHA256.HashData(payload)) + ":"
                + (rows.IsDBNull(5) ? "" : rows.GetString(5)) + ":" + (rows.IsDBNull(6) ? "" : rows.GetString(6));
            var key = (request, route, direction);
            if (captures.TryGetValue(key, out var previous) && previous != identity)
                throw new InvalidDataException($"包{id}与同一请求关联的{direction}内容冲突，不能合并奖励窗口。");
            captures[key] = identity;
            digest.AppendData(Encoding.UTF8.GetBytes($"{id}\n{direction}\n{route}\n{identity}\n"));
            packetCount++;
            JsonObject root;
            try { root = PacketAudit.Decode(payload); }
            catch (MessagePackSerializationException error) { throw new InvalidDataException($"包{id}不是有效MessagePack对象。", error); }
            if (route.EndsWith("/get_choice_reward", StringComparison.Ordinal))
            {
                if (direction == "request")
                {
                    rewardRequests.Add(Int(root, "event_id"));
                    if (request == requestId && route == endpoint) requestPacketId = id;
                }
                else if (id == anchorId)
                    rewards = Array(root["data"] as JsonObject ?? throw new InvalidDataException($"奖励包{id}缺少data对象。"), "choice_reward_array");
                continue;
            }
            if (requestPacketId == 0 || direction != "response")
                throw new UnknownMechanismException($"奖励响应{anchorId}与当前事件之间存在包{id}的未闭合动作，或缺少配对奖励请求。");
            var data = root["data"] as JsonObject;
            var common = PioneerNormalizer.GetCommon(data, route);
            var chara = common?["chara_info"] as JsonObject;
            if (chara is null)
                throw new UnknownMechanismException($"奖励响应{anchorId}之前的包{id}没有完整当前状态，不能跨过该响应关联事件。");
            if (Int(chara, "scenario_id") != 11 || Int(chara, "state") != 0)
                throw new InvalidDataException($"奖励响应{anchorId}关联的包{id}不是进行中的Pioneer育成。");
            var events = Objects(Array(common!, "unchecked_event_array"), "unchecked_event_array").ToArray();
            if (events.Length != 1 || rewardRequests.Any(eventId => eventId != Int(events[0], "event_id")))
                throw new UnknownMechanismException($"奖励响应{anchorId}的event_id与唯一当前事件不一致。");
            return new(payload, route, rewards!, new(id, requestPacketId, anchorId, packetCount,
                Convert.ToHexStringLower(digest.GetHashAndReset())));
        }
        throw new UnknownMechanismException($"奖励响应{anchorId}之前缺少当前事件及配对请求。");
    }
}
