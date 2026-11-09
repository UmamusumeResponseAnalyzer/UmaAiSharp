using System.Collections.Immutable;

namespace UmaAiSharp.Pioneer;

public enum MechanismEvidenceStatus
{
    SourceStructureVerified,
    ObservationsMatched,
    Contradicted,
    Unresolved,
}

public sealed record MechanismEvidence(string Id, MechanismEvidenceStatus Status, string Evidence, string Limitation);

public static class PioneerMechanisms
{
    public static ImmutableArray<MechanismEvidence> Construction { get; } =
    [
        new("facility-upgrade-edges", MechanismEvidenceStatus.SourceStructureVerified,
            "single_mode_11_facility_lv_up 的 43 行；394 对成功 planning 响应中的边均能查到。",
            "master 与包资源版本尚未绑定；不代表其他版本、全部机制或完整动作合法性。"),
        new("plan-order", MechanismEvidenceStatus.ObservationsMatched,
            "394 对成功 planning 的 priority 为连续 1..N；响应数组顺序并不等于 priority 顺序。",
            "样本均为 deck_id=3；没有覆盖所有合法计划。"),
        new("plan-cost-versus-planning-cost", MechanismEvidenceStatus.ObservationsMatched,
            "394 对成功 planning 中有 6 对 master 边费用总和低于 planning_cost。",
            "不得要求花满；完整提前确认规则和预算来源映射尚未核验。"),
        new("immediate-construction-at-point-threshold", MechanismEvidenceStatus.Contradicted,
            "正式核验的 18,747 个含完整所需字段的计划观测中，5,514 个不符合达到累计成本便立即更新设施的假设。",
            "另有仅含计划的部分响应，不能借用邻近回合补齐阶段；不得将即时阈值投影写入状态。"),
        new("settlement-at-current-points", MechanismEvidenceStatus.Contradicted,
            "story_id=400011107 消失后的 710 个相邻 check_event 观测，按当前 Pt 结算计划仅匹配 703 个。",
            "7 个反例及前后数值保存在 construction-settlement-evidence.json。"),
        new("settlement-at-reached-island-threshold", MechanismEvidenceStatus.ObservationsMatched,
            "同一批 710 个观测，用已达到的最高岛训练阈值结算累计成本范围内的设施，匹配 710 个。",
            "其中 705 个请求选择与前态事件匹配，2 个缺请求、3 个选择不同事件；未证明调度、完整事件或版本绑定。"),
        new("construction-ticket-observations", MechanismEvidenceStatus.ObservationsMatched,
            "上述 710 个响应的岛训练券为 0→1 共 618 次、1→1 共 92 次；last_gain_rights_num 在前后不变。",
            "仅覆盖 turn=5..59、deck_id=3；训练成员同时可能改变，尚无完整券事件转移规则。"),
        new("construction-event-400011108", MechanismEvidenceStatus.ObservationsMatched,
            "72 个明确请求对应的结算均在 turn=41、playing_state=1；设施分量和券数量候选均匹配。",
            "覆盖 deck_id=3、前态票数 0/1；其他事件效果和触发顺序未实现。"),
        new("construction-event-scheduling", MechanismEvidenceStatus.Unresolved,
            "710 个建设结算响应中，265 个推进回合、445 个不推进；后续事件队列有 156 种。",
            "尚未完成全部触发条件、队列优先级和多事件结算顺序反推；不得直接 pop 后返回普通训练。"),
        new("complete-plan-legality", MechanismEvidenceStatus.Unresolved,
            "官方 FAQ 说明存在低于预算仍可确认的情况；实包有低预算计划。",
            "重复设施、路线限制、全部确认条件仍需核验；提交动作明确失败。"),
    ];
}
