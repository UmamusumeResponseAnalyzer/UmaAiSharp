using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Development;
using UmaAiSharp.Pioneer.Events;

namespace UmaAiSharp.MCP.Evidence;

public sealed record TuckerFriendFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
    TuckerFriendContext Context, TuckerFriendState Before, TuckerFriendState After, int SkillPointGain);
public sealed record ProjectMemberFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
    int StoryId, int Turn, int ResultState, int TrainingCharacter, ImmutableArray<PioneerProjectSupport> Supports,
    ImmutableArray<PioneerProjectMember> Before, ImmutableArray<int> SelectedNpcIds,
    ImmutableArray<PioneerProjectMember> ExpectedMembers, ImmutableArray<PioneerProjectNpc> ExpectedNpcs);
public sealed record LateCheckpointFixture(string SourceSha256, long BeforeRecordId, long RequestRecordId, long AfterRecordId,
    int Turn, int Phase, int ResultState, int EventId, int StoryId, int PlayTiming, int ChoiceCount, int ChoiceNumber,
    ImmutableArray<int> ObservedDelta, bool TipsUnchanged, bool SkillsUnchanged, bool MembersUnchanged,
    int FollowingEventCount, bool MixedCheckpoint);
public sealed record PioneerEventComponentReport(IReadOnlyList<SourceFingerprint> Inputs,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<EvidenceIssue> Issues, ValidationSummary Validation);

public static class PioneerEventComponentAudit
{
    public static PioneerEventComponentReport Run(string inputDirectory, string fixtureDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new InvalidDataException("事件分量核验输出目录非空，拒绝覆盖。");
        var manifestPath = Path.Combine(fixtureDirectory, "event-component-provenance.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var paths = new List<(string Path, string Alias)> { (manifestPath, "event-component-provenance") };
        foreach (var expected in manifest.RootElement.GetProperty("inputs").EnumerateObject())
        {
            var path = Path.Combine(inputDirectory, expected.Name);
            if (Fingerprint(path, expected.Name).Sha256 != expected.Value.GetString())
                throw new InvalidDataException($"事件分量夹具与 {expected.Name} 的完整输入SHA-256不一致，禁止跨审计别名套用。");
            paths.Add((path, expected.Name));
        }
        foreach (var expected in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = expected.GetProperty("name").GetString()!;
            var path = Path.Combine(fixtureDirectory, name);
            if (Fingerprint(path, name).Sha256 != expected.GetProperty("sha256").GetString())
                throw new InvalidDataException($"事件分量夹具 {name} 的SHA-256不一致。");
            paths.Add((path, name));
        }
        var fingerprints = paths.Select(x => Fingerprint(x.Path, x.Alias)).ToArray();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var issues = new List<EvidenceIssue>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        Directory.CreateDirectory(outputDirectory);
        using var output = new StreamWriter(new FileStream(Path.Combine(outputDirectory, "event-component-cases.jsonl"), FileMode.CreateNew), new UTF8Encoding(false));
        foreach (var row in Read<TuckerFriendFixture>(Path.Combine(fixtureDirectory, "tucker-friend-observations.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count("Tucker.Checked");
            try
            {
                var candidate = TuckerEventSettlement.ApplyObservedComponents(row.Context, row.Before);
                var matches = candidate.After == row.After && candidate.SkillPointGain == row.SkillPointGain;
                Count(matches ? "Tucker.Matched" : "Tucker.Mismatch");
                output.WriteLine(JsonSerializer.Serialize(new { kind = "Tucker", row.SourceSha256, row.BeforeRecordId, row.RequestRecordId,
                    row.AfterRecordId, row.Context, Candidate = candidate, row.After, row.SkillPointGain, Matches = matches }, EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException)
            { issues.Add(new("TuckerComponentUnsupported", row.SourceSha256, row.AfterRecordId, error.Message)); }
        }
        foreach (var row in Read<ProjectMemberFixture>(Path.Combine(fixtureDirectory, "project-member-observations.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count("Members.Checked");
            try
            {
                var candidate = PioneerProjectMembers.ApplyObservedSelection(row.StoryId, row.Turn, row.ResultState, row.TrainingCharacter,
                    row.Supports, row.Before, row.SelectedNpcIds);
                var matches = candidate.Members.OrderBy(x => x.TargetId).SequenceEqual(row.ExpectedMembers.OrderBy(x => x.TargetId))
                    && candidate.AddedNpcs.OrderBy(x => x.TargetId).SequenceEqual(row.ExpectedNpcs.OrderBy(x => x.TargetId));
                Count(matches ? "Members.Matched" : "Members.Mismatch");
                Count(row.StoryId == 400011029 ? "Members.Initial" : "Members.Checkpoint");
                output.WriteLine(JsonSerializer.Serialize(new { kind = "Members", row.SourceSha256, row.BeforeRecordId, row.RequestRecordId,
                    row.AfterRecordId, row.StoryId, Candidate = candidate, Matches = matches }, EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException)
            { issues.Add(new("ProjectMemberComponentUnsupported", row.SourceSha256, row.AfterRecordId, error.Message)); }
        }
        foreach (var row in Read<LateCheckpointFixture>(Path.Combine(fixtureDirectory, "late-checkpoint-confirmations.jsonl")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count("Confirmation.Checked");
            try
            {
                var boundary = LateCheckpointConfirmation.ObserveAppliedBoundary(row.Turn, row.Phase, row.ResultState,
                    row.StoryId, row.PlayTiming, row.ChoiceCount);
                var confirmed = LateCheckpointConfirmation.Confirm(boundary, row.StoryId, row.ChoiceNumber);
                var matches = confirmed.Confirmed && row.ObservedDelta.Length == 9 && row.ObservedDelta.All(x => x == 0)
                    && row.TipsUnchanged && row.SkillsUnchanged && row.MembersUnchanged;
                Count(matches ? "Confirmation.Matched" : "Confirmation.Mismatch");
                if (row.MixedCheckpoint) Count("Confirmation.MixedCheckpoint");
                output.WriteLine(JsonSerializer.Serialize(new { kind = "Confirmation", row.SourceSha256, row.BeforeRecordId, row.RequestRecordId,
                    row.AfterRecordId, Candidate = confirmed, row.FollowingEventCount, Matches = matches }, EvidenceJson.LineOptions));
            }
            catch (Exception error) when (error is InvalidDataException or UnknownMechanismException or IllegalActionException)
            { issues.Add(new("LateCheckpointConfirmationUnsupported", row.SourceSha256, row.AfterRecordId, error.Message)); }
        }
        for (var i = 0; i < paths.Count; i++)
            if (Fingerprint(paths[i].Path, paths[i].Alias) != fingerprints[i])
                throw new InvalidDataException($"核验期间输入 {paths[i].Alias} 发生变化。");
        var report = new PioneerEventComponentReport(fingerprints, counts, issues, new("CandidateOnly",
            ["已给定选择或NPC身份结果的确定性分量；不生成事件或人员随机结果。",
                "9个混合响应的奖励归属与顺序未知；确认标记只能记录已经完整应用收益的观测边界。",
                "1496个Tucker、660个成员和220个确认窗口来自已用于推导的材料，不是独立验收。"]));
        File.WriteAllText(Path.Combine(outputDirectory, "pioneer-event-component-audit.json"), JsonSerializer.Serialize(report, EvidenceJson.Options), new UTF8Encoding(false));
        return report;
    }

    static IEnumerable<T> Read<T>(string path)
    {
        foreach (var line in File.ReadLines(path))
            yield return JsonSerializer.Deserialize<T>(line, EvidenceJson.ReadOptions) ?? throw new InvalidDataException($"夹具 {Path.GetFileName(path)} 包含空对象。");
    }
    static SourceFingerprint Fingerprint(string path, string alias)
    {
        using var stream = File.OpenRead(path);
        return new(alias, "PioneerEventComponentEvidence", Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }
}
