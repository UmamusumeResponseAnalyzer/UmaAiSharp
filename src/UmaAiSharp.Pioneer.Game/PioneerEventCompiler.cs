using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public static class PioneerEventCompiler
{
    public static PioneerStart.DeclaredEvent Compile(JsonObject chara, JsonObject currentEvent, JsonArray rewards,
        ImmutableArray<EventPartner>? partners = null)
    {
        var contents = currentEvent["event_contents_info"] as JsonObject ?? throw new InvalidDataException("事件缺少event_contents_info。");
        var options = Objects(Array(contents, "choice_array"), "choice_array").ToArray();
        if (options.Length < 2) throw new InvalidDataException("选项奖励导入要求至少两个已揭示选项。");
        var declarations = Objects(rewards, "rewards").ToArray();
        var choices = ImmutableArray.CreateBuilder<DeclaredEventChoice>(options.Length);
        var rewardKeys = new HashSet<int>();
        for (var index = 0; index < options.Length; index++)
        {
            var key = Int(options[index], "gain_select_id_index");
            if (!rewardKeys.Add(key)) throw new InvalidDataException("事件选项的奖励组身份重复。");
            var branches = declarations.Where(row => Int(row, "select_index") == key).Select(row => new DeclaredEventBranch(
                [.. Objects(Array(row, "gain_param_array"), "gain_param_array").Select(atom => new DeclaredEventEffect(
                    Int(atom, "display_id"), Int(atom, "effect_value_0"), Int(atom, "effect_value_1"), Int(atom, "effect_value_2")))])).ToImmutableArray();
            if (branches.IsEmpty) throw new InvalidDataException($"选项{index + 1}缺少奖励组{key}的声明。");
            choices.Add(new(index + 1, branches));
        }
        if (declarations.Any(row => !rewardKeys.Contains(Int(row, "select_index"))))
            throw new InvalidDataException("奖励声明包含当前选项之外的组身份。");
        return new(Int(chara, "turn"), Int(chara, "playing_state"),
            new(Int(currentEvent, "event_id"), Int(currentEvent, "chara_id"), Int(currentEvent, "story_id"), Int(currentEvent, "play_timing")),
            new(Int(chara, "speed"), Int(chara, "stamina"), Int(chara, "power"), Int(chara, "guts"), Int(chara, "wiz"), Int(chara, "vital"), Int(chara, "skill_point")),
            new(Int(chara, "max_speed"), Int(chara, "max_stamina"), Int(chara, "max_power"), Int(chara, "max_guts"), Int(chara, "max_wiz"), Int(chara, "max_vital")),
            Int(chara, "motivation"), [.. Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array")], choices.MoveToImmutable(), partners);
    }
}
