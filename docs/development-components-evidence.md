# Pioneer 确定性发展分量

本模块只执行已明确选择事件中的部分状态变化，不负责比赛结果、事件触发、后续队列或整个育成流程。使用本地 master 与实包；来源哈希用于归因，未要求资源版本完全一致。

## 已实现范围

| 分量 | 前态约束与计算 | 实包核验 |
|---|---|---|
| 比赛发展 Pt | `deck=3`、阶段5、故事400011105选择0；实际目标赛分类由调用者提供。普通20先乘最近高评价的 master 加成再向下取整，目标赛另加40 | 902 个两个研究都明确关联并独立分类的窗口，519目标赛、383非目标赛 |
| Tucker 资源 | 400011111、时机6、选择1、回合8..60、阶段5、deck3；支援30257、突破4、经验118185：发展Pt+80、技能Pt+3 | 376 个明确选择。master 支援30257为稀有度3，经验118185对应等级50 |
| 高评价评审进度 | 五期高评价已选事件；第2/4期五项普通训练等级各+1、上限5；理事长羁绊`min(100, before+5+2×注目株)` | 389 个明确高评价窗口；注目株且未满羁绊的可辨别样本6个，已满1个 |
| PJ 新增人数 | 第2/4期返回新增3人，其余高评价返回0人 | 389 个窗口核对人数与未移除既有成员；具体新增身份未生成 |
| 建设券 | 既有107/108设施分量同时返回`IssuedTickets=1`、`DiscardedTickets=beforeTickets`及持有量1；仅覆盖前持有0/1 | 777 个明确建设选择均上报`gain_num=1`：669个0→1、108个1→1 |

比赛组件的首次评审前证据只覆盖回合12目标赛；之后仅覆盖第1..4期高评价到下一次评审之间。低评价、最终评审之后、其他deck和其他事件分支显式抛出`UnknownMechanismException`。它不推断胜率、比赛名次、目标完成条件或粉丝奖励。目标赛分类必须来自实际比赛及当前已选路线，不能依据观测到的Pt增量反推分类。

Tucker选择2只有预览证据，没有实际选择结算；未实现。支援其他等级的master记录不等于已核验结算，不用+80覆盖。组件返回两个资源的后态，不消费事件、不推进回合。

`CheckpointSettlement.ApplyHighEvaluationProgress`输出训练等级、理事长羁绊和应新增人数。PJ成员身份由独立规则处理；完整评审事件还需要其他奖励及调度步骤。原有Pt清零/体力恢复分量保留独立口径；训练等级和羁绊分量的低评价显式停止。

`PioneerConstructionRules.ApplySettlementComponentsCandidate`返回`ConstructionComponentSettlement`。`After`保留当前回合及待处理事件，`IssuedTickets`是实际发券数，`DiscardedTickets`是此范围内超出持有上限的券数。调用方必须在完整事件处理流程中保证每个事件只结算一次；本方法不识别重复消费。turn64晚期两券、券上限切换、过期等不属于该建设分量。

## 核验入口与证据链

```powershell
dotnet run --project src/UmaAiSharp.Cli --no-build -- evidence development-components --input artifacts/evidence/local-audit-05 --research artifacts/research/blockers --output artifacts/evidence/development-components-new
dotnet run --project src/UmaAiSharp.Cli --no-build -- evidence checkpoint --input artifacts/evidence/local-audit-05 --output artifacts/evidence/checkpoint-components-new
dotnet run --project src/UmaAiSharp.Cli --no-build -- evidence construction --input artifacts/evidence/local-audit-05 --output artifacts/evidence/construction-components-new
```

输出目录必须为空。`DevelopmentComponentAudit.Run`先核对研究中五个归一化输入的完整SHA-256，再关联两个研究的比赛标签，并记录研究文件自身的前后指纹。新增或重导出的数据不匹配时直接报错，不能只靠`packets-0002`等alias复用旧标签。规则类本身不依赖研究目录。

比赛原有903与904统计的交集为902，交集的分类、加成与增量全部一致：

- 建设研究独有`packets-0004:5273`：有比赛后态标签，但比赛研究没有唯一完整比赛链。
- 比赛研究独有`packets-0002:11318/19916`：当前回合是粉丝数目标`condition_type=3`；建设研究保守单列歧义。
- 另外3个缺比赛标签；本入口合计保留6个无共同标签的事件，不计为公式通过。

`CheckpointAudit`在已存在的严格请求/响应/前态配对后调用进度组件，输出`checkpoint-progress.jsonl`及`MatchedProgressSelections/CandidateProgressMatches`。缺训练等级、理事长关系或PJ列表会记录明确错误，不补值。`ConstructionAudit`输出候选发券量、响应`gain_num`与是否相符；缺发券字段不能由剩余券净差补齐。

测试夹具位于`src/UmaAiSharp.Pioneer/Development/Evidence/`：`race-point-observations.jsonl`902行、`tucker-point-observations.jsonl`376行、`checkpoint-progress-observations.jsonl`389行。`export_component_fixtures.py`只投影研究转移的原始前后字段，没有使用研究脚本的预测值作为期望值；`component-provenance.json`保存输入/夹具SHA。来源只保留匿名源哈希和记录编号，未复制账号身份。

证据入口为`artifacts/research/blockers/construction-checkpoints/REPORT.md`、`run-02/transitions.jsonl`、`deep-03/race-labels.json`及`artifacts/research/blockers/races/hypotheses-02/pt-candidate.json`。902/376/389是同一批材料的回归范围，不是独立留出验收集。数值拟合成功不能证明全部条件与未观测分支。

2026-11-08 已编译CLI实包复跑结果：

| 报告 | 实际结果 |
|---|---|
| `artifacts/evidence/development-components-01/development-component-audit.json` | 比赛902/902、Tucker376/376；6个比赛事件无共同明确分类标签，不计通过 |
| `artifacts/evidence/checkpoint-components-01/checkpoint-audit.json` | 387个关联提交、391/391资源分量、高评价进度389/389；2个低评价没有调用高评价进度组件 |
| `artifacts/evidence/construction-components-01/construction-audit.json` | 782/782设施与持券候选；777个明确请求的发券量777/777，107分支705、108分支72 |

评审报告同时保留1373个缺完整事件字段的观测、4个不满足已知转移条件和1个缺连续前态；建设报告保留919个部分计划观测。它们没有被补值或纳入通过数。5个缺明确选择的建设候选也未升级为直接请求因果证据。

## 扩大比赛Pt研究入口

`DevelopmentComponentAudit.RunRaceStudy(inputDirectory, studyDirectory, masterPath, outputDirectory)`显式接受扩大比赛研究，独立于原902交集入口，不自动选择研究版本或回退到旧标签。

```powershell
dotnet src/UmaAiSharp.Cli/bin/Debug/net10.0/UmaAiSharp.Cli.dll evidence race-points --input artifacts/research/blockers/island-training/expansion-20261108/audit --study artifacts/research/blockers/races/expansion-20261108 --master G:/DMM/Umamusume/umamusume_Data/Persistent/master/master.mdb --output artifacts/evidence/race-points-expanded-new
```

研究定位文件包含2483个直接窗口（旧904、新1579），入口核对四个归一化输入与master的完整指纹，重新读取实际前态、请求、后态和相应比赛响应；用实际已选路线与master重新判定目标赛，调用产品公式并对比原始Pt净差。研究的predicted/observed字段不作为真值。窗口必须同源同局、请求响应明确配对、前态/请求/后态有序；中间仅允许完整配对的奖励查询。其余六个缺唯一比赛链的事件不在该定位集合中。

2026-11-08产品CLI报告`artifacts/evidence/race-points-expanded-01/race-point-study-audit.json`记录2483个定位窗口、2483个实际核验、2483个匹配、0个issues。逐例输入与结果为同目录`race-point-study-cases.jsonl`。本入口没有改变胜率、粉丝、通用比赛奖励、低评价或未观测设施效果的规则。

`PioneerRacePointGain`保留普通20、评审和目标赛40这几个局部分量的职责。它没有设施输入，不能自行识别海之家Lv3的effect206；完整比赛事件组合器必须识别该未实现效果并停止，不能把本分量的返回值当作已结清全部发展Pt。该设施分支没有出现在2483个定位窗口中，仍是完整模拟的核心阻塞。

## 尚未闭合

新增Tucker外出分量、PJ记录追加及末期已发奖确认边界见[事件与成员分量证据](pioneer-event-components-evidence.md)。成员身份仍须显式给定，没有实现抽样。

完整计划确认条件、Hint抽选与过滤、PJ成员身份、事件优先级、比赛结果模型及通用奖励仍未实现于这些组件。建设/评审的属性奖励也未由本模块生成。4个末期高评价响应已混入后续角色事件收益，而后续确认该事件的数值净差为0；因此不能机械地把所有事件收益都推迟到它自己的`check_event`请求时施加。
