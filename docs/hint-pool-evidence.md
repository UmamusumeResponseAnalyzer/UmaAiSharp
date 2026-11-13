# Hint 有效奖池与条件奖励

`HintRewardPool` 实现明确输入下的有效奖池，不产生 Hint 出现、领奖对象、奖项或等级增量的随机结果。候选按 master 的 `(group_id, rarity)` 去重；已记录 Lv5 排除，已学同组白／金技能排除白技能候选。没有 Hint 记录保留为 `null`，不伪造 Lv0。其他候选稀有度及同组已学稀有度明确停止。

属性奖励使用显式选定的单个 `single_mode_hint_gain` 奖励组。1200 以下部分按 1:1，以上部分按整数除 2、非零部分至少 1 点处理，再截断至上限；技能 Pt 直接增加。不同候选组不能相加，净变化为零也不能直接判为属性奖励。

`ApplyAttributeReward`返回`HintAttributeReward`：`Resources`为后态，`UncappedGains`为1200换算后、上限截断前的显示增量，`Overflow`为被属性上限截断的部分。正式Hint事件入口将其累加至路径指标；例如属性1800已到上限、奖励6时，实际增加0、截断前增加3、溢出3。规则版本为`jp-pioneer-white-hint-effective-pool-v2`。

## 原包复核

[C# 报告](../artifacts/evidence/hint-pool-audit-02/hint-pool-audit.json)从冻结的云端 MessagePack 导出独立解码前态、请求与后态，重新读取只读 master，调用产品组件。研究定位和完整输入均锁定 SHA-256；不读取在线数据库。前后 Hint 均检查等级和唯一性，同时改变已学技能的窗口不按单次 Hint 奖励验收。

| 项目 | 结果 |
| --- | ---: |
| 明确领取窗口 | 2,147 / 2,147 |
| 技能 Hint / 属性奖励 | 1,794 / 353 |
| 已学技能与原池重叠的窗口 | 308 |
| 有效池为空的窗口 | 16，全部属性奖励 |
| N=2 有效池 | 58，其中 21 次属性奖励 |
| SID 链 / 研究服务端时间唯一配对 | 67 / 2,080 |
| 输入或结果反例 | 0 |

云端数据来自 193 局、17 个新账号，与本地研究账号和育成身份不重叠；但这些数据已用于本轮解释选择，不能称为未触碰的独立留出集。2,080 个时间配对继承冻结研究的完整时间线判定，C# 核验没有重新实现全量时间线唯一性搜索；这与独立解码原包是两个不同的验证边界。

本地与云端 N=2 合计 38/136，仍不足以认证通用 `1/(N+1)` 概率。已学技能过滤与重新抽取等机制可能产生相同的有效池，不能据此确定服务器过滤时点。岛训练多个 Hint 的对象选择、相关性和领取顺序也未由本组件实现。

来源：[云端扩展报告](../artifacts/research/blockers/support-placement-hint/cloud-20261108-01/REPORT.md)、[原始导出复核](../artifacts/research/blockers/support-placement-hint/cloud-20261108-01/comparison-verified.json)。本轮按用户授权使用这些资源版本，版本不一致保留为来源信息。

## 运行

```json
{
  "name": "evidence_hint_pool",
  "arguments": {
    "researchDirectory": "artifacts/research/blockers/support-placement-hint/cloud-20261108-01",
    "masterPath": "<master.mdb>",
    "outputDirectory": "<新目录>"
  }
}
```

命令返回 `3`，表示完成限定分量核验。测试覆盖奖池去重、缺失记录、Lv5／已学排除、空池、1200／上限、原始导出配对与非法中奖反例；抽取概率不在这组测试范围内。
