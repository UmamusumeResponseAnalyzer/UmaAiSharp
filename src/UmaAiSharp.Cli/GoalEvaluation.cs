using System.Text.Json;
using UmaAiSharp.Core.Goals;

namespace UmaAiSharp.Cli;

public static class GoalEvaluation
{
    public static string ProbabilityText(GoalSummary summary)
        => summary.SuccessCount == 0
            ? $"模拟达标概率：0/{summary.SampleCount}（0%）；95%单侧统计区间[0%, {summary.ZeroSuccessProbabilityUpperBound95:P2}]。平均条件总缺口：{summary.MeanConstraintViolation:0.####}。"
            : $"模拟达标概率：{summary.SuccessCount}/{summary.SampleCount}（{summary.SuccessRate:P2}）。";

    public static GoalSummary Run(string goalPath, string outcomesPath, string outputPath)
    {
        var role = "目标文件";
        try
        {
            var goal = JsonSerializer.Deserialize<GoalDefinition>(File.ReadAllText(goalPath), GoalJson.Options)
                ?? throw new InvalidDataException("目标文件不能为 JSON null。");
            role = "终局指标文件";
            var outcomes = JsonSerializer.Deserialize<TerminalMetrics[]>(File.ReadAllText(outcomesPath), GoalJson.Options)
                ?? throw new InvalidDataException("终局指标文件必须为数组。");
            if (outcomes.Any(x => x is null))
                throw new InvalidDataException("终局指标数组不能包含 null。");
            var result = GoalEvaluator.Summarize(goal, outcomes);
            role = "输出报告";
            using var destination = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(destination, result, GoalJson.Options);
            return result;
        }
        catch (Exception error)
        {
            error.Data["FileRole"] = role;
            throw;
        }
    }
}
