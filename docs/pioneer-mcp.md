# Pioneer MCP 执行与推荐

`pioneer_run/replay/batch` 执行已接入的 Pioneer 规则路径，`pioneer_recommend` 比较自主推进到终局指标的根候选。运行要求 .NET 10、x64 与 AVX2。

这四个工具均接受 `model`。默认 `precise`，在普通、合宿和岛训练中使用有理数乘积及逐级量化。`fast` 使用浮点乘积和末端整数截断，省去逐级量化。两种模型使用相同的动作、失败率、分层上限和终局评分。报告的 `provenance.rulesIdentity` 含所选模型及公式版本。实包数值回放使用 `precise`。

## 条件经验推荐入口

从抓包库当前响应到终局的模型编译、命令及限制见[条件经验预测](conditional-forecast.md)。`pioneer_forecast` 使用经验模型。连接方式见 [MCP 接入](mcp.md)。

## 规则资源 v12 自主整局

导入时在 `pioneer_import_capture` 或 `pioneer_import_packet` 传入 `rulesPath`。后续执行工具使用同一文件的 `rulesPath` 以核对内容身份。`pioneer_run` 和自主 `pioneer_batch` 额外需要 `goalPath`。`pioneer_recommend` 继续接收导入产出的 `input.json` 和 `candidates.json`。自主执行根据生成状态选动作，`script` 为空。

事件决策的 `key` 使用 `turn << 32 | storyId`。`chooseEvent` 同时携带 `storyId`、实包 `eventId`、`characterId` 和选项。`ordinal` 区分同回合内的多次决策。抓包前缀的 `observedStoryHistory` 保留故事、回合、阶段、角色、支援身份和选项，连续事件的位置取同一故事的 `show_progress_1`。`import.json` 记录无法唯一配对的确认次数。版本 1 组件脚本的事件 `key` 使用协议编号。

自主休息按 Pioneer 抓包抽取 50 或 70 点体力，合宿休息恢复 40 并提升一档干劲。训练先按面板失败率判失败，失败后以相同概率判重败，采用 UmaAi-rust 的处罚分支。成功训练从提示支援中抽取一人，应用羁绊后在未达 Lv5、未购同组白／金技能的有效奖池中抽取一个技能或一组属性。固有 type 33 在技能分支抽取额外不同技能。技巧设施的 Hint 发生率、必出和全触发按对应合宿／岛训位置应用。友人首次成功同训可触发固定收益，随机解锁更新外出资格，外出动作结算对应阶段效果。

当前面板按指令启用状态、友人外出进度、比赛列表、出道状态和粉丝门槛生成训练、岛训练、休息、治病、出行和比赛候选。未来的岛训练分配取已建设施对应的普通训练人员，海之家在剩余人员中按友人、普通支援、先锋队员优先级补足。普通和岛训练中与 Tucker 同训的普通支援获得下一回合临时得意率 +60。岛训练扣券并结算全编成羁绊、发展 Pt 与建设。合宿休息与外出合并为一个指令。

比赛按第一名结算。续步读取已选路线、现行预约和 Pioneer 赛历，并在第 74、76、78 回合结算 URA。普通自选赛和训练参加整局比较。当前回合采用实包比赛列表，未来按月份、育成年级、出道状态、可见禁赛和粉丝门槛生成，同等级续步选粉丝最多的可报名项。目标赛采用通用奖励，其他比赛按等级估计单属性、技能 Pt 和体力成本。粉丝使用 master 基值、支援加成、表现倍率及可选赛的 500 粉丝分支。路线粉丝门槛和累计出赛目标到期结算。路线失败的样本在终局提供 `failedCareerGoalId`，计入达标率分母和执行报告的 `careerFailures`。

成功训练按里程碑结算升级，跨建设阈值时应用设施及发券结算。每期规划进入建设决策，续步按设施在剩余回合可提供的收益比较合法有序方案。经典合宿期间触发的建设在第 41 回合结算，第 65 回合赠两券，岛训练券于剧本结束时失效。五期评审更新资源、训练和人员分布。合宿模式及第 73、75、77 回合的终盘训练按回合切换。

固定事件按回合和行动结算。普通剧情沿用 UmaAi-rust 的新回合单次抽签、支援阶段计数和友人解锁优先级，奖励范围为五维和技能 Pt。支援阶段从已见故事的 `show_progress_1` 统计。Tucker 同训重复事件单独抽取，手动外出按阶段发放得意率。第 31、55 回合用冻结谱系、相性、已购技能及当时的 Hint 等级抽取整组因子效果。Uma Plan 每次比较两个候选，按五维换算增量之和加两倍技能 Pt 增量选一个。开局缺 Uma Plan 字段时按有会员处理。

条件回放可在休息、升级确认或岛训练动作显式提供 `turnArrival`。结果为 `ready` 时提供五项 `board`、可用指令掩码和已揭示的 `islandBoard`。出现待处理故事时提供实包 `event`，回放在该边界保留故事和协议身份。

生成资源时用 `tools/UmaAiSharp.Pioneer.DataGenerator/freeze_holdout.py` 冻结整局切分。依次运行 `fit_turns.py` 和 `fit_training_partners.py` 产出回合校准。DataGenerator 接收 master、新资源路径、回合校准和由 `fit_inheritance.py` 生成的继承校准。继承报告列明各因子类型的拟合和留出机会、观察触发数与期望数。导入文件按规则资源 SHA-256 绑定，更新规则后从实包重新导入。

自主续步按卡组的代表训练板估计各等级的产出和有效机会。它从剩余可训练回合扣除预留岛训与恢复时间，结合副属性收益估计五维缺口。普通训练的成功次数每跨过一次升级门槛，会让后续一次同类训练提前使用较高等级。续步按到达门槛的机会折算增量，并计入评审升级、失败率与 Lv5 上限。岛券按后续可用回合、当前彩圈和体力估值，规划按设施分支及预计落成时间估值。

育成目标在终局评价，推荐先比较联合达标率，再比较达标样本的平均终局五维评分。全部候选零达标时比较终局条件缺口。执行报告包含终局属性、属性上限、技能 Pt、溢出、五维评分和 RNG 末态。PioneerAiPlugin 使用相同的排序。

## 原始回合导入

```json
{
  "name": "pioneer_import_packet",
  "arguments": {
    "packetPath": "<响应.msgpack>",
    "endpoint": "/umamusume/single_mode_pioneer/check_event",
    "masterPath": "<master.mdb>",
    "outputDirectory": "artifacts/import/turn"
  }
}
```

`packetPath` 是原始 MessagePack 响应正文，`endpoint` 必须填写该响应的实际路径。`load`、`start` 的嵌套 common 按端点解码。输出目录须为空，成功后包含 `input.json`、`candidates.json` 和保存包／master 哈希的 `import.json`。源 master 只读；执行输入只导出模型字段，不复制账号或完整包体。

支持 `scenario_id=11` 的开局、普通／合宿就绪态、建设规划、建设107／108、训练升级／休息确认和末尾角色结局。原始 `start` 导入应用固定开局的 +120 技能 Pt 和理事长登场，并保留该响应揭示的训练板。要复现开局事件后的实盘训练板，使用首个就绪响应。黄金船已配对的指定训练加成作用于同回合普通或岛训练。未知窗口和未来回合按普通角色机制推进。后续继承使用包含亲本与开局响应的抓包前缀。

事件选项通过抓包SQLite中的当前奖励响应导入，见[已揭示事件选项](#已揭示事件选项)。

使用 `masterPath` 导入的版本 1 训练输入包含 `development`，后续执行也使用 `masterPath` 编译建设成本和阈值。建设确认导入产生一个选项 0 候选。版本 1 末尾角色结局输入自带该阶段所需数据。

从抓包SQLite恢复成功普通训练次数：

```json
{
  "name": "pioneer_import_capture",
  "arguments": {
    "capturePath": "<抓包.sqlite>",
    "packetId": "<当前响应编号>",
    "masterPath": "<master.mdb>",
    "outputDirectory": "artifacts/import/history-turn"
  }
}
```

仅使用锚点及之前本局记录，配对动作请求，核对五项协议次数，区分普通成功、合宿与失败。相同请求的重复捕获合并，冲突和缺失历史明确失败。`input.json` 的 `successfulOrdinaryTrainingCounts` 按速度、耐力、力量、根性、智力排列，`null` 表示未知。规则资源模式下，从单个就绪响应自主执行时按等级和执行次数估计未知进度，`import.json.estimatedTrainingHistory` 标明是否估计。`import.json.trainingHistory` 记录配对动作恢复的范围、计数及前缀哈希。[规则与验证](training-history.md)

## 训练到建设

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer/training-construction-replay.json",
    "masterPath": "<master.mdb>",
    "seed": "83",
    "outputPath": "artifacts/training-construction.json"
  }
}
```

普通成功训练在回合5..60跨过一个本期建设阈值时，进入`ConstructionEvent`（kind=4、ordinal=1、key=`turn<<32|211067`）。第二个动作选择事件211067、角色0、选项0。建设确认结算达到阈值的计划前缀及券数，随后停止1007；两动作中已消费2次选择、完整结束1次动作。没有第二步脚本时明确返回脚本缺项，停在实际建设决策。[机制、输入和验证](training-construction.md)

## 休息与合宿休息

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer-rest-ordinary.json",
    "seed": "71",
    "outputPath": "artifacts/rest-ordinary.json"
  }
}
```

```json
{
  "name": "pioneer_run",
  "arguments": {
    "inputPath": "examples/pioneer-rest-camp.json",
    "seed": "71",
    "outputPath": "artifacts/rest-camp.json"
  }
}
```

```json
{
  "name": "pioneer_batch",
  "arguments": {
    "inputPath": "examples/pioneer-rest-camp.json",
    "seed": "71",
    "samples": 4096,
    "workers": 16,
    "outputPath": "artifacts/rest-camp-batch.json"
  }
}
```

`trainingTurn.availableRestCommands`为可空掩码：1表示普通701、2表示合宿3606、0表示已知不可用，null表示没有观测；不能混合普通与合宿模式。`characterId`由master映射，普通休息事件要求该身份。动作`kind=rest`，`commandId`为701或3606，其余身份字段为0。

普通休息的条件结果为`kind=restEvent`，data包含`actionOrdinal`和`event`四项身份；只提供选中的事件，不提供未来状态。选择休息先到`RestConfirmation`（决策kind=19），资源不变；下一脚本动作是该事件的`chooseEvent`、选项0，key为`turn<<32|eventId`。已经揭示的休息事件可由`pendingRestEvent`直接导入，要求阶段5、唯一当前事件和普通面板。

已识别普通事件确认恢复50或70体力；合宿行动恢复40体力、干劲+1。结算后返回`RestContinuationRequired`（1026），回合与后续协议阶段标记未知。缺可用状态为1024、缺普通事件结果为1025、缺master角色身份为1027；停止不生成终局建议。两个示例均返回 `stopped`，并记录已结算分量。[规则和原包窗口](rest-action-evidence.md)

## 训练升级确认

普通训练执行后的 `finalState.header.pendingTrainingLevelCommandId` 区分未知null、已确认无升级0及待升级的普通训练指令。达到阈值时只登记里程碑，等级在事件确认时增加；完整前态示例为 `examples/pioneer/training-level-trigger-replay.json`。[触发规则及原包顺序](training-level-trigger.md)。

升级确认沿用`start.kind=trainingTurn`，`pendingTrainingLevelEvent`提供当前已经揭示的事件。要求阶段5、角色0、故事400000005、时机6及唯一当前事件；五类事件211024／211027／211025／211026／211028依次对应速度／耐力／力量／根性／智力。目标等级须低于5；提供成功普通次数时，该项须为正4倍数。

导入生成一个`chooseEvent`候选，`choiceNumber=0`。脚本决策`kind=18`，`ordinal=0`，`key=turn<<32|eventId`；动作身份使用导入生成的候选。确认只增加对应等级1，消费当前事件，不消耗RNG；返回`TrainingLevelContinuationRequired`（1023），已消费1、完整结算0，回合与协议阶段未知。当前事件确认不生成后续队列。原包及正式入口核验见[审计记录](../artifacts/audit-user-20261109-01/mechanisms/REPORT.md)。

## 根候选推荐

```json
{
  "name": "pioneer_recommend",
  "arguments": {
    "inputPath": "artifacts/import/turn/input.json",
    "masterPath": "<master.mdb>",
    "candidatesPath": "artifacts/import/turn/candidates.json",
    "goalPath": "examples/power-goal.json",
    "seed": "71",
    "samples": 2049,
    "workers": 16,
    "outputPath": "artifacts/recommendation.json"
  }
}
```

候选文件是 `{ "id": 整数, "action": 完整动作 }` 数组。候选编号必须唯一且非负，动作经过正式合法性校验；每个候选使用相同样本数和逻辑样本种子。推荐只接受 `autonomous` 输入。此入口中的 `script` 从根候选执行后的下一决策开始，根动作由搜索提供；`run/replay/batch` 的脚本则包含起点动作。

任何候选轨迹停止时，报告中的排序为空、建议为 null，并保留全部候选样本。自主整局按联合达标率、达标样本的平均五维评分 `meanSuccessfulAttributeRating` 和候选编号排序。分量评价按配置的目标优先级比较。全部候选零达标时，先比较平均条件总缺口，再按目标优先级选择建议，达标样本均值为 null。`recommendationEvaluation` 附模拟达标比例，零达标另附 95% 单侧统计上界。口径见 [零达标建议与概率](recommendation.md#零达标建议与概率)。初始化遇到未知机制时，输出 `phase=initialization` 的停止报告。

末尾角色结局示例：

```json
{
  "name": "pioneer_recommend",
  "arguments": {
    "inputPath": "examples/pioneer/character-ending.json",
    "candidatesPath": "examples/pioneer/character-ending-candidates.json",
    "goalPath": "examples/power-goal.json",
    "seed": "71",
    "samples": 2049,
    "workers": 16,
    "outputPath": "artifacts/ending-recommendation.json"
  }
}
```

该入口验证回合78、角色状态3、阶段5及事件6／对应角色结局111，结算五属性各+5、技能Pt+20，随后生成终局指标。此处只有一个确认动作。其属性上限固定，推荐入口按该范围预计算目标曲线；普通回合的完整未来推进尚未连接到此终局。

## 普通与合宿训练回合

合宿示例：

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer/camp-training-turn.json",
    "seed": "71",
    "outputPath": "<新报告.json>"
  }
}
```

示例为第38回合的根性3604成功行动。`start.data.camp`包含普通等级、五项基础值和设施；决策视图`isCamp=true`，动作保留3601..3605协议命令。预览使用合宿Lv5，执行次数增加到对应普通种类，普通等级不被改成5。结算停止在训练后事件窗口。


```json
{
  "name": "pioneer_run",
  "arguments": {
    "inputPath": "examples/pioneer/training-turn.json",
    "seed": "1234",
    "outputPath": "artifacts/execution/training-turn.json"
  }
}
```

示例为原包 `packets-0003/1266` 的回合32前态，包含五项普通训练，脚本选择速度101。`start.kind=trainingTurn`，决策 `kind=14`、`key=turn<<32`；动作携带实际训练命令。`planningPeriods` 保存同一deck的五期master规划记录，用于核对当前回合与已完成评审数。示例由七窗口夹具及对应已编译支援面板导出。

成功时结算即时资源、发展Pt、羁绊及训练次数，停止在 `TrainingEventsRequired`；只提供成功／失败标志时，失败记录次数并停在 `TrainingFailureEventRequired`。工具返回 `stopped`，动作消费1次、完整结算0次。提供已识别失败事件时可继续至失败选择，见下方回放示例；下一回合事件及完整终局仍有待接入。

条件回放将 `mode` 设为 `conditional`，并提供 `{"kind":"trainingResult","data":{"actionOrdinal":0,"success":true}}`。该结果只控制成功／失败，不输入收益或后续状态。

## 已揭示事件选项

以`get_choice_reward`响应编号为锚点：

```json
{
  "name": "pioneer_import_capture",
  "arguments": {
    "capturePath": "<抓包.sqlite>",
    "packetId": "<get_choice_reward响应编号>",
    "masterPath": "<master.mdb>",
    "outputDirectory": "artifacts/import/event"
  }
}
```

导入器向前关联同一viewer的配对奖励请求及唯一当前事件，只读取锚点及之前记录。奖励查询之间可以重复；相同请求身份的重复捕获必须内容一致，奖励窗口不能跨过未闭合动作或其他状态变化。选项声明按可见顺序映射，奖励组键不当作选项编号；多个候选结果和未解释原子原样保留。

输出`input.json`的起点为`declaredEvent`，`candidates.json`包含每个可见选项。`import.json.packetSha256`对应当前状态响应，`eventRewards`记录状态、请求、奖励三个包号和关联窗口哈希。此入口不要求捕获开局，也不推定连续事件历史完整。奖励声明含null原子等未解释格式时退出2，不生成执行输入。

执行脚本使用`import.json.decision`作为`decisionId`，以及所选候选的`action`。已有真实输入示例：

```json
{
  "name": "pioneer_run",
  "arguments": {
    "inputPath": "examples/pioneer/event-choice.json",
    "seed": "1234",
    "outputPath": "artifacts/execution/event-choice.json"
  }
}
```

示例来自 `packets-0003:55467/55469`，回合57、故事501068510。`start.kind=declaredEvent`、决策kind=15，包含当前资源、角色效果和所有选项的奖励声明。选择1的耐力声明为+10，执行结果为1199→1204。`PioneerEventCompiler`从当前角色、事件和`choice_reward_array`编译相同输入。

两个原始抓包库中，6,993个连续奖励窗口有6,991个可编译为15,433个选项；两份声明包含null奖励原子。正式CLI核验覆盖五个真实起点的导入与执行，包括上述1200跨越、多结果及带剩余效果的事件。[入口核验](../artifacts/implementation/event-import-01/REPORT.md)。

同一个选项可以有多个`branches`，顺序保留原奖励查询中的记录顺序。单一结果直接结算；多个结果的条件回放使用`{"kind":"eventRewardResult","data":{"actionOrdinal":0,"choiceNumber":1,"branchIndex":0}}`，索引从0开始。未提供结果时停在`EventRewardOutcomeRequired`。

资源结算后停在`EventRewardRemainderRequired`。状态的`EventRewardResourceMask`按位0..8依次表示速、耐、力、根、智、体力、体力上限、干劲、技能Pt；`EventRewardRemainingEffects`记录其他奖励原子的数量。原子内容保存在输入中，事件历史及后续调度尚待接入。公式与实包范围见[事件资源结算](event-reward-evidence.md)。

## 失败条件回放

仓库提供两条来自实包的失败片段：

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer/ordinary-failure-replay.json",
    "seed": "1234",
    "outputPath": "artifacts/execution/ordinary-replay.json"
  }
}
```

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer/camp-failure-replay.json",
    "seed": "1234",
    "outputPath": "artifacts/execution/camp-replay.json"
  }
}
```

| 示例 | 行动前 | 已识别条件结果后的分量 |
| --- | --- | --- |
| `ordinary-failure-replay.json` | 回合30，普通耐力训练105，耐力535，干劲5，执行次数1 | 耐力530，干劲4，回合31，执行次数2 |
| `camp-failure-replay.json` | 回合37，合宿耐力训练3602，耐力795，干劲5，执行次数8 | 耐力790，干劲4，回合38，执行次数9 |

两个示例的`start.kind`均为`trainingTurn`，由对应原始响应和master导入完整五项训练、六卡、资源与设施，`pendingStoryIds`为空。首个决策为kind14、key为`turn<<32`；失败后为kind2、ordinal1、key含实际event_id。`outcomes`单独提供`trainingFailure`事件身份和下一动作的`failurePenalty`分支；这些条件结果不包含后态。

耐力次数在训练尝试时增加一次，五项协议次数与耐力分量同步；进入失败选择时保留原资源和两条RNG。选择后扣耐力；正向思考25存在时抵消干劲下降并被消费，否则扣干劲。`trainingResult:false`只表示失败，不能替代具体事件身份。

两个回放均返回 `stopped`。先执行失败尝试到事件选择，再执行明确的选择分量，最终停在 `NextTurnEventsUnknown`。报告记录消费2个动作、完整结算1个动作；第二个动作的已知数值已经生效，后续自动事件未完成。回合已知，后续游戏阶段未知；不会把后包阶段或未来状态复制进执行状态。

## 不提供条件结果

```json
{
  "name": "pioneer_run",
  "arguments": {
    "inputPath": "examples/pioneer/ordinary-autonomous.json",
    "seed": "1234",
    "outputPath": "artifacts/execution/ordinary-autonomous.json"
  }
}
```

该示例使用普通失败分量前态，`mode` 为 `autonomous`，`outcomes` 显式为空。`failureTraining`起点在`data`中明确提供`supportBonds`和`characterEffects`；`state`保存失败计算的标量状态。当前失败事件生成规则未接入，执行在 `TrainingOutcomeRequired` 停止：动作尚未消费，资源和回合保持前态，工具状态为 `stopped`。

## 批量执行

```json
{
  "name": "pioneer_batch",
  "arguments": {
    "inputPath": "examples/pioneer/ordinary-failure-replay.json",
    "seed": "1234",
    "samples": 131,
    "workers": 4,
    "outputPath": "artifacts/execution/ordinary-batch.json"
  }
}
```

`pioneer_batch` 使用输入中明确指定的模式。`samples` 是正整数，`workers` 为1..32，`seed` 为 0 至 18446744073709551615 的十进制字符串。同一起点、脚本、条件结果与种子下，改变 worker 数量不改变对应 `sampleIndex` 的结果；增加样本数不改变已有前缀。

MCP 客户端可请求取消执行；报告保留全部请求样本，未执行或中途取消的槽位记录 `cancelled`、已消费动作与 RNG 末态。报告的 `execution` 字段记录执行范围、结果来源与计时范围。

条件示例不会抽样训练失败或惩罚分支，逐样本 RNG 状态仍记录各自派生的规则／策略随机流。批量重复条件片段不能作为失败概率、终局达标率或推荐质量估计。

## 建设规划

用 `pioneer_import_capture` 导入 `playing_state=35` 且事件队列为空的规划响应，传入包含 `planningSubmission` 校准的 `rulesPath`。导入的 `candidates.json` 包含所有合法有序方案。把 `input.json` 和 `candidates.json` 交给 `pioneer_recommend`，沿用同一份 `rulesPath` 和育成 `goalPath`。提交后的人员分配与事件资源从规则拟合抽样，跨发展 Pt 阈值时按方案顺序结算设施；报告保留各方案逐样本的终局和 RNG 末态。

`pioneer_import_packet` 也接收规划响应。缺少训练前缀时，根据当前等级、评审与执行次数估计成功普通训练进度。合宿首回合的岛训练券使用提交前规划响应中的计数。轨迹中的后续规划按剩余回合的设施收益比较合法方案。

指定方案的局部提交可使用以下示例。

```json
{
  "name": "pioneer_run",
  "arguments": {
    "inputPath": "examples/pioneer/planning-input.json",
    "masterPath": "<master.mdb>",
    "seed": "1234",
    "outputPath": "artifacts/execution/planning.json"
  }
}
```

示例来源为回合3的规划前态和提交计划：先海之家Lv1，再速度Lv1、耐力Lv1，总成本400。`start.kind` 为 `planning`，`action.kind` 为 `submitConstruction`，`action.plan.steps` 保存完整有序升级；其余动作字段为0。决策 `kind=13`，`key=turn<<32`。

该局部示例记录消费、结算各1个动作，回合3／阶段1，设施保持规划前值，计划进入轨迹独占缓冲；停止码为 `PlanningContinuationRequired`。规划模型和候选枚举见 [回合推荐实现](recommendation.md#有序建设计划)。

## 输入与报告

输入包含 `schemaVersion`、`rulesIdentity`、`mode`、`start`、`outcomes` 和 `script`。`start` 与每个 outcome 使用 `{ "kind": "...", "data": { ... } }`；数据结构对应 `PioneerStart` 和命令支持的条件结果类型。建设规划、建设事件、评审及带`development`的训练起点必须提供 `masterPath`，其余起点拒绝未使用的 `masterPath`。master 只读，来源 SHA 进入报告。

脚本的 `decisionId` 包含：

- `kind`：当前 `PioneerStage` 的数值。
- `ordinal`：本次执行从0开始的决策序号；不等于回合数。
- `key`：`PioneerScenario.DecisionKey(turn, eventOrCommandId)`，即 `((long)turn << 32) | (uint)id`。单指令片段用 command ID，五选一普通训练回合用0，事件用 event ID。

动作对象显式提供 `kind`、`commandId`、`eventId`、`characterId` 和 `choiceNumber`；不适用的字段也明确写0。属性对象和事件身份字段必须完整，不能用缺字段表达未知。重复字段、未知字段、数字枚举、缺失必需字段、无效模式、矛盾的条件结果顺序或未来状态字段均拒绝。数组中的null条目在JSON边界拒绝并报告路径；允许为空的对象属性仍可显式写null。`pioneer_run` 接受自主模式，`pioneer_replay` 接受条件模式。

输出文件必须不存在，已有报告和输入文件都不会被覆盖。报告包含输入／master 指纹、规则与 RNG 身份、逐样本进度／停止原因／随机状态，以及单次执行的局部状态；批量报告不保存每个样本的状态副本。`outcome=null` 表示没有完整终局，不能补成零值参与目标评价。

执行与推荐的 `completed` 表示全部样本生成终局指标，导入的 `completed` 表示输入和候选已生成。`stopped` 保留明确的停止原因。无效参数和文件错误返回工具错误。成功终局要求动作脚本与条件结果全部消费。

## 来源与验收

`tools/UmaAiSharp.Pioneer.DataGenerator/verify_pioneer.py` 通过 stdio 复跑当前编成的第 1、3 回合和首期建设规划，并从两份冻结抓包重建 212 局默认 Omakase 历史基线。运行需要 Python 的 `msgpack` 和已构建的 MCP。按顺序传入两个历史库，输出目录须不存在。

```text
python tools/UmaAiSharp.Pioneer.DataGenerator/verify_pioneer.py --mcp <MCP.dll> --rules <pioneer_rules.br> --goal examples/stamina-curve-goal.json --capture <当前抓包.sqlite> --baseline <历史库1.sqlite> <历史库2.sqlite> --workers 32 --output <新目录>
```

报告保留模型、终局均分、达标率、单／多 worker 一致性，以及严格超过历史基线均分的样本比例。均分优势以双组独立重采样的单侧 95% 下界检查，超过历史均分的 95% 比例单列为优化目标。历史基线的支援编成与当前编成不同。

示例从 `tests/UmaAiSharp.Tests/Fixtures/training-failure-snapshots.json` 的两条已解码窗口提取。原包库 SHA 为 `ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b`：普通窗口58275..58281，合宿窗口52989..52995。示例只保留游戏字段及匿名来源定位，不包含包体或账号身份。夹具中的后态仅用于测试断言。

`PioneerExecutionTests` 核对条件回放、自主停止、取消槽位、输入校验和拒绝覆盖。固定种子下的 worker 一致性与样本前缀由 Core 批次测试和代表性 Pioneer 轨迹核对。`McpStdioTests` 使用真实子进程核对工具发现、未知参数拒绝，以及抓包导入到严格推荐的产物交接。
