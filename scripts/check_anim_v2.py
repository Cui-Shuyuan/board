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
import anim_geometry_v2 as geom  # noqa: E402


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


def camera_at(cue: dict, t: float):
    frame = cue.get("camera_in")
    for op in cue.get("camera_ops") or []:
        if op.get("at", 0.0) > t + 1e-6:
            break
        if op.get("frame"):
            frame = op.get("frame")
    return frame


def apply_state_ops(start_state: dict, ops: list, t: float = 1e9):
    items = {c.get("Id"): c for c in (start_state or {}).get("components") or []}
    for op in ops or []:
        if op.get("at", 0.0) > t + 1e-9:
            break
        if op.get("op") == "put" and (op.get("item") or {}).get("Id"):
            items[op["item"]["Id"]] = op["item"]
        elif op.get("op") == "remove" and op.get("item_id"):
            items.pop(op["item_id"], None)
    return items


def check_camera_ops(rep, compiled: dict):
    """Camera is now a timed op stream; validate its shape and order."""
    for cue in compiled.get("cues") or []:
        if not cue.get("camera_in"):
            rep.append(f"{cue.get('id')}: missing camera_in")
        ops = cue.get("camera_ops") or []
        for i in range(1, len(ops)):
            if ops[i].get("at", 0.0) + 1e-9 < ops[i - 1].get("at", 0.0):
                rep.append(f"{cue.get('id')}: camera_ops not sorted by at")
                break
        for op in ops:
            if not op.get("frame"):
                rep.append(f"{cue.get('id')}: camera op missing frame")
                break
            if float(op.get("at", 0.0)) < -1e-9:
                rep.append(f"{cue.get('id')}: camera op at must be >= 0")
                break


def check_state_ops(rep, compiled: dict):
    """Every cue must be explainable as start_state + concrete state_ops.

    The runtime now applies these ops before painting clips.  If the ops do not
    reproduce first_state/end_state, the runtime logical state and the compiler
    logical state can drift again.
    """
    for cue in compiled.get("cues") or []:
        ops = cue.get("state_ops") or []
        for i in range(1, len(ops)):
            if ops[i]["at"] + 1e-9 < ops[i - 1]["at"]:
                rep.append(f"{cue.get('id')}: state_ops not sorted by at")
                break
        got_first = apply_state_ops(cue.get("start_state") or {}, ops, 1e-9)
        want_first = {c.get("Id"): c for c in (cue.get("first_state") or {}).get("components") or []}
        if got_first != want_first:
            rep.append(f"{cue.get('id')}: state_ops <=0 应用后与 first_state 不一致")
        got_end = apply_state_ops(cue.get("start_state") or {}, ops, 1e9)
        want_end = {c.get("Id"): c for c in (cue.get("end_state") or {}).get("components") or []}
        if got_end != want_end:
            rep.append(f"{cue.get('id')}: state_ops 应用后与 end_state 不一致")


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
        if camera_eq(camera_at(prev, prev.get("duration", 0.0)), camera_at(cur, 0.0)):
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


# ── stage layout overlap check ──────────────────────────────────────────────
#
# zones are logical, so a wrong stage center/step is invisible in the event
# stream and only explodes visually in Unity.  Replay the compiled component
# states through the same geometry module the runtime uses, and report any
# state where two zones' occupied rectangles overlap.  This is deliberately
# a warning: some overlaps (stacked piles, a marker next to a row) are known
# trade-offs, but the noble/card_market collision should not be silent again.

def _resolve_stage_path(track_path: Path, rel: str) -> Path | None:
    rel = (rel or "").strip()
    if not rel:
        return None
    d = track_path.parent
    for cand in (d / rel, d / (rel + ".json"), d / ".." / rel, d / ".." / (rel + ".json")):
        if cand.exists():
            return cand.resolve()
    return None


def _occupied_zone_boxes(components, zone_defs: dict) -> dict:
    """zone id -> (min_x, max_x, min_z, max_z, item_count) for this state."""
    grouped = {}
    for comp in components or []:
        zid = comp.get("ZoneId")
        zone = zone_defs.get(zid)
        if not zone:
            continue
        x, z = geom.slot_at(zone, int(comp.get("Order", 0) or 0))
        w, h = geom.zone_size(zone)
        box = (x - w * 0.5, x + w * 0.5, z - h * 0.5, z + h * 0.5)
        grouped.setdefault(zid, []).append(box)
    out = {}
    for zid, boxes in grouped.items():
        out[zid] = (min(b[0] for b in boxes), max(b[1] for b in boxes),
                    min(b[2] for b in boxes), max(b[3] for b in boxes), len(boxes))
    return out


def _rect_overlap(a, b):
    ox = min(a[1], b[1]) - max(a[0], b[0])
    oz = min(a[3], b[3]) - max(a[2], b[2])
    return ox, oz


def check_stage_layouts(warnings: list, track: dict, compiled: dict, track_path: Path):
    tree_stage = {t.get("id"): t.get("stage") for t in (compiled.get("trees") or [])}
    stage_defs = {}
    for tree in track.get("trees") or []:
        path = _resolve_stage_path(track_path, tree.get("stage"))
        if path is None:
            continue
        stage = load(path)
        sid = stage.get("id") or path.stem
        stage_defs[sid] = {
            z.get("id"): z for z in (stage.get("zones") or [])
            if isinstance(z, dict) and z.get("id")
            and (z.get("role") or "zone") != "offstage"
        }

    found = {}
    for cue in compiled.get("cues") or []:
        sid = tree_stage.get(cue.get("tree"))
        zone_defs = stage_defs.get(sid)
        if not zone_defs:
            continue
        components = {c.get("Id"): c for c in (cue.get("start_state") or {}).get("components") or []
                      if c.get("Id")}

        def capture(when: str):
            boxes = _occupied_zone_boxes(components.values(), zone_defs)
            zids = sorted(boxes)
            for i in range(len(zids)):
                for j in range(i + 1, len(zids)):
                    a, b = zids[i], zids[j]
                    ox, oz = _rect_overlap(boxes[a], boxes[b])
                    if ox <= 1e-9 or oz <= 1e-9:
                        continue
                    key = (a, b) if a < b else (b, a)
                    rec = found.setdefault(key, {"ox": 0.0, "oz": 0.0, "n": 0,
                                                 "score": (-1.0, -1), "ex": None})
                    rec["ox"] = max(rec["ox"], ox)
                    rec["oz"] = max(rec["oz"], oz)
                    rec["n"] += 1
                    # Keep the state with the largest overlap area, tie-broken by
                    # the number of items, so the warning shows "3 nobles vs 12
                    # market cards" instead of the first one-noble create op.
                    score = (round(ox * oz, 6), boxes[a][4] + boxes[b][4])
                    if score > rec["score"]:
                        rec["score"] = score
                        rec["ex"] = (cue.get("id"), when, boxes[a][4], boxes[b][4])

        capture("start")
        for op in cue.get("state_ops") or []:
            if op.get("op") == "put" and (op.get("item") or {}).get("Id"):
                components[op["item"]["Id"]] = op["item"]
            elif op.get("op") == "remove" and op.get("item_id"):
                components.pop(op["item_id"], None)
            capture(f"t={op.get('at', 0):g}")

    for (a, b), rec in sorted(found.items(), key=lambda kv: max(kv[1]["ox"], kv[1]["oz"]), reverse=True):
        ex = rec["ex"]
        where = f"{ex[0]} {ex[1]}" if ex else "?"
        counts = f"{ex[2]} 件 vs {ex[3]} 件" if ex else "?"
        warnings.append(
            f"stage 布局重叠：{a} × {b} 占用矩形重叠 {rec['ox']:.2f}×{rec['oz']:.2f}"
            f"（{rec['n']} 个状态，例如 {where}：{counts}）"
        )


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

    track = schema.resolve_track(load(src))
    compiled = load(out)
    schema_rep = schema.validate_track(track)
    errors = []
    warnings = list(schema_rep.warnings)
    check_stage_layouts(warnings, track, compiled, src)
    for e in schema_rep.errors:
        errors.append("schema: " + e)

    by_id = {c["id"]: c for c in compiled.get("cues") or []}
    src_by_id = {c.get("id"): c for c in track.get("cues") or [] if isinstance(c, dict) and c.get("id")}
    prev_end = None
    prev_id = None
    for cue in track.get("cues") or []:
        cid = cue.get("id")
        cc = by_id.get(cid)
        if cc is None:
            errors.append(f"{cid}: missing compiled cue")
            continue
        script = cue.get("script") or {}
        check_contract_piece(errors, cid, "enter", script.get("enter") or {}, cc.get("first_state"))
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

        if cue.get("transition") in ("continue", "overlay"):
            # State inheritance is cue-tree based, not track-order based.
            # A cue with an explicit parent may branch away from the previous
            # cue; its start state must match that parent, not the previous item.
            expected_id = cue.get("entry") or cue.get("parent")
            if expected_id == "initial":
                expected_start = {"components": [], "nextSeq": []}
            elif expected_id and expected_id in by_id:
                parent_decl = src_by_id.get(expected_id) or {}
                state_key = "start_state" if parent_decl.get("negative") else "end_state"
                expected_start = by_id[expected_id].get(state_key)
            else:
                expected_start = None
            if expected_start is not None and cc.get("start_state") != expected_start:
                errors.append(
                    f"{cid}: {cue.get('transition')} 但 start_state != {expected_id} 的 end_state"
                )
        if cue.get("transition") in ("cut", "world_cut"):
            first_cam = (cc.get("camera_ops") or [None])[0]
            if not first_cam or abs(float(first_cam.get("at", 0.0))) > 1e-6:
                errors.append(f"{cid}: cut/world_cut 必须有 at=0 的 camera op")
        prev_end = cc.get("end_state")
        prev_id = cid

    check_camera_ops(errors, compiled)
    check_state_ops(errors, compiled)
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
