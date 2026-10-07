#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Source-event target normalization and semantic-op lowering."""
from __future__ import annotations

try:  # package-style import
    from .schema_defs import (
        DEFAULT_DRAW_DURATION, DEFAULT_FLIP_DURATION, ENTITY_TARGET_FIELDS,
        SEMANTIC_OPS, SEMANTIC_SOURCE_OPS,
    )
except ImportError:  # direct script/module import with animation/ on sys.path
    from schema_defs import (
        DEFAULT_DRAW_DURATION, DEFAULT_FLIP_DURATION, ENTITY_TARGET_FIELDS,
        SEMANTIC_OPS, SEMANTIC_SOURCE_OPS,
    )

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
        if op in SEMANTIC_SOURCE_OPS:
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
                if op in SEMANTIC_SOURCE_OPS:
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


def _lower_semantic_event(ev: dict, cue_id: str = "") -> list:
    """Lower one semantic action event to its primitive event stream.

    The macros are deliberately thin: they do not validate ownership and do not
    change the runtime model.  ``move`` / ``take`` / ``pay`` are named
    transfer directions; ``flip`` / ``draw`` are edge-flip transfers
    (``from_top`` + ``flip`` are primitive transfer features).
    """
    if not isinstance(ev, dict):
        return [ev]
    op = ev.get("op")
    if op not in SEMANTIC_OPS:
        return [ev]
    where = f"cue {cue_id}: {op}" if cue_id else op

    def require(name):
        value = ev.get(name)
        if not value:
            raise ValueError(f"{where}: missing {name}")
        return value

    if op in ("take", "move", "pay"):
        require("source")
        require("destination")
        out = dict(ev)
        out["op"] = "transfer"
        return [out]

    if op == "flip":
        # Generic edge flip: an explicit object moves to destination while
        # turning over.  draw is the same visual locked to a deck top.
        source = require("source")
        if not isinstance(source, str):
            raise ValueError(f"{where}: flip needs exactly one source zone")
        destination = require("destination")
        if not isinstance(destination, str):
            raise ValueError(f"{where}: flip needs exactly one destination zone")
        to_face = str(ev.get("to") or "").strip().lower()
        if to_face not in ("face_up", "face_down"):
            raise ValueError(f"{where}: flip needs to=face_up/face_down")
        axis = str(ev.get("axis") or "long").strip().lower()
        if axis not in ("long", "short"):
            raise ValueError(f"{where}: axis must be long or short")
        direction = str(ev.get("direction") or "ccw").strip().lower()
        if direction not in ("ccw", "cw"):
            raise ValueError(f"{where}: direction must be ccw or cw")
        out = dict(ev)
        out["op"] = "transfer"
        out["to"] = to_face
        try:
            out["dur"] = float(ev.get("dur", DEFAULT_FLIP_DURATION) or DEFAULT_FLIP_DURATION)
        except (TypeError, ValueError):
            raise ValueError(f"{where}: dur must be numeric seconds")
        out["quantity"] = int(ev.get("quantity", 1) or 1)
        if out["quantity"] != 1:
            raise ValueError(f"{where}: flip quantity must be 1")
        out["flip"] = {"axis": axis, "direction": direction}
        out.pop("axis", None)
        out.pop("direction", None)
        return [out]

    # draw: top of source -> destination, visual is one edge flip.
    source = require("source")
    if not isinstance(source, str):
        raise ValueError(f"{where}: draw needs exactly one source zone")
    destination = require("destination")
    if not isinstance(destination, str):
        raise ValueError(f"{where}: draw needs exactly one destination zone")
    axis = str(ev.get("axis") or "long").strip().lower()
    if axis not in ("long", "short"):
        raise ValueError(f"{where}: axis must be long or short")
    direction = str(ev.get("direction") or "ccw").strip().lower()
    if direction not in ("ccw", "cw"):
        raise ValueError(f"{where}: direction must be ccw or cw")
    out = dict(ev)
    out["op"] = "transfer"
    out["to"] = require("to") if ev.get("to") else "face_up"
    try:
        out["dur"] = float(ev.get("dur", DEFAULT_DRAW_DURATION) or DEFAULT_DRAW_DURATION)
    except (TypeError, ValueError):
        raise ValueError(f"{where}: dur must be numeric seconds")
    out["quantity"] = int(ev.get("quantity", 1) or 1)
    if out["quantity"] != 1:
        raise ValueError(f"{where}: draw quantity must be 1; use multiple draws for several cards")
    # Transfer already defaults to the top of stack/pile sources, but make the
    # draw contract explicit: one top card, no selector materialization.
    out["from_top"] = True
    if not ev.get("space"):
        out["space"] = "entity"
    out["flip"] = {"axis": axis, "direction": direction}
    out.pop("axis", None)
    out.pop("direction", None)
    return [out]
