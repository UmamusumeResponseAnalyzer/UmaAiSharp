"""Rebuild the historical Omakase baseline and verify Pioneer through stdio MCP."""

import argparse
from collections import Counter
from contextlib import contextmanager
from functools import cache
from hashlib import file_digest
import json
from math import ceil
from pathlib import Path
import random
import sqlite3
from statistics import fmean, mean, median
import subprocess

import msgpack


ATTRIBUTES = ("speed", "stamina", "power", "guts", "wisdom")
CAPTURE_ATTRIBUTES = (*ATTRIBUTES[:4], "wiz")
DECK = (30256, 30257, 30264, 30282, 30289, 30304)
BASELINE_HASHES = (
    "ed9548f71bf6d6b85fad702942dfbf0c5157678e48a772400e409f8377df502b",
    "f487db586de49cd35bb93ac489df49963794cf22cd732be3df077df594361982",
)
BATCH_SEED = "1701411834604692317"
RECOMMEND_SEED = "12345"
BATCH_SAMPLES = 4096
RECOMMEND_SAMPLES = 128


def sha256(path):
    with path.open("rb") as source:
        return file_digest(source, "sha256").hexdigest()


def write_json(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, ensure_ascii=False)
        output.write("\n")


def input_hashes(args):
    inputs = {"mcp": args.mcp, "rules": args.rules, "goal": args.goal, "capture": args.capture}
    inputs.update({name: args.mcp.with_name(name + ".dll") for name in
                   ("UmaAiSharp.Core", "UmaAiSharp.Pioneer", "UmaAiSharp.Pioneer.Game")})
    return {name: sha256(path) for name, path in inputs.items()}


@cache
def attribute_points(value):
    # Mirror ScoringCandidates.StatusToPoint for the offline baseline.
    if value <= 1200:
        rates = (5, 8, 10, 13, 16, 18, 21, 24, 26, 28, 29, 30, 31, 33, 34, 35, 39, 41, 42, 43, 52, 55, 66, 68)
        return sum(max(0, min(51 if band == 23 else 50, value + 1 - 50 * band)) * rate
                   for band, rate in enumerate(rates)) // 10
    if value <= 2000:
        tenths = 38418
        remaining = value - 1200
        for band in range(80):
            count = min(remaining, 9 if band == 0 else 11 if band == 79 else 10)
            tenths += count * ((7910 + 131 * band) // 100)
            remaining -= count
            if remaining == 0:
                break
        return tenths // 10
    delta = value - 2000
    bands, remainder = divmod(delta, 25)
    return 14280 + (183 * delta + 25 * bands * (bands - 1) // 2 + bands * remainder + 1) // 10


def decode(blob):
    return msgpack.unpackb(blob, raw=False, strict_map_key=False)


def read_capture(path):
    return sqlite3.connect(path.as_uri() + "?mode=ro&immutable=1", uri=True)


def baseline_rows(path, source):
    rows = []
    with read_capture(path) as connection:
        active = None
        observed = set()
        packets = connection.execute("""
            SELECT id, direction, endpoint, payload FROM packets
            WHERE endpoint IN ('/umamusume/single_mode_pioneer/start', '/umamusume/single_mode_pioneer/finish',
                               '/umamusume/single_mode_pioneer/exec_command', '/umamusume/single_mode_pioneer/shima_training_exec')
            ORDER BY id
            """)
        for packet, direction, endpoint, blob in packets:
            endpoint = endpoint.rsplit("/", 1)[1]
            item = decode(blob)
            if direction == "request":
                if active is None or endpoint not in ("exec_command", "shima_training_exec"):
                    continue
                request = item["single_mode_exec_command_request_common"] if endpoint == "exec_command" else item
                if request.get("exec_auto_play_plan_id") != 110001:
                    active["omakase"] = False
                if endpoint == "exec_command":
                    active["turns"].add(request["current_turn"])
                continue
            if item.get("response_code") != 1:
                continue
            data = item["data"]
            if endpoint == "start":
                chara = data["single_mode_start_common"]["chara_info"]
                active = {
                    "packet": packet,
                    "identity": (chara["single_mode_chara_id"], chara["card_id"], json.dumps(chara["start_time"], sort_keys=True)),
                    "omakase": True,
                    "turns": set(),
                    "deck": tuple(sorted(card["support_card_id"] for card in chara["support_card_array"])),
                }
            if endpoint != "finish" or active is None:
                continue
            finish = data["single_mode_finish_common"]
            chara = finish["chara_info"]
            identity = (chara["single_mode_chara_id"], chara["card_id"], json.dumps(chara["start_time"], sort_keys=True))
            if identity != active["identity"] or chara["turn"] != 78:
                continue
            selected = [row for row in finish["trained_chara"] if row["trained_chara_id"] == finish["trained_chara_id"]]
            if len(selected) == 1 and active["omakase"] and len(active["turns"]) >= 45 and identity not in observed:
                values = dict(zip(ATTRIBUTES, (chara[key] for key in CAPTURE_ATTRIBUTES)))
                rows.append({
                    "source": source, "start": active["packet"], "finish": packet,
                    "attributes": values, "attributeScore": sum(map(attribute_points, values.values())),
                    "gameRankScore": selected[0]["rank_score"], "sameSupportCards": active["deck"] == DECK,
                })
                observed.add(identity)
            active = None
    return rows


def frozen_baseline(paths, output):
    inputs = []
    rows = []
    for index, (path, expected) in enumerate(zip(paths, BASELINE_HASHES, strict=True), 1):
        source = f"historical-{index}"
        digest = sha256(path)
        if digest != expected:
            raise ValueError(f"{source} SHA-256 应为 {expected}，实际为 {digest}。")
        rows.extend(baseline_rows(path, source))
        if sha256(path) != digest:
            raise ValueError(f"{source} 在扫描中发生变化。")
        inputs.append({"source": source, "sha256": digest})
    scores = sorted(row["attributeScore"] for row in rows)
    if len(scores) != 212 or sum(scores) != 5_499_575 or any(row["sameSupportCards"] for row in rows):
        raise ValueError(f"历史基线重建不一致，实际为 {len(scores)} 局、五维总分合计 {sum(scores)}。")
    write_json(output / "omakase-baseline.json", {
        "autoPlayPlanId": 110001, "inputs": inputs, "count": len(scores), "meanAttributeScore": mean(scores),
        "matchingDeckCount": sum(row["sameSupportCards"] for row in rows), "runs": rows,
    })
    return scores


class Mcp:
    def __init__(self, process):
        self.process = process
        self.sequence = 0

    def request(self, method, params, notify=False):
        self.sequence += 1
        request = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notify:
            request["id"] = self.sequence
        self.process.stdin.write(json.dumps(request) + "\n")
        self.process.stdin.flush()
        if notify:
            return None
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError(f"MCP 进程在 {method} 时关闭了 stdout。")
        reply = json.loads(line)
        if reply.get("id") != self.sequence or "result" not in reply:
            raise RuntimeError(f"MCP {method} 返回了意外消息 {reply}")
        return reply["result"]

    def tool(self, name, **arguments):
        args = {key: str(value) if isinstance(value, Path) else value for key, value in arguments.items()}
        result = self.request("tools/call", {"name": name, "arguments": args})
        if result.get("isError"):
            raise RuntimeError(f"MCP {name} 工具错误 {result}")
        return result["structuredContent"]


@contextmanager
def server(executable, output):
    with output.open("xb") as log, subprocess.Popen(
        ["dotnet", str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
        text=True, encoding="utf-8",
    ) as process:
        try:
            yield Mcp(process)
        finally:
            process.stdin.close()
            try:
                if process.wait(timeout=10) != 0:
                    raise RuntimeError("MCP 子进程退出失败，见 stderr 日志。")
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
                raise
            if process.stdout.read():
                raise RuntimeError("MCP stdout 含未配对的协议消息。")


def inspect_schema(mcp):
    mcp.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                               "clientInfo": {"name": "pioneer-acceptance", "version": "1"}})
    mcp.request("notifications/initialized", {}, notify=True)
    tools = {tool["name"]: tool["inputSchema"] for tool in mcp.request("tools/list", {})["tools"]}
    if len(tools) != 29:
        raise ValueError(f"MCP 工具数量应为 29，实际为 {len(tools)}。")
    for name in ("pioneer_run", "pioneer_replay", "pioneer_batch", "pioneer_recommend"):
        if tools[name]["properties"]["model"].get("default") != "precise":
            raise ValueError(f"{name} 的 MCP model 参数缺少默认值 precise。")


def compare_scores(outcomes, baseline):
    scores = [outcome["verifiedAttributeRating"] for outcome in outcomes]
    baseline_mean = mean(baseline)
    if any(score != sum(map(attribute_points, outcome["attributes"].values()))
           for score, outcome in zip(scores, outcomes)):
        raise ValueError("MCP 终局五维评分与历史基线公式不同。")
    # Resample each population independently; the cross-pairs are not independent observations.
    rng = random.Random(20261113)
    deltas = sorted(fmean(rng.choices(scores, k=len(scores))) - fmean(rng.choices(baseline, k=len(baseline)))
                    for _ in range(1000))
    return {
        "meanAttributeScore": mean(scores), "medianAttributeScore": median(scores),
        "minimumAttributeScore": min(scores), "maximumAttributeScore": max(scores),
        "fractionAboveHistoricalMean": mean(score > baseline_mean for score in scores),
        "historicalMeanDifference": mean(scores) - baseline_mean,
        "historicalMeanDifferenceLower95": deltas[ceil(0.05 * len(deltas)) - 1],
        "reaches1201Stamina": sum(outcome["attributes"]["stamina"] >= 1201 for outcome in outcomes),
        "allAttributesReach95PercentOfCap": sum(
            all(outcome["attributes"][key] * 100 >= outcome["caps"][key] * 95 for key in ATTRIBUTES)
            for outcome in outcomes
        ),
        "meanAttributes": {key: mean(outcome["attributes"][key] for outcome in outcomes) for key in ATTRIBUTES},
        "meanShortfallFromCaps": {key: mean(outcome["caps"][key] - outcome["attributes"][key] for outcome in outcomes)
                                  for key in ATTRIBUTES},
        "meanFutureSkillPoints": mean(outcome["futureGainedSkillPoints"] for outcome in outcomes),
    }


def result_summary(report, baseline, model):
    rows = report["results"]
    if f"/{model}/" not in report["provenance"]["rulesIdentity"]:
        raise ValueError(f"MCP 轨迹来源缺少 {model} 模型版本。")
    if [row["sampleIndex"] for row in rows] != list(range(report["sampleCount"])):
        raise ValueError("MCP 批次样本编号不完整。")
    completed = [row["outcome"] for row in rows if row["status"] == "completed"]
    stops = Counter(f'{row["stop"]["reason"]}/{row["stop"]["code"]}/{row["stop"]["position"]}'
                    for row in rows if row.get("stop"))
    summary = {
        "sampleCount": len(rows), "completed": len(completed), "stops": dict(stops),
        "executionMilliseconds": report["executionMilliseconds"], "allocatedBytes": report["processAllocatedBytes"],
    }
    summary["historicalScreenPass"] = False
    if len(rows) == len(completed):
        summary.update(compare_scores(completed, baseline))
        summary["historicalScreenPass"] = summary["historicalMeanDifferenceLower95"] > 0
        summary["historical95PercentTargetReached"] = summary["fractionAboveHistoricalMean"] >= 0.95
    return summary


def search_summary(mcp, folder, paths, model, workers, samples=RECOMMEND_SAMPLES):
    output = folder / f"recommend-{model}-w{workers}.json"
    mcp.tool("pioneer_recommend", **paths, candidatesPath=folder / "candidates.json",
             samples=samples, workers=workers, seed=RECOMMEND_SEED, outputPath=output, model=model)
    report = json.loads(output.read_bytes())
    search = report["search"]
    for candidate in search["candidates"]:
        if f"/{model}/" not in candidate["batch"]["provenance"]["rulesIdentity"]:
            raise ValueError(f"MCP 推荐来源缺少 {model} 模型版本。")
        # Memory plans may differ across worker counts.
        candidate["batch"].pop("memory", None)
    if search["status"] == "completed":
        summaries = {candidate["candidate"]["id"]: candidate["summary"] for candidate in search["candidates"]}
        ranking = search["ranking"]
        if len(ranking) != len(summaries) or set(ranking) != set(summaries) or ranking[0] != search["recommendedCandidateId"]:
            raise ValueError("MCP 推荐报告缺失候选或首选与排序不一致。")
        for left_id, right_id in zip(ranking, ranking[1:]):
            left, right = summaries[left_id], summaries[right_id]
            if left["successCount"] < right["successCount"]:
                raise ValueError("MCP 推荐把较低达标率排在较高达标率之前。")
            if left["successCount"] == right["successCount"] and left["successCount"] > 0:
                if left["meanSuccessfulAttributeRating"] < right["meanSuccessfulAttributeRating"]:
                    raise ValueError("MCP 推荐在达标率相同时把较低的五维均分排在前面。")
    return search, report["executionMilliseconds"]


def verify(mcp, args, scores):
    inspect_schema(mcp)
    checks = []
    for packet, expected_turn in ((196, 1), (234, 3)):
        folder = args.output / f"packet-{packet}"
        imported = mcp.tool("pioneer_import_capture", capturePath=args.capture, packetId=str(packet),
                            rulesPath=args.rules, outputDirectory=folder)
        if imported["status"] != "completed":
            checks.append({"packet": packet, "importStatus": imported["status"], "summary": imported.get("summary")})
            continue
        paths = {"inputPath": folder / "input.json", "goalPath": args.goal, "rulesPath": args.rules}
        panel = json.loads(paths["inputPath"].read_bytes())["start"]["data"]
        if panel["turn"] != expected_turn or panel["cardId"] != 114901 or tuple(sorted(card["cardId"] for card in panel["deck"])) != DECK:
            raise ValueError(f"当前抓包 #{packet} 的回合或编成与验收对象不符。")
        row = {"packet": packet, "turn": expected_turn, "importStatus": "completed", "models": {}}
        checks.append(row)
        run_path = folder / "run-precise.json"
        mcp.tool("pioneer_run", **paths, seed=RECOMMEND_SEED, model="precise", outputPath=run_path)
        run = json.loads(run_path.read_bytes())
        terminal = run["finalState"]["header"]
        row["singleRun"] = {
            "completed": run["completed"] == 1 and terminal["turn"] == 78 and terminal["stage"] == "terminal",
            "completedInheritances": terminal["completedInheritances"],
            "completedCheckpoint": terminal["completedCheckpoint"],
            "completedUraRaces": terminal["completedUraRaces"],
        }
        reports = {}
        for model in ("precise", "fast"):
            for workers in ((args.workers, 1) if packet == 196 and model == "precise" else (args.workers,)):
                output = folder / f"batch-{model}-w{workers}.json"
                mcp.tool("pioneer_batch", **paths, seed=BATCH_SEED, samples=BATCH_SAMPLES,
                         workers=workers, model=model, outputPath=output)
                report = json.loads(output.read_bytes())
                summary = result_summary(report, scores, model)
                if workers == 1:
                    row["models"][model]["batchWorkerEquality"] = (report["results"] == reports[model]["results"]
                                                                    and report["provenance"] == reports[model]["provenance"])
                else:
                    row["models"][model] = {"batch": summary}
                    reports[model] = report
            search, milliseconds = search_summary(mcp, folder, paths, model, args.workers)
            compare, single_milliseconds = search_summary(mcp, folder, paths, model, 1) if packet == 196 and model == "precise" else (None, None)
            recommendation = search["recommendedCandidateId"]
            best = next((candidate["summary"] for candidate in search["candidates"]
                         if candidate["candidate"]["id"] == recommendation), None)
            row["models"][model].update(searchStatus=search["status"], ranking=search["ranking"],
                                        recommendationSummary=best,
                                        recommendationWorkerEquality=search == compare if compare is not None else None,
                                        recommendationMilliseconds={f"workers{args.workers}": milliseconds, "workers1": single_milliseconds})
            print(f"#{packet} {model}: {summary['completed']}/{BATCH_SAMPLES}, "
                  f"mean={summary.get('meanAttributeScore')}, search={search['status']}", flush=True)
        precise = reports["precise"]["results"]
        fast = reports["fast"]["results"]
        if all(result["status"] == "completed" for result in precise + fast):
            paired = list(zip((sample["outcome"] for sample in precise), (sample["outcome"] for sample in fast), strict=True))
            row["modelComparison"] = {
                "meanAbsoluteAttributeRatingDifference": mean(
                    abs(x["verifiedAttributeRating"] - y["verifiedAttributeRating"]) for x, y in paired
                ),
                "meanAbsoluteTerminalAttributeDifferences": {
                    key: mean(abs(x["attributes"][key] - y["attributes"][key]) for x, y in paired) for key in ATTRIBUTES
                },
                "fastVsPreciseSpeedup": (reports["precise"]["executionMilliseconds"]
                                         / reports["fast"]["executionMilliseconds"]),
            }
    return checks


def verify_planning(mcp, args):
    folder = args.output / "packet-228"
    imported = mcp.tool("pioneer_import_capture", capturePath=args.capture, packetId="228",
                        rulesPath=args.rules, outputDirectory=folder)
    if imported["status"] != "completed":
        return {"packet": 228, "importStatus": imported["status"], "summary": imported.get("summary")}
    paths = {"inputPath": folder / "input.json", "goalPath": args.goal, "rulesPath": args.rules}
    panel = json.loads(paths["inputPath"].read_bytes())["start"]
    if panel["kind"] != "planning" or panel["data"]["state"]["turn"] != 3:
        raise ValueError("抓包 #228 的决策不是第 3 回合建设规划。")
    modes = {}
    for model in ("precise", "fast"):
        search, elapsed = search_summary(mcp, folder, paths, model, args.workers, samples=8)
        ranking = search["ranking"]
        plans = {candidate["candidate"]["id"]: candidate for candidate in search["candidates"]}
        best = plans[ranking[0]] if ranking else None
        print(f"#228 planning {model}: {len(search['candidates'])} candidates, search={search['status']}", flush=True)
        modes[model] = {
            "searchStatus": search["status"], "candidateCount": len(search["candidates"]),
            "samplesPerCandidate": 8, "recommendedCandidateId": search["recommendedCandidateId"],
            "recommendedSteps": best["candidate"]["action"]["plan"]["steps"] if best else None,
            "recommendationSummary": best["summary"] if best else None,
            "executionMilliseconds": elapsed,
        }
    return {"packet": 228, "importStatus": "completed", "models": modes}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mcp", type=Path, required=True)
    parser.add_argument("--rules", type=Path, required=True)
    parser.add_argument("--goal", type=Path, required=True)
    parser.add_argument("--capture", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, nargs=2, required=True, metavar=("CAPTURE_1", "CAPTURE_2"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--workers", type=int, choices=range(2, 33), default=32, metavar="2–32")
    args = parser.parse_args()
    args.baseline = [path.resolve(strict=True) for path in args.baseline]
    for name in ("mcp", "rules", "goal", "capture"):
        setattr(args, name, getattr(args, name).resolve(strict=True))
    args.output = args.output.resolve()
    args.output.mkdir()
    fingerprints = input_hashes(args)
    baseline = frozen_baseline(args.baseline, args.output)
    with server(args.mcp, args.output / "stderr.log") as mcp:
        checks = verify(mcp, args, baseline)
        planning = verify_planning(mcp, args)
    if input_hashes(args) != fingerprints:
        raise ValueError("验收期间 MCP 程序集、抓包、规则或目标发生变化。")
    write_json(args.output / "verification.json", {
        "comparison": "historicalOmakase",
        "batchSeed": BATCH_SEED, "recommendSeed": RECOMMEND_SEED,
        "inputsSha256": fingerprints, "historicalBaseline": {"count": len(baseline), "meanAttributeScore": mean(baseline)},
        "checks": checks, "planning": planning,
    })
    if any(row["importStatus"] != "completed"
           or row["singleRun"] != {"completed": True, "completedInheritances": 2,
                                   "completedCheckpoint": 5, "completedUraRaces": 3}
           or any(model["batch"]["completed"] != BATCH_SAMPLES or model["searchStatus"] != "completed"
                  or model["recommendationWorkerEquality"] is False or not model.get("batchWorkerEquality", True)
                  for model in row["models"].values()) for row in checks):
        raise SystemExit("MCP 轨迹或 worker 一致性未通过，见 verification.json。")
    if planning["importStatus"] != "completed" or any(model["searchStatus"] != "completed" for model in planning["models"].values()):
        raise SystemExit("第 3 回合建设规划未通过，见 verification.json。")
    if any(not model["batch"]["historicalScreenPass"] for row in checks for model in row["models"].values()):
        raise SystemExit("模型终局均分未显著高于历史 Omakase 基线，见 verification.json。")


if __name__ == "__main__":
    main()
