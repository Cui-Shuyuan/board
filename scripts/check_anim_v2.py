#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Static contract checker for the v2 compiled pipeline.

It answers the question that a pure `data == engine` reconciliation cannot:
does the hand-written `enter/exit` contract match the compiled start/end
snapshot produced by the compiler?

Usage:
    python3 scripts/check_anim_v2.py --game splendor --track _schema_example
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import anim_schema_v2 as schema  # noqa: E402


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def norm_contract_zones(part: dict) -> dict:
    zones = (part or {}).get("zones") or {}
    if isinstance(zones, dict):
        return {k: (v or {}) for k, v in zones.items()}
    out = {}
    for entry in zones:
        if isinstance(entry, dict):
            out[entry.get("zone")] = entry
    return out


def match_item(item: dict, want: dict) -> bool:
    if want.get("template") and item.get("TemplateId") != want.get("template"):
        return False
    if want.get("palette") and item.get("Palette") != want.get("palette"):
        return False
    return True


def face_int(v):
    return {"up": 2, "face_up": 2, "down": 1, "face_down": 1, "hidden": 0}.get(v)


def check_contract_piece(rep, cue_id, label, part, state):
    pieces = norm_contract_zones(part)
    components = (state or {}).get("components") or []
    for zone, spec in pieces.items():
        if spec is None:
            continue
        items = spec.get("items")
        if items is None:
            # count-only form
            if "count" in spec:
                n = sum(1 for c in components if c.get("ZoneId") == zone)
                if n != spec["count"]:
                    rep.append(f"{cue_id} {label}.{zone}.count: 期望 {spec['count']}，编译快照 {n}")
            continue
        for i, want in enumerate(items):
            where = f"{cue_id} {label}.{zone}.items[{i}]"
            matched = [c for c in components if c.get("ZoneId") == zone and match_item(c, want)]
            n = len(matched)
            wc = int(want.get("count", 1))
            if n != wc:
                rep.append(f"{where}: 期望 {wc} 件，编译快照 {n}")
                continue
            if "face" in want and want.get("face") not in (None, ""):
                fi = face_int(want["face"])
                bad = [c for c in matched if int(c.get("Face", 2)) != fi]
                if bad:
                    rep.append(f"{where}.face: 期望 {want['face']}，有 {len(bad)} 件朝向不符")


def picture_at(cue: dict, t: float):
    latest = None
    latest_at = None
    for clip in cue.get("clips") or []:
        if clip.get("kind") not in ("picture", "show"):
            continue
        if clip.get("at", 0.0) <= t + 1e-6:
            if latest_at is None or clip.get("at", 0.0) >= latest_at:
                latest_at = clip.get("at", 0.0)
                latest = clip.get("picture") if clip.get("picture_on") else None
    return latest


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="_schema_example")
    ap.add_argument("--source")
    a = ap.parse_args()
    src = Path(a.source) if a.source else ROOT / "games" / a.game / "tutorial" / "anim" / "v2" / f"{a.track}.anim.json"
    out = src.with_name(src.name.replace(".anim.json", ".compiled.json"))
    if not src.exists() or not out.exists():
        print(f"missing {src} or {out}", file=sys.stderr)
        return 2

    track = load(src)
    compiled = load(out)
    schema_rep = schema.validate_track(track)
    errors = []
    warnings = list(schema_rep.warnings)
    for e in schema_rep.errors:
        errors.append("schema: " + e)

    by_id = {c["id"]: c for c in compiled.get("cues") or []}
    prev_end = None
    prev_id = None
    for cue in track.get("cues") or []:
        cid = cue.get("id")
        cc = by_id.get(cid)
        if cc is None:
            errors.append(f"{cid}: missing compiled cue")
            continue
        script = cue.get("script") or {}
        check_contract_piece(errors, cid, "enter", script.get("enter") or {}, cc.get("start_state"))
        check_contract_piece(errors, cid, "exit", script.get("exit") or {}, cc.get("end_state"))

        want_enter_pic = (script.get("enter") or {}).get("picture")
        got_enter_pic = picture_at(cc, 0.0)
        if want_enter_pic != got_enter_pic:
            errors.append(f"{cid} picture(enter): 期望 {want_enter_pic!r}，编译 {got_enter_pic!r}")
        want_exit_pic = (script.get("exit") or {}).get("picture")
        got_exit_pic = picture_at(cc, float(cc.get("duration", 0.0)))
        if want_exit_pic != got_exit_pic:
            errors.append(f"{cid} picture(exit): 期望 {want_exit_pic!r}，编译 {got_exit_pic!r}")

        if cue.get("transition") in ("continue", "overlay") and prev_end is not None:
            if cc.get("start_state") != prev_end:
                errors.append(f"{cid}: {cue.get('transition')} 但 start_state != 上一条 end_state")
        if cue.get("transition") in ("cut", "world_cut"):
            if cc.get("camera", {}).get("at", 0.0) != 0.0:
                errors.append(f"{cid}: cut 的 camera.at 必须为 0")
        prev_end = cc.get("end_state")
        prev_id = cid

    for e in errors:
        print("ERR  " + e)
    for w in warnings:
        print("WARN " + w)
    if errors:
        print(f"FAIL {src}: {len(errors)} errors, {len(warnings)} warnings", file=sys.stderr)
        return 1
    print(f"OK   {src}: contracts match compiled snapshots ({len(by_id)} cues, {len(warnings)} warnings)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
