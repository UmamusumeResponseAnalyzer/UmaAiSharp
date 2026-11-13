namespace UmaAiSharp.Core.Goals;

public sealed class CompiledGoal
{
    enum MetricKind { Attribute, Deficit, Overflow, Rating, SkillPoints }
    readonly record struct Term(MetricKind Kind, TrainingAttribute Attribute, decimal Weight,
        MarginalCurve? Curve, decimal[]? Values);
    readonly AttributeConstraint[] constraints;
    readonly Term[] terms;
    readonly int[] offsets;
    readonly AttributeValues? maximumAttributes;
    public string Identity { get; }
    public int PriorityCount => offsets.Length - 1;
    public long SharedMemoryBytes { get; }

    internal CompiledGoal(GoalDefinition definition, AttributeValues? maximumAttributes = null)
    {
        Identity = definition.Identity;
        this.maximumAttributes = maximumAttributes;
        if (maximumAttributes is { } maximum)
            for (var i = 0; i < 5; i++)
                ArgumentOutOfRangeException.ThrowIfNegative(maximum[(TrainingAttribute)i]);
        constraints = definition.Constraints.ToArray();
        offsets = new int[definition.Priorities.Length + 1];
        var list = new List<Term>();
        var tables = new Dictionary<(MarginalCurve Curve, int Maximum), decimal[]>();
        var termCount = definition.Priorities.Sum(priority => priority.Terms.Length);
        // Includes retained objects plus construction lists/dictionary capacity in the reservation.
        var bytes = checked(256L + constraints.Length * 64L + offsets.Length * 8L + termCount * 256L);
        var curves = definition.Priorities.SelectMany(priority => priority.Terms)
            .Select(term => term.Metric).OfType<AttributeUtility>().ToArray();
        foreach (var curve in curves.Select(value => value.Curve).Distinct())
            bytes = checked(bytes + 64L + curve.Points.Length * 32L);
        if (maximumAttributes is { } tableBounds)
            foreach (var key in curves.Select(value => (value.Curve, Maximum: tableBounds[value.Attribute])).Distinct())
                bytes = checked(bytes + 64L + checked(key.Maximum + 1) * 16L);
        if (bytes > ExecutionMemoryPlan.LimitBytes)
            throw new InvalidOperationException("编译目标及预计算表超过8 GiB容量上限。");
        for (var index = 0; index < definition.Priorities.Length; index++)
        {
            offsets[index] = list.Count;
            foreach (var weighted in definition.Priorities[index].Terms)
            {
                var metric = weighted.Metric;
                var attribute = metric switch
                {
                    AttributeUtility value => value.Attribute,
                    AttributeDeficit value => value.Attribute,
                    AttributeOverflow value => value.Attribute,
                    _ => default
                };
                var kind = metric switch
                {
                    AttributeUtility => MetricKind.Attribute,
                    AttributeDeficit => MetricKind.Deficit,
                    AttributeOverflow => MetricKind.Overflow,
                    AttributeRating => MetricKind.Rating,
                    GainedSkillPoints => MetricKind.SkillPoints,
                    _ => throw new InvalidOperationException("目标指标未编译。")
                };
                var curve = (metric as AttributeUtility)?.Curve;
                decimal[]? values = null;
                if (curve is not null && maximumAttributes is { } bounds)
                {
                    var key = (curve, bounds[attribute]);
                    if (!tables.TryGetValue(key, out values))
                    {
                        var count = checked(key.Item2 + 1);
                        values = new decimal[count];
                        // Keep the original segment formula and decimal operation order for every integer value.
                        for (var value = 0; value < count; value++) values[value] = curve.Evaluate(value);
                        tables.Add(key, values);
                    }
                }
                list.Add(new(kind, attribute, weighted.Weight, curve, values));
            }
        }
        offsets[^1] = list.Count;
        terms = list.ToArray();
        SharedMemoryBytes = bytes;
    }

    public bool Evaluate(in TerminalMetrics result, Span<decimal> utilities)
    {
        if (utilities.Length != PriorityCount)
            throw new ArgumentException("效用缓冲区长度必须等于目标优先级数量。", nameof(utilities));
        Validate(result);
        if (result.FailedCareerGoalId.HasValue || !Feasible(result.Attributes)) return false;
        for (var priority = 0; priority < PriorityCount; priority++) utilities[priority] = Utility(priority, result);
        return true;
    }

    internal bool Accumulate(in TerminalMetrics result, Span<decimal> conditionalTotals,
        Span<decimal> unconditionalTotals, ref decimal violationTotal)
    {
        Validate(result);
        var violation = 0m;
        foreach (var constraint in constraints) violation += constraint.Violation(result.Attributes);
        violationTotal += violation;
        var feasible = violation == 0 && !result.FailedCareerGoalId.HasValue;
        for (var priority = 0; priority < PriorityCount; priority++)
        {
            var utility = Utility(priority, result);
            unconditionalTotals[priority] += utility;
            if (feasible) conditionalTotals[priority] += utility;
        }
        return feasible;
    }

    bool Feasible(AttributeValues attributes)
    {
        foreach (var constraint in constraints)
            if (constraint.Violation(attributes) != 0) return false;
        return true;
    }

    decimal Utility(int priority, in TerminalMetrics result)
    {
        var utility = 0m;
        for (var index = offsets[priority]; index < offsets[priority + 1]; index++)
        {
            ref readonly var term = ref terms[index];
            var metric = term.Kind switch
            {
                MetricKind.Attribute => term.Values is { } values ? values[result.Attributes[term.Attribute]]
                    : term.Curve!.Evaluate(result.Attributes[term.Attribute]),
                MetricKind.Deficit => result.Caps[term.Attribute] - result.Attributes[term.Attribute],
                MetricKind.Overflow => result.FutureOverflow?[term.Attribute]
                    ?? throw new ArgumentException("目标需要累计溢出，但轨迹没有经验证的溢出统计。", nameof(result)),
                MetricKind.Rating => result.VerifiedAttributeRating
                    ?? throw new ArgumentException("目标需要属性评价分，但轨迹未提供经验证的评分。", nameof(result)),
                MetricKind.SkillPoints => result.FutureGainedSkillPoints
                    ?? throw new ArgumentException("目标需要累计获得技能Pt，但轨迹没有对应统计。", nameof(result)),
                _ => throw new InvalidOperationException("编译目标包含未知指标。")
            };
            utility += term.Weight * metric;
        }
        return utility;
    }

    void Validate(TerminalMetrics result)
    {
        ArgumentNullException.ThrowIfNull(result);
        for (var index = 0; index < 5; index++)
        {
            var attribute = (TrainingAttribute)index;
            var value = result.Attributes[attribute];
            if (value < 0 || result.Caps[attribute] < value)
                throw new ArgumentException($"终局属性 {attribute} 的值或有效上限无效。", nameof(result));
            if (result.FutureOverflow is { } overflow && overflow[attribute] < 0)
                throw new ArgumentException($"属性 {attribute} 的累计截断值不能为负数。", nameof(result));
            if (maximumAttributes is { } maximum && value > maximum[attribute])
                throw new ArgumentException($"终局属性 {attribute} 超过已声明的目标预计算范围。", nameof(result));
        }
        if (result.FutureGainedSkillPoints < 0 || result.VerifiedAttributeRating < 0)
            throw new ArgumentException("获得的技能Pt或评价分不能为负数。", nameof(result));
    }
}
