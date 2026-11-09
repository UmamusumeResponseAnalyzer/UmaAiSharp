using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmaAiSharp.Pioneer;

public sealed record FacilityKind(int FacilityId, int CommandId);
public sealed record FacilityState(int Id, int FacilityId, int FacilityLv, int IconId, int StateId);
public sealed record FacilityUpgrade(int Id, int FacilityId, int CurrentStateId, int TargetStateId, int Cost);
public sealed record PlanningPeriod(int Id, int DeckId, int CheckPointType, int PlanningTurn,
    int CheckPointTurn, int TargetPt);
public sealed record IslandTicketThreshold(int Id, int DeckId, int TurnMin, int TurnMax, int GainShimaTrainingPt);

// The values are source data. Loading them does not establish their game semantics or version binding.
public sealed class PioneerMasterData
{
    static readonly JsonSerializerOptions RowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string SourceSha256 { get; }
    public ImmutableArray<FacilityKind> FacilityKinds { get; }
    public ImmutableArray<FacilityState> FacilityStates { get; }
    public ImmutableArray<FacilityUpgrade> FacilityUpgrades { get; }
    public ImmutableArray<PlanningPeriod> PlanningPeriods { get; }
    public ImmutableArray<IslandTicketThreshold> TicketThresholds { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public FrozenDictionary<(int FacilityId, int From, int To), FacilityUpgrade> UpgradesByEdge { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public FrozenDictionary<(int FacilityId, int StateId), FacilityState> StatesById { get; }

    public PioneerMasterData(string sourceSha256, ImmutableArray<FacilityKind> facilityKinds,
        ImmutableArray<FacilityState> facilityStates, ImmutableArray<FacilityUpgrade> facilityUpgrades,
        ImmutableArray<PlanningPeriod> planningPeriods, ImmutableArray<IslandTicketThreshold> ticketThresholds)
    {
        if (sourceSha256.Length != 64 || sourceSha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("master 来源必须提供 64 位 SHA-256。");
        if (facilityKinds.IsDefaultOrEmpty || facilityStates.IsDefaultOrEmpty || facilityUpgrades.IsDefaultOrEmpty
            || planningPeriods.IsDefaultOrEmpty || ticketThresholds.IsDefaultOrEmpty)
            throw new InvalidDataException("Pioneer 建设数据缺少必需表或表为空。");

        SourceSha256 = sourceSha256.ToLowerInvariant();
        FacilityKinds = facilityKinds;
        FacilityStates = facilityStates;
        FacilityUpgrades = facilityUpgrades;
        PlanningPeriods = planningPeriods;
        TicketThresholds = ticketThresholds;
        UpgradesByEdge = facilityUpgrades.ToFrozenDictionary(x => (x.FacilityId, x.CurrentStateId, x.TargetStateId));
        StatesById = facilityStates.ToFrozenDictionary(x => (x.FacilityId, x.StateId));
        var kinds = facilityKinds.Select(x => x.FacilityId).ToHashSet();
        if (kinds.Count != facilityKinds.Length)
            throw new InvalidDataException("设施类型表包含重复 facility_id。");
        foreach (var state in facilityStates)
            if (!kinds.Contains(state.FacilityId))
                throw new InvalidDataException($"设施状态 {state.StateId} 引用了未知设施 {state.FacilityId}。");
        foreach (var upgrade in facilityUpgrades)
            if (upgrade.Cost <= 0 || !StatesById.ContainsKey((upgrade.FacilityId, upgrade.CurrentStateId))
                || !StatesById.ContainsKey((upgrade.FacilityId, upgrade.TargetStateId)))
                throw new InvalidDataException($"升级记录 {upgrade.Id} 的费用或设施状态引用无效。");
    }

    public static PioneerMasterData ReadExport(TextReader input, string sourceSha256)
    {
        var kinds = ImmutableArray.CreateBuilder<FacilityKind>();
        var states = ImmutableArray.CreateBuilder<FacilityState>();
        var upgrades = ImmutableArray.CreateBuilder<FacilityUpgrade>();
        var periods = ImmutableArray.CreateBuilder<PlanningPeriod>();
        var thresholds = ImmutableArray.CreateBuilder<IslandTicketThreshold>();
        var lineNumber = 0;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var table = root.GetProperty("table").GetString();
                var row = root.GetProperty("row");
                if (row.ValueKind != JsonValueKind.Object)
                    throw new JsonException("row 必须为对象。");
                switch (table)
                {
                    case "single_mode_11_facility_kind": kinds.Add(row.Deserialize<FacilityKind>(RowJson)!); break;
                    case "single_mode_11_facility_state": states.Add(row.Deserialize<FacilityState>(RowJson)!); break;
                    case "single_mode_11_facility_lv_up": upgrades.Add(row.Deserialize<FacilityUpgrade>(RowJson)!); break;
                    case "single_mode_11_planning": periods.Add(row.Deserialize<PlanningPeriod>(RowJson)!); break;
                    case "single_mode_11_gain_training_pt": thresholds.Add(row.Deserialize<IslandTicketThreshold>(RowJson)!); break;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException($"master 导出第 {lineNumber} 行无法读取：{error.Message}", error);
            }
        }
        return new(sourceSha256, kinds.ToImmutable(), states.ToImmutable(), upgrades.ToImmutable(),
            periods.ToImmutable(), thresholds.ToImmutable());
    }
}
