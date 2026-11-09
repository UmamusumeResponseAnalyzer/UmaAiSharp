# Pioneer 片段执行内核

`PioneerScenario` 把已支持的 Pioneer 决策和结算分量接入统一查询、动作验证、脚本、复制和批量执行。起点由 `PioneerStart` 声明，包含当前观测与执行所需字段。末尾角色结局可返回 `TerminalMetrics`；其他路径的结算范围和停止位置见下表。

## 运行入口

```csharp
var data = PioneerRuntimeData.Compile(start, conditionalOutcomes, rulesIdentity,
    constructionMaster, checkpointMaster);
var root = data.CreateInitialState();
var scenario = new PioneerScenario(data);
```

`start` 是明确的当前边界：普通／合宿训练与休息面板、已揭示休息确认、耐力失败前训练、岛训练、建设事件、Hint 属性事件、Tucker 分量、PJ 追加、评审分量或已发奖显示确认。建设和评审入口分别需要已加载的对应 master。Runtime 不引用 CLI，不读取 SQLite，也不接收未来资源、未来人员或未来事件队列。

`PioneerRuntimeAction` 描述训练命令、岛训练、当前事件选择或完整有序建设计划。建设规划起点为 `PioneerStart.Planning`，决策视图提供当前规划状态，提交动作携带不可变 `PioneerConstructionPlan`。条件结果独立于策略动作，按从导入起点开始的 `ActionOrdinal` 编号，支持：

- 普通／合宿训练的成功标志（`TrainingResult`）。
- 普通休息选中的事件身份（`RestEvent`），恢复量由对应规则计算。
- 已揭示事件所选项的候选结果索引（`EventRewardResult`）。
- 已识别的训练失败及其事件身份；`TrainingTurn`可直接进入`FailureChoice`，失败事件与下一动作的惩罚分支分别消费。
- 已识别的失败选择结果分支。
- Hint 当前明确奖励组中的一个索引。
- PJ 已明确的 NPC 身份名单。

条件结果仅用于回放，决策视图不暴露它们。普通／合宿训练回合在未提供条件结果时按显示失败率抽样；其余条件组件缺少结果时保持未消费状态。错误事件身份由动作验证拒绝。数值候选身份随 `RulesIdentity` 保留。

决策身份为 `Kind = PioneerStage`、轨迹局部 `Ordinal` 和 `Key = ((long)Turn << 32) | (uint)EventOrCommandId`。岛训练使用命令3101，事件使用event_id。不同回合的相同命令不能消费同一脚本项；同回合多个选择由Ordinal区分。动作联合字段必须与类型一致，例如训练动作不能暗带事件或choice。当前序号上条件类型错误与缺少结果分别报告；终局若有遗留条件结果，以 `ScriptMismatch/OutcomeIdentityMismatch` 停止并记录消费数与总数。

## 状态与复制

普通／合宿面板的支援、Hint掩码和PJ人数保存在主状态的`TrainingBoard`中，可用指令掩码也由轨迹独占。查询通过`PioneerDecisionView.TrainingBoard`返回值副本；预览、发展Pt、羁绊和Hint候选读取此状态。位置生成与验证见[人员联合分配](joint-placement.md)。

`PioneerState` 独占一个标量主状态和一个连续整数缓冲区。缓冲按有界组件分段保存支援羁绊、岛训练分配、设施、训练等级、五项协议执行次数、角色效果、Hint 候选、当前故事、阈值、有序建设计划、临时效果原子和 PJ 记录。数据编译时按当前片段的输入长度及已实现后果预留容量；这是该实现的工作区需求，不是推定游戏中队列或技能的全局上限。

执行期间不扩容。`CopyInto` 只允许同一编成数据，复制主状态、各段有效长度和有效内容；只读规则与条件输入共享。工作区不足属于明确的编译／容量错误，不静默租借更大的数组。`MutablePayloadBytes` 报有效载荷，`WorkspaceBytes` 报已预留数组容量加对象开销预留；`SharedMemoryBytes` 是包含冷参考输入、规则表和编译数据的保守预算，不是 CLR 精确对象图测量。

`KnownParts` 区分已提供的资源分量。Tucker 资源片段只知道体力、上限、技能 Pt 等必要字段时，不把缺失五属性当成真实零。`TurnKnown`、`RawPlayingStateKnown` 区分当前步骤完成后能否确认回合／阶段；`SourceTurn`、`SourcePlayingState` 保留原动作来源。字段未知时，字段中的占位或此前数值不得作为已生成后态使用。

发展与评审字段分别标记：`DevelopmentPoints`、`Tickets`、`DeckId`、`CompletedCheckpoint`、`CheckpointResult` 和 `CheckpointBonusPercent` 只确认对应字段。`Planning` 同时确认规划预算、阈值数组和有序建设计划；`CheckpointProgress` 同时确认理事长羁绊与 `HasAttention` 状态，五项训练等级由 `TrainingLevels` 单独标记。各起点提供的范围如下：

| 起点 | 已知的发展与评审字段 |
|---|---|
| Tucker 发展 Pt | 发展 Pt、deck 身份 |
| 建设 | 发展 Pt、券、deck 身份、规划数据 |
| 岛训练 | 发展 Pt、券、已完成评审数；加成百分比仅在明确提供时已知 |
| 评审分量 | 发展 Pt、deck 身份、当前评审结果、理事长羁绊与 `HasAttention` |
| PJ／已发奖显示确认 | 当前评审结果 |

当前评审类型不等同于已完成评审数。显式提供的零值可以标记为已知；未提供的券数或加成即使存储槽为零，也没有对应已知位。`StopCode.None`、`ContinuationFrom=None` 表示尚未提交阶段性停止，不表示未知机制已经补齐。

`PioneerState.Clone` 只复制领域状态。精确执行恢复还包括策略游标、待动作、执行位置和两条 RNG；通过 Core 的 `ForkInto` 复制整个 `TrajectoryExecution`。重新独立采样使用 Core 的 `ResetSample`，不能混同两种操作。

`PendingTrainingLevelEventId` 单独保存训练升级里程碑：null为未知、0为已确认没有、正数为对应待结算事件。成功普通训练达到四次倍数时按当前等级登记，当前可见事件和训练等级保持原值；已揭示升级确认后清零。它随主状态复制，和 `Event`、`PendingStories` 的可见事件事实分别维护。[触发与排队证据](training-level-trigger.md)。

## 已接入路径与停止位置

| 起点 | 已执行的内容 | 停止位置 |
|---|---|---|
| 末尾角色结局 | 五属性各+5、技能Pt+20，应用1200换算与上限，记录路径Pt与溢出 | `Terminal`，输出终局指标 |
| 普通／合宿训练回合 | 五项指令收益／失败率预览，抽样或条件成功结果，资源／Pt／基础羁绊／执行次数／溢出 | 普通成功跨建设阈值进入建设107；其余为`TrainingEventsRequired`或`TrainingFailureEventRequired` |
| 单项训练升级确认 | 对应训练等级+1，消费已经揭示的事件 | `TrainingLevelContinuationRequired`；同回合后续与下一回合未调度 |
| 普通休息与确认 | 选择后揭示指定事件；确认后体力+50／70并封顶 | `RestContinuationRequired`；缺抽样结果时不消费行动 |
| 合宿休息＆外出 | 体力+40、干劲+1并封顶，保持次数和羁绊 | `RestContinuationRequired`；后续调度未接入 |
| 已揭示事件选项 | 保留奖励候选组，结算选中结果的属性／体力／上限／干劲／技能Pt及正向思考消耗 | `EventRewardRemainderRequired`；多个结果未指定时为 `EventRewardOutcomeRequired` |
| 两种耐力训练失败 | 训练次数 +1，进入真实失败选择；明确选择分支后耐力 −5、干劲 −1、回合 +1 | `NextTurnEventsUnknown`，不假定下一训练已就绪 |
| 岛训练 | 精确数值候选生成两层收益，结算资源／发展 Pt／羁绊／扣券／行动前人员 +60 原子 | `IslandContinuationUnknown` |
| 建设 107／108 | 达到阈值范围内的设施及独立发券分量 | `ConstructionRemainderUnknown` |
| Hint 明确属性组 | 单组属性及技能 Pt，保持现有 1200 与上限语义 | `HintRemainderUnknown` |
| Tucker 七类资源、发展 Pt 或 +20 原子 | 各入口已有的明确分量 | `TuckerRemainderUnknown` |
| PJ 明确身份 | 追加成员和新 NPC 记录 | `ProjectRemainderUnknown` |
| 高评价评审事件 | 发展 Pt／体力、训练等级、理事长羁绊 | `CheckpointRemainderUnknown`，不跳过其他奖励 |
| 已完整发奖的显示确认 | 只更新已确认标志 | `EventContinuationUnknown` |

局部分量提交后，Core 收到 `ActionConsumed=true, ActionSettled=false`。再次继续仅返回同一停止位置，不再选择动作、消耗条件结果、扣券或发奖。失败训练到失败选择是完整的首个决策转移，返回 `Settled`；失败选择后的下一回合事件尚未生成，因此第二个动作保持未完全结算。

`CharacterEnding=16` 通过选择后进入 `Terminal=17`。该起点之后的属性上限固定；`FutureOverflow` 记录1200换算后、被上限截断的显示属性单位，`FutureGainedSkillPoints` 从当前起点起累计，终局另返回五属性评分。资源终局不推定后续服务器阶段值，`RawPlayingStateKnown=false`；因子选择与账号保存属于独立流程。

临时效果原子仍保留来源回合和对象；没有加入猜测的生效、到期或叠加规则。条件 Hint 奖励不认证奖池抽样分布。建设不认证完整计划确认条件。其它能力和覆盖边界沿用各组件证据文档。

## 验证边界

`PioneerRuntimeTests` 将两条原始失败夹具接入正式脚本和执行器，在失败选择处复制，再核对后续状态和结构化停止；同时检查不同 worker 固定槽位一致性、未消费动作重试、变量缓冲的复制隔离及已提交分量不重复应用。PJ、Tucker、显示确认复用实际组件数值夹具；其中缺少完整事件身份的测试使用明确标注的本地身份标记，测试范围为已选择的结算分量。

`PioneerTerminalExecutionTests` 核对215个角色结局窗口、未来Pt与溢出、脚本／条件消费、分叉和CLI。确定性测试在成功终局与停止路径均抓取完整Header和有效数据区；比较2,049样本在1／4／16／32 worker下的结果及65样本前缀。

`PioneerRuntimeComponentTests` 将现有七组岛训练真实快照通过公共 master 编译入口导入并执行，与实际立即响应的资源、发展Pt、券和羁绊逐项比较。建设107/108夹具来自 `local-audit-05` 的8118→8120、1364→1366两窗，重新核对同局、唯一Paired请求响应、端点和事件选择后仅导出所需前后字段；`pioneer-runtime-construction.json` 保存来源和派生输入SHA-256。评审使用268→270的既有资源与进度投影。全部仍是已参与推导的语料，不是新的独立机制验收。

训练数值使用 `TrainingNumericKernel` 和共享 `CompiledTrainingDeck`；`RawGains` 分数字符串由审计入口生成。动态固有按羁绊阈值、训练等级和粉丝整除分段预编译；每个单元保留原子顺序和条件激活时的错误。执行只读查表，未知分支不会提前影响未触发的条件。岛训练编译入口交叉校验Deck与参与者身份、等级及羁绊，并预编译基础增量、海之家系数及各友情设施数的倍率。数值核保留BigInteger及逐原子取整。组件的未知规则异常转换为结构化停止。各片段耗时、分配和编译成本见性能报告。

合宿配置保存五项普通等级、按五属性顺序排列的基础值及六个设施。训练时使用有效Lv5解析支援固有，普通等级保留在状态中。静态设施基础增量、海之家系数和剧本倍率在规则编译时计算，行动路径直接消费数值。规则身份包含`pioneer-runtime-components-v12`和`pioneer-training-turn-v12`；等级确认规则为`pioneer-training-level-trigger-v2`。训练数值版本为`jp-training-refit14-d6x3-r5-candidate-v4`，包含在完整规则身份中；证据与统计口径见[训练行动](training-turn-evidence.md)。

`SuccessfulOrdinaryTrainingCounts`在已知时保存五项成功普通训练次数，与包含合宿和失败的`TrainingExecutionCounts`分别维护；缺失时不写已知位。两者归轨迹缓冲独占，计数更新不提前结算训练等级事件。[历史导入](training-history.md)

普通／合宿训练的全编成羁绊固有按参与掩码查表，64项共256B计入共享容量。未知顶层固有在编成阶段停止，即使持卡者不出现在面板上也要检查。固有121的来源与结算见[训练羁绊效果](training-bond-effects.md)。

`TrainingNumericResult.UncappedCommon/UncappedScenario`保存1200换算后、属性上限截断前的显示增量。剧本层从普通层实际获得后的属性开始换算；两层差额形成`Overflow`，普通／合宿／岛训练及已解析事件资源将它累加到`FutureOverflow`。

Hint属性奖励返回`HintAttributeReward`，同时保留后态、截断前显示增量和溢出；正式Hint入口累计`FutureOverflow`并标记已知，其他后果在`HintRemainderUnknown`停止。
