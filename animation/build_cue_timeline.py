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
TIMING_POLICY_SCHEMA = "tutorial-cue-timing-policy/v1"
TIMING_POLICY_VERSION = 1


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


def _recompute_course_timeline(
    document: dict,
    explicit_gaps: list[float] | None = None,
) -> None:
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
        if explicit_gaps is not None:
            gap = explicit_gaps[index]
        elif previous_old_end is None:
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


def _track_content_end(plan: dict, kind: str) -> float:
    end = 0.0
    for track in plan.get("tracks") or []:
        if not isinstance(track, dict) or track.get("kind") != kind:
            continue
        for segment in track.get("segments") or []:
            if not isinstance(segment, dict):
                continue
            cue_start = float(segment.get("cue_start", 0.0))
            segment_type = segment.get("type")
            if segment_type == "silence" or segment_type == "hold":
                content_end = cue_start + float(segment["duration"])
            elif segment_type in ("audio_clip", "animation"):
                content_end = (
                    cue_start
                    + float(segment["source_end"])
                    - float(segment["source_start"])
                )
            else:
                continue
            end = max(end, content_end)
    return end


def _trim_visual_to_duration(plan: dict, duration: float) -> None:
    for track in plan.get("tracks") or []:
        if not isinstance(track, dict) or track.get("kind") != "visual":
            continue
        kept_segments = []
        for segment in track.get("segments") or []:
            if not isinstance(segment, dict):
                continue
            cue_start = float(segment.get("cue_start", 0.0))
            remain = duration - cue_start
            if remain <= 1e-9:
                continue
            if segment.get("type") == "animation":
                source_start = float(segment["source_start"])
                source_end = float(segment["source_end"])
                if source_end - source_start > remain:
                    segment["source_end"] = round(source_start + remain, 9)
            elif segment.get("type") == "hold":
                if float(segment["duration"]) > remain:
                    segment["duration"] = round(remain, 9)
            kept_segments.append(segment)
        track["segments"] = kept_segments


def _apply_timing_policy(
    document: dict,
    policy_document: dict,
    legacy_runtime_durations: dict[str, float],
) -> None:
    """Apply a per-cue duration policy.

    ``max`` keeps the Phase 1 semantics and therefore never truncates a longer
    visual animation.  ``audio`` uses the complete authored audio track
    (including explicit silence) as the cue clock and trims visual overflow,
    which is appropriate for title cues with an authored 1-second pause.
    """
    default_mode = str(policy_document.get("default_mode") or "max").strip()
    cue_modes = policy_document.get("cues") or {}
    if not isinstance(cue_modes, dict):
        raise BuildError("timing policy cues must be an object")

    valid_modes = {"max", "audio", "visual", "runtime"}
    if default_mode not in valid_modes:
        raise BuildError(
            f"timing policy default_mode {default_mode!r} is not one of "
            + ", ".join(sorted(valid_modes)))
    for cue_id, mode in cue_modes.items():
        if not isinstance(cue_id, str) or not cue_id:
            raise BuildError("timing policy cue id must be a non-empty string")
        if mode not in valid_modes:
            raise BuildError(
                f"timing policy cue {cue_id!r} has invalid mode {mode!r}")

    diagnostics = document.setdefault("diagnostics", [])
    known_ids = set()
    for plan in document.get("cues") or []:
        cue_id = str(plan.get("cue_id") or "")
        known_ids.add(cue_id)
        mode = str(cue_modes.get(cue_id, default_mode))
        old_duration = float(plan["course"]["duration"])
        audio_end = _track_content_end(plan, "audio")
        visual_end = _track_content_end(plan, "visual")

        if mode == "max":
            new_duration = max(old_duration, audio_end, visual_end)
        elif mode == "audio":
            new_duration = audio_end if audio_end > 0.0 else old_duration
        elif mode == "visual":
            if audio_end > visual_end + 1e-6:
                raise BuildError(
                    f"timing policy cue {cue_id!r} mode=visual would cut audio "
                    f"(audio_end={audio_end:g}, visual_end={visual_end:g}); "
                    "use max or audio")
            new_duration = visual_end if visual_end > 0.0 else old_duration
        else:  # runtime
            new_duration = max(
                legacy_runtime_durations.get(cue_id, old_duration),
                audio_end,
            )

        if new_duration <= 0.0:
            raise BuildError(
                f"timing policy cue {cue_id!r} produced a non-positive duration")
        if new_duration < visual_end - 1e-9:
            _trim_visual_to_duration(plan, new_duration)
        if audio_end > new_duration + 1e-6:
            raise BuildError(
                f"timing policy cue {cue_id!r} would cut audio: "
                f"audio_end={audio_end:g}, duration={new_duration:g}")

        plan["course"]["duration"] = round(new_duration, 9)
        diagnostics.append({
            "code": "cue_timing_policy",
            "severity": "info",
            "cue_id": cue_id,
            "mode": mode,
            "original_duration": round(old_duration, 9),
            "audio_end": round(audio_end, 9),
            "visual_end": round(visual_end, 9),
            "duration": round(new_duration, 9),
        })

    unknown = sorted(set(cue_modes) - known_ids)
    if unknown:
        raise BuildError(
            "timing policy references unknown cue ids: " + ", ".join(unknown))



def _extend_last_subtitle_to_cue_end(document: dict) -> None:
    """Keep the final subtitle visible for the complete cue duration."""
    for plan in document.get("cues") or []:
        duration = float(plan["course"]["duration"])
        subtitles = plan.get("subtitles") or []
        if not subtitles:
            continue
        last = None
        for subtitle in subtitles:
            if not isinstance(subtitle, dict):
                continue
            start = float(subtitle.get("cue_start", 0.0))
            end = start + float(subtitle.get("duration", 0.0))
            end = min(duration, max(start, end))
            subtitle["duration"] = round(max(0.0, end - start), 9)
            if last is None or start + float(last.get("duration", 0.0)) < end:
                last = subtitle
        if last is not None:
            start = float(last.get("cue_start", 0.0))
            if duration > start:
                last["duration"] = round(duration - start, 9)


def _legacy_explicit_gaps(runtime: dict) -> list[float]:
    """Return gaps between legacy runtime cues, excluding duration tails."""

    gaps: list[float] = []
    previous_end: float | None = None
    for index, cue in enumerate(runtime.get("cues") or []):
        if not isinstance(cue, dict):
            raise BuildError(f"runtime cue {index}: expected object")
        start = float(cue.get("start", 0.0))
        duration = float(cue.get("duration", 0.0))
        if previous_end is None:
            gaps.append(0.0)
        else:
            gaps.append(max(0.0, start - previous_end))
        previous_end = start + duration
    return gaps


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


def _load_timing_policy(repo_root: Path, game: str, track: str) -> dict:
    path = (
        repo_root / "content" / "games" / game / "tutorial"
        / f"{track}.timing-policy.json"
    )
    if not path.is_file():
        return {"default_mode": "max", "cues": {}}
    document = _load_json(path)
    if document.get("schema") != TIMING_POLICY_SCHEMA:
        raise BuildError(
            f"{path}: expected schema {TIMING_POLICY_SCHEMA!r}")
    if document.get("schema_version") != TIMING_POLICY_VERSION:
        raise BuildError(
            f"{path}: expected schema_version {TIMING_POLICY_VERSION}")
    if document.get("game_id") not in (None, game):
        raise BuildError(f"{path}: game_id mismatch")
    if document.get("track") not in (None, track):
        raise BuildError(f"{path}: track mismatch")
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
        timing_policy = _load_timing_policy(repo_root, game, track)
        _apply_audio_boundaries(document, game, track, boundaries)
        legacy_runtime_durations = {
            str(cue.get("id")): float(cue.get("duration", 0.0))
            for cue in runtime.get("cues") or []
            if isinstance(cue, dict) and cue.get("id") is not None
        }
        _apply_timing_policy(document, timing_policy, legacy_runtime_durations)
        _recompute_course_timeline(
            document, _legacy_explicit_gaps(runtime))
        _extend_last_subtitle_to_cue_end(document)
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
