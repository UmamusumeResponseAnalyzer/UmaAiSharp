import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from ordinary_events import ordinary_supports, whole_run_reward


ORDINARY_COMMANDS = {101, 105, 102, 103, 106}
CAMP_COMMANDS = set(range(3601, 3606))
FIELDS = ('speed', 'stamina', 'power', 'guts', 'wisdom', 'skillPoints')
ACTION_ENDPOINTS = {'exec_command', 'shima_training_exec', 'planning', 'race_end'}


def rows(path):
    with path.open(encoding='utf-8') as stream:
        yield from map(json.loads, stream)


def file_hash(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def action(row):
    endpoint = row['endpoint'].rsplit('/', 1)[-1]
    fields = row['fields']
    if endpoint == 'shima_training_exec':
        return 'islandTraining', fields['current_turn']
    if endpoint == 'planning':
        return 'planningSubmission', fields['current_turn']
    if endpoint == 'race_end':
        return 'race', fields['single_mode_race_end_request_common']['current_turn']
    command = fields['single_mode_exec_command_request_common']
    turn, command_id = command['current_turn'], command['command_id']
    if command['command_type'] == 1:
        if command_id in ORDINARY_COMMANDS:
            return 'training', turn
        if command_id in CAMP_COMMANDS:
            return 'campTraining', turn
    return {
        (7, 701, 0): 'restConfirmation',
        (3, 0, 3606): 'campRest',
        (3, 0, 390): 'outing',
        (3, 0, 301): 'outing',
        (8, 801, 0): 'treatment',
    }.get((command['command_type'], command_id, command['command_group_id'])), turn


def phase(turn):
    return 4 if turn > 72 else (turn - 1) // 24 + 1


def fit(evidence, transitions, outcomes, split_path, master, calibration_path):
    observations = evidence / 'pioneer-observations.jsonl'
    actions = evidence / 'pioneer-actions.jsonl'
    exchanges = evidence / 'exchanges.jsonl'
    split = json.loads(split_path.read_bytes())
    fingerprints = {path.name: file_hash(path) for path in
                    (observations, actions, exchanges, transitions, outcomes, split_path, master, calibration_path)}
    if split['observationsSha256'] != fingerprints[observations.name]:
        raise ValueError('普通剧情拟合输入与冻结整局切分不匹配')
    groups = {run: 'fit' for run in split['fitRuns']}
    for run in split['holdoutRuns']:
        if run in groups:
            raise ValueError('普通剧情拟合组和留出组含有同局')
        groups[run] = 'holdout'

    paired = {row['key'] for row in rows(exchanges) if row['status'] == 'Paired'}
    action_keys = {row['exchangeKey']: action(row) for row in rows(actions)
                   if row['endpoint'].rsplit('/', 1)[-1] in ACTION_ENDPOINTS and row['exchangeKey'] in paired}
    classified = {(row['source'], int(row['after']['packetId'])): row for row in rows(outcomes)
                  if row['run'] in groups}
    cards, characters = ordinary_supports(master)
    rewards = {}
    reward_sources = defaultdict(Counter)
    for row in rows(transitions):
        after = row['after']
        if not after or after['runKey'] not in groups or row['excluded']:
            continue
        key = after['source'], int(after['packetId'])
        if key in classified and (reward := whole_run_reward(row, classified[key], cards, characters)) is not None:
            rewards[key] = reward

    frequencies = defaultdict(Counter)
    coverage = defaultdict(Counter)
    active = {}
    seen = set()
    last_run = {}

    def finish(run, window):
        if window is None:
            return
        key, sequence, valid = window
        group = groups[run]
        if not valid:
            coverage[group, *key]['interrupted'] += 1
            return
        frequencies[group, *key][tuple(reward for reward, _ in sequence if any(reward))] += 1
        for _, origin in sequence:
            reward_sources[group][origin] += 1

    for row in rows(observations):
        run, source = row['runKey'], row['source']
        if run not in groups:
            continue
        exchange = row['exchangeKey']
        if exchange in seen:
            continue
        seen.add(exchange)
        endpoint = row['endpoint'].rsplit('/', 1)[-1]
        packet = source, int(row['packetId'])
        if (previous := last_run.get(source)) is not None and previous != run:
            finish(previous, active.pop(previous, None))
        last_run[source] = run
        window = active.get(run)
        if endpoint in ACTION_ENDPOINTS or endpoint == 'finish':
            finish(run, active.pop(run, None))
            if exchange in action_keys:
                kind, turn = action_keys[exchange]
                if kind is not None and 1 <= turn <= 78:
                    active[run] = ((kind, phase(turn)), [], True)
            continue
        if endpoint != 'check_event' or window is None:
            continue
        if exchange not in paired or packet not in classified:
            active[run] = (window[0], window[1], False)
        elif packet in rewards:
            window[1].append(rewards[packet])

    for run, window in active.items():
        key, _, _ = window
        coverage[groups[run], *key]['unfinished'] += 1

    if not frequencies:
        raise ValueError('普通剧情拟合未找到完整行动窗口')
    calibration = json.loads(calibration_path.read_bytes())
    calibration['wholeRunOrdinaryRewards'] = [
        {'source': source, 'phase': stage, 'count': count,
         'rewards': [dict(zip(FIELDS, vector, strict=True), vital=0) for vector in sequence]}
        for (group, source, stage), counts in sorted(frequencies.items()) if group == 'fit'
        for sequence, count in sorted(counts.items())
    ]
    report = {
        'inputsSha256': fingerprints,
        'fitRuns': len(split['fitRuns']),
        'holdoutRuns': len(split['holdoutRuns']),
        'rewardSources': reward_sources,
        'strata': [{'source': source, 'phase': stage, 'group': group, 'complete': counts.total(),
                    'zero': counts[()], 'negative': sum(n for vector, n in counts.items()
                                                       if any(any(value < 0 for value in part) for part in vector)),
                    'meanRawReward': {field: sum(sum(part[i] for part in sequence) * n for sequence, n in counts.items())
                                             / counts.total() for i, field in enumerate(FIELDS)},
                    **coverage[group, source, stage]}
                   for (group, source, stage), counts in sorted(frequencies.items())],
    }
    return calibration, report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Fit five attributes and skill Pt from paired Pioneer stories')
    for name in ('evidence', 'transitions', 'outcomes', 'split', 'master', 'calibration', 'output'):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    calibration, report = fit(args.evidence, args.transitions, args.outcomes, args.split, args.master, args.calibration)
    args.output.mkdir(parents=True, exist_ok=True)
    for name, value in [('turn-calibration.json', calibration), ('ordinary-rewards-report.json', report)]:
        with (args.output / name).open('x', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
