#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Source track/event validation for v2 animation documents."""
from __future__ import annotations

try:  # package-style import
    from .inherit import resolve_track
    from .schema_defs import (
        FACES, MAGNIFIER_MASKS, MAGNIFIER_SHAPES, MIN_CAMERA_SHOT_SECONDS, OPS,
        Report, SHAPE_KINDS, STATE_OPS, TARGET_SPACES, TRACK_SCHEMA, TRANSITIONS,
        _has_selector,
    )
except ImportError:  # direct script/module import with animation/ on sys.path
    from inherit import resolve_track
    from schema_defs import (
        FACES, MAGNIFIER_MASKS, MAGNIFIER_SHAPES, MIN_CAMERA_SHOT_SECONDS, OPS,
        Report, SHAPE_KINDS, STATE_OPS, TARGET_SPACES, TRACK_SCHEMA, TRANSITIONS,
        _has_selector,
    )

def _check_offset(report: Report, where: str, ev: dict) -> None:
    """``offset`` is numeric time or mapping-valued spatial nudge."""
    if "offset" not in ev:
        return
    raw = ev.get("offset")
    if isinstance(raw, dict):
        for key in ("x", "y"):
            if key in raw:
                try:
                    float(raw[key])
                except (TypeError, ValueError):
                    report.error(f"{where}: offset.{key} must be numeric")
        return
    try:
        float(raw)
    except (TypeError, ValueError):
        report.error(f"{where}: event offset must be numeric; mapping offset = spatial nudge")


def _check_nudge(report: Report, where: str, ev: dict) -> None:
    for key in ("nudge", "marker_offset"):
        raw = ev.get(key)
        if raw is None:
            continue
        if not isinstance(raw, dict):
            report.error(f"{where}: {key} must be an object like {{x, y}}")
            continue
        for axis in ("x", "y"):
            if axis in raw:
                try:
                    float(raw[axis])
                except (TypeError, ValueError):
                    report.error(f"{where}: {key}.{axis} must be numeric")


def _check_object_target(report: Report, where: str, ev: dict):
    target = ev.get("target")
    ann_space = ev.get("annotation_space")
    if target is None:
        return
    targets = target if isinstance(target, list) else [target]
    for i, item in enumerate(targets):
        label = f"{where}.target[{i}]" if isinstance(target, list) else f"{where}.target"
        if not isinstance(item, dict):
            report.error(f"{label}: target item must be an object")
            continue
        space = item.get("space")
        if space not in TARGET_SPACES:
            report.error(f"{label}: target.space must be one of {sorted(TARGET_SPACES)}, got {space!r}")
            continue
        if space == "screen":
            if ann_space == "world":
                report.error(
                    f"{label}: explicit space='world' conflicts with a screen target; "
                    f"remove the top-level space or use target.space='entity'")
            if not (item.get("id") or item.get("overlay")):
                report.error(f"{label}: screen target needs id/overlay")
        else:
            if ann_space == "screen":
                report.error(
                    f"{label}: explicit space='screen' conflicts with an entity target; "
                    f"remove the top-level space or use target.space='screen'")
            if not (item.get("zone") or item.get("zones")):
                report.warn(f"{label}: entity target has no zone/zones")
            if item.get("order") is not None and ev.get("op") == "transfer":
                report.warn(
                    f"{label}: transfer order is a destination slot; "
                    f"write it on the event, not in target"
                )


def _check_selector(report: Report, where: str, ev: dict, required: bool = True):
    if _has_selector(ev):
        return
    if required:
        report.error(f"{where}: state op '{ev.get('op')}' needs a selector "
                     f"(template/palette/concept/parts)")


def _check_event(report: Report, where: str, ev: dict):
    if not isinstance(ev, dict):
        report.error(f"{where}: event must be an object")
        return
    op = ev.get("op")
    if op not in OPS:
        report.error(f"{where}: unknown op {op!r}; expected one of {sorted(OPS)}")
        return
    _check_object_target(report, where, ev)
    _check_offset(report, where, ev)
    _check_nudge(report, where, ev)
    for key in ("part_w", "part_h"):
        if key in ev and ev.get(key) is not None:
            try:
                if float(ev[key]) <= 0:
                    report.error(f"{where}: {key} must be > 0")
            except (TypeError, ValueError):
                report.error(f"{where}: {key} must be numeric")
    style = ev.get("style")
    if style is not None and not isinstance(style, dict):
        report.error(f"{where}: style must be an object")
    style = style if isinstance(style, dict) else {}
    for key in ("color", "stroke", "size", "gap"):
        raw = ev[key] if key in ev else style.get(key)
        if raw is None:
            continue
        if key == "color":
            if not isinstance(raw, str) or not raw.strip():
                report.error(f"{where}: style.color must be a non-empty string")
            continue
        try:
            if float(raw) <= 0:
                report.error(f"{where}: style.{key} must be > 0")
        except (TypeError, ValueError):
            report.error(f"{where}: style.{key} must be numeric")
    anchor = ev.get("anchor")
    if anchor is not None:
        if not isinstance(anchor, str) or not anchor.strip():
            report.error(f"{where}: event '{op}' anchor must be a non-empty string")
    else:
        if "at" not in ev:
            report.error(f"{where}: event '{op}' needs at or anchor")
        else:
            try:
                float(ev["at"])
            except (TypeError, ValueError):
                report.error(f"{where}: event '{op}' at must be numeric")

    if "dur" in ev:
        try:
            if float(ev["dur"]) < 0:
                report.error(f"{where}: event '{op}' dur must be >= 0")
        except (TypeError, ValueError):
            report.error(f"{where}: event '{op}' dur must be numeric")
    if "lead" in ev:
        try:
            if float(ev["lead"]) < 0:
                report.error(f"{where}: event '{op}' lead must be >= 0")
        except (TypeError, ValueError):
            report.error(f"{where}: event '{op}' lead must be numeric")

    if op in STATE_OPS:
        if op == "ensure":
            _check_selector(report, where, ev)
            if not ev.get("zone"):
                report.error(f"{where}: ensure needs zone")
            if int(ev.get("count", 1)) < 0:
                report.error(f"{where}: ensure count must be >= 0")
        elif op == "create":
            _check_selector(report, where, ev)
            if not ev.get("zone"):
                report.error(f"{where}: create needs zone")
            if int(ev.get("count", 1)) < 0:
                report.error(f"{where}: create count must be >= 0")
        elif op == "destroy":
            _check_selector(report, where, ev, required=False)
            if not ev.get("zone"):
                report.error(f"{where}: destroy needs zone")
            if int(ev.get("count", 1)) < 0:
                report.error(f"{where}: destroy count must be >= 0")
            if "from_back" in ev and not isinstance(ev["from_back"], bool):
                report.error(f"{where}: destroy from_back must be boolean")
        elif op == "transfer":
            _check_selector(report, where, ev, required=False)
            if not ev.get("source"):
                report.error(f"{where}: transfer needs source")
            if not ev.get("destination"):
                report.error(f"{where}: transfer needs destination")
            if int(ev.get("quantity", ev.get("count", 1))) < 0:
                report.error(f"{where}: transfer quantity must be >= 0")
        elif op == "stack":
            if not ev.get("destination"):
                report.error(f"{where}: stack needs destination")
            if not ev.get("capacity"):
                report.error(f"{where}: stack needs capacity")
        elif op == "shuffle":
            if not ev.get("zone"):
                report.error(f"{where}: shuffle needs zone")
        elif op == "move_order":
            if not ev.get("zone"):
                report.error(f"{where}: move_order needs zone")
            if "index" not in ev and "order" not in ev:
                report.error(f"{where}: move_order needs index/order")
        elif op == "set_order":
            _check_selector(report, where, ev, required=True)
            if not ev.get("zone"):
                report.error(f"{where}: set_order needs zone")
            if "slot" not in ev and "order" not in ev:
                report.error(f"{where}: set_order needs slot/order")
            if ev.get("layer") is not None:
                try:
                    int(ev["layer"])
                except (TypeError, ValueError):
                    report.error(f"{where}: set_order layer must be an integer")
        elif op == "set_face":
            _check_selector(report, where, ev, required=False)
            if not ev.get("zone"):
                report.error(f"{where}: set_face needs zone")
            if ev.get("to") not in ("face_up", "face_down"):
                report.error(f"{where}: set_face to must be face_up/face_down")
            flip = ev.get("flip")
            if flip not in (None, False, True) and not isinstance(flip, dict):
                report.error(f"{where}: set_face flip must be true or an object")
            elif isinstance(flip, dict):
                axis = str(flip.get("axis") or "long").strip().lower()
                if axis not in ("long", "short"):
                    report.error(f"{where}: flip.axis must be long or short")
                direction = str(flip.get("direction") or "ccw").strip().lower()
                if direction not in ("ccw", "cw"):
                    report.error(f"{where}: flip.direction must be ccw or cw")
                if "destination" in flip:
                    if not flip.get("destination"):
                        report.error(f"{where}: flip.destination must not be empty")
                    if not ev.get("zone"):
                        report.error(f"{where}: flip with destination needs a source zone")
                if flip.get("order") is not None:
                    try:
                        int(flip.get("order"))
                    except (TypeError, ValueError):
                        report.error(f"{where}: flip.order must be an integer")
    else:
        if op == "show":
            if ev.get("space") == "entity":
                if not ev.get("zone"):
                    report.error(f"{where}: entity show needs zone")
            elif "picture" not in ev:
                report.error(f"{where}: show needs picture (may be null)")
        elif op in ("highlight", "point", "shape", "fade", "scale"):
            if ev.get("space") == "screen":
                if not ev.get("overlay"):
                    report.error(f"{where}: {op} screen target needs overlay id")
            elif not ev.get("zone"):
                report.error(f"{where}: {op} needs zone")
            if op == "point" and not ev.get("indicator"):
                report.error(f"{where}: point needs indicator")
            if op == "shape":
                kind = ev.get("shape") or ev.get("indicator")
                if kind not in SHAPE_KINDS:
                    report.error(f"{where}: shape kind must be one of {sorted(SHAPE_KINDS)}, got {kind!r}")
            if op == "fade" and "to_alpha" not in ev and "alpha" not in ev:
                report.error(f"{where}: fade needs to_alpha")
            if op == "scale" and "scale" not in ev:
                report.error(f"{where}: scale needs scale")
        elif op == "hide":
            if ev.get("space") == "entity":
                if not ev.get("zone"):
                    report.error(f"{where}: entity hide needs zone")
            elif ev.get("space") != "screen" and not ev.get("overlay"):
                report.error(f"{where}: hide needs screen target")
        elif op == "wait":
            pass
        elif op == "magnifier":
            if ev.get("space") == "screen" or (ev.get("overlay") and not ev.get("zone")):
                report.error(f"{where}: magnifier only supports an entity target")
            elif not ev.get("zone"):
                report.error(f"{where}: magnifier needs zone")
            rect = ev.get("rect")
            if not isinstance(rect, dict):
                report.error(f"{where}: magnifier needs rect")
            else:
                for key in ("x", "y", "w", "h"):
                    if key in rect:
                        try:
                            float(rect[key])
                        except (TypeError, ValueError):
                            report.error(f"{where}: magnifier rect.{key} must be numeric")
                for key in ("w", "h"):
                    if key in rect:
                        try:
                            if float(rect[key]) <= 0:
                                report.error(f"{where}: magnifier rect.{key} must be > 0")
                        except (TypeError, ValueError):
                            pass
            for key in ("zoom", "padding"):
                raw = ev.get(key)
                if raw is None:
                    continue
                try:
                    if float(raw) <= 0:
                        report.error(f"{where}: magnifier {key} must be > 0")
                except (TypeError, ValueError):
                    report.error(f"{where}: magnifier {key} must be numeric")
            for key in ("view_center_x", "view_center_z"):
                raw = ev.get(key)
                if raw is None:
                    continue
                try:
                    float(raw)
                except (TypeError, ValueError):
                    report.error(f"{where}: magnifier {key} must be numeric")
            raw = ev.get("view_ortho_size")
            if raw is not None:
                try:
                    if float(raw) <= 0:
                        report.error(f"{where}: magnifier view_ortho_size must be > 0")
                except (TypeError, ValueError):
                    report.error(f"{where}: magnifier view_ortho_size must be numeric")
            ident = ev.get("id") if "id" in ev else ev.get("overlay")
            if ident is not None and not isinstance(ident, str):
                report.error(f"{where}: magnifier id must be a string")
            shape = ev.get("shape")
            if shape is not None:
                if not isinstance(shape, str) or shape.strip().lower() not in MAGNIFIER_SHAPES:
                    report.error(f"{where}: magnifier shape must be one of {sorted(MAGNIFIER_SHAPES)}, got {shape!r}")
            mask = ev.get("mask")
            if mask is not None:
                if not isinstance(mask, str) or mask.strip().lower() not in MAGNIFIER_MASKS:
                    report.error(f"{where}: magnifier mask must be one of {sorted(MAGNIFIER_MASKS)}, got {mask!r}")
        elif op == "label":
            if "text" not in ev or ev.get("text") is None:
                report.error(f"{where}: label needs text")
            if ev.get("space") == "screen" and not ev.get("overlay"):
                report.error(f"{where}: screen label needs overlay id")
            if (not ev.get("overlay")
                    and ev.get("space") != "entity"
                    and not ev.get("zone")
                    and not _has_selector(ev)):
                report.error(f"{where}: label needs an entity target or overlay id")
        elif op == "overlay_show":
            if not ev.get("overlay"):
                report.error(f"{where}: overlay_show needs overlay")
            if not (ev.get("template") or ev.get("image") or ev.get("background")):
                report.error(f"{where}: overlay_show needs template/image/background")
            rect = ev.get("rect")
            if rect is not None:
                if not isinstance(rect, dict):
                    report.error(f"{where}: overlay_show rect must be an object")
                else:
                    for key in ("x", "y", "w", "h"):
                        if key in rect:
                            try:
                                float(rect[key])
                            except (TypeError, ValueError):
                                report.error(f"{where}: overlay_show rect.{key} must be numeric")
        elif op == "overlay_hide":
            if not ev.get("overlay"):
                report.error(f"{where}: overlay_hide needs overlay")
        elif op == "camera":
            if not ev.get("shot"):
                report.error(f"{where}: camera needs shot")

    to = ev.get("to")
    if to not in ("face_up", "face_down", None, ""):
        report.error(f"{where}: to must be face_up/face_down/empty, got {to!r}")


def _check_contract(report: Report, where: str, part: dict):
    if part is None:
        report.error(f"{where}: missing contract")
        return
    if not isinstance(part, dict):
        report.error(f"{where}: contract must be an object")
        return
    if "picture" not in part:
        report.warn(f"{where}: no picture field; interpreted as picture=null")
    zones = part.get("zones")
    if zones is None:
        zones = {}
    if isinstance(zones, dict):
        zones = list(zones.items())
    if not isinstance(zones, list):
        report.error(f"{where}.zones: must be a map or list")
        return
    for entry in zones:
        if isinstance(entry, dict):
            zone = entry.get("zone")
            data = entry
        else:
            zone, data = entry
        label = f"{where}.zones[{zone!r}]"
        if not zone:
            report.error(f"{label}: zone id required")
            continue
        if not isinstance(data, dict):
            report.error(f"{label}: must be an object")
            continue
        items = data.get("items", [])
        if not isinstance(items, list):
            report.error(f"{label}.items must be a list")
            continue
        for i, item in enumerate(items):
            il = f"{label}.items[{i}]"
            if not isinstance(item, dict):
                report.error(f"{il}: must be an object")
                continue
            if not item.get("template"):
                report.error(f"{il}: template required (v2 contract uses stage identity)")
            if "count" in item:
                try:
                    if int(item["count"]) < 0:
                        report.error(f"{il}.count must be >= 0")
                except (TypeError, ValueError):
                    report.error(f"{il}.count must be numeric")
            face = item.get("face")
            if face not in FACES:
                report.error(f"{il}.face must be up/down/hidden/empty, got {face!r}")


# ── parent-child attribute inheritance ──────────────────────────────────────
#
# A cue is a small node.  The parent link is not only an entry-state pointer:
# a child inherits every attribute that it does not explicitly assign, and a
# child assignment overrides the inherited value.  `events` are different:
# they are this node's own state delta, so they are never inherited.
#
# State contracts (`script.enter` / `script.exit`) are inherited from the
# parent's *exit* state, not from the parent's entry state: a child naturally
# starts where the parent ended.  If the child writes its own contract, it is
# deep-merged over that inherited state, so a child only has to declare the
# zones/picture it changes.  `cut` / `world_cut` are reset points and do not
# inherit state contracts.


def validate_track(doc: dict, report: Report | None = None) -> Report:
    rep = report or Report()
    doc = resolve_track(doc)
    if not isinstance(doc, dict):
        rep.error("track root must be an object")
        return rep
    if doc.get("schema") != TRACK_SCHEMA:
        rep.error(f"schema: expected {TRACK_SCHEMA!r}, got {doc.get('schema')!r}")
    if doc.get("kind") not in ("animation_track", None):
        rep.error(f"kind: expected 'animation_track', got {doc.get('kind')!r}")
    for key in ("game", "track", "default_tree"):
        if not doc.get(key):
            rep.error(f"{key}: required")
    top_style = doc.get("annotation_style")
    if top_style is not None and not isinstance(top_style, dict):
        rep.error("annotation_style: must be an object")
    elif isinstance(top_style, dict):
        for key in ("color", "stroke", "size", "gap"):
            raw = top_style.get(key)
            if raw is None:
                continue
            if key == "color":
                if not isinstance(raw, str) or not raw.strip():
                    rep.error("annotation_style.color must be a non-empty string")
                continue
            try:
                if float(raw) <= 0:
                    rep.error(f"annotation_style.{key} must be > 0")
            except (TypeError, ValueError):
                rep.error(f"annotation_style.{key} must be numeric")

    worlds = doc.get("worlds")
    if not isinstance(worlds, list) or not worlds:
        rep.error("worlds: non-empty list required")
        worlds = []
    world_ids = set()
    for i, w in enumerate(worlds):
        where = f"worlds[{i}]"
        if not isinstance(w, dict):
            rep.error(f"{where}: must be an object"); continue
        wid = w.get("id")
        if not wid:
            rep.error(f"{where}.id required"); continue
        if wid in world_ids:
            rep.error(f"{where}.id duplicated: {wid}")
        world_ids.add(wid)
        mode = w.get("mode", "isolated")
        if mode not in ("isolated", "shared"):
            rep.error(f"{where}.mode must be isolated/shared, got {mode!r}")
        if not w.get("why"):
            rep.warn(f"{where}.why is empty; write why this world exists")

    trees = doc.get("trees")
    if not isinstance(trees, list) or not trees:
        rep.error("trees: non-empty list required")
        trees = []
    tree_ids = set()
    for i, t in enumerate(trees):
        where = f"trees[{i}]"
        if not isinstance(t, dict):
            rep.error(f"{where}: must be an object"); continue
        tid = t.get("id")
        if not tid:
            rep.error(f"{where}.id required"); continue
        if tid in tree_ids:
            rep.error(f"{where}.id duplicated: {tid}")
        tree_ids.add(tid)
        if t.get("world") not in world_ids:
            rep.error(f"{where}.world {t.get('world')!r} is not declared in worlds")
        if not t.get("stage"):
            rep.error(f"{where}.stage required")
        for k in ("purpose", "initial", "extent_note"):
            if not t.get(k):
                rep.warn(f"{where}.{k} is empty; write it in the text script")

    cues = doc.get("cues")
    if not isinstance(cues, list):
        rep.error("cues: list required")
        cues = []
    cue_ids = [c.get("id") for c in cues if isinstance(c, dict) and c.get("id")]
    if len(cue_ids) != len(set(cue_ids)):
        rep.error("cues: duplicate cue id")
    cue_order = {cid: idx for idx, cid in enumerate(cue_ids)}
    by_resolved = {
        c["id"]: c for c in cues
        if isinstance(c, dict) and c.get("id")
    }

    prev = None
    for i, c in enumerate(cues):
        where = f"cues[{i}]"
        if not isinstance(c, dict):
            rep.error(f"{where}: must be an object"); continue
        cid = c.get("id")
        if not cid:
            rep.error(f"{where}.id required"); continue
        where = f"cue {cid}"
        if c.get("tree") not in tree_ids:
            rep.error(f"{where}: tree {c.get('tree')!r} is not declared")
        if i == 0 and not c.get("parent") and not c.get("entry"):
            rep.error(f"{where}: first cue must declare an explicit entry: 'initial'")
        stage_ref = c.get("stage")
        if stage_ref is not None:
            if not isinstance(stage_ref, str) or not stage_ref.strip():
                rep.error(f"{where}: stage must be a non-empty string")
        demo_flag = c.get("demo")
        if demo_flag is not None and not isinstance(demo_flag, bool):
            rep.error(f"{where}: demo must be boolean")
        trans = c.get("transition")
        if trans not in TRANSITIONS:
            rep.error(f"{where}: transition must be one of {sorted(TRANSITIONS)}, got {trans!r}")
        parent = c.get("parent")
        if parent is not None and parent not in cue_ids:
            rep.error(f"{where}: parent {parent!r} does not exist")
        entry = c.get("entry")
        if entry is not None and entry != "initial" and entry not in cue_ids:
            rep.error(f"{where}: entry {entry!r} does not exist")
        if entry is not None and entry != "initial" and entry in cue_order:
            if cue_order[entry] >= i:
                rep.error(f"{where}: entry {entry!r} must point to an earlier cue")
        if i > 0 and trans != "continue" and not c.get("parent"):
            rep.warn(f"{where}: non-continue transition should declare parent explicitly")

        script = c.get("script")
        if not isinstance(script, dict):
            rep.error(f"{where}: script object required"); script = {}
        if not script.get("story"):
            rep.error(f"{where}: script.story required")
        if not script.get("note"):
            rep.warn(f"{where}: script.note is empty")
        if "camera" in script:
            rep.error(f"{where}: script.camera is not supported; use a camera event + stage shot")
        _check_contract(rep, f"{where}.script.enter", script.get("enter"))
        _check_contract(rep, f"{where}.script.exit", script.get("exit"))

        events = c.get("events", [])
        if not isinstance(events, list):
            rep.error(f"{where}: events must be a list"); events = []
        last_at = -1.0
        for j, ev in enumerate(events):
            _check_event(rep, f"{where}.events[{j}]", ev)
            if isinstance(ev, dict) and "at" in ev:
                at = float(ev.get("at", 0.0))
                if at + 1e-9 < last_at:
                    rep.error(f"{where}.events[{j}]: events are not sorted by at "
                             f"({last_at} -> {at})")
                last_at = max(last_at, at)
        camera_events = [ev for ev in events
                         if isinstance(ev, dict) and ev.get("op") == "camera"]
        # Anchored camera events are resolved against TTS beats by the compiler;
        # numeric camera events can be checked here directly.
        if not any("at" not in ev and ev.get("anchor") for ev in camera_events):
            camera_times = []
            for ev in camera_events:
                at = float(ev.get("at", 0.0) or 0.0) + max(0.0, float(ev.get("lead", 0.0) or 0.0))
                camera_times.append((at, ev.get("shot", "")))
            camera_times.sort(key=lambda x: x[0])
            for j in range(len(camera_times) - 1):
                t0, shot0 = camera_times[j]
                t1, shot1 = camera_times[j + 1]
                hold = t1 - t0
                if hold < MIN_CAMERA_SHOT_SECONDS - 1e-9:
                    rep.error(
                        f"{where}: camera shot {shot0!r} holds only {hold:.3f}s before "
                        f"{shot1!r}; minimum shot hold is {MIN_CAMERA_SHOT_SECONDS:.2f}s "
                        f"(one-frame camera shots are authoring bugs)")
        prev = cid

    if rep.errors:
        return rep

    # Tree is the visual scope, not a state boundary.  A child may continue
    # its parent's state while switching stage/tree; the compiler validates the
    # copied snapshots directly.
    return rep
