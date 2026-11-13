using System.Globalization;
using UmaAiSharp.Core;

namespace UmaAiSharp.MCP;

public sealed record ToolResult(string Status, object Summary, IReadOnlyDictionary<string, string> Artifacts)
{
    internal static ToolResult Report(string outputPath, object summary, string status = "completed")
        => new(status, summary, new Dictionary<string, string> { ["report"] = Path.GetFullPath(outputPath) });

    internal static Dictionary<string, string> Paths(string directory, params (string Role, string File)[] files)
        => files.ToDictionary(file => file.Role, file => Path.GetFullPath(Path.Combine(directory, file.File)));

    internal static string SearchStatus(SearchStatus status) => status switch
    {
        Core.SearchStatus.Completed => "completed",
        Core.SearchStatus.Stopped => "stopped",
        Core.SearchStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    internal static object[] SearchStops<TAction>(RootSearchResult<TAction> result) where TAction : notnull
        => result.Candidates.SelectMany(candidate => candidate.Batch.Results).Where(sample => sample.Stop.HasValue)
            .GroupBy(sample => (sample.Stop!.Value.Reason, sample.Stop.Value.Code))
            .Select(group => (object)new { group.Key.Reason, group.Key.Code, count = group.Count(),
                firstPosition = group.Min(sample => sample.Progress.Position) }).ToArray();
}

internal static class ToolArguments
{
    public static ulong Seed(string value)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : throw new InvalidDataException("seed 必须是 UInt64 范围内的十进制字符串。");

    public static long PacketId(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result : throw new InvalidDataException("packetId 必须是 Int64 范围内的正十进制字符串。");

    public static void ValidateBatch(int samples, int workers)
    {
        if (samples <= 0) throw new InvalidDataException("samples 必须是正整数。");
        if (workers is < 1 or > 32) throw new InvalidDataException("workers 必须是 1 至 32 的整数。");
    }
}
