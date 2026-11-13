import argparse
import collections
import json
import math
from pathlib import Path


def rows(path):
    with path.open(encoding='utf-8') as stream:
        for line in stream:
            yield json.loads(line)


def fit(research, split, output):
    frozen = research / 'frozen-01'
    partition = json.loads(split.read_bytes())
    fit_runs = set(partition['fitRuns'])
    holdout_runs = set(partition['holdoutRuns'])
    if fit_runs & holdout_runs:
        raise ValueError('继承拟合和留出组有同局')
    included_runs = fit_runs | holdout_runs
    factors = {}
    effects = collections.defaultdict(set)
    for record in rows(frozen / 'master-rows.jsonl'):
        row = record['row']
        if record['table'] == 'succession_factor':
            factors[row['factor_id']] = row
        elif record['table'] == 'succession_factor_effect':
            effects[row['factor_group_id']].add(row['effect_id'])

    # Preserve the reference classes and fit each observed type/effect group against its
    # actual opportunities. A prior with 100 equivalent opportunities stabilizes small strata.
    reference_rates = {1: (7000, 8000, 9000), 2: (100, 300, 500), 3: (500, 1000, 1500),
                       4: (300, 600, 900), 5: (100, 200, 300), 6: (300, 600, 900)}
    samples = collections.defaultdict(collections.Counter)
    holdout = collections.defaultdict(collections.Counter)
    for row in rows(frozen / 'opportunities.jsonl'):
        if row['runKey'] not in included_runs:
            continue
        group = factors[row['factorId']]['effect_group_id']
        target = samples if row['runKey'] in fit_runs else holdout
        target[row['type'], group][row['relationDistinct'] + row['g1Bonus'], row['triggered']] += 1
    rates = {}
    report = []
    for factor in factors.values():
        kind, group, stars = (factor[key] for key in ('factor_type', 'effect_group_id', 'rarity'))
        if kind == 7:
            continue  # Special conditions in succession_factor_addon_effect, not lottery entries.
        if (kind, group) in rates:
            continue
        reference = reference_rates[group // 10][stars - 1]
        observations = samples[kind, group]
        estimate = reference
        if observations and kind != 1:  # Blue probabilities are already near the saturation boundary.
            prior = min(0.99, reference * 170 / 1_000_000)
            def loss(basis_points):
                q = min(0.999999, basis_points * 170 / 1_000_000)
                score = 100 * (prior * math.log(q) + (1 - prior) * math.log1p(-q))
                for (relation, triggered), count in observations.items():
                    p = min(1.0, basis_points * (100 + relation) / 1_000_000)
                    score += count * math.log(max(1e-12, p if triggered else 1 - p))
                return -score
            estimate = min(range(25, max(2500, reference + 500) + 1, 25), key=loss)
        # The calibration key includes type. Inherited and ordinary factors can share effect_group_id.
        rates[kind, group] = estimate
        evaluation = holdout[kind, group]
        if observations or evaluation:
            report.append(dict(factorType=kind, effectGroup=group, reference=reference, fitted=estimate,
                fitOpportunities=observations.total(), holdoutOpportunities=evaluation.total(),
                holdoutObserved=sum(count for (_, triggered), count in evaluation.items() if triggered),
                holdoutExpectedReference=sum(count * min(1, reference * (100 + relation) / 1_000_000)
                    for (relation, _), count in evaluation.items()),
                holdoutExpectedFitted=sum(count * min(1, estimate * (100 + relation) / 1_000_000)
                    for (relation, _), count in evaluation.items())))

    frequencies = collections.Counter()
    # Blue effects are observed only when the factor's entire stat/cap bundle is isolated.
    for row in rows(frozen / 'isolated-blue.jsonl'):
        if row['runKey'] in fit_runs and row['match']:
            frequencies[(10 + row['stars'], (row['stat'] - 1) // 3 + 1)] += 1

    # For other factors use the compatible complete master-bundle assignments as a weak prior.
    # Each assignment is a witness, not an observed effect_id.
    for row in rows(research / 'joint-random-01/joint-results.jsonl'):
        if row['runKey'] not in fit_runs or row['status'] != 'compatible':
            continue
        for (_, factor_id), (effect, _) in zip(row['factorOrder'], row['candidateEffectIds'], strict=True):
            group = factors[factor_id]['effect_group_id']
            if group // 10 != 1:
                frequencies[group, effect] += 1
    pairs = sorted({(factor['effect_group_id'], effect) for factor in factors.values()
                    if factor['factor_type'] != 7 for effect in effects[factor['factor_group_id']]})
    result = {
        'rates': [{'factorType': kind, 'effectGroup': group, 'basisPoints': basis_points}
                  for (kind, group), basis_points in sorted(rates.items())],
        'weights': [{'effectGroup': group, 'effectId': effect, 'count': frequencies[group, effect] + 1}
                    for group, effect in pairs],
        # shortcut: 1–5 point hypothesis spans isolated conversion constraints. Replace after
        # fitted complete Hint alternatives identify their amount and target weights.
        'saturatedHintWeights': [1, 1, 1, 1, 1],
    }
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    with output.with_suffix('.report.json').open('x', encoding='utf-8', newline='\n') as stream:
        json.dump({'strata': report, 'effectWitnesses': dict((f'{group}:{effect}', count)
            for (group, effect), count in sorted(frequencies.items()))}, stream, indent=2)
        stream.write('\n')
    print(f'{sum(sum(v.values()) for v in samples.values())} factor opportunities, {sum(frequencies.values())} effect assignments')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Compile Pioneer inheritance frequencies from fitting runs')
    parser.add_argument('research', type=Path)
    parser.add_argument('split', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    fit(args.research, args.split, args.output)
