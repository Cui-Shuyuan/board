#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""v2 animation schema loader / validator.

The implementation is split into focused modules:
  * :mod:`schema_defs`      -- constants / Report / shared selectors
  * :mod:`normalize`        -- target normalization + semantic lowering
  * :mod:`inherit`          -- parent/entry inheritance (``resolve_track``)
  * :mod:`validate_track`   -- source event/track validation

This module keeps the public CLI and re-exports the old names so existing
imports (``import anim_schema_v2 as schema``) keep working unchanged.
"""
from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from pathlib import Path

try:  # package-style import
    from .schema_defs import (
        COMPILED_STAGE_SCHEMA, COMPILED_TRACK_SCHEMA, CONCEPT_ID_RE,
        DEFAULT_DRAW_DURATION, DEFAULT_FLIP_DURATION, ENTITY_TARGET_FIELDS, FACES,
        MAGNIFIER_MASKS, MAGNIFIER_SHAPES, MIN_CAMERA_SHOT_SECONDS, OPS,
        PRESENTATION_OPS, Report, SEMANTIC_OPS, SEMANTIC_SOURCE_OPS, SHAPE_KINDS,
        SPECIAL_CONCEPT_RE, STAGE_SCHEMA, STATE_OPS, TARGET_SPACES, TRACK_SCHEMA,
        TRANSITIONS, _LOCAL_CUE_KEYS, _has_selector, _valid_stage_concept,
        load_json,
    )
    from .normalize import (
        _entity_target_zone, _lower_semantic_event, _merge_entity_target_fields,
        _normalize_event,
    )
    from .inherit import _deep_copy, _deep_merge, _merge_contract, resolve_track
    from .validate_track import (
        _check_contract, _check_event, _check_nudge, _check_object_target,
        _check_offset, _check_selector, validate_track,
    )
except ImportError:  # direct script/module import with animation/ on sys.path
    from schema_defs import (
        COMPILED_STAGE_SCHEMA, COMPILED_TRACK_SCHEMA, CONCEPT_ID_RE,
        DEFAULT_DRAW_DURATION, DEFAULT_FLIP_DURATION, ENTITY_TARGET_FIELDS, FACES,
        MAGNIFIER_MASKS, MAGNIFIER_SHAPES, MIN_CAMERA_SHOT_SECONDS, OPS,
        PRESENTATION_OPS, Report, SEMANTIC_OPS, SEMANTIC_SOURCE_OPS, SHAPE_KINDS,
        SPECIAL_CONCEPT_RE, STAGE_SCHEMA, STATE_OPS, TARGET_SPACES, TRACK_SCHEMA,
        TRANSITIONS, _LOCAL_CUE_KEYS, _has_selector, _valid_stage_concept,
        load_json,
    )
    from normalize import (
        _entity_target_zone, _lower_semantic_event, _merge_entity_target_fields,
        _normalize_event,
    )
    from inherit import _deep_copy, _deep_merge, _merge_contract, resolve_track
    from validate_track import (
        _check_contract, _check_event, _check_nudge, _check_object_target,
        _check_offset, _check_selector, validate_track,
    )

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
