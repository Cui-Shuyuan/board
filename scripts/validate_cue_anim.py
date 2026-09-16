#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Validate the zone-based cue animation data.

Data layout
-----------
    games/{game}/tutorial/{track}.runtime.json             cue order / audio / duration
    games/{game}/tutorial/anim/_stage/{game}.table.json    table facts: zones, templates, initial
    games/{game}/tutorial/anim/{track}/{cue_id}.json       this cue's delta on that table

Model
-----
Animation = maintaining component state.  A component's state is (zone, order);
world position is derived from the zone layout.  So a cue contains no coordinates:
it only says things like "move gem#1 from its pile to player_holding".

Checks
------
* cue maps to a runtime cue and header fields agree
* stage file exists and is well formed (zones / templates / anchors / initial)
* every zone / template referenced by the cue exists
* events use the 8 primitives with the fields that primitive needs
* zone targets exist, `from` zones exist, `target` actor ids resolve
* timeline stays inside the cue audio duration
* warnings: unknown palette, animation ending too close to the audio end

Usage
-----
    python scripts/validate_cue_anim.py --game splendor --track full
    python scripts/validate_cue_anim.py --game splendor --track full --cue action.take.different.001
    python scripts/validate_cue_anim.py --file games/splendor/tutorial/anim/full/x.json
    python scripts/validate_cue_anim.py --game splendor --track full --json

Exit codes: 0 = ok (warnings allowed), 1 = errors, 2 = file not found.
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

ACTIONS = {"move", "flip", "rotate", "scale", "fade", "highlight", "shuffle",
           "showbox", "create", "destroy", "wait"}
SHAPES = {"panel", "gem", "shadow", "dot", "card"}

EASINGS = {
    "linear", "easeInQuad", "easeOutQuad", "easeInOutQuad",
    "easeInCubic", "easeOutCubic", "easeInOutCubic",
    "easeInBack", "easeOutBack", "easeInOutBack",
}

# Kept in sync with client/Assets/Scripts/Tutorial/Palette.cs
PALETTES = {
    "gem_diamond", "gem_sapphire", "gem_emerald", "gem_ruby", "gem_onyx",
    "gem_gold", "panel_supply", "panel_player", "panel_market", "panel_card",
    "panel_neutral", "accent_green", "accent_yellow", "accent_blue",
    "card_level_1", "card_level_2", "card_level_3", "noble", "shadow", "white",
}

MIN_TAIL_MARGIN = 0.15


class Report:
    def __init__(self, path: Path):
        self.path = path
        self.errors = []
        self.warnings = []

    def error(self, where, message):
        self.errors.append({"where": where, "message": message})

    def warn(self, where, message):
        self.warnings.append({"where": where, "message": message})


MARKET_COLS = 4          # 市场每行几格（与 stage 的 layout.cols 一致）


def color_of_target(target: str):
    """从组件 id 里取出颜色名：market_card_1_emerald#1 → emerald。取不到返回 None。"""
    if not target:
        return None
    base = target.split("#")[0]
    for c in ("emerald", "ruby", "diamond", "sapphire", "onyx", "gold"):
        if base.endswith("_" + c):
            return c
    return None


def load_json(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def derive_actor_ids(stage):
    """
    Reproduce ZoneStore.Spawn's id scheme: '{template}#{n}' with a per-template counter,
    numbered in stage.initial order regardless of zone.  This is the authoritative id set
    the runtime assigns, so cue targets are checked against it.
    """
    counters = {}
    ids = []
    for entry in stage.get("initial", []):
        tpl = entry.get("template", "")
        for _ in range(max(1, int(entry.get("count", 1)))):
            counters[tpl] = counters.get(tpl, 0) + 1
            ids.append(f"{tpl}#{counters[tpl]}")
    return ids


def validate_stage(stage_path: Path, report: Report, game_id: str):
    if not stage_path.exists():
        report.error("stage", f"牌桌文件不存在: {stage_path}")
        return None, set(), set()

    stage = load_json(stage_path)
    zones = {z["id"] for z in stage.get("zones", []) if z.get("id")}
    templates = {t["id"] for t in stage.get("templates", []) if t.get("id")}

    if stage.get("game_id") and stage["game_id"] != game_id:
        report.error("stage", f"game_id = {stage['game_id']!r}，应为 {game_id!r}")

    for i, zone in enumerate(stage.get("zones", [])):
        where = f"stage.zones[{i}]"
        if not zone.get("id"):
            report.error(where, "缺少 id")
            continue
        if "center" not in zone:
            report.error(where, f"zone {zone['id']} 缺少 center")
        layout = zone.get("layout") or {}
        for key in ("x_step", "z_step"):
            if float(layout.get(key, 0)) < 0:
                report.error(where, f"zone {zone['id']} 的 {key} 不能为负")
        if int(layout.get("cols", 1)) < 1:
            report.error(where, f"zone {zone['id']} 的 cols 至少为 1")

    for i, tpl in enumerate(stage.get("templates", [])):
        where = f"stage.templates[{i}]"
        if not tpl.get("id"):
            report.error(where, "缺少 id")
            continue
        if tpl.get("shape", "gem") not in SHAPES:
            report.error(where, f"未知 shape {tpl['shape']!r}")
        if tpl.get("palette") and tpl["palette"] not in PALETTES:
            report.warn(where, f"未知 palette {tpl['palette']!r}（见 Palette.cs）")

    for i, anchor in enumerate(stage.get("anchors", [])):
        where = f"stage.anchors[{i}]"
        if not anchor.get("id"):
            report.error(where, "缺少 id")
        if anchor.get("template") not in templates:
            report.error(where, f"anchor {anchor.get('id')!r} 引用了未知 template {anchor.get('template')!r}")

    for i, entry in enumerate(stage.get("initial", [])):
        where = f"stage.initial[{i}]"
        if entry.get("template") not in templates:
            report.error(where, f"未知 template {entry.get('template')!r}")
        if entry.get("zone") not in zones:
            report.error(where, f"未知 zone {entry.get('zone')!r}")
        if entry.get("palette") and entry["palette"] not in PALETTES:
            report.warn(where, f"未知 palette {entry['palette']!r}")
        if int(entry.get("count", 1)) < 1:
            report.error(where, "count 至少为 1")

    return stage, zones, templates


def collect_created_ids(anim_dir: Path):
    """扫一遍：哪些组件 id 会在某条 cue 里被 create 出来。

    跨 cue 的 create/destroy 是正常写法（cue 10 销毁 cue 9 创建的展示卡），
    但校验单条 cue 时看不到前一条创建了什么，会误报 target 不存在。
    """
    created = {}   # template -> 出现过的最大序号
    for p in sorted(anim_dir.glob("*.json")):
        try:
            doc = load_json(p)
        except Exception:
            continue
        for ev in doc.get("events") or []:
            if isinstance(ev, dict) and ev.get("action") == "create" and ev.get("template"):
                n = int(ev.get("count") or 1)
                created[ev["template"]] = max(created.get(ev["template"], 0), n)
    ids = set()
    for tpl, n in created.items():
        for i in range(1, n + 1):
            ids.add(f"{tpl}#{i}")
    return ids


def validate_cue(path: Path, runtime_cues, track, game_id, report: Report):
    try:
        doc = load_json(path)
    except json.JSONDecodeError as exc:
        report.error(path.stem, f"JSON 解析失败: {exc}")
        return

    cue_id = path.stem
    where = cue_id

    if doc.get("schema_version") != 1:
        report.warn(where, f"schema_version = {doc.get('schema_version')!r}，当前校验器针对 1")
    if doc.get("game_id") and doc["game_id"] != game_id:
        report.error(where, f"game_id = {doc['game_id']!r}，应为 {game_id!r}")
    if doc.get("track") and doc["track"] != track:
        report.error(where, f"track = {doc['track']!r}，应为 {track!r}")
    if doc.get("cue") and doc["cue"] != cue_id:
        report.error(where, f"cue = {doc['cue']!r}，与文件名 {cue_id!r} 不一致")

    dealt_slots = {}

    if cue_id not in runtime_cues:
        report.error(where, f"runtime 中不存在该 cue（{track}.runtime.json）")
        return
    duration = float(runtime_cues[cue_id].get("duration") or 0.0)

    stage_rel = doc.get("stage") or f"_stage/{game_id}.table"
    stage_path = ROOT / "games" / game_id / "tutorial" / "anim" / (stage_rel + ".json")
    stage, zones, templates = validate_stage(stage_path, report, game_id)
    if stage is None:
        return

    known_ids = set(derive_actor_ids(stage)) | collect_created_ids(path.parent)
    # cue 自己 start.set 出来的组件（如发牌前预置在盒里的正面卡）也是合法目标
    for seed in (doc.get("start") or {}).get("set") or []:
        tpl = seed.get("template")
        if not tpl:
            continue
        n = int(seed.get("expand_to") or seed.get("count") or 1)
        for i in range(1, max(1, n) + 1):
            known_ids.add(f"{tpl}#{i}")

    start = doc.get("start") or {}
    for i, seed in enumerate(start.get("set") or []):
        sw = f"{where} start.set[{i}]"
        if seed.get("template") not in templates:
            report.error(sw, f"未知 template {seed.get('template')!r}")
        if seed.get("zone") not in zones:
            report.error(sw, f"未知 zone {seed.get('zone')!r}")
        if seed.get("palette") and seed["palette"] not in PALETTES:
            report.warn(sw, f"未知 palette {seed['palette']!r}")
        if int(seed.get("count", 1)) < 1:
            report.error(sw, "count 至少为 1")

    # 防重复：stage.initial 已经放过的 (template,palette,zone)，cue.start 再无条件 set 一次
    # 就会凭空多出一份组件（一份在 zone 里、一份留在原处），表现为「画面外有东西飘」。
    initialized = {}
    for entry in stage.get("initial", []):
        key = (entry.get("template"), entry.get("palette"), entry.get("zone"))
        initialized[key] = initialized.get(key, 0) + int(entry.get("count", 1))
    for i, seed in enumerate(start.get("set") or []):
        key = (seed.get("template"), seed.get("palette"), seed.get("zone"))
        if key in initialized and not seed.get("expand_to"):
            report.warn(f"{where} start.set[{i}]: stage.initial 已在 {seed.get('zone')} 放了 "
                        f"{initialized[key]} 个 {seed.get('template')}，这里又无条件 set {seed.get('count', 1)} 个，"
                        f"会重复生成；若只是想让它们就位，删掉这条即可，或改用 expand_to")

    events = doc.get("events")
    if not isinstance(events, list) or not events:
        report.error(where, "events 为空")
        return

    prev_at = -1.0
    last_end = 0.0
    for i, ev in enumerate(events):
        ew = f"{where} events[{i}]"
        action = ev.get("action", "move")
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
            report.warn(ew, f"事件未按 at 升序（上一条 at={prev_at:g}）")
        prev_at = max(prev_at, at)

        easing = ev.get("easing")
        if easing and easing not in EASINGS:
            report.error(ew, f"未知 easing {easing!r}")

        target = ev.get("target")
        zone = ev.get("zone")

        # create 出来的组件 id 也算已知（模板名#序号），否则同一 cue 后续 target 会被误报
        if action == "create" and ev.get("template"):
            known_ids.add(f"{ev['template']}#1")
        if zone and zone not in zones:
            report.error(ew, f"未知 zone {zone!r}")
        if target and target not in known_ids:
            report.error(ew, f"target {target!r} 不是牌桌上已知的组件 id")

        if action == "move":
            if not target and not ev.get("from"):
                report.error(ew, "move 需要 target（指定某件）或 from（指定源 zone）")
            # from 必须是**数组**。写成字符串时 JsonUtility 会静默丢弃整个字段
            # （类型不匹配不报错），表现为 move 永远拿不到源 zone、牌堆搭不起来。
            raw_from = ev.get("from")
            if isinstance(raw_from, str):
                report.error(ew, f'move.from 必须写成数组：["{raw_from}"]（字符串会被静默丢弃）')
            sources = raw_from if isinstance(raw_from, list) else ([raw_from] if raw_from else [])
            if isinstance(raw_from, list) and not raw_from:
                report.error(ew, "move.from 是空数组")
            for source in sources:
                if source not in zones:
                    report.error(ew, f"from zone {source!r} 不存在")
            if not zone and not target:
                report.error(ew, "move 缺少目的地 zone")
            if int(ev.get("take", 0)) < 0:
                report.error(ew, "take 不能为负")
            # from+take 是按顺序取件：同一个 zone 里混放多种组件时极易取错，
            # 例如盒子里同时有宝石和卡片。用 take 时建议显式给 template。
            if ev.get("from") and int(ev.get("take", 0) or 0) > 0 and not ev.get("template"):
                report.warn(ew, "move 用 from+take 按顺序取件但没写 template；"
                                "若该 zone 混放多种组件，可能取到不该动的东西")
            if ev.get("order") == -2 and int(ev.get("slot", -1)) < 0:
                report.error(ew, "order=-2 需要同时给 slot（目标格位）")
        elif action == "rotate":
            if "angle" not in ev:
                report.error(ew, "rotate 需要 angle")
        elif action == "scale":
            if "scale" not in ev:
                report.error(ew, "scale 需要 scale（倍率）")
            if ev.get("scale_mode") not in (None, "to", "by"):
                report.error(ew, f"scale_mode 只能是 by/to，得到 {ev.get('scale_mode')!r}")
        elif action == "highlight":
            if not zone and not target:
                report.warn(ew, "highlight 既没有 zone 也没有 target，会对全体生效")
            peak = ev.get("peak_alpha")
            if peak is not None and not 0.0 <= float(peak) <= 1.0:
                report.error(ew, f"peak_alpha 超出 [0,1]: {peak}")
        elif action == "create":
            if not ev.get("template"):
                report.error(ew, "create 需要 template（要创建什么）")
            elif ev["template"] not in templates:
                report.error(ew, f"create 的 template {ev['template']!r} 不在 stage.templates 里")
            if not ev.get("zone"):
                report.error(ew, "create 需要 zone（创建到哪里）")
            elif ev["zone"] not in zones:
                report.error(ew, f"create 的 zone {ev['zone']!r} 不存在")
        elif action == "destroy":
            if not ev.get("target") and not ev.get("zone") and not ev.get("template"):
                report.error(ew, "destroy 需要 target 或 zone/template（否则要销毁什么不明确）")
        elif action == "showbox":
            on = ev.get("on", 1)
            # 隐藏时不需要 picture（沿用当前显示的那张）
            if on not in (0, 1, 0.0, 1.0):
                report.warn(ew, f"showbox.on 只能写 0 或 1，当前 {on!r}")
            if on and not ev.get("picture"):
                report.error(ew, "showbox 显示时需要 picture（相对 games/{game} 的图片路径）")
        elif action == "shuffle":
            if not zone and not target:
                report.warn(ew, "shuffle 既没有 zone 也没有 target，会对全体生效")

        end = at + lead + dur
        if action != "wait":
            last_end = max(last_end, end)

        # 记录发牌格位：用于检查「每行颜色组合是否雷同」
        slot = ev.get("slot")
        if action == "move" and zone == "card_market" and isinstance(slot, int) and slot >= 0:
            dealt_slots[slot] = target or ""

    # 市场是多行网格（4 列）：两行颜色顺序完全相同会误导观众，
    # 让人以为「必须这样摆」。真实市场是发牌结果，不会整齐成列。
    if dealt_slots:
        rows = {}
        for slot, target in dealt_slots.items():
            rows.setdefault(slot // MARKET_COLS, [None] * MARKET_COLS)[slot % MARKET_COLS] = target
        sigs = {}
        for row in sorted(rows):
            colors = tuple(color_of_target(t) for t in rows[row])
            sigs.setdefault(colors, []).append(row)
        for colors, rowlist in sigs.items():
            if len(rowlist) > 1 and any(c for c in colors):
                report.warn(where, f"市场第 {'、'.join(str(r+1) for r in rowlist)} 行的颜色顺序完全相同"
                                   f"（{' '.join(str(c) for c in colors)}）—— "
                                   f"真实市场不会这么整齐，容易让人误以为必须这样摆")

        if duration > 0 and end > duration + 1e-6:
            report.error(ew, f"事件结束于 {end:.2f}s，超出 cue 音频时长 {duration:.2f}s")

    if duration > 0 and last_end > 0 and duration - last_end < MIN_TAIL_MARGIN:
        report.warn(where, f"动画结束 {last_end:.2f}s 距音频结束 {duration:.2f}s 不足 {MIN_TAIL_MARGIN:.2f}s")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--cue", help="只校验这一条 cue")
    parser.add_argument("--file", help="直接校验指定文件")
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
        validate_cue(path, runtime_cues, args.track, args.game, report)
        reports.append(report)

    total_errors = sum(len(r.errors) for r in reports)
    total_warnings = sum(len(r.warnings) for r in reports)

    if args.json:
        errors, warnings = [], []
        for r in reports:
            for e in r.errors:
                errors.append({"file": str(r.path), **e})
            for w in r.warnings:
                warnings.append({"file": str(r.path), **w})
        print(json.dumps({"ok": total_errors == 0, "files": len(files),
                          "errors": errors, "warnings": warnings}, ensure_ascii=False, indent=2))
    else:
        for r in reports:
            print(f"{'OK ' if not r.errors else 'ERR'} {r.path.name}"
                  + (f"  ({len(r.warnings)} warning)" if r.warnings else ""))
            for e in r.errors:
                print(f"    error  {e['where']}: {e['message']}")
            for w in r.warnings:
                print(f"    warn   {w['where']}: {w['message']}")
        print(f"\n{len(files)} 个文件，{total_errors} 个错误，{total_warnings} 个警告")

    return 1 if total_errors else 0


if __name__ == "__main__":
    sys.exit(main())
