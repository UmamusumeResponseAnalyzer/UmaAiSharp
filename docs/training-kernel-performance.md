# 训练数值核

`TrainingFormulaCandidate.GenerateOrdinary/GenerateCamp/GenerateIsland` 保留审计接口、精确 BigInteger 有理数与 `RawGains`。`TrainingNumericKernel` 提供同一候选规则的纯数值结果，供运行时执行与性能测量使用。

## 接口与输入边界

```csharp
var deck = new CompiledTrainingDeck(definitions);
TrainingNumericResult gains = TrainingNumericKernel.GenerateOrdinary(input, deck);
// GenerateCamp(CampTrainingInput, deck) / GenerateIsland(IslandTrainingInput, deck)
```

三个方法也提供不带 `deck` 的入口，显式逐次解析原始支援定义。两组入口都返回包含 `Common`、`Scenario`、`UncappedCommon` 与 `UncappedScenario` 的值类型。编成定义包括卡片、等级、位置、训练类型以及原子效果；动态羁绊取自本次输入，粉丝数和训练等级条件按本次局面计算。

编成对象不可变，可以跨 worker 共享。它预解析普通效果和固有效果中的静态前缀；遇到依赖羁绊、训练等级或粉丝数的效果后，剩余原子按原顺序处理。静态定义与本次输入不匹配会明确报错，不能继续使用过期编成。相同内容的重新反序列化数组可通过校验；不要求数组对象身份相同。

`Common/Scenario` 是经过单层收益限制、1200换算和属性上限截断后的实际增量；`UncappedCommon/UncappedScenario` 保留1200换算后、属性上限截断前的显示增量。`Overflow` 汇总两层的上限截断差额，正式执行将其累积到 `FutureOverflow`。未量化分数与量化前后说明由参考接口提供。数值核不生成随机事件、人员分配、失败结果，也不推进回合。

## 算术等价与分配

- 参考路径保留约分后的分子／分母字符串；数值路径不计算这条额外的未量化乘积，不创建 `ExactTrainingGain`、诊断数组或字符串。
- 数值路径使用不约分的 BigInteger 分数。乘法保留精确分子和分母；十进制有效位量化由整数除法完成，量化后的分母为十的幂。约分不改变分数值，因此不需要在每次乘法中执行 GCD。
- 量化顺序保持为：基础与成长／友情／训练乘积保留六位有效数字后截断，人数乘积六位截断，海之家乘积六位截断，干劲乘积五位四舍五入，最后截成整数。每一步与参考路径对拍。
- 岛训练的友情、训练效果和干劲效果继续按每个原子应用位置比例与整数截断；不先合并普通／固有同类效果。
- 六维临时结果使用栈上 `Span<int>`，逐属性读取直接访问值类型字段；设施完整性验证使用六位集合，保留六个唯一设施、海之家等级、技能路线和未知效果的拒绝条件。

这不是零分配实现。BigInteger 在大数运算中仍可能分配，参与支援数组和包含动态效果的解析结果仍会创建对象。现有输入契约未限定原子个数与全部数值乘积，不能证明 `long` 或 `Int128` 足以覆盖；本轮没有定宽溢出降级、浮点近似或 epsilon 修正。

## 验证

`TrainingNumericKernelTests` 对三种模式各构造300组确定性输入，逐一比较参考结果、不带编成的数值结果与带编成的数值结果。输入变化包括训练等级、干劲、粉丝、羁绊阈值、参与者子集、设施效果、黄金船已解析效果及1200／上限附近状态。这是实现等价性测试，不是新的游戏实包证据。

额外检查包括岛训练原子截断、动态未知效果、静态定义变化拒绝、共享编成的并发隔离，以及128个友情原子产生超出128位的精确分母。原 `TrainingFormulaTests` 的实包22.99968整数边界和1200分层边界同时核验数值入口。既有实包夹具未修改。

原 Release 对照程序集位于 `artifacts/performance/engine-baseline-01`。基准须分别报告参考诊断入口、纯数值入口和编成数值入口，并将编成构建移出重复计算区间，另报其一次性成本。参考输出的诊断字段与数值输出不同，不能把全部差异称为纯算术提速。

首轮集中 Release 构建通过，`artifacts/validation/engine-implementation-01/engine.trx` 记录252/252测试通过。`TrainingFormula.cs` 与 `TrainingRational.cs` 的文件SHA和基线清单完全一致。

全实包数值对拍与性能结果以集中验证报告为准；未测量前不声明提速倍数或分配降低比例。
