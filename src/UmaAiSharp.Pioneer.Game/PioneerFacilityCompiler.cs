using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public sealed class PioneerFacilityCompiler
{
    readonly Dictionary<int, TrainingFacility> states;
    readonly ImmutableArray<ForecastFacilityBase> bases;

    public PioneerFacilityCompiler(PioneerFacilityRulesData rules)
    {
        states = rules.States.ToDictionary(state => state.StateId);
        bases = rules.Bases;
    }

    public ImmutableArray<TrainingFacility> Facilities(JsonObject pioneer) => Objects(Array(pioneer, "facility_info_array"), "facility_info_array")
        .Select(value =>
        {
            var id = Int(value, "facility_id");
            var state = states[Int(value, "state_id")];
            if (state.Id != id) throw new InvalidDataException("设施ID和master状态ID不一致。");
            return state;
        }).ToImmutableArray();

    public TrainingValues Base(int command, ImmutableArray<TrainingFacility> facilities)
        => TrainingBaseTable.FromFacilityRows(command, bases, facilities);
}
