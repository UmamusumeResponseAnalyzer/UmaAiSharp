"""Fit action-coupled Tucker events and director attendance from frozen historical runs."""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from ordinary_events import minimum_raw_attribute_gain


def rows(path):
    with path.open(encoding="utf-8") as source:
        yield from map(json.loads, source)


def fit(split_path, boards_path, tucker_path, deltas_path, event_transitions_path, calibration_path, output):
    split = json.loads(split_path.read_bytes())
    groups = {run: "fit" for run in split["fitRuns"]} | {run: "holdout" for run in split["holdoutRuns"]}
    tucker_slots = {}
    directors = defaultdict(Counter)
    seen_boards = set()
    for row in rows(boards_path):
        run = row["runKey"]
        if run not in groups:
            continue
        for card in row["cards"]:
            if card["cardId"] == 30257 and card["level"] == 50:
                tucker_slots[run] = card["position"]
        if row["contextVariants"] != 1 or (run, row["turn"]) in seen_boards:
            continue
        seen_boards.add((run, row["turn"]))
        camp = row["mode"] == "Camp"
        directors[groups[run], camp][any(102 in cell["participants"] for cell in row["loads"])] += 1

    events = defaultdict(list)
    for row in rows(deltas_path):
        run = row["packet"]["runKey"]
        if run in groups and row["alignedBefore"] and row["paired"] and 809049032 in row["stories"]:
            events[run, row["actionTurn"]].append(row["packet"]["packetId"])

    stamina_rewards = {}
    for row in rows(event_transitions_path):
        if row["excluded"] or row["event"]["story_id"] != 809049032:
            continue
        before, after = row["beforeState"], row["afterState"]
        if after["stamina"] >= before["max_stamina"]:
            continue
        gain = after["stamina"] - before["stamina"]
        if gain > 0:
            stamina_rewards[row["after"]["runKey"], row["after"]["packetId"]] = minimum_raw_attribute_gain(before["stamina"], gain)
    repeats = defaultdict(Counter)
    repeat_stamina = defaultdict(Counter)
    seen_actions = set()
    for row in rows(tucker_path):
        run, turn = row["after"]["runKey"], row["turn"]
        if run not in groups or tucker_slots.get(run) not in row["participants"] or not row["match"]:
            continue
        if (run, turn) in seen_actions:
            continue
        seen_actions.add((run, turn))
        # The first verified co-training has its own introduction event.
        if (run, "first") not in seen_actions:
            seen_actions.add((run, "first"))
            continue
        island = row["endpoint"] == "shima_training_exec"
        event = next((packet for packet in events[run, turn] if packet > row["after"]["packetId"]), None)
        group = groups[run], island
        repeats[group][event is not None] += 1
        if (reward := stamina_rewards.get((run, event))) is not None:
            repeat_stamina[group][reward] += 1

    def counts(data, key):
        return [{key: condition, "opportunities": counts.total(), "triggered": counts[True], "group": group}
                for (group, condition), counts in sorted(data.items())]

    repeat_counts = counts(repeats, "island")
    for row in repeat_counts:
        observed = repeat_stamina[row["group"], row["island"]]
        if not observed:
            raise ValueError("Tucker 同训拟合缺少可观察的耐力增量。")
        row["staminaRewards"] = [{"gain": gain, "count": count} for gain, count in sorted(observed.items())]
    director_counts = counts(directors, "camp")
    calibration = json.loads(calibration_path.read_bytes())
    calibration["tuckerRepeat"] = [{k: v for k, v in row.items() if k != "group"}
                                  for row in repeat_counts if row["group"] == "fit"]
    calibration["director"] = [{"camp": row["camp"], "boards": row["opportunities"], "appearances": row["triggered"]}
                               for row in director_counts if row["group"] == "fit"]
    if len(calibration["tuckerRepeat"]) != 2 or len(calibration["director"]) != 2:
        raise ValueError("拟合组缺少普通／岛训 Tucker 或普通／合宿理事长机会。")
    for path, data in ((output, calibration), (output.with_suffix(".report.json"), {
        "tuckerRepeat": repeat_counts, "director": director_counts,
        "inputSha256": {path.name: fingerprint(path) for path in (split_path, boards_path, tucker_path, deltas_path, event_transitions_path)},
    })):
        with path.open("x", encoding="utf-8") as stream:
            json.dump(data, stream, indent=2, ensure_ascii=False)
            stream.write("\n")


def fingerprint(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("split", "boards", "tucker", "deltas", "event_transitions", "calibration", "output"):
        parser.add_argument(name, type=Path)
    fit(**{f"{key}_path" if key != "output" else key: value for key, value in vars(parser.parse_args()).items()})
