# 共通育成机制

`UmaAiSharp.Base` 引用 Core。调用方提供 master 行、已观测前态、已选动作或已确定的奖励。一个剧本可以只复用其中一项，例如只用 `TrainingFailureRate.Calculate` 计算面板失败率，或只用 `HintRewardPool.Evaluate` 核对领奖前的白技能候选。

| 命名空间 | Base 接受的输入和结算范围 | Pioneer 保留的决定 |
| --- | --- | --- |
| `Base.Training` | 五级训练的标准人数和干劲倍率、给定支援效果与羁绊的友情判定、六卡编成效果、成功训练支援羁绊、给定 master 系数及状态偏移的面板失败率、已显示预览的资源结算 | 训练基础表、状态 ID 对偏移的映射、设施与评审加成、人数分布、普通／合宿／岛训倍率叠加和量化、失败分支与升级时机 |
| `Base.Training`、`Base.Events` | 体力上限与恢复、片头痛和正向思考对干劲的作用、已声明奖励的属性／体力／干劲／技能 Pt 分量、事件所属支援的加成 | 故事和选项身份、结果抽取、人物进度、Tucker 与建设结算、事件顺序 |
| `Base.Hints` | 用支援技能、已有 Hint 及已学白／金技能筛选白技能候选，结算已选定的一组属性奖励 | 何时出 Hint、由谁领取、属性和技能的抽取权重、岛训练的多次领取 |
| `Base.Inheritance` | 六槽谱系、开局因子与相性、初始支援加成与羁绊；途中按 Uma Plan 生成一组或两组候选，按 master 和校准参数抽取因子、结算属性、上限、适性、Hint 和技能 Pt，按同一评分选择模拟或实包候选 | 回合推进、随机流分配、包字段转换与轨迹状态记账 |
| `Base.Races` | 识别已选目标，按角色条件生成和查询分支赛程，合并预约、处理禁赛，计算分支事件与强敌目标奖励 | 提供实包、master 赛历及当前状态，执行赛事、普通赛奖、发展 Pt 和 URA 决赛 |
| `Base.Scoring` | 依据五维、脚质和距离适性、实际已持有的技能和等级、技能 master 计算评价点候选 | 未来技能 Pt 与 Hint 的价值、可购技能估算、目标与行动排序 |

### 训练与结算

人员倍率为每位参与者加 5%，普通卡在得意训练且羁绊达到 80 时具有友情资格。公开模拟器的训练实现和日服 master 支持这些分量。Pioneer 的普通训练与岛训练对人数的计权不同，体耗叠加和中间舍入也有单独证据。当前 master 的速度普通训练五级失败系数依次为 520、524、528、532、536，合宿速度指令 3601 为 536。用实包夹具的体力、状态、减免和系数独立复算，291 张面板的 1,455 个失败率均与显示值一致，其中 200 个非零。[umasim 训练实现](https://github.com/mee1080/umasim/blob/75bebbeb4316cac4049fe47981231b9088d2627e/core/src/commonMain/kotlin/io/github/mee1080/umasim/simulation2/Calculator.kt#L169-L225) · [训练公式证据](training-formula-evidence.md) · [训练羁绊证据](training-bond-effects.md)

固有 120 按完整编成统计五种得意训练，友人和团队卡计入技能 Pt 项，每项最多加 2。当前日服 master 中的支援 30187、30264 均使用羁绊 80 的同一参数组。Pioneer.Game 读取卡面与解锁等级，Base 将编成计数绑定到羁绊条件。固有 121 的全编成加成则按持有卡是否参加所选训练结算。[umasim 支援固有实现](https://github.com/mee1080/umasim/blob/75bebbeb4316cac4049fe47981231b9088d2627e/core/src/commonMain/kotlin/io/github/mee1080/umasim/data/SupportCardSpecialUnique.kt) · [固有 121 的 master 投影](../tests/UmaAiSharp.Tests/Fixtures/training-bond-support.json)

正向属性奖励在 1200 以下等量增长，超出的部分折半取整，有剩余时至少增加 1 点，然后应用当前上限。已声明事件夹具覆盖 1,789 个窗口，包括耐力 1199、声明 +10、后态 1204。事件加成取事件所属卡，体力恢复使用效果 25，属性和技能 Pt 使用效果 26。[玩家的跨剧本数值说明](https://umamusumeschool.com/status_aptitude/) · [支援效果对照](https://gamewith.jp/uma-musume/article/show/274990) · [umasim 友人事件](https://github.com/mee1080/umasim/blob/c97dede4efd71af9606cac0342f5f03944a41e74/core/src/commonMain/kotlin/io/github/mee1080/umasim/simulation2/SimulationStateUpdater.kt#L387-L411) · [固定奖励夹具](../tests/UmaAiSharp.Tests/Fixtures/pioneer-event-resources.json)

### Hint、继承与路线

玩家对已学技能的实测支持将其从后续支援 Hint 候选中排除。本次以日服 master 重读 2,147 个已配对的领奖窗口，1,794 次技能 Hint 和 353 次属性奖励均匹配。16 个空池全部取得属性奖励。相同观测可能来自不同抽选过程，因此 Base 只提供有效池及条件奖励。现有资料只覆盖白技能候选和同组已学白／金技能。[玩家的 50 次受控试验](https://owadablog.com/tips-crush/) · [Hint 配对与筛选口径](hint-pool-evidence.md)

玩家表列出开局蓝因子每星档的属性 5／12／21、上限 4／9／16，以及红因子总星升档、开局适性最高 A。已有 221 局开局审计逐组匹配五属性、上限、适性、绿 Hint、初始技能、羁绊和客户端相性。六卡共 1,326 个位置，亲本扩展数组均为空。其它才能等级、非空扩展的实际开局效果和途中抽取概率各有独立的验证边界。共同 G1 在本项目按 master 的相性组去重，每个交集组加 3。[玩家因子表](https://wikiwiki.jp/sppenpen/因子の出し方) · [WinSaddleAnalyzer 相性计算](https://github.com/URA-Plugins/WinSaddleAnalyzer/blob/919e9761da6f288eb77c9bdc2f33a5c6edb4efc1/FriendSearch.cs#L120-L175) · [开局原包审计及版本说明](initial-state-evidence.md)

经典年和资深年 4 月上半各有一次途中继承。开局时持有 Uma Plan，每次给出两组结果供选择，育成中途到期也保持两组。无会员时直接结算一组。五份本地包库中，开局明确无会员的 221 局共观察到 441 个继承事件，候选数组均为空；明确有会员的 5 局共观察到 6 个事件，每个均有两组候选。缺开局标记且无法从恢复包判明会员状态时，Pioneer 导入沿用有会员的假设。[官方 Uma Plan 说明](https://umamusume.jp/news/detail?id=3078)

模拟器按冻结谱系、相性及校准概率计算一次原始收益期望，供本次推荐的所有轨迹在第 31、55 回合共用。普通继承将触发概率、效果档概率和原始收益相乘后求和，附加因子按满足触发组门槛的概率计入。Uma Plan 按一组声明的五维原始增量之和加两倍技能 Pt 排序，同分取第一组。两组独立时，分数为 `s` 的整组收益按 `P(S < s) + P(S ≤ s)` 加权，组内的 Hint 与适性随胜者计入。实包已揭示的候选仍按包内实际属性及技能 Pt 比较，包 2697 两组得分为 172、243。

兑现时先加原始期望，再换成显示值并应用当时的属性上限。例如 +5 和 +40 各半时，单组期望为 +22.5，剩余原始空间为 10 点时到账 10 点。未解锁 Hint 保留解锁概率和解锁后的平均等级，已满级或已学技能按转换分布计入五维收益。小数收益按配对样本序号分配到整数状态。[继承校准生成器](../tools/UmaAiSharp.Pioneer.DataGenerator/fit_inheritance.py) · [候选结算及实包排序测试](../tests/UmaAiSharp.Tests/MidRunInheritanceTests.cs)

角色分支依据角色及当局的目标、赛果、干劲或事件选择。已实现成田白仁强敌路线、爱丽速子皋月赏后换赛、待兼福来的两次抽签、待兼诗歌剧禁赛及黄金旅程换赛和加赛。日服 master 中这些分支涉及的 23 个目标属于各自角色，路线的 `scenario_id=0`，目标的 `scenario_group_id=100`。待兼福来的候选及触发点另有玩家记录，权重来自实包估计。[角色路线核查](race-route-evidence.md) · [玩家对待兼福来两次抽签的记录](https://trap.jp/post/1367/)

解析实包赛程还需检查目标行适用的剧本组。四份 Pioneer 开局包的 47 个已选目标均映射到当前 master。测试也用剧本 10 的适用组验证同一角色路线可接受目标 1523、排除组 701 的目标 1530。剧本 4、6、13 另有 `chara_id=0` 的专用路线。[路线映射证据](race-route-evidence.md) · [跨剧本组测试](../tests/UmaAiSharp.Tests/RaceRouteTests.cs)

用 104 个实包已选路线状态分别编译包含当前回合、仅包含未来回合的赛程。两种导入边界共得到 314 份分支赛历，逐项核对 24,492 个回合，目标 ID、比赛 ID、空位和禁赛均与基线一致。预约与必经目标同回合时，必经目标优先。[赛历测试](../tests/UmaAiSharp.Tests/CharacterRaceScheduleTests.cs)

### 评价点

从每个本机抓包库最后一份 `/load/index` 响应读取殿堂角色，按剧本、完整评分输入和实得总分去重。用同一份日服 `skill_data` 分别运行基线与 Base 的 C# 评分器，两个版本均匹配 745／745 组，涵盖剧本 1 至 15，其中 Pioneer 245 组。样本含 819 个技能 ID。五维各自的最大观测值为 2321／1915／1829／1744／1803。该口径核对组合总分，未逐项观测技能和属性各自的贡献。[公开评价点计算器](https://umax-tools.com/scoring.html) · [评分分段与历史样本](race-route-evidence.md)

本次面板、Hint、路线和评分对照所用日服 master 的 SHA-256 为 `95f14e3b83d469d0a22abecdcbc21d194db59f5986e01c195130f9424269e234`。221 局开局计数沿用已有审计，原报告记录了当时的 master 和输入指纹。

### 迁移对照

基线为 `c6a30ea`。先用基线的 `pioneer_import_capture` 导入真实回合，两份 Release MCP 进程随后读取完全相同的输入、候选和规则文件。规则 SHA-256 为 `3a505733370e70e1bee8aff4cb138fc452ffcec5a413e4988fe16e116c73816e`。两种模型分别与自己的基线比较，报告仅排除编译耗时、执行耗时和进程分配字节数。

- `pioneer_recommend` 从回合 1 的包 196 开始，目标为 `examples/power-goal.json`，种子 12345，2 个 worker，每个候选首批 8 条。两种模型的 7 个候选都自动加样至每项 4,096 条。完整排序、每条终局、随机流末态及建议一致，首选均为力量训练 102。
- `pioneer_replay` 从包 58275 重新导入回合 30 的面板，使用仓库普通训练失败示例的条件结果和动作，种子 12345。两种模型均消费 2 个动作，在位置 2 到达 `NextTurnEventsUnknown`（1005）。资源、状态、随机流和停止位置均与基线一致。[条件回放脚本](../examples/pioneer/ordinary-failure-replay.json)
- `pioneer_run` 对同一份包 196 导入结果分别指定有、无 Uma Plan。另取五名分支角色的真实开局面板，包号依次为成田白仁 28238、爱丽速子 3440、待兼福来 12692、待兼诗歌剧 4876、黄金旅程 1822。七组输入各用 `precise`、种子 12345 推进到第 78 回合，均完成两次继承。两种会员输入和其中四名角色的完整输出与各自基线一致。爱丽速子的普通路线补入第 32 回合事件奖励后，包 3440 的终局智力由 1220 变为 1223，其余四维相同。分支单测另覆盖低干劲换赛、强敌资格边界和福来两次抽签的全部候选。[角色分支测试](../tests/UmaAiSharp.Tests/RaceRouteTests.cs)

直接按类型名引用已迁移机制的 .NET 工程需更新为 `UmaAiSharp.Base.*` 并重新编译。PioneerAiPlugin 的打包脚本同时打入 Core、Base、Pioneer 和 Pioneer.Game 四个运行时程序集。
