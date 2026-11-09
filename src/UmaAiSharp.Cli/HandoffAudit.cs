using System.Security.Cryptography;
using System.Text.Json;

namespace UmaAiSharp.Cli.Evidence;

public sealed record HandoffFrameAudit(string Source, int Line, int LabelledTurn, int? BeforeTurn,
    int? AfterTurn, int? CommandId, string StateAvailability, IReadOnlyDictionary<string, int> DeltaDifferences,
    IReadOnlyDictionary<string, int> BetweenFrameResourceChanges);
public sealed record HandoffRunAudit(string Run, int Frames, int RepeatedTurnLabels, int SameTurnActions,
    int AdvancingActions, int RegressingActions, int IncompleteStates, int DeltaMismatches,
    int BetweenFrameResourceChanges, IReadOnlyDictionary<string, int> Commands);
public sealed record HandoffAuditReport(string SchemaVersion, IReadOnlyList<SourceFingerprint> Sources,
    IReadOnlyList<HandoffRunAudit> Runs, IReadOnlyList<string> ComparedResourceFields, IReadOnlyList<string> Limitations);

public static class HandoffAudit
{
    static readonly string[] AttributeNames = ["speed", "stamina", "power", "guts", "wisdom"];
    static readonly string[] ScalarNames = ["vital", "skill_pt", "pt", "fans"];
    static readonly string[] DatasetFiles = ["turns.jsonl", "events.jsonl", "event_rewards.jsonl", "shima.jsonl", "planning.jsonl", "checkpoints.jsonl", "meta.json"];

    public static HandoffAuditReport Run(string handoffDirectory, string outputDirectory)
    {
        var input = Path.GetFullPath(handoffDirectory);
        var dataset = Path.Combine(input, "03_真实对局数据");
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("交接包核验输出目录非空，拒绝覆盖。");
        var runs = Directory.GetDirectories(dataset).Where(path =>
        {
            var name = Path.GetFileName(path);
            return name.Length > 1 && name[0] == 'v' && name[1..].All(char.IsAsciiDigit);
        }).Order(StringComparer.Ordinal).ToArray();
        if (runs.Length == 0) throw new InvalidDataException("交接包没有 vN 结构的对局数据。");

        var sources = new List<(string Path, SourceFingerprint Fingerprint)>();
        foreach (var run in runs)
            foreach (var file in DatasetFiles)
            {
                var path = Path.Combine(run, file);
                if (File.Exists(path)) sources.Add((path, Fingerprint(path, Path.GetFileName(run) + "/" + file)));
                else if (file == "turns.jsonl") throw new InvalidDataException($"{Path.GetFileName(run)} 缺少 turns.jsonl。");
            }
        Directory.CreateDirectory(output);
        var summaries = new List<HandoffRunAudit>();
        using (var writer = new StreamWriter(new FileStream(Path.Combine(output, "handoff-frames.jsonl"), FileMode.CreateNew, FileAccess.Write)))
            foreach (var run in runs)
            {
                var name = Path.GetFileName(run);
                using var reader = File.OpenText(Path.Combine(run, "turns.jsonl"));
                summaries.Add(AuditRun(name, reader, writer));
            }
        foreach (var source in sources)
            if (Fingerprint(source.Path, source.Fingerprint.Alias) != source.Fingerprint)
                throw new InvalidDataException($"核验期间 {source.Fingerprint.Alias} 发生变化，结果不能作为稳定来源证据。");
        var report = new HandoffAuditReport("1", sources.Select(x => x.Fingerprint).ToArray(), summaries,
            [.. AttributeNames, .. ScalarNames],
            ["DerivedJsonlWithoutOriginalRequestResponsePairs", "PreservedSourceOrderAndTurnLabels",
                "BetweenFrameEffectsNotAttributedToPreviousAction", "MissingTicketFieldMayHaveBeenConvertedToZeroBySourceExporter",
                "DeltaConsistencyIsNotRuleValidation", "NoAutonomousSimulationAcceptance"]);
        using var reportStream = new FileStream(Path.Combine(output, "handoff-audit.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(reportStream, report, EvidenceJson.Options);
        return report;
    }

    public static HandoffRunAudit AuditRun(string name, TextReader input, TextWriter output)
    {
        var lineNumber = 0;
        var labels = new HashSet<int>();
        var repeated = 0;
        var sameTurn = 0;
        var advancing = 0;
        var regressing = 0;
        var incomplete = 0;
        var mismatch = 0;
        var between = 0;
        var commands = new SortedDictionary<string, int>(StringComparer.Ordinal);
        JsonElement? previousAfter = null;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                var turn = Integer(row, "turn");
                if (!labels.Add(turn)) repeated++;
                var action = row.GetProperty("action");
                var commandValue = action.GetProperty("command_id");
                var command = commandValue.ValueKind != JsonValueKind.Null
                    ? commandValue.GetInt32() : (int?)null;
                var commandKey = command?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                commands[commandKey] = commands.GetValueOrDefault(commandKey) + 1;
                var before = Snapshot(row.GetProperty("before"));
                var after = Snapshot(row.GetProperty("after"));
                if (before is { } beforeValues) _ = Values(beforeValues);
                if (after is { } afterValues) _ = Values(afterValues);
                var changes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var differences = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var beforeTurn = before is { } start ? Integer(start, "turn") : (int?)null;
                var afterTurn = after is { } end ? Integer(end, "turn") : (int?)null;
                if (before is { } a && after is { } b)
                {
                    if (beforeTurn == afterTurn) sameTurn++;
                    else if (afterTurn > beforeTurn) advancing++;
                    else regressing++;
                    var delta = row.GetProperty("delta");
                    var actualDelta = Difference(a, b);
                    var declaredDelta = Values(delta);
                    foreach (var (key, value) in actualDelta)
                    {
                        var error = checked(declaredDelta[key] - value);
                        if (error != 0) differences.Add(key, error);
                    }
                    if (differences.Count != 0) mismatch++;
                }
                else incomplete++;
                if (previousAfter is { } previous && before is { } current)
                {
                    foreach (var (key, value) in Difference(previous, current))
                        if (value != 0) changes.Add(key, value);
                    if (changes.Count != 0) between++;
                }
                // Null breaks continuity; neither prior state nor a new logical turn is fabricated.
                previousAfter = after?.Clone();
                output.WriteLine(JsonSerializer.Serialize(new HandoffFrameAudit(name + "/turns.jsonl", lineNumber,
                    turn, beforeTurn, afterTurn, command, before is not null && after is not null ? "DerivedBeforeAfter" : "Incomplete",
                    differences, changes), EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"{name}/turns.jsonl 第 {lineNumber} 行的必需字段缺失或类型无效：{error.Message}", error);
            }
        }
        return new(name, lineNumber, repeated, sameTurn, advancing, regressing, incomplete, mismatch, between, commands);
    }

    static JsonElement? Snapshot(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Object when !value.EnumerateObject().Any() => null,
        JsonValueKind.Object => value,
        _ => throw new InvalidDataException("对局快照必须是对象或显式 null。"),
    };

    static Dictionary<string, int> Values(JsonElement value)
    {
        var stats = value.GetProperty("stats");
        if (stats.ValueKind != JsonValueKind.Array || stats.GetArrayLength() != 5)
            throw new InvalidDataException("stats 必须包含五个明确整数，缺值不能补零。");
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < AttributeNames.Length; index++) values.Add(AttributeNames[index], stats[index].GetInt32());
        foreach (var name in ScalarNames) values.Add(name, Integer(value, name));
        return values;
    }

    static Dictionary<string, int> Difference(JsonElement before, JsonElement after)
    {
        var start = Values(before);
        return Values(after).ToDictionary(x => x.Key, x => checked(x.Value - start[x.Key]), StringComparer.Ordinal);
    }

    static int Integer(JsonElement value, string name) => value.GetProperty(name).GetInt32();

    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "DerivedHandoffDataset", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
