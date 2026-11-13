using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace UmaAiSharp.MCP.Evidence;

public sealed record PlacementCard(int Position, int CardId, int Level, int CardType, int SpecialtyCommand,
    int PanelSpecialty, int DirectUniqueSpecialty, int PanelHint, int DirectUniqueHint,
    IReadOnlyList<int> ConditionalUniqueTypes, bool Introduced, IReadOnlyList<int> Commands, bool Hint);
public sealed record PlacementBoard(string RunKey, int Turn, string Mode, PacketReference Packet,
    string Endpoint, string CapturedAtUtc, IReadOnlyList<PlacementCard> Cards, JsonObject Context);
public sealed record PlacementOpportunity(string RunKey, int Turn, int Frames, int DistinctBoards,
    int DistinctContexts, IReadOnlyList<PlacementBoard> Boards);
public sealed record PlacementFrequency(int Successes, int Trials, int Runs, double? Rate,
    double? NominalBinomialWilson95Low, double? NominalBinomialWilson95High, double? RunClusterStandardError);
public sealed record PlacementBaseline(string Model, double PreferredProbability, double AbsentProbability,
    double ExpectedPreferred, double ExpectedAbsent, double ObservedMinusExpectedPreferred, double ObservedMinusExpectedAbsent);
public sealed record PlacementStratum(int CardId, int Level, string Mode, bool Introduced,
    bool DeckHasUnique122, int CardType, int PanelSpecialty, int DirectUniqueSpecialty,
    int PanelHint, int DirectUniqueHint, IReadOnlyList<int> ConditionalUniqueTypes,
    PlacementFrequency Absent, PlacementFrequency Preferred, PlacementFrequency HintPerBoard,
    PlacementFrequency HintGivenPresent, IReadOnlyList<PlacementBaseline> UnadjustedBaselines,
    double? LegacySixPercentHintExpected, string ContextStatus);
public sealed record PlacementCoverage(string Dimension, string Value, int Count);
public sealed record PlacementHintAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<PlacementStratum> Strata,
    IReadOnlyList<PlacementCoverage> Coverage, IReadOnlyList<EvidenceIssue> Issues,
    IReadOnlyList<string> ApplicabilityLimits, string Status);

public static class PlacementHintAudit
{
    static readonly int[] Ordinary = [101, 105, 102, 103, 106];
    static readonly int[] Camp = [3601, 3602, 3603, 3604, 3605];

    public static PlacementHintAuditReport Run(string inputDirectory, string masterPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("人员与Hint审计输出目录非空，拒绝覆盖已有证据。");
        var sources = new[] { (Path.Combine(inputDirectory, "pioneer-observations.jsonl"), "observations"),
            (Path.Combine(inputDirectory, "exchanges.jsonl"), "exchanges"), (masterPath, "master") };
        var fingerprints = sources.Select(source => Fingerprint(source.Item1, source.Item2)).ToArray();
        var master = new PlacementMaster(masterPath);
        var exchanges = File.ReadLines(sources[1].Item1).Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("请求响应关联记录不能为null。"))
            .ToDictionary(value => value.Key, StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var coverage = new Dictionary<(string Dimension, string Value), int>();
        var issues = new List<EvidenceIssue>();
        var boards = new Dictionary<(string Run, int Turn), List<PlacementBoard>>();
        var observedTurns = new HashSet<(string Run, int Turn)>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        void Count(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
        void Cover(string dimension, object value)
        {
            var key = (dimension, value.ToString()!);
            coverage[key] = coverage.GetValueOrDefault(key) + 1;
        }
        foreach (var line in File.ReadLines(sources[0].Item1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("育成观测不能为null。");
            Count("Observations");
            if (!exchanges.TryGetValue(observation.ExchangeKey, out var exchange) || exchange.Status != "Paired"
                || exchange.Endpoint != observation.Endpoint || !exchange.Responses.Contains(new(observation.Source, observation.PacketId)))
            { Count("ExchangeNotPaired"); continue; }
            if (observation.RunKey is null) { Count("RunIdentityMissing"); continue; }
            try
            {
                var common = PioneerNormalizer.GetCommon(observation.Visible, observation.Endpoint);
                if (common?["chara_info"] is not JsonObject chara) { Count("CharacterMissing"); continue; }
                if (Int(chara, "scenario_id") != 11) throw new InvalidDataException("人员与 Hint 观测不属于 Pioneer。");
                var turn = Int(chara, "turn");
                if (turn < 1) throw new InvalidDataException("回合必须为正整数。");
                observedTurns.Add((observation.RunKey, turn));
                if (common["unchecked_event_array"] is JsonArray events)
                {
                    foreach (var item in Objects(events, "unchecked_event_array"))
                    {
                        Count("PendingEventReferences");
                        // Repeated snapshots count one pending story context.
                        var contents = item["event_contents_info"] as JsonObject;
                        var identity = $"{observation.RunKey}:{turn}:{Int(chara, "playing_state")}:{Int(item, "story_id")}:{Int(item, "chara_id")}:{Int(item, "play_timing")}:{contents?["support_card_id"]}:{contents?["tips_training_partner_id"]}";
                        if (pending.Add(identity))
                        {
                            Count("DistinctPendingStoryContexts");
                            Cover("PendingEventPlayTiming", Int(item, "play_timing"));
                        }
                        else Count("RepeatedPendingEventReferences");
                    }
                }
                else Count("PendingEventArrayMissingOrNull");
                if (Int(chara, "playing_state") != 1) { Count("NotPlayingState1"); continue; }
                if (common["unchecked_event_array"] is not JsonArray { Count: 0 }) { Count("PendingOrUnobservedEvents"); continue; }
                if (common["home_info"] is not JsonObject home || home["command_info_array"] is not JsonArray commands)
                { Count("CommandsMissing"); continue; }
                var all = Objects(commands, "command_info_array").Where(command => Int(command, "command_type") == 1).ToArray();
                if (all.Select(command => Int(command, "command_id")).Distinct().Count() != all.Length)
                    throw new InvalidDataException("训练指令ID重复，不能重建完整面板。");
                var ordinary = all.Where(command => Ordinary.Contains(Int(command, "command_id"))).ToArray();
                var camp = all.Where(command => Camp.Contains(Int(command, "command_id"))).ToArray();
                var selected = ordinary.Length == 5 && camp.Length == 0 ? ordinary : camp.Length == 5 && ordinary.Length == 0 ? camp : null;
                if (selected is null) { Count("FiveTrainingSetIncompleteOrMixed"); continue; }
                if (selected.Any(command => Int(command, "is_enable") != 1 || command["params_inc_dec_info_array"] is not JsonArray))
                { Count("DisabledOrPlaceholderPreview"); continue; }
                foreach (var command in selected)
                    foreach (var parameter in Objects(Array(command, "params_inc_dec_info_array"), "params_inc_dec_info_array"))
                    { _ = Int(parameter, "target_type"); _ = Int(parameter, "value"); }
                var partnerCommands = new Dictionary<int, List<int>>();
                var hints = new HashSet<int>();
                foreach (var command in selected)
                {
                    var partners = Integers(Array(command, "training_partner_array"), "training_partner_array");
                    var tipPartners = Integers(Array(command, "tips_event_partner_array"), "tips_event_partner_array");
                    if (tipPartners.Except(partners).Any()) throw new InvalidDataException("Hint位置不在对应训练的人员数组中。");
                    foreach (var position in partners)
                    {
                        if (!partnerCommands.TryGetValue(position, out var places)) partnerCommands.Add(position, places = []);
                        places.Add(Int(command, "command_id"));
                    }
                    hints.UnionWith(tipPartners);
                }
                var deck = Objects(Array(chara, "support_card_array"), "support_card_array").ToArray();
                if (!deck.Select(card => Int(card, "position")).Order().SequenceEqual(Enumerable.Range(1, 6)))
                    throw new InvalidDataException("支援卡编成必须明确包含唯一的位置1至6。");
                var evaluations = Objects(Array(chara, "evaluation_info_array"), "evaluation_info_array")
                    .ToDictionary(row => Int(row, "target_id"));
                var cards = deck.OrderBy(card => Int(card, "position")).Select(card =>
                {
                    var position = Int(card, "position");
                    if (!evaluations.TryGetValue(position, out var evaluation)) throw new InvalidDataException("支援卡登场标志缺失。");
                    var appeared = Int(evaluation, "is_appear");
                    if (appeared is not (0 or 1)) throw new InvalidDataException("is_appear必须为0或1。");
                    var places = partnerCommands.GetValueOrDefault(position) ?? [];
                    return master.Card(card, appeared == 1, places.Order().ToArray(), hints.Contains(position));
                }).ToArray();
                var context = new JsonObject();
                foreach (var name in new[] { "gain_partner_support_effect_array", "unchecked_event_array" })
                    if (common.ContainsKey(name)) context[name] = common[name]?.DeepClone();
                foreach (var name in new[] { "chara_effect_id_array", "evaluation_info_array", "state", "scenario_id" })
                    if (chara.ContainsKey(name)) context[name] = chara[name]?.DeepClone();
                foreach (var name in new[] { "facility_info_array", "check_point_info_array", "pioneer_info", "shima_training_info" })
                    if (observation.Visible["pioneer_data_set"] is JsonObject pioneer && pioneer.ContainsKey(name))
                        context[name] = pioneer[name]?.DeepClone();
                var board = new PlacementBoard(observation.RunKey, turn, ordinary.Length == 5 ? "Ordinary" : "Camp",
                    new(observation.Source, observation.PacketId), observation.Endpoint, observation.CapturedAtUtc, cards, context);
                if (!boards.TryGetValue((observation.RunKey, turn), out var group)) boards.Add((observation.RunKey, turn), group = []);
                group.Add(board);
                Count("ReadyFrames");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException or OverflowException)
            {
                Count("UnsupportedOrMalformedFrame");
                issues.Add(new("PlacementHintInputRejected", observation.Source, observation.PacketId, error.Message));
            }
        }
        Directory.CreateDirectory(outputDirectory);
        var eligible = new List<PlacementBoard>();
        using (var writer = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "placement-opportunities.jsonl"), FileMode.CreateNew), new UTF8Encoding(false)))
        {
            foreach (var (key, frames) in boards.OrderBy(pair => pair.Key.Run, StringComparer.Ordinal).ThenBy(pair => pair.Key.Turn))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Count("ObservedReadyRunTurns");
                var distinct = frames.GroupBy(BoardIdentity, StringComparer.Ordinal).ToArray();
                var contextCount = frames.Select(frame => frame.Context.ToJsonString()).Distinct(StringComparer.Ordinal).Count();
                counts["RepeatedReadyBoardFrames"] = counts.GetValueOrDefault("RepeatedReadyBoardFrames") + frames.Count - distinct.Length;
                if (distinct.Length == 1) { Count("SingleBoardRunTurns"); eligible.Add(frames[0]); }
                else Count("MultipleBoardRunTurnsNotInBaselineComparison");
                if (contextCount > 1) Count("RunTurnsWithChangedContext");
                writer.WriteLine(JsonSerializer.Serialize(new PlacementOpportunity(key.Run, key.Turn, frames.Count, distinct.Length,
                    contextCount, frames), EvidenceJson.LineOptions));
                Cover("ReadyTurn", key.Turn);
                Cover("BoardsPerRunTurn", distinct.Length);
                foreach (var frame in distinct.Select(group => group.First()))
                {
                    Cover("Mode", frame.Mode);
                    Cover("FacilityStates", frame.Context["facility_info_array"]?.ToJsonString() ?? "MissingOrNull");
                    Cover("ObservedSupportEffectDelta", frame.Context.ContainsKey("gain_partner_support_effect_array")
                        ? frame.Context["gain_partner_support_effect_array"]?.ToJsonString() ?? "Null" : "Missing");
                    foreach (var card in frame.Cards)
                    {
                        Cover("CardIntroduction", card.Introduced ? "Introduced" : "NotIntroduced");
                        if (card.Commands.Count > 1) Count("MultiPositionCardBoards");
                        if (!card.Introduced && card.Commands.Count != 0) Count("NotIntroducedButPlacedCardBoards");
                    }
                }
            }
        }
        counts["ObservedRunTurns"] = observedTurns.Count;
        counts["ObservedRunTurnsWithoutReadyBoard"] = observedTurns.Count(key => !boards.ContainsKey(key));
        counts["RunsWithReadyBoards"] = boards.Keys.Select(key => key.Run).Distinct(StringComparer.Ordinal).Count();
        var strata = eligible.SelectMany(board => board.Cards.Select(card => (Board: board, Card: card)))
            .GroupBy(value => (value.Card.CardId, value.Card.Level, value.Board.Mode, value.Card.Introduced,
                Tucker: value.Board.Cards.Any(card => card.ConditionalUniqueTypes.Contains(122))))
            .OrderBy(group => group.Key.CardId).ThenBy(group => group.Key.Level).ThenBy(group => group.Key.Mode, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Introduced).ThenBy(group => group.Key.Tucker)
            .Select(group => Stratum(group.Select(value => (value.Board.RunKey, value.Card)).ToArray(), group.Key.Mode, group.Key.Tucker)).ToArray();
        foreach (var source in sources)
            if (Fingerprint(source.Item1, source.Item2).Sha256 != fingerprints.Single(value => value.Alias == source.Item2).Sha256)
                throw new InvalidDataException("审计期间输入改变，本次输出不能作为稳定证据。");
        var report = new PlacementHintAuditReport("1", fingerprints, counts, strata,
            coverage.OrderBy(pair => pair.Key.Dimension, StringComparer.Ordinal).ThenBy(pair => pair.Key.Value, StringComparer.Ordinal)
                .Select(pair => new PlacementCoverage(pair.Key.Dimension, pair.Key.Value, pair.Value)).ToArray(), issues,
            ["ObservedReadyBoardsAreNotProvenIndependentDraws", "NoFilteringByChosenTrainingAction",
                "NoGlobalDeduplicationAcrossTurns", "MultipleBoardsAreReportedNotAssumedMissingAtRandom",
                "NotIntroducedCardsKeptSeparateFromRandomAbsence", "UnadjustedPanelBaselinesAreNotScenarioPredictions",
                "SupportEffectDeltaIsNotAnActiveBuffSnapshot", "Unique122AndScenarioFacilitiesRequireTemporalResolution",
                "NominalWilsonIntervalsAssumeIndependentBernoulliTrials", "RunClusterSEAssumesIndependentRunsAndDoesNotRemoveSelectionBias",
                "PendingEventIdentityIsNotADrawDenominator", "EventRatesAndScenarioLengthScalingNotEstimated",
                "LocalMasterAcceptedWithoutStrictResourceVersionBinding", "NoRandomDistributionImplemented"], "DescriptiveEvidenceOnly");
        File.WriteAllText(Path.Combine(outputDirectory, "placement-hint-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static string BoardIdentity(PlacementBoard board) => JsonSerializer.Serialize(new { board.Mode, board.Cards }, EvidenceJson.LineOptions);

    static PlacementStratum Stratum((string Run, PlacementCard Card)[] entries, string mode, bool tucker)
    {
        var card = entries[0].Card;
        bool Preferred(PlacementCard value) => value.Commands.Contains(mode == "Ordinary" ? value.SpecialtyCommand
            : System.Array.IndexOf(Ordinary, value.SpecialtyCommand) is var index && index >= 0 ? Camp[index] : 0);
        var absent = Frequency(entries.Select(value => (value.Run, value.Card.Commands.Count == 0)).ToArray());
        var preferred = Frequency(card.CardType == 1 && Ordinary.Contains(card.SpecialtyCommand)
            ? entries.Select(value => (value.Run, Preferred(value.Card))).ToArray() : []);
        var hint = Frequency(entries.Select(value => (value.Run, value.Card.Hint)).ToArray());
        var hintPresent = Frequency(entries.Where(value => value.Card.Commands.Count != 0).Select(value => (value.Run, value.Card.Hint)).ToArray());
        var baselines = card.Introduced && card.CardType == 1 && entries.All(value => value.Card.Commands.Count <= 1)
            ? Baselines(card.PanelSpecialty, card.DirectUniqueSpecialty, entries.Length, preferred.Successes, absent.Successes) : [];
        return new(card.CardId, card.Level, mode, card.Introduced, tucker, card.CardType, card.PanelSpecialty,
            card.DirectUniqueSpecialty, card.PanelHint, card.DirectUniqueHint, card.ConditionalUniqueTypes,
            absent, preferred, hint, hintPresent, baselines,
            card.Introduced && card.CardType == 1 && card.DirectUniqueHint == 0 ? entries.Length * .06 * (1 + card.PanelHint / 100.0) : null,
            "UnresolvedDynamicScenarioAndConditionalUniqueEffects");
    }

    // Published historical hypotheses plus an explicitly separate two-stage hypothesis; no model is selected as a game rule.
    public static PlacementBaseline[] Baselines(int panel, int directUnique, int trials, int preferred, int absent)
    {
        var weight = (100.0 + panel) * (1 + directUnique / 100.0);
        var models = new List<(string Name, double Preferred, double Absent)>
        {
            ("Vip2021AdditiveWeights", (100.0 + panel + directUnique) / (550 + panel + directUnique), 50.0 / (550 + panel + directUnique)),
            ("Vip2021MultiplicativeUniqueWeights", weight / (450 + weight), 50 / (450 + weight)),
            ("PresenceFirst90ThenSameWeightsHypothesis", .9 * weight / (400 + weight), .1),
        };
        var legacy = .18 * (1 + (panel + directUnique) / 100.0);
        if (legacy <= .9) models.Add(("GameWithLegacyLinearApproximation", legacy, .1));
        return models.Select(model => new PlacementBaseline(model.Name, model.Preferred, model.Absent,
            trials * model.Preferred, trials * model.Absent, preferred - trials * model.Preferred, absent - trials * model.Absent)).ToArray();
    }

    public static PlacementFrequency Frequency((string Run, bool Success)[] values)
    {
        var n = values.Length;
        if (n == 0) return new(0, 0, 0, null, null, null, null);
        var successes = values.Count(value => value.Success);
        var p = (double)successes / n;
        const double z = 1.959963984540054;
        var center = (p + z * z / (2 * n)) / (1 + z * z / n);
        var radius = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / (1 + z * z / n);
        var groups = values.GroupBy(value => value.Run, StringComparer.Ordinal).ToArray();
        var g = groups.Length;
        var se = g > 1 ? Math.Sqrt(g / (double)(g - 1) * groups.Sum(group => Math.Pow(group.Count(value => value.Success) - p * group.Count(), 2))) / n : (double?)null;
        return new(successes, n, g, p, Math.Max(0, center - radius), Math.Min(1, center + radius), se);
    }

    sealed class PlacementMaster
    {
        readonly Dictionary<int, Dictionary<string, int>> cards;
        readonly Dictionary<int, Dictionary<string, int>> uniques;
        readonly Dictionary<int, Dictionary<string, int>[]> effects;
        readonly Dictionary<string, int>[] levels;
        public PlacementMaster(string path)
        {
            using var connection = EvidenceAudit.OpenReadOnly(path);
            cards = Rows(connection, "support_card_data").ToDictionary(row => row["id"]);
            uniques = Rows(connection, "support_card_unique_effect").ToDictionary(row => row["id"]);
            effects = Rows(connection, "support_card_effect_table").GroupBy(row => row["id"]).ToDictionary(group => group.Key, group => group.ToArray());
            levels = Rows(connection, "support_card_level");
        }
        public PlacementCard Card(JsonObject packet, bool introduced, int[] commands, bool hint)
        {
            var id = Int(packet, "support_card_id");
            var card = cards[id];
            var available = levels.Where(row => row["rarity"] == card["rarity"] && row["total_exp"] <= Int(packet, "exp")).ToArray();
            if (available.Length == 0) throw new InvalidDataException("支援卡经验无法映射本地等级表。");
            var level = available.Max(row => row["level"]);
            var panel = effects[card["effect_table_id"]].ToDictionary(row => row["type"], row => EffectAtLevel(row, level));
            var directSpecialty = 0;
            var directHint = 0;
            var conditional = new List<int>();
            if (card["unique_effect_id"] != 0)
            {
                var unique = uniques[card["unique_effect_id"]];
                if (level >= unique["lv"])
                    for (var index = 0; index < 2; index++)
                    {
                        var type = unique[$"type_{index}"];
                        if (type == 19) directSpecialty += unique[$"value_{index}"];
                        if (type == 18) directHint += unique[$"value_{index}"];
                        if (type >= 100) conditional.Add(type);
                    }
            }
            return new(Int(packet, "position"), id, level, card["support_card_type"], card["command_id"],
                panel.GetValueOrDefault(19), directSpecialty, panel.GetValueOrDefault(18), directHint,
                conditional.Order().ToArray(), introduced, commands, hint);
        }
        static int EffectAtLevel(Dictionary<string, int> row, int level)
        {
            var points = new[] { (Level: 1, Value: row["init"]) }.Concat(Enumerable.Range(1, 10)
                .Select(index => (Level: index * 5, Value: row[$"limit_lv{index * 5}"]))).Where(point => point.Value >= 0).ToArray();
            var lower = points.Where(point => point.Level <= level).ToArray();
            if (lower.Length == 0) return 0;
            var low = lower[^1];
            var upper = points.Where(point => point.Level >= level).ToArray();
            if (upper.Length == 0 || upper[0].Level == low.Level) return low.Value;
            var high = upper[0];
            return low.Value + (level - low.Level) * (high.Value - low.Value) / (high.Level - low.Level);
        }
        static Dictionary<string, int>[] Rows(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table;
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, int>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.GetInt32(index));
                rows.Add(row);
            }
            return rows.ToArray();
        }
    }

    static int Int(JsonObject value, string key) => value[key] is JsonValue item && item.TryGetValue<int>(out var number)
        ? number : throw new InvalidDataException($"{key}必须为显式整数，不能把缺失或null当0。");
    static JsonArray Array(JsonObject value, string key) => value[key] as JsonArray
        ?? throw new InvalidDataException($"{key}必须为显式数组。");
    static JsonObject[] Objects(JsonArray values, string path) => values.Select(value => value as JsonObject
        ?? throw new InvalidDataException($"{path}包含非对象项。")).ToArray();
    static int[] Integers(JsonArray values, string path)
    {
        var items = values.Select(value => value is JsonValue scalar && scalar.TryGetValue<int>(out var number) && number > 0
            ? number : throw new InvalidDataException($"{path}包含非正整数位置。")).ToArray();
        if (items.Distinct().Count() != items.Length) throw new InvalidDataException($"{path}包含重复位置。");
        return items;
    }
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, alias, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
