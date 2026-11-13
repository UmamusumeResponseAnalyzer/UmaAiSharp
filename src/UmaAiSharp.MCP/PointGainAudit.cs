using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;

namespace UmaAiSharp.MCP.Evidence;

public sealed record PointGainInput(int CommandId, int Participants, int CheckpointBonusPercent, bool FriendshipTraining);
public sealed record PointGainPreview(string Source, long PacketId, string RunKey, int Turn,
    PointGainInput Input, int Predicted, int Observed, bool Matches);
public sealed record PointGainAuditReport(string SchemaVersion, string RuleVersion, IReadOnlyList<SourceFingerprint> Inputs,
    int Previews, int ExactMatches, IReadOnlyDictionary<string, int> Coverage, IReadOnlyList<EvidenceIssue> Exclusions,
    IReadOnlyList<string> Limitations, string Status);

public static class PointGainAudit
{
    public static PointGainAuditReport Run(string inputDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationsPath = Path.Combine(inputDirectory, "pioneer-observations.jsonl");
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("发展 Pt 核验输出目录非空，拒绝覆盖。");
        var fingerprints = new[] { Fingerprint(observationsPath, "observations"), Fingerprint(masterPath, "master") };
        var cards = new Dictionary<int, (int Type, int Command)>();
        var npcs = new HashSet<int>();
        var characters = new HashSet<int>();
        var bonuses = new Dictionary<(int Checkpoint, int Result), int>();
        using (var connection = EvidenceAudit.OpenReadOnly(masterPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,support_card_type,command_id FROM support_card_data";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) cards.Add(reader.GetInt32(0), (reader.GetInt32(1), reader.GetInt32(2)));
            command.CommandText = "SELECT partner_id FROM single_mode_unique_chara WHERE scenario_id=11";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) npcs.Add(reader.GetInt32(0));
            command.CommandText = "SELECT id FROM chara_data";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) characters.Add(reader.GetInt32(0));
            command.CommandText = "SELECT check_point_type,result_state,effect_value_2 FROM single_mode_11_check_point_eff WHERE effect_type=202 AND effect_value_1=9";
            using (var reader = command.ExecuteReader())
                while (reader.Read()) bonuses.Add((reader.GetInt32(0), reader.GetInt32(1)), reader.GetInt32(2));
        }
        Directory.CreateDirectory(output);
        using var writer = new StreamWriter(new FileStream(Path.Combine(output, "point-gain-previews.jsonl"), FileMode.CreateNew, FileAccess.Write));
        var coverage = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var exclusions = new List<EvidenceIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previews = 0;
        var matches = 0;
        void Count(string key) => coverage[key] = coverage.GetValueOrDefault(key) + 1;
        foreach (var line in File.ReadLines(observationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("发展 Pt 核验观测不能为 null。");
            var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
            if (common?["chara_info"] is not JsonObject chara || common["home_info"] is not JsonObject home
                || observation.Visible["pioneer_data_set"] is not JsonObject pioneer)
            { Count("MissingFullSnapshot"); continue; }
            try
            {
                if (observation.RunKey is null) throw new InvalidDataException("缺少明确育成身份。");
                var turn = Int(chara, "turn");
                var results = Array(pioneer, "check_point_info_array").ToDictionary(x => Int(x, "check_point_type"), x => Int(x, "result_state"));
                var checkpoint = results.Count == 0 ? 0 : results.Keys.Max();
                if (checkpoint == 5) { Count("AfterLastCheckpointNotValidated"); continue; }
                if (checkpoint != 0 && !bonuses.ContainsKey((checkpoint, results[checkpoint])))
                    throw new UnknownMechanismException($"评审 {checkpoint}/{results[checkpoint]} 缺少发展 Pt 加成解释。");
                var bonus = checkpoint == 0 ? 0 : bonuses[(checkpoint, results[checkpoint])];
                var points = Array(pioneer, "pioneer_point_gain_info_array")
                    .ToDictionary(x => (Int(x, "command_type"), Int(x, "command_id")), x => Int(x, "gain_num"));
                var deck = Array(chara, "support_card_array").ToDictionary(x => Int(x, "position"), x => Int(x, "support_card_id"));
                var bonds = Array(chara, "evaluation_info_array").ToDictionary(x => Int(x, "target_id"), x => Int(x, "evaluation"));
                var guests = Array(pioneer, "evaluation_info_array").ToDictionary(x => Int(x, "target_id"), x => Int(x, "chara_id"));
                foreach (var command in Array(home, "command_info_array"))
                {
                    var id = Int(command, "command_id");
                    if (Int(command, "command_type") != 1 || Int(command, "is_enable") != 1) continue;
                    if (id is not (101 or 102 or 103 or 105 or 106)) { Count("NonOrdinaryTrainingNotValidated"); continue; }
                    if (!points.TryGetValue((1, id), out var observed)) { Count("NoPointPreview"); continue; }
                    try
                    {
                        if (command["training_partner_array"] is not JsonArray partners)
                            throw new InvalidDataException("缺少 training_partner_array。");
                        var ids = partners.Select(x => x?.GetValue<int>() ?? throw new InvalidDataException("伙伴编号不能为 null。")).ToArray();
                        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("训练伙伴编号重复。");
                        var participants = 0;
                        var friendship = false;
                        foreach (var position in ids)
                        {
                            if (deck.TryGetValue(position, out var cardId))
                            {
                                if (!cards.TryGetValue(cardId, out var card) || card.Type is not (1 or 2))
                                    throw new UnknownMechanismException($"支援卡 {cardId} 的友情资格尚未解释。");
                                if (!bonds.TryGetValue(position, out var bond)) throw new InvalidDataException($"缺少位置 {position} 的羁绊。");
                                participants++;
                                friendship |= card.Type == 1 && card.Command == id && bond >= 80;
                            }
                            else if (!npcs.Contains(position))
                            {
                                if (!guests.TryGetValue(position, out var character) || character != position || !characters.Contains(character))
                                    throw new UnknownMechanismException($"未识别训练伙伴 {position} 的人数分类。");
                                participants++;
                            }
                        }
                        var input = new PointGainInput(id, participants, bonus, friendship);
                        // Include partner identities and the observed value so contradictory previews survive deduplication.
                        var identity = JsonSerializer.Serialize(new { observation.RunKey, turn, input, Partners = ids, Observed = observed });
                        if (!seen.Add(identity)) { Count("RepeatedPreview"); continue; }
                        var predicted = PioneerPointGain.OrdinaryTraining(participants, bonus, friendship);
                        previews++;
                        if (predicted == observed) matches++;
                        Count("Command:" + id);
                        Count("Participants:" + participants);
                        Count("CheckpointBonus:" + bonus);
                        Count("Friendship:" + friendship);
                        writer.WriteLine(JsonSerializer.Serialize(new PointGainPreview(observation.Source, observation.PacketId,
                            observation.RunKey, turn, input, predicted, observed, predicted == observed), EvidenceJson.LineOptions));
                    }
                    catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or InvalidOperationException)
                    {
                        exclusions.Add(new("PointGainInputUnsupported", observation.Source, observation.PacketId, $"command={id}：{error.Message}"));
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or InvalidOperationException)
            {
                exclusions.Add(new("PointGainSnapshotUnsupported", observation.Source, observation.PacketId, error.Message));
            }
        }
        foreach (var (path, alias) in new[] { (observationsPath, "observations"), (masterPath, "master") })
            if (Fingerprint(path, alias) != fingerprints.Single(x => x.Alias == alias))
                throw new InvalidDataException("发展 Pt 核验期间输入发生变化，报告不能作为稳定证据。");
        var report = new PointGainAuditReport("1", PioneerPointGain.Version, fingerprints, previews, matches, coverage, exclusions,
            ["OrdinaryTrainingPreviewsOnly", "NoIslandTrainingOrRaceRuleValidation", "NoPlacementOrFriendshipProbabilityValidation",
                "NoTurnAdvancementOrPointSettlement", "GroupSupportCardsUnsupported", "CorpusUsedForHypothesisCheckingNotIndependentAcceptance"],
            previews == 0 ? "NoEligibleSamples" : matches == previews ? "ObservedPreviewsMatched" : "CounterexamplesFound");
        using var reportStream = new FileStream(Path.Combine(output, "point-gain-audit.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(reportStream, report, EvidenceJson.Options);
        return report;
    }

    static IEnumerable<JsonObject> Array(JsonObject value, string key) => value[key] is JsonArray array
        ? array.Select(x => x as JsonObject ?? throw new InvalidDataException($"{key} 项必须为对象。"))
        : throw new InvalidDataException($"缺少 {key} 数组。");
    static int Int(JsonObject value, string key) => value[key]?.GetValue<int>() ?? throw new InvalidDataException($"缺少整数 {key}。");
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "PointGainAuditInput", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
