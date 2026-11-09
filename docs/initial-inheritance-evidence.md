# 开局蓝、红因子分量

`InitialInheritanceRules` 计算六个明确谱系来源的开局蓝因子属性/上限增量，以及红因子适性提升。它不构造完整开局状态，不计算绿/白等其他因子，不抽取随机继承结果。

`InitialStateRules` 组合基础卡、支援初始属性和父母绿 Hint；相性另由 `InheritanceRelationRules` 计算，见[开局组合与相性分量](initial-state-evidence.md)。`EvaluateListedFactors` 仅计算快照明确列出的因子，不认证扩展字段完整性；原 `Evaluate` 入口继续拒绝缺失或非空扩展数组。

亲本原包的扩展数组平铺在完整亲本顶层，按 position_id 覆盖谱系。`InheritancePedigree.MapParent` 将本地 1/10/20 映射为当前三个有效来源；顶层显式空数组覆盖这三个槽位，祖辈缺少嵌套数组不构成输入缺失。此前按嵌套字段判断未知的结论已由 [221 局扩大研究](../artifacts/research/blockers/inheritance/expansion-20261108/REPORT.md)纠正。非空扩展只做结构解析，实际开局结算尚未放行。

## 已实现的计算

输入 master 投影来自 `succession_factor` 与 `succession_initial_factor`。计算读取传入的数值，没有把一次拟合结果写成隐藏常数。本地 master 的开局蓝因子表与[日文 Wiki 的开局继承表](https://wikiwiki.jp/sppenpen/%E5%9B%A0%E5%AD%90%E3%81%AE%E5%87%BA%E3%81%97%E6%96%B9)一致：

| 蓝因子星数 | 属性增量 | 对应属性上限增量 |
|---|---:|---:|
| 1 | 5 | 4 |
| 2 | 12 | 9 |
| 3 | 21 | 16 |

按属性目标分别累加六个谱系槽位的贡献；同一个因子 ID 出现在不同槽位仍分别贡献，不能把两个亲本共享的祖辈全局去重。属性目标 `1..5` 对应速度、耐力、力量、根性、智力。

红因子按同一种适性目标汇总星数，再查 master 的闭区间：

| 同适性总星数 | 未截断升档 |
|---|---:|
| 0 | 0 |
| 1–3 | 1 |
| 4–6 | 2 |
| 7–9 | 3 |
| 10–18 | 4 |

master 最后一档实际区间是 `10..999`；当前六来源、每来源星数 `1..3` 的输入范围最多 18 星。开局提升最高至 A（数值 7），已有更高输入不降低。输出同时保留总星数、未截断升档、提升前后等级和实际升档，不能将未截断增量当作已经应用的结果。

| 红因子 group/目标 | 含义 |
|---|---|
| 11 / 12 | 草地 / 泥地 |
| 21 / 22 / 23 / 24 | 逃 / 先行 / 差 / 追 |
| 31 / 32 / 33 / 34 | 短距离 / 英里 / 中距离 / 长距离 |

## 输入与输出边界

调用方式为 `new InitialInheritanceRules(factorMaster, initialMaster).Evaluate(sources, beforeAptitudes)`。

- `sources` 必须显式包含两个亲本及各自两个祖辈，共六个不重复 `InheritanceSlot`。不从缺失来源生成默认因子。
- 每来源必须有显式 `FactorIds` 和 `ExtendedFactorIds` 数组。非空扩展因子的合并规则未实现，明确抛出 `UnknownMechanismException`；不忽略、不自动替换。
- 当前覆盖的来源结构是恰好一蓝、一红。缺失、多项或其他结构需要新的证据，不自动推断。
- 每个因子 ID 必须可在传入 master 解析。已知的其他类型只进入 `UncalculatedFactors`，不算成零效果；新增或未解析因子留待后续机制实现。
- `beforeAptitudes` 必须显式提供全部十项适性。调用方负责提供继承前的值，不能把已经继承的响应后态再次传入。
- `Blue` 只有蓝因子增量，尚未加角色稀有度基础、剧本基础上限或支援初始属性。`Red` 是这一个开局分量对输入适性的变换。完整角色初态需要组合上述开局分量。

这是纯计算组件。原始 DTO 到六来源的证据解析、master 文件读取和完整开局组合不藏在此 API 内。

## 当前本地证据与未覆盖部分

[221 局扩大研究](../artifacts/research/blockers/inheritance/expansion-20261108/REPORT.md)覆盖蓝星 1/2/3 来源 341/858/127 个、直接父母绿星 1/2/3 来源 10/271/10 个。[initial-state-audit-03](../artifacts/evidence/initial-state-audit-03/initial-state-audit.json)从原库重新选择 442 个开局前最新亲本并调用当前 C# 组合组件，五属性、五上限、十适性和父母绿 Hint 各 221/221 匹配；技能、支援羁绊及客户端相性也各 221/221 匹配。祖辈缺少嵌套扩展字段的问题已由完整亲本顶层数组解析纠正，221 局六来源输入完整。

这些匹配包含 127 个蓝星 3 来源和 10 个直接父母绿星 3 来源，属于开局组合覆盖，不能把总值匹配拆成每个来源的独立效果验收。研究另外分离出途中蓝因子效果 181 个，其中星 3 有 29 个、来自 24 局；实际增量档位为 +10/+13/+16/+19/+22。这是**途中**效果证据，不是开局蓝星 3 的 +21 规则，也没有认证未见档位概率为零。途中随机抽样尚未接入。

221 局含此前 79 局及新增分析的 142 局，同样参与机制推导，不是独立留出集。正式核验保留一个源库与研究冻结时哈希不同的问题；本次原始输入读前后指纹稳定、七组比较均无反例，详见[开局组合核验说明](initial-state-evidence.md)。

## 历史 79 局证据

[研究脚本](../artifacts/research/initial_inheritance.py)通过开局前的殿堂快照和此前完成育成的快照补齐六来源。[study-04 报告](../artifacts/evidence/initial-inheritance-study-04/summary.json)对开局请求、响应和此前终局应用配对筛选，要求所用亲本快照在同一来源库的开局请求之前。底层探针的输入指纹与原始材料核验记录位于 [inheritance-probe-02/provenance.json](../artifacts/evidence/inheritance-probe-02/provenance.json)。

| 检查 | 结果 | 能证明的范围 |
|---|---:|---|
| 五项完整开局上限预测 | 79 / 79 匹配 | 本地角色稀有度基础 + 剧本基础 + 蓝因子上限分量的组合与这些响应相符 |
| 十项开局红适性预测 | 79 / 79 匹配 | 所用六来源与基础适性下的红因子分量吻合 |
| 蓝星 1 来源 | 154 | 上限观测覆盖 |
| 蓝星 2 来源 | 320 | 上限观测覆盖 |
| 蓝星 3 来源 | 0 | 仅指此历史 79 局；扩大研究已补充上述 127 个来源 |
| 蓝因子属性净增量 | 未独立比较 | 开局属性同时受基础属性、支援和其他效果影响，不能从总属性匹配偷换成分量验收 |

研究脚本导出了这 79 个样本的六来源及继承前适性，再由独立 C# 研究入口读取本地 master，直接调用 `InitialInheritanceRules`。上限和红适性各 79/79 匹配，结果见 [csharp-validation.json](../artifacts/evidence/initial-inheritance-study-04/csharp-validation.json)。它检查的是实际组件的两个分量，不是完整开局回放；蓝属性及其他因子没有被偷换成已验证。以上材料不是独立留出集。`study-01/02` 的早期筛选边界不作为当前结论依据。

使用本地 master 的 SHA-256：`4538336047066a76f4723d942010ac6fa3f21c958cbf5cf6b99642f97add3886`。用户允许本地材料研究，不要求与捕获版本严格绑定；这不扩大星数、卡池或机制的观测覆盖。

`InitialInheritanceRules` 的职责只有开局蓝/红分量。父母绿 Hint 与相性/G1 确定性计算由 `InitialStateRules`、`InheritanceRelationRules` 实现；这不表示四月随机继承、白因子抽样、满 Hint 转换、非空扩展因子的实际结算或完整开局事件已经完成。Wiki 页同时含这些内容，其随机率没有进入组件。

本地 WinSaddleAnalyzer 的 `CalculateRelation`、`SumWinSaddles` 与 `CalculateProper` 可作为相性/G1 与红因子概率候选：星数对应 1/3/5，再乘相性加成；它计算一次或两次继承的合并概率，不生成完整继承事件及实际增量。没有把参考代码当作服务器规则。日本公开工具也明确其概率依据公开专利等推导、实际概率未公开，见 [Umamusume DB 2026-03-28 说明](https://uma.pure-db.com/en-us/release-notes)。这些候选仍需用明确机会和完整来源条件独立核验。

## 代码与验证

[InitialInheritance.cs](../src/UmaAiSharp.Pioneer/Inheritance/InitialInheritance.cs)、[InitialInheritanceTests.cs](../tests/UmaAiSharp.Tests/InitialInheritanceTests.cs)。

```powershell
dotnet test tests/UmaAiSharp.Tests/UmaAiSharp.Tests.csproj --no-restore --filter FullyQualifiedName~InitialInheritanceTests -m:1
```

14 项测试通过：六槽重复因子仍各自贡献、蓝星 1/2/3 的两个分量、红总星的区间边界、A 上限、保留更高适性、缺来源、重复槽位、缺适性、未知因子、重复因子和未实现扩展均覆盖。构建有 `NU1900` 漏洞源访问告警，未完成在线依赖漏洞核查。
