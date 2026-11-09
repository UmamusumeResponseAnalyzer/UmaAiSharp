# 开局组合与相性分量

`InitialStateRules` 计算 start 阶段的五属性、五上限、十项适性、两个直接父母的绿因子 Hint、初始技能集合和六个支援卡位的初始羁绊。`InheritanceRelationRules` 计算角色相性和共同 G1 分量。它们不生成开局事件队列、可操作面板、NPC 出现状态、技能 Pt 或途中随机继承。

## 输入与边界

完整亲本通过 `InheritancePedigree.MapParent(parent, parentSlot)` 解析。基础因子保存为局部位置 1/10/20 及可见的更远祖辈，`FlatExtensions` 是整个亲本对象的顶层数组。本地 1/10/20 分别映射到当前第一亲本及其两祖辈，或第二亲本及其两祖辈。只使用选中亲本冻结的谱系，不取祖辈当前殿堂重新覆盖。

顶层数组显式为空即覆盖三个有效来源；祖辈没有嵌套数组不表示缺数据。顶层字段缺失、重复位置、扩展指向缺失基础、跨组替换、重复替换/新增明确失败。非空记录保留基础 ID 和扩展结果映射，结构解析不代表实际育成效果已验证；CLI 与 `Compose` 不放行非空扩展结算。

`InitialStateRules.Compose(character, scenarioLimits, sources, deck)` 接收解析后的完整六来源。`ComposeListedFactors` 仅作已列因子分量计算，不认证原始快照完整性。原包核验在确认亲本顶层输入后调用 `Compose`。

`InitialInheritanceRules.EvaluateListedFactors` 提供对应蓝/红分量。重复因子 ID 可以出现在不同谱系槽位，每槽独立贡献；同槽重复、缺少蓝/红或未知因子编号会失败。蓝/红原规则与来源见 [开局继承证据](initial-inheritance-evidence.md)。

此前将 316 个祖辈的嵌套字段缺失计为扩展输入未知，属于适配层误判。[扩大研究](../artifacts/research/blockers/inheritance/expansion-20261108/REPORT.md)的 896 份非空完整快照、801 条亲子边确认了扁平位置映射，其中 37 条亲子边还区分了子代创建后增加的强化。221 局选中亲本的 442 份顶层数组全部显式为空；非空扩展的实际开局/四月效果仍缺样本。

## 组合规则

五属性为 `card_rarity_data` 基础值，加六来源开局蓝因子，再加支援初始属性。上限为卡稀有度上限、剧本 11 上限增量与蓝因子上限之和。红因子沿用 master 的总星数分段、最高升到 A 的规则。

支援面板先根据稀有度和经验确定等级，对有效等级锚点线性插值取整；未解锁的项为零。初始属性使用 effect 9..13，直接固有同类相加。105 固有遍历全部六个卡位，包含自身：普通卡给对应训练属性增加 `value`，友人/团卡给五属性各增加 `value_1`。多个 105 分别贡献；扩大研究覆盖支援 30141 的 75 局，多个来源及团卡分支属于同参数机制的外推。

固有分类依据 [支援研究类型表](../artifacts/research/blockers/support-placement-hint/REPORT.md)：102/103/104、106..114、116/117/120 是训练条件效果，118/119/122 是位置效果，121 是训练羁绊效果；它们不参与初始属性/羁绊。101 如嵌套初始效果则明确失败。115 全队初始羁绊的对象和上限未实包核验，当前入口明确拒绝；未知原子或固有类型不按零处理。

绿因子只取两个直接父母。先按 `succession_initial_factor` 的星数选择 `effect_id`，再取对应组的 `target_type=41` Hint 分量，通过 skill master 映射到 `(group_id, rarity)`。同组其他上限效果属于途中继承 bundle，不在开局执行。3 星的 selector 为 4，对应 Hint +3，不是 Hint +4；扩大研究已包含 10 个父母绿星 3 来源及 127 个蓝星 3 来源。两个父母若指向同一 Hint，合并语义未核验，明确失败。

初始技能集合从 `card_rarity_data.skill_set` 读取 `skill_set` 中非零 skill_id 及对应 skill_level，保留等级，拒绝重复技能。本入口限定已核验的稀有度 1..3、才能等级 1；不把这些输入的完整匹配推广为才能 Hint 强化等规则。

`InitialSupportRules` 同时输出属性与羁绊。初始羁绊取卡位自己的面板 type14 等级插值及已解锁直接固有 type14；不广播给其他卡位。超过 100 的截断边界没有认证，明确拒绝。实包用 support_card_array.position 对应 evaluation_info_array.target_id，只比较六个支援卡位，不补全部 NPC。

扩大研究的 221 局覆盖 45 个育成卡、21 个支援卡。它是有限卡组上的组合检验，不能拆成每个新卡参数都经独立实包验证。start 的技能 Pt 0 与开局事件后的首个可操作面板 Pt 120 是不同阶段；本组件没有将任何一个阶段的 Pt 写成通用初始常数。

## 相性

角色身份从 `card_data.chara_id` 解析。关系项为参与角色的关系类型交集所对应的 master 点数之和。三人关系出现相同角色时该项为零，共同 G1 加分继续保留。

共同 G1 只取 `win_saddle_type=3`、`condition=0` 且经 race_instance 关联到 `race.grade=100` 的鞍点。按 `relation_group_id` 去重后取交集，每个共同组加 3；同比赛的不同鞍点 ID 不重复加分，G2/G3 和称号不加入。

祖辈来源分量为育成角色、直接父母、该祖辈的三人关系，加父母与该祖辈共同 G1。直接父母来源分量为育成角色与父母关系、两个父母关系、所属两个祖辈的三人关系及对应 G1 边。合计两个父母分量后扣除重复计入的父母间关系与 G1 边。

来源是 [继承研究](../artifacts/research/blockers/inheritance/REPORT.md) 与本地 master。研究中该候选与 79 个请求的 `current_succession_rank_point` 一致；该字段来自客户端请求，不是服务器随机继承概率的验证。未实现触发率、两次继承独立性、随机增量档位权重或满 Hint 转化规则。

## 实包核验

```powershell
dotnet src/UmaAiSharp.Cli/bin/Debug/net10.0/UmaAiSharp.Cli.dll evidence initial-state `
  --study artifacts/research/blockers/inheritance/expansion-20261108/frozen-01 `
  --input artifacts/research/blockers/island-training/expansion-20261108/audit `
  --packets C:\Users\micro\AppData\Local\UmamusumeResponseAnalyzer\PluginData\DebugSaveAllPackets `
  --master G:\DMM\Umamusume\umamusume_Data\Persistent\master\master.mdb `
  --output artifacts/evidence/initial-state-audit-03
```

研究 `starts.jsonl` 只提供 start 定位。核验器重新解码原始请求，再按请求选择的殿堂 ID，在同来源已捕获的 load/index、pre_single_mode/index、trained_chara/load 及 finish 自有快照中独立选择开局前最新记录。研究亲本位置仅用于对照；原包中更新的记录不会被旧定位覆盖。借用或活动亲本选择未核验，明确拒绝。

要求参与者一致、明确配对和严格先后顺序；不读取研究输出的预测属性、技能、羁绊或相性作为真值。start 中的稀有度、才能等级、支援经验是明确的初始化输入，其他对应后态字段仅用于比较。最新快照只指已捕获范围，不能证明捕获外没有更新。

源库可以比旧研究多出后续捕获，但会记录当前指纹与旧指纹不一致，再核对定位记录。所有本次输入读取前后 SHA-256 必须一致；SQLite 只读打开。报告仅输出匿名来源、原始记录 ID 和游戏数值，不输出捕获文件的账号名或原始 payload。

`initial-state-audit.json` 汇总七组分量、无法评价的输入与不匹配；`initial-state-samples.jsonl` 保存预测分量、实包对应值和实际使用的原始快照位置。`CompleteFactorInput` 只表示六来源因子输入已完整解析，`ListedComponentsMatched` 只指参与比较的字段；它们均不能代替整局或完整开局验证。没有输入问题时 CLI 返回 3 表示这一证据边界；存在报告问题时返回 1。

2026-11-08 的 [initial-state-audit-03](../artifacts/evidence/initial-state-audit-03/initial-state-audit.json)重新读取了 221 份 start 请求/响应和 442 个开局前最新自有亲本：

| 比较项 | 结果 |
|---|---:|
| 五属性、五上限、十适性、父母绿 Hint | 每组 221/221 匹配 |
| 客户端相性、初始技能集合、六卡位初始羁绊 | 每组 221/221 匹配 |
| 逐支援卡位羁绊 | 1,326/1,326 匹配 |
| 完整六来源因子输入 | 221；亲本顶层扩展均显式空 |
| 数值反例 / 无法评价开局 | 0 / 0 |

本次报告唯一问题为 `CaptureChangedSinceResearch`：`packets-0002` 的当前 SHA-256 为 `be9a536045d6b41977ed97d006c102a2e8305420281a620ba3ae06103d96fb5c`，与冻结研究记录不同。核验仍从当前原始行重建输入，没有沿用研究预测；所有十份本次输入读前后哈希一致。该差异保留为输入来源提示，CLI 本次返回 1，不能将匹配计数写成无条件 `Passed`。

历史 [initial-state-audit-02](../artifacts/evidence/initial-state-audit-02/initial-state-audit.json)复核旧 79 局五组分量，各 79/79；其中 `MissingAncestorExtensionCoverage` 是已定位的字段范围误判，不是游戏状态缺失。原始产物保留，不将历史计数改写成新实现的结果。

研究当前 master SHA-256 为 `4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886`。两大原包库 SHA-256 为 `ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b` 与 `f487db586de49cd35bb93ac489df49963794cf22cd732be3df077df594361982`；新旧匿名别名不同，不用裸别名跨批拼接。221 局含旧 79 局和本继承研究新增分析的 142 局，不是新的独立账号、谱系或预注册留出集。

测试覆盖顶层扩展的代际映射与冲突拒绝、绿因子 selector 与途中 bundle 分离、105 编成计数、技能/羁绊投影、相性同角色与鞍点组去重，以及原始 SQLite 最新快照选择、缺字段、数值反例、错配、坏时间和重复捕获。本轮最终集中测试 234/234 通过，记录位于 [integration.trx](../artifacts/validation/research-followup-20261108-03/integration.trx)；测试数量不代替原包或完整育成验收。

途中继承的 383/440 联合相容解没有接入生成器：正反顺序均相容，不能据此确定执行顺序或档位权重。剩余 57 个满 Hint 相关窗口、动态上限顺序、addon、非空扩展实际效果仍未闭合。没有以固定均值、任意概率或观察后态补齐这些规则。
