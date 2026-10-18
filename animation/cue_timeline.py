#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Pure compiler, validator and legacy adapter for the cue timeline protocol.

This module is intentionally independent from Unity, Android and the current
playback code.  It only transforms JSON-like dictionaries:

* ``tutorial-cue-source/v1`` -> ``tutorial-cue-plan/v1``
* validation of ``tutorial-cue-plan/v1`` plans
* legacy runtime document -> ``tutorial-cue-plan-set/v1`` plan set

The old runtime and compiled files are never modified.  In particular, this
module does not use subtitle word timings as audio boundaries and does not add
any title-cue or per-content special cases.
"""
from __future__ import annotations

import copy
import math
from typing import Any

SOURCE_SCHEMA = "tutorial-cue-source/v1"
PLAN_SCHEMA = "tutorial-cue-plan/v1"
PLAN_SET_SCHEMA = "tutorial-cue-plan-set/v1"
PLAN_SET_VERSION = 1

TRACK_KINDS = frozenset({"visual", "audio", "subtitle"})
TRACK_TAIL_POLICIES = {
    "visual": frozenset({"hold_last_frame", "clear"}),
    "audio": frozenset({"silence", "stop"}),
    "subtitle": frozenset({"clear"}),
}
SEGMENT_TYPES = {
    "visual": frozenset({"animation", "hold"}),
    "audio": frozenset({"audio_clip", "silence"}),
    "subtitle": frozenset({"subtitle"}),
}
AFTER_END_POLICIES = frozenset({"hold_last_frame", "clear"})

_EPSILON = 1e-9
_TIME_DIGITS = 9


class CueTimelineError(ValueError):
    """A protocol or data error that must not be silently repaired."""


# ── scalar helpers ─────────────────────────────────────────────────────────


def _mapping(value: Any, path: str) -> dict:
    if not isinstance(value, dict):
        raise CueTimelineError(f"{path}: expected object")
    return value


def _list(value: Any, path: str) -> list:
    if not isinstance(value, list):
        raise CueTimelineError(f"{path}: expected array")
    return value


def _number(value: Any, path: str, *, minimum: float | None = None,
            positive: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise CueTimelineError(f"{path}: expected a finite number")
    number = float(value)
    if not math.isfinite(number):
        raise CueTimelineError(f"{path}: expected a finite number")
    if positive and number <= 0.0:
        raise CueTimelineError(f"{path}: must be greater than 0")
    if minimum is not None and number < minimum:
        raise CueTimelineError(f"{path}: must be >= {minimum:g}")
    return number


def _text(value: Any, path: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise CueTimelineError(f"{path}: expected a non-empty string")
    return value


def _round_time(value: float) -> float:
    return round(float(value), _TIME_DIGITS)


def _sample_rate(value: Any, path: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise CueTimelineError(f"{path}: expected a positive integer")
    if value <= 0:
        raise CueTimelineError(f"{path}: expected a positive integer")
    return value


def _samples(seconds: float, rate: int, path: str) -> int:
    if seconds < 0.0:
        raise CueTimelineError(f"{path}: sample position cannot be negative")
    return int(round(seconds * rate))



# ── source compiler ────────────────────────────────────────────────────────


def _source_segment_duration(track_kind: str, segment: dict,
                             path: str) -> float:
    """Validate one source segment and return its duration in cue time."""
    segment_type = segment.get("type")
    if not isinstance(segment_type, str) or not segment_type:
        raise CueTimelineError(f"{path}.type: expected a non-empty string")
    allowed = SEGMENT_TYPES[track_kind]
    if segment_type not in allowed:
        raise CueTimelineError(
            f"{path}.type: unknown {track_kind} segment type {segment_type!r}")

    if segment_type == "animation":
        _text(segment.get("animation"), f"{path}.animation")
        after_end = segment.get("after_end")
        if after_end not in AFTER_END_POLICIES:
            raise CueTimelineError(
                f"{path}.after_end: unknown visual tail policy {after_end!r}")
        source = _mapping(segment.get("source"), f"{path}.source")
        start = _number(source.get("start"), f"{path}.source.start",
                        minimum=0.0)
        end = _number(source.get("end"), f"{path}.source.end", minimum=0.0)
        if end <= start:
            raise CueTimelineError(
                f"{path}.source.end: must be greater than source.start")
        return end - start

    if segment_type == "hold":
        return _number(segment.get("duration"), f"{path}.duration",
                       positive=True)

    if segment_type == "audio_clip":
        _text(segment.get("asset"), f"{path}.asset")
        source = _mapping(segment.get("source"), f"{path}.source")
        start = _number(source.get("start"), f"{path}.source.start",
                        minimum=0.0)
        end = _number(source.get("end"), f"{path}.source.end", minimum=0.0)
        if end <= start:
            raise CueTimelineError(
                f"{path}.source.end: must be greater than source.start")
        return end - start

    if segment_type == "silence":
        return _number(segment.get("duration"), f"{path}.duration",
                       positive=True)

    if segment_type == "subtitle":
        if not isinstance(segment.get("text"), str):
            raise CueTimelineError(f"{path}.text: expected a string")
        return _number(segment.get("duration"), f"{path}.duration",
                       positive=True)

    # SEGMENT_TYPES should make this unreachable, but fail closed if it does not.
    raise CueTimelineError(f"{path}.type: unknown segment type {segment_type!r}")


def _anchor_from_at(segment: dict, path: str) -> dict | float:
    if "at" not in segment:
        raise CueTimelineError(f"{path}.at: missing time anchor")
    at = segment.get("at")
    if isinstance(at, bool) or not isinstance(at, (dict, int, float)):
        raise CueTimelineError(f"{path}.at: expected seconds or an anchor object")
    if isinstance(at, (int, float)) and not isinstance(at, bool):
        return _number(at, f"{path}.at", minimum=0.0)
    at_obj = _mapping(at, f"{path}.at")
    anchor = at_obj.get("anchor", at_obj)
    if not isinstance(anchor, dict):
        raise CueTimelineError(f"{path}.at.anchor: expected object")
    return anchor


def _resolve_source_segment_starts(track_infos: list[dict],
                                   segment_by_id: dict[str, dict]) -> dict[str, float]:
    starts: dict[str, float] = {}
    durations: dict[str, float] = {}
    state: dict[str, int] = {}

    def get_duration(segment_id: str) -> float:
        if segment_id not in durations:
            info = segment_by_id[segment_id]
            durations[segment_id] = _source_segment_duration(
                info["track_kind"], info["segment"], info["path"])
        return durations[segment_id]

    def resolve(segment_id: str) -> float:
        if segment_id in starts:
            return starts[segment_id]
        if state.get(segment_id) == 1:
            raise CueTimelineError(
                f"anchor cycle detected at segment {segment_id!r}")
        info = segment_by_id[segment_id]
        state[segment_id] = 1
        segment = info["segment"]
        path = info["path"]
        anchor = _anchor_from_at(segment, path)

        if isinstance(anchor, float):
            cue_start = anchor
        else:
            anchor_type = anchor.get("type")
            if not isinstance(anchor_type, str) or not anchor_type:
                raise CueTimelineError(
                    f"{path}.at.anchor.type: expected a non-empty string")
            if anchor_type == "cue_start":
                offset = _number(anchor.get("offset", 0.0),
                                 f"{path}.at.anchor.offset", minimum=0.0)
                cue_start = offset
            elif anchor_type == "absolute":
                absolute = _number(anchor.get("time"), f"{path}.at.anchor.time",
                                   minimum=0.0)
                cue_start = absolute
            elif anchor_type == "after":
                ref = _text(anchor.get("segment"),
                            f"{path}.at.anchor.segment")
                if ref not in segment_by_id:
                    raise CueTimelineError(
                        f"{path}.at.anchor.segment: unknown segment {ref!r}")
                delay = _number(anchor.get("delay", 0.0),
                                f"{path}.at.anchor.delay", minimum=0.0)
                cue_start = resolve(ref) + get_duration(ref) + delay
            elif anchor_type == "after_all":
                refs = _list(anchor.get("segments"),
                             f"{path}.at.anchor.segments")
                if not refs:
                    raise CueTimelineError(
                        f"{path}.at.anchor.segments: must not be empty")
                delay = _number(anchor.get("delay", 0.0),
                                f"{path}.at.anchor.delay", minimum=0.0)
                latest = 0.0
                for index, ref in enumerate(refs):
                    ref = _text(ref, f"{path}.at.anchor.segments[{index}]")
                    if ref not in segment_by_id:
                        raise CueTimelineError(
                            f"{path}.at.anchor.segments[{index}]: "
                            f"unknown segment {ref!r}")
                    latest = max(latest, resolve(ref) + get_duration(ref))
                cue_start = latest + delay
            else:
                raise CueTimelineError(
                    f"{path}.at.anchor.type: unknown anchor type {anchor_type!r}")

        if not math.isfinite(cue_start) or cue_start < 0.0:
            raise CueTimelineError(
                f"{path}: resolved cue_start must be a non-negative finite number")
        starts[segment_id] = _round_time(cue_start)
        state[segment_id] = 2
        return starts[segment_id]

    for segment_id in segment_by_id:
        resolve(segment_id)
    return starts


def _effective_sample_rate(source: dict, override: int | None) -> int | None:
    if override is not None:
        return _sample_rate(override, "sample_rate")
    if "sample_rate" not in source or source.get("sample_rate") is None:
        return None
    return _sample_rate(source.get("sample_rate"), "source.sample_rate")


def compile_cue_source(source: dict, *, course_start: float = 0.0,
                       sample_rate: int | None = None) -> dict:
    """Compile one ``tutorial-cue-source/v1`` document into a cue plan.

    ``course_start`` is supplied by the caller because a single cue source has
    no global course context.  ``sample_rate`` is optional; when supplied (or
    present as source.sample_rate), audio clips carry source sample positions.

    Invalid input raises ``CueTimelineError``.  There are no fallback anchors,
    automatic duration corrections, or subtitle-word-based clip trims.
    """
    source = _mapping(source, "source")
    if source.get("schema") != SOURCE_SCHEMA:
        raise CueTimelineError(
            f"source.schema: expected {SOURCE_SCHEMA!r}, "
            f"got {source.get('schema')!r}")
    cue_id = _text(source.get("cue_id"), "source.cue_id")
    global_start = _number(course_start, "course_start", minimum=0.0)
    rate = _effective_sample_rate(source, sample_rate)

    tracks = _list(source.get("tracks"), "source.tracks")
    track_ids: set[str] = set()
    segment_by_id: dict[str, dict] = {}
    track_infos: list[dict] = []

    for track_index, raw_track in enumerate(tracks):
        track_path = f"source.tracks[{track_index}]"
        track = _mapping(raw_track, track_path)
        track_id = _text(track.get("id"), f"{track_path}.id")
        if track_id in track_ids:
            raise CueTimelineError(
                f"{track_path}.id: duplicate track id {track_id!r}")
        track_ids.add(track_id)

        kind = track.get("kind")
        if kind not in TRACK_KINDS:
            raise CueTimelineError(
                f"{track_path}.kind: unknown track kind {kind!r}")
        tail_policy = track.get("tail_policy")
        if tail_policy not in TRACK_TAIL_POLICIES[kind]:
            raise CueTimelineError(
                f"{track_path}.tail_policy: unknown {kind} tail policy "
                f"{tail_policy!r}")

        raw_segments = _list(track.get("segments"), f"{track_path}.segments")
        segment_infos: list[dict] = []
        for segment_index, raw_segment in enumerate(raw_segments):
            segment_path = f"{track_path}.segments[{segment_index}]"
            segment = _mapping(raw_segment, segment_path)
            segment_id = _text(segment.get("id"), f"{segment_path}.id")
            if segment_id in segment_by_id:
                raise CueTimelineError(
                    f"{segment_path}.id: duplicate segment id {segment_id!r}")
            info = {
                "track_index": track_index,
                "track_id": track_id,
                "track_kind": kind,
                "track_path": track_path,
                "segment": segment,
                "path": segment_path,
            }
            segment_by_id[segment_id] = info
            segment_infos.append(info)
        track_infos.append({
            "track": track,
            "path": track_path,
            "id": track_id,
            "kind": kind,
            "tail_policy": tail_policy,
            "segments": segment_infos,
        })

    starts = _resolve_source_segment_starts(track_infos, segment_by_id)

    # Segment durations are needed again for overlap validation.
    durations = {
        segment_id: _source_segment_duration(info["track_kind"],
                                             info["segment"], info["path"])
        for segment_id, info in segment_by_id.items()
    }

    for track_info in track_infos:
        ordered = sorted(
            track_info["segments"],
            key=lambda info: starts[_text(info["segment"].get("id"),
                                          f"{info['path']}.id")],
        )
        previous_id: str | None = None
        previous_end = 0.0
        for info in ordered:
            segment_id = _text(info["segment"].get("id"), f"{info['path']}.id")
            start = starts[segment_id]
            if previous_id is not None and start < previous_end - _EPSILON:
                raise CueTimelineError(
                    f"{info['track_path']}: segment {segment_id!r} overlaps "
                    f"segment {previous_id!r} (start={start:g}, "
                    f"previous_end={previous_end:g})")
            previous_id = segment_id
            previous_end = start + durations[segment_id]

    max_content_end = max(
        (starts[segment_id] + durations[segment_id]
         for segment_id in segment_by_id),
        default=0.0,
    )
    if "duration" in source:
        cue_duration = _number(source.get("duration"), "source.duration",
                               positive=True)
        if max_content_end > cue_duration + _EPSILON:
            raise CueTimelineError(
                "source.duration: explicit duration "
                f"{cue_duration:g}s is shorter than content end "
                f"{max_content_end:g}s")
    else:
        if max_content_end <= 0.0:
            raise CueTimelineError(
                "source.duration: omitted and no segment has positive duration")
        cue_duration = max_content_end

    out_tracks: list[dict] = []
    for track_info in track_infos:
        out_segments: list[dict] = []
        for info in track_info["segments"]:
            segment = info["segment"]
            segment_type = segment.get("type")
            segment_id = _text(segment.get("id"), f"{info['path']}.id")
            out_segment: dict[str, Any] = {
                "id": segment_id,
                "type": segment_type,
                "cue_start": starts[segment_id],
            }
            if segment_type == "animation":
                source_range = _mapping(segment.get("source"),
                                        f"{info['path']}.source")
                out_segment.update({
                    "animation": segment.get("animation"),
                    "source_start": _round_time(
                        _number(source_range.get("start"),
                                f"{info['path']}.source.start", minimum=0.0)),
                    "source_end": _round_time(
                        _number(source_range.get("end"),
                                f"{info['path']}.source.end", minimum=0.0)),
                    "after_end": segment.get("after_end"),
                })
            elif segment_type == "hold":
                out_segment["duration"] = _round_time(
                    _number(segment.get("duration"),
                            f"{info['path']}.duration", positive=True))
                if "pose" in segment:
                    out_segment["pose"] = segment.get("pose")
            elif segment_type == "audio_clip":
                source_range = _mapping(segment.get("source"),
                                        f"{info['path']}.source")
                source_start = _number(source_range.get("start"),
                                       f"{info['path']}.source.start",
                                       minimum=0.0)
                source_end = _number(source_range.get("end"),
                                     f"{info['path']}.source.end",
                                     minimum=0.0)
                out_segment.update({
                    "asset": segment.get("asset"),
                    "source_start": _round_time(source_start),
                    "source_end": _round_time(source_end),
                })
                if rate is not None:
                    out_segment["source_start_sample"] = _samples(
                        source_start, rate, f"{info['path']}.source.start")
                    out_segment["source_end_sample"] = _samples(
                        source_end, rate, f"{info['path']}.source.end")
            elif segment_type == "silence":
                out_segment["duration"] = _round_time(
                    _number(segment.get("duration"),
                            f"{info['path']}.duration", positive=True))
            elif segment_type == "subtitle":
                if not isinstance(segment.get("text"), str):
                    raise CueTimelineError(
                        f"{info['path']}.text: expected a string")
                out_segment["duration"] = _round_time(
                    _number(segment.get("duration"),
                            f"{info['path']}.duration", positive=True))
                out_segment["text"] = segment.get("text")
            else:  # guarded above; keep failure closed
                raise CueTimelineError(
                    f"{info['path']}.type: unknown segment type {segment_type!r}")
            out_segments.append(out_segment)
        out_tracks.append({
            "id": track_info["id"],
            "kind": track_info["kind"],
            "tail_policy": track_info["tail_policy"],
            "segments": out_segments,
        })

    plan: dict[str, Any] = {
        "schema": PLAN_SCHEMA,
        "cue_id": cue_id,
    }
    if "entry" in source:
        plan["entry"] = source.get("entry")
    plan["course"] = {
        "start": _round_time(global_start),
        "duration": _round_time(cue_duration),
    }
    plan["tracks"] = out_tracks
    return plan


# Common public aliases; keep the compiler vocabulary discoverable.
compile_source = compile_cue_source
compile_cue_plan = compile_cue_source


# ── compiled plan validator ────────────────────────────────────────────────


def _validate_plan_segment(track_kind: str, segment: dict, path: str,
                           cue_duration: float) -> tuple[str, float]:
    segment_id = _text(segment.get("id"), f"{path}.id")
    segment_type = segment.get("type")
    if not isinstance(segment_type, str) or not segment_type:
        raise CueTimelineError(f"{path}.type: expected a non-empty string")
    if segment_type not in SEGMENT_TYPES[track_kind]:
        raise CueTimelineError(
            f"{path}.type: unknown {track_kind} segment type {segment_type!r}")
    cue_start = _number(segment.get("cue_start"), f"{path}.cue_start",
                        minimum=0.0)

    if segment_type == "animation":
        _text(segment.get("animation"), f"{path}.animation")
        after_end = segment.get("after_end")
        if after_end not in AFTER_END_POLICIES:
            raise CueTimelineError(
                f"{path}.after_end: unknown visual tail policy {after_end!r}")
        source_start = _number(segment.get("source_start"),
                               f"{path}.source_start", minimum=0.0)
        source_end = _number(segment.get("source_end"),
                             f"{path}.source_end", minimum=0.0)
        if source_end <= source_start:
            raise CueTimelineError(
                f"{path}.source_end: must be greater than source_start")
        end = cue_start + (source_end - source_start)
    elif segment_type == "hold":
        end = cue_start + _number(segment.get("duration"),
                                  f"{path}.duration", positive=True)
    elif segment_type == "audio_clip":
        _text(segment.get("asset"), f"{path}.asset")
        source_start = _number(segment.get("source_start"),
                               f"{path}.source_start", minimum=0.0)
        source_end = _number(segment.get("source_end"),
                             f"{path}.source_end", minimum=0.0)
        if source_end <= source_start:
            raise CueTimelineError(
                f"{path}.source_end: must be greater than source_start")
        has_start_sample = "source_start_sample" in segment
        has_end_sample = "source_end_sample" in segment
        if has_start_sample != has_end_sample:
            raise CueTimelineError(
                f"{path}: source_start_sample and source_end_sample must "
                "either both be present or both be absent")
        if has_start_sample:
            start_sample = segment.get("source_start_sample")
            end_sample = segment.get("source_end_sample")
            if (isinstance(start_sample, bool)
                    or not isinstance(start_sample, int)
                    or start_sample < 0):
                raise CueTimelineError(
                    f"{path}.source_start_sample: expected a non-negative integer")
            if (isinstance(end_sample, bool)
                    or not isinstance(end_sample, int)
                    or end_sample < 0):
                raise CueTimelineError(
                    f"{path}.source_end_sample: expected a non-negative integer")
            if end_sample < start_sample:
                raise CueTimelineError(
                    f"{path}.source_end_sample: must be >= source_start_sample")
        end = cue_start + (source_end - source_start)
    elif segment_type == "silence":
        end = cue_start + _number(segment.get("duration"),
                                  f"{path}.duration", positive=True)
    elif segment_type == "subtitle":
        if not isinstance(segment.get("text"), str):
            raise CueTimelineError(f"{path}.text: expected a string")
        end = cue_start + _number(segment.get("duration"),
                                  f"{path}.duration", positive=True)
    else:  # guarded above
        raise CueTimelineError(f"{path}.type: unknown segment type {segment_type!r}")

    if end > cue_duration + _EPSILON:
        raise CueTimelineError(
            f"{path}: segment end {end:g}s exceeds cue duration "
            f"{cue_duration:g}s")
    return segment_id, _round_time(end)


def validate_cue_plan(plan: dict) -> dict:
    """Validate one ``tutorial-cue-plan/v1`` document.

    The function returns the original object on success and raises
    ``CueTimelineError`` on the first protocol violation.  It is read-only.
    """
    plan = _mapping(plan, "plan")
    if plan.get("schema") != PLAN_SCHEMA:
        raise CueTimelineError(
            f"plan.schema: expected {PLAN_SCHEMA!r}, "
            f"got {plan.get('schema')!r}")
    _text(plan.get("cue_id"), "plan.cue_id")
    if "entry" in plan and plan.get("entry") is not None:
        if not isinstance(plan.get("entry"), str):
            raise CueTimelineError("plan.entry: expected a string or null")

    course = _mapping(plan.get("course"), "plan.course")
    _number(course.get("start"), "plan.course.start", minimum=0.0)
    cue_duration = _number(course.get("duration"), "plan.course.duration",
                           positive=True)

    tracks = _list(plan.get("tracks"), "plan.tracks")
    track_ids: set[str] = set()
    segment_ids: set[str] = set()
    for track_index, raw_track in enumerate(tracks):
        track_path = f"plan.tracks[{track_index}]"
        track = _mapping(raw_track, track_path)
        track_id = _text(track.get("id"), f"{track_path}.id")
        if track_id in track_ids:
            raise CueTimelineError(
                f"{track_path}.id: duplicate track id {track_id!r}")
        track_ids.add(track_id)
        kind = track.get("kind")
        if kind not in TRACK_KINDS:
            raise CueTimelineError(
                f"{track_path}.kind: unknown track kind {kind!r}")
        tail_policy = track.get("tail_policy")
        if tail_policy not in TRACK_TAIL_POLICIES[kind]:
            raise CueTimelineError(
                f"{track_path}.tail_policy: unknown {kind} tail policy "
                f"{tail_policy!r}")

        raw_segments = _list(track.get("segments"), f"{track_path}.segments")
        intervals: list[tuple[float, float, str]] = []
        for segment_index, raw_segment in enumerate(raw_segments):
            segment_path = f"{track_path}.segments[{segment_index}]"
            segment = _mapping(raw_segment, segment_path)
            segment_id, end = _validate_plan_segment(
                kind, segment, segment_path, cue_duration)
            if segment_id in segment_ids:
                raise CueTimelineError(
                    f"{segment_path}.id: duplicate segment id {segment_id!r}")
            segment_ids.add(segment_id)
            intervals.append((
                _number(segment.get("cue_start"), f"{segment_path}.cue_start",
                        minimum=0.0),
                end,
                segment_id,
            ))

        intervals.sort(key=lambda item: (item[0], item[1], item[2]))
        for previous, current in zip(intervals, intervals[1:]):
            if current[0] < previous[1] - _EPSILON:
                raise CueTimelineError(
                    f"{track_path}: segment {current[2]!r} overlaps "
                    f"segment {previous[2]!r}")
    return plan


validate_compiled_plan = validate_cue_plan
validate_plan = validate_cue_plan


def validate_plan_set(document: dict) -> dict:
    """Validate a ``tutorial-cue-plan-set/v1`` document produced by the adapter."""
    document = _mapping(document, "document")
    if document.get("schema") != PLAN_SET_SCHEMA:
        raise CueTimelineError(
            f"document.schema: expected {PLAN_SET_SCHEMA!r}, "
            f"got {document.get('schema')!r}")
    if document.get("schema_version") != PLAN_SET_VERSION:
        raise CueTimelineError(
            f"document.schema_version: expected {PLAN_SET_VERSION}, "
            f"got {document.get('schema_version')!r}")
    cues = _list(document.get("cues"), "document.cues")
    if not cues:
        raise CueTimelineError("document.cues: must not be empty")
    seen_ids: set[str] = set()
    previous_end: float | None = None
    for index, raw_plan in enumerate(cues):
        plan = validate_cue_plan(_mapping(raw_plan, f"document.cues[{index}]"))
        cue_id = plan["cue_id"]
        if cue_id in seen_ids:
            raise CueTimelineError(
                f"document.cues[{index}].cue_id: duplicate cue id {cue_id!r}")
        seen_ids.add(cue_id)
        start = float(plan["course"]["start"])
        end = start + float(plan["course"]["duration"])
        if previous_end is not None and start < previous_end - _EPSILON:
            raise CueTimelineError(
                f"document.cues[{index}].course.start: cue overlaps previous cue")
        previous_end = end
    return document


validate_cue_plan_set = validate_plan_set


def validate_plan_document(document: dict) -> dict:
    """Validate either a single cue plan or a plan set."""
    document = _mapping(document, "document")
    if document.get("schema") == PLAN_SET_SCHEMA:
        return validate_plan_set(document)
    return validate_cue_plan(document)


# ── legacy runtime adapter ─────────────────────────────────────────────────


def _adapt_legacy_subtitles(cue: dict, path: str) -> list[dict]:
    """Convert runtime subtitle windows into the compiled plan presentation schema.

    Runtime subtitles use ``t``/``end`` and may carry word/confidence data.  The
    plan schema exposes only flat cue-local windows; word timing is presentation
    metadata and must never drive audio scheduling or trimming.
    """
    raw_subtitles = cue.get("subtitles") or []
    subtitles = _list(raw_subtitles, f"{path}.subtitles")
    result: list[dict] = []
    for index, raw_subtitle in enumerate(subtitles):
        subtitle_path = f"{path}.subtitles[{index}]"
        subtitle = _mapping(raw_subtitle, subtitle_path)
        start = _number(subtitle.get("t"), f"{subtitle_path}.t", minimum=0.0)
        end = _number(subtitle.get("end"), f"{subtitle_path}.end", minimum=0.0)
        if end < start - _EPSILON:
            raise CueTimelineError(
                f"{subtitle_path}.end: must be >= subtitle start")
        text = subtitle.get("text", "")
        if not isinstance(text, str):
            raise CueTimelineError(f"{subtitle_path}.text: expected a string")
        result.append({
            "id": f"subtitle.{index}",
            "cue_start": _round_time(start),
            "duration": _round_time(max(0.0, end - start)),
            "text": text,
        })
    return result


def _legacy_audio_start(cue: dict, path: str) -> float:
    # Older single-audio cues did not always need audio_start.  Treat an
    # absent field as the documented default zero, but reject malformed values.
    if "audio_start" not in cue or cue.get("audio_start") is None:
        return 0.0
    return _number(cue.get("audio_start"), f"{path}.audio_start", minimum=0.0)


def _legacy_audio_track(cue: dict, cue_id: str,
                        runtime_duration: float) -> tuple[dict | None, list[dict]]:
    path = f"runtime.cues[{cue_id!r}]"
    diagnostics: list[dict] = []
    audio_plan = cue.get("audio_plan")

    if isinstance(audio_plan, list) and audio_plan:
        segments: list[dict] = []
        cursor = 0.0
        for index, raw_segment in enumerate(audio_plan):
            segment_path = f"{path}.audio_plan[{index}]"
            segment = _mapping(raw_segment, segment_path)
            segment_type = segment.get("type")
            if segment_type == "clip":
                file_name = _text(segment.get("file"), f"{segment_path}.file")
                source_start = _number(segment.get("start"),
                                       f"{segment_path}.start", minimum=0.0)
                source_end = _number(segment.get("end"),
                                     f"{segment_path}.end", minimum=0.0)
                if source_end <= source_start:
                    raise CueTimelineError(
                        f"{segment_path}.end: must be greater than start")
                duration = source_end - source_start
                segments.append({
                    "id": f"audio.plan.{index}",
                    "type": "audio_clip",
                    "asset": file_name,
                    "cue_start": _round_time(cursor),
                    "source_start": _round_time(source_start),
                    "source_end": _round_time(source_end),
                })
                cursor += duration
            elif segment_type == "silence":
                duration = _number(segment.get("duration"),
                                   f"{segment_path}.duration", positive=True)
                segments.append({
                    "id": f"audio.plan.{index}",
                    "type": "silence",
                    "cue_start": _round_time(cursor),
                    "duration": _round_time(duration),
                })
                cursor += duration
            else:
                raise CueTimelineError(
                    f"{segment_path}.type: unknown audio_plan type "
                    f"{segment_type!r}")
        return {
            "id": "audio.main",
            "kind": "audio",
            "tail_policy": "silence",
            "segments": segments,
        }, diagnostics

    if cue.get("section_audio") or cue.get("audio"):
        audio = _text(cue.get("audio"), f"{path}.audio")
        audio_start = _legacy_audio_start(cue, path)
        if runtime_duration <= 0.0:
            raise CueTimelineError(
                f"{path}.duration: legacy audio cue must have positive duration")
        segment_type = "section_audio" if cue.get("section_audio") else "single_audio"
        return {
            "id": "audio.main",
            "kind": "audio",
            "tail_policy": "silence",
            "segments": [{
                "id": f"audio.{segment_type}",
                "type": "audio_clip",
                "asset": audio,
                "cue_start": 0.0,
                "source_start": _round_time(audio_start),
                "source_end": _round_time(audio_start + runtime_duration),
            }],
        }, diagnostics

    diagnostics.append({
        "code": "missing_legacy_audio",
        "severity": "warning",
        "message": f"cue {cue_id!r} has no legacy audio path",
    })
    return None, diagnostics


def _compiled_cues_by_id(compiled: dict | list | None) -> dict[str, dict]:
    if compiled is None:
        return {}
    if isinstance(compiled, list):
        raw_cues = compiled
    else:
        compiled = _mapping(compiled, "compiled")
        if isinstance(compiled.get("cues"), list):
            raw_cues = compiled["cues"]
        elif compiled.get("id") is not None:
            raw_cues = [compiled]
        else:
            raise CueTimelineError(
                "compiled: expected a cue or an object with a cues array")
    out: dict[str, dict] = {}
    for index, raw_cue in enumerate(raw_cues):
        cue = _mapping(raw_cue, f"compiled.cues[{index}]")
        cue_id = _text(cue.get("id"), f"compiled.cues[{index}].id")
        if cue_id in out:
            raise CueTimelineError(
                f"compiled.cues[{index}].id: duplicate cue id {cue_id!r}")
        out[cue_id] = cue
    return out


def _track_content_end(track: dict) -> float:
    end = 0.0
    for segment in track.get("segments") or []:
        segment_type = segment.get("type")
        cue_start = float(segment.get("cue_start", 0.0) or 0.0)
        if segment_type in ("animation", "audio_clip"):
            end = max(
                end,
                cue_start
                + float(segment["source_end"]) - float(segment["source_start"]),
            )
        elif segment_type in ("hold", "silence"):
            end = max(end, cue_start + float(segment["duration"]))
        else:
            raise CueTimelineError(
                f"internal adapter error: unknown segment type {segment_type!r}")
    return end


def adapt_legacy_runtime(runtime: dict,
                         compiled: dict | list | None = None) -> dict:
    """Adapt a legacy runtime document to a unified cue plan set.

    The old runtime's ``audio_plan``, ``section_audio`` and ordinary single
    audio representations are converted to audio tracks.  When a matching
    compiled animation cue exists, one visual animation segment is emitted from
    source time 0 to that cue's compiled duration.

    The effective cue duration is the maximum of the legacy runtime duration,
    audio track content duration, and compiled animation duration.  Global cue
    starts are rebuilt from those effective durations.  Only gaps explicitly
    present between consecutive legacy cues are retained.

    Neither input document is mutated.
    """
    runtime = _mapping(runtime, "runtime")
    cues = _list(runtime.get("cues"), "runtime.cues")
    if not cues:
        raise CueTimelineError("runtime.cues: must not be empty")
    compiled_by_id = _compiled_cues_by_id(compiled)

    plans: list[dict] = []
    diagnostics: list[dict] = []
    seen_cue_ids: set[str] = set()
    cursor = 0.0
    previous_old_end: float | None = None

    for index, raw_cue in enumerate(cues):
        path = f"runtime.cues[{index}]"
        cue = _mapping(raw_cue, path)
        cue_id = _text(cue.get("id"), f"{path}.id")
        if cue_id in seen_cue_ids:
            raise CueTimelineError(
                f"{path}.id: duplicate cue id {cue_id!r}")
        seen_cue_ids.add(cue_id)

        old_start = _number(cue.get("start"), f"{path}.start", minimum=0.0)
        runtime_duration = _number(cue.get("duration"), f"{path}.duration",
                                   positive=True)
        if previous_old_end is not None:
            if old_start < previous_old_end - _EPSILON:
                raise CueTimelineError(
                    f"{path}.start: legacy cues overlap or are out of order")
            gap = old_start - previous_old_end
        else:
            gap = 0.0
        previous_old_end = old_start + runtime_duration
        plan_start = cursor + max(0.0, gap)

        cue_diagnostics: list[dict] = []
        audio_track, audio_diagnostics = _legacy_audio_track(
            cue, cue_id, runtime_duration)
        cue_diagnostics.extend(audio_diagnostics)

        compiled_cue = compiled_by_id.get(cue_id)
        visual_track: dict | None = None
        compiled_duration: float | None = None
        if compiled_cue is None:
            diagnostic = {
                "code": "missing_compiled_animation",
                "severity": "warning",
                "cue_id": cue_id,
                "message": (
                    f"cue {cue_id!r} has no compiled animation; "
                    "no visual track was emitted"
                ),
            }
            cue_diagnostics.append(diagnostic)
        else:
            compiled_duration = _number(
                compiled_cue.get("duration"),
                f"compiled cue {cue_id!r}.duration",
                minimum=0.0,
            )
            if compiled_duration > 0.0:
                visual_track = {
                    "id": "visual.main",
                    "kind": "visual",
                    "tail_policy": "hold_last_frame",
                    "segments": [{
                        "id": "visual.compiled",
                        "type": "animation",
                        "animation": cue_id,
                        "cue_start": 0.0,
                        "source_start": 0.0,
                        "source_end": _round_time(compiled_duration),
                        "after_end": "hold_last_frame",
                    }],
                }
            else:
                # A compiled cue may legitimately contain no animation clips
                # and therefore have duration 0.  A zero-length animation
                # segment would be meaningless, so record the absence instead
                # of inventing a frame or silently extending the cue.
                cue_diagnostics.append({
                    "code": "empty_compiled_animation",
                    "severity": "warning",
                    "cue_id": cue_id,
                    "message": (
                        f"cue {cue_id!r} has a compiled cue with zero duration; "
                        "no visual segment was emitted"
                    ),
                })

        durations = [runtime_duration]
        if audio_track is not None:
            durations.append(_track_content_end(audio_track))
        if compiled_duration is not None:
            durations.append(compiled_duration)
        effective_duration = max(durations)

        tracks: list[dict] = []
        if visual_track is not None:
            tracks.append(visual_track)
        if audio_track is not None:
            tracks.append(audio_track)

        entry = cue.get("entry")
        if entry is None and compiled_cue is not None:
            entry = compiled_cue.get("entry")
        plan: dict[str, Any] = {
            "schema": PLAN_SCHEMA,
            "cue_id": cue_id,
            "entry": copy.deepcopy(entry),
            "course": {
                "start": _round_time(plan_start),
                "duration": _round_time(effective_duration),
            },
            "tracks": tracks,
            "subtitles": _adapt_legacy_subtitles(cue, path),
        }
        # Navigation/presentation metadata is not timing data.  Keeping it in
        # the plan makes the generated plan self-contained for Android chapter
        # trees while the legacy runtime stays the fallback source of truth.
        for metadata_key in ("text", "group_path", "refs"):
            if metadata_key in cue:
                plan[metadata_key] = copy.deepcopy(cue.get(metadata_key))
        if cue_diagnostics:
            plan["diagnostics"] = copy.deepcopy(cue_diagnostics)
            for diagnostic in cue_diagnostics:
                item = copy.deepcopy(diagnostic)
                item.setdefault("cue_id", cue_id)
                diagnostics.append(item)
        plans.append(plan)
        cursor = plan_start + effective_duration

    document = {
        "schema": PLAN_SET_SCHEMA,
        "schema_version": PLAN_SET_VERSION,
        "game_id": runtime.get("game_id"),
        "track": runtime.get("track"),
        "course": {
            "start": 0.0,
            "duration": _round_time(cursor),
        },
        "cues": plans,
        "diagnostics": diagnostics,
    }
    # Adapter output is expected to be a valid plan set by construction.
    validate_plan_set(document)
    return document


adapt_legacy = adapt_legacy_runtime
