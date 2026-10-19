#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build one shared compiled cue timeline plan set.

Usage:
    python3 animation/build_cue_timeline.py --game <game> --track <track>

For a track configured with ``audio_source=audio_slice_manifest`` the input
pipeline is:

    {track}.audio-slices.json
        -> timing point refs / logical audio slices / subtitle refs
        -> cue timing policy stored in the same fixed timeline file
        -> compiled cue plan set

The fixed timeline file is the sole source of timing-point seconds, audio
source ranges, cue-to-slice assignments, per-cue timing policy and optional
visual source ranges.  The runtime and compiled animation documents contribute
cue order, navigation metadata and the animation ids referenced by the fixed
timeline; they do not define audio timing.

The output is one deterministic ``tutorial/{track}.cue-timeline.json`` plan set
consumed by both Android and Unity.  The input files are never modified.
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
TIMING_POLICY_SCHEMA = "tutorial-cue-timing-policy/v1"
TIMING_POLICY_VERSION = 1
AUDIO_SLICES_SCHEMA = "tutorial-audio-slices/v1"
AUDIO_SLICES_VERSION = 4


class BuildError(RuntimeError):
    """Raised when the generated plan does not satisfy the Phase 4 contract."""


class _DuplicateJsonKey(ValueError):
    """Raised by the JSON decoder when an object repeats a key."""


def _reject_duplicate_json_keys(pairs: list[tuple[str, Any]]) -> dict:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _DuplicateJsonKey(f"duplicate JSON object key {key!r}")
        result[key] = value
    return result


def _load_json(path: Path) -> dict:
    try:
        value = json.loads(
            path.read_text(encoding="utf-8"),
            object_pairs_hook=_reject_duplicate_json_keys,
        )
    except FileNotFoundError as exc:
        raise BuildError(f"missing input file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise BuildError(f"invalid JSON in {path}: {exc}") from exc
    except _DuplicateJsonKey as exc:
        raise BuildError(f"invalid JSON in {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise BuildError(f"{path}: expected a JSON object")
    return value


def _recompute_course_timeline(
    document: dict,
    explicit_gaps: list[float] | None = None,
) -> None:
    """Recompute cue starts after all track durations are final.

    Cue-local segment layout is untouched; the whole track stays on one
    contiguous cue-local timeline.
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
            if segment_type == "state":
                content_end = cue_start
            elif segment_type == "silence" or segment_type == "hold":
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
            elif segment.get("type") in ("hold", "state"):
                if float(segment.get("duration", 0.0)) > remain:
                    segment["duration"] = round(remain, 9)
            kept_segments.append(segment)
        track["segments"] = kept_segments


VALID_TIMING_MODES = frozenset({"max", "audio", "visual"})
TIMING_OPTION_KEYS = frozenset({
    "mode",
    "trim_to_speech_end",
    "trailing_silence_seconds",
    "post_speech_silence_seconds",
})
TIMING_POLICY_GLOBAL_KEYS = frozenset({
    "schema",
    "schema_version",
    "game_id",
    "track",
    "audio_source",
    "audio_slice_manifest",
    "script_source",
    "default_mode",
})
VISUAL_SEGMENT_KEYS = frozenset({
    "id",
    "type",
    "animation",
    "cue_start_ref",
    "source_start_ref",
    "source_end_ref",
    "after_end",
})
VISUAL_SEGMENT_TYPES = frozenset({"animation", "state"})
VISUAL_AFTER_END_POLICIES = frozenset({"hold_last_frame", "clear"})


def _validate_timing_policy_scope(policy_document: dict, path: Path) -> None:
    """Reject per-cue policy data in the global timing-policy document.

    The fixed ``{track}.audio-slices.json`` file owns all cue ids, timing points,
    audio groups and per-cue timing policy.  The separate timing-policy file may
    only carry global pipeline configuration and ``default_mode``.
    """
    forbidden = sorted(set(policy_document) - TIMING_POLICY_GLOBAL_KEYS)
    if forbidden:
        raise BuildError(
            f"{path}: per-cue or unknown timing policy fields are forbidden; "
            "move them to the fixed audio-slices timeline: "
            + ", ".join(forbidden)
        )



def _normalize_cue_timing_options(
    timeline_document: dict,
    default_mode: str,
) -> dict[str, dict]:
    """Parse the fixed timeline's per-cue timing policy.

    No cue ids are special-cased here: any cue may opt into a policy by adding
    an entry to ``cue_timing`` in the fixed timeline file.
    """
    raw_options = timeline_document.get("cue_timing", {})
    if not isinstance(raw_options, dict):
        raise BuildError("audio slices cue_timing must be an object")
    if default_mode not in VALID_TIMING_MODES:
        raise BuildError(
            f"timing policy default_mode {default_mode!r} is not one of "
            + ", ".join(sorted(VALID_TIMING_MODES)))

    normalized: dict[str, dict] = {}
    for cue_id, raw_option in raw_options.items():
        if not isinstance(cue_id, str) or not cue_id.strip():
            raise BuildError("audio slices cue_timing cue id must be a non-empty string")
        cue_id = cue_id.strip()
        if cue_id in normalized:
            raise BuildError(f"audio slices cue_timing duplicate cue id {cue_id!r}")
        if isinstance(raw_option, str):
            option: dict = {"mode": raw_option}
        elif isinstance(raw_option, dict):
            option = raw_option
        else:
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} must be an object or mode string")

        unknown = sorted(set(option) - TIMING_OPTION_KEYS)
        if unknown:
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} has unknown fields: "
                + ", ".join(unknown))

        mode = str(option.get("mode") or default_mode).strip()
        if mode not in VALID_TIMING_MODES:
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} has invalid mode {mode!r}")

        trim = option.get("trim_to_speech_end", False)
        if not isinstance(trim, bool):
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} "
                "trim_to_speech_end must be boolean")

        trailing = option.get("trailing_silence_seconds", 0.0)
        if isinstance(trailing, bool) or not isinstance(trailing, (int, float)):
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} "
                "trailing_silence_seconds must be numeric")
        trailing = float(trailing)
        if not math.isfinite(trailing) or trailing < 0.0:
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} "
                "trailing_silence_seconds must be finite and >= 0")

        post_speech = option.get("post_speech_silence_seconds")
        if post_speech is not None:
            if isinstance(post_speech, bool) or not isinstance(
                    post_speech, (int, float)):
                raise BuildError(
                    f"audio slices cue_timing {cue_id!r} "
                    "post_speech_silence_seconds must be numeric")
            post_speech = float(post_speech)
            if not math.isfinite(post_speech) or post_speech < 0.0:
                raise BuildError(
                    f"audio slices cue_timing {cue_id!r} "
                    "post_speech_silence_seconds must be finite and >= 0")

        normalized[cue_id] = {
            "mode": mode,
            "trim_to_speech_end": trim,
            "trailing_silence_seconds": trailing,
            "post_speech_silence_seconds": post_speech,
        }
    return normalized


def _apply_timing_policy(
    document: dict,
    cue_timing_options: dict[str, dict],
    default_mode: str,
) -> None:
    """Apply per-cue duration policy supplied by the fixed timeline file.

    ``max`` keeps the Phase 1 semantics and therefore never truncates a longer
    visual animation.  ``audio`` uses the complete authored audio track
    (including explicit silence) as the cue clock and trims visual overflow.
    """
    if default_mode not in VALID_TIMING_MODES:
        raise BuildError(
            f"timing policy default_mode {default_mode!r} is not one of "
            + ", ".join(sorted(VALID_TIMING_MODES)))
    for cue_id, option in cue_timing_options.items():
        if option.get("mode") not in VALID_TIMING_MODES:
            raise BuildError(
                f"audio slices cue_timing {cue_id!r} has invalid mode "
                f"{option.get('mode')!r}")

    diagnostics = document.setdefault("diagnostics", [])
    known_ids = set()
    for plan in document.get("cues") or []:
        cue_id = str(plan.get("cue_id") or "")
        known_ids.add(cue_id)
        option = cue_timing_options.get(cue_id, {})
        mode = str(option.get("mode") or default_mode)
        old_duration = float(plan["course"]["duration"])
        audio_end = _track_content_end(plan, "audio")
        visual_end = _track_content_end(plan, "visual")

        if mode == "max":
            new_duration = max(old_duration, audio_end, visual_end)
        elif mode == "audio":
            if audio_end <= 0.0:
                raise BuildError(
                    f"cue {cue_id!r} mode=audio requires a non-empty audio track")
            new_duration = audio_end
        elif mode == "visual":
            if audio_end > visual_end + 1e-6:
                raise BuildError(
                    f"timing policy cue {cue_id!r} mode=visual would cut audio "
                    f"(audio_end={audio_end:g}, visual_end={visual_end:g}); "
                    "use max or audio")
            if visual_end <= 0.0:
                raise BuildError(
                    f"cue {cue_id!r} mode=visual requires a non-empty visual track")
            new_duration = visual_end
        if new_duration <= 0.0:
            raise BuildError(
                f"timing policy cue {cue_id!r} produced a non-positive duration")
        if new_duration < visual_end - 1e-9:
            _trim_visual_to_duration(plan, new_duration)
        for track_plan in plan.get("tracks") or []:
            if not isinstance(track_plan, dict) or track_plan.get("kind") != "visual":
                continue
            for segment in track_plan.get("segments") or []:
                if isinstance(segment, dict) and segment.get("type") == "state":
                    remaining = new_duration - float(segment.get("cue_start", 0.0))
                    if remaining <= 1e-9:
                        raise BuildError(
                            f"cue {cue_id!r} state visual segment starts at "
                            f"or after cue end")
                    segment["duration"] = round(remaining, 9)
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
            "trim_to_speech_end": bool(option.get("trim_to_speech_end", False)),
            "trailing_silence_seconds": float(
                option.get("trailing_silence_seconds", 0.0)),
            "original_duration": round(old_duration, 9),
            "audio_end": round(audio_end, 9),
            "visual_end": round(visual_end, 9),
            "duration": round(new_duration, 9),
        })

    unknown = sorted(set(cue_timing_options) - known_ids)
    if unknown:
        raise BuildError(
            "audio slices cue_timing references unknown cue ids: "
            + ", ".join(unknown))


def _load_timing_policy(repo_root: Path, game: str, track: str) -> dict:
    path = (
        repo_root / "content" / "games" / game / "tutorial"
        / f"{track}.timing-policy.json"
    )
    if not path.is_file():
        raise BuildError(f"missing timing policy: {path}")
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
    _validate_timing_policy_scope(document, path)
    return document


def _normalize_game_relative_path(raw: str, game_dir: Path, game: str) -> str:
    text = str(raw or "").strip().replace("\\", "/")
    if not text:
        raise BuildError("empty asset path in TTS manifest")
    path = Path(text)
    if path.is_absolute():
        try:
            path = path.resolve().relative_to(game_dir.resolve())
        except ValueError as exc:
            raise BuildError(f"asset path is outside game dir: {raw}") from exc
        return path.as_posix()
    prefix = f"content/games/{game}/"
    if text.startswith(prefix):
        return text[len(prefix):]
    candidate = game_dir / text
    if candidate.is_file():
        return text
    raise BuildError(f"asset file not found: {raw}")


def _load_audio_slices(repo_root: Path, game: str, track: str, rel_path: str) -> dict:
    path = repo_root / "content" / "games" / game / rel_path
    document = _load_json(path)
    if document.get("schema") != AUDIO_SLICES_SCHEMA:
        raise BuildError(f"{path}: expected schema {AUDIO_SLICES_SCHEMA!r}")
    if document.get("schema_version") != AUDIO_SLICES_VERSION:
        raise BuildError(f"{path}: expected schema_version {AUDIO_SLICES_VERSION}")
    if document.get("game_id") not in (None, game):
        raise BuildError(f"{path}: game_id mismatch")
    if document.get("track") not in (None, track):
        raise BuildError(f"{path}: track mismatch")

    sample_rate = document.get("sample_rate")
    if isinstance(sample_rate, bool) or not isinstance(sample_rate, int):
        raise BuildError(f"{path}: sample_rate must be a positive integer")
    if sample_rate <= 0:
        raise BuildError(f"{path}: sample_rate must be a positive integer")

    timing_points = document.get("timing_points")
    if not isinstance(timing_points, dict) or not timing_points:
        raise BuildError(f"{path}: timing_points must be a non-empty object")
    for point_id, point in timing_points.items():
        if not isinstance(point_id, str) or not point_id.strip():
            raise BuildError(f"{path}: timing point id must be a non-empty string")
        if not isinstance(point, dict):
            raise BuildError(f"{path}: timing point {point_id!r} must be an object")
        value = point.get("time")
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise BuildError(
                f"{path}: timing point {point_id!r} time must be numeric")
        if not math.isfinite(float(value)) or float(value) < 0.0:
            raise BuildError(
                f"{path}: timing point {point_id!r} time must be finite and >= 0")

    cues = document.get("cues")
    if not isinstance(cues, dict) or not cues:
        raise BuildError(f"{path}: cues must be a non-empty object")
    slice_owners: dict[str, str] = {}
    cue_slice_ids: dict[str, list[str]] = {}
    for cue_id, raw_slices in cues.items():
        if not isinstance(cue_id, str) or not cue_id.strip():
            raise BuildError(f"{path}: cue id must be a non-empty string")
        cue_id = cue_id.strip()
        if not isinstance(raw_slices, list) or not raw_slices:
            raise BuildError(f"{path}: cue {cue_id!r} must map to a non-empty slice array")
        owned_slice_ids: list[str] = []
        for slice_index, raw_slice in enumerate(raw_slices):
            if not isinstance(raw_slice, dict):
                raise BuildError(
                    f"{path}: cue {cue_id!r} slice[{slice_index}] must be an object")
            slice_id = str(raw_slice.get("id") or "").strip()
            if not slice_id:
                raise BuildError(
                    f"{path}: cue {cue_id!r} slice[{slice_index}] id is required")
            if slice_id in slice_owners:
                raise BuildError(
                    f"{path}: duplicate audio slice id {slice_id!r} "
                    f"(cues {slice_owners[slice_id]!r} and {cue_id!r})")
            slice_owners[slice_id] = cue_id
            owned_slice_ids.append(slice_id)

            allowed_slice_keys = {
                "id", "asset", "start_ref", "end_ref",
                "speech_end_ref", "subtitles",
            }
            unknown = sorted(set(raw_slice) - allowed_slice_keys)
            if unknown:
                raise BuildError(
                    f"{path}: audio slice {slice_id!r} has unknown fields: "
                    + ", ".join(unknown)
                    + "; all source times must be timing-point refs")
            if "start" in raw_slice or "end" in raw_slice or "speech_end" in raw_slice:
                raise BuildError(
                    f"{path}: audio slice {slice_id!r} must use timing-point refs, "
                    "not inline seconds")
            asset = raw_slice.get("asset")
            if not isinstance(asset, str) or not asset.strip():
                raise BuildError(f"{path}: audio slice {slice_id!r} asset is required")
            for field in ("start", "end"):
                ref = raw_slice.get(field + "_ref")
                if not isinstance(ref, str) or not ref.strip():
                    raise BuildError(
                        f"{path}: audio slice {slice_id!r} {field}_ref is required")
                _resolve_timing_ref(timing_points, ref, slice_id, field + "_ref")
            speech_ref = raw_slice.get("speech_end_ref")
            if speech_ref is not None:
                if not isinstance(speech_ref, str) or not speech_ref.strip():
                    raise BuildError(
                        f"{path}: audio slice {slice_id!r} speech_end_ref must "
                        "be a non-empty string")
                _resolve_timing_ref(timing_points, speech_ref, slice_id, "speech_end_ref")

            subtitles = raw_slice.get("subtitles", [])
            if not isinstance(subtitles, list):
                raise BuildError(
                    f"{path}: audio slice {slice_id!r} subtitles must be an array")
            for subtitle_index, raw_subtitle in enumerate(subtitles):
                if not isinstance(raw_subtitle, dict):
                    raise BuildError(
                        f"{path}: audio slice {slice_id!r} "
                        f"subtitle[{subtitle_index}] must be an object")
                unknown_subtitle = sorted(
                    set(raw_subtitle) - {"start_ref", "end_ref", "text"})
                if unknown_subtitle:
                    raise BuildError(
                        f"{path}: audio slice {slice_id!r} subtitle"
                        f"[{subtitle_index}] has unknown fields: "
                        + ", ".join(unknown_subtitle))
                if not isinstance(raw_subtitle.get("text"), str):
                    raise BuildError(
                        f"{path}: audio slice {slice_id!r} subtitle"
                        f"[{subtitle_index}] text must be a string")
                for field in ("start", "end"):
                    ref = raw_subtitle.get(field + "_ref")
                    if not isinstance(ref, str) or not ref.strip():
                        raise BuildError(
                            f"{path}: audio slice {slice_id!r} subtitle"
                            f"[{subtitle_index}] {field}_ref is required")
                    _resolve_timing_ref(
                        timing_points, ref, slice_id,
                        f"subtitle[{subtitle_index}].{field}_ref")
        cue_slice_ids[cue_id] = owned_slice_ids

    cue_timing = document.get("cue_timing", {})
    if not isinstance(cue_timing, dict):
        raise BuildError(f"{path}: cue_timing must be an object")
    unknown_timing_cues = sorted(set(cue_timing) - set(cues))
    if unknown_timing_cues:
        raise BuildError(
            f"{path}: cue_timing references unknown cue ids: "
            + ", ".join(unknown_timing_cues))

    # Optional per-cue visual animation mapping.  When present this is the
    # sole source for animation reference IDs, cue-local starts, source ranges
    # and after_end policies.  A cue may reference multiple animation segments;
    # the runtime executor resolves them by cue-local time without owning an
    # independent clock.  Omitting the map uses the single compiled-cue
    # default for tracks that have not authored multi-segment visuals yet.
    cue_visuals = document.get("cue_visuals", {})
    if not isinstance(cue_visuals, dict):
        raise BuildError(f"{path}: cue_visuals must be an object")
    unknown_visual_cues = sorted(set(cue_visuals) - set(cues))
    if unknown_visual_cues:
        raise BuildError(
            f"{path}: cue_visuals references unknown cue ids: "
            + ", ".join(unknown_visual_cues))
    visual_segment_ids: set[str] = set()
    for cue_id, raw_visuals in cue_visuals.items():
        if not isinstance(raw_visuals, list) or not raw_visuals:
            raise BuildError(
                f"{path}: cue_visuals {cue_id!r} must be a non-empty array")
        previous_cue_start = -1.0
        for visual_index, raw_visual in enumerate(raw_visuals):
            visual_path = f"{path}: cue_visuals {cue_id!r}[{visual_index}]"
            if not isinstance(raw_visual, dict):
                raise BuildError(f"{visual_path} must be an object")
            unknown_visual = sorted(set(raw_visual) - VISUAL_SEGMENT_KEYS)
            if unknown_visual:
                raise BuildError(
                    f"{visual_path} has unknown fields: "
                    + ", ".join(unknown_visual))
            segment_id = str(raw_visual.get("id") or "").strip()
            if not segment_id:
                raise BuildError(f"{visual_path}.id is required")
            if segment_id in visual_segment_ids:
                raise BuildError(
                    f"{path}: duplicate visual segment id {segment_id!r}")
            visual_segment_ids.add(segment_id)
            animation = raw_visual.get("animation")
            if not isinstance(animation, str) or not animation.strip():
                raise BuildError(f"{visual_path}.animation is required")
            visual_type = str(raw_visual.get("type") or "animation").strip()
            if visual_type not in VISUAL_SEGMENT_TYPES:
                raise BuildError(
                    f"{visual_path}.type must be one of "
                    + ", ".join(sorted(VISUAL_SEGMENT_TYPES)))
            after_end = raw_visual.get("after_end")
            if after_end not in VISUAL_AFTER_END_POLICIES:
                raise BuildError(
                    f"{visual_path}.after_end must be one of "
                    + ", ".join(sorted(VISUAL_AFTER_END_POLICIES)))
            cue_start = _resolve_timing_ref(
                timing_points,
                _required_visual_ref(raw_visual, "cue_start", visual_path),
                segment_id, "cue_start_ref")
            if visual_type == "animation":
                source_start = _resolve_timing_ref(
                    timing_points,
                    _required_visual_ref(
                        raw_visual, "source_start", visual_path),
                    segment_id, "source_start_ref")
                source_end = _resolve_timing_ref(
                    timing_points,
                    _required_visual_ref(raw_visual, "source_end", visual_path),
                    segment_id, "source_end_ref")
                if source_end <= source_start:
                    raise BuildError(
                        f"{visual_path}: source range must be positive")
            if cue_start < previous_cue_start:
                raise BuildError(
                    f"{visual_path}.cue_start_ref: visual segments must be "
                    "ordered by cue-local start")
            previous_cue_start = cue_start

    audio_groups = document.get("audio_groups", {})
    if not isinstance(audio_groups, dict):
        raise BuildError(f"{path}: audio_groups must be an object")
    grouped_cues: set[str] = set()
    grouped_slices: set[str] = set()
    for group_id, raw_group in audio_groups.items():
        if not isinstance(group_id, str) or not group_id.strip():
            raise BuildError(f"{path}: audio group id must be a non-empty string")
        if not isinstance(raw_group, dict):
            raise BuildError(f"{path}: audio group {group_id!r} must be an object")
        unknown_group_keys = sorted(set(raw_group) - {"cue_ids", "slice_ids"})
        if unknown_group_keys:
            raise BuildError(
                f"{path}: audio group {group_id!r} has unknown fields: "
                + ", ".join(unknown_group_keys))
        cue_ids = raw_group.get("cue_ids")
        slice_ids = raw_group.get("slice_ids")
        if not isinstance(cue_ids, list) or not cue_ids:
            raise BuildError(f"{path}: audio group {group_id!r} cue_ids is required")
        if not isinstance(slice_ids, list) or not slice_ids:
            raise BuildError(f"{path}: audio group {group_id!r} slice_ids is required")
        if len(cue_ids) != len(slice_ids):
            raise BuildError(
                f"{path}: audio group {group_id!r} cue_ids and slice_ids "
                "must have the same length")
        assets: set[str] = set()
        for index, (cue_id, slice_id) in enumerate(zip(cue_ids, slice_ids)):
            if not isinstance(cue_id, str) or not cue_id.strip():
                raise BuildError(
                    f"{path}: audio group {group_id!r} cue_ids[{index}] is invalid")
            if not isinstance(slice_id, str) or not slice_id.strip():
                raise BuildError(
                    f"{path}: audio group {group_id!r} slice_ids[{index}] is invalid")
            cue_id = cue_id.strip()
            slice_id = slice_id.strip()
            if cue_id not in cue_slice_ids:
                raise BuildError(
                    f"{path}: audio group {group_id!r} references unknown cue {cue_id!r}")
            if slice_id not in cue_slice_ids[cue_id]:
                raise BuildError(
                    f"{path}: audio group {group_id!r} slice {slice_id!r} "
                    f"does not belong to cue {cue_id!r}")
            if cue_id in grouped_cues:
                raise BuildError(
                    f"{path}: cue {cue_id!r} belongs to multiple audio groups")
            if slice_id in grouped_slices:
                raise BuildError(
                    f"{path}: slice {slice_id!r} belongs to multiple audio groups")
            grouped_cues.add(cue_id)
            grouped_slices.add(slice_id)
            raw_slice = cues[cue_id][cue_slice_ids[cue_id].index(slice_id)]
            assets.add(str(raw_slice.get("asset") or ""))
        if len(assets) != 1:
            raise BuildError(
                f"{path}: audio group {group_id!r} must reference one shared asset")
    return document


def _resolve_timing_ref(
    timing_points: dict,
    ref: str,
    owner: str,
    field: str,
) -> float:
    point = timing_points.get(ref)
    if not isinstance(point, dict):
        raise BuildError(
            f"audio slice {owner!r} {field} references missing timing point {ref!r}")
    value = point.get("time")
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BuildError(
            f"audio slice {owner!r} {field} timing point {ref!r} "
            "must contain a numeric time")
    seconds = float(value)
    if not math.isfinite(seconds) or seconds < 0.0:
        raise BuildError(
            f"audio slice {owner!r} {field} timing point {ref!r} "
            "must be finite and >= 0")
    return seconds


def _required_slice_ref(raw_slice: dict, field: str, owner: str) -> str:
    key = field + "_ref"
    ref = raw_slice.get(key)
    if not isinstance(ref, str) or not ref.strip():
        raise BuildError(f"audio slice {owner!r} requires {key}")
    return ref.strip()


def _required_visual_ref(raw_visual: dict, field: str, owner: str) -> str:
    key = field + "_ref"
    ref = raw_visual.get(key)
    if not isinstance(ref, str) or not ref.strip():
        raise BuildError(f"visual segment {owner!r} requires {key}")
    return ref.strip()


def _build_cue_plan_set_from_audio_slices(
    repo_root: Path,
    game: str,
    track: str,
    runtime: dict,
    compiled: dict,
    policy_document: dict,
) -> dict:
    """Build plans only from refs in the fixed logical audio-slice timeline.

    The fixed file owns timing-point values, cue-to-slice assignments,
    subtitle refs, audio groups and per-cue timing policy.  This function has no
    game-, track- or cue-specific branches.  Missing/duplicate/illegal refs fail
    closed; there are no silent timing fallbacks.
    """
    game_dir = repo_root / "content" / "games" / game
    slice_rel = str(policy_document.get("audio_slice_manifest") or "").strip()
    script_rel = str(policy_document.get("script_source") or "").strip()
    if not slice_rel or not script_rel:
        raise BuildError(
            "audio_slice_manifest timing policy requires "
            "audio_slice_manifest and script_source")
    slice_doc = _load_audio_slices(repo_root, game, track, slice_rel)
    timing_points = slice_doc["timing_points"]
    script = _load_json(game_dir / script_rel)
    sample_rate = int(slice_doc.get("sample_rate") or runtime.get("sample_rate") or 24000)
    default_mode = str(policy_document.get("default_mode") or "max").strip()
    cue_timing_options = _normalize_cue_timing_options(slice_doc, default_mode)
    cue_visuals = slice_doc.get("cue_visuals", {})

    compiled_by_id = {
        str(cue.get("id")): cue
        for cue in compiled.get("cues") or []
        if isinstance(cue, dict) and cue.get("id") is not None
    }
    runtime_by_id = {
        str(cue.get("id")): cue
        for cue in runtime.get("cues") or []
        if isinstance(cue, dict) and cue.get("id") is not None
    }
    script_by_id = {
        str(cue.get("id")): cue
        for cue in script.get("cues") or []
        if isinstance(cue, dict) and cue.get("id") is not None
    }
    runtime_ids = [
        str(cue.get("id") or "").strip()
        for cue in runtime.get("cues") or []
        if isinstance(cue, dict)
    ]
    if any(not cue_id for cue_id in runtime_ids):
        raise BuildError("runtime cue id is empty")
    if len(runtime_ids) != len(set(runtime_ids)):
        raise BuildError("runtime contains duplicate cue ids")
    script_ids = [
        str(cue.get("id") or "").strip()
        for cue in script.get("cues") or []
        if isinstance(cue, dict)
    ]
    if any(not cue_id for cue_id in script_ids):
        raise BuildError("script cue id is empty")
    if len(script_ids) != len(set(script_ids)):
        raise BuildError("script contains duplicate cue ids")
    fixed_cue_ids = list(slice_doc["cues"])
    if set(runtime_ids) != set(fixed_cue_ids):
        missing = sorted(set(runtime_ids) - set(fixed_cue_ids))
        extra = sorted(set(fixed_cue_ids) - set(runtime_ids))
        raise BuildError(
            "fixed audio-slices cue coverage mismatch: "
            f"missing={missing!r} extra={extra!r}")
    if set(runtime_ids) != set(script_ids):
        missing = sorted(set(runtime_ids) - set(script_ids))
        extra = sorted(set(script_ids) - set(runtime_ids))
        raise BuildError(
            "script/runtime cue coverage mismatch: "
            f"missing={missing!r} extra={extra!r}")

    plans: list[dict] = []
    diagnostics: list[dict] = [{
        "code": "audio_slice_source",
        "severity": "info",
        "message": "cue audio and timing resolve from the fixed audio-slices timeline",
    }]
    for cue_id in runtime_ids:
        runtime_cue = runtime_by_id[cue_id]
        script_cue = script_by_id[cue_id]
        cue_text = "".join(
            str(beat.get("text") or "")
            for beat in (script_cue.get("beats") or [])
            if isinstance(beat, dict)
        )
        raw_slices = slice_doc["cues"][cue_id]
        option = cue_timing_options.get(cue_id, {})
        trim_to_speech_end = bool(option.get("trim_to_speech_end", False))

        audio_segments: list[dict] = []
        subtitle_segments: list[dict] = []
        cursor = 0.0
        last_speech_end = 0.0
        for slice_index, raw_slice in enumerate(raw_slices):
            owner = str(raw_slice.get("id") or "")
            asset = _normalize_game_relative_path(
                str(raw_slice.get("asset") or ""), game_dir, game)
            asset_path = game_dir / asset
            if not asset_path.is_file():
                raise BuildError(
                    f"audio slice {owner!r} references missing audio file: {asset}")

            start = _resolve_timing_ref(
                timing_points,
                _required_slice_ref(raw_slice, "start", owner),
                owner, "start_ref")
            end = _resolve_timing_ref(
                timing_points,
                _required_slice_ref(raw_slice, "end", owner),
                owner, "end_ref")
            if not math.isfinite(start) or not math.isfinite(end) or end <= start:
                raise BuildError(
                    f"audio slice {owner!r} has invalid source range "
                    f"[{start:g}, {end:g})")

            speech_end: float | None = None
            has_speech_ref = raw_slice.get("speech_end_ref") is not None
            if trim_to_speech_end and not has_speech_ref:
                raise BuildError(
                    f"audio slice {owner!r} trim_to_speech_end requires "
                    "speech_end_ref")
            if has_speech_ref:
                speech_end = _resolve_timing_ref(
                    timing_points,
                    _required_slice_ref(raw_slice, "speech_end", owner),
                    owner, "speech_end_ref")
                if not math.isfinite(speech_end):
                    raise BuildError(
                        f"audio slice {owner!r} speech_end must be finite")
                if speech_end < start or speech_end > end:
                    raise BuildError(
                        f"audio slice {owner!r} speech_end {speech_end:g} "
                        f"must be within source range [{start:g}, {end:g}]")

            effective_end = speech_end if trim_to_speech_end else end
            if effective_end is None or effective_end <= start:
                raise BuildError(
                    f"audio slice {owner!r} trimmed source range must be "
                    "non-empty and non-negative")
            duration = effective_end - start

            segment = {
                "id": f"audio.slice.{slice_index}",
                "type": "audio_clip",
                "audio_slice_id": owner,
                "asset": asset,
                "cue_start": round(cursor, 9),
                "source_start": round(start, 9),
                "source_end": round(effective_end, 9),
                "source_start_sample": int(round(start * sample_rate)),
                "source_end_sample": int(round(effective_end * sample_rate)),
            }
            audio_segments.append(segment)

            for subtitle_index, raw_subtitle in enumerate(
                    raw_slice.get("subtitles") or []):
                sub_owner = f"{owner} subtitle[{subtitle_index}]"
                sub_start = _resolve_timing_ref(
                    timing_points,
                    _required_slice_ref(raw_subtitle, "start", sub_owner),
                    owner, f"subtitle[{subtitle_index}].start_ref")
                sub_end = _resolve_timing_ref(
                    timing_points,
                    _required_slice_ref(raw_subtitle, "end", sub_owner),
                    owner, f"subtitle[{subtitle_index}].end_ref")
                if sub_end <= sub_start:
                    raise BuildError(
                        f"audio slice {owner!r} subtitle[{subtitle_index}] "
                        "must have a positive duration")
                if sub_start < start or sub_end > effective_end:
                    raise BuildError(
                        f"audio slice {owner!r} subtitle[{subtitle_index}] "
                        f"range [{sub_start:g}, {sub_end:g}] is outside "
                        f"[{start:g}, {effective_end:g}]")
                subtitle_segments.append({
                    "id": f"subtitle.{slice_index}.{subtitle_index}",
                    "type": "subtitle",
                    "cue_start": round(cursor + (sub_start - start), 9),
                    "duration": round(sub_end - sub_start, 9),
                    "text": str(raw_subtitle.get("text") or "").strip(),
                })
            if speech_end is not None:
                last_speech_end = cursor + (speech_end - start)
            else:
                last_speech_end = cursor + duration
            cursor += duration

        post_speech_target = option.get("post_speech_silence_seconds")
        if post_speech_target is not None:
            existing_tail = max(0.0, cursor - last_speech_end)
            trailing = max(0.0, float(post_speech_target) - existing_tail)
        else:
            trailing = float(option.get("trailing_silence_seconds", 0.0))
        if trailing > 0.0:
            audio_segments.append({
                "id": "audio.trailing_silence",
                "type": "silence",
                "cue_start": round(cursor, 9),
                "duration": round(trailing, 9),
            })
            cursor += trailing

        visual_segments: list[dict] = []
        raw_visuals = cue_visuals.get(cue_id)
        if raw_visuals is not None:
            for visual_index, raw_visual in enumerate(raw_visuals):
                segment_id = str(raw_visual.get("id") or "").strip()
                animation = str(raw_visual.get("animation") or "").strip()
                visual_type = str(
                    raw_visual.get("type") or "animation").strip()
                compiled_cue = compiled_by_id.get(animation)
                if compiled_cue is None:
                    raise BuildError(
                        f"cue {cue_id!r} visual segment {segment_id!r} "
                        f"references unknown compiled animation {animation!r}")
                cue_start = _resolve_timing_ref(
                    timing_points,
                    _required_visual_ref(
                        raw_visual, "cue_start", segment_id),
                    segment_id, "cue_start_ref")
                if visual_type == "state":
                    visual_segments.append({
                        "id": segment_id,
                        "type": "state",
                        "animation": animation,
                        "cue_start": round(cue_start, 9),
                        # Filled to the final cue duration after timing policy.
                        "duration": 1e-9,
                        "after_end": str(raw_visual.get("after_end")),
                    })
                    continue
                source_start = _resolve_timing_ref(
                    timing_points,
                    _required_visual_ref(
                        raw_visual, "source_start", segment_id),
                    segment_id, "source_start_ref")
                source_end = _resolve_timing_ref(
                    timing_points,
                    _required_visual_ref(
                        raw_visual, "source_end", segment_id),
                    segment_id, "source_end_ref")
                compiled_duration = float(
                    compiled_cue.get("duration", 0.0) or 0.0)
                if source_end > compiled_duration + 1e-6:
                    raise BuildError(
                        f"cue {cue_id!r} visual segment {segment_id!r} "
                        f"source_end={source_end:g} exceeds compiled animation "
                        f"duration {compiled_duration:g}")
                visual_segments.append({
                    "id": segment_id,
                    "type": "animation",
                    "animation": animation,
                    "cue_start": round(cue_start, 9),
                    "source_start": round(source_start, 9),
                    "source_end": round(source_end, 9),
                    "after_end": str(raw_visual.get("after_end")),
                })
        else:
            visual_duration = 0.0
            compiled_cue = compiled_by_id.get(cue_id)
            if compiled_cue is not None:
                visual_duration = float(compiled_cue.get("duration", 0.0) or 0.0)
            if compiled_cue is not None:
                if visual_duration > 0.0:
                    visual_segments.append({
                        "id": "visual.compiled",
                        "type": "animation",
                        "animation": cue_id,
                        "cue_start": 0.0,
                        "source_start": 0.0,
                        "source_end": round(visual_duration, 9),
                        "after_end": "hold_last_frame",
                    })
                else:
                    # A zero-duration compiled cue still owns an instant
                    # state/overlay frame.  Dropping it would freeze or clear
                    # the visual state across audio-only cues.
                    visual_segments.append({
                        "id": "visual.state",
                        "type": "state",
                        "animation": cue_id,
                        "cue_start": 0.0,
                        "duration": 1e-9,
                        "after_end": "hold_last_frame",
                    })

        visual_duration = 0.0
        for segment in visual_segments:
            if segment["type"] == "state":
                continue
            visual_duration = max(
                visual_duration,
                float(segment["cue_start"])
                + float(segment["source_end"])
                - float(segment["source_start"]))
        tracks: list[dict] = []
        if visual_segments:
            tracks.append({
                "id": "visual.main",
                "kind": "visual",
                "tail_policy": "hold_last_frame",
                "segments": visual_segments,
            })
        if audio_segments:
            tracks.append({
                "id": "audio.main",
                "kind": "audio",
                "tail_policy": "silence",
                "segments": audio_segments,
            })
        if subtitle_segments:
            tracks.append({
                "id": "subtitle.main",
                "kind": "subtitle",
                "tail_policy": "clear",
                "segments": subtitle_segments,
            })
        if not tracks:
            raise BuildError(f"cue {cue_id!r} produced no executable tracks")

        flat_subtitles = [{
            "id": segment["id"],
            "cue_start": segment["cue_start"],
            "duration": segment["duration"],
            "text": segment["text"],
        } for segment in subtitle_segments]
        group_path = script_cue.get("group_path") or runtime_cue.get("group_path") or []
        refs = script_cue.get("refs") or runtime_cue.get("refs") or []
        plans.append({
            "schema": cue_timeline.PLAN_SCHEMA,
            "cue_id": cue_id,
            "entry": runtime_cue.get("entry"),
            "course": {
                "start": 0.0,
                "duration": round(max(cursor, visual_duration), 9),
            },
            "tracks": tracks,
            "subtitles": flat_subtitles,
            "text": runtime_cue.get("text") or cue_text,
            "group_path": list(group_path),
            "refs": list(refs),
        })

    return {
        "schema": cue_timeline.PLAN_SET_SCHEMA,
        "schema_version": cue_timeline.PLAN_SET_VERSION,
        "game_id": runtime.get("game_id") or game,
        "track": track,
        "course": {"start": 0.0, "duration": 0.0},
        "cues": plans,
        "diagnostics": diagnostics,
    }


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
        timing_policy = _load_timing_policy(repo_root, game, track)
        audio_source = str(timing_policy.get("audio_source") or "").strip()
        if audio_source != "audio_slice_manifest":
            raise BuildError(
                "all cue-timeline tracks require audio_source="
                "audio_slice_manifest; legacy runtime timing is not a "
                f"runtime cue-time source (got {audio_source!r})")
        default_mode = str(timing_policy.get("default_mode") or "max").strip()
        document = _build_cue_plan_set_from_audio_slices(
            repo_root, game, track, runtime, compiled, timing_policy)
        slice_rel = str(
            timing_policy.get("audio_slice_manifest") or "").strip()
        slice_doc = _load_audio_slices(repo_root, game, track, slice_rel)
        cue_timing_options = _normalize_cue_timing_options(
            slice_doc, default_mode)
        _apply_timing_policy(document, cue_timing_options, default_mode)
        _recompute_course_timeline(
            document, [0.0] * len(document.get("cues") or []))
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
        for track in plan.get("tracks") or []:
            if not isinstance(track, dict):
                continue
            for segment in track.get("segments") or []:
                if not isinstance(segment, dict):
                    continue
                if segment.get("type") != "audio_clip":
                    continue
                raw_asset = str(segment.get("asset") or "").strip().replace("\\", "/")
                if not raw_asset or Path(raw_asset).is_absolute():
                    raise BuildError(
                        f"cue {plan.get('cue_id')!r}: audio asset must be a "
                        f"game-relative path: {raw_asset!r}")
                if not (game_dir / raw_asset).is_file():
                    raise BuildError(
                        f"cue {plan.get('cue_id')!r}: plan references missing "
                        f"audio file: {raw_asset}")

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
    parser.add_argument("--game", required=True, help="game id")
    parser.add_argument("--track", required=True, help="tutorial track id")
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
