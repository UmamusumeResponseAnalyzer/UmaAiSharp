using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmaAiSharp.Core;

namespace UmaAiSharp.Pioneer.Development;

public sealed record CheckpointPointBonus(int Id, int CheckPointType, int GroupId, int PtRate, int AddValueMax);
public sealed record CheckpointEffect(int Id, int CheckPointType, int ResultState, int EffectType,
    [property: JsonPropertyName("effect_value_1")] int EffectValue1,
    [property: JsonPropertyName("effect_value_2")] int EffectValue2,
    [property: JsonPropertyName("effect_value_3")] int EffectValue3,
    [property: JsonPropertyName("effect_value_4")] int EffectValue4);

// Effect values retain master units; this reader does not assign semantics to effect_type.
public sealed class CheckpointMasterData
{
    static readonly JsonSerializerOptions RowJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public string SourceSha256 { get; }
    public FrozenDictionary<(int DeckId, int CheckpointType), PlanningPeriod> Periods { get; }
    public FrozenDictionary<int, CheckpointPointBonus> PointBonuses { get; }
    public FrozenDictionary<(int CheckpointType, int ResultState), ImmutableArray<CheckpointEffect>> Effects { get; }

    public CheckpointMasterData(string sourceSha256, IEnumerable<PlanningPeriod> periods,
        IEnumerable<CheckpointPointBonus> bonuses, IEnumerable<CheckpointEffect> effects)
    {
        if (sourceSha256.Length != 64 || sourceSha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("检查点 master 来源必须提供 64 位 SHA-256。");
        SourceSha256 = sourceSha256.ToLowerInvariant();
        Periods = periods.ToFrozenDictionary(x => (x.DeckId, x.CheckPointType));
        PointBonuses = bonuses.ToFrozenDictionary(x => x.CheckPointType);
        Effects = effects.GroupBy(x => (x.CheckPointType, x.ResultState))
            .ToFrozenDictionary(x => x.Key, x => x.OrderBy(row => row.Id).ToImmutableArray());
        if (Periods.Count == 0 || PointBonuses.Count == 0 || Effects.Count == 0)
            throw new InvalidDataException("检查点 master 缺少 planning、pioneer_pt_bonus 或 check_point_eff 行。");
        foreach (var period in Periods.Values)
            if (period.TargetPt <= 0 || !PointBonuses.ContainsKey(period.CheckPointType))
                throw new InvalidDataException($"检查点规划行 {period.Id} 的目标或 bonus 引用无效。");
        foreach (var bonus in PointBonuses.Values)
            if (bonus.PtRate < 0 || bonus.AddValueMax < 0)
                throw new InvalidDataException($"检查点 bonus 行 {bonus.Id} 的倍率或上限为负数。");
    }

    public ImmutableArray<CheckpointEffect> GetEffects(int checkpointType, int resultState) =>
        Effects.TryGetValue((checkpointType, resultState), out var rows) ? rows
            : throw new UnknownMechanismException($"master 没有检查点 {checkpointType}、结果 {resultState} 的效果行。");

    public static CheckpointMasterData ReadExport(TextReader input, string sourceSha256)
    {
        var periods = new List<PlanningPeriod>();
        var bonuses = new List<CheckpointPointBonus>();
        var effects = new List<CheckpointEffect>();
        var lineNumber = 0;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var row = root.GetProperty("row");
                if (row.ValueKind != JsonValueKind.Object)
                    throw new JsonException("row 必须为对象。");
                switch (root.GetProperty("table").GetString())
                {
                    case "single_mode_11_planning": periods.Add(row.Deserialize<PlanningPeriod>(RowJson)!); break;
                    case "single_mode_11_pioneer_pt_bonus": bonuses.Add(row.Deserialize<CheckpointPointBonus>(RowJson)!); break;
                    case "single_mode_11_check_point_eff": effects.Add(row.Deserialize<CheckpointEffect>(RowJson)!); break;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException($"检查点 master 导出第 {lineNumber} 行无法读取：{error.Message}", error);
            }
        }
        return new(sourceSha256, periods, bonuses, effects);
    }
}
