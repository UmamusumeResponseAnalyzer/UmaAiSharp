# 执行引擎与 Pioneer 性能验收

## Pioneer 同局路径对照

第二轮以插件 `2026.11.14.6` 的程序集为基线。临时得意率写入状态缓冲区末尾，追加和过期时只操作对应数据。岛训练复用卡位掩码对应的支援卡集合，设施基础值用栈上缓冲区收集。精确训练公式直接返回零基础值分量的零收益。

同机 Release 进程、32 worker、种子 `12345`、`precise` 模型和每候选首批 32 条。每组交替运行三次，表中为搜索耗时中位数。

| 实包面板 | 基线耗时 | 优化后 | 托管分配降幅 |
|---|---:|---:|---:|
| 第 1 回合训练 | 1.655 s | 1.450 s | 6.7% |
| 第 21 回合训练 | 5.079 s | 4.847 s | 7.9% |
| 第 24 回合建设 | 2.536 s | 2.440 s | 8.1% |
| 第 30 回合训练 | 1.756 s | 1.635 s | 9.2% |
| 第 39 回合合宿 | 397 ms | 383 ms | 8.2% |
| 第 72 回合训练 | 15.48 ms | 13.47 ms | 11.5% |

岛训练卡组表每次规则编译新增 4,128 字节共享预算。 [第二轮计时](../artifacts/performance/pioneer-937-pass2/benchmark-summary.json)

同局 83 个面板的 1,712,192 条轨迹与基线一致，逐步核对行动、阶段、结果、事件历史、临时得意率及其他状态分区，并比较终局结果、RNG、加样数量和排名。单 worker 与 `fast` 各另验三个面板。三组 stdio MCP 报告除运行计量与共享预算外逐项一致。 [第二轮路径核验](../artifacts/performance/pioneer-937-pass2/path-verification.json)

### 首轮

以 `89fa96b` 为基线，固定一局真实育成的 78 个回合面板和 5 个建设面板。沿用插件的 `precise`、种子 `12345`、每候选首批 32 条和原有加样规则。下表为同机 Release 进程使用 32 个 worker 的三次搜索中位数，计时包含加样与排序，输入编译和 JSON 写入在计时范围外。

| 实包面板 | 模拟轨迹 | 基线耗时 | 优化后 | 托管分配降幅 |
|---|---:|---:|---:|---:|
| 第 1 回合训练 | 14,336 | 2.03 s | 1.67 s | 37.6% |
| 第 24 回合建设 | 24,576 | 32.11 s | 2.66 s | 37.5% |
| 第 30 回合训练 | 10,240 | 2.28 s | 1.91 s | 37.3% |
| 第 39 回合合宿 | 7,168 | 545 ms | 409 ms | 39.0% |
| 第 72 回合训练 | 1,344 | 16.64 ms | 14.10 ms | 24.0% |

建设的 32 条样本由一个工作区扩展到最多 32 个，进程 CPU 时间中位数由 32.41 s 增至 41.89 s。人员分布按原整数权重抽样，直接复用按卡位排列的人员数组。岛训只计算当前友情设施数量所需的奖励档位。表中的分配为进程累计托管分配。 [计时记录](../artifacts/performance/pioneer-937-89fa96b/benchmark-summary.json)

83 个面板共比较 1,712,192 条轨迹和 84,797,312 次行动。逐样本行动序列、中间状态摘要、终局结果、两路 RNG 末态、加样数量及排名完全一致。另比较单 worker 与基线 32 worker 的三个面板、`fast` 模型前后的三个面板。真实 stdio MCP 比较建设、训练和合宿三份完整推荐报告，排除计时与分配字段后内容一致。 [路径核验](../artifacts/performance/pioneer-937-89fa96b/path-verification.json)

## 早期执行引擎基准

严格执行器为runtime v12，训练收益数值核为`jp-training-refit14-d6x3-r5-candidate-v5`；经验预测使用`pioneer-component-transitions-v3`。下方基准逐项标明测量版本，v4位宽验证见[数值修复记录](../artifacts/implementation/numeric-width-01/REPORT.md)，经验推荐整链计时见[本轮报告](../artifacts/implementation/forecast-integration-01/REPORT.md)。共享加32工作区8 GiB的最大配置实际编译峰值，以及全部负载的稳定性能矩阵尚未完成。

## 数值位宽修复（runtime v12／数值核v4）

同机相同基准源码、夹具，分别加载保留v3及当前v4程序集。每项2次启动、3轮预热、8轮150ms；误差为99.9%区间半宽。

| 负载 | v3均值±误差 | v4均值±误差 | 每次分配，前后相同 |
|---|---:|---:|---:|
| 普通预览 | 1.342±0.0432 μs | 1.343±0.0193 μs | 640 B |
| 普通执行，含复位 | 1.634±0.0259 μs | 1.511±0.0216 μs | 640 B |
| 合宿预览 | 2.122±0.0618 μs | 2.205±0.0687 μs | 1,504 B |
| 合宿执行，含复位 | 2.507±0.0678 μs | 2.374±0.0807 μs | 1,504 B |

普通执行均值降低7.5%，其余三项区间重叠；本轮未测出显著耗时回退，不外推整体吞吐改善。76配置、1,868,688条轨迹在六档worker、两种ISA及修复前后保持完整执行摘要一致。原包分量复核及程序集指纹见[核验结果](../artifacts/implementation/numeric-width-01/verification.json)，[修复前](../artifacts/implementation/numeric-width-01/bdn-before-03/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)和[修复后](../artifacts/implementation/numeric-width-01/bdn-after-03/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)保留分配及反汇编。

## 严格分配验收

人员分布和事件资源的严格0 B门槛在独立新进程执行。启动前固定分层编译设置，入口检查设置并输出每个热点的分配量与随机末态：

```powershell
$env:DOTNET_TieredCompilation='0'
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll allocation-check artifacts/allocation-new.json
Remove-Item Env:DOTNET_TieredCompilation
```

五个负载为单人分配、0／6／12名NPC联合分配，以及带片头痛／正向思考的事件资源。各预热10,000次再测10,000次；普通xUnit保留数值、RNG、状态隔离与worker不变量。runtime v12／数值核v3审计的原生和AVX2各三个新进程全部0 B，见[独立进程记录](../artifacts/audit-user-20261109-07/parallel/verification.json)。它是同步热点的分配验收；并行批次使用整个进程计数。

## runtime v12／数值核v3审计快照

同机BDN四项，2次启动、3轮预热、8轮150ms；下表误差为99.9%区间半宽。

| 负载 | 均值±误差 | 每次分配 |
|---|---:|---:|
| 普通训练预览 | 1.359±0.0612 μs | 640 B |
| 普通训练执行，含复位 | 1.735±0.1674 μs | 640 B |
| 合宿训练预览 | 2.113±0.0211 μs | 1,504 B |
| 合宿训练执行，含复位 | 2.483±0.0445 μs | 1,504 B |

预览分配等于对应完整单动作执行分配，可优先分析数值路径；该轮没有采集CPU／分配调用栈。4,096样本的全进程累计分配，普通为3.02～3.05 MB、合宿为6.56～6.59 MB。76配置的1,868,688条轨迹、456对跨ISA批次哈希一致。数据见[审计性能](../artifacts/audit-user-20261109-07/parallel/REPORT.md)。

## 建设热核优化（runtime v11）

设施槽位与累计master边成本在加载时预编译，第二个建设动作直接读写轨迹缓冲。相同两组真实输入的v10／v11 BDN对照中，建设确认由299～319 ns降至104 ns，分配由1.52～1.73 KiB降至0 B；完整两动作片段由2.10～2.13 μs降至1.82～1.84 μs。两组片段均值分别降低13.2%／13.9%，对应误差区间不重叠；配置编译时间区间重叠，不能判定加速。训练前置动作仍有分配。[逐项误差、输入与程序集指纹](../artifacts/audit-user-20261109-06/REPORT.md)。

## 训练到建设初始接入（runtime v10）

runtime v10／training-turn v10增加普通训练跨阈值到建设107的第二次决策。状态头保持264 B，设施、阈值与计划存入独占缓冲，master只在加载时读取。十个当前配置的共享加32工作区账本最大145,370 B。固定分层编译关闭、预热10,000次后的完整两动作片段分配2,320～2,632 B／次；未过线及失败片段为768 B／次。新增决策与结算增加了执行工作量，本轮未做前后吞吐比较或稳定BDN。原生／AVX2的245,880条轨迹及60对批次哈希一致。[当前输入与证据](training-construction.md)、[原生观测](../artifacts/implementation/training-construction-01/probe-native.json)、[AVX2观测](../artifacts/implementation/training-construction-01/probe-avx2.json)。

本轮交付紧凑状态、可复用工作区、精确分叉、固定任务批次及 Pioneer 已确认片段的执行入口。完整 Pioneer 开局到终局仍未闭合；本文所有“片段”均不等于一次完整育成。机制覆盖见 [Pioneer 执行边界](pioneer-runtime.md)，输入格式见 [MCP 执行](pioneer-mcp.md)。

## 训练升级待结算状态（runtime v9）

`training-level-trigger-01`增加一个nullable int字段保存待结算升级，主状态头从256B变为264B，保持无托管引用；连续缓冲没有增加分区。11个相同前态在独立进程固定`DOTNET_TieredCompilation=0`，预热及测量各10,000次，与保留的v8程序集逐项比较，热执行分配量相同。普通负载672～960B／次、合宿1536B／次，包含现有数值计算。[v8观测](../artifacts/implementation/training-level-trigger-01/allocation-before.json)、[v9观测](../artifacts/implementation/training-level-trigger-01/allocation-after.json)。

11个配置的共享加32工作区账本各增加254B（包括规则身份字符串差异），最大102,706B。六档worker、两种ISA共270,468条轨迹，66对批次哈希一致；本轮两套全测试各368项通过。没有新测稳定延迟或吞吐，以下表格保持其各自版本口径。[验证记录](../artifacts/implementation/training-level-trigger-01/verify-final/verification.json)。

## 条件支援与岛设施预编译（2026-11-09）

下表属于runtime v7的`training-compile-01`快照。其后runtime v8加入休息指令可用性及休息确认；十个休息片段在固定`DOTNET_TieredCompilation=0`独立进程中，预热10,000次后测量10,000次重置／完整片段执行，各为0B托管分配。原生和`DOTNET_EnableAVX512=0`均通过，另有六档worker的245,880条轨迹状态与双RNG一致；该单线程分配观测不包含批量结果及调度，不作为吞吐基准。[休息探针](../artifacts/implementation/rest-action-01/probe/Program.cs)、[原生结果](../artifacts/implementation/rest-action-01/probe-native.json)、[AVX2结果](../artifacts/implementation/rest-action-01/probe-avx2.json)。v8尚未重新获得完整BDN时间基线。

`training-compile-01`在runtime v7上预编译动态支援的羁绊、等级和粉丝分段，以及岛训练静态设施组合。表只读共享，保留各效果原子、整数运算顺序和条件错误；超过预编译容量预算时明确拒绝。数值规则、状态布局、随机流和停止位置不变。

同一输入、2次启动、3轮预热、8轮150ms的BDN前后测量如下。Error为99.9%区间半宽；基线为修改前v7，最终结果对应简化后的条件编译。首次中间版本的区间较宽，记录保留在`training-compile-after-01`，不用于下表结论。

| 负载 | 修改前 μs | 修改后 μs | 每次托管分配：前→后 |
|---|---:|---:|---:|
| 普通训练预览 | 1.320 ±0.0245 | 1.352 ±0.0351 | 640→640 B |
| 普通训练行动 | 1.624 ±0.0220 | 1.624 ±0.0135 | 640→640 B |
| 合宿训练预览 | 2.207 ±0.0701 | 2.123 ±0.0202 | 1728→1504 B |
| 合宿训练行动 | 2.485 ±0.0412 | 2.483 ±0.0771 | 1728→1504 B |
| 岛训练1058片段 | 2.587 ±0.1009 | 2.344 ±0.0259 | 1664→1440 B |
| 岛训练3298片段 | 2.938 ±0.0422 | 2.541 ±0.0592 | 1544→1440 B |

岛训练两项耗时分别减少约9%和14%。普通／合宿行动耗时基本持平，普通预览区间重叠；没有把合宿预览的小幅变化单独认定为加速。剩余分配包括精确数值核，不能称训练整体零分配。[修改前](../artifacts/performance/training-compile-before-02/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)、[训练最终结果](../artifacts/performance/training-compile-after-02/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)、[岛训练修改前](../artifacts/performance/training-compile-before-02/results/UmaSharpAi.Benchmarks.IslandSegmentBenchmarks-report-github.md)、[岛训练最终结果](../artifacts/performance/training-compile-after-02/results/UmaSharpAi.Benchmarks.IslandSegmentBenchmarks-report-github.md)。

空间和编译成本：普通／合宿失败配置各3个独立新进程，首次配置编译中位数由25.94／28.93ms变为27.21／30.25ms，包含首次JIT、不含JSON解析；编译分配分别增加1288B。共享加32工作区的保守账本由46,178／50,574B变为77,410／81,806B，包含预留编译暂存空间，不能解释为实际存活内存。原型的LINQ分段编译增加约5ms，已改为直接循环；最终仍有约1.3ms的首次编译成本。原始记录位于`artifacts/implementation/training-compile-01/cold-{before,final}-{ordinary,camp}-{1,2,3}.json`。

Release361项、AVX2针对性32项通过，另复跑独立量化、两层截断、动态固有、RNG和decimal探针。七个岛训练起点各64种羁绊组合的正式执行与精确参考一致；原子跨1200和上限顺序保留。以下各节为其注明版本的历史测量。最大受支持配置的8GiB实际峰值、零分配单测的隔离验收及完整育成吞吐仍未闭合。

2026-11-09 runtime v6增加成功普通训练历史分区，并共用Tucker／通用事件体力结算。已知计数增加五个整数的有效数据；未知历史不分配该分区内容。历史恢复位于CLI加载边界，执行只维护计数。该快照通过358项测试及31项AVX2针对性检查，未重新测量BDN；下方v5及更早快照的时间不能作为v6实测。

## 失败效果结算与完整训练前态基准

`failure-effects-02`和`failure-effects-batch-01`使用完整普通/合宿`TrainingTurn`前态，经两次选择停在下一回合事件窗口。失败值状态和事件身份不含托管引用，热路径直接读取羁绊span。34组BDN完成2次启动、3轮预热、8轮200ms测量，保持系统电源计划；[计时前184项源码、DLL和输入指纹](../artifacts/implementation/failure-effects-01/source-hashes.json)在测量后复核。

| 当前负载 | 普通：均值±99.9%半宽 | 合宿：均值±99.9%半宽 | 托管分配（普通/合宿） |
|---|---:|---:|---:|
| 编译完整配置（预热后） | 11.29±0.433 μs | 17.82±0.636 μs | 23.98/28.22 KiB |
| 创建状态副本 | 20.11±0.723 ns | 21.54±0.622 ns | 464/512 B |
| 复制到预留工作区 | 5.281±0.426 ns | 5.334±0.082 ns | 0/0 B |
| 失败选择处精确分叉 | 8.919±0.642 ns | 7.365±0.228 ns | 0/0 B |
| 复位并执行两动作失败片段 | 232.1±3.94 ns | 230.5±3.89 ns | 0/0 B |

4096片段的普通/合宿批次，1 worker为1,028.3±16.30 / 1,035.6±13.67 μs，16 worker为202.4±3.50 / 204.1±3.14 μs，32 worker为213.6±8.82 / 213.3±8.95 μs。64片段约14.9–16.2 μs，只有一个逻辑块。这组负载32 worker未优于16 worker；数值与RNG一致性独立验证。

同一审计探针、关闭分层JIT的独立进程中，失败片段分配从448 B降为0 B；4096片段全进程分配从约2.23–2.26 MB降至约0.40–0.42 MB，结果数组和调度分配仍在。比较采用相同完整前态，不将历史精简起点的时间当作当前加速基线。[原探针](../artifacts/audit-current-review-02/parallel/allocation-failure.json)、[当前探针](../artifacts/implementation/failure-effects-01/allocation-failure.json)。

独立新进程首次配置/根状态/脚本编译为48.90/53.52 ms，分配27,976/33,784 B，包含首次JIT而不含JSON读取。共享加32工作区估算为45,968/50,364 B。进程峰值包含启动及输入解析，这两个配置的观测不构成最大受支持配置的8 GiB峰值验收。[普通观测](../artifacts/implementation/failure-effects-01/profile-ordinary.json)、[合宿观测](../artifacts/implementation/failure-effects-01/profile-camp.json)。

有效报告：[编译](../artifacts/performance/failure-effects-02/results/UmaSharpAi.Benchmarks.ExecutionCompilationBenchmarks-report-github.md)、[复制](../artifacts/performance/failure-effects-02/results/UmaSharpAi.Benchmarks.ExecutionCopyBenchmarks-report-github.md)、[分叉](../artifacts/performance/failure-effects-02/results/UmaSharpAi.Benchmarks.ExecutionForkBenchmarks-report-github.md)、[片段](../artifacts/performance/failure-effects-02/results/UmaSharpAi.Benchmarks.ExecutionSegmentBenchmarks-report-github.md)、[64样本](../artifacts/performance/failure-effects-batch-01/results/UmaSharpAi.Benchmarks.ExecutionSmallBatchBenchmarks-report-github.md)、[4096样本](../artifacts/performance/failure-effects-batch-01/results/UmaSharpAi.Benchmarks.ExecutionLargeBatchBenchmarks-report-github.md)。沙箱内`failure-effects-01`未构建成功，未计入测量；有效运行在沙箱外完成。

## 全队羁绊与训练失败接入快照

`training-transition-01`对应全编成固有检查、64项羁绊加成表及训练到失败选择的接入。主状态头保持248B，每个编成新增256B共享查表，已计入容量账本。使用同一普通／合宿夹具，2次独立启动、3轮预热、8轮200ms测量；[185项源码、程序集和夹具哈希](../artifacts/performance/training-transition-01/source-hashes.json)在计时前记录。

| 当前负载 | 均值 ± 99.9%区间半宽 | 每次托管分配 |
|---|---:|---:|
| 普通训练预览 | 1.396 ± 0.035 μs | 640 B |
| 普通训练重置并执行 | 1.709 ± 0.030 μs | 640 B |
| 合宿训练预览 | 2.245 ± 0.066 μs | 1728 B |
| 合宿训练重置并执行 | 2.629 ± 0.060 μs | 1728 B |

与下方`joint-placement-01`相比，四项区间均重叠，分配量相同；该组测量没有分辨出稳定的速度变化。新表查询未增加这两种负载的热路径托管分配。结果及反汇编见[BDN报告](../artifacts/performance/training-transition-01/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)。新失败链的后续测量见本文首节。

## 轨迹面板与联合采样快照

`joint-placement-01`对应248B主状态头及轨迹独占的支援/Hint/PJ面板，使用相同训练夹具，2次独立启动、3轮预热、8轮200ms测量。运行7组基准，输入与程序集身份见[哈希清单](../artifacts/performance/joint-placement-01/source-hashes.json)。

| 当前负载 | 均值 ± 99.9%区间半宽 | 每次托管分配 |
|---|---:|---:|
| 6支援、无PJ联合面板 | 147.8 ± 2.28 ns | 0 B |
| 6支援、6 PJ联合面板 | 270.4 ± 4.94 ns | 0 B |
| 6支援、12 PJ联合面板 | 611.9 ± 12.36 ns | 0 B |
| 普通训练预览 | 1.421 ± 0.038 μs | 640 B |
| 普通训练重置并执行 | 1.673 ± 0.043 μs | 640 B |
| 合宿训练预览 | 2.235 ± 0.092 μs | 1728 B |
| 合宿训练重置并执行 | 2.608 ± 0.060 μs | 1728 B |

联合采样计入Hint抽取及超容量重抽，使用下方六卡和0/6/12 PJ配置，未计入规则编译。训练仍执行到训练后事件窗口。与`training-turn-02`相比，普通执行均值增加约8.2%、合宿约6.3%，分配量相同；当前改动提供可变化的轨迹面板，不作为执行加速交付。这是跨进程快照比较，尚未分离面板解码、状态复制和测量环境各自的影响。动态固有解析与BigInteger成本继续保留。

[联合采样结果](../artifacts/performance/joint-placement-01/results/UmaSharpAi.Benchmarks.PlacementBenchmarks-report-github.md)、[训练结果](../artifacts/performance/joint-placement-01/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)。本轮未复测完整批量BDN，也未完成8GiB配置编译峰值验收。

## 普通与合宿训练行动快照

`training-turn-02`覆盖面板移入主状态前的普通与合宿查询、行动执行以及64/4096样本、1/2/4/8/16/32 worker。28组BDN全部生成测量值；每组2次独立启动、3轮预热、8轮200ms测量。首次沙箱内尝试未完成子进程构建，未计入结果；第二次测量在本机执行，保持电源计划。

| 当前负载 | 时间（均值 ± 99.9%区间半宽） | 每次托管分配 |
|---|---:|---:|
| 普通训练预览 | 1.329 ± 0.044 μs | 640 B |
| 普通训练重置并执行 | 1.546 ± 0.039 μs | 640 B |
| 合宿训练预览 | 2.254 ± 0.052 μs | 1728 B |
| 合宿训练重置并执行 | 2.454 ± 0.075 μs | 1728 B |
| 普通训练4096样本，1 worker | 6.495 ± 0.070 ms | — |
| 普通训练4096样本，32 worker | 0.644 ± 0.044 ms | — |
| 合宿训练4096样本，1 worker | 9.706 ± 0.152 ms | — |
| 合宿训练4096样本，32 worker | 1.158 ± 0.075 ms | — |

此表的分配量来自单线程MemoryDiagnoser；该批量基准未统计全进程分配。负载为已识别成功的单次训练，执行至训练后事件待处理位置。预览含收益、失败率和友情资格；执行还包含状态/随机流重置、资源/Pt/羁绊/次数/溢出结算。64样本只有一个任务块，普通约97–100μs、合宿约154–157μs。

[单次决策及分配](../artifacts/performance/training-turn-02/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-report-github.md)、[全部批量结果](../artifacts/performance/training-turn-02/results/UmaSharpAi.Benchmarks.TrainingTurnBatchBenchmarks-report-github.md)、[反汇编](../artifacts/performance/training-turn-02/results/UmaSharpAi.Benchmarks.TrainingTurnDecisionBenchmarks-asm.md)。合宿的静态设施组合已预编译；动态固有数组和BigInteger仍是热路径成本。下方保留此前片段的测量快照。

## 岛训练对象分配更新

`forward-engine-01` 使用当前源码运行相同 `island-3298`、4,096片段及1／2／4／8／16／32 worker。全进程累计分配从审计快照的36,312,000～36,424,992 B降至6,818,984～6,846,872 B，约减少81%。执行直接消费状态span，移除了逐次DTO、字典和排序集合，保留原精确数值顺序。剩余成本包含动态固有效果解析、BigInteger及批量结果。

[更新前](../artifacts/audit-current-20261108/parallel/profile-island-current.json)、[更新后](../artifacts/validation/forward-engine-01/profile-island-final.json)均为独立进程辅助观测；本文不据单次耗时给出稳定吞吐加速倍数。下方BDN表格保留其原快照口径。

## 测量口径

角色结局推荐已按固定属性上限接入目标曲线预计算。事件、角色结局及推荐入口尚未完成当前快照的稳定BDN验收；64任务块的小批并行度仍需单独测量。当前预编译测量见本文首节，其他机制与入口验收见[实施报告](implementation-status.md)。

下方原执行引擎的BDN与272,902项全语料结果对应 `engine-implementation-05` 的源码快照。后续修正了训练等级顺序和整数中间值，并加入建设动作；当前验证记录位于 [实施报告](implementation-status.md)，各节分别保留其测量范围和指纹。

- Windows 11，AMD Ryzen 9 9950X3D，16核／32逻辑处理器；.NET SDK 10.0.401，运行时10.0.14，Release x64。
- BenchmarkDotNet 0.15.8，2个独立进程启动，每次3轮预热、8轮测量、目标每轮200ms。没有改动系统电源计划。均值旁的 Error 为 BDN 的99.9%置信区间半宽，不把测量噪声当成收益。
- 复制／数值基准带分配与反汇编诊断。并行批次另用 `GC.GetTotalAllocatedBytes(true)` 统计进程总分配与 GC 次数；进程峰值包含启动、夹具解析、预热与此前批次，不能解读成单个工作区的大小。
- 最初两次沙箱内 BDN 子进程构建失败，未产生测量值。有效结果为沙箱外的 `kernel-03`、`execution-01`、`island-01`；这些失败报告没有参与比较。
- 产品未增加运行依赖。仅基准工程使用 BenchmarkDotNet。NuGet 漏洞索引访问失败时保留 NU1900 警告，没有关闭审计。

原始 Release DLL 与源码指纹位于 `artifacts/performance/engine-baseline-01/`。基准在独立 AssemblyLoadContext 加载原训练实现，通过一次编译的委托调用；其调用包装成本包含在原基线时间内。当前精确参考核也单独测量，避免把包装差异解释成算法收益。新执行器之前没有真实 Pioneer 状态机，因此其延迟／吞吐只报告绝对值，不构造不存在的旧整局基线。

## 训练数值核

三条固定普通训练输入来自既有实包研究，JSON 保留来源指纹和包号。开始计时前核对冻结基线、当前参考核与数值核的两层数值相等。

| 输入 | 原 Release 审计核 | 当前精确参考核 | 预编译数值核 | 分配：原 → 预编译 |
|---|---:|---:|---:|---:|
| 无支援参与 | 4.543 ±0.303 μs | 4.412 ±0.247 μs | 1.543 ±0.129 μs | 3184 → 776 B |
| 多支援参与 | 5.962 ±0.395 μs | 5.764 ±0.413 μs | 1.820 ±0.141 μs | 8248 → 1480 B |
| 高属性 | 5.476 ±0.365 μs | 5.213 ±0.297 μs | 1.741 ±0.189 μs | 7232 → 1152 B |

这三条输入的均值约为原审计核的31%～34%，分配减少76%～84%。收益来自去掉审计字符串、静态支援解析缓存及省去分数中间约分；不是改变取整或随机质量。该范围不能推广到所有卡组、全阶段或整局吞吐。

`TrainingFraction` 继续使用 BigInteger，保留 D6／R5 有效数字量化和原子整除顺序。尚未证明全部合法输入可用定宽整数承载，因此没有加入 Int64／Int128 猜测边界、浮点、epsilon 或按输入切换的慢路径。动态面板和 BigInteger 仍有分配，不能称为零分配数值核。

报告：[训练结果](../artifacts/performance/kernel-03/results/UmaSharpAi.Benchmarks.TrainingBenchmarks-report-github.md)、[反汇编](../artifacts/performance/kernel-03/results/UmaSharpAi.Benchmarks.TrainingBenchmarks-asm.md)。

岛训练另外选择两个真实前态：回合10、5支援／0 NPC／五座普通设施未建；回合66、6支援／12 NPC／含两座Lv5设施。两者都经公共 master 编译入口导出，没有导入行动后状态。

| 岛训练输入 | 精确参考核 | 预编译数值核 | 分配：参考 → 预编译 |
|---|---:|---:|---:|
| 回合10 | 10.130 ±0.620 μs | 2.958 ±0.110 μs | 24.99 → 1.73 KiB |
| 回合66 | 12.966 ±1.031 μs | 3.382 ±0.271 μs | 32.89 → 1.63 KiB |

报告：[岛训练数值核](../artifacts/performance/island-01/results/UmaSharpAi.Benchmarks.IslandTrainingBenchmarks-report-github.md)。数值核没有生成完整事件后果；原有精确审计实现仍保留。

## 复制算子选择

| 连续载荷 | `Span.CopyTo` | 手写 AVX2 | 手写 AVX-512 |
|---|---:|---:|---:|
| 128 B | 2.085 ±0.072 ns | 3.362 ±0.097 ns | 2.670 ±0.257 ns |
| 1 KiB | 5.820 ±0.516 ns | 10.194 ±0.879 ns | 7.697 ±0.623 ns |
| 16 KiB | 122.152 ±7.131 ns | 145.602 ±14.532 ns | 137.484 ±6.852 ns |

保留 `Span.CopyTo`；没有把手写向量循环投入生产。标准库可以选择运行时支持的指令，这与自写AVX-512执行核是两回事。三者都在测量前核对复制内容，裸数据复制不跨越托管引用对象。

报告：[复制结果](../artifacts/performance/kernel-03/results/UmaSharpAi.Benchmarks.CopyBenchmarks-report-github.md)、[反汇编](../artifacts/performance/kernel-03/results/UmaSharpAi.Benchmarks.CopyBenchmarks-asm.md)。

## 状态分叉与决策片段

| 负载 | 均值 | 每次托管分配 |
|---|---:|---:|
| 失败起点分配新副本 | 20.39～20.84 ns | 336 B |
| 失败起点 `CopyInto` | 5.08～6.07 ns | 0 B |
| 失败中间决策的精确执行分叉 | 7.08 ns | 0 B |
| 重置并运行普通／合宿失败两次决策 | 215.9～220.8 ns | 128 B |
| 岛训练起点／已结算分量分配新副本 | 27.67～32.27 ns | 552 B |
| 同一批岛训练状态 `CopyInto` | 6.03～6.81 ns | 0 B |
| 重置并运行岛训练已知单段 | 4.263～4.796 μs | 7.16～8.41 KiB |

这些微基准复用已配置的场景和脚本，片段计时包含重置状态／随机流。两次失败决策分别在真实失败选择及未知下一回合事件处停下；岛训练停在未知后续事件处。预留工作区后的复制零分配，不代表结算路径零分配。

报告：[状态复制](../artifacts/performance/execution-01/results/UmaSharpAi.Benchmarks.ExecutionCopyBenchmarks-report-github.md)、[精确分叉](../artifacts/performance/execution-01/results/UmaSharpAi.Benchmarks.ExecutionForkBenchmarks-report-github.md)、[失败决策](../artifacts/performance/execution-01/results/UmaSharpAi.Benchmarks.ExecutionSegmentBenchmarks-report-github.md)、[岛训练状态复制](../artifacts/performance/island-01/results/UmaSharpAi.Benchmarks.IslandCopyBenchmarks-report-github.md)、[岛训练决策](../artifacts/performance/island-01/results/UmaSharpAi.Benchmarks.IslandSegmentBenchmarks-report-github.md)。

## 批量与线程数

每批4096个固定任务，表中每个任务都是一个已确认片段：

| worker | 普通失败两决策／批 | 重型岛训练／批 | 岛训练片段／秒，均值换算 |
|---:|---:|---:|---:|
| 1 | 969.6 ±7.5 μs | 17.781 ±0.290 ms | 230,356 |
| 2 | 586.2 ±15.3 μs | 10.004 ±1.552 ms | 409,440 |
| 4 | 367.6 ±4.3 μs | 6.257 ±2.182 ms | 654,585 |
| 8 | 280.6 ±3.9 μs | 4.086 ±1.744 ms | 1,002,570 |
| 16 | 257.0 ±8.4 μs | 3.006 ±0.761 ms | 1,362,427 |
| 32 | 258.3 ±7.2 μs | 3.176 ±0.721 ms | 1,289,875 |

失败负载每片段消费2个动作，岛训练消费1个动作；换算动作吞吐时应据此区分。重型岛训练的并行方差较大，不据均值给16与32 worker排出确定名次，也不将此结果称为“育成/秒”。任务数量保持一致；线程数没有改变模拟质量或截断深度。

64任务的小批仅有一个固定64任务块，只有领取该块的worker执行轨迹。普通失败小批约15.6～17.5 μs；重型岛训练小批约288～385 μs，线程增加没有明确收益。该调度取舍及小批开销保留在报告中，不通过改变样本数掩盖。

报告：[4096任务失败批次](../artifacts/performance/execution-01/results/UmaSharpAi.Benchmarks.ExecutionLargeBatchBenchmarks-report-github.md)、[64任务失败批次](../artifacts/performance/execution-01/results/UmaSharpAi.Benchmarks.ExecutionSmallBatchBenchmarks-report-github.md)、[岛训练批次](../artifacts/performance/island-01/results/UmaSharpAi.Benchmarks.IslandBatchBenchmarks-report-github.md)。

## 冷启动、分配与峰值

两个独立进程分别进行普通／合宿失败场景及脚本的首次编译：6.323／7.278 ms，1720 B托管分配，未触发GC。夹具读取／JSON解析在此计时外，JIT在计时内。预热后的配置编译微基准为约63～64 ns、816 B；不能用该微基准值代表启动时间。一次真实岛训练CLI冷进程观测的输入编译为113.18 ms、首次执行25.17 ms，包含加载与JIT，不能替代稳定微基准。

| 进程级观测 | 4096任务托管分配，1..32 worker | 观测进程生命周期RSS峰值 | 共享＋32工作区预算 |
|---|---:|---:|---:|
| 失败两决策 | 2.22～2.24 MiB | 47.3 MiB | 20,068 B |
| 重型岛训练 | 34.63～34.73 MiB | 97.1 MiB | 57,310 B |

预算中的单工作区分别496 B和708 B，包含执行上下文；共享数据分别4196 B和34,654 B。容量没有在轨迹执行中扩张。进程分配包含全部worker与返回结果，不是 `GetAllocatedBytesForCurrentThread`；一次预热后的独立观测可能仍处于分层JIT阶段，因此不将其与稳定单轨 `MemoryDiagnoser` 数字直接相减。岛训练4096批次最多观察到Gen0两次、Gen1一次、Gen2一次，逐次记录保留在JSON。

报告：[合宿进程观测](../artifacts/performance/profile-camp-01.json)、[普通进程观测](../artifacts/performance/profile-ordinary-01.json)、[岛训练进程观测](../artifacts/performance/profile-island-01.json)。这些峰值只认证已测配置，不能证明任意未实现剧本／卡组的容量。

## 数值与机制验证

扩大语料重算了272,902项可重建训练输入：普通221,336、合宿24,890、岛训练26,676。数值核与精确参考核逐项相同，可见收益层也维持零反例。另11,976项因黄金船历史未知明确排除；没有填造历史来增加分母。报告见 [training-modes-engine-01](../artifacts/evidence/training-modes-engine-01/training-modes-audit.json)。

900组生成输入覆盖三模式及两种数值入口；其他用例核对量化边界、1200跨越、上限、原子取整、大整数和并发共享。参考核对拍证明实现一致，不证明候选公式唯一，也不构成独立游戏机制验收。

正式执行器包含两条真实失败多决策窗口、七条岛训练行动、建设107／108、评审和其他已确认事件分量。条件结果仅给出失败事件、已识别惩罚、Hint组或NPC身份，不提供后态或未来队列。停止点及已消费／已结算计数逐项核对，停止后继续不会再次扣券、发奖或消费动作。

最终Release验收293/293通过，见 [测试记录](../artifacts/validation/engine-implementation-05/engine-final.trx)。另在独立CLI进程中比较普通失败、合宿失败及重型岛训练：原生ISA和禁用AVX-512的AVX2设置，各自1／2／4／8／16／32 worker、131个样本，共36组逐样本完整结果相同；3组单轨最终局部状态相同，停止位置和RNG末态相同。禁用AVX2时启动被明确拒绝，未产生执行报告。见 [ISA验收](../artifacts/validation/engine-isa-03/verification.json)。

ISA限制采用.NET 10的 `DOTNET_EnableAVX512=0` 与 `DOTNET_EnableAVX2=0`，只设置测试子进程环境；名称来源为 [dotnet/runtime release/10.0配置定义](https://github.com/dotnet/runtime/blob/release/10.0/src/coreclr/inc/clrconfigvalues.h)。首次ISA探测使用了无效的旧开关名，断言检测到能力未关闭，未计为通过；正式报告采用正确开关且核对实际能力标记。这证明当前执行路径在两种运行设置下一致，不是恢复游戏服务器RNG。

## 复现命令

输出路径必须是新文件／新目录。先完成Release构建，再执行：

```powershell
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll --filter '*TrainingBenchmarks*' '*CopyBenchmarks*' --launchCount 2 --warmupCount 3 --iterationCount 8 --iterationTime 200 --exporters json --artifacts <新目录>
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll --filter '*Execution*Benchmarks*' '*Island*Benchmarks*' --launchCount 2 --warmupCount 3 --iterationCount 8 --iterationTime 200 --exporters json --artifacts <新目录>
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll profile camp <新报告.json>
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll profile ordinary <新报告.json>
dotnet benchmarks/UmaAiSharp.Benchmarks/bin/Release/net10.0/UmaAiSharp.Benchmarks.dll profile island <新报告.json>
```

复制候选基准要求本机同时支持AVX2和AVX-512；执行器最低要求仍是x64／AVX2。原始Release对照额外需要保留的本地基线DLL。基准fixture的SHA在启动时检查，内容变化必须重新确认输入与结果。

## 容量与未完成范围

预算为共享规则／预计算容量加32个工作区，固定上限8 GiB；原始包、调用者保留分叉和结果槽另计。Pioneer 使用无托管引用的标量头和独占连续整数区，预留容量由输入及当前已实现片段的后果计算，执行期不扩容。保守对象预算与进程内存观测分别报告，不能互相替代。

固定样本根搜索已接入；神经模型、自动购技、在线推荐、其他剧本和存档系统未实现。人员／Hint／事件的完整随机生成、临时效果生命周期、全部事件内容、继承、比赛结果与失败／重试、全阶段调度和终局生成仍是核心阻塞，不能放进“可选延期”来宣布完整 Pioneer 完成。详细机制账目保留在 [实施报告](implementation-status.md)。
