# 确定性单轨执行内核

Core 执行显式场景与策略，支持结构体状态、独占工作区、精确分叉、固定任务批处理和根候选搜索。游戏规则与随机事件概率由场景提供。运行平台要求 x64 与 AVX2；AVX512 是可选能力标记，Core 没有另写复制算法，连续缓冲区交由 `Span.CopyTo` 等标准操作处理。

## 场景与所有权

`ISimulationScenario<TState,TDecision,TAction,TOutcome>` 的查询使用 `in TState`，修改使用 `ref TState`。引用类型状态也可以实现同一契约。

- `CreateWorkspace(in root)` 创建独占存储；`CopyState(in source, ref destination)` 复制全部影响未来的字段和有效段长度，不修改源状态。
- `Inspect(in state, ordinal)` 返回决策、已完整结算的终局或结构化停止。决策身份为 `DecisionId(Kind, Ordinal, Key)`，其中 `Ordinal` 是本轨迹的局部消费序号；`Key` 由剧本编码必要的回合、事件等语义，不能使用不稳定的对象哈希。
- `ValidateAction` 验证实际动作；`ApplyAction` 执行新动作；`ResumeAutomatic` 只续算已经消费、尚未结算完毕的动作。
- `GetOutcome` 只读取终局，并返回自己拥有的数据。

`in` 不会自动冻结字段中的数组。决策视图是借用数据，只在下一次状态修改前有效；策略若保存历史，必须复制所需内容。动作必须是值或具有独立寿命的不可变数据，不能借用即将复用的工作区。终局结果、诊断、策略历史和分叉目标均不得引用另一个工作区的可变数组。

未使用尾部可以不清零，但任何规则、哈希、结果投影只能读取有效段。可变数组的容量在导入/编译阶段计算并预留；正常执行、复位与分叉不隐式扩容。目标容量不足是明确错误，不能截短状态。

## 新样本、单步与精确分叉

`CreateExecution` 创建调用方拥有的 `TrajectoryExecution`。它保存领域状态、策略游标、规则/策略 RNG、待消费动作、执行阶段、动作消费/结算计数、位置、根种子和样本编号。

```csharp
var execution = SimulationRunner.CreateExecution(scenario, policy, root, seed, sampleIndex);
var step = SimulationRunner.Advance(scenario, policy, ref execution);
var result = SimulationRunner.Continue(scenario, policy, ref execution);
```

`Advance` 最多执行一次动作尝试或一次自动续算；结果为 `Advanced`、`Completed` 或 `Stopped`。`Continue` 循环至完成或停止。未知机制、非法动作、脚本不匹配及取消使用 `SimulationStop`，保存原因、模块数字代码、位置和两个数值参数；消息格式化留在展示边界。程序错误继续抛异常，不改写成未知游戏机制。

程序错误可能发生在状态修改期间；调用者须废弃该执行上下文或通过 `ResetSample` 重新开始。只有结构化停止结果支持继续执行。

`TransitionResult` 明确区分动作是否已经消费、是否已经结算：

| 消费 | 结算 | 后续执行 |
|---|---|---|
| 否 | 否 | 保留已经选择的动作，续跑时重新验证并尝试 `ApplyAction` |
| 是 | 否 | 保留已提交状态，续跑调用 `ResumeAutomatic`，不重新选择或消费动作 |
| 是 | 是 | 返回下一次决策检查；消费与结算分别计数 |

“未消费但已结算”是程序不变量错误。停止结果不带伪造终局值。一个已结算动作可以紧接未知的下一阶段；其已完成计数和领域状态仍保留。

`ForkInto(scenario, policy, source, ref destination)` 要求目标已有独占工作区，复制完整执行上下文、两条 RNG 的当前状态、待动作与策略历史。它不生成新随机流。`ResetSample(..., seed, sampleIndex)` 则复制根领域状态、清空轨迹进度、重置策略游标，并从根种子/样本编号建立两条独立新流。两者不能互换。

`ActionScript<TDecision,TAction>` 在构造时一次检查不可变步骤，所有轨迹共享步骤，每条轨迹使用独立 `ScriptCursor`。身份不符、脚本耗尽及终局剩余步骤均返回结构化停止，不补动作。引用类型动作必须显式提供独占不可变负载的容量声明。

## RNG 与批量执行

`SimulationRandom` 是只含 `ulong` 状态的值类型，调用链始终按 `ref` 传递。算法保持 `splitmix64-v1`，样本派生保持 `sample-domain-mix-v1`；`NextInt` 的拒绝采样和上界为1时的抽数行为保持原样。策略随机消耗不能影响规则流。

`RunBatch` 同步执行固定数量样本。固定批次按每块 64 个逻辑样本分配工作区。分批根搜索可复用为后续大批准备的工作区，并把较小的新增区间拆给多个 worker。编号、种子及结果位置由逻辑样本决定。每个 worker 复用一个独占状态和策略游标，结果写入原始 `sampleIndex` 槽。每个结果保存两条 RNG 的末态。

```csharp
var batch = SimulationRunner.RunBatch(scenario, policy, root, seed, samples, workerCount);
```

批量结果只保留结果/停止位置与 RNG 末态，不保存全部领域快照；worker在下一样本会复用工作区。需要精确分叉、检查停止状态或续跑时，使用调用方持有的单轨上下文。

1..32 worker在相同固定输入及完整工作量下必须逐样本一致，扩大批次不改变已有样本前缀。实时取消发生在不同任务进度上，因此不同worker设置的取消集合可能不同。所有请求槽保留完成或取消结果；未执行槽报告本样本派生的初始 RNG 状态。完整预算评价要求所有样本完成。

## 容量预算

`ExecutionMemoryPlan` 固定按32个工作区记账，要求：

```text
共享规则、预计算及策略数据容量 + 32 ×（领域工作区 + 策略游标 + 执行上下文容量）≤ 8 GiB
```

即使本次worker少于32也按此容量验收。场景与策略必须完整申报其数组capacity、对象及独占负载；`GetWorkspaceBytes(in root)` 表示需要预留的完整工作区容量，不能只计算有效段。`SimulationRunner.GetMemoryPlan(scenario, policy, state, cursor)` 从现有状态和游标返回同一份账本，展示层不复制容量公式或另建游标。Runner在分配领域工作区前检查预算，并验证创建后的工作区和批次结束时的容量没有超过声明。输入容量不足、负数、溢出或超预算明确失败，不缩小轨迹内容。

该账本针对引擎额外共享数据和工作区，不是整个进程RSS限制；输出槽及调用方持有的根输入/导出结果另计。它依赖场景正确申报和禁止执行中扩容，不能认证任意错误实现的临时分配峰值。基准需同时记录实际分配和内存峰值。

## 目标预编译

`GoalDefinition` 一次编译约束与指标序列。`GoalEvaluator.Compile(goal, maximumAttributes)` 另外为明确的整数属性范围生成曲线查表；超过声明范围明确失败。未选择查表编译的定义直接执行原曲线公式，不猜游戏上限。

表中每个整数值独立调用原 `MarginalCurve.Evaluate` 生成，保留decimal运算顺序；不通过逐点权重累加替换原分段公式。指标权重、同级求和、优先级顺序、样本归约顺序和成功后的除法保持不变。

`CompiledGoal.Evaluate(in metrics, Span<decimal>)` 写调用方缓冲区，返回是否同时达标。返回false时缓冲区不构成有效效用。`GoalEvaluator.Summarize` 直接累积，不创建每轨迹的 `GoalSample` 或效用数组；只有需要单个结果对象的便利入口才分配它们。零达标的条件均值仍为null。

批次重载 `Summarize(CompiledGoal, SimulationBatch<TerminalMetrics>)` 要求全部固定槽完成且样本编号与槽位一致，按编号归约，拒绝停止或重排后的输入。终局 JSON 的必填字段、未知值null与严格格式校验保持不变。

## 验证范围

Core测试覆盖值状态修改、独占缓冲区、RNG黄金向量、策略随机隔离、精确分叉、部分动作续算、长短轨迹工作区复用、取消后待动作、固定结果槽、1/4/32 worker与批次前缀、脚本独立游标、内存边界、程序错误传播，以及逐整数decimal查表一致性和停止批次拒绝。

性能验收分别记录克隆、单步、固定任务批次和目标评价；真实完整Pioneer路径未实现时不报告“完整育成/秒”。复制指令与可选ISA优化由基准结果决定，不为使用SIMD而改变数值或随机语义。

`ExecutionBenchmarks.cs`使用已锁定SHA-256的普通/合宿完整训练前态和失败原包投影，分别测量配置编译、分配克隆/预留工作区复制、执行上下文精确分叉、复位后执行两次决策、64与4096个固定任务。JSON解析和实包前后态对照在计时外完成；首个决策为`TrainingTurn`，失败事件与结果分支是显式条件输入。第二次决策之后停在下一回合事件待处理阶段，计量单位为失败片段。

两档批次分别比较1/2/4/8/16/32 worker。64个样本只有一个逻辑块，因此这档主要显示小任务调度和工作区开销，不代表32路并行负载。并行批次不使用`MemoryDiagnoser`推断全进程分配；独立进程辅助入口`ExecutionBatchAllocation.Measure(caseName, samples, workers)`在预热后记录`GC.GetTotalAllocatedBytes(true)`差值及耗时，覆盖工作线程分配，但它不是峰值存活内存或RSS。

该入口另外记录本次三代GC次数差值与`PeakWorkingSet64`/`PeakPagedMemorySize64`。两项进程峰值包含启动、夹具解析和预热，不能解释为本次批次独占峰值；结果校验与checksum在计时/分配计数结束后执行。`MeasureColdCompilation(caseName)`应在独立新进程中调用一次，夹具解析在计时外，仅计首次规则编译、根状态和脚本构造，另报共享容量、单工作区容量及32工作区预算；它不先执行片段或预热编译。
