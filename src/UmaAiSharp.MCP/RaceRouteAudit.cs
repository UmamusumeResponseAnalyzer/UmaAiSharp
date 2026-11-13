using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Races;

namespace UmaAiSharp.MCP.Evidence;

public sealed record RaceRouteObservation(string Source, long PacketId, string RunKey, int Turn, int CardId,
    int RouteId, IReadOnlyList<int> SelectedIds, IReadOnlyList<RaceGoal> Goals, IReadOnlyList<int> UnselectedMasterIds);
public sealed record RaceRouteAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<EvidenceIssue> Issues, IReadOnlyList<string> Limitations, string Status);

public static class RaceRouteAudit
{
    public static RaceRouteAuditReport Run(string inputDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationsPath = Path.Combine(inputDirectory, "pioneer-observations.jsonl");
        var exchangesPath = Path.Combine(inputDirectory, "exchanges.jsonl");
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("比赛路线核验输出目录非空，拒绝覆盖。");
        var sources = new[] { (observationsPath, "observations"), (exchangesPath, "exchanges"), (masterPath, "master") };
        var fingerprints = sources.Select(x => Fingerprint(x.Item1, x.Item2)).ToArray();
        var exchanges = File.ReadLines(exchangesPath).Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("请求响应配对记录不能为 null。"))
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var routes = new List<RaceRoute>();
        var goals = new List<RaceGoal>();
        var cards = new Dictionary<int, int>();
        var groups = new HashSet<int>();
        using (var connection = EvidenceAudit.OpenReadOnly(masterPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,scenario_id,chara_id,race_set_id,condition_set_id,priority FROM single_mode_route";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) routes.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
            command.CommandText = """
                SELECT id,race_set_id,scenario_group_id,target_type,sort_id,turn,race_type,condition_type,condition_id,
                    condition_value_1,condition_value_2,determine_race,determine_race_flag,alt_determine_race,
                    alt_determine_race_flag,determine_race_for_generate FROM single_mode_route_race
                """;
            using (var reader = command.ExecuteReader())
                while (reader.Read()) goals.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12),
                    reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15)));
            command.CommandText = "SELECT id,chara_id FROM card_data";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) cards.Add(reader.GetInt32(0), reader.GetInt32(1));
            command.CommandText = "SELECT group_id FROM single_mode_scenario_group WHERE scenario_id=11";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) groups.Add(reader.GetInt32(0));
        }
        var catalog = new RaceRouteCatalog(routes, goals, cards, groups);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lastState = new Dictionary<string, string>(StringComparer.Ordinal);
        var runs = new HashSet<string>(StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        using var writer = new StreamWriter(new FileStream(Path.Combine(output, "race-route-observations.jsonl"), FileMode.CreateNew, FileAccess.Write));
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var line in File.ReadLines(observationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("比赛路线观测不能为 null。");
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint || !exchange.Responses.Contains(new(observation.Source, observation.PacketId)))
            { Count("ExchangeNotPaired"); continue; }
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (common?["chara_info"] is not JsonObject chara) { Count("NoCharacterSnapshot"); continue; }
            try
            {
                if (observation.RunKey is null) throw new InvalidDataException("缺少明确育成身份。");
                if (Int(chara, "scenario_id") != 11) throw new InvalidDataException("比赛路线观测不属于 Pioneer。");
                var cardId = Int(chara, "card_id");
                var routeId = Int(chara, "route_id");
                var turn = Int(chara, "turn");
                if (chara["route_race_id_array"] is not JsonArray selected)
                    throw new InvalidDataException("缺少 route_race_id_array。");
                var ids = selected.Select(x => x?.GetValue<int>() ?? throw new InvalidDataException("比赛目标编号不能为 null。")).ToArray();
                var resolved = catalog.ResolveSelected(cardId, routeId, ids);
                var identity = JsonSerializer.Serialize(new { observation.RunKey, cardId, routeId, ids });
                if (lastState.TryGetValue(observation.RunKey, out var previous) && previous != identity)
                    Count("RouteStateTransitionsInInputOrder");
                lastState[observation.RunKey] = identity;
                if (!seen.Add(identity)) { Count("RepeatedRouteState"); continue; }
                Count("DistinctRouteStates");
                runs.Add(observation.RunKey);
                var all = goals.Where(x => x.RaceSetId == catalog.Routes[routeId].RaceSetId && groups.Contains(x.ScenarioGroupId))
                    .OrderBy(x => x.SortId).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
                var unselected = all.Except(ids).ToArray();
                Count("MappedRouteStates");
                Count(ids.SequenceEqual(all) ? "EqualsAllMasterRows" : "RequiresSelectedGoalState");
                foreach (var goal in resolved) Count("GoalConditionType:" + goal.ConditionType);
                writer.WriteLine(JsonSerializer.Serialize(new RaceRouteObservation(observation.Source, observation.PacketId,
                    observation.RunKey, turn, cardId, routeId, ids, resolved, unselected), EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or InvalidOperationException)
            {
                Count("RejectedFrames");
                issues.Add(new("RouteSnapshotUnsupported", observation.Source, observation.PacketId, error.Message));
            }
        }
        counts["Runs"] = runs.Count;
        foreach (var (path, alias) in sources)
            if (Fingerprint(path, alias) != fingerprints.Single(x => x.Alias == alias))
                throw new InvalidDataException("比赛路线核验期间输入发生变化。");
        var report = new RaceRouteAuditReport("1", fingerprints, counts, issues,
            ["SelectedGoalMappingOnly", "ConditionalRouteSelectionNotImplemented", "GoalSuccessConditionsNotInterpreted",
                "OptionalRaceEligibilityAndRewardsNotImplemented", "RaceOutcomeDistributionNotValidated"],
            issues.Count == 0 && counts.GetValueOrDefault("MappedRouteStates") > 0 ? "ObservedMappingsMatched" : "MappingIncomplete");
        using var stream = new FileStream(Path.Combine(output, "race-route-audit.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, report, EvidenceJson.Options);
        return report;
    }

    static int Int(JsonObject value, string key) => value[key]?.GetValue<int>() ?? throw new InvalidDataException($"缺少整数 {key}。");
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "RaceRouteAuditInput", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
