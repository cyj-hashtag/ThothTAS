#!/usr/bin/env python3
"""Read real replay logs; never launch a process or modify game/host/log files.

Usage: python scripts/Summarize-Coverage.py [project_root] [--no-cache]
The output is a bounded evidence index, not a substitute for ReplayVerifier's
complete byte-for-byte comparison. No static component is a gameplay event.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import sys

VERSION = 1
RULE_VERSION = 2
LABELS = {
    "movement": "脚本移动与玩家 XY 变化",
    "bullet_observed": "新活动子弹进入帧快照",
    "collision_callback": "碰撞/触发回调实际调用",
    "player_death": "玩家死亡字段变化",
    "room_completed": "房间完成字段变化",
    "scene_change": "不同场景之间切换",
    "natural_transition": "房间完成后的自然转场证据",
    "teleport_callback": "传送回调或运行事件记录",
    "teleport_jump": "传送回调记录真实 XY 跳跃",
    "teleport_candidate": "传送几何与状态联合候选",
    "boss_state_change": "Boss 实例字段实际变化",
    "laser_beat_change": "LaserBeat 激光开关实际变化",
    "audio_clock_progress": "玩法音频时钟推进",
}
BOSS = re.compile(r"/(?:BigBoss|BossBouncer|Boss|FinalBoss)(?:#\d+)?\.")
SITE = re.compile(r"^([^:|]+)[:|](?:0x)?([0-9a-fA-F]+)[:|](?:IL_)?([0-9a-fA-F]+)")


def utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def read_json(path: Path):
    with path.open(encoding="utf-8-sig") as stream:
        return json.load(stream)


def relative(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root).as_posix()
    except ValueError:
        return str(path.resolve())


def get(mapping, name, default=None):
    return mapping.get(name, mapping.get(name[:1].lower() + name[1:], default))


def fingerprint(directory: Path) -> dict:
    # Size + modification time bound the cache to this exact log snapshot.
    answer = {}
    for name in ("frames.jsonl", "random.tsv", "phases.tsv", "runtime-events.txt", "events.log",
                 "host-run.json", "process-start.json", "script.tas"):
        p = directory / name
        if p.exists():
            s = p.stat()
            answer[name] = {"bytes": s.st_size, "mtimeNs": s.st_mtime_ns}
    return answer


def numeric(value):
    try:
        v = float(str(value).split("@", 1)[0])
        return v if math.isfinite(v) else None
    except (ValueError, TypeError):
        return None


def xy(value):
    if not isinstance(value, str):
        return None
    values = value.split(",")
    if len(values) < 2:
        return None
    x, y = numeric(values[0]), numeric(values[1])
    return (x, y) if x is not None and y is not None else None


def canonical_site(raw):
    match = SITE.match(raw)
    if not match:
        return None
    assembly, token, offset = match.groups()
    return f"{assembly.removesuffix('.dll')}:0x{int(token, 16):08x}:IL_{int(offset, 16):04x}"


def evidence(file: str, line: int, frame: int | None, **data) -> dict:
    return {"file": file, "line": line, "frame": frame, **data}


def add_event(run, label: str, item: dict, limit: int):
    group = run["events"].setdefault(label, {"label": LABELS[label], "count": 0, "samples": []})
    group["count"] += 1
    if len(group["samples"]) < limit:
        group["samples"].append(item)


def summarize_run(directory: Path, root: Path, catalog: dict, limit: int) -> dict:
    run = {"directory": relative(directory, root), "sourceSnapshot": fingerprint(directory),
           "warnings": [], "events": {}, "scenes": [], "randomSites": {},
           "phases": {}, "frameRecords": 0, "firstFrame": None, "lastFrame": None,
           "ruleVersion": RULE_VERSION}
    for name, key in (("host-run.json", "host"), ("process-start.json", "processStart")):
        p = directory / name
        if p.exists():
            try:
                run[key] = {"file": relative(p, root), "data": read_json(p)}
            except (OSError, ValueError) as exc:
                run["warnings"].append(f"{name}: {exc}")

    # Retain one real phase witness per interesting class/frame. Every callback
    # name also has an independent total and first occurrence in the source TSV.
    phase_frame = defaultdict(dict)
    p = directory / "phases.tsv"
    phase_file = relative(p, root)
    phase_count = 0
    if p.exists():
        with p.open(encoding="utf-8-sig") as stream:
            for line_no, raw in enumerate(stream, 1):
                if line_no == 1 and raw.startswith("index\t"):
                    continue
                cols = raw.rstrip("\r\n").split("\t")
                if len(cols) != 3:
                    run["warnings"].append(f"phases.tsv:{line_no}: incomplete record")
                    continue
                try:
                    frame = int(cols[1])
                except ValueError:
                    run["warnings"].append(f"phases.tsv:{line_no}: invalid frame")
                    continue
                callback = cols[2]
                proof = evidence(phase_file, line_no, frame, phase=callback)
                item = run["phases"].setdefault(callback, {"count": 0, "first": proof})
                item["count"] += 1
                phase_count += 1
                if callback.startswith(("Teleporter.", "LaserBeat.", "RhythmLaser.",
                                        "Boss.", "BigBoss.", "BossBouncer.", "FinalBoss.")):
                    phase_frame[frame].setdefault(callback, proof)
                if ".OnCollision" in callback or ".OnTrigger" in callback:
                    add_event(run, "collision_callback", proof, limit)
                if callback == "Teleporter.OnOtherReceiverOn":
                    add_event(run, "teleport_callback", proof, limit)
    else:
        run["warnings"].append("missing phases.tsv")
    run["phaseCalls"] = phase_count

    p = directory / "random.tsv"
    random_file = relative(p, root)
    random_count = 0
    random_frames = Counter()
    if p.exists():
        with p.open(encoding="utf-8-sig") as stream:
            for line_no, raw in enumerate(stream, 1):
                if line_no == 1 and raw.startswith("index\t"):
                    continue
                cols = raw.rstrip("\r\n").split("\t")
                if len(cols) != 7:
                    run["warnings"].append(f"random.tsv:{line_no}: incomplete record")
                    continue
                try:
                    frame, index = int(cols[1]), int(cols[0])
                except ValueError:
                    run["warnings"].append(f"random.tsv:{line_no}: invalid index/frame")
                    continue
                site = canonical_site(cols[3]) or cols[3]
                proof = evidence(random_file, line_no, frame, phase=cols[2],
                                 index=index, runtimeSite=cols[3], api=cols[4],
                                 arguments=cols[5], result=cols[6])
                item = run["randomSites"].setdefault(site, {"count": 0, "first": proof,
                         "staticCatalogMatch": site in catalog, "initializationCalls": 0,
                         "replayCalls": 0})
                item["count"] += 1
                item["initializationCalls" if frame < 0 else "replayCalls"] += 1
                if site in catalog and catalog[site]["api"] != cols[4]:
                    run["warnings"].append(f"random.tsv:{line_no}: API differs from static catalog")
                random_count += 1
                random_frames["initialization" if frame < 0 else "replay"] += 1
    else:
        run["warnings"].append("missing random.tsv")
    run["randomCalls"] = random_count
    run["randomCallStages"] = dict(random_frames)

    previous = None
    completed = None
    p = directory / "frames.jsonl"
    if p.exists():
        frame_file = relative(p, root)
        with p.open(encoding="utf-8-sig") as stream:
            for line_no, raw in enumerate(stream, 1):
                try:
                    record = json.loads(raw)
                    frame, scene, fields = int(record["frame"]), record["scene"], record["fields"]
                    if not isinstance(fields, dict):
                        raise ValueError("fields is not an object")
                except (ValueError, KeyError, TypeError) as exc:
                    run["warnings"].append(f"frames.jsonl:{line_no}: {exc}")
                    continue
                run["frameRecords"] += 1
                if run["firstFrame"] is None:
                    run["firstFrame"] = frame
                    run["baseline"] = evidence(frame_file, line_no, frame, scene=scene,
                             randomCount=record.get("randomCount"), randomHash=record.get("randomHash"),
                             phaseSummary=record.get("phases"))
                run["lastFrame"] = frame
                if not run["scenes"] or run["scenes"][-1]["scene"] != scene:
                    run["scenes"].append({"scene": scene, "firstFrame": frame,
                                          "lastFrame": frame, "first": evidence(frame_file, line_no, frame,
                                                                                 field="scene", value=scene)})
                else:
                    run["scenes"][-1]["lastFrame"] = frame

                # Reduce snapshots immediately; large pool and material lists are
                # not retained. Comparison remains the verifier's responsibility.
                selected = {k: v for k, v in fields.items() if
                    "/Player." in k or "/Teleporter." in k or BOSS.search(k) or
                    k.endswith(".Room.roomCompleted") or k.endswith(".Room.playerDeathCount") or
                    k.endswith(".Bullet.timeAlive") or k.endswith(".LaserBeat.lasersAreOn") or
                    k.startswith("AudioClock.") or k.startswith("Input.") or k.startswith("Globals.")}
                proof = lambda key, value: evidence(frame_file, line_no, frame, scene=scene,
                                                     field=key, value=value)
                if previous is not None:
                    old_frame, old_scene, old_fields, old_line = previous
                    contiguous = frame == old_frame + 1
                    if not contiguous:
                        run["warnings"].append(f"frames.jsonl:{line_no}: frame sequence {old_frame}->{frame}")
                    if scene != old_scene:
                        item = proof("scene", scene)
                        item["before"] = evidence(frame_file, old_line, old_frame, field="scene", value=old_scene)
                        add_event(run, "scene_change", item, limit)
                        if completed and completed["scene"] == old_scene:
                            natural = dict(item)
                            natural["roomCompletion"] = completed
                            natural["interpretation"] = "different scene after observed roomCompleted=True; no reload-only inference"
                            add_event(run, "natural_transition", natural, limit)
                        completed = None
                    if contiguous and scene == old_scene:
                        for key, value in selected.items():
                            old = old_fields.get(key)
                            if old == value:
                                continue
                            item = proof(key, value)
                            item["before"] = evidence(frame_file, old_line, old_frame, field=key, value=old)
                            phases = list(phase_frame.get(frame, {}).values())
                            if key.endswith(".Player.isDead") and old == "False" and value == "True":
                                add_event(run, "player_death", item, limit)
                            if key.endswith(".Room.roomCompleted") and old == "False" and value == "True":
                                completed = item
                                add_event(run, "room_completed", item, limit)
                            if BOSS.search(key) and old is not None:
                                # Music/track names and mere instance existence never qualify.
                                member = key.rsplit(".", 1)[-1]
                                if not key.endswith((".position", ".rotation", ".scale", ".velocity", ".angularVelocity")):
                                    item["relatedPhases"] = [x for x in phases if x["phase"].startswith(
                                        ("Boss.", "BigBoss.", "BossBouncer.", "FinalBoss."))]
                                    item["member"] = member
                                    add_event(run, "boss_state_change", item, limit)
                            if key.endswith(".LaserBeat.lasersAreOn") and old is not None:
                                item["relatedPhases"] = [x for x in phases if x["phase"].startswith("LaserBeat.")]
                                clocks = {k: v for k, v in selected.items() if k.startswith("AudioClock.")}
                                item["audioClockFields"] = clocks
                                add_event(run, "laser_beat_change", item, limit)
                            if key.startswith("AudioClock.") and key.endswith(".time") and old is not None:
                                now, before = numeric(value), numeric(old)
                                if now is not None and before is not None and now > before:
                                    add_event(run, "audio_clock_progress", item, limit)
                            if key.endswith(".Bullet.timeAlive") and old is None:
                                item["qualification"] = "active bullet newly captured; not a claim about exact spawn callback timing"
                                add_event(run, "bullet_observed", item, limit)
                            if key.endswith("/Player.position") and old is not None:
                                a, b = xy(old), xy(value)
                                if not a or not b or a == b:
                                    continue
                                jump = math.dist(a, b)
                                if numeric(selected.get("Input.moveX")) not in (None, 0) or numeric(selected.get("Input.moveY")) not in (None, 0):
                                    move = dict(item)
                                    move["input"] = {k: selected.get(k) for k in ("Input.moveX", "Input.moveY")}
                                    add_event(run, "movement", move, limit)
                                if jump >= 2.0:
                                    # Native code writes player XY to Teleporter XY,
                                    # deactivates it, and starts slowdown. Require
                                    # this joint geometry/field pattern, not presence.
                                    for tkey, tvalue in selected.items():
                                        if not tkey.endswith("/Teleporter.position"):
                                            continue
                                        txy = xy(tvalue)
                                        prefix = tkey.removesuffix(".position")
                                        active_key = prefix + ".Teleporter.teleporterActive"
                                        scale_key = prefix + ".Teleporter.timeScalerActive"
                                        old_active, active = old_fields.get(active_key), selected.get(active_key)
                                        scaler = selected.get(scale_key)
                                        related = [x for x in phases if x["phase"].startswith("Teleporter.")]
                                        if txy and math.dist(b, txy) <= 0.75 and related and (
                                            old_active == "True" or scaler == "True"):
                                            candidate = dict(item)
                                            candidate["xyDistance"] = jump
                                            candidate["targetDistance"] = math.dist(b, txy)
                                            candidate["relatedPhases"] = related
                                            candidate["teleporterFields"] = [proof(tkey, tvalue),
                                                proof(active_key, active), proof(scale_key, scaler)]
                                            candidate["teleporterBefore"] = evidence(frame_file, old_line, old_frame,
                                                field=active_key, value=old_active)
                                            candidate["qualification"] = "candidate only: no direct OnOtherReceiverOn/static teleport flag in captured fields"
                                            add_event(run, "teleport_candidate", candidate, limit)
                previous = frame, scene, selected, line_no
                run["last"] = evidence(frame_file, line_no, frame, scene=scene,
                            randomCount=record.get("randomCount"), randomHash=record.get("randomHash"),
                            phaseSummary=record.get("phases"))
    else:
        run["warnings"].append("missing frames.jsonl")

    run["completion"] = None
    for p in (directory / "runtime-events.txt", directory / "events.log"):
        if not p.exists():
            continue
        with p.open(encoding="utf-8-sig") as stream:
            for line_no, raw in enumerate(stream, 1):
                match = re.search(r"complete;frames=(\d+);randomCount=(\d+)", raw)
                if match:
                    run["completion"] = evidence(relative(p, root), line_no, int(match[1]),
                                                 field="runtime completion", value=raw.strip())
                if re.search(r"(?:teleport callback|teleport jump|teleport executed|Teleporter\.OnOtherReceiverOn)", raw, re.I):
                    frame_match = re.search(r"frame=(\d+)", raw)
                    # Evidence without an attributable frame cannot be a path label.
                    if frame_match:
                        callback = evidence(relative(p, root), line_no, int(frame_match[1]),
                                  phase="Teleporter.OnOtherReceiverOn return observer", value=raw.strip())
                        add_event(run, "teleport_callback", callback, limit)
                        data = dict(part.split("=", 1) for part in raw.strip().split(";")[1:] if "=" in part)
                        before, after = xy(data.get("before")), xy(data.get("after"))
                        # Active is captured AFTER the original method returns;
                        # the real branch deactivates the teleporter. An explicit
                        # activeBefore is stronger if future logs include it.
                        active_before = data.get("activeBefore")
                        if (data.get("player1Flag") == "True" and before and after and before != after and
                            active_before != "False"):
                            jump = dict(callback)
                            jump["fields"] = {**data, "activeAfter": data.get("active")}
                            jump["xyDistance"] = math.dist(before, after)
                            jump["qualification"] = "real callback before/after XY change plus player1Flag=True; active is post-return, normally False"
                            add_event(run, "teleport_jump", jump, limit)
    expected = run.get("host", {}).get("data", {}).get("frames")
    run["completeLog"] = bool(run["completion"] and run["firstFrame"] == 0 and
                              run["lastFrame"] == run["completion"]["frame"] and
                              run["frameRecords"] == run["lastFrame"] + 1 and
                              (expected is None or expected == run["lastFrame"]))
    run["sourceChangedWhileReading"] = run["sourceSnapshot"] != fingerprint(directory)
    if run["sourceChangedWhileReading"]:
        run["warnings"].append("source files changed while reading: active snapshot, not final verification")
        run["completeLog"] = False
    return run


def summarize_reports(paths: list[Path], root: Path, input_root: Path):
    reports, references = [], defaultdict(list)
    for path in paths:
        entry = {"file": relative(path, root)}
        try:
            raw = read_json(path)
            if not isinstance(raw, dict) or "Passed" not in raw and "passed" not in raw:
                continue
            differences = get(raw, "Differences", []) or []
            if get(raw, "Difference") is not None:
                differences += [get(raw, "Difference")]
            failed = get(raw, "FailedRuns", []) or []
            error, passed = get(raw, "Error"), get(raw, "Passed", False)
            entry.update({"status": "failed" if error or differences or failed else
                         "passed" if passed is True else "pending-or-not-passed",
                         "passedFlag": passed, "error": error, "differences": differences,
                         "failedRuns": failed, "startedUtc": get(raw, "StartedUtc"),
                         "scope": get(raw, "Scope"), "runReferences": []})
            for number, value in enumerate(get(raw, "Runs", []) or []):
                item = {"Directory": value} if isinstance(value, str) else value
                dirname = get(item, "Directory")
                if not dirname:
                    continue
                directory = Path(dirname).resolve()
                try:
                    directory.relative_to(input_root)
                except ValueError:
                    entry.setdefault("warnings", []).append(f"run directory outside input scope: {dirname}")
                    continue
                ref = {"report": entry["file"], "reportStatus": entry["status"],
                       "runIndex": number, "mode": get(item, "Mode", "editor/unspecified"),
                       "speed": get(item, "Speed"), "declaredFrames": get(item, "Frames"),
                       "pauseStable": get(item, "PauseStable")}
                references[str(directory)].append(ref)
                entry["runReferences"].append({"directory": relative(directory, root), **ref})
            for number, item in enumerate(failed):
                dirname = get(item, "Directory")
                if dirname:
                    directory = Path(dirname).resolve()
                    try:
                        directory.relative_to(input_root)
                    except ValueError:
                        continue
                    references[str(directory)].append({"report": entry["file"],
                        "reportStatus": "failed", "failedRunIndex": number,
                        "mode": get(item, "Mode"), "speed": get(item, "Speed"),
                        "error": get(item, "Error"), "failureFrame": get(item, "Frame")})
        except (OSError, ValueError) as exc:
            entry.update(status="unreadable", error=str(exc))
        reports.append(entry)
    return reports, references


def write_markdown(result: dict) -> str:
    def cell(value):
        return str(value).replace("|", "\\|").replace("\n", " ").replace("\r", " ")

    def cite(proof):
        path = proof["file"]
        frame = proof.get("frame")
        member = proof.get("field", proof.get("phase", ""))
        return f"`{path}:{proof['line']}`，frame={frame}，`{member}`"

    summary = result["summary"]
    lines = ["# 实际回放覆盖索引", "", f"生成时间：{result['generatedUtc']}。输入：`{result['input']}`。", "",
        "只读取已有真实游戏日志和验证报告。此索引不重新执行游戏，也不代替逐帧与完整随机记录比较；通过状态引用原验证报告。静态类、静态调用点以及载入画面不能独立证明玩法路径已执行。", "",
        f"报告：通过 {summary['passedReports']}，失败 {summary['failedReports']}，进行中/未通过 {summary['pendingReports']}。运行日志 {summary['runs']} 份，其中被通过报告引用且日志完整 {summary['passedCompletedRuns']} 份。", "",
        "## 验证报告", "", "| 文件 | 状态 | 成功记录 run | 失败记录 run | 分歧 |", "|---|---|---:|---:|---:|"]
    for report in result["reports"]:
        lines.append(f"| `{cell(report['file'])}` | {report['status']} | {len(report.get('runReferences', []))} | {len(report.get('failedRuns', []))} | {len(report.get('differences', []))} |")
        if report.get("error"):
            lines.extend(["", f"错误：`{cell(report['error'])[:700]}`", ""])
    lines.extend(["", "## 每次运行", "", "| 日志目录 | 验证引用 | 帧范围/记录数 | 场景 | 随机点/调用 | 完成 |", "|---|---|---|---|---:|---|"])
    for run in result["runs"]:
        modes = ", ".join(f"{x['reportStatus']}:{x.get('mode')}@{x.get('speed')}" for x in run["reportReferences"]) or "无通过验证报告引用"
        scenes = ", ".join(f"{x['scene']}({x['firstFrame']}..{x['lastFrame']})" for x in run["scenes"])
        lines.append(f"| `{cell(run['directory'])}` | {cell(modes)} | {run['firstFrame']}..{run['lastFrame']} / {run['frameRecords']} | {cell(scenes)} | {len(run['randomSites'])}/{run['randomCalls']} | {'完整' if run['completeLog'] else '未确认完整'} |")
    lines.extend(["", "## 玩法证据", "",
        "下面的次数包括重复回放，不能解释为独立玩法类型数。JSON 保留每次运行的计数与有限样本。传送回调本身可能只执行未激活分支；真实跳跃要求回调观测的 player1Flag=True 且 before/after XY 变化。active 在返回后读取，真正跳跃会使其 False，不能要求返回后仍 True。没有回调观测时，几何与状态的联合匹配仍只算候选。", "",
        "| 标签 | 日志事件次数 | 通过完整 run 数 | 首个实际证据 |", "|---|---:|---:|---|"])
    for label, title in LABELS.items():
        item = result["pathCoverage"][label]
        proof = cite(item["first"]) if item["first"] else "未观察到符合条件的证据"
        lines.append(f"| {title} | {item['count']} | {len(item['passedCompletedRuns'])} | {cell(proof)} |")
    lines.extend(["", "## 随机调用点", "",
        f"静态直接调用点 {result['randomCoverage']['staticSites']}；本输入日志实际调用 {result['randomCoverage']['observedStaticSites']}；其中通过完整运行覆盖 {result['randomCoverage']['observedStaticSitesInPassedRuns']}。未匹配运行点 {len(result['randomCoverage']['unknownRuntimeSites'])}。首次证据 frame=-1 表示建立基线前初始化，不能计作脚本输入阶段路径。", "",
        "| 静态 siteId | 调用次数 | 初始化/回放 | 通过完整 run 数 | 首次证据 |", "|---|---:|---|---:|---|"])
    for site, item in sorted(result["randomCoverage"]["observed"].items()):
        lines.append(f"| `{site}` | {item['count']} | {item['initializationCalls']}/{item['replayCalls']} | {len(item['passedCompletedRuns'])} | {cell(cite(item['first']))} |")
    lines.extend(["", "未观察到的静态 siteId 完整列在 `coverage.json` 的 `randomCoverage.unobservedStaticSites`，不能据此断言调用不可达。", "",
                  "## 实际阶段", "", "| 回调名称 | 次数 | 首个文件与帧 |", "|---|---:|---|"])
    for name, item in sorted(result["phaseCoverage"].items()):
        lines.append(f"| `{name}` | {item['count']} | {cell(cite(item['first']))} |")
    warnings = [(run["directory"], warning) for run in result["runs"] for warning in run["warnings"]]
    lines.extend(["", "## 证据界限与重跑", "", *[f"- {x}" for x in result["limitations"]], "",
        "```powershell", "python D:\\Workspace4All\\ThothTAS\\scripts\\Summarize-Coverage.py D:\\Workspace4All\\ThothTAS", "```", "",
        "缓存只复用 size/mtime 与规则版本完全一致的读取结果；`--no-cache` 强制重新读取。新回放结束后重跑即可更新报告。"])
    if warnings:
        lines.extend(["", "读取警告：", "", *[f"- `{cell(path)}`：{cell(w)}" for path, w in warnings[:100]]])
        if len(warnings) > 100:
            lines.append(f"- 其余 {len(warnings)-100} 条见 JSON。")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", nargs="?", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--input", type=Path, help="default: project/work/acceptance")
    parser.add_argument("--output", type=Path, help="default: project/docs/verification")
    parser.add_argument("--no-cache", action="store_true")
    parser.add_argument("--sample-limit", type=int, default=20)
    args = parser.parse_args()
    if args.sample_limit < 1:
        parser.error("--sample-limit must be positive")
    root = args.project.resolve()
    input_root = (args.input or root / "work/acceptance").resolve()
    output = (args.output or root / "docs/verification").resolve()
    catalog_path = root / "docs/random-calls.json"
    static = read_json(catalog_path)
    catalog_stat = catalog_path.stat()
    catalog_snapshot = {"bytes": catalog_stat.st_size, "mtimeNs": catalog_stat.st_mtime_ns}
    catalog = {canonical_site(x["siteId"]): x for x in static["randomCalls"]}
    reports, refs = summarize_reports(sorted(input_root.rglob("*report*.json")), root, input_root)
    directories = {str(p.parent.resolve()) for p in input_root.rglob("frames.jsonl")} | set(refs)
    cache = {}
    prior = output / "coverage.json"
    if prior.exists() and not args.no_cache:
        try:
            raw = read_json(prior)
            if (raw.get("ruleVersion") == RULE_VERSION and raw.get("sampleLimit") == args.sample_limit and
                raw.get("catalogSnapshot") == catalog_snapshot):
                cache = {str((root / x["directory"]).resolve()): x for x in raw["runs"]}
        except (OSError, ValueError, KeyError):
            pass
    runs = []
    for dirname in sorted(directories):
        directory = Path(dirname)
        cached = cache.get(dirname)
        if cached and not cached.get("sourceChangedWhileReading") and cached["sourceSnapshot"] == fingerprint(directory):
            run = cached
        else:
            print(f"Reading {relative(directory, root)}", flush=True)
            run = summarize_run(directory, root, catalog, args.sample_limit)
        run["reportReferences"] = refs.get(dirname, [])
        run["inPassedCompletedReport"] = run["completeLog"] and not run["warnings"] and any(
            x["reportStatus"] == "passed" for x in run["reportReferences"])
        runs.append(run)

    observed, phases = {}, {}
    paths = {k: {"label": v, "count": 0, "runs": [], "passedCompletedRuns": [], "first": None}
             for k, v in LABELS.items()}
    for run in runs:
        for label, item in run["events"].items():
            dest = paths[label]
            dest["count"] += item["count"]
            dest["runs"].append(run["directory"])
            if run["inPassedCompletedReport"]:
                dest["passedCompletedRuns"].append(run["directory"])
            if dest["first"] is None and item["samples"]:
                dest["first"] = item["samples"][0]
        for site, item in run["randomSites"].items():
            dest = observed.setdefault(site, {"count": 0, "initializationCalls": 0, "replayCalls": 0,
                "staticCatalogMatch": site in catalog, "first": item["first"], "runs": [], "passedCompletedRuns": []})
            for k in ("count", "initializationCalls", "replayCalls"):
                dest[k] += item[k]
            dest["runs"].append(run["directory"])
            if run["inPassedCompletedReport"]:
                dest["passedCompletedRuns"].append(run["directory"])
        for name, item in run["phases"].items():
            dest = phases.setdefault(name, {"count": 0, "first": item["first"], "runs": []})
            dest["count"] += item["count"]
            dest["runs"].append(run["directory"])
    result = {"schemaVersion": VERSION, "ruleVersion": RULE_VERSION, "sampleLimit": args.sample_limit,
        "generatedUtc": utc(), "input": relative(input_root, root), "evidenceKind": "actual-game-log-index",
        "catalogSnapshot": catalog_snapshot,
        "summary": {"runs": len(runs), "passedReports": sum(x["status"] == "passed" for x in reports),
            "failedReports": sum(x["status"] == "failed" for x in reports),
            "pendingReports": sum(x["status"] not in ("passed", "failed") for x in reports),
            "passedCompletedRuns": sum(x["inPassedCompletedReport"] for x in runs)},
        "reports": reports, "runs": runs, "pathCoverage": paths, "phaseCoverage": phases,
        "randomCoverage": {"staticCatalog": relative(catalog_path, root), "staticSites": len(catalog),
            "observedStaticSites": len(set(observed) & set(catalog)),
            "observedStaticSitesInPassedRuns": sum(site in catalog and bool(item["passedCompletedRuns"]) for site, item in observed.items()),
            "observed": observed, "unknownRuntimeSites": sorted(set(observed) - set(catalog)),
            "unobservedStaticSites": sorted(set(catalog) - set(observed))},
        "limitations": [
            "仅覆盖输入目录中生成时已有的日志。进行中、未完整、没有通过报告引用的运行可提供观察证据，不能计作确定性验收通过。",
            "通过状态引用原始验证器报告；本脚本不执行游戏，不重新进行完整帧/随机字节比较。",
            "每个标签和实际随机点都有文件、行号、帧号、字段或阶段证据；样本有数量上限，JSON 计数保留全部匹配次数。",
            "场景转场要求不同 scene，并且旧场景先实际记录 roomCompleted 从 False 到 True；不将同场景死亡重载计作自然转场。",
            "传送几何候选不是直接回调证据。原帧快照不记录静态标志；新增真实回调观测可记录 player1Flag 和 before/after，跳跃标签要求标志真且 XY 变化。返回后 active=False 符合原跳跃分支，不解释为跳跃前未激活。",
            "Boss 标签仅证明实际 Boss 实例字段变化，不等同击败 Boss；BossMusic/BossTrack 不匹配此标签。",
            "碰撞标签只证明 OnCollision/OnTrigger 回调被调用，不证明玩家死亡或伤害，需分别查看字段证据。",
            "frame=-1 的随机/阶段属于基线前初始化；执行某个随机点不证明所有参数分支、种子或关卡均覆盖。",
            "random.tsv 的 phase 是最近插桩入口标签，不是完整调用栈；随机所属方法由静态 siteId 确定，实际阶段入口另以 phases.tsv 为证。",
            "不同旧报告和当前报告均保留，失败不会因后来报告通过而自动隐藏。",
        ]}
    output.mkdir(parents=True, exist_ok=True)
    for name, content in (("coverage.json", json.dumps(result, ensure_ascii=False, indent=2) + "\n"),
                          ("coverage.md", write_markdown(result))):
        target = output / name
        temporary = output / (name + ".tmp")
        temporary.write_text(content, encoding="utf-8")
        temporary.replace(target)
    print(json.dumps(result["summary"], ensure_ascii=False))
    print(f"random sites {result['randomCoverage']['observedStaticSites']}/{len(catalog)}; outputs: {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
