#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Validate per-cue animation data against the runtime cue table.

Data layout
-----------
    games/{game}/tutorial/{track}.runtime.json          cue order / audio / duration
    games/{game}/tutorial/anim/{track}/{cue_id}.json    scene + primitive timeline

The validator is deterministic and runs before Unity: it makes the LLM-written
animation data fail here with machine-readable errors instead of showing small
visual bugs on a Windows screenshot round-trip.

Checks
------
* every anim file maps to a cue that exists in the runtime table (and vice versa
  is only a warning: not every cue needs an animation)
* header fields agree with the runtime (game_id / track / cue)
* actor ids unique; palette, shape, sorting order, highlight actor validity
* event timeline inside the cue audio duration
* each event uses one of the 8 primitives with the fields that primitive needs
* target resolves to an actor id, an actor group, or "all"
* slot references in move.to_slot exist
* easing names exist in Easing.cs
* overlapping transforms on the same actor are reported as warnings

Usage
-----
    python scripts/validate_cue_anim.py --game splendor --track full
    python scripts/validate_cue_anim.py --game splendor --track full --cue action.take.different.001
    python scripts/validate_cue_anim.py --file games/splendor/tutorial/anim/full/x.json
    python scripts/validate_cue_anim.py --game splendor --track full --json

Exit codes: 0 = ok (warnings allowed), 1 = errors, 2 = file not found / not readable.
"""

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

ACTIONS = {"move", "flip", "rotate", "scale", "fade", "highlight", "shuffle", "wait"}
SHAPES = {"panel", "gem", "shadow", "dot"}

# Kept in sync with client/Assets/Scripts/Tutorial/Easing.cs
EASINGS = {
    "linear", "easeInQuad", "easeOutQuad", "easeInOutQuad",
    "easeInCubic", "easeOutCubic", "easeInOutCubic",
    "easeInBack", "easeOutBack", "easeInOutBack",
}

# Kept in sync with the Palette table in TutorialCueAnimPlayer.cs
PALETTES = {
    "gem_diamond", "gem_sapphire", "gem_emerald", "gem_ruby", "gem_onyx",
    "gem_gold", "panel_supply", "panel_player", "shadow", "white",
}

# Actions that animate a transform; used for the overlapping-animation warning.
TRANSFORM_ACTIONS = {"move", "flip", "rotate", "scale", "fade"}

# Minimum animation headroom before the audio ends (seconds).
MIN_TAIL_MARGIN = 0.15


class Report:
    def __init__(self, path: Path):
        self.path = path
        self.errors = []
        self.warnings = []

    def error(self, where: str, message: str):
        self.errors.append({"where": where, "message": message})

    def warn(self, where: str, message: str):
        self.warnings.append({"where": where, "message": message})


def load_json(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def validate_file(anim_path: Path, runtime_cues: dict, track: str, game_id: str, report: Report):
    try:
        doc = load_json(anim_path)
    except json.JSONDecodeError as exc:
        report.error(anim_path.name, f"JSON 解析失败: {exc}")
        return None

    cue_id = anim_path.stem
    where = cue_id

    # ---- header ----
    if doc.get("schema_version") != 1:
        report.warn(where, f"schema_version = {doc.get('schema_version')!r}，当前校验器针对 1")
    if doc.get("game_id") and doc.get("game_id") != game_id:
        report.error(where, f"game_id = {doc.get('game_id')!r}，应为 {game_id!r}")
    if doc.get("track") and doc.get("track") != track:
        report.error(where, f"track = {doc.get('track')!r}，应为 {track!r}")
    if doc.get("cue") and doc.get("cue") != cue_id:
        report.error(where, f"cue = {doc.get('cue')!r}，与文件名 {cue_id!r} 不一致")

    if cue_id not in runtime_cues:
        report.error(where, f"runtime 中不存在该 cue（{track}.runtime.json）")
        return doc

    cue = runtime_cues[cue_id]
    duration = float(cue.get("duration") or 0.0)

    # ---- scene ----
    scene = doc.get("scene")
    if not isinstance(scene, dict):
        report.error(where, "缺少 scene")
        return doc

    actors = scene.get("actors")
    if not isinstance(actors, list) or not actors:
        report.error(where, "scene.actors 为空")
        return doc

    ids = set()
    groups = set()
    highlights = set()
    for i, actor in enumerate(actors):
        aw = f"{where} scene.actors[{i}]"
        aid = actor.get("id")
        if not aid:
            report.error(aw, "缺少 id")
            continue
        if aid in ids:
            report.error(aw, f"actor id 重复: {aid}")
        ids.add(aid)
        if actor.get("group"):
            groups.add(actor["group"])

        palette = actor.get("palette")
        if palette and palette not in PALETTES:
            report.error(aw, f"未知 palette {palette!r}（见 TutorialCueAnimPlayer.Palette）")

        shape = actor.get("shape", "gem")
        if shape not in SHAPES:
            report.error(aw, f"未知 shape {shape!r}，可选 {sorted(SHAPES)}")

        if actor.get("highlight"):
            highlights.add(aid)
            if shape != "panel" and shape != "dot":
                report.warn(aw, "highlight actor 建议用 panel/dot shape")

        if actor.get("world_size", 0) is not None and float(actor.get("world_size", 0)) < 0:
            report.error(aw, "world_size 不能为负")

        alpha = float(actor.get("alpha", 1.0))
        if not 0.0 <= alpha <= 1.0:
            report.error(aw, f"alpha 超出 [0,1]: {alpha}")

        for key in ("sprite",):
            if actor.get(key) and not actor.get("shape"):
                report.warn(aw, f"{key} 已指定但没有 shape，占位图形回退为 gem")

    # ---- events ----
    events = doc.get("events")
    if not isinstance(events, list) or not events:
        report.error(where, "events 为空")
        return doc

    prev_at = -1.0
    transform_windows = {}  # target -> list of (start, end, action)
    last_end = 0.0

    for i, ev in enumerate(events):
        ew = f"{where} events[{i}]"
        action = ev.get("action")
        if action not in ACTIONS:
            report.error(ew, f"未知 action {action!r}，只能是 {sorted(ACTIONS)}")
            continue

        at = float(ev.get("at", 0.0))
        dur = float(ev.get("dur", 0.0))
        lead = float(ev.get("lead", 0.0))

        if at < 0:
            report.error(ew, f"at 不能为负: {at}")
        if dur < 0:
            report.error(ew, f"dur 不能为负: {dur}")
        if at + 1e-6 < prev_at:
            report.warn(ew, f"事件未按 at 升序排列（上一事件 at={prev_at:g}，本事件 at={at:g}）")
        prev_at = max(prev_at, at)

        easing = ev.get("easing")
        if easing and easing not in EASINGS:
            report.error(ew, f"未知 easing {easing!r}，只能是 {sorted(EASINGS)}")

        target = ev.get("target")
        if target and target not in ids and target not in groups:
            report.error(ew, f"target {target!r} 既不是 actor id 也不是 group")

        # primitive-specific requirements
        if action == "move":
            mv = ev.get("move")
            if not isinstance(mv, dict) or not any(k in mv for k in ("dx", "dy", "dz", "to_slot", "to_x", "to_z")):
                report.error(ew, "move 需要 move.dx/dy/dz 或 move.to_slot / to_x / to_z 之一")
            elif mv.get("to_slot") and mv["to_slot"] not in ids:
                report.error(ew, f"move.to_slot {mv['to_slot']!r} 不是已定义的 actor")
        elif action in ("rotate", "flip"):
            if action == "rotate" and "angle" not in ev:
                report.error(ew, "rotate 需要 angle")
        elif action == "scale":
            if "scale" not in ev and ev.get("scale_mode") != "to":
                report.error(ew, "scale 需要 scale（倍率）")
            if ev.get("scale_mode") not in (None, "to", "by"):
                report.error(ew, f"scale_mode 只能是 by/to，得到 {ev.get('scale_mode')!r}")
        elif action == "fade":
            pass
        elif action == "highlight":
            if target is None:
                report.warn(ew, "highlight 未指定 target，会对全体 actor 生效")
            elif target not in highlights and target not in groups:
                report.warn(ew, f"highlight target {target!r} 不是 highlight actor（画面里没有对应的高亮层）")
            peak = ev.get("peak_alpha")
            if peak is not None and not 0.0 <= float(peak) <= 1.0:
                report.error(ew, f"peak_alpha 超出 [0,1]: {peak}")
        elif action == "shuffle":
            if target is None:
                report.warn(ew, "shuffle 未指定 target，会对全体 actor 生效")

        # timeline bounds
        end = at + lead + dur
        if action != "wait":
            last_end = max(last_end, end)
        if duration > 0 and end > duration + 1e-6:
            report.error(ew, f"事件结束于 {end:.2f}s，超出 cue 音频时长 {duration:.2f}s")

        if action in TRANSFORM_ACTIONS and target:
            transform_windows.setdefault(target, []).append((at + lead, end, action))

    # ---- warnings on the whole timeline ----
    if duration > 0 and last_end > 0:
        if duration - last_end < MIN_TAIL_MARGIN:
            report.warn(where,
                        f"动画结束 {last_end:.2f}s 距音频结束 {duration:.2f}s 不足 {MIN_TAIL_MARGIN:.2f}s")

    for target, windows in transform_windows.items():
        windows.sort()
        for (s1, e1, a1), (s2, e2, a2) in zip(windows, windows[1:]):
            if s2 < e1 - 1e-6 and a1 != "fade" and a2 != "fade":
                report.warn(where,
                            f"actor {target!r} 的 {a1}({s1:.2f}~{e1:.2f}s) 与 {a2}({s2:.2f}~{e2:.2f}s) 时间重叠")

    return doc


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--cue", help="只校验这一条 cue")
    parser.add_argument("--file", help="直接校验指定文件（此时 --game/--track 只用于定位 runtime）")
    parser.add_argument("--json", action="store_true", help="机器可读输出")
    args = parser.parse_args()

    game_root = ROOT / "games" / args.game
    runtime_path = game_root / "tutorial" / f"{args.track}.runtime.json"
    if not runtime_path.exists():
        print(f"runtime 不存在: {runtime_path}", file=sys.stderr)
        return 2

    runtime = load_json(runtime_path)
    runtime_cues = {c["id"]: c for c in runtime.get("cues", [])}

    anim_dir = game_root / "tutorial" / "anim" / args.track
    if args.file:
        files = [Path(args.file)]
    else:
        if not anim_dir.is_dir():
            print(f"动画目录不存在: {anim_dir}", file=sys.stderr)
            return 2
        files = sorted(anim_dir.glob("*.json"))
        if args.cue:
            files = [f for f in files if f.stem == args.cue]
            if not files:
                print(f"找不到 cue 动画: {anim_dir / (args.cue + '.json')}", file=sys.stderr)
                return 2

    if not files:
        print(f"没有动画文件可校验（{anim_dir}）")
        return 0

    reports = []
    for path in files:
        report = Report(path)
        validate_file(path, runtime_cues, args.track, args.game, report)
        reports.append(report)

    total_errors = sum(len(r.errors) for r in reports)
    total_warnings = sum(len(r.warnings) for r in reports)

    if args.json:
        errors = []
        warnings = []
        for r in reports:
            for e in r.errors:
                errors.append({"file": str(r.path), **e})
            for w in r.warnings:
                warnings.append({"file": str(r.path), **w})
        print(json.dumps({
            "ok": total_errors == 0,
            "files": len(files),
            "errors": errors,
            "warnings": warnings,
        }, ensure_ascii=False, indent=2))
    else:
        for r in reports:
            status = "OK " if not r.errors else "ERR"
            note = f"  ({len(r.warnings)} warning)" if r.warnings else ""
            print(f"{status} {r.path.name}{note}")
            for e in r.errors:
                print(f"    error  {e['where']}: {e['message']}")
            for w in r.warnings:
                print(f"    warn   {w['where']}: {w['message']}")
        print(f"\n{len(files)} 个文件，{total_errors} 个错误，{total_warnings} 个警告")

    return 1 if total_errors else 0


if __name__ == "__main__":
    sys.exit(main())
