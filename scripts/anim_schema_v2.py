#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v2 animation schema loader / validator.

This is intentionally source-focused: it checks the hand-written `.anim.json`
and `.stage.json` format before the deterministic compiler turns them into
`*.compiled.json`.

Usage:
    python3 scripts/anim_schema_v2.py games/splendor/tutorial/anim/v2/_schema_example.anim.json
    python3 scripts/anim_schema_v2.py --example
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

TRACK_SCHEMA = "tutorial-anim/v2"
STAGE_SCHEMA = "tutorial-stage/v2"
COMPILED_TRACK_SCHEMA = "tutorial-anim-compiled/v2"
COMPILED_STAGE_SCHEMA = "tutorial-stage-compiled/v2"

TRANSITIONS = {"continue", "overlay", "cut", "world_cut"}
STATE_OPS = {"ensure", "create", "destroy", "transfer", "stack", "shuffle", "move_order", "set_face"}
PRESENTATION_OPS = {"show", "highlight", "point", "fade", "scale", "wait", "camera"}
OPS = STATE_OPS | PRESENTATION_OPS
FACES = {"up", "down", "hidden", None, ""}


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
    if "at" not in ev:
        report.error(f"{where}: event '{op}' missing at")
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
    else:
        if op == "show":
            if "picture" not in ev:
                report.error(f"{where}: show needs picture (may be null)")
        elif op in ("highlight", "point", "fade", "scale"):
            if not ev.get("zone"):
                report.error(f"{where}: {op} needs zone")
            if op == "point" and not ev.get("indicator"):
                report.error(f"{where}: point needs indicator")
            if op == "fade" and "to_alpha" not in ev and "alpha" not in ev:
                report.error(f"{where}: fade needs to_alpha")
            if op == "scale" and "scale" not in ev:
                report.error(f"{where}: scale needs scale")
        elif op == "wait":
            pass
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


def validate_track(doc: dict, report: Report | None = None) -> Report:
    rep = report or Report()
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
        trans = c.get("transition")
        if trans not in TRANSITIONS:
            rep.error(f"{where}: transition must be one of {sorted(TRANSITIONS)}, got {trans!r}")
        parent = c.get("parent")
        if parent is not None and parent not in cue_ids:
            rep.error(f"{where}: parent {parent!r} does not exist")
        if i > 0 and trans != "continue" and not c.get("parent"):
            rep.warn(f"{where}: non-continue transition should declare parent explicitly")

        script = c.get("script")
        if not isinstance(script, dict):
            rep.error(f"{where}: script object required"); script = {}
        if not script.get("story"):
            rep.error(f"{where}: script.story required")
        if not script.get("note"):
            rep.warn(f"{where}: script.note is empty")
        cam = script.get("camera")
        if cam is not None:
            # Legacy carrier field: cameras are now timed `camera` events that
            # reference a named stage shot.  Keep accepting it for a transition
            # period, but warn so data gets migrated.
            rep.warn(f"{where}: script.camera is deprecated; use a camera event + stage shot")
            if not isinstance(cam, dict):
                rep.error(f"{where}: script.camera must be an object")
            else:
                zones = cam.get("zones", [])
                if not isinstance(zones, list) or not zones:
                    rep.error(f"{where}: script.camera.zones must be a non-empty list")
                fill = cam.get("fill", 0.8)
                if fill is not None and not (0 < float(fill) <= 1):
                    rep.error(f"{where}: script.camera.fill must be in (0,1]")
        _check_contract(rep, f"{where}.script.enter", script.get("enter"))
        _check_contract(rep, f"{where}.script.exit", script.get("exit"))

        events = c.get("events", [])
        if not isinstance(events, list):
            rep.error(f"{where}: events must be a list"); events = []
        last_at = -1.0
        for j, ev in enumerate(events):
            _check_event(rep, f"{where}.events[{j}]", ev)
            if isinstance(ev, dict):
                at = float(ev.get("at", 0.0))
                if at + 1e-9 < last_at:
                    rep.error(f"{where}.events[{j}]: events are not sorted by at "
                             f"({last_at} -> {at})")
                last_at = max(last_at, at)
        prev = cid

    if rep.errors:
        return rep

    # Cross-cue continuity is deliberately cheap here; full continuity is
    # checked by the compiler against snapshots.
    by_id = {c["id"]: c for c in cues if isinstance(c, dict) and c.get("id")}
    for cid, c in by_id.items():
        if c.get("parent") and c["parent"] in by_id:
            p = by_id[c["parent"]]
            if (p.get("tree"), p.get("transition")) != (c.get("tree"), c.get("transition")):
                # This is normal for cut cues; only warn when both sides claim continue.
                if c.get("transition") == "continue" and p.get("transition") == "continue":
                    rep.warn(f"cue {cid}: continue parent {c['parent']} uses different tree")
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
        if not z.get("id"):
            rep.error(f"zones[{i}].id required")
        if not isinstance(z.get("layout", {}), dict):
            rep.error(f"zones[{i}].layout must be object")
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
        paths = [root / "games/splendor/tutorial/anim/v2/_schema_example.anim.json",
                 root / "games/splendor/tutorial/anim/v2/_schema_example.stage.json"]
    rc = 0
    for p in paths:
        rc |= print_report(str(p), validate_file(p))
    return rc


if __name__ == "__main__":
    sys.exit(main())
