# Pioneer 已选事件与成员分量

本次接入依据建设/评审与事件历史的2026-11-08扩展研究。组件处理已经选定的事件，或已经明确给定的NPC身份结果；没有生成触发概率、完整事件顺序、成员抽样池或成员配置。

## Tucker 七类事件

`TuckerEventSettlement.ApplyObservedComponents`接受明确的事件、原始choice、支援配置及前态，返回体力/上限、干劲、羁绊、外出解锁和story_step的后态，以及技能Pt增量。仅覆盖支援30257、突破4、经验118185。数据来自1496个明确选择窗口：

| 故事 | 原始choice | 窗口数 | 回体（截断前） | 体力上限 | 技能Pt |
|---|---:|---:|---:|---:|---:|
| 809049004 | 1 | 216 | 32 | 0 | 13 |
| 809049011 | 0 | 215 | 32 | +4 | 0 |
| 809049012 | 0 | 216 | 32 | 0 | 0 |
| 809049013 | 1 | 217 | 80 | 0 | 0 |
| 809049014 | 0 | 215 | 40 | 0 | 0 |
| 809049015 | 0 | 200 | 48 | 0 | 0 |
| 809049021 | 0 | 217 | 24 | 0 | 0 |

体力先增加上限并封顶120，再恢复并按新上限截断；与通用事件共用`VitalRules`。120上限采用UmaAi公共模型；215个首次外出样本仅覆盖104→108与108→112，未覆盖封顶边界。干劲增加1、上限5。羁绊增加5，前态含8/愛嬌○时增加7，上限100。第三次外出的+80有12个未触顶观测，不能改写为“回满”。解锁允许前态羁绊低于60；本函数不据羁绊决定事件发生。

外出1..5要求前态已经解锁，story_step分别从0推进到5；解锁事件仅将is_outing从0改为1，节日事件保留进度。其他选择、等级、进度结构明确失败。五属性、Hint、幸運体質及发展Pt不在该分量中，不能把未处理字段当作收益0；400011111发展Pt沿用已有独立组件。

809049032不在这七类事件中。岛训练组件的`GrantTuckerSpecialty`只处理五张普通卡得意率+20的原子变化；该事件其他耐力、干劲及关系变化没有由本模块实现，不能把该原子变化当作完整Tucker事件。

## PJ 记录追加

`PioneerProjectMembers.ApplyObservedSelection`覆盖五张普通支援与Tucker的编成。初始化要求已有PJ集合为空、显式给定6个NPC身份，生成五个普通支援成员及六个NPC。可用链接角色1021、1027、1031、1040、1069在排除育成角色和编成角色后必须包含在结果中，不自动补人。

第二、第四期高评价要求前态分别有11、14个PJ成员，并显式给定3个新人身份。追加保留原成员，新NPC的target_id、training_partner_id取该身份，evaluation=0、is_appear=1、is_outing=0、story_step=0。660个窗口分为221个初始化、220个第二期和219个第四期。

输入的新人名单是一个外部已给定的结果，不是合法动作，也不是组件产生的随机样本。组件检查人数、重复和与育成/编成/既有PJ的重叠，不认证完整候选池及所有资格条件。自主模拟若没有经过验证的身份抽样规则，不能从实包后态取这份名单来生成未来。未更新岛训练配置；评审响应中的配置不含新人，不证明新人以后不能参加。

## 末期已发奖显示事件

`LateCheckpointConfirmation.ObserveAppliedBoundary`只能记录turn61/phase1、末期评审已经完整反映收益后出现的已观测角色事件：event3005、timing1、无选项，覆盖38个故事身份。`Confirm`只把该显示标记置为已确认，拒绝重复确认或身份/choice不符，不修改任何奖励字段，也不决定后续事件或育成结束。

220条连续确认的九项数值、Hint集合、技能和PJ集合均无变化。Hint数组重排按内容集合比较。9个末期高评价响应混入额外数值，但其内部归因与顺序尚不唯一；本次没有硬编码额外+10属性或+20技能Pt。

现有`CheckpointSettlement`只完成部分评审收益，不能独自证明已经到达这个完整发奖边界。因此该标记可由完整观测导入；自主模拟必须先补齐全部奖励结算，不能在只执行Pt/体力分量后调用它来跳过未知收益。此规则不适用于其他无选项事件。

## 建设触发边界

`ConstructionTriggerRules`补充回合40的比赛Pt过线：只产生turn41、故事400011108、timing1的待处理阈值，不立即插入可见队列。两条实包为源A的30177/30178/30179（295→317）和40863/40864/40865（294→316）。其他合宿比赛回合未覆盖；一次跨多个阈值继续停止。

扩大研究的1101个成功计划没有提供拒绝边界。既有完整计划确认仍未实现；不把“成本合法且预算用满或不能追加”提升为充分合法条件。

## 夹具与正式核验

`src/UmaAiSharp.Pioneer/Development/Evidence/export_event_component_fixtures.py`按研究引用重新读取扩大归一化文件，核对同源、同局、唯一引用、配对成员、endpoint、前态/请求/后态顺序和队首选择，再投影原始数值。支援角色及类型从只读master解析。输出不带runKey或账号身份：

- `tucker-friend-observations.jsonl`：1496行。
- `project-member-observations.jsonl`：660行。
- `late-checkpoint-confirmations.jsonl`：220行，其中9行标记混合评审边界。
- `event-component-provenance.json`：五个归一化输入、研究文件、master、脚本及夹具SHA-256。

```powershell
dotnet src/UmaAiSharp.Cli/bin/Debug/net10.0/UmaAiSharp.Cli.dll evidence event-components --input artifacts/research/blockers/island-training/expansion-20261108/audit --fixtures src/UmaAiSharp.Pioneer/Development/Evidence --output artifacts/evidence/pioneer-event-components-new
```

`PioneerEventComponentAudit.Run(inputDirectory, fixtureDirectory, outputDirectory)`先核对完整输入与夹具指纹，再调用上述产品规则并记录逐条候选结果。输入不匹配不能按同名alias关联；输出目录非空时拒绝覆盖。`CandidateOnly`和退出3表示限定分量核验，不表示完整育成通过。

2026-11-08产品CLI最终复跑报告为`artifacts/evidence/event-components-02/pioneer-event-component-audit.json`：Tucker1496/1496、成员660/660（初始化221、评审439）、确认220/220，其中9条属于混合评审边界，issues为0。该版本包含NPC与已有target_id冲突的拒绝校验。逐例结果保留在同目录`event-component-cases.jsonl`。

原始研究入口：`artifacts/research/blockers/construction-checkpoints/expansion-20261108/REPORT.md`及`delivery-02/`；`artifacts/research/blockers/events-history/mechanisms-20261108/REPORT.md`及`analysis-04/`。原研究只读解码核对了建设/评审相关4479条转移、12518个原包；本次夹具从对应指纹的归一化数据重新投影。全部材料已参与推导，没有独立留出验收。
