# Pioneer 建设机制：实现与证据范围

当前实现使用本地 master 和实包。用户已明确允许版本不严格一致；来源哈希与捕获版本保留用于差异归因，版本未绑定不阻止本地规则实现。本文的数量由最终 `local-audit-05` 导出及 `construction-audit-06` 复跑确认，不代表其他数据版本或全部合法游戏状态。

## 复核入口

```json
{
  "name": "evidence_construction",
  "arguments": {
    "evidenceDirectory": "artifacts/evidence/local-audit-05",
    "outputDirectory": "artifacts/evidence/construction-audit-new"
  }
}
```

输出目录必须为空或不存在。工具读取 `audit.json`、`master-rows.jsonl`、`pioneer-observations.jsonl`、`pioneer-actions.jsonl` 和 `exchanges.jsonl`；复用归一化器的 load/start/finish 路由，不从邻近回合补齐缺字段。输出包括：

- `construction-audit.json`：统计、部分观测和未知机制。
- `construction-observations.jsonl`：每个可核验快照的即时建设假设差异。
- `construction-settlements.jsonl`：建设事件前后设施、券数量和明确请求选择的核验。
- `construction-triggers.jsonl`：建设事件首次出现在响应数组时的 Pt、阈值计数与 `last_gain_rights_num`。
- `construction-component-changes.jsonl`：同局相邻可读观测中，设施、券数量或券字段是否给出数值的变化；保留来源记录和请求对应情况。字段不出现不解释成 0 张券。

建设分量的核验结论见工具结果的 `summary.validation`。

## 已核验的数据和局部分量

本地 master 的 `single_mode_11_facility_lv_up` 有 43 条升级边。394 对成功 planning 请求/响应的目标边均能查到，计划按 `priority` 排序；响应数组顺序不代表执行顺序。请求中的 `prev_state_id` 均为 0，而响应给出实际前态，不能把请求的 0 解释成未建设设施。

394 对计划均属于 `deck_id=3`。用请求的回合查询 master planning 表，`target_pt` 与响应 `planning_cost` 一致；其中 6 对计划成本低于预算。因此实现保留完整有序计划，成本分析允许剩余预算；没有把“必须花光预算”或“不能重复升级同一设施”写成合法性规则。重复设施计划尚无成功样本，完整提交条件明确报未知。

| 正式复核项目 | 结果 |
| --- | ---: |
| 具有所需角色阶段字段的非空计划观测 | 18,747 |
| 仅含计划、缺对应角色阶段的部分响应 | 919 |
| “Pt 达到累计成本立即建设”反例 | 5,514 |
| 400011107 消失后的相邻可核验观测 | 710 |
| 400011107 按当前 Pt 结算累计成本的匹配 | 703 |
| 400011107 按已达到最高岛训练阈值结算的匹配 | 710 |
| 400011107 实际执行局部候选后设施/券数量匹配 | 710 / 710 |
| 400011107 请求选中对应事件且 choice=0、回合一致 | 705 |
| 400011108 可核验观测及明确请求对应 | 72 / 72 |
| 400011108 实际执行局部候选后设施/券数量匹配 | 72 / 72 |

710 条中的另外 2 条缺对应请求，3 条请求选中的 event_id 与前态不同。它们可以支持数值相关性核验，不能算作明确的直接请求对应。即使请求对应，也不能据此宣称完整事件调度和所有伴随效果已经证明。

`ApplySettlementComponentsCandidate` 更新不可变局部状态中的设施和岛训练券数量。初始证据范围为 `deck_id=3`、无重复设施计划、明确持有 0 或 1 张券，且只显示以下一个事件：400011107 / `turn=5..59` / `playing_state=5`，或 400011108 / `turn=41` / `playing_state=1`。设施使用本地 master 费用，以已达到的最高 `gain_shima_training_pt_array` 阈值决定完成的计划前缀；已完成设施保留。该批400011107的券观测为0→1共618条、1→1共92条；400011108分别为55条和17条。当前实现支持已核验的turn60普通训练及比赛Pt建设边界，范围和新增机会分母见[训练到建设](training-construction.md)。实现设置剩余券数量，不推断被持有上限丢弃的券数。

这个方法不移除事件、不推进回合，也不生成训练成员分配。`PioneerDevelopmentState` 是局部观测组件，不是可独立开始完整育成搜索的根状态。代码没有用部分实现冒充 `ISimulationScenario`。

## 触发与结算顺序的反例

710 次建设事件首次出现在响应数组的观测中，708 次与当前计划下已达到阈值的数量增加一致；另外 2 次前后 Pt、阈值计数及 `last_gain_rights_num` 均不变，经 `check_event` 后才显示建设事件。不能只在 Pt 增加时判断是否需要显示该事件。

这两例已回查 `local-audit-05` 对应的原始 SQLite 捕获窗口，来源 SHA-256 为 `058d9ce35589ddc9900e0d6574c469645406ab621ef83c36daaf6f7271181845`：

| 窗口 | Pt 过线 | 先显示的事件 | 随后显示建设 |
| --- | --- | --- | --- |
| 22206..22219，turn 53 | 22215：378→458，阈值 400 | 400011019「無人島の大魔王」，play_timing=6 | 22217：400011107，play_timing=10 |
| 23398..23417，turn 15 | 23413：186→207，阈值 200 | 501038524「カワイイライブの後で……♪」，play_timing=3 | 23415：400011107，play_timing=10 |

第一例经 Tucker 奖励事件 400011111 获得 80 Pt；第二例经比赛 Pt 事件 400011105 获得 21 Pt。两例过线时 `last_gain_rights_num` 已从 2 变为 1，但建设尚未显示。400011019 在整批 79 次观测中均位于 turn 53；501038524 是本地 master 标明的角色事件，在这批数据中只有一例，不能据此说它固定发生在 turn 15。

两个原始窗口分别有 14、20 条连续记录；各自只有一个明确的育成身份，捕获时间单调，请求响应全部成对，没有重复 payload，也没有 `load`、登录或重连加载路由。窗口内可见的是同一动作/比赛后的事件处理链，未重复执行回合动作。这足以用事件处理顺序解释建设显示延迟；捕获不包含 TCP 连接日志，不能据此断言期间绝无底层网络重连。

`last_gain_rights_num` 也不能直接用作每期清零的已处理阈值游标：239 次新事件出现时该值从 2 降至 1；前一期结束后的值可保留到下一期第一次建设事件。完整触发机制还需要区分计划阶段和已经发生但尚未显示的事件。

400011107 结算后 265 次推进回合，445 次保持回合；后续事件数组有 156 种。岛训练成员分配也可能改变。因此“移除建设事件，固定返回普通训练”不成立；响应提供的事件数组也不能据此视为全部内部后续事件的可见清单。

400011108 的 72 条前后观测均保持 turn 41、playing_state 1。它说明相同设施分量可以通过另一条事件路径结算，不能把未进入 400011107 样本的路径当成零概率。

完整变化清单保存在报告的 `componentChanges` 及 `construction-component-changes.jsonl`。当前观察到 788 次设施变化，其中 782 次属于上述两个候选分支；其余 6 次出现在 `check_event` 或 `exec_command`，尚不能与前态的明确事件选择对应，不能据此新增规则。券数量变化还出现在 `shima_training_exec`；字段数值的出现/消失还涉及 `planning`、`pioneer_complete` 和 400011021 / 400011022 / 400011110 等事件路径。清单区分请求对应与单纯前后差异，不把相邻响应直接视为完整因果证据。

这 6 个窗口也已回查原库：每个窗口都有在整库中找不到对应响应的请求，或找不到对应请求的响应；缺失记录列表保存在 `construction-window-evidence.json` 的 `rawWindows`。部分请求明确选择了已观测到的建设事件 ID 211067/211076，但缺少即时前态/响应，不能补造成完整转移。当前没有据此证明第三条设施结算路径；也没有证明全游戏只存在 107、108 两条路径。

脱敏反例位于 `src/UmaAiSharp.Pioneer/Evidence/`，只保留源文件哈希、记录 ID、游戏数值及路由，不含账号或会话标识。7 个当前 Pt 反例、2 个延迟显示反例及 `last_gain_rights_num` 降低示例均可定位原始记录。

## 待结算事实与事件窗口

`ConstructionTriggerRules.ApplyPointGainCandidate` 接收当前周期的 Pt、阈值、阶段、事件队列，以及由其他已核验机制给出的 Pt 增量。它更新 Pt，并在跨过一级阈值时返回 `PendingConstructionMilestone`；不向可见事件队列插入故事，不建造设施，不发券。

| 已知 Pt 来源 | 待结算故事/窗口 | 连续证据夹具 |
| --- | --- | ---: |
| 普通训练 | 400011107 / play_timing 6 | 538 |
| 岛训练 | 400011107 / play_timing 6 | 70 |
| 比赛 Pt 事件 400011105 | 400011107 / play_timing 10 | 52 |
| Tucker Pt 事件 400011111 | 400011107 / play_timing 10 | 39 |
| 经典合宿训练 | 最早 turn 41 的 400011108 / play_timing 1 | 70 |

这 769 条夹具全部来自实际窗口。两条普通延迟案例包含在 timing 10 的数量中；已达到阈值的待结算事实会保留到相应窗口，而不是要求每次显示事件时 Pt 都再次增加。原始枚举中的相同故事 400011107 使用两个 event_id：211067 对应 timing 6，211076 对应 timing 10。因此只保存 story_id 不足以表示完整事件窗口。

107 在这批严格夹具中出现于 32 个不同回合，不能把设施完成等同于几个固定规划回合。master 的规划回合为 3、12、24、36、48；经典合宿后 108 的 turn 41 则是单独的固定返回窗口。

日文 [VIP Wiki 的游戏帮助转录](https://wikiwiki.jp/vip_umamusu/無人島へようこそ%20-DESIGN%20YOUR%20ISLAND-) 说明经典合宿期间可积累发展 Pt，设施升级与券获取留到合宿结束后。该说明与本地 turn 41 的结算吻合；它是社区转录，未当作独立的一手官方页面。代码把这个期间规则应用于 turn 37..40，其中实包跨阈值只直接覆盖 39/40，37/38 是由期间说明支持的泛化，不是逐边界实测。

官方 [Cygames FAQ](https://support.umamusume.jp/faq/) 直接说明了未花光计划成本仍可能允许确认的情况，例如仅剩熟练技巧路线可选；官方 [剧本介绍](https://umamusume.jp/contents/game/scenario/designyourisland/) 将半年规划与积累 Pt 推进建造分开说明。这些资料没有给出服务端事件队列的完整排序算法，代码没有把 timing 数值解释成所有事件的通用优先级。

核验器会验证过线请求、响应和前态的同源顺序，奖励预览请求必须成对且完成于所选动作之前；延后显示期间的请求响应也必须完整。两条跨未解释局部包的经典合宿关联（`6638→6658`、`2200→2224`，来源分别为 `9caf1b...`、`058d9c...`）单列在 `associationOnlyCrossings`：中间有缺育成身份的 `race_start` 及缺事件数组的 `race_end`，未纳入 769 条连续夹具。它们支持关联调查，不认证可恢复事件链。

当前 API 明确拒绝其他 deck、未知 Pt 来源、与 master 不一致的阈值、非空训练前事件队列、一次跨过多级阈值等未覆盖输入。它不维护完整待处理事件队列，也未证明多个尚未结算阈值之间如何合并。Pt 增量本身由训练、比赛或事件组件负责。

可复跑证据工具：

```powershell
python src/UmaAiSharp.Pioneer/Evidence/audit_construction_windows.py --input artifacts/evidence/local-audit-05 --packets <原始SQLite目录> --master <本地master.mdb> --output <新的JSON文件>
```

本次报告为 `artifacts/evidence/construction-windows-04.json`；仓库内保留 `construction-window-evidence.json`、`construction-window-fixtures.jsonl` 和所需 `construction-master.jsonl`。脚本以只读方式打开原库，核对原库与归一化来源哈希，只输出匿名引用、数字及 master 事件标题。5 项 `PioneerConstructionTriggerTests` 覆盖全部 769 条连续夹具、两条延迟显示、经典合宿返回、已达到阈值不重复触发及未知输入停止；测试由统一集成运行确认。

## 未完成项

建设部分尚未实现完整计划合法性验证、重复设施与路线限制、事件调度优先级、训练成员分配、所有 deck 的效果、晚期券赠送/保存/过期，以及 checkpoint 和岛完成后的全部结算。完整动作执行不要求枚举全部建设方案。没有用平均概率补齐这些机制。

完整 Pioneer 模拟还需要基础育成机制、完整训练公式和效果顺序、属性单位/上限/截断、人物和支援卡事件、影响后续育成的技能/效果状态、继承、育成层比赛结果与奖励、终局处理等。建设局部分量通过不等于这些机制完成；购技和进化按用户确认范围排除，其余未知仍属于后续核心实现范围。

## 建设发券分量补充

设施结算返回值包含`After`、实际发券数与超出本阶段持有上限的丢弃数；持券1→1不表示未发券。777个明确选择的`gain_num=1`证据和核验入口见[development-components-evidence.md](development-components-evidence.md)。此返回值不消费事件或推进回合。

扩大研究补充回合40比赛Pt跨阈值后待turn41的108窗口；两条具体来源和未覆盖边界见[事件与成员分量证据](pioneer-event-components-evidence.md)。1101个成功计划没有解除完整可提交判定的证据缺口。
