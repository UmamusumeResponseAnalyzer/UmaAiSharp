using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;

namespace UmaAiSharp.MCP.Evidence;

public static class PacketAudit
{
    const string PioneerPrefix = "/umamusume/single_mode_pioneer/";
    static readonly string[] RequiredColumns =
        ["id", "captured_at_utc", "direction", "endpoint", "request_id", "app_ver", "res_ver", "viewer_id", "payload"];

    public static PacketInventory Read(IReadOnlyList<(string Path, string Alias)> sources,
        TextWriter exchanges, TextWriter observations, TextWriter actions, List<EvidenceIssue> issues, CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<(string RequestId, string Endpoint), Exchange>();
        var endpoints = new Dictionary<(string Endpoint, string Direction), long>();
        var domains = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var trajectories = new Dictionary<string, Trajectory>(StringComparer.Ordinal);
        var appVersions = new SortedSet<string>(StringComparer.Ordinal);
        var resourceVersions = new SortedSet<string>(StringComparer.Ordinal);
        var decoded = new HashSet<(string RequestId, string Endpoint, string Direction, string CaptureIdentity)>();
        var packetCount = 0L;
        var pioneerCount = 0L;
        var observationCount = 0;
        var decodingFailures = 0;
        foreach (var (path, alias) in sources)
        {
            using var connection = EvidenceAudit.OpenReadOnly(path);
            using var transaction = connection.BeginTransaction(deferred: true);
            ValidateSchema(connection, transaction, alias);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id,direction,endpoint,request_id,app_ver,res_ver,payload,viewer_id,captured_at_utc FROM packets ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                packetCount++;
                var id = reader.GetInt64(0);
                var direction = reader.GetString(1);
                var endpoint = reader.GetString(2);
                var requestId = reader.GetString(3);
                var appVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
                var resourceVersion = reader.IsDBNull(5) ? null : reader.GetString(5);
                var payload = (byte[])reader.GetValue(6);
                var actorKey = reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7))
                    ? null : PioneerNormalizer.Hash("viewer\n" + reader.GetString(7));
                var capturedAt = reader.GetString(8);
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
                var captureIdentity = JsonSerializer.Serialize(new { hash, actorKey, appVersion, resourceVersion });
                var reference = new PacketReference(alias, id);
                var endpointKey = (endpoint, direction);
                endpoints[endpointKey] = endpoints.GetValueOrDefault(endpointKey) + 1;
                if (direction is not ("request" or "response"))
                {
                    issues.Add(new("InvalidDirection", alias, id, "direction不是request或response；该记录未配对。"));
                    continue;
                }
                if (!Guid.TryParseExact(requestId, "N", out _))
                {
                    issues.Add(new("InvalidRequestId", alias, id, "request_id不是捕获格式规定的GUID N；该记录未配对。"));
                    continue;
                }
                var key = (requestId, endpoint);
                if (!groups.TryGetValue(key, out var group))
                    groups.Add(key, group = new(PioneerNormalizer.Hash(requestId + "\n" + endpoint), endpoint));
                group.Add(direction, reference, hash, captureIdentity, actorKey, appVersion, resourceVersion);
                if (!endpoint.StartsWith(PioneerPrefix, StringComparison.Ordinal)) continue;
                pioneerCount++;
                if (appVersion is not null) appVersions.Add(appVersion);
                if (resourceVersion is not null) resourceVersions.Add(resourceVersion);
                if (string.IsNullOrWhiteSpace(appVersion) || string.IsNullOrWhiteSpace(resourceVersion))
                    issues.Add(new("MissingPacketVersion", alias, id, "Pioneer包缺少app_ver或res_ver。"));
                // Only duplicate captures of the same exchange are collapsed. Repeated real requests remain distinct.
                if (!decoded.Add((requestId, endpoint, direction, captureIdentity))) continue;
                JsonObject root;
                try
                {
                    root = Decode(payload);
                }
                catch (Exception error) when (error is MessagePackSerializationException or JsonException
                    or InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    decodingFailures++;
                    issues.Add(new("DecodeFailure", alias, id, $"原始正文无法解码为MessagePack对象（{error.GetType().Name}）。"));
                    continue;
                }
                CollectDomains(root, "$", domains);
                if (direction == "request")
                {
                    actions.WriteLine(JsonSerializer.Serialize(new ObservedAction(alias, id, group.Key, endpoint, capturedAt,
                        PioneerNormalizer.ActionFields(root)), EvidenceJson.LineOptions));
                    continue;
                }
                PioneerObservation observation;
                try
                {
                    observation = PioneerNormalizer.Normalize(root, alias, id, group.Key, endpoint, actorKey, capturedAt);
                }
                catch (InvalidOperationException)
                {
                    issues.Add(new("InvalidObservationShape", alias, id, "已知观测字段的对象/数组结构不符合协议；未猜测修复。"));
                    continue;
                }
                observations.WriteLine(JsonSerializer.Serialize(observation, EvidenceJson.LineOptions));
                observationCount++;
                foreach (var (name, field) in observation.Fields)
                    if (field.Status == "InvalidType")
                        issues.Add(new("InvalidScalarType", alias, id, $"chara_info.{name}不是整数。"));
                if (observation.RunKey is not { } runKey)
                {
                    issues.Add(new("UnassociatedObservation", alias, id, "缺少捕获viewer_id或有效single_mode_chara_id，不能归入育成轨迹。"));
                    continue;
                }
                if (!trajectories.TryGetValue(runKey, out var trajectory))
                    trajectories.Add(runKey, trajectory = new(runKey));
                trajectory.Add(observation, appVersion, resourceVersion);
            }
        }

        var paired = 0;
        var orphans = 0;
        var conflicts = 0;
        var duplicates = 0;
        foreach (var group in groups.Values.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = group.Summarize();
            exchanges.WriteLine(JsonSerializer.Serialize(summary, EvidenceJson.LineOptions));
            duplicates += summary.DuplicateCaptures;
            if (summary.Status == "Paired") paired++;
            else if (summary.Status == "ConflictingCapture") conflicts++;
            else orphans++;
            if (summary.Status != "Paired")
                issues.Add(new(summary.Status, "exchange:" + summary.Key, null,
                    $"{summary.Endpoint}；不同请求正文{summary.DistinctRequestBodies}，不同响应正文{summary.DistinctResponseBodies}；冲突也可能来自参与者身份元数据。"));
            if (summary.AppVersions.Count > 1 || summary.ResourceVersions.Count > 1)
                issues.Add(new("ExchangeVersionConflict", "exchange:" + summary.Key, null, "同一请求关联的捕获版本不一致。"));
        }
        var trajectorySummaries = trajectories.Values.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => value.Summarize(issues)).ToArray();
        foreach (var trajectory in trajectorySummaries)
        {
            if (!trajectory.HasStartExchange)
                issues.Add(new("StartNotObserved", trajectory.RunKey, null, "该轨迹未观察到可关联的start响应；不能声明从开局完整捕获。"));
            if (!trajectory.HasFinishExchange)
                issues.Add(new("FinishNotObserved", trajectory.RunKey, null, "该轨迹未观察到可关联的finish响应；不能声明终局捕获完整。"));
        }
        return new(packetCount, pioneerCount, groups.Count, paired, orphans, conflicts, duplicates, decodingFailures,
            observationCount, appVersions.ToArray(), resourceVersions.ToArray(),
            endpoints.OrderBy(pair => pair.Key.Endpoint, StringComparer.Ordinal).ThenBy(pair => pair.Key.Direction, StringComparer.Ordinal)
                .Select(pair => new EndpointCount(pair.Key.Endpoint, pair.Key.Direction, pair.Value)).ToArray(),
            domains.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PacketDomain(pair.Key, "Uninterpreted", pair.Value.OrderBy(value => value.Key, StringComparer.Ordinal)
                    .Select(value => new ValueCount(value.Key, value.Value)).ToArray())).ToArray(), trajectorySummaries);
    }

    public static JsonObject Decode(ReadOnlyMemory<byte> payload)
    {
        var json = MessagePackSerializer.ConvertToJson(payload, MessagePackSerializerOptions.Standard
            .WithSecurity(MessagePackSecurity.UntrustedData));
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("MessagePack根不是对象。");
    }

    static void ValidateSchema(SqliteConnection connection, SqliteTransaction transaction, string alias)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(packets)";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) columns.Add(reader.GetString(1));
        var missing = RequiredColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{alias}不是受支持的DebugSaveAllPackets数据库，缺列：{string.Join(", ", missing)}。");
    }

    static void CollectDomains(JsonNode? node, string path, Dictionary<string, Dictionary<string, long>> domains)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) CollectDomains(item, path + "[]", domains);
            return;
        }
        if (node is not JsonObject map) return;
        foreach (var (name, value) in map)
        {
            var childPath = path + "." + name;
            if (value is JsonValue && (name.Contains("effect", StringComparison.OrdinalIgnoreCase)
                || name.Contains("condition", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_type", StringComparison.Ordinal) || name == "result_state"))
            {
                if (!domains.TryGetValue(childPath, out var values)) domains.Add(childPath, values = new(StringComparer.Ordinal));
                var text = value.ToJsonString();
                values[text] = values.GetValueOrDefault(text) + 1;
            }
            CollectDomains(value, childPath, domains);
        }
    }

    sealed class Exchange(string key, string endpoint)
    {
        public string Key { get; } = key;
        readonly List<PacketReference> requests = [];
        readonly List<PacketReference> responses = [];
        readonly HashSet<string> requestBodies = new(StringComparer.Ordinal);
        readonly HashSet<string> responseBodies = new(StringComparer.Ordinal);
        readonly HashSet<string> requestCaptures = new(StringComparer.Ordinal);
        readonly HashSet<string> responseCaptures = new(StringComparer.Ordinal);
        readonly HashSet<string> actors = new(StringComparer.Ordinal);
        readonly SortedSet<string> apps = new(StringComparer.Ordinal);
        readonly SortedSet<string> resources = new(StringComparer.Ordinal);

        public void Add(string direction, PacketReference reference, string hash, string captureIdentity,
            string? actor, string? app, string? resource)
        {
            (direction == "request" ? requests : responses).Add(reference);
            (direction == "request" ? requestBodies : responseBodies).Add(hash);
            (direction == "request" ? requestCaptures : responseCaptures).Add(captureIdentity);
            if (actor is not null) actors.Add(actor);
            if (app is not null) apps.Add(app);
            if (resource is not null) resources.Add(resource);
        }

        public ExchangeSummary Summarize()
        {
            var status = requestBodies.Count > 1 || responseBodies.Count > 1 || actors.Count > 1 ? "ConflictingCapture"
                : requests.Count == 0 ? "ResponseOrphan" : responses.Count == 0 ? "RequestOrphan" : "Paired";
            return new(Key, endpoint, status, requests, responses, requestBodies.Count, responseBodies.Count,
                requests.Count + responses.Count - requestCaptures.Count - responseCaptures.Count, apps.ToArray(), resources.ToArray());
        }
    }

    sealed class Trajectory(string key)
    {
        public string Key { get; } = key;
        readonly List<ObservationPosition> observations = [];
        readonly SortedSet<string> appVersions = new(StringComparer.Ordinal);
        readonly SortedSet<string> resourceVersions = new(StringComparer.Ordinal);

        public void Add(PioneerObservation observation, string? appVersion, string? resourceVersion)
        {
            observations.Add(new(observation.Source, observation.PacketId, observation.Endpoint,
                observation.CapturedAtUtc, observation.Fields["turn"].Value));
            if (appVersion is not null) appVersions.Add(appVersion);
            if (resourceVersion is not null) resourceVersions.Add(resourceVersion);
        }

        public TrajectorySummary Summarize(List<EvidenceIssue> issues)
        {
            if (appVersions.Count > 1 || resourceVersions.Count > 1)
                issues.Add(new("TrajectoryVersionConflict", Key, null,
                    "同一育成轨迹观察到多个app_ver或res_ver；不能作为单一版本的规则轨迹。"));
            var hasStart = observations.Any(value => value.Endpoint.EndsWith("/start", StringComparison.Ordinal));
            var hasFinish = observations.Any(value => value.Endpoint.EndsWith("/finish", StringComparison.Ordinal));
            var crossSource = observations.Select(value => value.Source).Distinct(StringComparer.Ordinal).Skip(1).Any();
            var stamps = observations.Select(value => (Observation: value, Time: CaptureTime(value.CapturedAtUtc))).ToArray();
            var ambiguous = false;
            if (crossSource)
            {
                ambiguous = stamps.Any(value => value.Time is null)
                    || stamps.GroupBy(value => value.Time).Any(group => group.Select(value => value.Observation.Source)
                        .Distinct(StringComparer.Ordinal).Skip(1).Any());
                foreach (var source in stamps.GroupBy(value => value.Observation.Source))
                {
                    DateTimeOffset? previous = null;
                    foreach (var stamp in source.OrderBy(value => value.Observation.PacketId))
                    {
                        if (stamp.Time < previous) ambiguous = true;
                        previous = stamp.Time;
                    }
                }
            }
            if (ambiguous)
            {
                issues.Add(new("TrajectoryOrderUnverified", Key, null,
                    "跨库轨迹缺少可排序捕获时间、时间发生倒退或存在跨库同刻观测；不按文件名或回合值猜测顺序。"));
                return new(Key, observations.Count, null, null, hasStart, hasFinish, 0, 0, "Unverified",
                    appVersions.ToArray(), resourceVersions.ToArray());
            }
            var ordered = crossSource ? stamps.OrderBy(value => value.Time).Select(value => value.Observation)
                : observations.OrderBy(value => value.PacketId);
            long? firstTurn = null;
            long? lastTurn = null;
            var gaps = 0;
            var regressions = 0;
            foreach (var observation in ordered)
            {
                if (observation.Turn is not { } turn) continue;
                firstTurn ??= turn;
                if (lastTurn is { } previous && turn > previous + 1)
                {
                    gaps++;
                    issues.Add(new("TurnJump", observation.Source, observation.PacketId,
                        $"观测回合从{previous.ToString(CultureInfo.InvariantCulture)}跳至{turn.ToString(CultureInfo.InvariantCulture)}；需核验缺包或自动推进，不能直接认定轨迹完整。"));
                }
                else if (lastTurn is { } prior && turn < prior)
                {
                    regressions++;
                    issues.Add(new("TurnRegression", observation.Source, observation.PacketId, $"观测回合从{prior}退回{turn}；未按回合值重排。"));
                }
                lastTurn = turn;
            }
            return new(Key, observations.Count, firstTurn, lastTurn, hasStart, hasFinish, gaps, regressions,
                crossSource ? "CapturedTimeAcrossSources" : "SourceRowId", appVersions.ToArray(), resourceVersions.ToArray());
        }

        static DateTimeOffset? CaptureTime(string text)
        {
            var hasOffset = text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
            return hasOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time : null;
        }

        sealed record ObservationPosition(string Source, long PacketId, string Endpoint, string CapturedAtUtc, long? Turn);
    }
}
