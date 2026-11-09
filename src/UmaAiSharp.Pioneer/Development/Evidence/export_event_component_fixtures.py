"""Re-read normalized game fields at researched references; export anonymous component fixtures."""
import argparse
import hashlib
import json
import sqlite3
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser()
    for name in ['input', 'research', 'master', 'output']:
        parser.add_argument('--' + name, type=Path, required=True)
    a = parser.parse_args()
    construction = a.research / 'construction-checkpoints/expansion-20261108'
    event_root = a.research / 'events-history/mechanisms-20261108'
    provenance = read(construction / 'extracted-01/provenance.json')
    for name, expected in provenance['inputs'].items():
        assert sha(a.input / name) == expected, 'Normalized input changed: ' + name
    assert sha(a.master) == provenance['masterSha256']
    sources = provenance['sourceHashes']
    tucker_path = event_root / 'analysis-04/tucker-component-fixtures.jsonl'
    member_path = construction / 'delivery-02/member-joins.json'
    confirmation_path = construction / 'delivery-02/late-reward-confirmations.json'
    tucker = [json.loads(line) for line in tucker_path.read_text(encoding='utf-8').splitlines()]
    members = read(member_path)['cases']
    confirmations = read(confirmation_path)['cases']
    refs = []
    for row in tucker:
        refs.append((sources[row['source']], row['beforeRef']['packetId'], row['requestRef']['packetId'], row['afterRef']['packetId']))
    for row in members:
        refs.append(tuple(row[k] for k in ['sourceSha256', 'beforeRecordId', 'requestRecordId', 'afterRecordId']))
    for row in confirmations:
        for item in [row, row['nextConfirmation']]:
            refs.append(tuple(item[k] for k in ['sourceSha256', 'beforeRecordId', 'requestRecordId', 'afterRecordId']))
    wanted = {(source, record) for source, before, request, after in refs for record in [before, after]}
    frames = {}
    for line in (a.input / 'pioneer-observations.jsonl').open(encoding='utf-8-sig'):
        row = json.loads(line)
        key = (sources[row['source']], row['packetId'])
        if key in wanted:
            assert key not in frames
            frames[key] = row
    exchanges = {row['key']: row for row in map(json.loads, (a.input / 'exchanges.jsonl').open(encoding='utf-8-sig'))}
    wanted_requests = {(source, request) for source, _, request, _ in refs}
    requests = {}
    for row in map(json.loads, (a.input / 'pioneer-actions.jsonl').open(encoding='utf-8-sig')):
        key = (sources[row['source']], row['packetId'])
        if key in wanted_requests:
            assert key not in requests
            requests[key] = row
    with sqlite3.connect(a.master.resolve().as_uri() + '?mode=ro', uri=True) as db:
        support_master = {r[0]: (r[1], r[2]) for r in db.execute('select id,chara_id,support_card_type from support_card_data')}
        card_master = dict(db.execute('select id,chara_id from card_data'))

    def pair(ref):
        source, before_id, request_id, after_id = ref
        before, after, request = frames[(source, before_id)], frames[(source, after_id)], requests[(source, request_id)]
        exchange = exchanges[after['exchangeKey']]
        assert before['runKey'] and before['runKey'] == after['runKey']
        assert request['exchangeKey'] == after['exchangeKey'] and request['source'] == before['source'] == after['source']
        assert before_id < request_id < after_id and exchange['status'] == 'Paired'
        assert exchange['endpoint'] == request['endpoint'] == after['endpoint']
        assert {'source': request['source'], 'id': request_id} in exchange['requests']
        assert {'source': after['source'], 'id': after_id} in exchange['responses']
        common_before, common_after = before['visible'], after['visible']
        assert 'chara_info' in common_before and 'chara_info' in common_after, 'Unexpected endpoint-specific common'
        action = request['fields']['single_mode_check_event_request_common']
        assert action['current_turn'] == common_before['chara_info']['turn']
        events = common_before['unchecked_event_array']
        assert len(events) == 1 and events[0]['event_id'] == action['event_id']
        reference = {'sourceSha256': source, 'beforeRecordId': before_id, 'requestRecordId': request_id, 'afterRecordId': after_id}
        return reference, common_before, common_after, action, events[0]

    outputs = {'tucker-friend-observations.jsonl': [], 'project-member-observations.jsonl': [], 'late-checkpoint-confirmations.jsonl': []}
    for row, ref in zip(tucker, refs[:len(tucker)]):
        reference, before, after, action, event = pair(ref)
        b, c = before['chara_info'], after['chara_info']
        support = [x for x in b['support_card_array'] if x['support_card_id'] == 30257]
        assert len(support) == 1
        support = support[0]
        def state(chara):
            bonds = [x for x in chara['evaluation_info_array'] if x['target_id'] == support['position']]
            assert len(bonds) == 1
            bond = bonds[0]
            return {'vital': chara['vital'], 'maxVital': chara['max_vital'], 'motivation': chara['motivation'],
                    'bond': bond['evaluation'], 'outingUnlocked': bool(bond['is_outing']), 'storyStep': bond['story_step']}
        context = {'storyId': event['story_id'], 'choiceNumber': action['choice_number'], 'supportCardId': 30257,
                   'limitBreakCount': support['limit_break_count'], 'supportExperience': support['exp'], 'hasCharmEffect': 8 in b['chara_effect_id_array']}
        assert context['storyId'] == row['story'] and context['choiceNumber'] == row['choice']
        outputs['tucker-friend-observations.jsonl'].append({**reference, 'context': context, 'before': state(b), 'after': state(c),
            'skillPointGain': c['skill_point'] - b['skill_point']})
    for row in members:
        ref = tuple(row[k] for k in ['sourceSha256', 'beforeRecordId', 'requestRecordId', 'afterRecordId'])
        reference, before, after, action, event = pair(ref)
        assert action['choice_number'] == 0
        b, c = before['chara_info'], after['chara_info']
        sb, sa = before['pioneer_data_set'], after['pioneer_data_set']
        def team(scenario):
            return [{'targetId': x['target_id'], 'characterId': x['chara_id']} for x in scenario['evaluation_info_array'] if x['is_pioneer_team']]
        old, new = team(sb), team(sa)
        old_ids = {x['targetId'] for x in old}
        added = [x for x in new if x['targetId'] not in old_ids and x['targetId'] > 6]
        selected = [x['characterId'] for x in added]
        assert all(x['targetId'] == x['characterId'] for x in added)
        assert all(x in new for x in old)
        result = 0 if event['story_id'] == 400011029 else next(x['result_state'] for x in sb['check_point_info_array']
            if x['check_point_type'] == (2 if event['story_id'] == 400011008 else 4))
        supports = [{'position': x['position'], 'characterId': support_master[x['support_card_id']][0],
            'cardType': support_master[x['support_card_id']][1]} for x in b['support_card_array']]
        added_bonds = [x for x in c['evaluation_info_array'] if x['target_id'] in selected]
        assert len(added_bonds) == len(selected)
        expected_npcs = [{'targetId': x['target_id'], 'trainingPartnerId': x['training_partner_id'], 'bond': x['evaluation'],
                         'isAppear': bool(x['is_appear']), 'isOuting': bool(x['is_outing']), 'storyStep': x['story_step']} for x in added_bonds]
        outputs['project-member-observations.jsonl'].append({**reference, 'storyId': event['story_id'], 'turn': b['turn'],
            'resultState': result, 'trainingCharacter': card_master[b['card_id']], 'supports': supports, 'before': old,
            'selectedNpcIds': selected, 'expectedMembers': new, 'expectedNpcs': expected_npcs})
    for row in confirmations:
        ref = tuple(row['nextConfirmation'][k] for k in ['sourceSha256', 'beforeRecordId', 'requestRecordId', 'afterRecordId'])
        reference, before, after, action, event = pair(ref)
        b, c = before['chara_info'], after['chara_info']
        keys = ['speed', 'stamina', 'power', 'guts', 'wiz', 'skill_point', 'vital', 'fans', 'motivation']
        delta = [c[k] - b[k] for k in keys]
        tips = lambda chara: sorted((x['group_id'], x['rarity'], x['level']) for x in chara['skill_tips_array'])
        skills = lambda chara: sorted((x['skill_id'], x['level']) for x in chara['skill_array'])
        team = lambda frame: sorted((x['target_id'], x['chara_id'], x['is_pioneer_team']) for x in frame['pioneer_data_set']['evaluation_info_array'])
        result = next(x['result_state'] for x in before['pioneer_data_set']['check_point_info_array'] if x['check_point_type'] == 5)
        outputs['late-checkpoint-confirmations.jsonl'].append({**reference, 'turn': b['turn'], 'phase': b['playing_state'],
            'resultState': result, 'eventId': event['event_id'], 'storyId': event['story_id'], 'playTiming': event['play_timing'],
            'choiceCount': len(event['event_contents_info']['choice_array']), 'choiceNumber': action['choice_number'],
            'observedDelta': delta, 'tipsUnchanged': tips(b) == tips(c), 'skillsUnchanged': skills(b) == skills(c),
            'membersUnchanged': team(before) == team(after), 'followingEventCount': len(after['unchecked_event_array']),
            'mixedCheckpoint': not row['baseHintMatches']})
    assert [len(x) for x in outputs.values()] == [1496, 660, 220]
    a.output.mkdir(parents=True, exist_ok=True)
    for name, rows in outputs.items():
        with (a.output / name).open('x', encoding='utf-8', newline='\n') as stream:
            for row in rows:
                stream.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')
    manifest = {'inputs': provenance['inputs'], 'masterSha256': provenance['masterSha256'], 'sourceHashes': sources,
        'researchFiles': [{'name': str(path.relative_to(a.research)), 'sha256': sha(path)} for path in [tucker_path, member_path, confirmation_path]],
        'scriptSha256': sha(Path(__file__)), 'files': [{'name': name, 'count': len(rows), 'sha256': sha(a.output / name)} for name, rows in outputs.items()]}
    with (a.output / 'event-component-provenance.json').open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
    for name, expected in provenance['inputs'].items():
        assert sha(a.input / name) == expected
    assert sha(a.master) == provenance['masterSha256']
    print({name: len(rows) for name, rows in outputs.items()})


if __name__ == '__main__':
    main()
