# Pioneer MCP 执行与推荐

导入、执行及推荐均显式提供同一份 `rulesPath`。规则资源的 SHA-256 绑定在导入输入的 `rulesIdentity` 中。更换资源后从实包重新导入。连接方式见 [MCP 接入](mcp.md)。

`pioneer_run`、`pioneer_replay`、`pioneer_batch` 和 `pioneer_recommend` 均接受 `model`。默认 `precise`，在普通、合宿和岛训练中使用有理数乘积及逐级量化。`fast` 使用浮点乘积和末端整数截断。两种模型使用相同的动作、失败率、属性上限和终局评分。正常执行报告的 `provenance.rulesIdentity` 包含资源 SHA-256 与模型名。实包数值回放使用 `precise`。

## 自主整局

`pioneer_run` 和自主 `pioneer_batch` 读取 `goalPath`。策略根据每步模拟出的状态选择行动。`pioneer_recommend` 先比较所有合法根候选，再调用同一策略继续。自主输入的 `script` 和 `outcomes` 都为空。

行动列表未列出的休息指令视为不可用。预生成规则从 master 读取比赛专属状态，这些状态不影响训练失败率、休息和岛训练即时结算。若所需结算规则缺失导致轨迹停止，报告保留停止码及可用的具体原因。

事件决策的 `key` 使用 `((long)turn << 32) | (uint)storyId`。抓包前缀的 `observedStoryHistory` 保留故事、回合、阶段、角色、支援身份和选项。连续事件进度取 `show_progress_1`。实包确认按协议 `eventId` 配对，`import.json` 记录未能唯一配对的确认数量。

自主休息按 Pioneer 抓包抽取 50 或 70 点体力，合宿休息恢复 40 并提升一档干劲。轻败采用首选项，扣所选训练的主属性 5 点及一档干劲。智力训练失败只回 5 点体力。其它训练失败后以同一面板失败率判重败，重败沿用经验规则。成功训练的 Hint 领取加 5 羁绊，有爱娇时加 7。结算羁绊后，在未达 Lv5、未购同组白／金技能的有效奖池中抽取一个技能或一组属性。固有 type 33 在技能分支抽取额外不同技能。技巧设施的 Hint 发生率、必出和全触发按对应合宿／岛训位置应用。

当前面板按指令启用状态、友人外出进度、比赛列表、出道状态和粉丝门槛生成训练、岛训练、休息、治病、出行和比赛候选。未来的岛训练分配取已建设施对应的普通训练人员，海之家在剩余人员中按友人、普通支援、先锋队员优先级补足。普通和岛训练中与 Tucker 同训的普通支援获得下一回合临时得意率 +60。岛训练扣券并结算全编成羁绊、发展 Pt 与建设。合宿休息与外出合并为一个指令。

普通出行的地点分布使用法米通 121 次实测作为先验。卡拉 OK、河边、神社小吉、中吉和大吉分别为 44／34／25／13／5 次。卡拉 OK 升两档干劲，其余升一档并分别回 10／10／20／30 点体力。Pioneer 实包可核对两次出行的直接收益和随后角色事件，地点概率、角色事件触发次序及神社治病仍缺本剧本样本。 [出行试验](https://www.famitsu.com/news/202106/01222293.html)

比赛按第一名结算。续步读取已选路线、现行预约和 Pioneer 赛历，并在第 74、76、78 回合结算 URA。普通自选赛和训练参加整局比较。当前回合采用实包比赛列表，未来按月份、育成年级、出道状态、可见禁赛和粉丝门槛生成，同等级续步保留粉丝最多的可报名项以及能解锁目标赛专属奖励的比赛。自选赛按等级估计单属性、技能 Pt 和体力成本。粉丝使用 master 基值、支援加成、表现倍率及可选赛的 500 粉丝分支。路线粉丝门槛和累计出赛目标到期结算。达标和目标赛获胜均增加理事长羁绊 4。注目株使理事长与记者每次羁绊奖励额外加 2。路线失败的样本在终局提供 `failedCareerGoalId`，计入达标率分母和执行报告的 `careerFailures`。

目标赛程和训练日历共用轨迹上的路线。爱丽速子在第 32 回合的「Report『透明な視線』」取得智力 +5。皋月赏参赛干劲普通或更低时，确认该故事会将目标从日本德比换为 NHK 英里杯。待兼福来出道与宝塚各抽一场目标赛，未揭示时使用小样本先验，提示列出目标编号及估计权重。黄金旅程的固定换赛和加赛、待兼诗歌剧取消日本杯并禁赛的回合由对应的游戏目标编号编译。累计出赛目标按日期、级别和逐场可达粉丝计算剩余报名机会。连战后的自选赛在胜利奖励与采访后抽取疲劳，按 Pioneer 实包的三连与四连以上分别估计干劲、属性和肌荒分支。

续步按生涯赛程逐场检查必经赛的报名门槛。可参加的必经赛按最低首名粉丝收益计入，再检查当回合结束时的生涯粉丝目标和之后的比赛门槛。自选赛只按首个仍未覆盖的粉丝缺口获得补粉加分。

成功同训时，理事长和记者先各加 2 羁绊，带「注目株」时各加 4。评价事件再给理事长 5／7，记者 7／9。记者按确认前的羁绊给所选训练的主属性，四档各为 2／3／4／5，带「注目株」各加 2。理事长评价给技能 Pt。普通训练另按实包频率抽取自主加练，比较体力 −5、训练主属性 +5、理事长羁绊 +5 的上项与回体 5 的下项。自选赛记者采访同时给予 500 粉丝、15 羁绊和一档干劲。

记者收尾的羁绊分界为 40／70／90。普通四档的五维单项和技能 Pt 分别为 (0, 5)、(0, 10)、(3, 10)、(5, 20)。带「注目株」时为 (0, 10)、(0, 20)、(9, 30)、(11, 42)。后两档各有一个实包用于估计。该奖励由羁绊及角色状态决定。自主整局导入须包含记者羁绊。

海之家的支援出场率沿用对应等级的拟合行。缺样本的等级以实包里普通支援的 `0 → +10` 缺席赔率变化外推，PJ 人员保持相同模式下的观测分布。

成功训练按里程碑结算升级，跨建设阈值时应用设施及发券结算。每期规划进入建设决策，续步按设施在剩余回合可提供的收益比较合法有序方案。经典合宿期间触发的建设在第 41 回合结算。自主模式在发券时按当前阶段一张的持有上限确定结算后券数，即使导入的合宿面板隐藏了之前的券数。第 65 回合赠两券，岛训练券于剧本结束时失效。五期评审更新资源、训练和人员分布。合宿模式及第 73、75、77 回合的终盘训练按回合切换。

固定事件按回合和行动结算。普通剧情沿用 UmaAi-rust 的新回合单次抽签、支援阶段计数和友人解锁优先级。支援阶段从已见故事的 `show_progress_1` 统计。Tucker 解锁和「道を指し、導く者」均比较实包声明的两项奖励。第三段外出比较基础回体 50 和五维各 +8、技能 Pt +10，并应用卡片事件加成。首期建设开放至最终评审的外出奖励含固定 3 技能 Pt，并比较发展 Pt +80 对评审及补券的价值与覆盖现有券的机会成本。重复同训按普通／岛训分开抽取，耐力奖励按卡片效果计算。第 25 回合和 SSR 第二、第五段手动外出授予相应 Hint。第 31、55 回合按共通继承规则，用冻结谱系、相性、已购技能及当时的 Hint 等级抽取整组因子效果。开局无 Uma Plan 时直接结算一组，有 Uma Plan 时按 Base 的默认评分挑一组，即五维换算增量之和加两倍技能 Pt 增量。抓包无法判明开局会员状态时按有会员处理。[继承规则及实包对照](base-mechanics.md#hint继承与路线)

第 25 回合的「新年の抱負」比较单项属性 +25、回体 20 和技能 Pt +20。首项属性按角色确定，本次日服 master 的 136 个育成角色有 135 个已确定首项属性，其中 39 个有实包记录。デアリングタクト（1075）的「お正月のセールへ」在日文攻略中分别记为力量 +25 和根性 +10；当前只比较回体和技能 Pt，并提示缺少该角色的实包。第 49 回合的「初詣」比较回体 30、五维各 +8 和技能 Pt +35。两场都按续步估值选择，计入预计属性溢出和可利用的回体，第 25 回合还计入塔克随后确定发放的回体。同分取游戏列表中靠前的选项。 [育成事件资料](https://gamewith.jp/uma-musume/article/show/253241) · [GameTora 107501](https://gametora.com/ja/umamusume/characters/107501-daring-tact) · [GameWith 角色事件](https://gamewith.jp/uma-musume/article/show/587801) · [神ゲー攻略同一选项](https://kamigame.jp/umamusume/page/449130076269739712.html)

经典合宿末回合行动前比较力量 +10 和根性 +10 的有效收益。第 49 回合行动后的福引按实包奖品编号拟合，196 个拟合样本各档为 24／14／61／79／18。两种头奖具有相同的资源收益。

条件回放使用实包提供[训练后事件与下一面板](#训练后事件条件回放)。休息、升级确认或岛训练可以通过 `turnArrival` 提供同一种接续面板。

生成资源时用 `tools/UmaAiSharp.Pioneer.DataGenerator/freeze_holdout.py` 冻结整局切分。依次运行 `fit_turns.py` 和 `fit_training_partners.py` 产出回合校准。DataGenerator 接收 master、新资源路径、回合校准和由 `fit_inheritance.py` 生成的继承校准。继承报告列明各因子类型的拟合和留出机会、观察触发数与期望数。导入文件按规则资源 SHA-256 绑定，更新规则后从实包重新导入。

自主续步按卡组的代表训练板估计各等级的产出和有效机会。普通训练升级按同一批板的等级增量、剩余机会、评审和 Lv5 上限估值。续步还计入塔克同训对羁绊和下一回合人员分布的影响，以及建设发券后的岛训机会。休息和友人外出共用体力估值，结合实际回复空间、剩余外出窗口和之后可兑现的训练比较。详见 [回合推荐](recommendation.md)。

固有升级前的最后一回合，续步按第一名粉丝分布计算参赛跨过门槛的概率。目标赛第一名以五维各 +3、技能 Pt +45 为通用奖，按路线、战绩和当时的角色状态结算专属奖。各笔奖励先乘比赛加成并向下取整，再分别应用 1200 以上换算与属性上限。目标赛专属 Hint 和角色状态随奖励结算。

SQLite 导入将 `race_history` 与已经完成的 `race_entry → race_end → race_out` 合并，同一回合采用正式比赛结果。连胜、经典三冠和三重冠只使用第一名记录。模拟未来的第一名写入同一份战绩。成田白仁优先采用原包揭示的目标或「岐」的实际选择。在未来模拟到「岐」时，经典三冠赛每胜计 2，其他经典级 G1 每胜计 1，达到 6 则选择强敌路线。

稀疏的目标赛分支按实包估计。气槽三重冠估计另给五维各 +3、Pt +20，樱花进王连胜后的速度基数估计为 +15，鹤丸刚志的资深级有马纪念另给五维各 +5。成田大进带「練習ベタ」获胜的日经赏按五维各 +20、Pt +73 估计，并解除该状态。黄金船宝冢连胜采用实包中的第 1 项。[目标赛样本脚本](../tools/UmaAiSharp.Pioneer.DataGenerator/fit_goal_race_rewards.py) 用去重的逐包数据重算通用奖命中率和按目标编号的研究估计，输出 JSON。

育成目标在终局评价。PioneerAiPlugin 与 MCP 都先比较联合达标率，再比较达标样本的平均终局总分。每条轨迹用自己的技能 Pt、Hint 和适性计算可购技能评分，扣除起点可购分及已购技能的固定评分。第 51、55、72 回合的固有升级粉丝门槛分别为 60,000／70,000／120,000，基础适性为泥地 A 且草地 E–G 的已识别角色使用 40,000／60,000／80,000。第 55 回合还要求理事长羁绊至少 60。第 65 回合按育成与编成角色决定六项提示的金白档，并按当前可购技能价值选一项。前四期大好评及第五期评审抽取一次编成支援的普通 Hint 奖励，第五期另按评价给全身全霊或末脚的 Hint。五次大好评的结局发放本能の懸け橋 Hint Lv1，前四次大好评而末评为好评的结局发放あるがままに Hint Lv3。

资源内含 master 的技能链、费用、适性标签和角色觉醒技能。每个技能组择一，在预算内最大化可购分数。技能进化和养成中的购技动作排除在这一估计范围外。续步使用相同的有效 Hint 奖池，按解锁和折扣估计技能收益，同时计入属性奖励分支。

## 原始回合导入

```json
{
  "name": "pioneer_import_capture",
  "arguments": {
    "capturePath": "<抓包.sqlite>",
    "packetId": "2043",
    "rulesPath": "<pioneer_rules.br>",
    "outputDirectory": "<本次工作目录>/turn"
  }
}
```

也可调用 `pioneer_import_packet`，将 `capturePath` 和 `packetId` 换成响应正文的 `packetPath` 与实际 `endpoint`，例如 `/umamusume/single_mode_pioneer/check_event`。`load`、`start` 的嵌套 common 按端点解码。输出包含 `input.json`、`candidates.json` 和保存源包、源 master 及规则身份的 `import.json`。执行输入保留游戏状态字段。

支持 `scenario_id=11` 的开局、普通／合宿就绪态、建设规划和确认、训练升级、休息确认与末尾角色结局。原始 `start` 导入应用固定开局的 +120 技能 Pt 和理事长登场，并保留揭示的训练板。要复现开局事件后的实盘训练板，使用首个就绪响应。

SQLite 导入仅使用锚点及更早的本局记录，配对训练与事件请求，恢复成功普通训练次数、故事历史、路线战绩和谱系。若训练前缀无法还原，导入保留当前响应并登记原因，自主续步按当前等级、评审与执行次数估计训练进度。缺少开局亲本时省略剩余的途中继承。`import.json.estimatedTrainingHistory` 标记训练进度估计，`ignoredMechanisms` 列出影响本次计算的育成卡、支援卡、故事或目标赛编号。训练次数与配对证据见 [训练历史](training-history.md)。

## 根候选推荐

```json
{
  "name": "pioneer_recommend",
  "arguments": {
    "inputPath": "<本次工作目录>/turn/input.json",
    "candidatesPath": "<本次工作目录>/turn/candidates.json",
    "rulesPath": "<pioneer_rules.br>",
    "goalPath": "examples/stamina-curve-goal.json",
    "seed": "12345",
    "samples": 32,
    "workers": 32,
    "outputPath": "artifacts/recommendation.json"
  }
}
```

候选文件是 `{ "id": 整数, "action": 完整动作 }` 数组，编号唯一且非负。每个候选首批使用 `samples` 条轨迹及同一组样本种子。自主整局的训练和建设规划按配对分差加样，`search.sampleLimitPerCandidate` 记录单候选上限，`summary.sampleCount` 记录该候选的实际样本数。任一候选因机制缺失停止时，报告保留已调度的样本，建议编号为空。

候选按联合达标率和达标样本的平均终局总分 `meanSuccessfulTotalRating` 排序，同时列出五维及新增技能分。所有候选均为零达标时，依次比较路线失败率、平均条件总缺口和目标优先级。初始化缺少机制时报告 `phase=initialization`。详见 [回合推荐](recommendation.md)。

## 建设规划

用 `pioneer_import_capture` 导入 `playing_state=35` 且事件队列为空的规划响应。`candidates.json` 列出所有合法有序方案，同样交给 `pioneer_recommend`。跨发展 Pt 阈值时按方案顺序落成设施；报告保留每个方案的逐样本终局及 RNG 末态。轨迹内的后续规划按设施在剩余回合可提供的收益比较方案。正式规划模型见 [有序建设计划](recommendation.md#有序建设计划)。

## 训练后事件条件回放

从 `pioneer_import_capture` 导入一张可行动面板。将 `input.json` 改为 `mode: conditional`，保留起点和对应的 `rulesPath`，按实包顺序填写 `script` 和 `outcomes`。每个行动最多一个同序号的条件结果。

- 训练使用 `trainingResult`。在 `data` 提供 `actionOrdinal`、`success` 和 `next`。训练即时资源、羁绊、发展 Pt 和次数由训练规则计算。 `next` 来自训练响应。
- 普通事件使用 `observedEventResult`。在 `data` 提供 `actionOrdinal`、完整 `event`、请求中的 `choiceNumber`、该事件的 `changes` 和确认响应中的 `next`。脚本决策使用 `kind: 21`，按当前事件回合和 `storyId` 计算 `key`。 `chooseEvent` 携带 `storyId`、`characterId` 和所选编号。完整 `event` 保留观察到的协议身份。
- 记者评价故事 `400000031` 至 `400000034` 可把 `changes` 显式设成 `null`，由模拟器核对训练人员和羁绊档位、计算奖励。
- 观察到训练升级时，执行器进入训练等级确认 `kind: 18`。确认动作使用 `turnArrival`，由规则增加训练等级。建设 107／108 在事件确认时复用设施升级和发券结算。

`next` 和 `turnArrival.data` 使用以下面板字段。后者还需提供 `actionOrdinal`。

| `arrival` | 实包字段 |
| --- | --- |
| `sameTurnEvent`、`nextTurnEvent` | `turn`、`rawPlayingState`、`event`、`choiceCount`。0 或 1 项的确认编号为 0，多项按实际请求填 1 至选项数。 |
| `trainingLevel` | `turn`、`rawPlayingState`、`event`。 |
| `ready` | `turn`、五项 `board`、`availableTrainingCommands`、`availableRestCommands`。岛训练可用时提供 `islandBoard`。下一张面板可能和刚确认的事件在同一回合。 |
| `otherPhase` | `turn`、`rawPlayingState`。报告保存该阶段，以该阶段的入口继续。 |

`changes` 可以为空对象。资源、上限、干劲、发展 Pt、粉丝和羁绊填写单个事件的面板差分，属性使用游戏显示数值。 `bonds` 每项为 `{ targetId, change }`，支持六个卡位及理事长 102、记者 103。 `hints` 每项为 `{ skill: { groupId, rarity }, level }`，`level` 为事件结算后的实际等级。 `addedEffects` 和 `removedEffects` 填角色状态 ID。 `grants` 填当前事件产生的短期得意率效果，`unlockedFriends` 填本次解锁外出的友人卡位。

条件回放按提供的顺序消费事件，全程使用观测训练结果、选项和面板。单次执行报告的 `finalState.hintLevels` 按技能组和稀有度返回已知 Hint。脚本只覆盖前缀时，到达下一决策后以 `scriptMismatch` 结束，可用已结算动作数和 `finalState.header` 核对是否抵达预期面板。

## 记者收尾与目标赛条件回放

- 记者收尾以 `400000091` 待确认响应为导入锚点。将导入文件设为条件模式，使用导入报告中的决策和首个候选作为唯一脚本动作，`outcomes` 填空数组。轨迹结算本事件，随后停在后续收尾剧情的边界。
- 目标赛以出赛前面板为锚点，脚本选实际比赛。结果类型填 `targetRaceWin`，在 `data` 提供 `actionOrdinal`、`programId`、从比赛结果响应读取的 `fansGained`、赛后选择 `postRaceChoice`（默认 0），以及角色目标故事确认响应中的 `next`。模型使用导入的战绩和状态计算奖励。单独的先锋发展 Pt 事件由该故事的后续确认处理。
- 美妙姿势的札幌纪念（目标 549、场次 82）按脚质结算。逃、先行、差的胜利结果须另填 `excludedAttribute`，取值为 `speed`、`stamina`、`power`、`guts` 或 `wisdom`。用比赛结束与目标故事确认时的五维和上限核对哪一项未获奖。无法唯一判定时留空，回放会在发奖前停止。缺少比赛脚质、或使用追込而额外属性数值尚未核验时，同样停止。其他目标赛省略此字段。
- 爱丽速子「透明な視線」和待兼诗歌剧第 70 回合故事可用 `observedEventResult` 确认，填 `changes: null` 由 Base 的分支规则算出奖励。如已给出实包资源差分，回放直接应用该差分，奖励只结算一次。[分支奖励的结算时点](race-route-evidence.md#分支奖励的结算时点)

## 已揭示事件选项

可用抓包库的 `get_choice_reward` 响应编号导入。导入器向前关联同一账号配对的奖励请求和当前事件，选项编号按可见顺序映射。输出起点为 `declaredEvent`，候选保留全部可见选择。`import.json.eventRewards` 记录状态、请求、奖励包号和窗口指纹。

只有当前状态包时，队首已揭示的多个选项也可导入。缺少奖励声明的选项保留在候选中，提示会列出故事与选项编号；收到配对的奖励响应后可以重新导入比较。

`pioneer_recommend` 的 `immediateEventRewards` 报告按 `goalPath` 比较每个选项在此次事件结算后的属性、约束缺口、属性溢出和新增技能 Pt。体力、干劲、羁绊与状态变化列在各分支后态，`grantedHints`、`grantedFans` 和 `grantedDevelopmentPoints` 列出声明的授予量。这些分量的后续训练、购技、建设和赛程收益未纳入即时比较。

发生概率未知时逐分支比较。一个选项的每个结果都优于另一个选项的每个结果，后者才会被排除。`contenderIds` 只剩一个候选时才返回推荐，多个候选表示并列或结果范围重叠。整组奖励缺失的分支保留为未知；单项奖励缺失时保留同分支其余效果。反馈时附 `ignoredMechanisms` 中的支援卡或角色、故事、选项、分支编号和未计效果。日本玩家对笹针师等事件的实测按选项区分，样本不足以推算其他故事的成功率。 [法米通育成实测](https://www.famitsu.com/news/202106/01222293.html)

奖励编号取自日服 master 的 `text_data` 类别 394。编号 13 按五项属性同时增加。分支含单个随机属性奖励（14／34）时，报告枚举从五项属性中选出指定数量的全部组合，同一 `branchIndex` 的不同组合列在 `randomAttributes`。故事 501135301 的「随机三项 +5」有十个组合，15 个单分支实包窗口都观察到三项属性增加。编号 2 的属性扣减按显示值直接扣除。两个育成抓包库中有 91 项与声明对应的实际扣减，其中 8 项的前态超过 1200。编号 9／37 获得状态、10 解除状态，35／36 扣除羁绊。保护状态是否因拦截异常而消耗、同一属性的多个奖励原子的结算顺序等缺口会随分支列出。

以 `pioneer_replay` 执行 `examples/pioneer/event-choice.json`。样例为故事 `501068510` 的选择 1，耐力奖励 +10 在 1200 换算后为 1199→1204。单一结果直接结算；多结果用 `{"kind":"eventRewardResult","data":{"actionOrdinal":0,"choiceNumber":1,"branchIndex":0}}` 选择，索引从 0 开始。资源数值结算后，无法归因的奖励原子保留在输入中。

## 训练到建设

`examples/pioneer/training-construction-replay.json` 包含成功普通训练和建设确认。过线后的决策为 `kind=4`，`key` 使用建设故事 `400011107`。确认按当前计划顺序落成达到阈值的设施，并结算岛训练券。详见 [触发规则](training-construction.md)。

## 休息与合宿休息

`examples/pioneer-rest-ordinary.json` 提供选择普通休息 701 后揭示的 `restEvent`，脚本确认该故事后结算 50 或 70 点体力。`examples/pioneer-rest-camp.json` 选择合宿休息 3606，恢复 40 点体力与一档干劲。条件回放可用 `turnArrival` 注入观测到的后续面板。详见 [休息原包证据](rest-action-evidence.md)。

## 训练升级确认

`examples/pioneer/training-level-trigger-replay.json` 从第 16 回合第 4 次成功速度训练开始。训练完成时只登记待结算里程碑。故事 `400000005` 揭示后，确认动作才增加对应训练等级。当前等级、协议次数与成功普通次数分别保留。详见 [触发规则](training-level-trigger.md)。

## 失败条件回放

```json
{
  "name": "pioneer_replay",
  "arguments": {
    "inputPath": "examples/pioneer/ordinary-failure-replay.json",
    "rulesPath": "<pioneer_rules.br>",
    "seed": "1234",
    "outputPath": "artifacts/execution/ordinary-replay.json"
  }
}
```

| 条件回放样例 | 行动前 | 观察到的结算 |
| --- | --- | --- |
| `ordinary-failure-replay.json` | 第 30 回合普通耐力 105，耐力 535，干劲 5 | 耐力 530，干劲 4，推进至第 31 回合 |
| `camp-failure-replay.json` | 第 37 回合合宿耐力 3602，耐力 795，干劲 5 | 耐力 790，干劲 4，推进至第 38 回合 |
| `camp-training-turn.json` | 第 38 回合合宿根性 3604 | 用有效 Lv5 结算，增加对应协议次数，保留普通训练等级 |

失败样例通过 `trainingFailure` 提供观测事件，通过 `failurePenalty` 提供惩罚分支。训练尝试增加一次协议执行次数，确认才扣属性与干劲。若有正向思考，按效果规则抵消干劲下降。两条样例覆盖到下一回合事件边界。完整报告保存已消费动作数、停止码和局部状态。

## 批量执行

`pioneer_batch` 按 `input.json` 的 `mode` 运行。参数包含 `rulesPath`、`seed`、`samples`、`workers` 和 `outputPath`，自主模式还须传入 `goalPath`。种子为 UInt64 十进制字符串，`samples` 为正整数，`workers` 取 1–32。固定种子下，worker 数量不影响对应 `sampleIndex` 的结果。取消时，报告保留全部请求样本的位置及已知 RNG 末态。

## 输入与报告

执行输入包含 `rulesIdentity`、`mode`、`start`、`outcomes`、`script`，以及有需要时的 `placement`。`start` 和各项 `outcomes` 使用 `{ "kind": "...", "data": { ... } }`。读入时校验必填属性，并明确拒绝未知和重复字段。既有输入若含退役字段，从原始实包重新导入。

条件脚本的每项包含 `decisionId` 和 `action`。决策 `kind` 对应 `PioneerStage` 数值，`ordinal` 为从 0 开始的执行序号，`key` 高 32 位为回合，低 32 位为故事、单指令片段的指令或当前决策使用的 0。脚本从导入报告的决策及候选动作起步。当前事件动作的逻辑身份只用 `storyId`、`characterId` 和 `choiceNumber`。

工具创建新的报告文件。一般执行报告记录输入和规则指纹、逐样本终局、停止原因与 RNG；单次执行另报最终局部状态。`outcome=null` 表示未产生完整终局，不能纳入终局评价。

## 复跑

`tools/UmaAiSharp.Pioneer.DataGenerator/verify_pioneer.py` 使用真实 stdio，核对工具发现、导入和推荐、模型以及单／多 worker 一致性。历史 Omakase 参考值从两份显式提供的抓包库重算。需 Python 的 `msgpack` 和已构建的 MCP。用新的输出目录保存结果。

```text
python tools/UmaAiSharp.Pioneer.DataGenerator/verify_pioneer.py --mcp <MCP.dll> --rules <pioneer_rules.br> --goal examples/stamina-curve-goal.json --capture <当前抓包.sqlite> --baseline <历史库1.sqlite> <历史库2.sqlite> --workers 32 --output <新目录>
```
