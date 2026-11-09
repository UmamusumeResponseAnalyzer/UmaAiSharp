# 人员联合分配

`PioneerJointPlacement`接受六个明确支援卡位及登场状态、PJ成员身份和单人分布，按身份排序生成逐成员位置与Hint，以及五项训练的支援掩码、Hint掩码和PJ人数。`TrainingBoard`为无托管引用的值类型，由每条轨迹独占；决策查询返回值副本。

当前成员类型为普通支援、Tucker和PJ成员。理事长、记者、其他特殊人员及多位置支援尚未接入此生成器。导入的真实面板保留已揭示的位置；训练后事件与回合边界接通后，才能在下一回合的正确时点调用联合生成。

## 位置与Hint顺序

1. 按支援卡位抽取一次本回合Hint标志；未登场的卡跳过，确定为0或1的概率不抽数。
2. 按成员身份顺序抽位置。缺席为-1；0..4分别为速度、耐力、力量、根性、智力。
3. 若任一训练超过五人，完成本次全部位置抽样，然后重抽整个面板；Hint标志保持不变。
4. 接受面板后，仅出席的普通支援显示已抽中的Hint。结果缓冲由调用方独占，采样不分配托管对象。

整板重抽与Hint先于位置刷新采用固定提交[umasim的SimulationStateUpdater.kt](https://github.com/mee1080/umasim/blob/c97dede4efd71af9606cac0342f5f03944a41e74/core/src/commonMain/kotlin/io/github/mee1080/umasim/simulation2/SimulationStateUpdater.kt)的时序解释。本地72,680个设施观察最多五人，与容量限制相容；最终面板不能区分服务器的重抽、截断或人员优先顺序。单人`Sample`保持原有的位置后Hint消费顺序；联合生成单独标记为`pioneer-joint-placement-v1`。

## PJ分层参数

PJ五个位置等权，每项权重100；缺席权重为`500 × 缺席数 / 出席数`，编译时约分为整数。机会只包含当前明确的已登场PJ成员，不把未加入的角色计为缺席。

| 模式 | 海之家参数 | 机会 | 出席 | 缺席 | 缺席率及按育成聚类的95%近似区间 |
|---|---:|---:|---:|---:|---|
| 普通 | 0 | 4,908 | 4,416 | 492 | 10.02%，9.18%–10.87% |
| 普通 | 10 | 97,587 | 87,848 | 9,739 | 9.98%，9.78%–10.18% |
| 合宿 | 10 | 25,839 | 24,255 | 1,584 | 6.13%，5.82%–6.44% |

材料来自221局、14,530个上下文唯一的完整面板，剔除原14,536面板中6个多义记录。各组覆盖221局；按每局缺席残差计算聚类标准误，再取正态近似区间。这批材料参与了参数选择，没有独立留出。计数及输入SHA256见[机会统计](../artifacts/implementation/joint-placement-01/npc-opportunities.json)，[脚本](../artifacts/implementation/joint-placement-01/npc-opportunities.py)只读冻结观测文件。

表中频率定义容量条件化之前的候选权重；整板重抽会改变最终边际分布。包含全部人员的回合生成链接通后，需要检验最终联合分布，不能把输入频率作为联合分布已通过验证的证据。

参考实现把客串角色映射到R卡，源码保存在[Store](../artifacts/implementation/joint-placement-01/sources/Store.kt)与[SupportCard](../artifacts/implementation/joint-placement-01/sources/SupportCard.kt)。本地PJ得意位置相对每个非得意位置的观察比约0.9995，因此采用位置等权模型，未直接移植R卡得意率。

## 执行与验证

`PioneerStateHeader.TrainingBoard`保存已揭示面板，`KnownParts.TrainingBoard`表示该事实存在；决策视图缺失时返回null。预览、发展Pt、羁绊和Hint候选从状态面板读取，静态数值来自规则配置。`CopyInto`及精确分叉同时复制面板。

普通与合宿输入在统一编译边界检查支援加PJ最多五人；岛训练使用自身规则。规则身份为`pioneer-runtime-components-v12/pioneer-training-turn-v12`，单人分布为`pioneer-placement-20261108-v2`。

测试覆盖独立整板重抽参考、Hint每回合只抽一次、种子与RNG末态、成员输入顺序、1/2/4/8/16/32 worker及前缀、零分配，以及生成面板经正式训练入口执行。原有普通/合宿实包预览与执行测试继续覆盖状态面板改造。
