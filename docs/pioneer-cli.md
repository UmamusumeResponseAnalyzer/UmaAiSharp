# Pioneer 执行与推荐

`pioneer run/replay/batch` 执行已接入的 Pioneer 规则路径，`pioneer recommend` 比较完整执行到终局指标的根候选。当前可从末尾角色事件生成终局指标；普通训练、建设和其他事件的后续调度仍有停止点。运行要求 .NET 10、x64 与 AVX2。

## 条件经验推荐入口

从抓包库当前响应到终局的模型编译、命令及限制见[条件经验预测](conditional-forecast.md)。使用 `pioneer recommend --model`；下述 `--input` 命令用于显式规则起点和动作脚本。

## 原始回合导入

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer import --packet <响应.msgpack> --endpoint /umamusume/single_mode_pioneer/check_event --master <master.mdb> --output artifacts/import/turn
```

`--packet` 是原始 MessagePack 响应正文，`--endpoint` 必须填写该响应的实际路径。`load`、`start` 的嵌套 common 按端点解码。输出目录须为空，成功后包含 `input.json`、`candidates.json` 和保存包／master 哈希的 `import.json`。源 master 只读；执行输入只导出模型字段，不复制账号或完整包体。

支持 `scenario_id=11` 的普通／合宿就绪态、建设107／108、训练升级／休息确认和末尾角色结局。训练导入读取六卡、支援效果、五项训练、休息可用性、master角色身份、已揭示人员／Hint、资源、当前有序计划、设施、阈值及券数，按当前启用状态生成根候选。黄金船指定训练历史未接入导入；缺少所需历史或其他阶段输出 `import.json` 的结构化停止，退出码3。

事件选项通过抓包SQLite中的当前奖励响应导入，见[已揭示事件选项](#已揭示事件选项)。

导入的训练输入包含`development`，后续`run/replay/batch/recommend`须提供`--master <master.mdb>`编译建设成本和阈值。建设确认导入产生一个选项0候选；末尾角色结局输入不使用`--master`。

从抓包SQLite恢复成功普通训练次数：

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer import --packets <抓包.sqlite> --packet-id <当前响应编号> --master <master.mdb> --output artifacts/import/history-turn
```

仅使用锚点及之前本局记录，配对动作请求，核对五项协议次数，区分普通成功、合宿与失败；相同请求的重复捕获合并，冲突和缺失历史明确失败。`input.json`的`successfulOrdinaryTrainingCounts`按速度、耐力、力量、根性、智力排列；单包导入为`null`，表示未知。`import.json.trainingHistory`记录范围、动作计数及前缀哈希。该字段只恢复训练计数，不表示连续事件等全部历史完整。[规则与验证](training-history.md)

## 训练到建设

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer replay --input examples/pioneer/training-construction-replay.json --master <master.mdb> --seed 83 --output artifacts/training-construction.json
```

普通成功训练在回合5..60跨过一个本期建设阈值时，进入`ConstructionEvent`（kind=4、ordinal=1、key=`turn<<32|211067`）。第二个动作选择事件211067、角色0、选项0。建设确认结算达到阈值的计划前缀及券数，随后停止1007；两动作中已消费2次选择、完整结束1次动作。没有第二步脚本时明确返回脚本缺项，停在实际建设决策。[机制、输入和验证](training-construction.md)

## 休息与合宿休息

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer replay --input examples/pioneer-rest-ordinary.json --seed 71 --output artifacts/rest-ordinary.json
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer run --input examples/pioneer-rest-camp.json --seed 71 --output artifacts/rest-camp.json
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer batch --input examples/pioneer-rest-camp.json --seed 71 --samples 4096 --workers 16 --output artifacts/rest-camp-batch.json
```

`trainingTurn.availableRestCommands`为可空掩码：1表示普通701、2表示合宿3606、0表示已知不可用，null表示没有观测；不能混合普通与合宿模式。`characterId`由master映射，普通休息事件要求该身份。动作`kind=rest`，`commandId`为701或3606，其余身份字段为0。

普通休息的条件结果为`kind=restEvent`，data包含`actionOrdinal`和`event`四项身份；只提供选中的事件，不提供未来状态。选择休息先到`RestConfirmation`（决策kind=19），资源不变；下一脚本动作是该事件的`chooseEvent`、选项0，key为`turn<<32|eventId`。已经揭示的休息事件可由`pendingRestEvent`直接导入，要求阶段5、唯一当前事件和普通面板。

已识别普通事件确认恢复50或70体力；合宿行动恢复40体力、干劲+1。结算后返回`RestContinuationRequired`（1026），回合与后续协议阶段标记未知。缺可用状态为1024、缺普通事件结果为1025、缺master角色身份为1027；停止不生成终局建议。两个示例的正常退出码为3，表示已结算至明确的后续停止位置。[规则和原包窗口](rest-action-evidence.md)

## 训练升级确认

普通训练执行后的 `finalState.header.pendingTrainingLevelEventId` 区分未知null、已确认无升级0及待结算事件编号。达到阈值时只登记里程碑，等级在事件确认时增加；完整前态示例为 `examples/pioneer/training-level-trigger-replay.json`。[触发规则及原包顺序](training-level-trigger.md)。

升级确认沿用`start.kind=trainingTurn`，`pendingTrainingLevelEvent`提供当前已经揭示的事件。要求阶段5、角色0、故事400000005、时机6及唯一当前事件；五类事件211024／211027／211025／211026／211028依次对应速度／耐力／力量／根性／智力。目标等级须低于5；提供成功普通次数时，该项须为正4倍数。

导入生成一个`chooseEvent`候选，`choiceNumber=0`。脚本决策`kind=18`，`ordinal=0`，`key=turn<<32|eventId`；动作身份使用导入生成的候选。确认只增加对应等级1，消费当前事件，不消耗RNG；返回`TrainingLevelContinuationRequired`（1023），已消费1、完整结算0，回合与协议阶段未知。当前事件确认不生成后续队列。原包及正式入口核验见[审计记录](../artifacts/audit-user-20261109-01/mechanisms/REPORT.md)。

## 根候选推荐

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer recommend --input artifacts/import/turn/input.json --master <master.mdb> --candidates artifacts/import/turn/candidates.json --goal examples/power-goal.json --seed 71 --samples 2049 --workers 16 --output artifacts/recommendation.json
```

候选文件是 `{ "id": 整数, "action": 完整动作 }` 数组。候选编号必须唯一且非负，动作经过正式合法性校验；每个候选使用相同样本数和逻辑样本种子。推荐只接受 `autonomous` 输入。此入口中的 `script` 从根候选执行后的下一决策开始，根动作由搜索提供；`run/replay/batch` 的脚本则包含起点动作。

任何候选轨迹停止时，报告中的排序为空、建议为null，并保留全部候选样本。全部轨迹到达终局后按联合达标率、各级目标效用和候选编号排序。全部候选零达标时，先比较平均条件总缺口，再按原有目标优先级选择建议，条件均值保留null。`recommendationEvaluation`附模拟达标比例，零达标另附95%单侧统计上界；口径见[零达标建议与概率](recommendation.md#零达标建议与概率)。初始化遇到未知机制时，输出 `phase=initialization` 的停止报告。

末尾角色结局示例：

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer recommend --input examples/pioneer/character-ending.json --candidates examples/pioneer/character-ending-candidates.json --goal examples/power-goal.json --seed 71 --samples 2049 --workers 16 --output artifacts/ending-recommendation.json
```

该入口验证回合78、角色状态3、阶段5及事件6／对应角色结局111，结算五属性各+5、技能Pt+20，随后生成终局指标。此处只有一个确认动作。其属性上限固定，推荐入口按该范围预计算目标曲线；普通回合的完整未来推进尚未连接到此终局。

## 普通与合宿训练回合

合宿示例：

```powershell
dotnet run --project src/UmaAiSharp.Cli --no-build -- pioneer replay --input examples/pioneer/camp-training-turn.json --seed 71 --output <新报告.json>
```

示例为第38回合的根性3604成功行动。`start.data.camp`包含普通等级、五项基础值和设施；决策视图`isCamp=true`，动作保留3601..3605协议命令。预览使用合宿Lv5，执行次数增加到对应普通种类，普通等级不被改成5。结算停止在训练后事件窗口。


```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer run --input examples/pioneer/training-turn.json --seed 1234 --output artifacts/execution/training-turn.json
```

示例为原包 `packets-0003/1266` 的回合32前态，包含五项普通训练，脚本选择速度101。`start.kind=trainingTurn`，决策 `kind=14`、`key=turn<<32`；动作携带实际训练命令。`planningPeriods` 保存同一deck的五期master规划记录，用于核对当前回合与已完成评审数。示例由七窗口夹具及对应已编译支援面板导出。

成功时结算即时资源、发展Pt、羁绊及训练次数，停止在 `TrainingEventsRequired`；只提供成功／失败标志时，失败记录次数并停在 `TrainingFailureEventRequired`。退出码3，动作消费1次、完整结算0次。提供已识别失败事件时可继续至失败选择，见下方回放示例；下一回合事件及完整终局仍有待接入。

条件回放将 `mode` 设为 `conditional`，并提供 `{"kind":"trainingResult","data":{"actionOrdinal":0,"success":true}}`。该结果只控制成功／失败，不输入收益或后续状态。

## 已揭示事件选项

以`get_choice_reward`响应编号为锚点：

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer import --packets <抓包.sqlite> --packet-id <get_choice_reward响应编号> --master <master.mdb> --output artifacts/import/event
```

导入器向前关联同一viewer的配对奖励请求及唯一当前事件，只读取锚点及之前记录。奖励查询之间可以重复；相同请求身份的重复捕获必须内容一致，奖励窗口不能跨过未闭合动作或其他状态变化。选项声明按可见顺序映射，奖励组键不当作选项编号；多个候选结果和未解释原子原样保留。

输出`input.json`的起点为`declaredEvent`，`candidates.json`包含每个可见选项。`import.json.packetSha256`对应当前状态响应，`eventRewards`记录状态、请求、奖励三个包号和关联窗口哈希。此入口不要求捕获开局，也不推定连续事件历史完整。奖励声明含null原子等未解释格式时退出2，不生成执行输入。

执行脚本使用`import.json.decision`作为`decisionId`，以及所选候选的`action`。已有真实输入示例：

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer run --input examples/pioneer/event-choice.json --seed 1234 --output artifacts/execution/event-choice.json
```

示例来自 `packets-0003:55467/55469`，回合57、故事501068510。`start.kind=declaredEvent`、决策kind=15，包含当前资源、角色效果和所有选项的奖励声明。选择1的耐力声明为+10，执行结果为1199→1204。`PioneerEventCompiler`从当前角色、事件和`choice_reward_array`编译相同输入。

两个原始抓包库中，6,993个连续奖励窗口有6,991个可编译为15,433个选项；两份声明包含null奖励原子。正式CLI核验覆盖五个真实起点的导入与执行，包括上述1200跨越、多结果及带剩余效果的事件。[入口核验](../artifacts/implementation/event-import-01/REPORT.md)。

同一个选项可以有多个`branches`，顺序保留原奖励查询中的记录顺序。单一结果直接结算；多个结果的条件回放使用`{"kind":"eventRewardResult","data":{"actionOrdinal":0,"choiceNumber":1,"branchIndex":0}}`，索引从0开始。未提供结果时停在`EventRewardOutcomeRequired`。

资源结算后停在`EventRewardRemainderRequired`。状态的`EventRewardResourceMask`按位0..8依次表示速、耐、力、根、智、体力、体力上限、干劲、技能Pt；`EventRewardRemainingEffects`记录其他奖励原子的数量。原子内容保存在输入中，事件历史及后续调度尚待接入。公式与实包范围见[事件资源结算](event-reward-evidence.md)。

## 失败条件回放

仓库提供两条来自实包的失败片段：

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer replay --input examples/pioneer/ordinary-failure-replay.json --seed 1234 --output artifacts/execution/ordinary-replay.json
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer replay --input examples/pioneer/camp-failure-replay.json --seed 1234 --output artifacts/execution/camp-replay.json
```

| 示例 | 行动前 | 已识别条件结果后的分量 |
| --- | --- | --- |
| `ordinary-failure-replay.json` | 回合30，普通耐力训练105，耐力535，干劲5，执行次数1 | 耐力530，干劲4，回合31，执行次数2 |
| `camp-failure-replay.json` | 回合37，合宿耐力训练3602，耐力795，干劲5，执行次数8 | 耐力790，干劲4，回合38，执行次数9 |

两个示例的`start.kind`均为`trainingTurn`，由对应原始响应和master导入完整五项训练、六卡、资源与设施，`pendingStoryIds`为空。首个决策为kind14、key为`turn<<32`；失败后为kind2、ordinal1、key含实际event_id。`outcomes`单独提供`trainingFailure`事件身份和下一动作的`failurePenalty`分支；这些条件结果不包含后态。

耐力次数在训练尝试时增加一次，五项协议次数与耐力分量同步；进入失败选择时保留原资源和两条RNG。选择后扣耐力；正向思考25存在时抵消干劲下降并被消费，否则扣干劲。`trainingResult:false`只表示失败，不能替代具体事件身份。

预期退出码均为 `3`：先执行失败尝试到事件选择，再执行明确的选择分量，最终停在 `NextTurnEventsUnknown`。报告记录消费2个动作、完整结算1个动作；第二个动作的已知数值已经生效，后续自动事件未完成。回合已知，后续游戏阶段未知；不会把后包阶段或未来状态复制进执行状态。

## 不提供条件结果

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer run --input examples/pioneer/ordinary-autonomous.json --seed 1234 --output artifacts/execution/ordinary-autonomous.json
```

该示例使用普通失败分量前态，`mode` 为 `autonomous`，`outcomes` 显式为空。`failureTraining`起点在`data`中明确提供`supportBonds`和`characterEffects`；`state`保存失败计算的标量状态。当前失败事件生成规则未接入，执行在 `TrainingOutcomeRequired` 停止：动作尚未消费，资源和回合保持前态，退出码为 `3`。

## 批量执行

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer batch --input examples/pioneer/ordinary-failure-replay.json --seed 1234 --samples 131 --workers 4 --output artifacts/execution/ordinary-batch.json
```

`batch` 使用输入中明确指定的模式。`--samples` 是正整数，`--workers` 为1..32，`--seed` 为0..18446744073709551615的无符号整数。同一起点、脚本、条件结果与种子下，改变 worker 数量不改变对应 `sampleIndex` 的结果；增加样本数不改变已有前缀。

Ctrl+C 请求取消执行；报告保留全部请求样本，未执行或中途取消的槽位记录 `cancelled`、已消费动作与 RNG 末态。报告的 `execution` 字段记录执行范围、结果来源与计时范围。

条件示例不会抽样训练失败或惩罚分支，逐样本 RNG 状态仍记录各自派生的规则／策略随机流。批量重复条件片段不能作为失败概率、终局达标率或推荐质量估计。

## 建设规划

```powershell
dotnet run --project src/UmaAiSharp.Cli -c Release --no-build -- pioneer run --input examples/pioneer/planning-input.json --master <master.mdb> --seed 1234 --output artifacts/execution/planning.json
```

示例来源为回合3的规划前态和提交计划：先海之家Lv1，再速度Lv1、耐力Lv1，总成本400。`start.kind` 为 `planning`，`action.kind` 为 `submitConstruction`，`action.plan.steps` 保存完整有序升级；其余动作字段为0。决策 `kind=13`，`key=turn<<32`。

报告记录消费、结算各1个动作，回合3／阶段1，设施保持规划前值，计划进入轨迹独占缓冲；停止码为 `PlanningContinuationRequired`，退出码3。规划模型和候选枚举见 [回合推荐实现](recommendation.md#有序建设计划)。

## 输入与报告

输入包含 `schemaVersion`、`rulesIdentity`、`mode`、`start`、`outcomes` 和 `script`。`start` 与每个 outcome 使用 `{ "kind": "...", "data": { ... } }`；数据结构对应 `PioneerStart` 和命令支持的条件结果类型。建设规划、建设事件、评审及带`development`的训练起点必须提供 `--master <master.mdb>`，其余起点拒绝未使用的 `--master`。master 只读，来源 SHA 进入报告。

脚本的 `decisionId` 包含：

- `kind`：当前 `PioneerStage` 的数值。
- `ordinal`：本次执行从0开始的决策序号；不等于回合数。
- `key`：`PioneerScenario.DecisionKey(turn, eventOrCommandId)`，即 `((long)turn << 32) | (uint)id`。单指令片段用 command ID，五选一普通训练回合用0，事件用 event ID。

动作对象显式提供 `kind`、`commandId`、`eventId`、`characterId` 和 `choiceNumber`；不适用的字段也明确写0。属性对象和事件身份字段必须完整，不能用缺字段表达未知。重复字段、未知字段、数字枚举、缺失必需字段、无效模式、矛盾的条件结果顺序或未来状态字段均拒绝。数组中的null条目在JSON边界拒绝并报告路径；允许为空的对象属性仍可显式写null。`run` 只接受自主模式，`replay` 只接受条件模式，不自动转换。

输出文件必须不存在，已有报告和输入文件都不会被覆盖。报告包含输入／master 指纹、规则与 RNG 身份、逐样本进度／停止原因／随机状态，以及单次执行的局部状态；批量报告不保存每个样本的状态副本。`outcome=null` 表示没有完整终局，不能补成零值参与目标评价。

退出码：执行／推荐命令的 `0` 表示所有请求样本均生成终局指标，导入命令的 `0` 表示输入和候选成功生成；`2` 表示输入、参数或文件错误；`3` 表示有明确停止。角色结局推荐示例返回0，其余执行片段示例返回3。成功终局要求动作脚本与条件结果全部消费。报告中的时间和进程总分配属于 CLI 运行观测。

## 来源与验收

示例从 `tests/UmaAiSharp.Tests/Fixtures/training-failure-snapshots.json` 的两条已解码窗口提取。原包库 SHA 为 `ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b`：普通窗口58275..58281，合宿窗口52989..52995。示例只保留游戏字段及匿名来源定位，不包含包体或账号身份。夹具中的后态仅用于测试断言。

`PioneerExecutionCommandTests` 核对示例前态与来源、条件回放结果、自主未知停止、1／4／16／32 worker逐样本一致性、样本前缀与 RNG 状态、严格解析、参数错误和拒绝覆盖。具体集中执行结果见验收报告；示例和断言本身不证明完整育成实现。
