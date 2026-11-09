# Pioneer 检查点评审证据

`CheckpointSettlement` 提供有界的达标评价、指定评审事件的发展 Pt 清零和体力恢复、高评价的训练等级与理事长羁绊变化，以及新增PJ人数。它不结算属性、技能或具体成员身份，也不推进事件队列或回合。

## 数据来源

输入为 `artifacts/evidence/local-audit-05` 的脱敏请求、响应、配对和 master 导出。正式报告为 `artifacts/evidence/checkpoint-audit-01/checkpoint-audit.json`，逐条证据为同目录的 `checkpoint-submissions.jsonl`、`checkpoint-settlements.jsonl`。

`src/UmaAiSharp.Pioneer/Development/Evidence/provenance.json` 记录原始来源哈希、归一化五个输入文件的哈希、报告哈希及 11 张表的比较结果。391 条测试夹具来自这些事件结算记录，只保留匿名来源哈希、前态/请求/后态记录号及必需游戏数值。没有复制账号或育成身份。

交接包 `02_master数据库/master_copy.mdb` 与本地日服 `master.mdb` 的 11 张 `single_mode_11_*` 表，建表 SQL 与按首列排序后的全部行相同。两份数据库的整体 SHA-256 不同：

- 本地日服：`4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886`。
- 交接包繁中：`a9f5ad407e71e9ac471493d28aac3c79fa4f292fd345e9f953afce634c04d534`。

只读复核脚本为 `src/UmaAiSharp.Pioneer/Development/Evidence/compare_master.py`，参数是 `--left <本地master> --right <交接包master>`。它以 SQLite `mode=ro` 打开文件，输出来源哈希及逐表结构/行比较；本次输出保存在同目录 `master-table-comparison.json`。表相同不证明客户端或服务端版本相同；版本差异保留用于归因，不阻止已验证的本地分量实现。

交接包七局 `checkpoints.jsonl` 的 35 个摘要也符合“Pt 达目标时结果为 2，否则为 1”的候选比较，覆盖 deck 2、3，其中结果 1 两例。`tc-checkpoint-summaries.json` 保留摘要源哈希、行号及对应 deck/目标。摘要没有成对原始请求和即时前后响应，不能证明奖励时序，也没有解除结果判定的缺口。

## 已核验行为

387 个检查点提交满足同一局、同一来源、请求响应明确配对、`前态记录号 < 请求记录号 < 后态记录号`、请求 `current_turn` 与前后相同，且前态阶段为 34。这 387 个响应均进入阶段 5，发展 Pt、体力和体力上限保持不变。直接在 `check_point` 响应结算溢出恢复会产生反例。

随后明确选择以下评审事件的 391 个 `check_event` 请求，全部符合发展 Pt 清零与体力候选式：

```text
surplus = max(0, before.Pt - planning[deck, checkpointType].target_pt)
recovery = min(pioneer_pt_bonus.add_value_max,
               floor(surplus * pioneer_pt_bonus.pt_rate / 1000))
after.vital = min(before.max_vital, before.vital + recovery)
after.Pt = 0
```

| 事件 story_id | 检查点/已知结果 | 明确选择数 | Pt/体力匹配数 |
| --- | --- | ---: | ---: |
| 400011004 | 1 / 2 | 79 | 79 |
| 400011008 | 2 / 2 | 78 | 78 |
| 400011012 | 3 / 2 | 78 | 78 |
| 400011017 | 4 / 2 | 77 | 77 |
| 400011021 | 5 / 2 | 77 | 77 |
| 400011022 | 5 / 1 | 2 | 2 |

这些选择均为 deck 3、阶段 5、`choice_number=0`，回合由该检查点的 master 行确定。前四期观察到结算后仍在同一回合、阶段为 35；第五期观察到 turn 60→61、阶段为 1，但后续事件可不同，分量 API 没有把这些观测扩展为通用调度规则。

目标取当前 `checkpointType` 的 master 行。`check_point` 响应已经可能显示下一周期的 `planning_cost`，不能拿它代替本次目标。

`CheckpointMasterData` 原样读取 `planning`、`pioneer_pt_bonus`、`check_point_eff`。结果效果按 `(checkpointType, resultState)` 取表，保留 `effect_type` 与四个原始数值，不通过“大成功是成功两倍”推导。例如第 1 期 effect 204 的值分别为 20 和 15，并非二倍关系；第 5 期没有 effect 202 行。

`EvaluateReachedTargetCandidate` 只在 deck 3、阶段 34、master 对应的第 1..5 期回合运行。Pt 达到目标时返回结果 2 及该结果的 master 效果行；Pt 低于目标则抛出 `UnknownMechanismException` 停止。387 个直接提交样本中，385 个严格高于目标、2 个低于目标，恰好等于目标的样本为 0。等号边界按目标阈值定义及 [日文游戏帮助转录](https://wikiwiki.jp/vip_umamusu/無人島へようこそ%20-DESIGN%20YOUR%20ISLAND-) 的达标说明解释，由合成测试检验实现。转录页面为社区来源。

## 适用边界与未实现项

- 结果 1 的日服直接结算证据只有第 5 期两例。第 1..4 期结果 1、其他 deck、其他选择均明确拒绝套用资源结算。结果与 Pt/目标关系矛盾也明确失败，不改写输入结果。
- 目标比较在 387 个日服提交中全部匹配，但缺少低于首个建设阈值等局面，未实现完整低评价结果判定。master 只列出结果 1/2 的效果，不等于已证明不存在其他结果或流程。已知第 5 期结果 1 的资源结算入口仍可接受实包给定的结果；这与从任意低 Pt 推导结果不同。
- 属性奖励涉及显示/内部单位和同时发生的其他奖励，技能 Pt 也出现额外增量；本模块没有把整段净变化强归因于评审。
- 完整有序计划合法性、重复设施与路线限制、建设事件调度、岛训练增益和券全生命周期没有由这批评审证据补全。交接包 Python/Kotlin 的自动决策、近似概率或默认结果不进入规则。
- Kotlin 首阈值循环以“累计成本尚未到阈值”为继续条件，可能在加入下一设施后超额；这不能替代已存在的日服建设反例。`planning_cost` 与 `target_pt` 的相等关系也不能证明它只表示目标、完全没有预算约束。

核验器对 1373 个缺少事件数组的观测记录缺字段并打断前后连续性，另有 4 个提交阶段/回合不符、1 个提交缺相邻完整前态；不会补空数组或继承陈旧前态。读前、读后均校验归一化输入文件哈希。输入的来源哈希由原始证据报告声明；派生文件另有自己的哈希，不把两者等同。

## 复跑与验证

```powershell
dotnet src/UmaAiSharp.Cli/bin/Debug/net10.0/UmaAiSharp.Cli.dll evidence checkpoint --input artifacts/evidence/local-audit-05 --output <新目录>
dotnet test tests/UmaAiSharp.Tests/UmaAiSharp.Tests.csproj --no-restore --filter FullyQualifiedName~PioneerCheckpointTests -p:WarningsNotAsErrors=NU1900 -m:1 -nr:false
```

核验状态为 `CandidateOnly`，退出码为 3，不表示整局模拟通过。原有 15 个定向测试覆盖 391 条实包资源夹具、倍率取整及两层上限、已知低结果、未知分支、缺 master 字段、提交与结算分离、请求来源/成员关系/捕获顺序，结果位于 `artifacts/validation/checkpoint-tests/checkpoint-tests.trx`。本轮另加 6 个评价用例，覆盖五期目标等号、低于目标停止及阶段限制，由统一集成运行确认。在线 NuGet 漏洞索引不可达产生的 NU1900 不属于本次机制验证。

## 高评价进度分量补充

训练等级、理事长羁绊及新增PJ人数的实现与389个局部窗口证据见[development-components-evidence.md](development-components-evidence.md)。`CheckpointAudit`同时输出这些分量的核验，不生成具体PJ身份或后续事件。

扩大材料支持给定新人身份后的记录追加，以及末期已经发奖的角色显示确认；边界见[事件与成员分量证据](pioneer-event-components-evidence.md)。9个混合响应未被改写成额外评审奖励公式。
