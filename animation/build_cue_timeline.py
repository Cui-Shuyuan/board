#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build one shared compiled cue timeline plan set.

Usage:
    python3 animation/build_cue_timeline.py --game splendor --track quick

The command reads the existing ``tutorial/{track}.runtime.json`` and
``tutorial/anim/v2/{track}.compiled.json`` files, adapts them with the Phase 1
legacy adapter, validates the complete plan set, and writes a deterministic
``tutorial/{track}.cue-timeline.json`` next to the runtime document.

The old runtime and compiled files are read-only inputs.  The output is a
single plan set so Android and Unity can consume exactly the same cue ids,
course starts and effective durations.
"""
from __future__ import annotations

import argparse
import copy
import json
import math
import sys
from pathlib import Path
from typing import Any

import cue_timeline


ROOT = Path(__file__).resolve().parent.parent
AUDIO_BOUNDARIES_SCHEMA = "tutorial-audio-boundaries/v1"
AUDIO_BOUNDARIES_VERSION = 1


class BuildError(RuntimeError):
    """Raised when the generated plan does not satisfy the Phase 4 contract."""


def _load_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise BuildError(f"missing input file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise BuildError(f"invalid JSON in {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise BuildError(f"{path}: expected a JSON object")
    return value


def _recompute_course_timeline(document: dict) -> None:
    """Recompute cue starts after audio source ranges change.

    Explicit gaps between legacy cues are preserved; cue-local segment layout
    is untouched.  This keeps the whole track on one recursive timeline.
    """
    previous_old_end: float | None = None
    cursor = 0.0
    for index, plan in enumerate(document.get("cues") or []):
        course = plan.get("course")
        if not isinstance(course, dict):
            raise BuildError(f"cue {index}: missing course")
        old_start = float(course.get("start", 0.0))
        old_duration = float(course.get("duration", 0.0))
        if previous_old_end is None:
            gap = 0.0
        else:
            gap = max(0.0, old_start - previous_old_end)

        new_duration = old_duration
        for track in plan.get("tracks") or []:
            if not isinstance(track, dict) or track.get("kind") != "audio":
                continue
            for segment in track.get("segments") or []:
                if not isinstance(segment, dict) or segment.get("type") != "audio_clip":
                    continue
                content_end = (
                    float(segment.get("cue_start", 0.0))
                    + float(segment["source_end"])
                    - float(segment["source_start"])
                )
                new_duration = max(new_duration, content_end)

        new_start = cursor + gap
        course["start"] = round(new_start, 9)
        course["duration"] = round(new_duration, 9)
        cursor = new_start + new_duration
        previous_old_end = old_start + old_duration

    document.setdefault("course", {})["start"] = 0.0
    document["course"]["duration"] = round(cursor, 9)


def _load_audio_boundaries(repo_root: Path, game: str, track: str) -> dict:
    path = (
        repo_root / "content" / "games" / game / "tutorial"
        / f"{track}.audio-boundaries.json"
    )
    if not path.is_file():
        return {}
    document = _load_json(path)
    if document.get("schema") != AUDIO_BOUNDARIES_SCHEMA:
        raise BuildError(
            f"{path}: expected schema {AUDIO_BOUNDARIES_SCHEMA!r}"
        )
    if document.get("schema_version") != AUDIO_BOUNDARIES_VERSION:
        raise BuildError(
            f"{path}: expected schema_version {AUDIO_BOUNDARIES_VERSION}"
        )
    if document.get("game_id") not in (None, game):
        raise BuildError(f"{path}: game_id mismatch")
    if document.get("track") not in (None, track):
        raise BuildError(f"{path}: track mismatch")
    boundaries = document.get("boundaries") or []
    if not isinstance(boundaries, list):
        raise BuildError(f"{path}: boundaries must be an array")
    return document


def _apply_audio_boundaries(
    document: dict,
    game: str,
    track: str,
    boundaries_document: dict,
) -> None:
    """Apply explicit, content-derived source_start corrections.

    These overrides exist for cases where legacy cue starts were derived from
    subtitle alignment and landed after the first phoneme.  They are checked
    against the audio segment and never guessed with a fixed pre-roll constant.
    """
    boundaries = boundaries_document.get("boundaries") or []
    if not boundaries:
        return

    plans_by_id = {
        str(plan.get("cue_id")): plan
        for plan in document.get("cues") or []
        if isinstance(plan, dict)
    }
    diagnostics = document.setdefault("diagnostics", [])
    for index, boundary in enumerate(boundaries):
        if not isinstance(boundary, dict):
            raise BuildError(f"audio boundary {index}: expected object")
        cue_id = str(boundary.get("cue_id") or "").strip()
        segment_id = str(boundary.get("segment_id") or "").strip()
        source_start = boundary.get("source_start")
        if not cue_id or not segment_id:
            raise BuildError(
                f"audio boundary {index}: cue_id and segment_id are required")
        if isinstance(source_start, bool) or not isinstance(
                source_start, (int, float)):
            raise BuildError(
                f"audio boundary {index}: source_start must be numeric")
        if not math.isfinite(float(source_start)) or float(source_start) < 0.0:
            raise BuildError(
                f"audio boundary {index}: source_start must be finite and >= 0")

        plan = plans_by_id.get(cue_id)
        if plan is None:
            raise BuildError(
                f"audio boundary {index}: unknown cue_id {cue_id!r}")
        target = None
        for track_plan in plan.get("tracks") or []:
            if not isinstance(track_plan, dict) or track_plan.get("kind") != "audio":
                continue
            for segment in track_plan.get("segments") or []:
                if isinstance(segment, dict) and segment.get("id") == segment_id:
                    target = segment
                    break
            if target is not None:
                break
        if target is None or target.get("type") != "audio_clip":
            raise BuildError(
                f"audio boundary {index}: unknown audio clip "
                f"{cue_id}/{segment_id}"
            )

        previous = float(target["source_start"])
        corrected = round(float(source_start), 9)
        if corrected >= previous:
            raise BuildError(
                f"audio boundary {index}: corrected source_start "
                f"{corrected:g} must be earlier than {previous:g}"
            )
        if corrected >= float(target["source_end"]):
            raise BuildError(
                f"audio boundary {index}: corrected source_start must be "
                "earlier than source_end"
            )
        target["source_start"] = corrected
        diagnostics.append({
            "code": "audio_start_onset_guard",
            "severity": "info",
            "cue_id": cue_id,
            "segment_id": segment_id,
            "original_source_start": round(previous, 9),
            "corrected_source_start": corrected,
            "reason": str(boundary.get("reason") or ""),
        })

    _recompute_course_timeline(document)


def build_plan_set(repo_root: Path, game: str, track: str) -> dict:
    """Build and validate one complete plan set without writing it."""
    game_dir = repo_root / "content" / "games" / game
    runtime_path = game_dir / "tutorial" / f"{track}.runtime.json"
    compiled_path = game_dir / "tutorial" / "anim" / "v2" / f"{track}.compiled.json"

    runtime = _load_json(runtime_path)
    compiled = _load_json(compiled_path)
    if runtime.get("game_id") not in (None, game):
        raise BuildError(
            f"runtime game_id mismatch: expected {game!r}, "
            f"got {runtime.get('game_id')!r}"
        )
    if runtime.get("track") not in (None, track):
        raise BuildError(
            f"runtime track mismatch: expected {track!r}, "
            f"got {runtime.get('track')!r}"
        )

    try:
        document = cue_timeline.adapt_legacy_runtime(runtime, compiled)
        boundaries = _load_audio_boundaries(repo_root, game, track)
        _apply_audio_boundaries(document, game, track, boundaries)
        cue_timeline.validate_plan_set(document)
    except cue_timeline.CueTimelineError as exc:
        raise BuildError(f"plan validation failed: {exc}") from exc

    # Phase 4 contract: every cue is fully represented in the same plan set.
    runtime_cues = runtime.get("cues") or []
    plan_cues = document.get("cues") or []
    if len(plan_cues) != len(runtime_cues):
        raise BuildError(
            "plan cue coverage mismatch: "
            f"runtime={len(runtime_cues)} plan={len(plan_cues)}"
        )
    for index, (runtime_cue, plan) in enumerate(zip(runtime_cues, plan_cues)):
        if not isinstance(runtime_cue, dict) or not isinstance(plan, dict):
            raise BuildError(f"cue {index}: expected objects")
        if plan.get("cue_id") != runtime_cue.get("id"):
            raise BuildError(
                f"cue {index}: plan id {plan.get('cue_id')!r} does not match "
                f"runtime id {runtime_cue.get('id')!r}"
            )
        if "entry" not in plan:
            raise BuildError(f"cue {plan.get('cue_id')!r}: missing entry")
        course = plan.get("course")
        if not isinstance(course, dict) or "start" not in course or "duration" not in course:
            raise BuildError(f"cue {plan.get('cue_id')!r}: missing course timing")
        if not plan.get("tracks"):
            raise BuildError(f"cue {plan.get('cue_id')!r}: missing tracks")

    return document


def serialize_plan_set(document: dict) -> str:
    """Return the canonical deterministic JSON representation."""
    return json.dumps(document, ensure_ascii=False, indent=2) + "\n"


def build_and_write(
    repo_root: Path,
    game: str,
    track: str,
    *,
    output: Path | None = None,
) -> tuple[Path, dict]:
    document = build_plan_set(repo_root, game, track)
    game_dir = repo_root / "content" / "games" / game
    destination = output or (
        game_dir / "tutorial" / f"{track}.cue-timeline.json"
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = serialize_plan_set(document)

    # Fail closed if the serialized document cannot be parsed and validated
    # again.  This catches non-JSON values before a release is published.
    parsed = json.loads(payload)
    try:
        cue_timeline.validate_plan_set(parsed)
    except cue_timeline.CueTimelineError as exc:
        raise BuildError(f"serialized plan validation failed: {exc}") from exc

    destination.write_text(payload, encoding="utf-8")
    return destination, document


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, help="game id, e.g. splendor")
    parser.add_argument("--track", required=True, help="tutorial track, e.g. quick")
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=ROOT,
        help=argparse.SUPPRESS,
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    game = args.game.strip()
    track = args.track.strip()
    if not game or not track:
        print("error: --game and --track must not be empty", file=sys.stderr)
        return 2

    try:
        path, document = build_and_write(
            args.repo_root.resolve(), game, track)
    except (BuildError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print(
        f"wrote {path} cues={len(document['cues'])} "
        f"duration={document['course']['duration']} track={document['track']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
