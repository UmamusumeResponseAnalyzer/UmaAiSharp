import argparse
import hashlib
import json
from pathlib import Path


def freeze(observations, output):
    starts = {}
    finishes = set()
    digest = hashlib.sha256()
    with observations.open('rb') as stream:
        for line in stream:
            digest.update(line)
            row = json.loads(line)
            run = row['runKey']
            if not run:
                continue
            endpoint = row['endpoint'].rsplit('/', 1)[-1]
            if endpoint == 'start':
                starts.setdefault(run, set()).add((row['source'], row['packetId']))
            elif endpoint == 'finish':
                finishes.add(run)

    # runKey hashes the actor and game run ID, independently of the capture source.
    eligible = starts.keys() & finishes
    ranked = sorted(eligible, key=lambda run: hashlib.sha256(f'pioneer-rules-v2/{run}'.encode()).digest())
    held = set(ranked[:len(ranked) // 10])
    if not held:
        raise ValueError('完整育成数不足，无法冻结留出组')
    manifest = {
        'schemaVersion': 1,
        'observationsSha256': digest.hexdigest(),
        'evaluation': 'retrospective',
        'eligibility': 'same run has both start and finish',
        'selection': 'lowest 10% SHA-256(pioneer-rules-v2/<runKey>)',
        'fitRuns': sorted(eligible - held),
        'holdoutRuns': {run: sorted(starts[run]) for run in sorted(held)},
        'excludedWithoutFinish': sorted(starts.keys() - finishes),
        'excludedWithoutStart': sorted(finishes - starts.keys()),
    }
    with output.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    print(f'{len(eligible - held)} fit / {len(held)} holdout / {len(starts.keys() ^ finishes)} excluded')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Freeze complete Pioneer runs before fitting mechanisms')
    parser.add_argument('observations', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    freeze(args.observations, args.output)
