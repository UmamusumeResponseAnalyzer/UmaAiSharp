using System.Collections.Immutable;
using System.Text.Json.Nodes;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Pioneer.Game.PioneerMasterCompiler;

namespace UmaAiSharp.Pioneer.Game;

public static class PioneerEventCompiler
{
    public static PioneerStart.DeclaredEvent Compile(JsonObject chara, JsonObject currentEvent, JsonArray? rewards,
        JsonObject pioneer, PioneerEventPartnersData partnerRules)
    {
        var unknown = new List<int>();
        var partners = EventPartners(partnerRules, chara, pioneer, unknown);
        var start = Compile(chara, currentEvent, rewards, partners);
        return unknown.Count == 0 ? start : start with { IgnoredMechanisms = [.. start.IgnoredMechanisms,
            $"故事 {start.Event.StoryId} 的 NPC 身份未核验，忽略对应羁绊或外出变化。target_id={string.Join(", ", unknown)}。"] };
    }

    public static PioneerStart.DeclaredEvent Compile(JsonObject chara, JsonObject currentEvent, JsonArray? rewards,
        ImmutableArray<EventPartner>? partners = null)
    {
        var contents = currentEvent["event_contents_info"] as JsonObject ?? throw new InvalidDataException("事件缺少event_contents_info。");
        var options = Objects(Array(contents, "choice_array"), "choice_array").ToArray();
        if (options.Length < 2) throw new InvalidDataException("选项奖励导入要求至少两个已揭示选项。");
        var declarations = rewards is null ? [] : Objects(rewards, "rewards").ToArray();
        var ignored = ImmutableArray.CreateBuilder<string>();
        var supportId = Int(contents, "support_card_id");
        var storyId = Int(currentEvent, "story_id");
        var character = Int(currentEvent, "chara_id");
        var choices = ImmutableArray.CreateBuilder<DeclaredEventChoice>(options.Length);
        var rewardKeys = new HashSet<int>();
        for (var index = 0; index < options.Length; index++)
        {
            var key = Int(options[index], "gain_select_id_index");
            if (!rewardKeys.Add(key)) throw new InvalidDataException("事件选项的奖励组身份重复。");
            var branches = ImmutableArray.CreateBuilder<DeclaredEventBranch>();
            foreach (var row in declarations.Where(row => Int(row, "select_index") == key))
            {
                if (row.TryGetPropertyValue("gain_param_array", out var declared) && declared is null)
                {
                    branches.Add(new([], MissingDeclaration: true));
                    continue;
                }
                var atoms = Array(row, "gain_param_array");
                var effects = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
                for (var atomIndex = 0; atomIndex < atoms.Count; atomIndex++)
                {
                    if (atoms[atomIndex] is null or JsonArray { Count: 0 })
                    {
                        var owner = supportId > 0 ? $"支援卡 {supportId}" : $"角色 {character}";
                        ignored.Add($"{owner}，故事 {storyId}，选项 {index + 1}，分支 {branches.Count} 的第 {atomIndex + 1} 项奖励为空，已忽略。");
                        continue;
                    }
                    var atom = atoms[atomIndex] as JsonObject ?? throw new InvalidDataException("事件奖励原子必须是对象。");
                    effects.Add(new(Int(atom, "display_id"), Int(atom, "effect_value_0"), Int(atom, "effect_value_1"), Int(atom, "effect_value_2")));
                }
                branches.Add(new(effects.ToImmutable(), MissingDeclaration: atoms.Count > 0 && effects.Count == 0));
            }
            if (branches.Count == 0) branches.Add(new([], MissingDeclaration: true));
            choices.Add(new(index + 1, branches.ToImmutable()));
        }
        if (declarations.Any(row => !rewardKeys.Contains(Int(row, "select_index"))))
            throw new InvalidDataException("奖励声明包含当前选项之外的组身份。");
        return new(Int(chara, "turn"), Int(chara, "playing_state"),
            new(Int(currentEvent, "event_id"), character, storyId, Int(currentEvent, "play_timing")),
            new(Int(chara, "speed"), Int(chara, "stamina"), Int(chara, "power"), Int(chara, "guts"), Int(chara, "wiz"), Int(chara, "vital"), Int(chara, "skill_point")),
            new(Int(chara, "max_speed"), Int(chara, "max_stamina"), Int(chara, "max_power"), Int(chara, "max_guts"), Int(chara, "max_wiz"), Int(chara, "max_vital")),
            Int(chara, "motivation"), [.. Integers(Array(chara, "chara_effect_id_array"), "chara_effect_id_array")],
            choices.MoveToImmutable(), partners, supportId) { IgnoredMechanisms = ignored.ToImmutable() };
    }
}
