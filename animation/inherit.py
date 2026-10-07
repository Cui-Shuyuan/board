#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Cue inheritance and effective-state resolution for v2 tracks."""
from __future__ import annotations

import copy

try:  # package-style import
    from .compiled_state import resolve_effective_state_source
    from .normalize import _lower_semantic_event, _normalize_event
    from .schema_defs import _LOCAL_CUE_KEYS
except ImportError:  # direct script/module import with animation/ on sys.path
    from compiled_state import resolve_effective_state_source
    from normalize import _lower_semantic_event, _normalize_event
    from schema_defs import _LOCAL_CUE_KEYS


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

        # effective state source: the shared resolver keeps this in sync with
        # the compiler and audit.  Unknown explicit entry references fall back
        # to no source here, matching the old resolve_track behavior; strict
        # compilation still rejects them.
        source = resolve_effective_state_source(
            raw,
            by_id=by_id,
            prev_id=_prev_raw_id(cid),
        )
        state_source_id = source["id"] if source["kind"] == "cue" and source["id"] in by_id else None
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
        lowered = []
        for raw_ev in (raw.get("events") or []):
            normalized = _normalize_event(_deep_copy(raw_ev))
            lowered.extend(_lower_semantic_event(normalized, raw.get("id") or ""))
        eff["events"] = lowered
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
