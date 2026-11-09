using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace UmaAiSharp.Cli.Evidence;

public static class EvidenceAudit
{
    public static EvidenceReport Run(string packetsPath, string masterPath, string outputDirectory)
    {
        var packetFiles = Directory.Exists(packetsPath)
            ? Directory.GetFiles(packetsPath, "*.sqlite", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : [Path.GetFullPath(packetsPath)];
        if (packetFiles.Length == 0) throw new InvalidDataException("输入目录没有.sqlite包库。");
        var sources = packetFiles.Select((path, index) => (Path: Path.GetFullPath(path), Alias: $"packets-{index + 1:D4}")).ToArray();
        var master = Path.GetFullPath(masterPath);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("输出目录非空；为保留已有证据，拒绝覆盖。");
        var fingerprints = sources.Select(source => Fingerprint(source.Path, source.Alias, "DebugSaveAllPackets"))
            .Append(Fingerprint(master, "master", "GameMasterSqlite")).ToArray();
        Directory.CreateDirectory(output);
        var issues = new List<EvidenceIssue>();
        MasterInventory masterInventory;
        using (var rows = NewWriter(output, "master-rows.jsonl")) masterInventory = MasterAudit.Read(master, rows);
        PacketInventory packets;
        using (var exchanges = NewWriter(output, "exchanges.jsonl"))
        using (var observations = NewWriter(output, "pioneer-observations.jsonl"))
        using (var actions = NewWriter(output, "pioneer-actions.jsonl"))
            packets = PacketAudit.Read(sources, exchanges, observations, actions, issues);
        foreach (var (path, alias) in sources.Append((master, "master")))
        {
            var before = fingerprints.Single(fingerprint => fingerprint.Alias == alias);
            var after = Fingerprint(path, alias, before.Kind);
            if (before.Sha256 != after.Sha256 || before.Bytes != after.Bytes)
                issues.Add(new("SourceChangedDuringAudit", alias, null, "审计前后源文件哈希或长度变化；本次派生数据不能作为固定版本证据。"));
        }
        var binding = new VersionBinding("Unverified", null, packets.ResourceVersions,
            "按已确认范围使用本地master与实包。内容哈希与包头res_ver已记录，但未证明属于同一资源版本；此差异不阻断本地研究，机制结论仍须实证核验。");
        var blockers = new List<string>
        {
            "MechanismInterpretationsUnverified",
            // Endpoint and turn continuity cannot prove that every decision within a turn was captured.
            "TrajectoryCompletenessUnverified",
        };
        if (packets.PioneerPackets == 0) blockers.Add("NoPioneerPackets");
        if (packets.DecodingFailures > 0) blockers.Add("PacketDecodingFailures");
        if (packets.OrphanExchanges > 0) blockers.Add("OrphanExchanges");
        if (packets.ConflictingExchanges > 0) blockers.Add("ConflictingExchanges");
        if (issues.Any(issue => issue.Code == "SourceChangedDuringAudit")) blockers.Add("SourceChangedDuringAudit");
        if (issues.Any(issue => issue.Code is "InvalidDirection" or "InvalidRequestId" or "InvalidObservationShape" or "InvalidScalarType"))
            blockers.Add("PacketSchemaErrors");
        var report = new EvidenceReport("1", DateTimeOffset.UtcNow, fingerprints, binding, "Unverified",
            masterInventory, packets, issues, new("Blocked", blockers));
        File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("输入数据库不存在。");
        // A detached main file is not a complete WAL snapshot. Require a stable exported database instead.
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
            throw new InvalidDataException("输入存在非空WAL；请提供包含已提交状态的稳定数据库快照，不能仅审计主文件。");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    static SourceFingerprint Fingerprint(string path, string alias, string kind)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new(alias, kind, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            error.Data["FileRole"] = alias == "master" ? "master数据库" : $"包库 {alias}";
            throw;
        }
    }

    static StreamWriter NewWriter(string directory, string name)
    {
        try
        {
            return new(new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            error.Data["FileRole"] = $"派生输出 {name}";
            throw;
        }
    }
}
