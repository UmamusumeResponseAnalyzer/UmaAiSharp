# 终局观测边界

`evidence terminal --input <标准化证据目录> --output <新目录>` 核对已配对的 Pioneer `finish` 响应。它按同包 `trained_chara_id` 唯一选择殿堂对象，检查五属性、上限与技能数组的一致性，输出所选对象的属性、技能 Pt、评分、因子和剧本字段。

`pioneer_complete` 只结束剧本阶段。研究材料中它发生在 turn 72→73，后续仍有 URA、角色和支援结局、因子选择及 `finish`。事件队列为空、一个固定回合或一次 `pioneer_complete` 均不能替代终局判定。

终局选择不能使用殿堂数组首项。旧研究的 78 个 `finish` 都通过精确身份匹配；数组包含 7–44 个对象。因子跨协议步骤传递存在顺序变化，后续因子核验应比较带重复项的内容集合，不要求原数组顺序。当前命令只核验同包字段，没有将没有育成身份的 `factor_select` 响应自动归入最近一局。

`total_skill_pt` 与剩余 `skill_point` 分开保留，不假定两者相等；任何一个都不直接等于“从当前决策起未来获得的 Pt”。实际溢出依赖完整路径，不能从终值补算。结果未自动送入目标评价器，避免编造这些缺失指标。

核验所需的身份、状态、五属性与上限、技能数组、Pt 或评分字段缺失或不一致均单列问题。因子与剧本字段只投影、不核验生成规则；其缺失、null 与存在分别标记，不当成空数组。命令返回 3：局部观测匹配不证明终局奖励、因子生成或自主整局模拟已经实现。原始研究与窗口证据位于 `artifacts/research/blockers/state-machine-terminal/REPORT.md`、`verification-04/`。

## 角色结局执行入口

`PioneerStart.CharacterEnding` 从最后角色结局事件前态开始：回合78、角色状态3、阶段5、事件6、时机3，故事编号为`500000000 + chara_id * 1000 + 111`。执行选择0，五属性各增加原始量5、技能Pt增加20，使用事件资源核处理1200与属性上限，随后返回`TerminalMetrics`。未来Pt从该起点起累计，未来溢出使用换算后的显示属性单位。

215个严格窗口覆盖38个角色；事件后资源与各自后续`finish`资源一致。脚本从事件前态生成这些结果，后包仅用于断言。与推导同源的材料保存为`tests/UmaAiSharp.Tests/Fixtures/pioneer-character-endings.json`；来源筛选、原始字段与哈希见`artifacts/implementation/turn-progression-01/character-ending-windows-03.json`。

Pioneer 高评价结局之后还有 Tucker 及角色结局奖励。规则资源 v6 从第 72 回合收尾推进到三轮 URA 决赛，再依次结算终盘奖励。Tucker 的两个主要奖励档位按拟合组的频数抽样。因子选择与账号保存排除在终局指标之外。[自主整局](pioneer-mcp.md#规则资源-v6-自主整局)

正式回放的终局检查要求动作脚本及所有条件结果消费完毕；存在遗留条件时返回`ScriptMismatch/OutcomeIdentityMismatch`。原始响应导入、终局执行及推荐例见[MCP 执行](pioneer-mcp.md)。

## 高评价结局数值候选

`PioneerEndingSettlement.ApplyCandidate` 只计算已明确选择的 `211040 / 400011027` 事件：turn 78、阶段 5、选择 0、五次评价均为 2，并且此前没有 `21036 / 2` Hint。输出属性奖励、技能 Pt +540 和该 Hint 等级 1。它不决定事件何时出现、不处理已有 Hint 的合并、不生成账号奖励或因子，也不会把此次事件当作完整终局。

每项属性的原始奖励为 55：1200 以下部分按 1:1，超过部分整除 2；超过部分为正时至少得到 1 点，随后截至明确上限。这个最少 1 点分支目前只有一个临界观测，不能推广成所有事件的通用舍入规则。[プレ氏的无人岛研究](https://playershi.com/2025/06/26/designyourisland/)提供 +55、+540 和结局 Hint 的外部候选，没有提供完整舍入顺序。

2026-11-08 扩大材料复核：216 个相关响应中，215 个具有匹配队首、配对请求及保守捕获邻接；另 1 个排除。215 个窗口的五属性、Pt、未变化上限和 Hint 均符合候选；全部此前无该 Hint。纯向下分段仅匹配 214 个，全局向上减半仅匹配 1 个。新数据未增加最少 1 点分支的独立临界样本，因此这是明确范围内的数值候选，不是唯一服务端实现的认证。

复算脚本和来源哈希：`artifacts/research/integration-20261108/ending_probe.py`、`endings-04/summary.json`。加强源、端点、配对成员及额外请求筛选后仍为 215 个窗口；体力与体力上限也全部未变。产品测试夹具 `tests/UmaAiSharp.Tests/Fixtures/pioneer-endings.json` 从同指纹的 `endings-02` 提取，只保留 215 个窗口的前值、上限、后值与来源哈希，删除账户和育成身份。它用于验证 C# 翻译，与推导材料同源，不算留出的游戏验收集。
