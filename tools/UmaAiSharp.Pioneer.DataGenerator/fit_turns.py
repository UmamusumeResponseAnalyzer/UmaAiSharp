import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path

from ordinary_events import effects_until_ready, frequency, ordinary_effect, ordinary_supports, scenario_resources, scalar_effect
from ura_fans import read_ura_fan_basis


REST_EPISODES = {701, 718}
UPGRADE_STORY = 400000005
TRAINING_HINT = 801000003
ISLAND_OPEN = 400011107
TUCKER_AFTER_TRAINING = 809049032
FIRST_YEAR_CONFIRMATION = 400000100
ORDINARY_TRAINING = {101, 105, 102, 103, 106}
CAMP_TRAINING = set(range(3601, 3606))
TRAINING_ORDER = (101, 105, 102, 103, 106)


def rows(path):
    with path.open(encoding='utf-8') as stream:
        for line in stream:
            yield json.loads(line)


def fingerprint(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def classify(turn, after, pending, ready_panel):
    if after['playing_state'] == 1 and after['turn'] == turn + 1:
        return 'nextTurnEvent' if pending else 'ready' if ready_panel else 'otherPhase'
    if after['turn'] == turn and pending:
        if turn == 13 and len(pending) == 1 and (pending[0]['story_id'], pending[0]['chara_id'], pending[0]['play_timing']) == (FIRST_YEAR_CONFIRMATION, 0, 6):
            return 'turnConfirmation'
        return 'sameTurnEvent'
    return 'otherPhase'


def rest_episode(event, character):
    episode = event['story_id'] - (500000000 + character * 1000)
    return episode if character > 0 and event['chara_id'] == character and event['play_timing'] == 6 and episode in REST_EPISODES else None


def upgrade_order(windows, assignments):
    counts = defaultdict(Counter)
    before_stories, after_stories = set(), set()
    next_packets = defaultdict(list)
    for window in windows:
        if not window['expectedEvent']:
            continue
        events = window['events']
        indices = [index for index, event in enumerate(events) if event['storyId'] == UPGRADE_STORY]
        if len(indices) != 1 or events[indices[0]]['eventId'] != window['expectedEvent']:
            raise ValueError('训练升级窗口的故事或具体训练目标不匹配')
        index = indices[0]
        before = [event['storyId'] for event in events[:index]]
        after = [event['storyId'] for event in events[index + 1:]]
        group = assignments.get(window['at']['runKey'], 'excluded')
        counts[group]['upgradeFirst' if index == 0 else 'upgradeAfterOtherEvents'] += 1
        counts[group]['sameTurnBefore'] += len(before)
        counts[group]['sameTurnAfterUpgradeFirst' if index == 0 else 'sameTurnAfterUpgradeLate'] += len(after)
        before_stories.update(before)
        after_stories.update(after)
        for earlier, later, label in ((ISLAND_OPEN, TRAINING_HINT, 'islandBeforeHint'),
                                       (TRAINING_HINT, TUCKER_AFTER_TRAINING, 'hintBeforeTucker')):
            if earlier in before and later in before:
                counts[group][label] += 1
                if before.index(earlier) > before.index(later):
                    raise ValueError(f'升级前相对顺序出现反例 {earlier} / {later}')
        last = window['last']
        next_packets[last['source'], int(last['packetId'])].append((group, index == 0))
    return counts, before_stories, after_stories, next_packets


def ready_panel(visible, after):
    if after is None or after['playing_state'] != 1 or visible.get('unchecked_event_array') != []:
        return False
    home = visible.get('home_info') or {}
    disabled = set(home.get('disable_command_id_array') or [])
    enabled = {row['command_id'] for row in home.get('command_info_array') or []
               if row['is_enable'] == 1 and row['command_id'] not in disabled}
    camp = 37 <= after['turn'] <= 40 or 61 <= after['turn'] <= 64
    return (CAMP_TRAINING | {3606} if camp else ORDINARY_TRAINING | {701}) <= enabled


def counts_with_interval(by_run):
    counts = sum(by_run.values(), Counter())
    total = counts.total()
    if not total:
        return None
    result = {}
    for key, count in sorted(counts.items()):
        probability = count / total
        variance = sum((group[key] - probability * group.total()) ** 2 for group in by_run.values())
        runs = len(by_run)
        error = 1.96 * math.sqrt(runs / (runs - 1) * variance) / total if runs > 1 else None
        result[key] = {'count': count, 'opportunities': total, 'runs': runs, 'rate': probability,
                       'clusterNormal95': [max(0, probability - error), min(1, probability + error)]
                       if error and 0 < count < total else None}
    return result


def fit(evidence, events, boards_path, level_windows, split, event_outcomes, master):
    paths = [evidence / name for name in ('pioneer-observations.jsonl', 'pioneer-actions.jsonl', 'exchanges.jsonl')]
    observations, actions, exchanges = paths
    hashes = {path.name: fingerprint(path) for path in (*paths, events, boards_path, level_windows, split, event_outcomes, master)}
    manifest = json.loads(split.read_text(encoding='utf-8'))
    if manifest['observationsSha256'] != hashes[observations.name]:
        raise ValueError('观测文件与冻结切分的 SHA-256 不匹配')
    assignments = {run: 'fit' for run in manifest['fitRuns']}
    for run in manifest['holdoutRuns']:
        if run in assignments:
            raise ValueError('拟合组和留出组有重复育成')
        assignments[run] = 'holdout'

    paired = {row['key'] for row in rows(exchanges) if row['status'] == 'Paired'}
    commands = {row['exchangeKey']: request for row in rows(actions)
                if (request := row['fields'].get('single_mode_exec_command_request_common')) is not None
                and row['exchangeKey'] in paired}
    rest = defaultdict(lambda: defaultdict(Counter))
    arrivals = defaultdict(lambda: defaultdict(lambda: defaultdict(Counter)))
    ordinary = defaultdict(Counter)
    unresolved_arrivals = []
    ordinary_edges = {}
    planning_edges = {}
    scenario_by_packet = {}
    cards, characters = ordinary_supports(master)
    ura_base, fan_bonus = read_ura_fan_basis(master)
    ura_fans = defaultdict(lambda: defaultdict(Counter))
    ura_seen = set()
    classified = {(row['after']['source'], int(row['after']['packetId'])): row for row in rows(event_outcomes)
                  if row['run'] in assignments and row['category'] != 'fixed_or_unclassified'}
    placements = defaultdict(lambda: defaultdict(lambda: defaultdict(Counter)))
    boards = {(row['packet']['source'], int(row['packet']['packetId'])): row for row in rows(boards_path)
              if row['contextVariants'] == 1 and row['runKey'] in assignments}
    training_opportunities = defaultdict(list)
    history_frequencies = Counter()
    history_holdout = []
    history_runs = defaultdict(set)
    windows = json.loads(level_windows.read_bytes())
    order_counts, before_stories, after_stories, next_packets = upgrade_order(windows, assignments)
    next_stories = set()
    for window in windows:
        if window['matches'] and window['at']['runKey'] in assignments:
            before = window['before']
            training_opportunities[before['source'], int(before['packetId'])].append(window)
    eligible_training = set()
    packet_keys = {}
    ready_packets = {}
    seen = set()

    def add_arrival(group, source, turn, run, packet, arrival):
        if arrival in ('sameTurnEvent', 'nextTurnEvent'):
            unresolved_arrivals.append((group, source, turn, run, packet, arrival))
        else:
            arrivals[group][source, turn][run][arrival] += 1

    for row in rows(observations):
        run = row['runKey']
        group = assignments.get(run)
        exchange = row['exchangeKey']
        packet = row['source'], int(row['packetId'])
        visible = row['visible']
        if (windows_at_next_turn := next_packets.pop(packet, None)) is not None:
            pending = visible.get('unchecked_event_array')
            if pending:
                next_stories.add(pending[0]['story_id'])
                for window_group, upgrade_first in windows_at_next_turn:
                    order_counts[window_group]['nextTurnStoryAfterUpgradeFirst' if upgrade_first else 'nextTurnStoryAfterUpgradeLate'] += 1
        if group is None or exchange not in paired:
            continue
        packet_keys[packet] = exchange
        scenario_by_packet[packet] = scenario_resources(visible)
        after = visible.get('chara_info')
        if after and after['turn'] in ura_base and row['endpoint'].endswith('/race_end'):
            reward = visible.get('race_reward_info')
            turn = after['turn']
            if reward and reward['result_rank'] == 1 and (run, turn) not in ura_seen:
                ura_seen.add((run, turn))
                bonus = sum(fan_bonus(card['support_card_id'], card['exp']) for card in after['support_card_array'])
                basis = ura_base[turn] * (100 + bonus) // 100
                percent = (reward['gained_fans'] * 100 + basis // 2) // basis
                ura_fans[group][turn][percent] += 1
        if ready_panel(visible, after):
            ready_packets[packet] = after['turn']
        board = boards.pop(packet, None)
        if board is not None:
            common = next((value for value in row['visible'].values()
                           if isinstance(value, dict) and 'chara_info' in value), row['visible'])
            appeared = {value['target_id'] for value in common['chara_info']['evaluation_info_array'] if value['is_appear'] == 1}
            participants = {member for cell in board['loads'] for member in cell['participants']}
            mode, house = board['mode'] == 'Camp', board['attendanceEffect']
            for card in board['cards']:
                if card['position'] not in appeared or len(card['commands']) > 1 or card['cardType'] not in (1, 2):
                    continue
                kind = 'training' if card['cardType'] == 1 else 'tucker'
                key = 'absent' if not card['commands'] else 'preferred' if kind == 'training' and card['preferred'] else 'other'
                placements[group][kind, mode, house][run][key] += 1
            for member in row['visible']['pioneer_data_set']['evaluation_info_array']:
                target = member['target_id']
                if member['is_pioneer_team'] == 1 and target >= 1000 and target in appeared:
                    placements[group]['projectNpc', mode, house][run]['other' if target in participants else 'absent'] += 1
        if (opportunities := training_opportunities.pop(packet, None)) is not None:
            common = next((value for value in row['visible'].values()
                           if isinstance(value, dict) and 'chara_info' in value), row['visible'])
            turn = common['chara_info']['turn']
            camp_periods = int(turn > 37) + int(turn > 61)
            pioneer = visible['pioneer_data_set']
            executed = {row['base_command_id']: row['exec_count'] for row in pioneer['training_exec_info_array']}
            checkpoint_boosts = sum(row['check_point_type'] in (2, 4) and row['result_state'] == 2
                                    for row in pioneer['check_point_info_array'])
            command_rows = (common.get('home_info') or {}).get('command_info_array') or []
            for window in opportunities:
                command = window['command']
                ordinary_command = TRAINING_ORDER[command - 3601] if command in CAMP_TRAINING else command
                successful = window['beforeCount']
                count = executed[ordinary_command]
                level = window['beforeLevel']
                minimum = (level - 1 - checkpoint_boosts) * 4
                maximum = count if level == 5 else min(count, minimum + 3)
                if not max(0, minimum) <= successful <= maximum:
                    raise ValueError(f'训练历史超出面板与评审允许范围 {packet} {ordinary_command}')
                history_runs[group].add(run)
                if group == 'fit':
                    history_frequencies[camp_periods, ordinary_command, count - successful] += 1
                else:
                    history_holdout.append((camp_periods, ordinary_command, count, successful, max(0, minimum), maximum, level))
                if not window['success']:
                    continue
                training = next((cmd for cmd in command_rows if cmd['command_id'] == window['command']), None)
                first = window['events'][0]['storyId'] if window['events'] else None
                if training is None or training['tips_event_partner_array'] or first == ISLAND_OPEN:
                    continue
                if window['expectedEvent']:
                    arrival = 'trainingLevel' if first == UPGRADE_STORY else 'sameTurnEvent'
                    arrivals[group]['trainingWithLevel', turn][run][arrival] += 1
                else:
                    result_at = window['at']
                    eligible_training.add((result_at['source'], int(result_at['packetId'])))
        request = commands.get(exchange)
        pending = visible.get('unchecked_event_array')
        if row['endpoint'].endswith('/planning') and after is not None and after['playing_state'] == 1 and pending is not None and exchange not in seen:
            seen.add(exchange)
            add_arrival(group, 'planningSubmission', after['turn'], run, packet,
                        'sameTurnEvent' if pending else 'ready' if packet in ready_packets else 'otherPhase')
            continue
        if request is None or after is None or pending is None or exchange in seen:
            continue
        seen.add(exchange)
        turn, command = request['current_turn'], request['command_id']
        if command == 701:
            episode = rest_episode(pending[0], pending[0]['chara_id']) if len(pending) == 1 else None
            if after['turn'] != turn or episode is None:
                raise ValueError(f'普通休息响应出现未归类分支 {exchange}')
            rest[group][run][episode] += 1
            continue
        source = ('training' if command in ORDINARY_TRAINING else 'campTraining' if command in CAMP_TRAINING
                  else 'campRest' if (request['command_type'], command, request['command_group_id'], request['select_id']) == (3, 0, 3606, 0) else None)
        if source is None or source != 'campRest' and (packet not in eligible_training
                or (visible.get('command_result') or {}).get('result_state') != 2):
            continue
        if any(event['story_id'] in (UPGRADE_STORY, TRAINING_HINT, ISLAND_OPEN) for event in pending):
            continue
        add_arrival(group, source, turn, run, packet, classify(turn, after, pending, packet in ready_packets))

    seen.clear()
    for row in rows(events):
        event, before, after = (row.get(key) for key in ('event', 'beforeState', 'afterState'))
        if row['excluded'] or not event or not before or not after:
            continue
        identity = row['after']
        run = identity['runKey']
        group = assignments.get(run)
        packet = identity['source'], identity['packetId']
        exchange = packet_keys.get(packet)
        story = event['story_id']
        source = ('restConfirmation' if rest_episode(event, event['chara_id']) is not None else 'trainingLevel' if story == UPGRADE_STORY
                  else 'turnConfirmation' if story == FIRST_YEAR_CONFIRMATION and before['turn'] == 13 and event['play_timing'] == 6
                  else 'treatment' if event['event_id'] == 7016 and story % 1000 == 717
                  else 'outing' if story == 400011111 or (809049011 <= story <= 809049015
                        and not any(item['story_id'] == 400011111 for item in row['pendingAfter']))
                  else 'race' if story == 400011105 else None)
        if group is None or exchange is None or exchange in seen:
            continue
        seen.add(exchange)
        before_at = row['before']
        before_packet = (before_at['source'], int(before_at['packetId'])) if before_at else None
        if before_at and before_at.get('runKey') == run and before_packet[0] == packet[0] and before_packet[1] < packet[1]:
            if before['turn'] == after['turn'] and after['turn'] in (3, 13, 25, 37, 49) and before['state'] == after['state'] == 0:
                before_scenario, after_scenario = scenario_by_packet.get(before_packet), scenario_by_packet.get(packet)
                scalar = scalar_effect(before, row['delta'])
                if before_scenario is not None and before_scenario == after_scenario and scalar is not None:
                    planning_edges[before_packet] = (packet, (*scalar, 0, 0)) if before_packet not in planning_edges else None
            effect = ordinary_effect(row, classified.get(packet), cards, characters,
                                     scenario_by_packet.get(before_packet), scenario_by_packet.get(packet))
            if effect is not None:
                ordinary_edges[before_packet] = (packet, effect) if before_packet not in ordinary_edges else None
        if source is None:
            continue
        turn = before['turn']
        if source == 'turnConfirmation' and (turn != 13 or row['delta'] != {'turn': 1}):
            raise ValueError(f'回合确认不符合第13回合无资源变化的结算 {exchange}')
        add_arrival(group, source, turn, run, packet, classify(turn, after, row['pendingAfter'], packet in ready_packets))

    for group, source, turn, run, packet, arrival in unresolved_arrivals:
        effect = (effects_until_ready(packet, turn - 1, planning_edges, ready_packets) if source == 'planningSubmission'
                  else effects_until_ready(packet, turn, ordinary_edges, ready_packets))
        if effect is None:
            arrivals[group][source, turn][run][arrival] += 1
            continue
        arrivals[group][source, turn][run]['ordinaryToReady'] += 1
        if group == 'fit':
            ordinary[source, turn][effect] += 1

    calibration = {'ordinaryRest': [{'storyEpisode': key, 'count': count}
                                    for key, count in sorted(sum(rest['fit'].values(), Counter()).items())],
                   'arrivals': [], 'placement': [],
                   'uraFans': [{'turn': turn, 'percent': percent, 'count': count}
                               for turn, counts in sorted(ura_fans['fit'].items())
                               for percent, count in sorted(counts.items())],
                   'trainingHistory': [{'campPeriods': phase, 'commandId': command, 'otherExecutions': other, 'count': count}
                                       for (phase, command, other), count in sorted(history_frequencies.items())]}
    for (source, turn), by_run in sorted(arrivals['fit'].items()):
        count = sum(by_run.values(), Counter())
        calibration['arrivals'].append({'source': source, 'turn': turn,
                                        **{key: count[key] for key in ('ready', 'turnConfirmation', 'trainingLevel', 'sameTurnEvent', 'nextTurnEvent', 'otherPhase')},
                                        'ordinary': [frequency(effect, n) for effect, n in sorted(ordinary[source, turn].items())]})
    for (kind, camp, house), by_run in sorted(placements['fit'].items()):
        count = sum(by_run.values(), Counter())
        if count['other']:
            calibration['placement'].append({'kind': kind, 'camp': camp, 'seaHousePercent': house,
                                             'absent': count['absent'], 'other': count['other']})
    history_metrics = Counter()
    for phase, command, count, successful, minimum, maximum, level in history_holdout:
        if level == 5:
            continue
        weights = [1 + history_frequencies[phase, command, count - value] for value in range(minimum, maximum + 1)]
        modal = minimum + weights.index(max(weights))
        history_metrics['upgradeableWindows'] += 1
        history_metrics['modalMatches'] += int(modal == successful)
        history_metrics['modalAbsoluteError'] += abs(modal - successful)
    report = {'schemaVersion': 1, 'inputs': hashes,
              'uraFans': ura_fans,
              'trainingHistory': {'fitRuns': len(history_runs['fit']), 'fitWindows': sum(history_frequencies.values()),
                                  'holdoutRuns': len(history_runs['holdout']), 'holdoutWindows': len(history_holdout),
                                  **history_metrics},
              'ordinaryEventEdges': sum(edge is not None for edge in ordinary_edges.values()),
              'upgradeOrder': {'windows': order_counts, 'sameTurnBeforeStories': len(before_stories),
                               'sameTurnAfterStories': len(after_stories), 'nextTurnFirstStories': len(next_stories),
                               'beforeAndSameTurnAfterOverlap': sorted(before_stories & after_stories),
                               'beforeAndNextTurnFirstOverlap': sorted(before_stories & next_stories)},
              'ordinaryRest': {group: counts_with_interval(rest[group]) for group in ('fit', 'holdout')},
              'arrivals': [{'source': source, 'turn': turn,
                            **{group: counts_with_interval(arrivals[group][source, turn]) for group in ('fit', 'holdout')}}
                           for source, turn in sorted(arrivals['fit'].keys() | arrivals['holdout'].keys())],
              'placement': [{'kind': kind, 'camp': camp, 'seaHousePercent': house,
                             **{group: counts_with_interval(placements[group][kind, camp, house]) for group in ('fit', 'holdout')}}
                            for kind, camp, house in sorted(placements['fit'].keys() | placements['holdout'].keys())]}
    return calibration, report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Fit Pioneer turn opportunities from frozen whole runs')
    for name in ('evidence', 'events', 'boards', 'level_windows', 'split', 'event_outcomes', 'master', 'output'):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    calibration, report = fit(args.evidence, args.events, args.boards, args.level_windows, args.split, args.event_outcomes, args.master)
    args.output.mkdir()
    for name, value in [('turn-calibration.json', calibration), ('turn-calibration-report.json', report)]:
        with (args.output / name).open('x', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
