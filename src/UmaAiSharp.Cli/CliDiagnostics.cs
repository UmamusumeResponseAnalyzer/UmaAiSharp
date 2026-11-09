using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UmaAiSharp.Cli;

public static class CliDiagnostics
{
    public static bool IsGoalValidationError(Exception error) =>
        error.TargetSite?.DeclaringType?.Namespace == "UmaAiSharp.Core.Goals";

    public static string Describe(Exception error)
    {
        var role = error.Data["FileRole"] as string ?? "命令输入/输出";
        return error switch
        {
            PlatformNotSupportedException => $"执行平台不支持：{error.Message}",
            InvalidDataException => $"{role}：{error.Message}",
            JsonException json => $"{role}：JSON校验失败，路径 {json.Path ?? "$"}，行 {json.LineNumber?.ToString() ?? "未知"}、字节 {json.BytePositionInLine?.ToString() ?? "未知"}（均从0开始）。"
                + (json.Message.Contains("missing required properties", StringComparison.Ordinal)
                    ? json.Message : Regex.Replace(json.Message, "'[^']*'|\"[^\"]*\"", "〈已省略〉")),
            SqliteException sqlite => $"{role}：SQLite读取失败，错误码 {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}。",
            IOException => $"{role}：文件操作失败（{error.GetType().Name}，HRESULT 0x{error.HResult:X8}）。",
            UnauthorizedAccessException => $"{role}：没有文件访问权限（HRESULT 0x{error.HResult:X8}）。",
            ArgumentException when IsGoalValidationError(error) => $"{role}：{error.Message}",
            ArgumentException argument => $"{role}：参数格式无效（参数 {argument.ParamName ?? "未提供名称"}，{error.GetType().Name}）。",
            _ => throw new ArgumentException("该异常不属于CLI输入错误。", nameof(error)),
        };
    }
}
