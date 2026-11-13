# UmaAiSharp.MCP

## 本机连接

按 [README](../README.md#构建) 构建 Release。客户端的 stdio 命令为 `dotnet`，参数为 `<仓库绝对路径>/src/UmaAiSharp.MCP/bin/Release/net10.0/UmaAiSharp.MCP.dll`。工具通过 `tools/list` 发现。路径由服务进程解析，跨客户端调用时使用绝对路径。全量模型和大批推荐可耗时数分钟，客户端超时应覆盖调用预算。

本页 JSON 示例是 `tools/call` 的 `params`。

```json
{
  "name": "pioneer_import_capture",
  "arguments": {
    "capturePath": "<抓包.sqlite>",
    "packetId": "<当前响应编号>",
    "masterPath": "<master.mdb>",
    "outputDirectory": "<新目录>"
  }
}
```

将结果中 `artifacts.input` 和 `artifacts.candidates` 传给 `pioneer_recommend` 的 `inputPath` 和 `candidatesPath`。输入所需的 master、种子和样本参数见 [Pioneer 执行与推荐](pioneer-mcp.md#根候选推荐)。种子和响应编号用十进制字符串传输。

## 结果

- `structuredContent.status` 为 `completed`、`stopped` 或 `cancelled`。执行停止时，摘要保留停止原因或停止计数。
- `summary.validation` 给出证据核验口径和结论，部分报告还提供阻塞项。工具完成只表示已完成该次操作。
- `artifacts` 将用途映射到本机绝对路径。完整报告、逐样本结果和模型留在文件中。输出文件必须是新文件，输出目录须为新目录或空目录。源数据库只读。
- 输入、文件和参数错误返回 `isError: true`。取消进行中的批量模拟会保留已完成和已取消的样本槽位。取消长扫描时，输出目录中可能留有部分文件，重试使用新目录。

## 工具

| 工作 | 工具 |
| --- | --- |
| 导入 | `pioneer_import_packet`、`pioneer_import_capture` |
| 执行与严格推荐 | `pioneer_run`、`pioneer_replay`、`pioneer_batch`、`pioneer_recommend` |
| 经验预测 | `pioneer_build_model`、`pioneer_calibrate_events`、`pioneer_forecast`。见 [条件经验预测](conditional-forecast.md)。 |
| 目标评价 | `goals_evaluate`。输入 `goalPath`、`outcomesPath` 和 `outputPath`。 |

### 证据工具

| 工作 | 工具 |
| --- | --- |
| 原包与报告 | `evidence_audit`、`evidence_validate`、`evidence_handoff`。见 [实包审计](evidence-audit.md)。 |
| 训练与人员 | `evidence_training`、`evidence_training_formula`、`evidence_training_modes`、`evidence_point_gain`、`evidence_placement_hint`、`evidence_hint_pool` |
| 剧本结算 | `evidence_construction`、`evidence_checkpoint`、`evidence_island_action`、`evidence_event_components`、`evidence_development_components` |
| 初态、比赛与终局 | `evidence_initial_state`、`evidence_race_route`、`evidence_race_points`、`evidence_terminal`、`evidence_scoring` |
