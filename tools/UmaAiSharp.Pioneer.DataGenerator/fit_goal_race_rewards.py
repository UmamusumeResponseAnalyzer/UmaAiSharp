"""Compare the common target race reward with per-goal estimates in the deduplicated packet audit."""

import argparse
import gzip
import json
from collections import defaultdict
from pathlib import Path
from statistics import mean


BASE = (3, 3, 3, 3, 3, 45)
STATS = ("speed", "stamina", "power", "guts", "wiz")


def displayed(before, cap, raw):
    below = min(raw, max(0, 1200 - before))
    above = raw - below
    return min(cap - before, below + (max(1, above // 2) if above else 0))


def infer(row, rate):
    values = row["preResources"][:5]
    caps = row["preCaps"][:5]
    observed = [row["delta"].get(stat, 0) for stat in STATS]
    possible = [tuple(n for n in range(101) if displayed(value, cap, n * rate // 100) == gain)
                for value, cap, gain in zip(values, caps, observed, strict=True)]
    common = set.intersection(*(set(options) for options in possible))
    if common:
        # The same award for all five attributes is directly identifiable even when one is capped.
        attributes = [min(common, key=lambda n: (abs(n - 3), n))] * 5
    else:
        attributes = []
        for value, cap, gain, options in zip(values, caps, observed, possible, strict=True):
            if value + gain == cap:
                attributes.append(None)
            elif options:
                attributes.append(min(options, key=lambda n: (abs(n - 3), n)))
            else:
                raw = next(n for n in range(201) if displayed(value, cap, n) == gain)
                attributes.append((raw + 0.5) * 100 / rate)
    points = row["delta"].get("skill_point", 0)
    basis = [n for n in range(201) if n * rate // 100 == points]
    return (*attributes, basis[0] if basis else (points + 0.5) * 100 / rate)


def observations(root):
    rules = json.loads((root / "full/rules-used.json").read_bytes())
    inventory = json.loads((root / "corpus/inventory.json").read_bytes())
    splits = {(frame["source"], frame["packet"]): run["split"] for run in inventory["runs"]
              for frame in run["starts"] + run["anchors"]}
    traces = json.loads((root / "verified/ledger-summary.json").read_bytes())["raceTraces"]
    programs = {(alias, trace["entry"]): trace["program"] for alias, items in traces.items()
                for trace in items if trace.get("program")}
    goals = {goal["id"]: (goal["turn"], goal["programId"]) for goal in rules["fixedRaceGoals"]}
    rates = {career: 100 + sum(effect["value"] for card in deck for effect in card["effects"] + card["uniqueEffects"]
                               if effect["type"] == 15) for career, deck in rules["decks"].items()}
    career_splits, entries, result = {}, {}, []
    with gzip.open(root / "verified/paired-ledger.jsonl.gz", "rt", encoding="utf-8") as ledger:
        for row in map(json.loads, ledger):
            ref = tuple(row["ref"])
            career = row.get("career")
            if ref in splits:
                career_splits[career] = splits[ref]
            if row["route"] == "race_entry" and ref in programs:
                matches = [goal for goal in row.get("routeGoals", []) if goals.get(goal) == (row["turn"], programs[ref])]
                if len(matches) == 1:
                    entries[ref] = matches[0]
            if row.get("gaps") or row["coverage"] != "pairedTransition" or row["route"] != "check_event":
                continue
            context, event = row.get("raceContext"), row.get("consumed")
            if not context or not event or context[1] != row["before"] or context[2] != 1:
                continue
            goal = entries.get((ref[0], context[0]))
            if goal is None or event[3] != 3 or not 500000000 <= event[0] < 600000000 or event[1] < 10000:
                continue
            if row["delta"].get("vital", 0) != 0:
                raise ValueError(f"Unexpected target race vitality change in {ref}")
            result.append((career, (event[2], goal), rates[career], row))
    return result, career_splits


def comparison(rows, fitted):
    outcome = {"count": len(rows), "baselineExact": 0, "estimateExact": 0, "baselineAbsoluteError": 0, "estimateAbsoluteError": 0}
    for _, key, rate, row in rows:
        seen = [row["delta"].get(stat, 0) for stat in (*STATS, "skill_point")]
        for name, profile in (("baseline", BASE), ("estimate", fitted.get(key, BASE))):
            scaled = [int(x * rate / 100) for x in profile]
            predicted = [displayed(value, cap, amount) for value, cap, amount in
                         zip(row["preResources"][:5], row["preCaps"][:5], scaled[:5], strict=True)] + [scaled[5]]
            outcome[name + "Exact"] += predicted == seen
            outcome[name + "AbsoluteError"] += sum(abs(a - b) for a, b in zip(predicted, seen, strict=True))
    return outcome


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audit", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    rows, career_splits = observations(args.audit)
    split = defaultdict(list)
    for row in rows:
        split[career_splits.get(row[0], "external")].append(row)
    by_goal = defaultdict(list)
    for _, key, rate, row in split["fit"]:
        by_goal[key].append(infer(row, rate))
    fitted = {}
    for key, values in by_goal.items():
        components = [tuple(x for x in v if x is not None) for v in zip(*values, strict=True)]
        result = tuple(round(mean(v), 6) if v else base for v, base in zip(components, BASE, strict=True))
        if result != BASE:
            fitted[key] = result
    results = {name: comparison(items, fitted) for name, items in sorted(split.items())}
    fixed = {key for key, values in by_goal.items() if len(values) >= 10
             and len(set(values)) == 1 and all(type(x) is int for x in values[0])}
    report = {"profiles": len(fitted), "consistentTargets": len(fixed), "results": results, "estimates": [
        {"characterId": character, "goalId": goal, "basis": values, "samples": len(by_goal[character, goal]),
         "consistent": (character, goal) in fixed}
        for (character, goal), values in sorted(fitted.items())
    ]}
    with args.output.open("x", encoding="utf-8", newline="\n") as out:
        json.dump(report, out, indent=2)
        out.write("\n")


if __name__ == "__main__":
    main()
