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


def check_shuffle_visuals(rep, cue: dict, compiled_cue: dict):
    """A shuffle event must leave a visible clip behind.

    v1 had a dedicated in-place jitter; during the v2 rewrite the compiler kept
    only the state permutation and the compiled cue carried no shuffle clip, so
    the animation silently disappeared.  This guard makes that failure loud.
    """
    want = [ev.get("zone") for ev in (cue.get("events") or [])
            if ev.get("op") == "shuffle" and ev.get("zone")]
    if not want:
        return
    got = {}
    for clip in compiled_cue.get("clips") or []:
        if clip.get("kind") == "shuffle":
            z = clip.get("to_zone") or clip.get("from_zone")
            got[z] = got.get(z, 0) + 1
    for zone in want:
        if got.get(zone, 0) == 0:
            rep.append(f"{cue.get('id')}: shuffle({zone}) 有事件，但编译产物没有任何 shuffle 片段（混洗动画丢失）")


def camera_eq(a, b):
    if not a or not b:
        return a is b
    for key in ("center_x", "center_z", "ortho_size", "pitch"):
        if abs(float(a.get(key, 0.0)) - float(b.get(key, 0.0))) > 1e-6:
            return False
    return True


def check_dirty_boundaries(rep, compiled: dict):
    """Flag the "old-shot item survives under the new camera" dirty frame.

    A camera cut applies at t=0, but state continuity (start_state) may carry an
    item from the previous cue.  If that item is still visible in `first_state`
    under the new camera and only disappears later in the cue, the runtime
    renders a frame that neither shot describes.  This is the exact class behind
    the cue9 -> cue10 one-frame shrink.

    The rule is deliberately narrow to avoid false positives: only a *carried*
    item (present in both previous end_state and current first_state) that is
    gone by end_state and appears while the camera changes is a hard error.
    Same-camera intentional removals are not flagged.
    """
    cues = compiled.get("cues") or []
    for i in range(1, len(cues)):
        prev, cur = cues[i - 1], cues[i]
        prev_end = {c.get("Id") for c in (prev.get("end_state") or {}).get("components") or []}
        cur_first_state = (cur.get("first_state") or {}).get("components") or []
        cur_first = {c.get("Id") for c in cur_first_state}
        cur_end = {c.get("Id") for c in (cur.get("end_state") or {}).get("components") or []}
        if not prev_end or not cur_first:
            continue
        if camera_eq(prev.get("camera"), cur.get("camera")):
            continue
        ghosts = (prev_end & cur_first) - cur_end
        for iid in sorted(ghosts):
            comp = next((c for c in cur_first_state if c.get("Id") == iid), {})
            removal = None
            for clip in cur.get("clips") or []:
                if clip.get("item_id") != iid:
                    continue
                if clip.get("kind") in ("destroy", "fade"):
                    removal = clip.get("at")
                    break
            when = f"t={removal:g}" if isinstance(removal, (int, float)) else "本 cue 后段"
            rep.append(
                f"{cur.get('id')}: 脏帧风险——机位切换后的第一帧仍带着上一镜的 "
                f"{comp.get('TemplateId') or iid}（{iid}），它到 {when} 才被移除。"
                f"请把这次移除移到 at=0，或让机位延后切换。"
            )


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
        check_shuffle_visuals(errors, cue, cc)

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

    check_dirty_boundaries(errors, compiled)

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
