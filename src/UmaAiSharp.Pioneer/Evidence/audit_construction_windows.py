"""Read-only packet research; writes anonymous numeric evidence, never packet bodies or identities."""

import argparse
import bisect
import collections
import hashlib
import json
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--input", type=Path, required=True)
parser.add_argument("--packets", type=Path, required=True)
parser.add_argument("--master", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
if args.output.exists():
    raise ValueError("Output already exists; choose a new evidence file.")


def fingerprint(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def rows(name):
    with (args.input / name).open(encoding="utf-8") as source:
        for line in source:
            yield json.loads(line)


report = json.loads((args.input / "audit.json").read_text(encoding="utf-8"))
hashes = {row["alias"]: row["sha256"] for row in report["sources"]}
input_hashes = {name: fingerprint(args.input / name) for name in
               ["audit.json", "pioneer-observations.jsonl", "pioneer-actions.jsonl", "exchanges.jsonl"]}
exchanges = {row["key"]: row for row in rows("exchanges.jsonl")}
actions = collections.defaultdict(list)
source_actions = collections.defaultdict(list)
actions_by_id = {}
for row in rows("pioneer-actions.jsonl"):
    actions[row["exchangeKey"]].append(row)
    source_actions[row["source"]].append((row["packetId"], row["endpoint"].rsplit("/", 1)[-1]))
    actions_by_id[(row["source"], row["packetId"])] = row
for items in source_actions.values():
    items.sort()
source_action_ids = {key: [item[0] for item in items] for key, items in source_actions.items()}

previous = {}
pending = collections.defaultdict(list)
fixtures = []
associations = []
unresolved = []
event_counts = collections.Counter()
story_turns = collections.defaultdict(collections.Counter)
windows = [("packets-0004", 22206, 22219), ("packets-0004", 23398, 23417),
           ("packets-0002", 5322, 5330), ("packets-0002", 5402, 5411),
           ("packets-0004", 5313, 5323), ("packets-0004", 6300, 6309),
           ("packets-0004", 6360, 6375), ("packets-0004", 6379, 6386)]
window_rows = [[] for _ in windows]
window_runs = [set() for _ in windows]
skipped = collections.Counter()


def interrupt_pending(alias, record_id, reason):
    for _, _, item in pending[alias]:
        item.setdefault("evidenceInterruptions", []).append({"recordId": record_id, "reason": reason})


for observation in rows("pioneer-observations.jsonl"):
    alias = observation["source"]
    endpoint = observation["endpoint"].rsplit("/", 1)[-1]
    visible = observation["visible"]
    common = visible.get("single_mode_" + endpoint + "_common") if endpoint in ["load", "start", "finish"] else visible
    chara = common.get("chara_info") if isinstance(common, dict) else None
    scenario = visible.get("pioneer_data_set")
    if pending[alias]:
        step_exchange = exchanges.get(observation["exchangeKey"])
        step_requests = actions[observation["exchangeKey"]]
        if not step_exchange or step_exchange["status"] != "Paired" or len(step_requests) != 1 \
                or step_exchange["endpoint"] != observation["endpoint"] \
                or step_requests[0]["source"] != alias \
                or {"source": alias, "id": step_requests[0]["packetId"]} not in step_exchange["requests"] \
                or {"source": alias, "id": observation["packetId"]} not in step_exchange["responses"] \
                or step_requests[0]["packetId"] >= observation["packetId"]:
            interrupt_pending(alias, observation["packetId"], "IntermediateRequestResponsePairingUnproven")
    if not isinstance(chara, dict) or not isinstance(scenario, dict):
        if endpoint != "get_choice_reward":
            interrupt_pending(alias, observation["packetId"], "IntermediateStateOrRunUnavailable")
            previous.pop(alias, None)
        continue
    info = scenario["pioneer_info"]
    events = common.get("unchecked_event_array")
    if not isinstance(events, list):
        interrupt_pending(alias, observation["packetId"], "IntermediateEventArrayUnavailable")
        previous.pop(alias, None)
        continue
    plan = sorted(scenario.get("planning_info_array", []), key=lambda step: step["priority"])
    current = (observation, chara, info, events, plan)
    for event in events:
        if 400011100 <= event["story_id"] <= 400011120:
            event_counts[(event["story_id"], event["event_id"], event["play_timing"])]+=1
        if event["story_id"] in [400011019, 501038524]:
            story_turns[event["story_id"]][chara["turn"]] += 1
    for i, (source, start, end) in enumerate(windows):
        if source == alias and start <= observation["packetId"] <= end:
            window_runs[i].add(observation["runKey"])
            window_rows[i].append({"recordId": observation["packetId"], "endpoint": endpoint,
                "turn": chara["turn"], "phase": chara["playing_state"], "developmentPoints": info["pioneer_pt"],
                "thresholds": info["gain_shima_training_pt_array"], "lastGainRightsNum": info["last_gain_rights_num"],
                "events": [{"eventId": e["event_id"], "storyId": e["story_id"], "rawPlayTiming": e["play_timing"]} for e in events]})
    before = previous.get(alias)
    previous[alias] = current
    if before and observation["runKey"] and observation["runKey"] == before[0]["runKey"] and plan == before[4]:
        old, old_chara, old_info, old_events, _ = before
        thresholds = info["gain_shima_training_pt_array"]
        crossed = [value for value in thresholds if old_info["pioneer_pt"] < value <= info["pioneer_pt"]]
        if crossed and thresholds == old_info["gain_shima_training_pt_array"]:
            requests = actions[observation["exchangeKey"]]
            exchange = exchanges.get(observation["exchangeKey"])
            if len(requests) != 1:
                skipped["RequestMissingOrAmbiguous"] += 1
                continue
            request = requests[0]
            if not exchange or exchange["status"] != "Paired" or exchange["endpoint"] != observation["endpoint"] \
                    or request["source"] != alias or request["endpoint"] != observation["endpoint"] \
                    or {"source": alias, "id": request["packetId"]} not in exchange["requests"] \
                    or {"source": alias, "id": observation["packetId"]} not in exchange["responses"] \
                    or not old["packetId"] < request["packetId"] < observation["packetId"]:
                skipped["PairingOrOrderUnproven"] += 1
                continue
            ids = source_action_ids[alias]
            between = source_actions[alias][bisect.bisect_right(ids, old["packetId"]):bisect.bisect_left(ids, request["packetId"])]
            if any(route != "get_choice_reward" for _, route in between):
                skipped["InterveningMutationRequest"] += 1
                continue
            previews_complete = True
            for preview_id, _ in between:
                preview = actions_by_id[(alias, preview_id)]
                paired_preview = exchanges[preview["exchangeKey"]]
                previews_complete &= paired_preview["status"] == "Paired" \
                    and {"source": alias, "id": preview_id} in paired_preview["requests"] \
                    and all(ref["source"] == alias and preview_id < ref["id"] < request["packetId"]
                            for ref in paired_preview["responses"])
            if not previews_complete:
                skipped["InterveningRewardPreviewIncomplete"] += 1
                continue
            fields = request["fields"]
            request_common = fields
            source_kind = None
            if endpoint == "exec_command":
                request_common = fields["single_mode_exec_command_request_common"]
                if request_common["command_type"] == 1:
                    source_kind = 0
            elif endpoint == "shima_training_exec":
                source_kind = 1
            elif endpoint == "check_event":
                request_common = fields["single_mode_check_event_request_common"]
                selected = [e for e in old_events if e["event_id"] == request_common["event_id"]]
                if len(selected) == 1:
                    source_kind = {400011105: 2, 400011111: 3}.get(selected[0]["story_id"])
            if source_kind is None or request_common["current_turn"] != old_chara["turn"]:
                skipped["PointSourceOrTurnUnproven"] += 1
                continue
            item = {"sourceSha256": hashes[alias], "beforeRecordId": old["packetId"], "requestRecordId": request["packetId"],
                "afterRecordId": observation["packetId"], "rewardPreviewRequestsBetween": len(between),
                "before": {"turn": old_chara["turn"], "rawPlayingState": old_chara["playing_state"], "deckId": old_info["deck_id"],
                    "developmentPoints": old_info["pioneer_pt"], "thresholds": old_info["gain_shima_training_pt_array"],
                    "pendingStoryIds": [e["story_id"] for e in old_events]},
                "pointGain": info["pioneer_pt"] - old_info["pioneer_pt"], "sourceKind": source_kind,
                "crossedThresholds": crossed, "visibleEventsAtPointGain": [e["story_id"] for e in events]}
            pending[alias].append((observation["runKey"], plan, item))
    remaining = []
    for run, pending_plan, item in pending[alias]:
        if run != observation["runKey"] or pending_plan != plan:
            unresolved.append(item)
            continue
        appeared = [e for e in events if e["story_id"] in [400011107, 400011108]]
        if len(appeared) == 1:
            ids = source_action_ids[alias]
            interval = source_actions[alias][bisect.bisect_right(ids, item["afterRecordId"]):bisect.bisect_left(ids, observation["packetId"])]
            for request_id, _ in interval:
                step_request = actions_by_id[(alias, request_id)]
                step_exchange = exchanges[step_request["exchangeKey"]]
                if step_exchange["status"] != "Paired" \
                        or {"source": alias, "id": request_id} not in step_exchange["requests"] \
                        or not step_exchange["responses"] \
                        or not all(ref["source"] == alias and request_id < ref["id"] <= observation["packetId"]
                                   for ref in step_exchange["responses"]):
                    item.setdefault("evidenceInterruptions", []).append({"recordId": request_id,
                        "reason": "IntermediateRequestHasNoPairedResponseWithinWindow"})
            event = appeared[0]
            item.update({"eventRecordId": observation["packetId"], "eventTurn": chara["turn"],
                         "eventStoryId": event["story_id"], "eventRawPlayTiming": event["play_timing"]})
            (associations if item.get("evidenceInterruptions") else fixtures).append(item)
        else:
            remaining.append((run, pending_plan, item))
    pending[alias] = remaining
unresolved.extend(item for items in pending.values() for _, _, item in items)

raw_files = sorted(args.packets.glob("*.sqlite"))
raw_window_reports = []
for i, (alias, start, end) in enumerate(windows):
    path = raw_files[int(alias.rsplit("-", 1)[-1]) - 1]
    actual_hash = fingerprint(path)
    if actual_hash != hashes[alias]:
        raise ValueError("Raw packet file does not match the normalized source hash.")
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as connection:
        records = connection.execute("select id,captured_at_utc,direction,endpoint,request_id,payload "
                                     "from packets where id between ? and ? order by id", (start, end)).fetchall()
        unmatched = []
        for row in records:
            opposite = "response" if row[2] == "request" else "request"
            count = connection.execute("select count(*) from packets where request_id=? and endpoint=? and direction=?",
                                       (row[4], row[3], opposite)).fetchone()[0]
            if count == 0:
                unmatched.append({"recordId": row[0], "direction": row[2], "endpoint": row[3].rsplit("/", 1)[-1]})
    requests = collections.defaultdict(list)
    for row in records:
        requests[row[4]].append(row)
    raw_window_reports.append({"purpose": "DelayedConstruction" if i < 2 else "UnattributedFacilityChange",
        "sourceSha256": actual_hash, "firstRecordId": start, "lastRecordId": end,
        "rawRecordCount": len(records), "continuousRecordIds": [r[0] for r in records] == list(range(start, end + 1)),
        "capturedTimesMonotonic": all(a[1] <= b[1] for a, b in zip(records, records[1:])),
        "uniqueKnownRunCount": len(window_runs[i] - {None}),
        "allRawRequestPairsComplete": all(len(v) == 2 and {r[2] for r in v} == {"request", "response"}
                                            and v[0][3] == v[1][3] for v in requests.values()),
        "duplicatePayloads": len(records) - len({(r[2], r[3], r[5]) for r in records}), "unmatchedRawRecords": unmatched,
        "routes": dict(collections.Counter(r[3].rsplit("/", 1)[-1] for r in records)), "observations": window_rows[i]})
with sqlite3.connect(args.master.resolve().as_uri() + "?mode=ro", uri=True) as connection:
    titles = [{"storyId": row[0], "title": row[1]} for row in connection.execute(
        'select "index",text from text_data where category=181 and "index" in (400011019,501038524,400011105,400011107,400011108,400011110,400011111)')]
result = {"schemaVersion": "1", "inputs": input_hashes, "masterSha256": fingerprint(args.master),
          "rawWindows": raw_window_reports, "masterEventTitles": titles,
          "eventWindows": [{"storyId": key[0], "eventId": key[1], "rawPlayTiming": key[2], "observations": count}
                           for key, count in sorted(event_counts.items())],
          "storyTurnCounts": {str(key): dict(value) for key, value in story_turns.items()},
          "pointCrossingFixtures": fixtures, "associationOnlyCrossings": associations,
          "unresolvedCrossings": unresolved, "exclusions": dict(skipped)}
for name, expected in input_hashes.items():
    if fingerprint(args.input / name) != expected:
        raise ValueError("Normalized evidence changed during the audit.")
with args.output.open("x", encoding="utf-8") as output:
    json.dump(result, output, ensure_ascii=False, indent=2)
print(json.dumps({"matchedCrossings": len(fixtures), "associationOnly": len(associations),
                  "unresolved": len(unresolved), "exclusions": dict(skipped)}))
