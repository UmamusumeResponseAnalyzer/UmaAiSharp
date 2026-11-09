# 比赛路线与评分核查

核查日期：2026-11-05。本模块读取育成当前已经选择的目标 ID，并映射到 master；没有实现条件分支选择、比赛结果或奖励。

## master 与状态边界

`single_mode_route` 将角色映射到 `race_set_id`，同时保留剧本、条件集合和优先级。`single_mode_route_race` 包含目标回合、顺序、条件、普通/决赛标识及条件分支字段。`single_mode_scenario_group` 决定哪些目标行适用于 Pioneer。`card_data` 提供育成卡到角色的映射。

当前育成的 `route_id` 和 `route_race_id_array` 必须保留在状态中。不能仅取同一 `race_set_id` 的全部 master 行生成赛程，因为这些行同时包含互斥候选和后来追加的目标。

示例：路线 122 的目标 1523 和 1524 同为第 5 项、回合 44，但目标赛、名次要求和条件标记不同。实包观察到 1523 被 1524 替换，随后还可追加目标 1529。目标 1530–1532 则使用决赛组编号 10001–10003，不能当作普通比赛 program ID。

`RaceRouteCatalog.ResolveSelected` 只执行明确 ID 的解析，检查育成卡、路线归属、剧本组及编号唯一性，保留原有顺序与全部条件字段。它既不猜选哪个候选，也不把结构对应当成目标完成条件已经解释。

## 本地核验

报告：`artifacts/evidence/race-route-audit-02/race-route-audit.json`；逐状态行在同目录 `race-route-observations.jsonl`。

| 项目 | 结果 |
|---|---:|
| 育成数 | 79 |
| 去重后的路线状态 | 104 |
| 已选目标全部能对应到角色路线及 Pioneer master 组 | 104 |
| 输入行序中的路线状态切换 | 25，未宣称跨包库的全局时序已重建 |
| 已选集合等于全部相关 master 行 | 67 |
| 必须依赖已选目标状态、不能直接取全部行 | 37 |
| exchange 不满足配对及响应归属条件 | 58，未用于映射 |
| 输入解释错误 | 0 |

来源为 `local-audit-05` 的 observations、exchanges 和本地 master；三者读前读后指纹一致。只采用 `Paired` 且响应成员/endpoint 相符的观测，要求 `scenario_id=11`。无效目标在去重前拒绝，不被重复帧吞掉。该核验没有使用后续状态来替模拟器决定分支。

## 尚缺规则

- 开局路线候选的条件与优先级选择，以及育成中的替换/追加时机。
- 目标条件类型 1、2、3 的完整解释及全部边界。
- 可选比赛的开放条件、粉丝要求、固定禁赛回合与特殊角色限制。
- 已确认育成层比赛结果分布、失败与重试、奖励、比赛后事件。

`single_mode_chara_program`、`single_mode_program`、`single_mode_turn`、`race_instance`、`race`、`race_course_set`、`single_mode_race_group` 及 `single_mode_race_restrict_turn` 提供后续核查所需的数据关系；表的存在不等于上述算法已完成。没有构造“全部目标都获胜”的路线执行器。

## 评分引用

已逐段对照本地 UmamusumeResponseAnalyzer 的 `Database.StatusToPoint(int)`：当前 `ScoringCandidates.StatusToPoint` 使用同样的 0–1200 分段、1201–2000 分段及超过 2000 后按 25 点分段的公式，不增加 Host 运行依赖。

已有 520 个去重输入与总分组合匹配仍属于限定材料的总分核验。速度覆盖到 2338，其他四项没有超过 2000 的样本；不能把函数一致性或总分匹配当成所有属性区间的独立实证。购技/进化按用户最新范围排除，技能 Pt 及影响后续育成的实际状态保留；已有评分候选不代表提供了购技策略。

## 复跑

```powershell
dotnet src/UmaAiSharp.Cli/bin/Debug/net10.0/UmaAiSharp.Cli.dll evidence race-route --input artifacts/evidence/local-audit-05 --master G:/DMM/Umamusume/umamusume_Data/Persistent/master/master.mdb --output artifacts/evidence/race-route-audit-next
```

退出码 3 表示生成了有界映射报告，比赛模拟尚未完成。测试覆盖条件目标切换、决赛组保留、不完整/异剧本/冲突输入，以及无效重复状态的明确拒绝。
