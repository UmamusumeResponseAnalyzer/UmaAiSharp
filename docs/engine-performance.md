# 执行引擎与 Pioneer 性能基准

使用 Release 构建 `benchmarks/UmaAiSharp.Benchmarks`。基准要求 x64／AVX2，显式比较 AVX-512 的复制项目还需要该指令集。

| 基准 | 测量范围 |
| --- | --- |
| `TrainingBenchmarks`、`IslandTrainingBenchmarks` | 以现行精确参考核为对照，比较紧凑数值核及预编译卡组。初始化核对普通层和剧本层的实际增量。 |
| `Execution*Benchmarks` | 普通／合宿失败实包的初始编译、复制、分叉、两步执行及 64／4,096 样本。报告单位为失败片段。 |
| `TrainingTurn*Benchmarks` | 当前训练行动的预览、单步结算和批量执行。 |
| `ConstructionBenchmarks`、`PlacementBenchmarks` | 有序建设方案的枚举与提交，以及人员板的生成。 |
| `CopyBenchmarks`、`IslandCopyBenchmarks` | 复制算子和复用工作区的状态复制。 |

夹具在计时外加载和核对。全量基准所需的本地实包投影见 [README](../README.md#本地验证材料)。冷编译、即时执行与大批量调度分开计量。

## 复跑

```powershell
$bench = 'benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll'
dotnet $bench --filter '*TrainingBenchmarks*' '*IslandTrainingBenchmarks*' --exporters json --artifacts '<新目录>'
$env:DOTNET_TieredCompilation = '0'
dotnet $bench allocation-check '<新报告.json>'
dotnet $bench profile ordinary '<新报告.json>'
dotnet $bench profile camp '<新报告.json>'
dotnet $bench profile island '<新报告.json>'
```

`profile` 比较 64／4,096 个样本在 1／2／4／8／16／32 worker 下的结果校验和。64 个样本主要用于测量小任务调度。报告包含累计托管分配、耗时、GC 次数和进程内存峰值。进程内存峰值也包含程序启动和夹具加载。

对完整育成搜索计时，可通过 [Pioneer MCP](pioneer-mcp.md#根候选推荐) 固定实包锚点、规则指纹、模型、目标、种子、首批样本与 worker 数。先对照排名、逐样本终局、停止位置和 RNG 末态，再比较耗时。报告保留实际加样后的样本量。
