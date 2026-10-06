#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v2 animation schema loader / validator.

This is intentionally source-focused: it checks the hand-written `.anim.json`
and `.stage.json` format before the deterministic compiler turns them into
`*.compiled.json`.

Usage:
    python3 animation/anim_schema_v2.py content/games/splendor/tutorial/anim/v2/_schema_example.anim.json
    python3 animation/anim_schema_v2.py --example
"""
from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from pathlib import Path

TRACK_SCHEMA = "tutorial-anim/v2"
STAGE_SCHEMA = "tutorial-stage/v2"
COMPILED_TRACK_SCHEMA = "tutorial-anim-compiled/v2"
COMPILED_STAGE_SCHEMA = "tutorial-stage-compiled/v2"

TRANSITIONS = {"continue", "overlay", "cut", "world_cut"}
STATE_OPS = {"ensure", "create", "destroy", "transfer", "stack", "shuffle", "move_order", "set_face"}
PRESENTATION_OPS = {"show", "hide", "highlight", "point", "shape", "fade", "scale", "wait", "camera", "label", "magnifier",
                     "overlay_show", "overlay_hide"}
SHAPE_KINDS = {"arrow", "circle", "cross", "forbid", "box"}
# Magnifier 专用形状（circle=圆形透镜；box=矩形透镜）
MAGNIFIER_SHAPES = {"circle", "box"}
MAGNIFIER_MASKS = {"items", "full"}
# 对象接口：世界/屏幕对象的原语统一指向一个 target。
#   {"space": "entity", "zone": ..., "template": ..., "palette": ..., "concept": ..., "parts": [...], "order": n}
#   {"space": "screen", "id": "overlay_slot"}
TARGET_SPACES = {"entity", "screen"}
# `order` is a normal selector field for most entity ops.  For transfer it is
# destination placement and must stay on the event; `_check_object_target`
# warns when it is written inside target instead.
ENTITY_TARGET_FIELDS = ("zone", "template", "palette", "concept", "parts", "order")
# 一个机位至少要保持这么久，否则属于「1 帧镜头」书写事故。
MIN_CAMERA_SHOT_SECONDS = 0.4
OPS = STATE_OPS | PRESENTATION_OPS
FACES = {"up", "down", "hidden", None, ""}
CONCEPT_ID_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.:\-]*$")
SPECIAL_CONCEPT_RE = re.compile(r"^<[A-Za-z_][A-Za-z0-9_.:\-]*>$")

def _valid_stage_concept(value: str) -> bool:
    text = (value or "").strip()
    return bool(CONCEPT_ID_RE.match(text) or SPECIAL_CONCEPT_RE.match(text))


class Report:
    def __init__(self):
        self.errors: list[str] = []
        self.warnings: list[str] = []

    def error(self, msg: str):
        self.errors.append(msg)

    def warn(self, msg: str):
        self.warnings.append(msg)

    def ok(self) -> bool:
        return not self.errors


def load_json(path: str | Path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _has_selector(ev: dict) -> bool:
    return any(ev.get(k) for k in ("template", "palette", "concept", "parts"))


def _entity_target_zone(target: dict):
    if not isinstance(target, dict):
        return None
    zones = target.get("zones")
    if isinstance(zones, list):
        return [str(z) for z in zones if z]
    return target.get("zone")


def _merge_entity_target_fields(ev: dict, target: dict):
    """Copy selector fields from an entity target object onto the flat event.

    Transfer `order` is a compatibility pass-through (destination placement);
    new source data must write it on the event itself, not in `target`.
    """
    for key in ENTITY_TARGET_FIELDS:
        if key in target and (key not in ev or ev.get(key) in (None, "")):
            ev[key] = target[key]


def _normalize_event(ev):
    """Normalize the source-level object-target interface into the flat fields
    that the compiler, validators and audit tools consume.

    Source events may target either an entity (zone + selector) or a screen
    object (overlay slot id).  Keeping the flattened form means the compiler,
    validators and audit tools share one implementation; the interface is the
    authoring surface, not a second data model.
    """
    if not isinstance(ev, dict):
        return ev
    op = ev.get("op")
    target = ev.get("target")
    explicit_space = str(ev.get("space") or "").strip().lower()
    if explicit_space in ("world", "screen"):
        ev["annotation_space"] = explicit_space
        # Keep the internal flat form meaningful: world == entity,
        # screen == overlay/screen object.
        ev["space"] = "entity" if explicit_space == "world" else "screen"
    elif explicit_space in ("entity", "screen"):
        ev["space"] = explicit_space
    if isinstance(target, list):
        # Multi-target form (transfer from several source zones).  Selection
        # fields are taken from the first target; V2 transfer already applies
        # one selector across all source zones.
        zones = []
        first = None
        for item in target:
            if not isinstance(item, dict) or item.get("space") != "entity":
                continue
            first = first or item
            z = _entity_target_zone(item)
            if isinstance(z, list):
                zones.extend(z)
            elif z:
                zones.append(z)
        if first is not None:
            _merge_entity_target_fields(ev, first)
        if op == "transfer":
            ev["source"] = zones
            ev["space"] = "entity"
    elif isinstance(target, dict):
        space = target.get("space")
        if space == "screen":
            slot = target.get("id") or target.get("overlay")
            ev["space"] = "screen"
            if slot:
                ev["overlay"] = slot
            if op == "show":
                ev["op"] = "overlay_show"
            elif op == "hide":
                ev["op"] = "overlay_hide"
        elif space == "entity":
            ev["space"] = "entity"
            if op == "stack":
                zone = _entity_target_zone(target)
                if isinstance(zone, list):
                    zone = zone[0] if zone else None
                if not ev.get("destination") and zone:
                    ev["destination"] = zone
            else:
                _merge_entity_target_fields(ev, target)
                zone = _entity_target_zone(target)
                if op == "transfer":
                    if isinstance(zone, list):
                        ev["source"] = zone
                    elif zone and not ev.get("source"):
                        ev["source"] = zone
                elif zone and not ev.get("zone"):
                    ev["zone"] = zone
    # transfer destination may itself be an object reference
    dest = ev.get("destination")
    if isinstance(dest, dict):
        dspace = dest.get("space")
        if dspace == "entity":
            ev["destination"] = dest.get("zone")
        elif dspace == "screen":
            ev["destination"] = dest.get("id") or dest.get("overlay")
            ev["destination_space"] = "screen"
    # some hand-written states may wrap the source in an object reference
    src = ev.get("source")
    if isinstance(src, dict):
        sspace = src.get("space")
        if sspace == "entity":
            ev["source"] = src.get("zone")
    # `set_face` flip can carry its own destination (flip into a zone/slot).
    # Normalize the nested object target to the same flat zone string used by
    # transfer, so the compiler only sees one form.
    flip = ev.get("flip")
    if isinstance(flip, dict):
        fdest = flip.get("destination")
        if isinstance(fdest, dict):
            fspace = fdest.get("space")
            if fspace == "entity":
                flip["destination"] = fdest.get("zone")
            elif fspace == "screen":
                flip["destination"] = fdest.get("id") or fdest.get("overlay")
        if flip.get("destination") is None:
            flip.pop("destination", None)
    return ev


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

_LOCAL_CUE_KEYS = {"id", "parent", "entry", "negative", "qa", "events", "stage", "demo"}


def _deep_copy(v):
    return copy.deepcopy(v)


def _deep_merge(base, overlay):
    """Recursive map merge; child values override, arrays/scalars replace."""
    if isinstance(base, dict) and isinstance(overlay, dict):
        out = {k: _deep_copy(v) for k, v in base.items()}
        for k, v in overlay.items():
            out[k] = _deep_merge(out[k], v) if k in out else _deep_copy(v)
        return out
    return _deep_copy(overlay)


def _merge_contract(base, overlay):
    """Merge an inherited state contract with a child's contract.

    Contract zones are merged at the zone level: if the child writes a zone,
    that whole zone spec replaces the inherited one.  This matters for counts
    and item lists (e.g. child writes `showcase: {count: 0}` to clear it).
    """
    if not isinstance(base, dict) or not isinstance(overlay, dict):
        return _deep_copy(overlay)
    out = {k: _deep_copy(v) for k, v in base.items()}
    for k, v in overlay.items():
        if k == "zones" and isinstance(v, dict):
            zones = _deep_copy(out.get("zones") or {})
            for zid, spec in v.items():
                zones[zid] = _deep_copy(spec)
            out["zones"] = zones
        else:
            out[k] = _deep_copy(v)
    return out


def resolve_track(doc: dict) -> dict:
    """Return a copy of a track document with cue inheritance expanded.

    ``tree`` is the visual scope (stage/zones/camera); it is not a state
    partition.  ``cue_n`` inherits the full state of its effective source by
    default: explicit ``entry`` wins, otherwise ``parent``; if neither is
    present, the previous cue in track order is the source.  State contracts
    follow the same effective source.  A stage may display only a subset of the
    inherited state; components in zones absent from the current stage stay in
    the logical state and are hidden at render time.
    """
    if not isinstance(doc, dict) or not isinstance(doc.get("cues"), list):
        return doc
    raw_cues = doc["cues"]
    by_id = {}
    order = {}
    duplicate = False
    for i, c in enumerate(raw_cues):
        if not isinstance(c, dict) or not c.get("id"):
            continue
        if c["id"] in by_id:
            duplicate = True
        by_id[c["id"]] = c
        order[c["id"]] = i
    if duplicate:
        return doc

    tree_stage_paths = {
        str(t.get("id")): t.get("stage")
        for t in (doc.get("trees") or [])
        if isinstance(t, dict) and t.get("id")
    }

    resolved = {}
    visiting = set()

    def _prev_raw_id(cid):
        idx = order.get(cid, -1)
        if idx <= 0:
            return None
        prev = raw_cues[idx - 1]
        return prev.get("id") if isinstance(prev, dict) else None

    def resolve_one(cid):
        if cid in resolved:
            return resolved[cid]
        if cid in visiting:
            return {}
        raw = by_id.get(cid)
        if raw is None:
            return {}
        visiting.add(cid)
        parent_id = raw.get("parent")
        base = resolve_one(parent_id) if parent_id in by_id else {}

        transition = raw.get("transition", "continue")
        # tree resolution: explicit -> parent -> previous cue -> default
        tree = raw.get("tree")
        if tree is None and base:
            tree = base.get("tree")
        if tree is None:
            prev_id = _prev_raw_id(cid)
            prev_res = resolve_one(prev_id) if prev_id in by_id else {}
            tree = prev_res.get("tree")
        if tree is None:
            tree = doc.get("default_tree")

        # effective state source (mirrors the compiler)
        entry = raw.get("entry")
        state_source_id = None
        if entry:
            if entry != "initial" and entry in by_id:
                state_source_id = entry
        elif transition not in ("cut", "world_cut"):
            if parent_id in by_id:
                state_source_id = parent_id
            else:
                prev_id = _prev_raw_id(cid)
                if prev_id in by_id:
                    state_source_id = prev_id
        source_raw = by_id.get(state_source_id) if state_source_id else None

        # authoring-parent inheritance may cross trees now
        inherit = bool(base) and transition in ("continue", "overlay")
        eff = {}
        if inherit:
            for k, v in base.items():
                if k not in _LOCAL_CUE_KEYS and k not in ("transition", "tree", "stage"):
                    eff[k] = _deep_copy(v)

        for k, v in raw.items():
            if k in _LOCAL_CUE_KEYS:
                continue
            if k == "script":
                continue
            eff[k] = _deep_copy(v)

        eff["tree"] = _deep_copy(tree)
        raw_stage = raw.get("stage")
        if raw_stage:
            resolved_stage = _deep_copy(raw_stage)
        elif base and raw.get("tree") is None and base.get("stage"):
            resolved_stage = _deep_copy(base.get("stage"))
        else:
            resolved_stage = _deep_copy(tree_stage_paths.get(str(tree)))
        if resolved_stage is not None:
            eff["stage"] = resolved_stage

        base_script = base.get("script") or {}
        source_script = (resolved.get(state_source_id) or {}).get("script") or {} if state_source_id else {}
        child_script = raw.get("script") if isinstance(raw.get("script"), dict) else {}

        script = {}
        for k, v in base_script.items():
            if k not in ("enter", "exit"):
                script[k] = _deep_copy(v)
        for k, v in child_script.items():
            if k not in ("enter", "exit"):
                script[k] = _deep_copy(v)

        if source_raw is not None and bool(source_raw.get("negative")):
            inherited_state = source_script.get("enter")
        else:
            inherited_state = source_script.get("exit")
        if inherited_state is None and source_script:
            inherited_state = source_script.get("enter")

        child_enter = child_script.get("enter")
        child_exit = child_script.get("exit")
        if isinstance(inherited_state, dict):
            if isinstance(child_enter, dict):
                script["enter"] = _merge_contract(inherited_state, child_enter)
            else:
                script["enter"] = _deep_copy(inherited_state)
            if isinstance(child_exit, dict):
                script["exit"] = _merge_contract(inherited_state, child_exit)
            else:
                script["exit"] = _deep_copy(inherited_state)
        else:
            if isinstance(child_enter, dict):
                script["enter"] = _deep_copy(child_enter)
            if isinstance(child_exit, dict):
                script["exit"] = _deep_copy(child_exit)

        if script or "script" in raw:
            eff["script"] = script

        eff["id"] = raw.get("id")
        eff["parent"] = raw.get("parent")
        if raw.get("negative") is not None:
            eff["negative"] = bool(raw.get("negative"))
        if raw.get("qa") is not None:
            eff["qa"] = _deep_copy(raw.get("qa"))
        if raw.get("entry") is not None:
            eff["entry"] = _deep_copy(raw.get("entry"))
        if "demo" in raw:
            eff["demo"] = bool(raw.get("demo"))
        elif base and inherit:
            eff["demo"] = bool(base.get("demo"))
        else:
            eff["demo"] = False
        eff["events"] = [_normalize_event(_deep_copy(ev))
                         for ev in (raw.get("events") or [])]
        if "transition" not in raw:
            eff["transition"] = "continue"
        if "tree" not in eff or eff.get("tree") is None:
            eff["tree"] = _deep_copy(tree)

        visiting.discard(cid)
        resolved[cid] = eff
        return eff

    new_cues = []
    for c in raw_cues:
        if isinstance(c, dict) and c.get("id"):
            new_cues.append(resolve_one(c["id"]))
        else:
            new_cues.append(c)
    out = dict(doc)
    out["cues"] = new_cues
    return out


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


def validate_stage(doc: dict, report: Report | None = None) -> Report:
    rep = report or Report()
    if not isinstance(doc, dict):
        rep.error("stage root must be an object")
        return rep
    if doc.get("schema") != STAGE_SCHEMA:
        rep.error(f"schema: expected {STAGE_SCHEMA!r}, got {doc.get('schema')!r}")
    for key in ("game", "id", "zones", "templates"):
        if key not in doc:
            rep.error(f"{key}: required")
    zones = doc.get("zones") or []
    ids = [z.get("id") for z in zones if isinstance(z, dict)]
    if len(ids) != len(set(ids)):
        rep.error("stage: duplicate zone id")
    for i, z in enumerate(zones):
        if not isinstance(z, dict):
            rep.error(f"zones[{i}]: must be object"); continue
        zid = z.get("id")
        if not zid:
            rep.error(f"zones[{i}].id required")
        where = f"zones[{i}]"
        if not isinstance(z.get("layout", {}), dict):
            rep.error(f"{where}.layout must be object")
        concept = z.get("concept")
        label = z.get("label")
        parts = z.get("parts")
        qa_ignore = z.get("qa_ignore")
        if qa_ignore is not None and not isinstance(qa_ignore, bool):
            rep.error(f"{where}.qa_ignore must be boolean")
            qa_ignore = False
        qa_ignore = bool(qa_ignore)
        concept_text = ""
        if concept is not None:
            if not isinstance(concept, str):
                rep.error(f"{where}.concept must be a string or null")
            else:
                concept_text = concept.strip()
                if concept_text and not _valid_stage_concept(concept_text):
                    rep.error(
                        f"{where}.concept must be a concept id or special reference "
                        f"like <player_holding>; got {concept!r}"
                    )
                if concept_text and not (isinstance(label, str) and label.strip()):
                    rep.warn(f"{where}.label is empty; QA summaries will fall back to concept")
        if label is not None and not isinstance(label, str):
            rep.error(f"{where}.label must be a string or null")
        if parts is not None:
            if not isinstance(parts, list):
                rep.error(f"{where}.parts must be a list")
            else:
                if parts and not concept_text and not qa_ignore:
                    rep.warn(f"{where}.parts is non-empty but concept is empty; "
                             f"logical mapping will be ignored")
                for j, part in enumerate(parts):
                    pl = f"{where}.parts[{j}]"
                    if not isinstance(part, dict):
                        rep.error(f"{pl}: must be an object")
                        continue
                    key = part.get("key")
                    if not isinstance(key, str) or not key.strip():
                        rep.error(f"{pl}.key is required and must be a non-empty string")
                    if "value" not in part or part.get("value") is None:
                        rep.error(f"{pl}.value is required")
    tids = [t.get("id") for t in (doc.get("templates") or []) if isinstance(t, dict)]
    if len(tids) != len(set(tids)):
        rep.error("stage: duplicate template id")
    shots = doc.get("shots") or []
    if not isinstance(shots, list):
        rep.error("stage: shots must be a list")
    sids = []
    for i, sh in enumerate(shots):
        if not isinstance(sh, dict):
            rep.error(f"shots[{i}]: must be object"); continue
        if not sh.get("id"):
            rep.error(f"shots[{i}].id required")
        sids.append(sh.get("id"))
        zones = sh.get("zones")
        if not isinstance(zones, list) or not zones:
            rep.error(f"shots[{i}].zones must be a non-empty list")
        fill = sh.get("fill", 0.8)
        if not (0 < float(fill) <= 1):
            rep.error(f"shots[{i}].fill must be in (0,1]")
    if len(sids) != len(set(sids)):
        rep.error("stage: duplicate shot id")
    for t in doc.get("templates") or []:
        if not isinstance(t, dict):
            continue
        for key in ("face_image", "back_image"):
            img = t.get(key) or ""
            if not img:
                continue
            if not img.endswith("_cutout.png"):
                rep.warn(f"template {t.get('id')}: {key} 不是 _cutout.png（"
                         f"运行时会重新走白底/圆角启发式，可能出白边）")
        for m in t.get("face_image_by_palette") or []:
            if not isinstance(m, dict):
                continue
            img = m.get("face_image") or m.get("back_image") or ""
            if img and not img.endswith("_cutout.png"):
                rep.warn(f"template {t.get('id')}: palette 图不是 _cutout.png")
    return rep


def print_report(path: str, rep: Report) -> int:
    for e in rep.errors:
        print(f"ERR  {path}: {e}")
    for w in rep.warnings:
        print(f"WARN {path}: {w}")
    if rep.ok():
        print(f"OK   {path} ({len(rep.warnings)} warnings)")
        return 0
    print(f"FAIL {path}: {len(rep.errors)} errors, {len(rep.warnings)} warnings", file=sys.stderr)
    return 1


def validate_file(path: str | Path) -> Report:
    doc = load_json(path)
    kind = (doc or {}).get("schema") if isinstance(doc, dict) else None
    if kind == TRACK_SCHEMA:
        return validate_track(doc)
    if kind == STAGE_SCHEMA:
        return validate_stage(doc)
    rep = Report()
    rep.error(f"unknown schema {kind!r}; expected {TRACK_SCHEMA!r} or {STAGE_SCHEMA!r}")
    return rep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("paths", nargs="*")
    ap.add_argument("--example", action="store_true")
    a = ap.parse_args()
    paths = a.paths
    if a.example or not paths:
        root = Path(__file__).resolve().parent.parent
        paths = [root / "content/games/splendor/tutorial/anim/v2/_schema_example.anim.json",
                 root / "content/games/splendor/tutorial/anim/v2/_schema_example.stage.json"]
    rc = 0
    for p in paths:
        rc |= print_report(str(p), validate_file(p))
    return rc


if __name__ == "__main__":
    sys.exit(main())
