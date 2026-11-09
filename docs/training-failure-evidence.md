# 已识别训练失败分量

`TrainingFailureSettlement` 处理明确给定的失败事件和已识别分支。它不抽取失败、不生成失败 story，也不把选项 1 固定成唯一结果。当前范围为 card104501 的合宿耐力3602/event211087/story400011112，以及 card104503 的普通耐力105/event7043/story501045713；两个事件均 timing6，六张支援均已满羁绊。

`BeginResolvedFailure` 将耐力执行次数加1，进入等待失败选择的阶段，五属性、体力、技能Pt、干劲与回合保持原值。它不执行成功预览，也不结算训练等级跨阈值等未建模分量。`ResolveChoice` 要求原始选择1及明确的“耐力−5、干劲−1”分支：耐力减5；正向思考25存在时抵消干劲下降并消耗效果，否则干劲减1。之后推进一回合，返回 `NextTurnEventsRequired`。低属性/低干劲、耐力1200以上的负向结算、未满羁绊、其它事件组合和其它分支明确拒绝。

失败事件与计算状态均为无托管引用的值类型，羁绊通过只读span传入。正向思考与通用事件资源使用相同的`MotivationRules`，资源和效果消耗共同提交。该行为采用master效果25描述和UmaAi干劲规则；普通／合宿的构造边界验证覆盖中间分叉、效果移除后的后续状态段、重复继续及1..32 worker。规则版本为`jp-pioneer-observed-stamina-failure-v2`，正式CLI反例复测见[执行记录](../artifacts/implementation/failure-effects-01/verify-reproducible-02/verification.json)。

## 两个原包窗口

完整`PioneerStart.TrainingTurn`通过同一正式训练动作接入这两条路径：`trainingFailure`条件结果提供该动作的失败事件，`failurePenalty`对应下一次选择。普通与合宿示例由52989和58275原始前态重新导入；五项协议次数和耐力计数同步增加一次。`PioneerTrainingFlowTests`在失败选择处精确分叉，再核对选择后实包投影、重复继续、RNG和1/2/4/8/16/32 worker结果。来源指纹见[本次输入](../artifacts/implementation/training-transition-01/inputs.json)，CLI示例见[执行说明](pioneer-cli.md#失败条件回放)。

[mechanisms-15/REPORT.md](../artifacts/research/blockers/training-settlement/mechanisms-15/REPORT.md)的两例来源于同一已冻结库。两次选项展示均为选项1两组、选项2三组，没有分支概率；本模块只接受已经明确的分支身份。

| 内容 | 合宿耐力 | 普通耐力 |
|---|---:|---:|
| 前态 / 立即响应 / 选择响应 | 52989 / 52991 / 52995 | 58275 / 58277 / 58281 |
| 回合 | 37 → 37 → 38 | 30 → 30 → 31 |
| 耐力执行次数 | 8 → 9 → 9 | 1 → 2 → 2 |
| 耐力 | 795 → 795 → 790 | 535 → 535 → 530 |
| 干劲 | 5 → 5 → 4 | 5 → 5 → 4 |
| 体力 | 80 → 80 → 80 | 65 → 65 → 65 |

2026-11-08 通过 [training_failure_fixture.py](../artifacts/research/training_failure_fixture.py)独立重新读取原始 SQLite：每例连续7包、共14个 payload 的 SHA-256 与 `verified-01/verification.json` 锁定记录逐个一致。重新检查唯一请求/响应配对、端点、严格时间顺序、参与者、版本、同局、就绪前态、实际训练与事件选择。完整角色对象的即时差只有 `playing_state`，选择后的差只有耐力、干劲、回合与 `playing_state`；即时 Pioneer 对象只有训练次数变化。

源库 SHA-256 为 `ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b`，只读打开，读前后一致。[training-failure-snapshots.json](../tests/UmaAiSharp.Tests/Fixtures/training-failure-snapshots.json)保存两例状态投影、事件身份、包号/端点和来源哈希。前态角色效果`[12,26]`由相同原包的完整导入补入，原始投影逐字节复原后哈希保持`8aa76a36…b2aaec`；补充后的夹具SHA-256为`ed26048117b2327287cc3704007430e86250ba0ea3d03cf79caa40b8f018863f`。转换来源及哈希见[记录](../artifacts/implementation/failure-effects-01/fixture-provenance.json)。`TrainingFailureTests` 分别核对两个阶段，并检查重复操作和未支持结果。

本轮最终集中测试 234/234 通过，包含上述两例原包夹具；记录见 [integration.trx](../artifacts/validation/research-followup-20261108-03/integration.trx)。这是组件测试，不能代替完整失败机制或自主育成验收。

原始窗口投影导出到新文件；补充字段与CLI的可重复核验使用第二条命令，输出目录同样须不存在：

```powershell
python -B artifacts/research/training_failure_fixture.py --output artifacts/evidence/training-failure-fixture-new.json
python -B artifacts/implementation/failure-effects-01/verify.py artifacts/validation/failure-effects-new
```

## 未认证范围

研究的76个显示2%风险动作中失败2次，集中于9局、两种衣装。它不是通用概率估计，也不能区分显示整数率和内部抽样精度。两个支援队伍均满羁绊，所以不变的可见羁绊不能证明原始羁绊增量为零。没有计数跨四次阈值的失败样本；没有智慧/其它训练的完整失败、其它选择分支、完整下回合事件或其权重。两例同样参与实现推导，不是独立留出集。
