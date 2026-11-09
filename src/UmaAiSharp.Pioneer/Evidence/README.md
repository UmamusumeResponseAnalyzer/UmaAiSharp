# Pioneer 建设证据

这些文件保留源文件 SHA-256、记录 ID 和核验所需的游戏数值；不包含账号、会话或育成角色标识。源文件哈希用于定位本地原始证据，不表示数据已与游戏资源版本绑定。

- `planning-observations.json`：开拓 Pt 达到累计设施成本后，设施仍未变化的响应及后续结算响应。
- `planning-cost-evidence.json`：394 对成功 planning 请求/响应的统计，以及 6 对低于 `planning_cost` 的计划。请求中的 `prev_state_id=0` 不能解释为设施初始状态。
- `construction-settlement-evidence.json`：710 次相邻 `check_event` 观测的统计，以及当前 Pt 结算假设的 7 个反例。核验只比较设施分量：按已达到的最高 `gain_shima_training_pt_array` 阈值结算计划累计成本，710 次匹配。
- `construction-ticket-evidence.json`：同批观测的券数量、回合与后续事件队列，以及 4 组匿名数值示例。其中 705 次请求选择与前态事件 ID 匹配，2 次缺请求、3 次选择不同事件；不能将 710 次全部解释为直接事件因果验证。
- `construction-trigger-evidence.json`：建设事件显示与阈值计数的统计、2 个 Pt 不变的显示反例，以及 `last_gain_rights_num` 从 2 降至 1 的示例。
- `construction-return-evidence.json`：400011108 的 72 个请求对应结算，记录券数量、阶段及后续事件。
- `construction-component-inventory.json`：正式复跑的来源、两个已实现候选分支及全部可观察设施/券变化分组；未对应到请求的差异不当作已证明的规则。
- `construction-window-evidence.json`、`construction-window-fixtures.jsonl`：两个延迟显示原始窗口、6 个未归因设施变化的缺包清单、769 条连续窗口夹具及 2 条跨局部响应的关联证据。关联证据不计入严格夹具。
- `construction-master.jsonl`：窗口组件所需的本地 master 表行；来源哈希记录在窗口证据中。
- `audit_construction_windows.py`：只读原库和标准化输入，复跑待建设窗口核验；不输出原始身份或 payload。

`ConstructionEvidence.CheckImmediateCompletion` 和 `CheckObservedSettlement` 是只读假设核验器。后者的调用方必须先确认同一局的相邻响应及 `check_event` 路由；方法检查前后事件队列、计划、Pt 和阈值。它不触发事件，不发券，不推进育成。重复设施计划尚无样本，核验器明确拒绝据此推定结果。

`PioneerConstructionRules.ApplySettlementComponentsCandidate` 使用本地 master 成本，更新设施及岛训练券数量这两个分量，覆盖 `deck_id=3`、明确持有 0/1 张券及唯一事件：400011107 / turn 5..59 / playing_state 5，或 400011108 / turn 41 / playing_state 1。它保留待处理事件和回合；成员分配尚未实现，调用它不等于完成该事件。来源哈希和包版本用于差异归因，版本未严格绑定不阻止本地规则核验。

当前已实现数据读取、计划顺序、master 边成本分析、局部设施结算和上述观测核验。完整动作合法性、事件调度、普通/岛训练、属性单位与截断、支援卡和人物事件、技能购买、继承、比赛与终局结算均不由这些证据证明。`ValidateForSubmission` 在完整条件未核验时明确失败，没有提供可完成整局的模拟器。

正式复核入口为 `evidence construction --input <evidence audit 输出目录> --output <新目录>`。它复用已脱敏的观测、请求配对与 master 导出，分别输出快照反例和局部转移核验；输入里的缺字段、孤包或未知规则不会被补零。
