using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Cli.Evidence.PioneerMasterCompiler;

namespace UmaAiSharp.Cli.Evidence;

public sealed class PioneerFacilityCompiler
{
    readonly Dictionary<int, Dictionary<string, int>> states;
    readonly Dictionary<int, ImmutableArray<TrainingFacilityEffect>> effects;
    readonly Dictionary<string, int>[] bases;

    public PioneerFacilityCompiler(string path)
    {
        using var connection = EvidenceAudit.OpenReadOnly(path);
        states = PioneerMasterCompiler.Rows(connection, "single_mode_11_facility_state").ToDictionary(row => row["state_id"]);
        effects = PioneerMasterCompiler.Rows(connection, "single_mode_11_facility_effect").GroupBy(row => row["state_id"])
            .ToDictionary(group => group.Key, group => group.Select(row => new TrainingFacilityEffect(row["effect_type"],
                row["effect_value_1"], row["effect_value_2"], row["effect_value_3"])).ToImmutableArray());
        bases = PioneerMasterCompiler.Rows(connection, "single_mode_11_shima_effect");
    }

    public ImmutableArray<TrainingFacility> Facilities(JsonObject pioneer) => Objects(Array(pioneer, "facility_info_array"), "facility_info_array")
        .Select(value =>
        {
            var id = Int(value, "facility_id");
            var state = states[Int(value, "state_id")];
            if (state["facility_id"] != id) throw new InvalidDataException("设施ID和master状态ID不一致。");
            return new TrainingFacility(id, state["state_id"], state["facility_lv"], effects.TryGetValue(state["state_id"], out var rows) ? rows : []);
        }).ToImmutableArray();

    public TrainingValues Base(int command, ImmutableArray<TrainingFacility> facilities)
    {
        var levels = facilities.ToDictionary(value => value.Id, value => value.Level);
        var values = new Dictionary<int, int>();
        foreach (var row in bases.Where(row => row["command_id"] == command && row["result_state"] == 2))
        {
            if (row["required_facility_id"] != 0 && levels[row["required_facility_id"]] == 0) continue;
            var target = row["target_type"];
            if (target is not (1 or 2 or 3 or 4 or 5 or 10 or 30)) throw new UnknownMechanismException($"未知训练基础target={target}。");
            if (values.TryGetValue(target, out var previous) && previous != row["effect_value"])
                throw new UnknownMechanismException("同一训练基础分量存在冲突master条目。");
            values[target] = row["effect_value"];
        }
        if (values.Count == 0) throw new UnknownMechanismException($"command={command}没有训练基础master条目。");
        return new(values.GetValueOrDefault(1), values.GetValueOrDefault(2), values.GetValueOrDefault(3), values.GetValueOrDefault(4),
            values.GetValueOrDefault(5), values.GetValueOrDefault(10), values.GetValueOrDefault(30));
    }
}
