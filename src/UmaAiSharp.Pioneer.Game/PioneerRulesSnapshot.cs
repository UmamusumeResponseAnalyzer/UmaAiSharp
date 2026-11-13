using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UmaAiSharp.Pioneer;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Hints;
using UmaAiSharp.Pioneer.Inheritance;
using UmaAiSharp.Pioneer.Runtime;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.Pioneer.Game;

public sealed record PioneerRulesSnapshot(int SchemaVersion, string SourceMasterSha256, PioneerTrainingRulesData Training,
    PioneerFacilityRulesData Facilities, PioneerEventPartnersData EventPartners, ImmutableArray<PioneerRaceRule> Races,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PioneerMasterData? Construction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PioneerTurnCalibration? TurnCalibration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableArray<PioneerStoryProgress>? StoryProgress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PioneerProgressionData? Progression = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PioneerInheritanceData? Inheritance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<PioneerCardHints> CardHints = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<int> TraineeRandomStories = default)
{
    public const int StaticVersion = 1, CurrentVersion = 12;
    static readonly JsonSerializerOptions Json = new(PioneerGameJson.Options) { WriteIndented = false };

    public static PioneerRulesSnapshot Load(Stream resource)
    {
        using var brotli = new BrotliStream(resource, CompressionMode.Decompress, leaveOpen: true);
        var snapshot = JsonSerializer.Deserialize<PioneerRulesSnapshot>(brotli, Json)
            ?? throw new InvalidDataException("Pioneer 规则资源为空。");
        snapshot.Validate();
        return snapshot;
    }

    public void Write(Stream output)
    {
        Validate();
        using var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true);
        JsonSerializer.Serialize(brotli, this, Json);
    }

    public static (PioneerRulesSnapshot Snapshot, string Identity) ReadArtifact(string path)
    {
        using var resource = File.OpenRead(path);
        return ReadArtifact(resource);
    }

    public static (PioneerRulesSnapshot Snapshot, string Identity) ReadArtifact(Stream resource)
    {
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        bytes.Position = 0;
        var snapshot = Load(bytes);
        if (snapshot.SchemaVersion != CurrentVersion)
            throw new InvalidDataException($"回合续步规则要求版本 {CurrentVersion}，当前为 {snapshot.SchemaVersion}。");
        return (snapshot, "pioneer-rules/" + Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))));
    }

    public PioneerTurnRules CompileTurnRules(PioneerStart.TrainingTurn start, PioneerPlacementSeed placement)
    {
        if (TurnCalibration is null)
            throw new InvalidDataException("回合续步缺少拟合规则。");
        var progress = start.HintProgress ?? throw new InvalidDataException("整局起点缺少当前 Hint 和已学技能。");
        var hints = new PioneerHintRules(CardHints, Inheritance!.Skills, start.Deck, progress);
        var observed = start.ObservedStoryHistory ?? [];
        var supportStories = observed.Where(story => story.SupportCardId != 0
            && start.Deck.Any(card => card.TrainingCommandId != 0 && card.CardId == story.SupportCardId)).ToArray();
        var first = Math.Min(5, supportStories.Count(story => story.ProgressStage == 1));
        var second = Math.Min(first, supportStories.Count(story => story.ProgressStage == 2));
        var third = Math.Min(second, supportStories.Count(story => story.ProgressStage == 3));
        var trainee = Math.Min(7, observed.Where(story => TraineeRandomStories.BinarySearch(story.StoryId) >= 0)
            .Select(story => story.StoryId).Distinct().Count());
        return new(TurnCalibration, Training.Commands.Select(row => (row.CommandId, row.Level, row.FailureRate)),
            start, placement.AppearedSupportMask, placement.AppearedProjectMembers,
            Facilities.States, Facilities.Bases, Progression!.UraFirstPlaceFans,
            start.Inheritance is { } inheritance ? new(Inheritance, inheritance,
                start.CharacterId ?? start.CardId / 100, start.CardId, hints, progress.OwnedSkillIds) : null,
            hints, new(first, second, third, trainee), CompileRaceCalendar(start));
    }

    ImmutableArray<PioneerRaceOption>[] CompileRaceCalendar(PioneerStart.TrainingTurn start)
    {
        var calendar = new ImmutableArray<PioneerRaceOption>[79];
        var groups = Progression!.CharacterPrograms.Where(row => row.CharacterId == start.CardId / 100)
            .SelectMany(row => new[] { row.ProgramGroup, row.SecondProgramGroup }).Append(0).ToHashSet();
        var byDate = Races.Where(race => race.Grade is >= 100 and <= 800 && groups.Contains(race.ProgramGroup))
            .ToLookup(race => (race.Month, race.Half));
        foreach (var date in Progression.RaceCalendar)
            calendar[date.Turn] = [.. byDate[(date.Month, date.Half)].Where(race => race.Permission switch
                {
                    1 or 6 => date.Year == 1,
                    2 or 7 => date.Year == 2,
                    3 => date.Year is 2 or 3,
                    4 or 8 => date.Year == 3,
                    _ => false,
                }).Select(race => race.Option(false)).OrderByDescending(race => race.FirstPlaceFans).ThenBy(race => race.ProgramId)];
        return calendar;
    }

    public CheckpointMasterData CompileCheckpoints() => new(SourceMasterSha256, Training.PlanningPeriods,
        Progression!.CheckpointBonuses, Progression.CheckpointEffects);

    void Validate()
    {
        if (SchemaVersion is not (StaticVersion or CurrentVersion))
            throw new InvalidDataException($"Pioneer 规则资源版本 {SchemaVersion} 未识别。");
        if (SchemaVersion == CurrentVersion && (Construction is null || TurnCalibration is null || TurnCalibration.UraFans.IsDefaultOrEmpty
            || CardHints.IsDefaultOrEmpty || TraineeRandomStories.IsDefaultOrEmpty
            || TurnCalibration.TuckerRepeat.IsDefaultOrEmpty || TurnCalibration.Director.IsDefaultOrEmpty
            || TurnCalibration.TuckerRepeat.Length != 2 || TurnCalibration.Director.Length != 2
            || TurnCalibration.TuckerRepeat[0].Island == TurnCalibration.TuckerRepeat[1].Island
            || TurnCalibration.Director[0].Camp == TurnCalibration.Director[1].Camp
            || TurnCalibration.TuckerRepeat.Any(row => row.Opportunities <= 0 || row.Triggered < 0 || row.Triggered > row.Opportunities
                || row.StaminaRewards.IsDefaultOrEmpty || row.StaminaRewards.Any(reward => reward.Gain <= 0 || reward.Count <= 0)
                || row.StaminaRewards.Sum(reward => (long)reward.Count) > row.Triggered)
            || TurnCalibration.Director.Any(row => row.Boards <= 0 || row.Appearances < 0 || row.Appearances > row.Boards)
            || StoryProgress is not { IsDefaultOrEmpty: false } || Progression is null || Inheritance is null
            || Progression.UraFirstPlaceFans.Length != 3 || Progression.UraFirstPlaceFans.Any(fans => fans <= 0)
            || Progression.FixedRaceGoals.IsDefaultOrEmpty || Progression.CareerGoals.IsDefaultOrEmpty
            || Progression.CharacterPrograms.IsDefaultOrEmpty || Progression.RaceCalendar.IsDefaultOrEmpty
            || Construction.SourceSha256 != SourceMasterSha256))
            throw new InvalidDataException("Pioneer 规则资源缺少建设规则、回合校准、故事进度、继承或整局阶段数据。");
        if (SourceMasterSha256.Length != 64 || !SourceMasterSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Pioneer 规则资源缺少有效的源 master SHA-256。");
        if (Training.Commands.IsDefaultOrEmpty || Training.SupportCards.IsDefaultOrEmpty || Training.SupportLevels.IsDefaultOrEmpty
            || Training.SupportEffects.IsDefaultOrEmpty || Training.UniqueEffects.IsDefault || Training.Characters.IsDefaultOrEmpty
            || Training.CheckpointEffects.IsDefaultOrEmpty || Training.PlanningPeriods.IsDefaultOrEmpty
            || Facilities.States.IsDefaultOrEmpty || Facilities.Bases.IsDefaultOrEmpty
            || EventPartners.Supports.IsDefaultOrEmpty || EventPartners.Npcs.IsDefault || EventPartners.CharacterIds.IsDefaultOrEmpty
            || Races.IsDefaultOrEmpty || Training.SupportEffects.Any(effect => effect.Values.Length != 11))
            throw new InvalidDataException("Pioneer 规则资源的训练、设施、人员或比赛数据缺失。");
    }
}

public sealed record PioneerPlacementSeed(int AppearedSupportMask, ImmutableArray<int> AppearedProjectMembers)
{
    public static PioneerPlacementSeed Observe(JsonObject chara, JsonObject pioneer)
    {
        var evaluations = PioneerMasterCompiler.Objects(PioneerMasterCompiler.Array(chara, "evaluation_info_array"), "evaluation_info_array")
            .ToDictionary(row => PioneerMasterCompiler.Int(row, "target_id"), row => PioneerMasterCompiler.Int(row, "is_appear"));
        var mask = 0;
        for (var position = 1; position <= 6; position++)
            if (evaluations[position] == 1) mask |= 1 << (position - 1);
        var team = PioneerMasterCompiler.Objects(PioneerMasterCompiler.Array(pioneer, "evaluation_info_array"), "evaluation_info_array")
            .Where(row => PioneerMasterCompiler.Int(row, "is_pioneer_team") == 1)
            .Select(row => PioneerMasterCompiler.Int(row, "target_id"))
            .Where(target => target >= 1000 && evaluations[target] == 1).Order();
        return new(mask, [.. team]);
    }
}

public sealed record PioneerTrainingRulesData(ImmutableArray<PioneerTrainingRule> Commands, ImmutableArray<PioneerSupportCardRule> SupportCards,
    ImmutableArray<PioneerSupportLevelRule> SupportLevels, ImmutableArray<PioneerSupportEffectRule> SupportEffects,
    ImmutableArray<PioneerUniqueEffectRule> UniqueEffects, ImmutableArray<PioneerCharacterRule> Characters,
    ImmutableArray<PioneerCheckpointTrainingEffect> CheckpointEffects, ImmutableArray<PlanningPeriod> PlanningPeriods);
public readonly record struct PioneerTrainingRule(int CommandId, int Level, int BaseCommandId, int FailureRate);
public readonly record struct PioneerSupportCardRule(int Id, int Rarity, int EffectTableId, int UniqueEffectId, int CommandId, int SupportType);
public readonly record struct PioneerSupportLevelRule(int Rarity, int Level, int TotalExperience);
public readonly record struct PioneerSupportEffectRule(int TableId, int Type, ImmutableArray<int> Values);
public readonly record struct PioneerUniqueEffectRule(int Id, int Level, ImmutableArray<SupportUniqueEffect> Effects);
public readonly record struct PioneerCharacterRule(int Id, int CharacterId, TrainingValues GrowthPercent);
public readonly record struct PioneerCheckpointTrainingEffect(int Checkpoint, int Result, int Type, int Target, int Value);

public sealed record PioneerFacilityRulesData(ImmutableArray<TrainingFacility> States, ImmutableArray<ForecastFacilityBase> Bases);
public sealed record PioneerEventPartnersData(ImmutableArray<PioneerSupportIdentity> Supports,
    ImmutableArray<PioneerNpcIdentity> Npcs, ImmutableArray<int> CharacterIds);
public readonly record struct PioneerSupportIdentity(int CardId, int CharacterId, int Type);
public readonly record struct PioneerNpcIdentity(int PartnerId, int CharacterId);
public readonly record struct PioneerRaceRule(int ProgramId, int RequiredFans, string? Name, int Grade = 0, int FirstPlaceFans = 0,
    int ProgramGroup = 0, int Permission = 0, int Month = 0, int Half = 0)
{
    internal PioneerRaceOption Option(bool target) => new(ProgramId, Grade, FirstPlaceFans, target, Name, RequiredFans);
}
public readonly record struct PioneerStoryProgress(int StoryId, int Stage, int Length, int SupportCardId = 0);
public readonly record struct PioneerFixedRaceGoal(int Id, int Turn, int ProgramId);
public readonly record struct PioneerRaceDate(int Turn, int Year, int Month, int Half);
public readonly record struct PioneerCharacterRaceProgram(int CharacterId, int ProgramGroup, int SecondProgramGroup);
public sealed record PioneerProgressionData(ImmutableArray<CheckpointPointBonus> CheckpointBonuses,
    ImmutableArray<CheckpointEffect> CheckpointEffects, ImmutableArray<int> UraFirstPlaceFans,
    ImmutableArray<PioneerFixedRaceGoal> FixedRaceGoals, ImmutableArray<PioneerCareerGoal> CareerGoals,
    ImmutableArray<PioneerRaceDate> RaceCalendar, ImmutableArray<PioneerCharacterRaceProgram> CharacterPrograms);
