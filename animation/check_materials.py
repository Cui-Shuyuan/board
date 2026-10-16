#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic contract/fact checks for reusable tutorial materials.

The source loader already rejects missing refs, cycles, bad role bindings and
unknown use fields.  This checker adds the state-level layer:

* action ``contract.postconditions`` are compared with the compiled cue state;
* ``contract.preconditions`` are compared with the cue's entry state when the
  action is the first state-producing event in that cue;
* example ``facts.zones`` / ``facts.objects`` are compared with the compiled
  state for cues that realize the example and do no later state mutation.

Run::

    python3 animation/check_materials.py --game splendor
    python3 animation/check_materials.py --game splendor --track quick
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import material_library as ml  # noqa: E402
import section_source  # noqa: E402


STATE_OPS = ml.STATE_OPS | ml.SEMANTIC_STATE_OPS
FACE_NORMALIZE = {
    "up": 2, "face_up": 2, 2: 2,
    "down": 1, "face_down": 1, 1: 1,
    "hidden": 0, 0: 0, "": "",
}


def _norm_face(value):
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, str):
        return FACE_NORMALIZE.get(value.strip().lower(), value)
    return value


def _components(snapshot) -> list[dict]:
    if not isinstance(snapshot, dict):
        return []
    return [x for x in (snapshot.get("components") or []) if isinstance(x, dict)]


def _zone_count(snapshot, zone: str) -> int:
    return sum(1 for item in _components(snapshot) if item.get("ZoneId") == zone)


def _part_value(item: dict, key: str):
    for part in item.get("parts") or []:
        if isinstance(part, dict) and part.get("key") == key:
            return part.get("value")
    return None


def _item_matches(item: dict, selector: dict) -> bool:
    """Match a compiled component against a semantic fact selector."""
    mapping = {
        "template": "TemplateId",
        "palette": "Palette",
        "concept": "Concept",
        "zone": "ZoneId",
        "order": "Order",
        "layer": "Layer",
    }
    ignore = {
        "id", "logical_id", "kind", "type", "description", "note", "role",
    }
    for key, expected in selector.items():
        if key in ignore:
            continue
        if key == "parts":
            for part in expected or []:
                pkey = part.get("key")
                pvalue = part.get("value")
                if _part_value(item, pkey) != pvalue:
                    return False
            continue
        if key == "face":
            actual = item.get("Face")
            if _norm_face(actual) != _norm_face(expected):
                return False
            continue
        actual_key = mapping.get(key)
        if actual_key is None:
            return False
        actual = item.get(actual_key)
        if actual != expected:
            return False
    return True


def _check_zone_contract(snapshot, zones: dict, where: str, report: list[str]) -> int:
    checks = 0
    for zone, spec in (zones or {}).items():
        if not isinstance(spec, dict):
            report.append(f"{where}: contract for zone {zone!r} must be an object")
            continue
        count = _zone_count(snapshot, str(zone))
        checks += 1
        if "count" in spec and count != int(spec["count"]):
            report.append(
                f"{where}: zone {zone!r} count={count}, contract expects {int(spec['count'])}"
            )
        if "count_min" in spec and count < int(spec["count_min"]):
            report.append(
                f"{where}: zone {zone!r} count={count}, contract expects >= {int(spec['count_min'])}"
            )
        if "count_max" in spec and count > int(spec["count_max"]):
            report.append(
                f"{where}: zone {zone!r} count={count}, contract expects <= {int(spec['count_max'])}"
            )
        contains = spec.get("contains") or []
        if not isinstance(contains, list):
            report.append(f"{where}: zone {zone!r} contains must be a list")
            continue
        candidates = [x for x in _components(snapshot) if x.get("ZoneId") == zone]
        for cidx, selector in enumerate(contains):
            if not isinstance(selector, dict):
                report.append(f"{where}: zone {zone!r} contains[{cidx}] must be an object")
                continue
            checks += 1
            expected_count = selector.get("count")
            matched = [x for x in candidates if _item_matches(x, selector)]
            if expected_count is not None and len(matched) != int(expected_count):
                report.append(
                    f"{where}: zone {zone!r} selector {selector!r} matched {len(matched)}, "
                    f"expected count {int(expected_count)}"
                )
            elif not matched:
                report.append(f"{where}: zone {zone!r} missing expected item {selector!r}")
    return checks


def _check_example_facts(src, usage, asset, cue: dict, end_state, report: list[str]) -> int:
    checks = 0
    variant_entry = None
    if usage.variant:
        variant_entry = next(
            (v for v in asset.variants if str(v.get("id")) == str(usage.variant)), None
        )
    base_facts = asset.facts if isinstance(asset.facts, dict) else {}
    variant_facts = (
        variant_entry.get("facts")
        if isinstance(variant_entry, dict) and isinstance(variant_entry.get("facts"), dict)
        else {}
    )
    facts = dict(base_facts)
    facts.update({k: v for k, v in variant_facts.items() if k != "objects"})
    # Common object identities come from the example; a variant may add its own.
    objects = list(base_facts.get("objects") or []) + list(variant_facts.get("objects") or [])
    facts["objects"] = objects
    zones = facts.get("zones") or {}
    if not zones and not objects:
        return 0
    where = f"{usage.cue} example={asset.id}" + (f" variant={usage.variant}" if usage.variant else "")
    if zones:
        checks += _check_zone_contract(end_state, zones, where, report)
    if objects:
        components = _components(end_state)
        for idx, selector in enumerate(objects):
            if not isinstance(selector, dict):
                report.append(f"{where}: facts.objects[{idx}] must be an object")
                continue
            checks += 1
            if not any(_item_matches(item, selector) for item in components):
                report.append(f"{where}: missing fact object {selector!r}")
    return checks


def _event_zones(ev: dict) -> set[str]:
    zones = set()
    if not isinstance(ev, dict):
        return zones

    def add(value):
        if isinstance(value, str):
            zones.add(value)
        elif isinstance(value, list):
            zones.update(str(x) for x in value if x)
        elif isinstance(value, dict):
            z = value.get("zone")
            if z:
                zones.add(str(z))
            for z in value.get("zones") or []:
                zones.add(str(z))

    add(ev.get("zone"))
    add(ev.get("source"))
    add(ev.get("destination"))
    add(ev.get("to_zone"))
    target = ev.get("target")
    if isinstance(target, dict):
        add(target.get("zone"))
        add(target.get("zones"))
    return zones


def check_track(game: str, track: str, *, quiet: bool = False) -> tuple[int, list[str]]:
    tutorial = section_source.tutorial_dir(game)
    try:
        src = section_source.load_track_source(game, track)
    except Exception as exc:  # noqa: BLE001 - CLI should show source-chain errors
        return 1, [f"{game}/{track}: source load failed: {exc}"]
    if not getattr(src, "material_usages", None):
        if not quiet:
            print(f"OK   {game}/{track}: no material usages")
        return 0, []

    compiled_path = section_source.compiled_path(game, track)
    if not compiled_path.is_file():
        return 1, [f"{game}/{track}: missing compiled output {compiled_path}; run compile_animation_v2"]
    compiled = json.loads(compiled_path.read_text(encoding="utf-8"))
    if compiled.get("source_sha256") != src.source_hash:
        return 1, [
            f"{game}/{track}: compiled output is stale relative to source; "
            f"run compile_animation_v2.py (or compile_tutorial.py) first"
        ]
    compiled_by_id = {c.get("id"): c for c in compiled.get("cues") or [] if c.get("id")}
    lib = ml.load_materials(game, tutorial)
    report: list[str] = []
    notes: list[str] = []
    checks = 0

    # Source-order cue map gives us the expanded event stream for preposition checks.
    cue_map = {c.get("id"): c for c in src.anim_doc.get("cues") or [] if c.get("id")}
    for usage in src.material_usages:
        cue = cue_map.get(usage.cue)
        comp = compiled_by_id.get(usage.cue)
        if cue is None or comp is None:
            report.append(f"{usage.cue}: material usage points at unknown cue")
            continue
        asset = lib.assets.get(usage.material)
        if asset is None:
            report.append(f"{usage.where}: material no longer exists")
            continue
        if usage.mode == "isolated" and not (cue.get("entry") or cue.get("parent")):
            report.append(
                f"{usage.where}: isolated example {asset.id} needs an explicit entry/parent "
                f"state boundary"
            )

        # ── action contracts ───────────────────────────────────────────
        if asset.kind == "action" and usage.contract:
            cwhere = f"{usage.cue} action={asset.id}"
            post = usage.contract.get("postconditions")
            if isinstance(post, dict):
                checks += _check_zone_contract(comp.get("end_state"), post.get("zones") or {},
                                               cwhere + " post", report)
            pre = usage.contract.get("preconditions")
            if isinstance(pre, dict) and pre.get("zones"):
                events = cue.get("events") or []
                first = usage.events[0] if usage.events else None
                idx = next((i for i, ev in enumerate(events) if ev == first), None)
                before = events[:idx] if idx is not None else []
                touched = set()
                for ev in before:
                    if ev.get("op") in STATE_OPS:
                        touched.update(_event_zones(ev))
                expected_zones = {str(z) for z in (pre.get("zones") or {})}
                skipped = sorted(expected_zones & touched)
                checkable = {z: spec for z, spec in (pre.get("zones") or {}).items()
                             if str(z) not in touched}
                if skipped:
                    notes.append(
                        f"{cwhere} precondition partially skipped for mutated zone(s): "
                        + ", ".join(skipped)
                    )
                if checkable:
                    checks += _check_zone_contract(comp.get("start_state"), checkable,
                                                   cwhere + " pre", report)

        # ── example facts ──────────────────────────────────────────────
        if asset.kind == "example" and usage.use_index >= 0:
            events = cue.get("events") or []
            first = usage.events[0] if usage.events else None
            # Find the last event of this use in the expanded cue.  We compare
            # facts at cue end only when no later state op can mutate them.
            idx = next((i for i, ev in enumerate(events) if ev == first), None)
            after = events[(idx or 0) + len(usage.events):] if idx is not None else []
            if any((ev.get("op") in STATE_OPS) for ev in after):
                notes.append(
                    f"{usage.cue} example={asset.id}: facts check skipped because the cue "
                    f"continues with state changes"
                )
            else:
                checks += _check_example_facts(src, usage, asset, cue, comp.get("end_state"), report)

    for note in notes:
        print(f"NOTE {note}", file=sys.stderr)
    if report:
        return 1, report
    if not quiet:
        kinds = {}
        for u in src.material_usages:
            kinds[u.kind] = kinds.get(u.kind, 0) + 1
        print(
            f"OK   {game}/{track}: {len(src.material_usages)} material usage(s) "
            f"{kinds}, {checks} deterministic state check(s)"
        )
    return 0, []


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", action="append", dest="tracks")
    a = ap.parse_args(argv)
    tracks = a.tracks or section_source.discover_tracks(a.game)
    rc = 0
    for track in tracks:
        code, errors = check_track(a.game, track)
        if code:
            rc = code
            for err in errors:
                print(f"ERR  {err}", file=sys.stderr)
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
