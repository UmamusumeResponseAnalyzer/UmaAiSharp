using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UmaAiSharp.MCP.Evidence;

public sealed record EvidenceIssue(string Code, string Source, long? PacketId, string Detail);
public sealed record SourceFingerprint(string Alias, string Kind, string Sha256, long Bytes);
public sealed record PacketReference(string Source, long Id);
public sealed record EndpointCount(string Endpoint, string Direction, long Count);
public sealed record ValueCount(string? Value, long Count);
public sealed record MasterColumn(string Name, string DeclaredType, bool NotNull, int PrimaryKeyPosition);
public sealed record MasterTable(string Name, long Rows, IReadOnlyList<MasterColumn> Columns, bool RowsExported);
public sealed record MasterDomain(string Table, string Column, string Interpretation,
    IReadOnlyList<ValueCount> Values);
public sealed record MasterInventory(IReadOnlyList<MasterTable> Tables, IReadOnlyList<MasterDomain> Domains,
    long ExportedRows);
public sealed record PacketDomain(string Path, string Interpretation, IReadOnlyList<ValueCount> Values);
public sealed record ExchangeSummary(string Key, string Endpoint, string Status,
    IReadOnlyList<PacketReference> Requests, IReadOnlyList<PacketReference> Responses,
    int DistinctRequestBodies, int DistinctResponseBodies, int DuplicateCaptures,
    IReadOnlyList<string> AppVersions, IReadOnlyList<string> ResourceVersions);
public sealed record ObservedInteger(string Status, long? Value);
public sealed record PioneerObservation(string Source, long PacketId, string ExchangeKey, string Endpoint,
    string? RunKey, string CapturedAtUtc, IReadOnlyDictionary<string, ObservedInteger> Fields,
    IReadOnlyDictionary<string, string> SectionPresence, IReadOnlyList<string> DecisionEvidence,
    JsonObject Visible, string Completeness = "ObservedOnly");
public sealed record ObservedAction(string Source, long PacketId, string ExchangeKey, string Endpoint, string CapturedAtUtc,
    JsonObject Fields);
public sealed record TrajectorySummary(string RunKey, int Observations, long? FirstTurn, long? LastTurn,
    bool HasStartExchange, bool HasFinishExchange, int ForwardGaps, int Regressions, string OrderingEvidence,
    IReadOnlyList<string> AppVersions, IReadOnlyList<string> ResourceVersions, string Completeness = "ObservedOnly");
public sealed record PacketInventory(long Packets, long PioneerPackets, int Exchanges, int PairedExchanges,
    int OrphanExchanges, int ConflictingExchanges, int DuplicateCaptures, int DecodingFailures,
    int Observations, IReadOnlyList<string> AppVersions, IReadOnlyList<string> ResourceVersions,
    IReadOnlyList<EndpointCount> Endpoints, IReadOnlyList<PacketDomain> Domains,
    IReadOnlyList<TrajectorySummary> Trajectories);
public sealed record VersionBinding(string Status, string? MasterResourceVersion,
    IReadOnlyList<string> PacketResourceVersions, string Reason);
public sealed record ValidationSummary(string Status, IReadOnlyList<string> Blockers);
public sealed record EvidenceReport(string SchemaVersion, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SourceFingerprint> Sources, VersionBinding VersionBinding,
    string RegionVerification, MasterInventory Master, PacketInventory Packets,
    IReadOnlyList<EvidenceIssue> Issues, ValidationSummary Validation);

public static class EvidenceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static JsonSerializerOptions LineOptions { get; } = new(JsonSerializerDefaults.Web);

    public static JsonSerializerOptions ReadOptions { get; } = new(Options)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };
}
