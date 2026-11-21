using System.ComponentModel;
using ModelContextProtocol.Server;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP;

[McpServerToolType]
public static class PioneerTools
{
    [McpServerTool(Name = "pioneer_import_packet", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从单个 MessagePack 响应导入当前决策和候选。endpoint 为原响应端点。输出到新目录或空目录。")]
    public static ToolResult ImportPacket(string packetPath, string endpoint, string rulesPath, string outputDirectory,
        CancellationToken cancellationToken)
        => PioneerImport.Run(packetPath, endpoint, null, rulesPath, outputDirectory, cancellationToken);

    [McpServerTool(Name = "pioneer_import_capture", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从抓包 SQLite 导入响应编号及此前的育成记录，生成输入、合法候选与导入报告。packetId 为正十进制字符串。")]
    public static ToolResult ImportCapture(string capturePath, string packetId, string rulesPath, string outputDirectory,
        CancellationToken cancellationToken)
        => PioneerImport.Run(capturePath, null, ToolArguments.PacketId(packetId), rulesPath, outputDirectory, cancellationToken);

    [McpServerTool(Name = "pioneer_run", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("用 autonomous 输入执行一条轨迹。seed 为无符号十进制字符串。缺机制时返回停止位置。model 可选 precise 或 fast。")]
    public static ToolResult Run(string inputPath, string seed, string rulesPath, string goalPath, string outputPath,
        CancellationToken cancellationToken, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, rulesPath, PioneerExecutionMode.Autonomous,
            cancellationToken: cancellationToken, goalPath: goalPath,
            model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_replay", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("用 conditional 输入及已识别的条件结果回放一条轨迹。seed 为无符号十进制字符串。model 可选 precise 或 fast。")]
    public static ToolResult Replay(string inputPath, string seed, string rulesPath, string outputPath,
        CancellationToken cancellationToken, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, rulesPath, PioneerExecutionMode.Conditional,
            cancellationToken: cancellationToken, model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_batch", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按输入文件的模式执行固定样本批次。seed 为无符号十进制字符串，samples 为正整数，workers 为 1 至 32。model 可选 precise 或 fast。")]
    public static ToolResult Batch(string inputPath, string seed, int samples, int workers, string rulesPath, string outputPath,
        CancellationToken cancellationToken, string? goalPath = null, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, rulesPath, samples: samples,
            workers: workers, cancellationToken: cancellationToken, goalPath: goalPath,
            model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_recommend", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("比较合法根候选和育成目标。seed 为无符号十进制字符串，samples 为每候选首批样本数，自主训练和建设规划按配对分差加样。workers 为 1 至 32。model 可选 precise 或 fast。")]
    public static ToolResult Recommend(string inputPath, string goalPath, string candidatesPath, string seed, int samples, int workers,
        string rulesPath, string outputPath, CancellationToken cancellationToken, string model = "precise")
        => PioneerRecommendation.Run(inputPath, goalPath, candidatesPath, ToolArguments.Seed(seed), samples,
            workers, outputPath, rulesPath, cancellationToken, PioneerModel.Resolve(model));

    [McpServerTool(Name = "goals_evaluate", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按目标 JSON 评价终局指标数组，计算联合达标率和目标效用。")]
    public static ToolResult EvaluateGoals(string goalPath, string outcomesPath, string outputPath)
        => ToolResult.Report(outputPath, GoalEvaluation.Run(goalPath, outcomesPath, outputPath));
}
