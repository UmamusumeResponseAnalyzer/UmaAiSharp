# 共通育成机制

`UmaAiSharp.Base` 引用 Core。调用方提供 master 行、已观测前态、已选动作或已确定的奖励。一个剧本可以只复用其中一项，例如只用 `TrainingFailureRate.Calculate` 计算面板失败率，或只用 `HintRewardPool.Evaluate` 核对领奖前的白技能候选。

| 命名空间 | Base 接受的输入和结算范围 | Pioneer 保留的决定 |
| --- | --- | --- |
| `Base.Training` | 五级训练的标准人数和干劲倍率、给定支援效果与羁绊的友情判定、六卡编成效果、成功训练支援羁绊、给定 master 系数及状态偏移的面板失败率、已显示预览的资源结算 | 训练基础表、状态 ID 对偏移的映射、设施与评审加成、人数分布、普通／合宿／岛训倍率叠加和量化、失败分支与升级时机 |
| `Base.Training`、`Base.Events` | 体力上限与恢复、片头痛和正向思考对干劲的作用、已声明奖励的属性／体力／干劲／技能 Pt 分量、事件所属支援的加成 | 故事和选项身份、结果抽取、人物进度、Tucker 与建设结算、事件顺序 |
| `Base.Hints` | 用支援技能、已有 Hint 及已学白／金技能筛选白技能候选，结算已选定的一组属性奖励 | 何时出 Hint、由谁领取、属性和技能的抽取权重、岛训练的多次领取 |
| `Base.Inheritance` | 亲本及祖辈六槽映射、蓝因子属性与上限、红因子适性、直接父母绿 Hint、初始支援加成与羁绊、相性 | 开局阶段推进、第 31／55 回合因子触发率和效果档位、满 Hint 转换、继承候选取舍 |
| `Base.Races` | 按调用方给出的剧本 ID 和适用组，核对育成卡、角色绑定路线及本局已选目标 ID，保留顺序和互斥候选的身份 | 分支选择、赛程变化、比赛结果、发展 Pt、URA 决赛与收尾奖励 |
| `Base.Scoring` | 依据五维、脚质和距离适性、实际已持有的技能和等级、技能 master 计算评价点候选 | 未来技能 Pt 与 Hint 的价值、可购技能估算、目标与行动排序 |

### 训练与结算

人员倍率为每位参与者加 5%，普通卡在得意训练且羁绊达到 80 时具有友情资格。公开模拟器的训练实现和日服 master 支持这些分量。Pioneer 的普通训练与岛训练对人数的计权不同，体耗叠加和中间舍入也有单独证据。当前 master 的速度普通训练五级失败系数依次为 520、524、528、532、536，合宿速度指令 3601 为 536。用实包夹具的体力、状态、减免和系数独立复算，291 张面板的 1,455 个失败率均与显示值一致，其中 200 个非零。[umasim 训练实现](https://github.com/mee1080/umasim/blob/75bebbeb4316cac4049fe47981231b9088d2627e/core/src/commonMain/kotlin/io/github/mee1080/umasim/simulation2/Calculator.kt#L169-L225) · [训练公式证据](training-formula-evidence.md) · [训练羁绊证据](training-bond-effects.md)

固有 120 按完整编成统计五种得意训练，友人和团队卡计入技能 Pt 项，每项最多加 2。当前日服 master 中的支援 30187、30264 均使用羁绊 80 的同一参数组。Pioneer.Game 读取卡面与解锁等级，Base 将编成计数绑定到羁绊条件。固有 121 的全编成加成则按持有卡是否参加所选训练结算。[umasim 支援固有实现](https://github.com/mee1080/umasim/blob/75bebbeb4316cac4049fe47981231b9088d2627e/core/src/commonMain/kotlin/io/github/mee1080/umasim/data/SupportCardSpecialUnique.kt) · [固有 121 的 master 投影](../tests/UmaAiSharp.Tests/Fixtures/training-bond-support.json)

正向属性奖励在 1200 以下等量增长，超出的部分折半取整，有剩余时至少增加 1 点，然后应用当前上限。已声明事件夹具覆盖 1,789 个窗口，包括耐力 1199、声明 +10、后态 1204。事件加成取事件所属卡，体力恢复使用效果 25，属性和技能 Pt 使用效果 26。途中继承原算法在「1199 加 2」这类输入上采用不同的舍入，故保留在途中继承模型。[玩家的跨剧本数值说明](https://umamusumeschool.com/status_aptitude/) · [支援效果对照](https://gamewith.jp/uma-musume/article/show/274990) · [umasim 友人事件](https://github.com/mee1080/umasim/blob/c97dede4efd71af9606cac0342f5f03944a41e74/core/src/commonMain/kotlin/io/github/mee1080/umasim/simulation2/SimulationStateUpdater.kt#L387-L411) · [固定奖励夹具](../tests/UmaAiSharp.Tests/Fixtures/pioneer-event-resources.json)

### Hint、继承与路线

玩家对已学技能的实测支持将其从后续支援 Hint 候选中排除。本次以日服 master 重读 2,147 个已配对的领奖窗口，1,794 次技能 Hint 和 353 次属性奖励均匹配。16 个空池全部取得属性奖励。相同观测可能来自不同抽选过程，因此 Base 只提供有效池及条件奖励。现有资料只覆盖白技能候选和同组已学白／金技能。[玩家的 50 次受控试验](https://owadablog.com/tips-crush/) · [Hint 配对与筛选口径](hint-pool-evidence.md)

玩家表列出开局蓝因子每星档的属性 5／12／21、上限 4／9／16，以及红因子总星升档、开局适性最高 A。已有 221 局开局审计逐组匹配五属性、上限、适性、绿 Hint、初始技能、羁绊和客户端相性。六卡共 1,326 个位置，亲本扩展数组均为空。其它才能等级、非空扩展的实际开局效果和途中抽取概率各有独立的验证边界。共同 G1 在本项目按 master 的相性组去重，每个交集组加 3。[玩家因子表](https://wikiwiki.jp/sppenpen/因子の出し方) · [WinSaddleAnalyzer 相性计算](https://github.com/URA-Plugins/WinSaddleAnalyzer/blob/919e9761da6f288eb77c9bdc2f33a5c6edb4efc1/FriendSearch.cs#L120-L175) · [开局原包审计及版本说明](initial-state-evidence.md)

四份 Pioneer 开局包的 47 个已选目标均映射到当前 master。测试也用剧本 10 的适用组验证同一角色路线可接受目标 1523、排除组 701 的目标 1530。剧本 4、6、13 另有 `chara_id=0` 的专用路线，需要各自的分派规则。[路线映射证据](race-route-evidence.md) · [跨剧本组测试](../tests/UmaAiSharp.Tests/RaceRouteTests.cs)

### 评价点

从每个本机抓包库最后一份 `/load/index` 响应读取殿堂角色，按剧本、完整评分输入和实得总分去重。用同一份日服 `skill_data` 分别运行基线与 Base 的 C# 评分器，两个版本均匹配 745／745 组，涵盖剧本 1 至 15，其中 Pioneer 245 组。样本含 819 个技能 ID。五维各自的最大观测值为 2321／1915／1829／1744／1803。该口径核对组合总分，未逐项观测技能和属性各自的贡献。[公开评价点计算器](https://umax-tools.com/scoring.html) · [评分分段与历史样本](race-route-evidence.md)

本次面板、Hint、路线和评分对照所用日服 master 的 SHA-256 为 `95f14e3b83d469d0a22abecdcbc21d194db59f5986e01c195130f9424269e234`。221 局开局计数沿用已有审计，原报告记录了当时的 master 和输入指纹。

### 迁移对照

基线为 `c6a30ea`。先用基线的 `pioneer_import_capture` 导入真实回合，两份 Release MCP 进程随后读取完全相同的输入、候选和规则文件。规则 SHA-256 为 `3a505733370e70e1bee8aff4cb138fc452ffcec5a413e4988fe16e116c73816e`。两种模型分别与自己的基线比较，报告仅排除编译耗时、执行耗时和进程分配字节数。

- `pioneer_recommend` 从回合 1 的包 196 开始，目标为 `examples/power-goal.json`，种子 12345，2 个 worker，每个候选首批 8 条。两种模型的 7 个候选都自动加样至每项 4,096 条。完整排序、每条终局、随机流末态及建议一致，首选均为力量训练 102。
- `pioneer_replay` 从包 58275 重新导入回合 30 的面板，使用仓库普通训练失败示例的条件结果和动作，种子 12345。两种模型均消费 2 个动作，在位置 2 到达 `NextTurnEventsUnknown`（1005）。资源、状态、随机流和停止位置均与基线一致。[条件回放脚本](../examples/pioneer/ordinary-failure-replay.json)

直接按类型名引用已迁移机制的 .NET 工程需更新为 `UmaAiSharp.Base.*` 并重新编译。PioneerAiPlugin 的打包脚本同时打入 Core、Base、Pioneer 和 Pioneer.Game 四个运行时程序集。
