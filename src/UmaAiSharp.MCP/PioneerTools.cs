using System.ComponentModel;
using ModelContextProtocol.Server;
using UmaAiSharp.Core;
using UmaAiSharp.Pioneer.Training;

namespace UmaAiSharp.MCP;

[McpServerToolType]
public static class PioneerTools
{
    [McpServerTool(Name = "pioneer_import_packet", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从单个 MessagePack 响应导入当前决策和候选。endpoint 为原响应端点。输出到新目录或空目录。")]
    public static ToolResult ImportPacket(string packetPath, string endpoint, string outputDirectory, CancellationToken cancellationToken,
        string? masterPath = null, string? rulesPath = null)
        => PioneerImport.Run(packetPath, endpoint, null, masterPath, outputDirectory, cancellationToken, rulesPath);

    [McpServerTool(Name = "pioneer_import_capture", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从抓包 SQLite 导入响应编号及此前的育成记录，生成输入、合法候选与导入报告。packetId 为正十进制字符串。")]
    public static ToolResult ImportCapture(string capturePath, string packetId, string outputDirectory, CancellationToken cancellationToken,
        string? masterPath = null, string? rulesPath = null)
        => PioneerImport.Run(capturePath, null, ToolArguments.PacketId(packetId), masterPath, outputDirectory, cancellationToken, rulesPath);

    [McpServerTool(Name = "pioneer_run", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("用 autonomous 输入执行一条轨迹。seed 为无符号十进制字符串。缺机制时返回停止位置。model 可选 precise 或 fast。")]
    public static ToolResult Run(string inputPath, string seed, string outputPath, CancellationToken cancellationToken,
        string? masterPath = null, string? rulesPath = null, string? goalPath = null, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, PioneerExecutionMode.Autonomous,
            masterPath: masterPath, cancellationToken: cancellationToken, rulesPath: rulesPath, goalPath: goalPath,
            model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_replay", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("用 conditional 输入及已识别的条件结果回放一条轨迹。seed 为无符号十进制字符串。model 可选 precise 或 fast。")]
    public static ToolResult Replay(string inputPath, string seed, string outputPath, CancellationToken cancellationToken,
        string? masterPath = null, string? rulesPath = null, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, PioneerExecutionMode.Conditional,
            masterPath: masterPath, cancellationToken: cancellationToken, rulesPath: rulesPath, model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_batch", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按输入文件的模式执行固定样本批次。seed 为无符号十进制字符串，samples 为正整数，workers 为 1 至 32。model 可选 precise 或 fast。")]
    public static ToolResult Batch(string inputPath, string seed, int samples, int workers, string outputPath,
        CancellationToken cancellationToken, string? masterPath = null, string? rulesPath = null, string? goalPath = null, string model = "precise")
        => PioneerExecution.Run(inputPath, ToolArguments.Seed(seed), outputPath, samples: samples,
            workers: workers, masterPath: masterPath, cancellationToken: cancellationToken, rulesPath: rulesPath, goalPath: goalPath,
            model: PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_recommend", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("比较合法根候选和育成目标。seed 为无符号十进制字符串，samples 为每候选首批样本数，自主训练回合按配对分差加样。workers 为 1 至 32。model 可选 precise 或 fast。")]
    public static ToolResult Recommend(string inputPath, string goalPath, string candidatesPath, string seed, int samples, int workers,
        string outputPath, CancellationToken cancellationToken, string? masterPath = null, string? rulesPath = null, string model = "precise")
        => PioneerRecommendation.Run(inputPath, goalPath, candidatesPath, ToolArguments.Seed(seed), samples,
            workers, outputPath, masterPath, cancellationToken, rulesPath, PioneerModel.Resolve(model));

    [McpServerTool(Name = "pioneer_forecast", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从抓包前缀进行经验过渡预测。packetId 和 seed 为十进制字符串。报告标记 empiricalTransition。")]
    public static ToolResult Forecast(string capturePath, string packetId, string masterPath, string modelPath, string goalPath,
        string seed, int samples, int workers, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            return PioneerForecast.Run(capturePath, ToolArguments.PacketId(packetId), masterPath, modelPath, goalPath,
                ToolArguments.Seed(seed), samples, workers, outputPath, cancellationToken);
        }
        catch (UnknownMechanismException error)
        {
            return PioneerExecution.WriteInitializationStop(capturePath, Path.GetFullPath(outputPath), error, "empiricalTransition");
        }
    }

    [McpServerTool(Name = "pioneer_build_model", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("从标准化证据、配对事件窗口和 master 编译经验预测模型。输出到新目录或空目录。")]
    public static ToolResult BuildModel(string evidenceDirectory, string eventsPath, string masterPath, string outputDirectory,
        CancellationToken cancellationToken)
        => PioneerForecastCompiler.Build(evidenceDirectory, eventsPath, masterPath, outputDirectory, cancellationToken);

    [McpServerTool(Name = "pioneer_calibrate_events", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("结合事件窗口和源模型提取基础奖励分量，输出校准报告。")]
    public static ToolResult CalibrateEvents(string eventsPath, string modelPath, string outputPath, CancellationToken cancellationToken)
        => PioneerEventModelCompiler.Run(eventsPath, modelPath, outputPath, cancellationToken);

    [McpServerTool(Name = "goals_evaluate", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("按目标 JSON 评价终局指标数组，计算联合达标率和目标效用。")]
    public static ToolResult EvaluateGoals(string goalPath, string outcomesPath, string outputPath)
        => ToolResult.Report(outputPath, GoalEvaluation.Run(goalPath, outcomesPath, outputPath));
}
