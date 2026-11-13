import sqlite3


ATTRIBUTES = ('speed', 'stamina', 'power', 'guts', 'wiz')
RESOURCE_FIELDS = (*ATTRIBUTES, 'vital', 'skill_point', 'motivation', 'max_vital')
ORDINARY_CATEGORIES = {'support_chain', 'support_other', 'character_random_candidate'}


def ordinary_supports(master):
    with sqlite3.connect(master.resolve().as_uri() + '?mode=ro', uri=True) as db:
        cards = {row[0] for row in db.execute('SELECT id FROM support_card_data WHERE support_card_type=1')}
        characters = {row[0] for row in db.execute('''
            SELECT chara_id FROM support_card_data GROUP BY chara_id
            HAVING MIN(support_card_type)=1 AND MAX(support_card_type)=1''')}
    return cards, characters


def scenario_resources(visible):
    pioneer = visible.get('pioneer_data_set')
    if not pioneer:
        return None
    info = pioneer.get('pioneer_info') or {}
    return (info.get('pioneer_pt'), info.get('planning_cost'), info.get('deck_id'),
            (pioneer.get('shima_training_info') or {}).get('remain_num'),
            tuple((row['facility_id'], row['state_id']) for row in pioneer.get('facility_info_array') or ()),
            tuple((row['check_point_type'], row['result_state']) for row in pioneer.get('check_point_info_array') or ()))


def ordinary_effect(row, classified, cards, characters, before_scenario, after_scenario):
    if classified is None or classified['category'] not in ORDINARY_CATEGORIES:
        return None
    event = row['event']
    metadata = row.get('master') or {}
    contents = event.get('event_contents_info') or {}
    if (metadata.get('race_event_flag') or event.get('succession_event_info') or event.get('minigame_result')
            or any(option.get('target_race_id') for option in contents.get('choice_array') or ())):
        return None
    if classified['category'] != 'character_random_candidate':
        owner = contents.get('support_card_id') or metadata.get('support_card_id') or classified.get('supportCard')
        if owner not in cards and (owner or metadata.get('support_chara_id') not in characters):
            return None
    before, after = row['beforeState'], row['afterState']
    if (before['state'] != after['state'] or not before_scenario or before_scenario != after_scenario
            or set(row['delta']) - {*RESOURCE_FIELDS, 'turn'}
            or set(before['chara_effect_id_array']) != set(after['chara_effect_id_array'])
            or before['guest_outing_info_array'] != after['guest_outing_info_array']
            or before['skill_array'] != after['skill_array'] or before['disable_skill_id_array'] != after['disable_skill_id_array']):
        return None
    partners = lambda state: {v['target_id']: {k: x for k, x in v.items() if k != 'evaluation'}
                              for v in state['evaluation_info_array']}
    if partners(before) != partners(after):
        return None
    if row['partnerEffects']:
        return None
    bonds = classified['bondDelta']
    if len(bonds) > 1 or any(not 1 <= slot <= 6 or amount < 0 for slot, amount in bonds):
        return None
    scalar = scalar_effect(before, row['delta'])
    return (*scalar, bonds[0][0] if bonds else 0, bonds[0][1] if bonds else 0) if scalar is not None else None


def scalar_effect(before, delta):
    if any(delta.get(key, 0) < 0 for key in (*ATTRIBUTES, 'skill_point', 'max_vital')):
        return None
    # Approximate pre-conversion positive gains from displayed changes. A capped observation remains a lower bound.
    gains = []
    for key in ATTRIBUTES:
        gain = delta.get(key, 0)
        below = min(gain, max(0, 1200 - before[key]))
        gains.append(below + 2 * (gain - below))
    return (*gains, *(delta.get(key, 0) for key in RESOURCE_FIELDS[5:]))


def effects_until_ready(packet, turn, edges, ready):
    effect = [0] * len(RESOURCE_FIELDS)
    bonds = {}
    while (edge := edges.get(packet)) is not None:
        next_packet, window_effect = edge
        for i, gain in enumerate(window_effect[:-2]):
            effect[i] += gain
        target, gain = window_effect[-2:]
        if gain:
            bonds[target] = bonds.get(target, 0) + gain
        packet = next_packet
        if ready.get(packet) == turn + 1:
            if len(bonds) <= 1:
                return (*effect, sum(bonds.values()))
            return None
    return None


def frequency(effect, count):
    names = ('speed', 'stamina', 'power', 'guts', 'wisdom', 'vital', 'skillPoints')
    return {'count': count, 'gains': dict(zip(names, effect[:7])), 'motivation': effect[7],
            'maxVital': effect[8], 'bond': effect[9]}


def minimum_raw_attribute_gain(before, gain):
    lower = min(gain, max(0, 1200 - before))
    upper = gain - lower
    return gain if gain <= 0 else lower + (1 if upper == 1 else 2 * upper)


def whole_run_reward(row, classified, cards, characters):
    if classified['category'] not in ORDINARY_CATEGORIES:
        return None
    event, before, after = (row[key] for key in ('event', 'beforeState', 'afterState'))
    metadata = row.get('master') or {}
    contents = event.get('event_contents_info') or {}
    if (event['play_timing'] == 3 or metadata.get('race_event_flag')
            or event.get('succession_event_info') or event.get('minigame_result')
            or event['story_id'] in (801000003, 809049032)
            or any(option.get('target_race_id') for option in contents.get('choice_array') or ())):
        return None
    if classified['category'] != 'character_random_candidate':
        owner = contents.get('support_card_id') or metadata.get('support_card_id') or classified.get('supportCard')
        if owner not in cards and (owner or metadata.get('support_chara_id') not in characters):
            return None
    if before['state'] != 0 or after['state'] != 0:
        return None

    delta = row['delta']
    stats = tuple(delta.get(key, 0) for key in ATTRIBUTES)
    points = delta.get('skill_point', 0)
    if points < 0:
        return None
    declared = row.get('rewards')
    options = declared['rewards'] if declared and declared['paired'] and declared['contextUninterrupted'] else ()
    selected = {result['select_index'] for result in row['resultIndices']}
    compatible = set()
    for option in options:
        if option['select_index'] not in selected:
            continue
        raw = [0] * 6
        seen = set()
        for atom in option['gain_param_array']:
            if not isinstance(atom, dict):
                break
            target = atom['effect_value_0']
            if atom['display_id'] not in (1, 2) or atom['effect_value_2'] != 0 or target not in (1, 2, 3, 4, 5, 30):
                continue
            index = target - 1 if target <= 5 else 5
            if index in seen:
                break
            seen.add(index)
            raw[index] = atom['effect_value_1'] * (1 if atom['display_id'] == 1 else -1)
        else:
            def settle(key, gain):
                if gain <= 0:
                    return max(-before[key], gain)
                lower = min(gain, max(0, 1200 - before[key]))
                upper = gain - lower
                return min(before['max_' + key] - before[key], lower + (max(1, upper // 2) if upper else 0))

            if raw[5] == points and tuple(settle(key, gain) for key, gain in zip(ATTRIBUTES, raw)) == stats:
                compatible.add(tuple(raw))
    if len(compatible) == 1:
        return compatible.pop(), 'declared'

    # Observed gains at the cap are lower bounds. Unresolved raw increments use the smallest matching gain.
    capped = any(after[key] == before['max_' + key] for key in ATTRIBUTES)
    return (*(minimum_raw_attribute_gain(before[key], gain) for key, gain in zip(ATTRIBUTES, stats)), points), 'capLowerBound' if capped else 'observed'
