# 技能与事件的本地证据

本地材料足以读取技能定义、基础需要点数、提示候选和进化条件描述，并复核已观察到的技能状态变化；尚不足以独立实现完整购技结算和全部角色、支援卡事件规则。本轮没有新增猜测性的技能规则或空实现骨架。

材料使用 `artifacts/evidence/local-audit-04`、本地 master、已解密的资源索引，以及两个事件资源的工作区副本。按用户确认的范围，版本严格一致不是研究前提；结论仍限定于实际读到和核对过的内容。没有使用 Host 的技能价格公式作为游戏事实。

## 捕获能证明什么

当前材料共有 79 个开局，覆盖 37 张育成角色卡、20 张支援卡。master 中有 274 条 `card_data` 和 566 条 `support_card_data`；这些条目数量不代表捕获已经覆盖其行为。

全库 endpoint 清单中，唯一包含 `skill` 的路径是 `/umamusume/skill_set/index`，请求、响应各 12 条。没有观察到购技接口 `/umamusume/single_mode_pioneer/gain_skills`。本地协议 DTO 定义该接口包含 `gain_skill_info_array`、`current_turn`、`exec_auto_play_plan_id`、`skill_set_deck_id`；DTO 证明协议形状，不证明价格或扣点算法。

Pioneer 的 25,793 个带 `chara_info` 的观测同时带有 `skill_array`、`skill_tips_array`、`skill_point`、`disable_skill_id_array`、`skill_upgrade_info_array`。这些字段应分别保留，不能将提示、已学技能、禁用技能和进化进度合并。

按同一 run 的源行顺序比较相邻可见角色状态，观察到 40 个不同已学技能 ID、223 次技能列表变化：

| 变化 | 次数 |
| --- | ---: |
| 既有技能等级上升 1 | 217 |
| 添加技能 | 3 |
| 移除技能 | 3 |
| 变化后响应为 `check_event` | 219 |
| 变化后响应为 `exec_command` / `race_end` / `shima_training_exec` | 2 / 1 / 1 |

223 次变化中，218 次技能 Pt 不变，5 次增加，没有一次技能 Pt 下降。相邻观测不是已证明完整的单步转移；尤其非 `check_event` 的变化可能跨过缺失响应，不能按后一 endpoint 直接归因。它们不是购技价格样本。

### 技能可以被事件移除

以下三组对应事件添加后再移除同一组三技能 `204591`、`204601`、`204611`，六次变化均未改变技能 Pt：

| 包库 | 添加前 → 后行 ID | 移除前 → 后行 ID |
| --- | --- | --- |
| packets-0004 | 14295 → 14297 | 14437 → 14439 |
| packets-0004 | 17329 → 17331 | 17475 → 17477 |
| packets-0004 | 28789 → 28791 | 28947 → 28949 |

添加前待处理事件为 `event_id=15373`、`story_id=501141117`，回合 29；移除前为 `15381`、`501141204`，回合 44。master 将两故事关联到 `card_chara_id=1141`。因此技能状态不能使用只增集合，技能数也不能作为累计购技次数。

这三组记录没有证明事件所有触发条件、分支或概率，也未验证其他卡组和状态。不能据此把“回合 29 自动添加、回合 44 自动移除”写成通用规则。

### 技能等级与进化是不同证据

等级变化包括 51 次 `1→2`、74 次 `2→3`、68 次 `3→4`、24 次 `4→5`。例如 `packets-0002` 的 `688→690` 在待处理事件 `3007/story501060107` 前后，将技能 `10601` 从等级 1 升为 2，Pt 不变。该记录可以作为已观察事件结果，不能推出触发所需粉丝、好感或概率条件。

`skill_upgrade_info_array` 另有 `condition_id`、`total_count`、`current_count`。本轮出现 14 个不同条件 ID，其中包含 Pioneer 六个进化目标对应的 12 个条件。条件计数可核对，但没有实际购买或执行进化的请求证据。

## master 可直接提供的内容

| 表与实际行数 | 可读取的事实 | 不能仅由该表认定的规则 |
| --- | --- | --- |
| `skill_data`：2,223 | 技能 ID、rarity、group_id、group_rate、评价值、条件文本、能力类型与参数等原始定义 | 购技可用性、提示折扣、扣点顺序；技能运行时条件表达式的完整解释 |
| `single_mode_skill_need_point`：1,195 | 每个条目的 `need_skill_point`、`status_type/value`、`solvable_type` | 提示和状态修正如何叠加、取整、下位技能抵扣、批量购买顺序；例如 ID 200011 的表值为 110，不等于任意状态都实际扣 110 |
| `card_data` + `available_skill_set`：274 + 1,916 | 角色卡关联的技能集合、技能 ID 与 `need_rank` | 何时将条目暴露为可学技能、进化资格与其他条件的组合 |
| `support_card_data` + `skill_set`：566 + 6,164 | 支援卡关联的技能组和条目中的技能等级 | 提示随机抽取概率、重复提示处理、卡片独特效果对结果的修正 |
| `single_mode_hint_gain`：5,216 | 支援卡、hint/group、类型、两个值、condition_set、priority | `hint_gain_type` 的执行语义、同组条目组合和抽样分布；实际类型 0 有 4,029 行，类型 1 有 1,187 行 |
| `skill_upgrade_description`：636；`skill_upgrade_condition`：1,897 | 进化目标与角色/等级关联，condition ID、num/sub_num、timing/count 类型 | `num/sub_num` 的布尔组合及何时更新/重置计数，进化执行时替换与互斥逻辑 |
| `skill_upgrade_speciality`：53 | 剧本、基础技能、进化技能的映射；Pioneer 有 6 条 | 仅凭映射执行进化或断定所有目标可同时选择 |
| `skill_exp`：10；`skill_level_value`：331 | level/scale、ability_type/level/系数原始数据 | 育成事件升级的完整触发条件；这些表不替代事件调度 |

Pioneer 的六条基础技能 → 进化技能映射为：`203651→411011`、`203661→411021`、`202721→411031`、`200511→411041`、`202101→411051`、`200721→411061`。

`skill_up_scenario_condition` 全表 12 行，但 `scenario_id=11` 为零行；Pioneer 的上述 12 个条件实际位于 `skill_upgrade_condition`。查错表会错误判断 Pioneer 没有进化条件。

`text_data` 的 category 290 还提供条件描述。例如 `41101101` 描述持有至少两个中距离技能，`41101102` 描述至少两个长距离技能，`41101103` 描述最终评价会以大好评完成；捕获中前两者 `total_count=2`，第三个为 `1`。这支持 ID/描述/目标计数对应关系，不单独证明条件之间的 AND/OR 组合和更新时机。

`single_mode_scenario` 的 Pioneer 行包含 `gimmick_discount_rate=5000`、`skill_upgrade_num=2`、`max_skill_point=9999`。本轮只记录原值，不把单位、应用对象和触发时机从字段名推导成结算公式。

## 事件表不是完整效果表

`single_mode_story_data` 有 20,694 行，包含角色/支援卡归属、story/short_story、展示与比赛事件标记等；没有完整的“选择 → 属性/Pt/技能/状态增减及概率”列。

相关表可提供部分结构：`single_mode_story_condition_set` 487 行、`single_mode_story_root` 36 行、`single_mode_select_skip` 9 行、`single_mode_conclusion_set` 1,809 行。它们不能代替完整事件触发器和结果分布。`single_mode_event_choice_reward` 61 行仅有 `disp_type` 和 `effect_value_type_0..2`；`single_mode_event_cr_priority` 84 行提供显示/条件类型优先级，不能当每个事件的数值奖励表。

本轮包中观察到 2,179 个不同 `story_id`，514 个故事曾展示多个 choice。785 个观察到的 story ID 不在 `single_mode_story_data`，但这 785 个全部有本地 timeline 资源；因此该 master 表也不能单独用作完整事件资源目录。

20,537 个带 `gain_parameter_info` 的观测中，该对象只出现三个字段：`gain_partner_support_effect_array`、`effected_command_id_array`、`is_view_training_bonus_turn_log`。本轮不能从这个对象直接取得通用属性、Pt 和技能变化表，仍需按完整上下文比较前后状态，并处理上限截断、事件链和缺包。

本地 `UmamusumeDeserializeDB5/Generator/Events.cs` 的 `Generate` 读取 kamigame JSON，并执行名称、归属和效果文本纠正；`SuccessEvent.cs` 还有显式人工事件条目。它们是第三方派生资料，不是从 master 直接解出的游戏完整效果数据，本轮没有执行这些网络生成器或把其公式当作实证。

## 本地事件资源的实际核验

解密后的 meta 索引含 54,496 条 `story/data/` 资源，本次逐条路径检查全部已落盘。其中 22,195 条为 `storytimeline`，22,230 条为 `resourcelist`，其余为其他故事资源。路径存在检查不等于全库内容解析或完整性校验。捕获中的 2,179 个 story ID 全部能匹配 timeline。

针对已观察到技能添加/移除的两个故事，本轮做了以下实际操作：

1. 将选定 bundle 复制到工作区，仅对副本执行已有 `UmaDecryptor` 的 `decrypt-dat`；输入为含两个资源哈希文件的目录，输出为新目录，meta 也使用工作区副本。
2. 工具版本为 `1.0.0+2163be916e9599b2d225e10557f6432bea135e8e`，报告 2 成功、0 失败。两个输出 SHA 均发生变化；原游戏文件、未处理副本和 meta 副本 SHA 均保持一致。
3. 使用本机已有 AssetStudio DLL 对处理后的副本读取全部对象类型树，没有沿用文本提取器的 Title/Text/Choice 白名单。`501141117` 读出 1,037 个对象，`501141204` 读出 510 个，合计 1,547 个，缺少类型树的对象数为 0。

初次直接读取 `501141117` 副本曾出现 `Lz4 decompression error, write -1 bytes but expected 131072 bytes`。该错误本身没有被解释为某个确定原因；后续转换成功、字节改变、转换后可完整读取分别记录为独立事实。

两个完整类型树包含 `StoryTimelineData`、文本、角色动作、镜头、声音、屏幕效果、Debug 等对象。额外检查 `StoryTimelineObjectControlClipData`，其字段是粒子、位置、图层、音量等演出控制；不能因名字带 Effect/Value/Parameter 就解释成育成效果。三个实际增删的技能 ID `204591`、`204601`、`204611` 在两份全部 dump 中均未出现。

本轮未在这两份 bundle 中找到能声明对应技能增删、Pt 数值、触发条件或概率的完整规则表。这个结论只适用于这两个已检查样本；不能推断其他资源或引用的运行时代码均不包含规则。

派生证据位于：

- `artifacts/skill-evidence/resource-samples/manifest.json`：选定输入的资源名、ID、大小、SHA。
- `artifacts/skill-evidence/resource-probe-summary.json`：工具 SHA、转换前后 SHA、源未变检查和对象数量。
- `artifacts/skill-evidence/type-tree-inventory.json`：全部对象、脚本类型和字段名。
- `artifacts/skill-evidence/type-trees.jsonl`：全部对象 dump。
- `artifacts/skill-evidence/inspect-copied-resources.ps1`：已有 DLL 读取副本的复核脚本；已有输出会拒绝重复创建。

资源副本与完整 dump 仅用于本地研究，不作为项目发布内容。本轮没有安装新工具、批量解密整个资源库、操作游戏或修改游戏源文件。

## 可实现与缺证据边界

可以直接使用的部分是带来源指纹的 master 查询、提示/已学/禁用/进化进度的独立状态读取、已捕获动作与状态变化回放，以及上述事件结果的局部事实校验。这些能力不需要猜测规则。

完整执行规则还缺少：

- 购技请求与前后状态配对，覆盖提示等级、状态修正、下位/上位关系、批量顺序、舍入、技能移除或替换及不足 Pt 等合法性边界。
- 进化操作的实际请求/响应及资格变化过程，区分已学技能等级上升、技能进化和购买上位技能。
- 提示抽取和重复提示的条件分布、支援卡效果修正与触发顺序。
- 各角色/支援卡事件的前置条件、调度、互斥、连续事件进度、选择分支、随机结果、状态/技能增删以及上限截断。三个重复观察结果不能覆盖这些条件。

没有把 `check_event` 中的技能变化冒充购买，也没有把“材料未见”写成“游戏不支持”。本轮不添加仅能重放几个故事 ID 的伪通用事件执行器；获得更多控制变量样本或可解析规则源后再实现相应规则。
