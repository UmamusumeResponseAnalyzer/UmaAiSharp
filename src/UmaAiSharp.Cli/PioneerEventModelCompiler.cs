using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.Cli.Evidence;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Events;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;
using static UmaAiSharp.Cli.Evidence.PioneerMasterCompiler;

namespace UmaAiSharp.Cli;

public sealed record EventCalibrationResult(ImmutableArray<EventRewardSample> Samples,
    ImmutableArray<ForecastEventReward> Bindings, IReadOnlyDictionary<string, int> Counts, ImmutableArray<string> Conflicts);

public static class PioneerEventModelCompiler
{
    public static int Run(Dictionary<string, string> options)
    {
        string[] required = ["--events", "--model", "--output"];
        if (options.Count != required.Length || required.Any(key => !options.ContainsKey(key)))
            throw new InvalidDataException("事件分量编译需要--events事件窗口、--model源预测模型和--output新报告路径。");
        var output = Path.GetFullPath(options["--output"]);
        if (File.Exists(output)) throw new InvalidDataException("事件分量报告已存在，拒绝覆盖。");
        var input = JsonSerializer.Deserialize<ForecastModelInput>(File.ReadAllBytes(options["--model"]), PioneerExecutionCommand.JsonOptions)
            ?? throw new InvalidDataException("源模型为空。");
        var calibration = Compile(options["--events"], input);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, new { schemaVersion = 1, version = StatisticalEventRewards.Version,
            scope = "resourceComponentsOnly", input.MasterSha256, input.EvidenceSha256,
            modelSha256 = PioneerForecastCompiler.HashFile(options["--model"]),
            eventsSha256 = PioneerForecastCompiler.HashFile(options["--events"]),
            calibration }, PioneerExecutionCommand.JsonOptions);
        Console.WriteLine($"已编译{calibration.Samples.Length}条基础事件资源样本，涉及{calibration.Samples.Select(sample => sample.StoryId).Distinct().Count()}个故事；其余奖励原子单独保留，排除原因见报告。");
        return 0;
    }

    // Reward declarations retain values hidden by caps; transitions identify the actually chosen result.
    public static EventCalibrationResult Compile(string path, ForecastModelInput evidence)
    {
        var transitions = evidence.Transitions.ToDictionary(row => (row.Source, row.PacketId));
        var indices = evidence.Transitions.Select((row, index) => (row, index))
            .ToDictionary(item => (item.row.Source, item.row.PacketId), item => item.index);
        var decks = evidence.Transitions.Where(row => row.TrainingTemplate >= 0).GroupBy(row => row.Configuration)
            .ToDictionary(group => group.Key, group => evidence.TrainingTemplates[group.First().TrainingTemplate].Deck);
        var samples = ImmutableArray.CreateBuilder<EventRewardSample>();
        var bindings = ImmutableArray.CreateBuilder<ForecastEventReward>();
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var counts = new SortedDictionary<string, int>();
        var seen = new HashSet<(string Source, long Packet)>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var line in File.ReadLines(path))
        {
            var row = JsonNode.Parse(line)!.AsObject();
            var after = row["after"]!.AsObject();
            var source = after["source"]!.GetValue<string>();
            var packet = after["packetId"]!.GetValue<long>();
            if (!seen.Add((source, packet))) { Count("duplicateWindows"); continue; }
            Count("windows");
            if (Array(row, "excluded").Count != 0 || Array(row, "intermediate").Count != 0
                || !transitions.TryGetValue((source, packet), out var transition))
            { Count("outsidePairedAcceptedRuns"); continue; }
            if (row["rewards"] is not JsonObject rewards || rewards["paired"]?.GetValue<bool>() != true
                || rewards["contextUninterrupted"]?.GetValue<bool>() != true)
            { Count("noContinuousDeclaration"); continue; }
            var current = row["event"]!.AsObject();
            var before = row["beforeState"]!.AsObject();
            var post = row["afterState"]!.AsObject();
            var choice = Int(row["requestFields"]!.AsObject(), "choice_number");
            var options = Array(current["event_contents_info"]!.AsObject(), "choice_array");
            if (choice < 1 || choice > options.Count) { Count("noSelectableOption"); continue; }
            var key = Int(options[choice - 1]!.AsObject(), "gain_select_id_index");
            var branches = Objects(Array(rewards, "rewards"), "rewards").Where(value => Int(value, "select_index") == key).ToArray();
            var matching = new List<ImmutableArray<DeclaredEventEffect>>();
            var supported = true;
            foreach (var branch in branches)
            {
                if (Array(branch, "gain_param_array").Any(atom => atom is not JsonObject)) { supported = false; break; }
                var atoms = Objects(Array(branch, "gain_param_array"), "gain_param_array").Select(atom =>
                    new DeclaredEventEffect(Int(atom, "display_id"), Int(atom, "effect_value_0"), Int(atom, "effect_value_1"), Int(atom, "effect_value_2"))).ToImmutableArray();
                try
                {
                    var effects = Integers(Array(before, "chara_effect_id_array"), "chara_effect_id_array");
                    var result = new DeclaredEventRewards(new([.. atoms.Where(IsResource)])).Apply(Resources(before), Limits(before), Int(before, "motivation"), effects.Contains(25), effects.Contains(5));
                    if (result.Resources == Resources(post) && result.Limits == Limits(post) && result.Motivation == Int(post, "motivation")
                        && !matching.Any(previous => previous.AsSpan().SequenceEqual(atoms.AsSpan()))) matching.Add(atoms);
                }
                catch (UnknownMechanismException) { supported = false; break; }
            }
            if (!supported) { Count("hasUnmodeledAtoms"); continue; }
            if (matching.Count != 1)
            {
                Count(matching.Count == 0 ? "resourceProjectionMismatch" : "censoredBranchAmbiguity");
                if (matching.Count == 0) conflicts.Add($"{source}:{packet}");
                continue;
            }
            var cardId = Int(current["event_contents_info"]!.AsObject(), "support_card_id");
            var support = cardId == 0 ? null : decks[transition.Configuration].SingleOrDefault(card => card.CardId == cardId);
            if (cardId != 0 && support is null) { Count("unknownEventOwner"); continue; }
            if (support is not null)
                support = support with { Bond = Int(Objects(Array(before, "evaluation_info_array"), "evaluation_info_array")
                    .Single(partner => Int(partner, "target_id") == support.Position), "evaluation") };
            var scaling = support is null ? default : EventEffectScaling.FromSupport(support);
            var neutral = ImmutableArray.CreateBuilder<DeclaredEventEffect>();
            foreach (var atom in matching[0].Where(IsResource))
            {
                var inverse = atom.DisplayId == 1 ? scaling.InferUncappedBase(atom.Value0, atom.Value1) : atom.Value1;
                if (inverse is not { } basis) { supported = false; break; }
                neutral.Add(atom with { Value1 = basis });
            }
            if (!supported) { Count("bonusInverseConflict"); continue; }
            var master = row["master"] as JsonObject;
            var progress = master is null ? 0 : Int(master, "show_progress_1") * 100 + Int(master, "show_progress_2") * 10 + Int(master, "show_progress_3");
            var family = new EventRewardFamily(support is null ? 0 : support.TrainingCommandId == 0 ? 2 : 1,
                support?.TrainingCommandId ?? 0, Int(current, "event_id"), Int(current, "play_timing"),
                (Int(before, "turn") - 1) / 24, progress, choice);
            samples.Add(new(transition.Run, Int(current, "story_id"), family, neutral.ToImmutable(), [.. matching[0].Where(atom => !IsResource(atom))]));
            bindings.Add(new(indices[(source, packet)], support?.Position ?? 0, samples[^1]));
            Count("acceptedResourceSamples");
        }
        return new(samples.ToImmutable(), bindings.ToImmutable(), counts, conflicts.ToImmutable());
    }

    static bool IsResource(DeclaredEventEffect atom) => atom.DisplayId is 1 or 2
        && atom.Value0 is 1 or 2 or 3 or 4 or 5 or 10 or 20 or 30 && atom.Value2 == 0;

    static TrainingValues Resources(JsonObject state) => new(Int(state, "speed"), Int(state, "stamina"), Int(state, "power"),
        Int(state, "guts"), Int(state, "wiz"), Int(state, "vital"), Int(state, "skill_point"));
    static TrainingLimits Limits(JsonObject state) => new(Int(state, "max_speed"), Int(state, "max_stamina"), Int(state, "max_power"),
        Int(state, "max_guts"), Int(state, "max_wiz"), Int(state, "max_vital"));
}
