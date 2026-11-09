"""Project recorded before/after fields; never use a research formula as the expected value."""
import argparse
import hashlib
import json
from pathlib import Path


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--research', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    construction = args.research / 'construction-checkpoints'
    provenance_path = construction / 'run-02/provenance.json'
    provenance = read(provenance_path)
    inputs = {args.input / name: value for name, value in provenance['inputs'].items()}
    assert all(sha(path) == digest for path, digest in inputs.items()), 'Normalized input fingerprint mismatch'
    transitions = construction / 'run-02/transitions.jsonl'
    assert sha(transitions) == read(construction / 'analysis-02/summary.json')['inputSha256']
    label_path = construction / 'deep-03/race-labels.json'
    race_path = args.research / 'races/hypotheses-02/pt-candidate.json'
    races = {(provenance['sourceHashes'][r['ref']['source']], r['ref']['packetId']): r
             for r in read(race_path) if 'race' in r}
    labels = {}
    for row in read(label_path)['rows']:
        key = (row['sourceSha256'], row['afterRecordId'])
        if row['ambiguousGoalCondition'] or key not in races:
            continue
        other = races[key]
        assert row['target'] == other['target']
        assert row['raceEndRecordId'] == other['race']['packetId']
        labels[key] = row['target']
    outputs = {'race-point-observations.jsonl': [], 'tucker-point-observations.jsonl': [],
               'checkpoint-progress-observations.jsonl': []}
    high = {400011004: 1, 400011008: 2, 400011012: 3, 400011017: 4, 400011021: 5}
    commands = {'speed': 101, 'stamina': 105, 'power': 102, 'guts': 103, 'wisdom': 106}
    def progress(chara):
        levels = {r['command_id']: r['level'] for r in chara['training_level_info_array']}
        director = [r['evaluation'] for r in chara['evaluation_info_array'] if r['target_id'] == 102]
        assert len(director) == 1
        return {'trainingLevels': {name: levels[command] for name, command in commands.items()},
                'directorBond': director[0], 'hasAttentionEffect': 9 in chara['chara_effect_id_array']}
    with transitions.open(encoding='utf-8') as stream:
        for line in stream:
            row = json.loads(line)
            if len(row['selected']) != 1:
                continue
            selected = row['selected'][0]
            story = selected['story_id']
            if story not in high and story not in (400011105, 400011111):
                continue
            ref = {k: row[k] for k in ('sourceSha256', 'beforeRecordId', 'requestRecordId', 'afterRecordId')}
            before, after, request = row['before'], row['after'], row['request']
            ch, ah = before['chara'], after['chara']
            info = before['scenario']['pioneer_info']
            context = {'turn': ch['turn'], 'rawPlayingState': ch['playing_state'], 'deckId': info['deck_id'],
                       'storyId': story, 'choiceNumber': request['choice_number']}
            if story == 400011105:
                key = (row['sourceSha256'], row['afterRecordId'])
                if key not in labels:
                    continue
                cp = sorted(before['scenario']['check_point_info_array'], key=lambda r: r['check_point_type'])
                context.update(isTargetRace=labels[key], completedCheckpointType=cp[-1]['check_point_type'] if cp else 0,
                               completedCheckpointResult=cp[-1]['result_state'] if cp else 0)
                outputs['race-point-observations.jsonl'].append({**ref, 'context': context,
                    'beforePoints': info['pioneer_pt'], 'afterPoints': after['scenario']['pioneer_info']['pioneer_pt']})
            elif story == 400011111:
                support = [r for r in ch['support_card_array'] if r['support_card_id'] == 30257]
                assert len(support) == 1
                context.update(playTiming=selected['play_timing'], supportCardId=support[0]['support_card_id'],
                               limitBreakCount=support[0]['limit_break_count'], supportExperience=support[0]['exp'])
                outputs['tucker-point-observations.jsonl'].append({**ref, 'context': context,
                    'before': {'developmentPoints': info['pioneer_pt'], 'skillPoints': ch['skill_point']},
                    'after': {'developmentPoints': after['scenario']['pioneer_info']['pioneer_pt'], 'skillPoints': ah['skill_point']}})
            else:
                checkpoint = high[story]
                cp = [r for r in before['scenario']['check_point_info_array'] if r['check_point_type'] == checkpoint]
                assert len(cp) == 1 and cp[0]['result_state'] == 2
                context.update(checkpointType=checkpoint, resultState=cp[0]['result_state'])
                old_members = {r['target_id'] for r in before['scenario']['evaluation_info_array'] if r['is_pioneer_team']}
                new_members = {r['target_id'] for r in after['scenario']['evaluation_info_array'] if r['is_pioneer_team']}
                outputs['checkpoint-progress-observations.jsonl'].append({**ref, 'context': context,
                    'before': progress(ch), 'after': progress(ah), 'addedMembers': len(new_members - old_members),
                    'removedMembers': len(old_members - new_members)})
    assert [len(v) for v in outputs.values()] == [902, 376, 389]
    inputs.update({path: sha(path) for path in [transitions, label_path, race_path, provenance_path, Path(__file__)]})
    for name, rows in outputs.items():
        with (args.output / name).open('x', encoding='utf-8', newline='\n') as stream:
            for row in rows:
                stream.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')
    manifest = {'scope': 'Recorded components only; no independent holdout', 'masterSha256': provenance['masterSha256'],
                'inputs': [{'path': str(path), 'sha256': digest} for path, digest in inputs.items()],
                'outputs': [{'name': name, 'count': len(rows), 'sha256': sha(args.output / name)} for name, rows in outputs.items()],
                'raceLabels': {'agreed': 902, 'constructionOnly': 1, 'racesOnlyConditionType3': 2, 'unlabelled': 3}}
    with (args.output / 'component-provenance.json').open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
    assert all(sha(path) == digest for path, digest in inputs.items()), 'Inputs changed during export'
    print({name: len(rows) for name, rows in outputs.items()})


if __name__ == '__main__':
    main()
