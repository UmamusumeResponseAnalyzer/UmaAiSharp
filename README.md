# UmaAiSharp

C# / .NET 10 育成模拟与行动评估工程，包含确定性多线程执行、固定样本根搜索、Pioneer 机制、目标评价和实包审计。

## 构建

```powershell
dotnet restore UmaAiSharp.slnx --locked-mode -m:1 -nodeReuse:false
dotnet build src/UmaAiSharp.MCP/UmaAiSharp.MCP.csproj -c Release --no-restore -m:1 -nodeReuse:false
```

用本机 MCP 客户端启动 `dotnet <绝对路径>/UmaAiSharp.MCP.dll`。连接方式、结构化结果和工具清单见 [MCP 接入](docs/mcp.md)。

SDK 与依赖版本分别记录在 `global.json`、`Directory.Packages.props` 和各项目的 `packages.lock.json`。运行不依赖 UmamusumeResponseAnalyzer Host 程序集。

执行引擎要求 x64／AVX2。BenchmarkDotNet 0.15.8 只用于独立基准项目，不属于产品运行依赖。

## 本地验证材料

完整解决方案构建、基准项目及全量测试依赖未公开的本地抓包夹具。运行前需在 `tests/UmaAiSharp.Tests/Fixtures/` 提供 `pioneer-camp-turns.json`、`training-condition-panels.json`、`training-construction.json`、`training-level-events.json` 和 `training-level-triggers.json`，然后执行：

```powershell
dotnet test UmaAiSharp.slnx --no-restore -m:1 -nodeReuse:false
```

`artifacts/` 中的模型、研究材料、历史性能基线和报告不随仓库发布；文档指向这些材料及 `docs/event-reward-evidence.md` 的链接仅在材料齐全的本地工作区可用。

## 可执行功能

`pioneer_forecast` 从抓包前缀比较训练、休息、岛训练、外出、比赛、保健室、建设规划或已揭示事件选项，通过明确标注的经验过渡模型预测到终局。当前角色成长与六卡效果由 master 编译，不要求模型包含相同配置；缺失机制保留停止。输入、实测覆盖及适用限制见[条件经验预测](docs/conditional-forecast.md)。完整机制模拟及独立推荐质量验收尚未完成。

规则资源 v12 可通过 `pioneer_import_capture` → `pioneer_recommend` 比较合法训练、岛训练、建设、恢复及比赛，并模拟两次继承到第 78 回合。属性条件在终局判定。候选先比较联合达标率，再比较达标轨迹的平均五维评分。精确和快速公式模型可在执行时选定。见 [Pioneer MCP](docs/pioneer-mcp.md#规则资源-v12-自主整局)。

`pioneer_calibrate_events`提取去除事件所属支援加成的基础资源样本，提供同类事件统计参数及排除计数。`pioneer_build_model`将已校准资源绑定到未来转移，按当前卡组与羁绊重新结算。用法和适用边界见[事件基础资源模型](docs/event-statistical-model.md)。

当前训练面板由轨迹独占，支援与PJ的联合位置生成见[人员联合分配](docs/joint-placement.md)。

状态3／5／11／27的训练失败率分量，以及片头痛对通用事件干劲的影响，见[角色状态](docs/condition-effects.md)。

抓包SQLite可按当前响应编号恢复[成功普通训练次数](docs/training-history.md)，区分合宿和失败；单包输入保留明确的未知历史。

抓包中的当前奖励响应可导入[事件选项](docs/pioneer-mcp.md#已揭示事件选项)，关联当前状态并生成全部可见选项候选；多结果分支与其他效果分别保留。

成功普通训练按四次阈值登记[待结算升级](docs/training-level-trigger.md)，已揭示的五类升级事件经正式入口确认后增加等级；输入和停止位置见[训练升级确认](docs/pioneer-mcp.md#训练升级确认)。

普通训练跨越本期发展Pt阈值后进入[建设107决策](docs/training-construction.md)，确认后结算当前有序计划的设施前缀及岛训练券。回合导入包含当前建设状态，执行时提供`masterPath`；建设后的人员配置和事件调度保留停止位置。

普通休息支持选择与事件确认两个决策；合宿休息可自主结算体力／干劲。导入按当前面板增加合法休息候选，后续事件调度保留明确停止位置，见[休息执行](docs/pioneer-mcp.md#休息与合宿休息)及[原包证据](docs/rest-action-evidence.md)。

普通／合宿训练接入全队固有121的[羁绊结算](docs/training-bond-effects.md)。两条失败回放从完整训练面板进入失败选择，结算惩罚及正向思考效果消耗；输入和停止位置见[失败回放](docs/pioneer-mcp.md#失败条件回放)。

根候选搜索、人员与 Hint 抽样模型见 [回合推荐实现](docs/recommendation.md)。导入工具将原始回合响应编译为执行输入和合法根候选。分量执行与条件经验预测使用各自的规则身份。

Pioneer 入口、普通／合宿训练回合、已揭示事件选项、真实失败片段示例与条件结果格式见 [执行与推荐](docs/pioneer-mcp.md)。`pioneer_run` 为自主模式，`pioneer_replay` 接受限定的已识别结果，`pioneer_batch` 按输入模式执行固定样本批次。未实现机制输出结构化停止，搜索结果保留各候选的逐样本停止位置。

证据工具只读打开源数据库，派生文件使用新输出路径。审计摘要分别返回工具执行状态和证据核验结论。完整工具列表见 [MCP 接入](docs/mcp.md#证据工具)。

离线 `goals_evaluate` 接受终局指标数组；`pioneer_recommend` 评价执行器生成的终局指标。输入格式与优先级语义见 [架构与契约](docs/architecture.md)。两个目标示例分别演示属性条件与多级目标、耐力边际曲线；数值和优先级由调用者选择。

## 工程边界

| 项目 | 当前职责 |
| --- | --- |
| Core | 值类型随机流、借用决策视图、独占工作区、精确分叉、确定性批量调度、预编译终局目标评价、固定样本根候选搜索 |
| Pioneer | 紧凑状态与片段状态机、普通／合宿训练行动、精确训练数值核、有序建设规划、建设与评审分量、已解析失败分支、岛训练即时结算、Hint有效奖池、Tucker事件分量、发展 Pt、开局组合与相性、已选比赛目标映射、结局与评分候选 |
| Pioneer.Game | 规则快照、回合输入编译和可复用的预测会话，供 MCP 和 URA 的 PioneerAiPlugin 进程内调用 |
| MCP | SQLite / MessagePack 读取、回合导入、规则输入编译、单轨迹／条件回放／批量执行、根候选推荐、差分核验及目标评价 |
| Tests | 输入边界、规则局部样例、轨迹隔离和确定性不变量 |

PioneerAiPlugin 在 URA 的训练面板显示行动建议，在「建设规划」工作区显示有序方案排名。两处都使用自主整局模拟和 `goalPath`，可用 `model` 选取公式。维护者使用 `tools/UmaAiSharp.Pioneer.DataGenerator`，依次传入 master、规则资源输出路径、回合校准文件和继承校准文件，生成插件内置规则。

实施结果、核心缺口和延期项分别列在 [实施报告](docs/implementation-status.md)。完整实包与派生材料保留在被忽略的 `artifacts/` 中，按本地研究数据处理。

执行器设计见 [Core](docs/core-engine.md) 与 [Pioneer](docs/pioneer-runtime.md)；基准口径、实测数据及未采用优化见 [性能交付报告](docs/engine-performance.md)。

交接包参考实现的吸收范围、反例及排除项见 [移交包核查](docs/handoff-review.md)。

日文资料及本地对照分别记录在 [训练公式](docs/training-formula-evidence.md)、[人员与 Hint](docs/placement-hint-evidence.md)、[开局继承](docs/initial-inheritance-evidence.md)、[比赛路线与评分](docs/race-route-evidence.md)。人员、Hint、随机事件和比赛结果的完整生成规则尚未认证。

九个研究会话的接入状态见 [研究接入记录](docs/research-integration.md)。终局对象选择、数值候选与路径指标边界见 [终局证据](docs/terminal-evidence.md)。研究标签依赖的命令要求对应输入指纹匹配；`evidence_initial_state` 重新解码原始包。局部核验报告不能据此宣布完整模拟通过。

新增组件的输入、证据和未实现边界见 [Hint奖池](docs/hint-pool-evidence.md)、[岛训练行动](docs/island-action-evidence.md)、[训练失败](docs/training-failure-evidence.md)、[Tucker与评审事件](docs/pioneer-event-components-evidence.md)。
