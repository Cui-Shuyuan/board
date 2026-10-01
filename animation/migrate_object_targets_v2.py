#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Migrate hand-written animation events to the object-target interface.

Before this migration every primitive selected its receiver with whichever
fields happened to be relevant (``zone`` for world objects, ``overlay`` for
screen objects, ``source``/``destination`` for transfers).  The v2 authoring
surface is now:

    target: {"space": "entity", "zone": "...", ...selector...}
    target: {"space": "entity", "zones": ["..."], ...selector...}
    target: {"space": "screen", "id": "overlay_slot"}

The compiler normalizes that interface back to the flat internal fields, so
this script is a source-data migration only.

Usage:
    python3 animation/migrate_object_targets_v2.py <track.anim.json> [--write]
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

SELECTOR_KEYS = ("template", "palette", "concept", "parts", "order")
ENTITY_OPS = {"ensure", "create", "destroy", "shuffle", "move_order", "set_face", "stack"}
SINGLE_TARGET_OPS = {"ensure", "create", "destroy", "shuffle", "move_order", "set_face",
                     "stack", "highlight", "point", "fade", "scale"}


def _take_selector(ev: dict) -> dict:
    out = {}
    for key in SELECTOR_KEYS:
        if key in ev:
            out[key] = ev.pop(key)
    return out


def _entity_target(ev: dict, zone_key: str = "zone") -> dict:
    target = {"space": "entity"}
    source = ev.pop(zone_key, None)
    if isinstance(source, list):
        target["zones"] = list(source)
    else:
        target["zone"] = source
    target.update(_take_selector(ev))
    return target


def migrate_event(ev: dict) -> dict:
    op = ev.get("op")
    if op in ("camera", "wait"):
        return ev
    # Whole-stage picture is a singleton environment primitive, not an object.
    if op == "show" and "picture" in ev and "overlay" not in ev:
        return ev

    if op == "overlay_show":
        ev["op"] = "show"
        target = {"space": "screen", "id": ev.pop("overlay")}
        ev["target"] = target
        return ev
    if op == "overlay_hide":
        ev["op"] = "hide"
        target = {"space": "screen", "id": ev.pop("overlay")}
        ev["target"] = target
        return ev
    if op == "label":
        ev["target"] = {"space": "screen", "id": ev.pop("overlay")}
        return ev

    if op == "stack":
        ev["target"] = _entity_target(ev, "destination")
        return ev

    if op in SINGLE_TARGET_OPS:
        ev["target"] = _entity_target(ev)
        return ev

    if op == "transfer":
        src = ev.pop("source", None)
        target = {"space": "entity"}
        if isinstance(src, list):
            target["zones"] = list(src)
        else:
            target["zone"] = src
        target.update(_take_selector(ev))
        ev["target"] = target
        dest = ev.pop("destination", None)
        ev["destination"] = {"space": "entity", "zone": dest}
        return ev

    return ev


def migrate_doc(doc: dict) -> tuple[int, list[str]]:
    changed = 0
    ops = []
    for cue in doc.get("cues") or []:
        for ev in cue.get("events") or []:
            if not isinstance(ev, dict):
                continue
            before = json.dumps(ev, ensure_ascii=False, sort_keys=True)
            migrate_event(ev)
            after = json.dumps(ev, ensure_ascii=False, sort_keys=True)
            if before != after:
                changed += 1
                ops.append(ev.get("op", "?"))
    return changed, ops


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("track")
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()
    path = Path(args.track)
    doc = json.loads(path.read_text(encoding="utf-8"))
    changed, ops = migrate_doc(doc)
    counts = {}
    for op in ops:
        counts[op] = counts.get(op, 0) + 1
    print(f"{path}: {changed} events migrated")
    for op, n in sorted(counts.items()):
        print(f"  {op}: {n}")
    if args.write:
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n",
                        encoding="utf-8")
        print("written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
