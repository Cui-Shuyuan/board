#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic contract, fact and timing checks for reusable tutorial materials.

The source loader rejects missing refs, cycles, bad role bindings and unknown
use fields.  This checker adds the state/time layer:

* every material call (including nested action calls) is checked on its exact
  source-event boundary using the compiler's in-memory event state timeline;
* action ``contract.preconditions``/``postconditions`` are compared with the
  call entry/exit snapshots (no "skip because a later event repaired it");
* example ``facts.zones``/``facts.objects`` are compared with the example
  setup boundary, with logical object ids mapped to distinct actual entities;
* timing policies (causal order, minimum span/duration, cue window, optional
  overlap conflicts) are checked on the same call boundary;
* the on-disk compiled product must equal a fresh compiler run, so a failed or
  stale build cannot be reported as the latest successful output.

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


STATE_OPS = ml.STATE_EVENT_OPS
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
    """Match a compiled component against a semantic selector (no id/count)."""
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


def _selector_count(value, where: str, report: list[str]) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        report.append(f"{where}: count must be a non-negative integer")
        return None
    return value


def _check_zone_contract(snapshot, zones: dict, where: str, report: list[str]) -> int:
    checks = 0
    for zone, spec in (zones or {}).items():
        if not isinstance(spec, dict):
            report.append(f"{where}: contract for zone {zone!r} must be an object")
            continue
        count = _zone_count(snapshot, str(zone))
        checks += 1
        if "count" in spec:
            expected = _selector_count(spec.get("count"), f"{where} zone {zone!r}", report)
            if expected is not None and count != expected:
                report.append(
                    f"{where}: zone {zone!r} count={count}, contract expects {expected}"
                )
        if "count_min" in spec:
            expected = _selector_count(spec.get("count_min"), f"{where} zone {zone!r}", report)
            if expected is not None and count < expected:
                report.append(
                    f"{where}: zone {zone!r} count={count}, contract expects >= {expected}"
                )
        if "count_max" in spec:
            expected = _selector_count(spec.get("count_max"), f"{where} zone {zone!r}", report)
            if expected is not None and count > expected:
                report.append(
                    f"{where}: zone {zone!r} count={count}, contract expects <= {expected}"
                )
        contains = spec.get("contains") or []
        if not isinstance(contains, list):
            report.append(f"{where}: zone {zone!r} contains must be a list")
            continue
        candidates = [x for x in _components(snapshot) if x.get("ZoneId") == zone]
        for cidx, raw_selector in enumerate(contains):
            if not isinstance(raw_selector, dict):
                report.append(f"{where}: zone {zone!r} contains[{cidx}] must be an object")
                continue
            selector = dict(raw_selector)
            selector.pop("id", None)
            selector.pop("logical_id", None)
            expected_count = _selector_count(
                selector.pop("count", None),
                f"{where} zone {zone!r} contains[{cidx}]",
                report,
            )
            checks += 1
            matched = [x for x in candidates if _item_matches(x, selector)]
            if expected_count is None:
                if not matched:
                    report.append(
                        f"{where}: zone {zone!r} missing expected item {raw_selector!r}"
                    )
            elif len(matched) != expected_count:
                report.append(
                    f"{where}: zone {zone!r} selector {raw_selector!r} matched "
                    f"{len(matched)}, expected count {expected_count}"
                )
    return checks


def _check_fact_objects(snapshot, objects: list, where: str, report: list[str]) -> int:
    """Map logical fact ids to distinct actual components.

    Each fact object without ``count`` claims exactly one unclaimed component;
    an ambiguous selector or a second logical id trying to reuse the same
    actual entity is an error.  A fact object with ``count`` validates exactly
    that many matches and claims their actual ids (logical id must then be
    omitted or represent the group).
    """
    components = _components(snapshot)
    checks = 0
    claimed_actual: set[str] = set()
    claimed_logical: dict[str, str] = {}
    for idx, raw_selector in enumerate(objects or []):
        if not isinstance(raw_selector, dict):
            report.append(f"{where}: facts.objects[{idx}] must be an object")
            continue
        selector = dict(raw_selector)
        logical_id = str(selector.pop("id", selector.pop("logical_id", "")) or "").strip()
        expected_count = _selector_count(
            selector.pop("count", None), f"{where} facts.objects[{idx}]", report
        )
        checks += 1
        matched = [x for x in components if _item_matches(x, selector)]
        if expected_count is not None:
            if logical_id and expected_count != 1:
                report.append(
                    f"{where}: facts.objects[{idx}] logical id {logical_id!r} needs count=1"
                )
            if len(matched) != expected_count:
                report.append(
                    f"{where}: facts.objects[{idx}] {raw_selector!r} matched "
                    f"{len(matched)}, expected count {expected_count}"
                )
                continue
            if expected_count == 0:
                continue
        else:
            unclaimed = [x for x in matched if str(x.get("Id")) not in claimed_actual]
            if not unclaimed:
                report.append(
                    f"{where}: facts.objects[{idx}] {raw_selector!r} has no unclaimed match"
                )
                continue
            if len(unclaimed) > 1:
                report.append(
                    f"{where}: facts.objects[{idx}] {raw_selector!r} is ambiguous "
                    f"({len(unclaimed)} unclaimed matches)"
                )
                continue
            matched = unclaimed
        actual_ids = [str(x.get("Id")) for x in matched]
        for actual_id in actual_ids:
            if actual_id in claimed_actual:
                report.append(
                    f"{where}: facts.objects[{idx}] reuses actual entity {actual_id!r}"
                )
            claimed_actual.add(actual_id)
        if logical_id:
            if logical_id in claimed_logical:
                report.append(
                    f"{where}: duplicate logical fact object id {logical_id!r}"
                )
            else:
                claimed_logical[logical_id] = actual_ids[0] if actual_ids else ""
    return checks


def _example_facts(asset: ml.MaterialAsset, variant: str | None) -> dict:
    base_facts = asset.facts if isinstance(asset.facts, dict) else {}
    variant_entry = None
    if variant:
        variant_entry = next(
            (v for v in asset.variants if str(v.get("id")) == str(variant)), None
        )
    variant_facts = (
        variant_entry.get("facts")
        if isinstance(variant_entry, dict) and isinstance(variant_entry.get("facts"), dict)
        else {}
    )
    facts = dict(base_facts)
    facts.update({k: v for k, v in variant_facts.items() if k != "objects"})
    facts["objects"] = (
        list(base_facts.get("objects") or []) + list(variant_facts.get("objects") or [])
    )
    return facts


def _check_example_facts(asset: ml.MaterialAsset, variant: str | None,
                         snapshot, where: str, report: list[str]) -> int:
    facts = _example_facts(asset, variant)
    zones = facts.get("zones") or {}
    objects = facts.get("objects") or []
    checks = 0
    if not zones and not objects:
        return 0
    if zones:
        checks += _check_zone_contract(snapshot, zones, where, report)
    if objects:
        checks += _check_fact_objects(snapshot, objects, where, report)
    return checks


def _event_state_selector(ev: dict) -> tuple:
    target = ev.get("target") if isinstance(ev.get("target"), dict) else {}
    parts = target.get("parts")
    return (
        target.get("zone") or ev.get("zone") or tuple(ev.get("source") or []) or None,
        target.get("template") or ev.get("template"),
        target.get("palette") or ev.get("palette"),
        target.get("concept") or ev.get("concept"),
        json.dumps(parts if parts is not None else ev.get("parts"), sort_keys=True,
                   ensure_ascii=False, default=str),
        target.get("order") if target.get("order") is not None else ev.get("order"),
    )


def _state_signature(snapshot: dict) -> str:
    return json.dumps(snapshot or {}, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"))


def _check_timing(usage: ml.MaterialUsage, asset: ml.MaterialAsset,
                  times: list[float], cue_duration: float | None,
                  timeline_segment: list[dict] | None,
                  report: list[str]) -> int:
    events = usage.events or []
    if len(times) != len(events):
        report.append(
            f"{usage.where}: timing source map mismatch ({len(times)} times for "
            f"{len(events)} events)"
        )
        return 0
    checks = 1
    where = usage.where
    changed: list[bool] | None = None
    if timeline_segment is not None and len(timeline_segment) == len(events) + 1:
        changed = [
            _state_signature(timeline_segment[i]) != _state_signature(timeline_segment[i + 1])
            for i in range(len(events))
        ]
    state_indices = [
        i for i, ev in enumerate(events)
        if ev.get("op") in STATE_OPS and (changed is None or changed[i])
    ]
    last_state_time = None
    for i in state_indices:
        t = float(times[i])
        if t < -1e-9:
            report.append(f"{where}: event[{i}] resolves to negative time {t}")
        if last_state_time is not None and t + 1e-9 < last_state_time:
            report.append(
                f"{where}: causal order broken at event[{i}]: time {t} is before "
                f"previous state event time {last_state_time}"
            )
        last_state_time = max(last_state_time, t) if last_state_time is not None else t
    ends = []
    for i, ev in enumerate(events):
        try:
            dur = float(ev.get("dur", 0.0) or 0.0)
        except (TypeError, ValueError):
            report.append(f"{where}: event[{i}] has non-numeric dur")
            continue
        if dur < 0:
            report.append(f"{where}: event[{i}] has negative duration {dur}")
        ends.append(float(times[i]) + max(0.0, dur))
    if ends:
        start = min(float(times[i]) for i in range(len(times)))
        end = max(ends)
        span = end - start
        checks += 1
        min_span = asset.timing.get("min_span")
        if min_span is not None and span + 1e-9 < float(min_span):
            report.append(
                f"{where}: material span {span:.3f}s is below min_span {float(min_span):.3f}s"
            )
        min_event = asset.timing.get("min_event_duration")
        if min_event is not None:
            for i, ev in enumerate(events):
                if ev.get("op") not in STATE_OPS:
                    continue
                dur = float(ev.get("dur", 0.0) or 0.0)
                if 0.0 < dur + 1e-9 < float(min_event):
                    report.append(
                        f"{where}: event[{i}] duration {dur:.3f}s is below "
                        f"min_event_duration {float(min_event):.3f}s"
                    )
        if cue_duration is not None and end > float(cue_duration) + 1e-6:
            report.append(
                f"{where}: material ends at {end:.3f}s, beyond cue duration "
                f"{float(cue_duration):.3f}s"
            )
    if asset.timing.get("conflict_check"):
        checks += 1
        for i in state_indices:
            for j in state_indices:
                if j <= i:
                    continue
                ei, ej = events[i], events[j]
                dur_i = max(0.0, float(ei.get("dur", 0.0) or 0.0))
                ti, tj = float(times[i]), float(times[j])
                overlap = (ti + dur_i) > tj + 1e-9
                if not overlap:
                    continue
                if _event_state_selector(ei) == _event_state_selector(ej):
                    report.append(
                        f"{where}: conflicting state writes on the same selector in "
                        f"events[{i}] and events[{j}] overlap in time"
                    )
    return checks


def _runtime_windows(game: str, track: str) -> dict[str, float]:
    """Return cue -> section-audio window in seconds for section-first runtime."""
    path = section_source.tutorial_dir(game) / f"{track}.runtime.json"
    if not path.is_file():
        return {}
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        return {}
    groups: dict[str, list[dict]] = {}
    for cue in doc.get("cues") or []:
        if isinstance(cue, dict) and cue.get("id"):
            groups.setdefault(str(cue.get("audio") or ""), []).append(cue)
    windows: dict[str, float] = {}
    for cues in groups.values():
        cues.sort(key=lambda c: float(c.get("audio_start", 0.0) or 0.0))
        for i, cue in enumerate(cues):
            start = float(cue.get("audio_start", 0.0) or 0.0)
            if i + 1 < len(cues):
                nxt = float(cues[i + 1].get("audio_start", 0.0) or 0.0)
                windows[str(cue.get("id"))] = max(0.0, nxt - start)
            else:
                windows[str(cue.get("id"))] = max(
                    0.0, float(cue.get("duration", 0.0) or 0.0)
                )
    return windows


def _material_end_time(usage: ml.MaterialUsage, times: list[float]) -> float:
    end = 0.0
    for i, ev in enumerate(usage.events or []):
        dur = float(ev.get("dur", 0.0) or 0.0)
        end = max(end, float(times[i]) + max(0.0, dur))
    return end


def _canonical_without_source(doc: dict) -> str:
    clean = json.loads(json.dumps(doc))
    if isinstance(clean, dict):
        clean.pop("source_sha256", None)
    return json.dumps(clean, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def check_track(game: str, track: str, *, quiet: bool = False,
                strict_timing: bool = False) -> tuple[int, list[str]]:
    tutorial = section_source.tutorial_dir(game)
    try:
        src = section_source.load_track_source(game, track)
    except Exception as exc:  # noqa: BLE001 - CLI should show source-chain errors
        return 1, [f"{game}/{track}: source load failed: {exc}"]
    if not getattr(src, "material_usages", None):
        if not quiet:
            print(f"OK   {game}/{track}: no material usages")
        return 0, []

    try:
        import compile_animation_v2 as anim_compile  # noqa: PLC0415
        compiler = anim_compile.Compiler(
            section_source.build_anim_path(game, track),
            doc=src.anim_doc,
            source_root=src.tutorial,
            source_hash=src.source_hash,
            script_doc=src.script_doc,
            record_event_states=True,
        )
        compiled = compiler.compile()
    except Exception as exc:  # noqa: BLE001
        return 1, [f"{game}/{track}: in-memory compile failed: {exc}"]

    compiled_path = section_source.compiled_path(game, track)
    if not compiled_path.is_file():
        return 1, [f"{game}/{track}: missing compiled output {compiled_path}"]
    on_disk = json.loads(compiled_path.read_text(encoding="utf-8"))
    if _canonical_without_source(on_disk) != _canonical_without_source(compiled):
        return 1, [
            f"{game}/{track}: on-disk compiled output is stale/divergent; "
            f"run compile_animation_v2.py (or compile_tutorial.py) before checking"
        ]
    if on_disk.get("source_sha256") != src.source_hash:
        return 1, [
            f"{game}/{track}: compiled output source hash is stale; "
            f"run compile_animation_v2.py (or compile_tutorial.py) before checking"
        ]

    lib = ml.load_materials(game, tutorial)
    runtime_windows = _runtime_windows(game, track)
    report: list[str] = []
    warnings: list[str] = []
    checks = 0
    compiled_by_id = {c.get("id"): c for c in compiled.get("cues") or [] if c.get("id")}
    cue_map = {c.get("id"): c for c in src.anim_doc.get("cues") or [] if c.get("id")}

    for usage in src.material_usages:
        cue = cue_map.get(usage.cue)
        comp = compiled_by_id.get(usage.cue)
        if cue is None or comp is None:
            report.append(f"{usage.where}: material usage points at unknown cue")
            continue
        asset = lib.assets.get(usage.material)
        if asset is None:
            report.append(f"{usage.where}: material no longer exists")
            continue
        if usage.mode == "isolated" and not usage.explicit_entry:
            report.append(
                f"{usage.where}: isolated example {asset.id} needs an explicit cue "
                f"entry boundary (not implicit previous-cue inheritance)"
            )
        timeline = compiler.event_state_timelines.get(usage.cue) or []
        times = compiler.event_times.get(usage.cue) or []
        entry = None
        exit = None
        if usage.event_count > 0:
            start = usage.event_start
            stop = start + usage.event_count
            if start < 0 or stop >= len(timeline) or stop > len(times):
                report.append(
                    f"{usage.where}: event boundary [{start}, {stop}) is outside "
                    f"compiler timeline (len={len(timeline)})"
                )
                continue
            entry = timeline[start]
            exit = timeline[stop]
            checks += _check_timing(
                usage, asset, times[start:stop],
                float(comp.get("duration", 0.0) or 0.0),
                timeline[start:stop + 1], report,
            )
            window = runtime_windows.get(usage.cue)
            if window is not None:
                end = _material_end_time(usage, times[start:stop])
                if end > window + 1e-6:
                    msg = (
                        f"{where}: material ends at {end:.3f}s, beyond audio "
                        f"window {window:.3f}s"
                    )
                    if cue.get("hold_after_audio"):
                        warnings.append(msg + " (hold_after_audio extends playback)")
                    elif strict_timing:
                        report.append(msg + "; add an explicit hold_after_audio or tighten timing")
                    else:
                        warnings.append(msg + "; no hold_after_audio")

        where = f"{usage.cue} {usage.source_chain()}"

        if asset.kind == "action" and usage.contract:
            checks += 1
            pre = usage.contract.get("preconditions")
            post = usage.contract.get("postconditions")
            if isinstance(pre, dict) and pre.get("zones"):
                if entry is None:
                    report.append(f"{where}: precondition cannot be checked without event boundary")
                else:
                    checks += _check_zone_contract(entry, pre.get("zones") or {},
                                                   where + " pre", report)
            if isinstance(post, dict) and post.get("zones"):
                if exit is None:
                    report.append(f"{where}: postcondition cannot be checked without event boundary")
                else:
                    checks += _check_zone_contract(exit, post.get("zones") or {},
                                                   where + " post", report)

        if asset.kind == "example" and usage.event_count > 0:
            checks += _check_example_facts(asset, usage.variant, exit, where, report)

    for warning in warnings:
        print(f"WARN {warning}", file=sys.stderr)
    if report:
        return 1, report
    if not quiet:
        kinds = {}
        for u in src.material_usages:
            kinds[u.kind] = kinds.get(u.kind, 0) + 1
        print(
            f"OK   {game}/{track}: {len(src.material_usages)} material usage(s) "
            f"{kinds}, {checks} deterministic state/timing check(s)"
        )
    return 0, []


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", action="append", dest="tracks")
    ap.add_argument("--strict-timing", action="store_true",
                    help="turn audio-window overruns into errors")
    a = ap.parse_args(argv)
    tracks = a.tracks or section_source.discover_tracks(a.game)
    rc = 0
    for track in tracks:
        code, errors = check_track(a.game, track, strict_timing=a.strict_timing)
        if code:
            rc = code
            for err in errors:
                print(f"ERR  {err}", file=sys.stderr)
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
