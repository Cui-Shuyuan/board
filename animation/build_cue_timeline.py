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
import json
import sys
from pathlib import Path
from typing import Any

import cue_timeline


ROOT = Path(__file__).resolve().parent.parent


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
