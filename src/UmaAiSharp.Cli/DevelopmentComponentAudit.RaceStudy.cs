using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Races;
using static UmaAiSharp.Cli.Evidence.PioneerMasterCompiler;

namespace UmaAiSharp.Cli.Evidence;

public sealed record RacePointStudyReport(IReadOnlyList<SourceFingerprint> Inputs, int LocatedWindows,
    int CheckedWindows, int MatchedWindows, IReadOnlyList<EvidenceIssue> Issues, ValidationSummary Validation);

public static partial class DevelopmentComponentAudit
{
    public static RacePointStudyReport RunRaceStudy(string inputDirectory, string studyDirectory, string masterPath, string outputDirectory)
    {
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("比赛发展Pt研究核验输出目录非空，拒绝覆盖。");
        var summaryPath = Path.Combine(studyDirectory, "run-01", "summary.json");
        var summary = ReadJson(summaryPath);
        var paths = new List<(string Path, string Alias)> { (summaryPath, "race-study-summary"), (masterPath, "master") };
        var required = new[] { "audit.json", "pioneer-observations.jsonl", "pioneer-actions.jsonl", "exchanges.jsonl" };
        foreach (var name in required.Append(Path.GetFileName(masterPath)))
        {
            var expected = summary.GetProperty("inputs").EnumerateArray().Single(x => Path.GetFileName(x.GetProperty("path").GetString()) == name);
            var path = name == Path.GetFileName(masterPath) ? masterPath : Path.Combine(inputDirectory, name);
            if (Fingerprint(path, name).Sha256 != expected.GetProperty("sha256").GetString())
                throw new InvalidDataException($"比赛研究与 {name} 的完整输入SHA-256不一致；不按旧alias复用标签。");
            if (path != masterPath) paths.Add((path, name));
        }
        var labelsPath = Path.Combine(studyDirectory, "hypotheses-01", "pt-candidate.json");
        var windowsPath = Path.Combine(studyDirectory, "verification-01.json");
        paths.AddRange([(labelsPath, "race-study-labels"), (windowsPath, "race-study-windows")]);
        var fingerprints = paths.Select(x => Fingerprint(x.Path, x.Alias)).ToArray();
        var sourceReport = JsonSerializer.Deserialize<EvidenceReport>(File.ReadAllText(Path.Combine(inputDirectory, "audit.json")), EvidenceJson.ReadOptions)
            ?? throw new InvalidDataException("缺少比赛研究输入来源报告。");
        var sourceHashes = sourceReport.Sources.ToDictionary(x => x.Alias, x => x.Sha256);
        var labels = ReadJson(labelsPath).EnumerateArray().Where(x => x.TryGetProperty("race", out _))
            .ToDictionary(x => (x.GetProperty("ref").GetProperty("source").GetString()!, x.GetProperty("ref").GetProperty("packetId").GetInt64()));
        var windows = ReadJson(windowsPath).GetProperty("windows").EnumerateArray().ToArray();
        var wanted = new HashSet<(string Source, long Record)>();
        foreach (var window in windows)
        {
            var before = Reference(window.GetProperty("before"));
            var after = Reference(window.GetProperty("after"));
            wanted.Add(before); wanted.Add(after); wanted.Add(Reference(labels[after].GetProperty("race")));
        }
        var frames = new Dictionary<(string Source, long Record), PioneerObservation>();
        foreach (var line in File.ReadLines(Path.Combine(inputDirectory, "pioneer-observations.jsonl")))
        {
            var row = JsonSerializer.Deserialize<PioneerObservation>(line, EvidenceJson.ReadOptions)
                ?? throw new InvalidDataException("比赛观测不能为null。");
            if (wanted.Contains((row.Source, row.PacketId))) frames.Add((row.Source, row.PacketId), row);
        }
        var exchanges = File.ReadLines(Path.Combine(inputDirectory, "exchanges.jsonl"))
            .Select(line => JsonSerializer.Deserialize<ExchangeSummary>(line, EvidenceJson.ReadOptions)!).ToDictionary(x => x.Key);
        var requests = File.ReadLines(Path.Combine(inputDirectory, "pioneer-actions.jsonl"))
            .Select(line => JsonSerializer.Deserialize<ObservedAction>(line, EvidenceJson.ReadOptions)!)
            .GroupBy(x => x.ExchangeKey).ToDictionary(x => x.Key, x => x.ToArray());
        var captured = exchanges.Values.SelectMany(exchange => exchange.Requests.Select(x => (x.Source, x.Id, Request: true, Exchange: exchange))
            .Concat(exchange.Responses.Select(x => (x.Source, x.Id, Request: false, Exchange: exchange))))
            .GroupBy(x => x.Source).ToDictionary(x => x.Key, x => x.OrderBy(row => row.Id).ToArray());
        var capturedIds = captured.ToDictionary(x => x.Key, x => x.Value.Select(row => row.Id).ToArray());
        using var db = EvidenceAudit.OpenReadOnly(masterPath);
        var goals = PioneerMasterCompiler.Rows(db, "single_mode_route_race").ToDictionary(x => x["id"]);
        var groups = PioneerMasterCompiler.Rows(db, "single_mode_race_group").GroupBy(x => x["race_group_id"])
            .ToDictionary(x => x.Key, x => x.Select(row => row["race_program_id"]).ToHashSet());
        var master = new CheckpointMasterData(Fingerprint(masterPath, "master").Sha256,
            PioneerMasterCompiler.Rows(db, "single_mode_11_planning").Select(x => new PlanningPeriod(x["id"], x["deck_id"], x["check_point_type"], x["planning_turn"], x["check_point_turn"], x["target_pt"])),
            PioneerMasterCompiler.Rows(db, "single_mode_11_pioneer_pt_bonus").Select(x => new CheckpointPointBonus(x["id"], x["check_point_type"], x["group_id"], x["pt_rate"], x["add_value_max"])),
            PioneerMasterCompiler.Rows(db, "single_mode_11_check_point_eff").Select(x => new CheckpointEffect(x["id"], x["check_point_type"], x["result_state"], x["effect_type"], x["effect_value_1"], x["effect_value_2"], x["effect_value_3"], x["effect_value_4"])));
        var rules = new PioneerRacePointGain(master);
        var cases = new List<RacePointComponentCase>();
        var issues = new List<EvidenceIssue>();
        var seen = new HashSet<(string Source, long Record)>();
        foreach (var window in windows)
        {
            var key = Reference(window.GetProperty("after"));
            try
            {
                if (!seen.Add(key)) throw new InvalidDataException("研究窗口重复引用响应。");
                var before = frames[Reference(window.GetProperty("before"))];
                var after = frames[key];
                var race = frames[Reference(labels[key].GetProperty("race"))];
                var exchange = exchanges[after.ExchangeKey];
                var action = requests[after.ExchangeKey].Single();
                if (before.Source != after.Source || action.Source != after.Source || race.Source != after.Source
                    || before.RunKey is null || before.RunKey != after.RunKey || race.RunKey != after.RunKey
                    || exchange.Status != "Paired" || exchange.Endpoint != after.Endpoint || action.Endpoint != after.Endpoint
                    || !exchange.Requests.Contains(new(action.Source, action.PacketId)) || !exchange.Responses.Contains(new(after.Source, after.PacketId))
                    || action.PacketId != window.GetProperty("requestPacketId").GetInt64()
                    || !(race.PacketId < before.PacketId && before.PacketId < action.PacketId && action.PacketId < after.PacketId))
                    throw new InvalidDataException("比赛Pt窗口同源、同局、配对成员或记录顺序不符。");
                var left = System.Array.BinarySearch(capturedIds[after.Source], before.PacketId);
                var right = System.Array.BinarySearch(capturedIds[after.Source], after.PacketId);
                if (left < 0 || right <= left) throw new InvalidDataException("捕获索引缺少比赛Pt窗口边界。");
                for (var i = left + 1; i < right; i++)
                {
                    var row = captured[after.Source][i];
                    if (row.Request && row.Id == action.PacketId) continue;
                    if (row.Exchange.Status != "Paired" || !row.Exchange.Endpoint.EndsWith("/get_choice_reward", StringComparison.Ordinal))
                        throw new InvalidDataException("比赛Pt窗口存在未解释的中间请求或响应。");
                }
                var b = PioneerNormalizer.GetCommon(before.Visible, before.Endpoint)!;
                var raceCommon = PioneerNormalizer.GetCommon(race.Visible, race.Endpoint)!;
                var chara = b["chara_info"]!.AsObject();
                var turn = PioneerMasterCompiler.Int(chara, "turn");
                var request = action.Fields["single_mode_check_event_request_common"]!.AsObject();
                var selected = b["unchecked_event_array"]!.AsArray().OfType<JsonObject>().Single();
                if (PioneerMasterCompiler.Int(request, "current_turn") != turn || PioneerMasterCompiler.Int(request, "event_id") != PioneerMasterCompiler.Int(selected, "event_id")
                    || PioneerMasterCompiler.Int(selected, "story_id") != 400011105)
                    throw new InvalidDataException("比赛Pt请求未命中前态当前回合的唯一对应事件。");
                var raceHistory = raceCommon["race_history"]!.AsArray().OfType<JsonObject>().Single(x => PioneerMasterCompiler.Int(x, "turn") == turn);
                var program = PioneerMasterCompiler.Int(raceHistory, "program_id");
                var selectedGoals = raceCommon["chara_info"]!["route_race_id_array"]!.AsArray().Select(x => goals[x!.GetValue<int>()]);
                var target = selectedGoals.Any(x => x["turn"] == turn && x["condition_type"] == 1
                    && (x["condition_id"] == program || groups.TryGetValue(x["condition_id"], out var group) && group.Contains(program)));
                if (target != labels[key].GetProperty("target").GetBoolean())
                    throw new InvalidDataException("用实际比赛及已选路线重新分类与研究标签不一致。");
                var scenario = before.Visible["pioneer_data_set"]!.AsObject();
                var checkpoints = scenario["check_point_info_array"]!.AsArray().OfType<JsonObject>().OrderBy(x => PioneerMasterCompiler.Int(x, "check_point_type")).ToArray();
                var context = new RacePointContext(turn, PioneerMasterCompiler.Int(chara, "playing_state"), PioneerMasterCompiler.Int(scenario["pioneer_info"]!.AsObject(), "deck_id"),
                    400011105, PioneerMasterCompiler.Int(request, "choice_number"), target, checkpoints.Length == 0 ? 0 : PioneerMasterCompiler.Int(checkpoints[^1], "check_point_type"),
                    checkpoints.Length == 0 ? 0 : PioneerMasterCompiler.Int(checkpoints[^1], "result_state"));
                var oldPoints = PioneerMasterCompiler.Int(scenario["pioneer_info"]!.AsObject(), "pioneer_pt");
                var newPoints = PioneerMasterCompiler.Int(after.Visible["pioneer_data_set"]!["pioneer_info"]!.AsObject(), "pioneer_pt");
                var gain = rules.CalculateObservedEvent(context);
                cases.Add(new(new(sourceHashes[after.Source], before.PacketId), new(sourceHashes[after.Source], after.PacketId), action.PacketId,
                    context, oldPoints, newPoints, gain, checked(oldPoints + gain) == newPoints));
            }
            catch (Exception error) when (error is UnknownMechanismException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
            { issues.Add(new("RacePointStudyUnsupported", key.Source, key.Record, error.Message)); }
        }
        for (var i = 0; i < paths.Count; i++)
            if (Fingerprint(paths[i].Path, paths[i].Alias) != fingerprints[i]) throw new InvalidDataException($"比赛Pt核验期间{paths[i].Alias}发生变化。");
        var report = new RacePointStudyReport(fingerprints, windows.Length, cases.Count, cases.Count(x => x.Matches), issues,
            new("CandidateOnly", ["研究仅定位窗口；当前输入重新解析选择、前后Pt、比赛及路线，不读取研究预测值作真值。",
                "低评价、失败重试、海之家Lv3及完整比赛奖励未覆盖；这些窗口不是独立留出验收。"]));
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllLines(Path.Combine(outputDirectory, "race-point-study-cases.jsonl"), cases.Select(x => JsonSerializer.Serialize(x, EvidenceJson.LineOptions)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, "race-point-study-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static (string Source, long Record) Reference(JsonElement value) =>
        (value.GetProperty("source").GetString()!, value.GetProperty("packetId").GetInt64());
}
