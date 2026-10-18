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


def _timing_mode(option: Any, default_mode: str) -> str:
    if option is None:
        return default_mode
    if isinstance(option, str):
        return option
    if isinstance(option, dict):
        return str(option.get("mode") or default_mode)
    raise BuildError(f"timing policy cue option must be a string or object, got {option!r}")


def _cue_trailing_silence_seconds(policy_document: dict, cue_id: str) -> float:
    option = (policy_document.get("cues") or {}).get(cue_id)
    if not isinstance(option, dict):
        return 0.0
    value = option.get("trailing_silence_seconds", 0.0)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BuildError(
            f"timing policy cue {cue_id!r} trailing_silence_seconds must be numeric")
    seconds = float(value)
    if not math.isfinite(seconds) or seconds < 0.0:
        raise BuildError(
            f"timing policy cue {cue_id!r} trailing_silence_seconds must be >= 0")
    return seconds


def _cue_post_speech_silence_seconds(policy_document: dict, cue_id: str) -> float | None:
    option = (policy_document.get("cues") or {}).get(cue_id)
    if not isinstance(option, dict) or "post_speech_silence_seconds" not in option:
        return None
    value = option.get("post_speech_silence_seconds")
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BuildError(
            f"timing policy cue {cue_id!r} post_speech_silence_seconds must be numeric")
    seconds = float(value)
    if not math.isfinite(seconds) or seconds < 0.0:
        raise BuildError(
            f"timing policy cue {cue_id!r} post_speech_silence_seconds must be >= 0")
    return seconds


def _tts_last_word_end_seconds(
    raw_subtitle_path: str | None,
    game_dir: Path,
    game: str,
) -> float | None:
    if not raw_subtitle_path:
        return None
    rel = _normalize_game_relative_path(raw_subtitle_path, game_dir, game)
    events = json.loads((game_dir / rel).read_text(encoding="utf-8"))
    if not isinstance(events, list):
        return None
    ends: list[float] = []
    for event in events:
        if not isinstance(event, dict):
            continue
        for word in event.get("words") or []:
            if not isinstance(word, dict):
                continue
            try:
                ends.append(float(word.get("endTime", 0.0)))
            except (TypeError, ValueError):
                continue
    return max(ends) if ends else None


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
    for cue_id, option in cue_modes.items():
        if not isinstance(cue_id, str) or not cue_id:
            raise BuildError("timing policy cue id must be a non-empty string")
        mode = _timing_mode(option, default_mode)
        if mode not in valid_modes:
            raise BuildError(
                f"timing policy cue {cue_id!r} has invalid mode {mode!r}")

    diagnostics = document.setdefault("diagnostics", [])
    known_ids = set()
    for plan in document.get("cues") or []:
        cue_id = str(plan.get("cue_id") or "")
        known_ids.add(cue_id)
        mode = _timing_mode(cue_modes.get(cue_id), default_mode)
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


def _load_tts_subtitle_events(
    raw_path: str | None,
    game_dir: Path,
    game: str,
    beat_text: str,
    beat_duration: float,
) -> list[dict]:
    if not raw_path:
        return [{
            "cue_start": 0.0,
            "duration": round(beat_duration, 9),
            "text": beat_text,
        }]
    rel = _normalize_game_relative_path(raw_path, game_dir, game)
    path = game_dir / rel
    events = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(events, list):
        raise BuildError(f"subtitle file must contain an array: {path}")
    result: list[dict] = []
    for event_index, event in enumerate(events):
        if not isinstance(event, dict):
            continue
        text = str(event.get("text") or "").strip()
        words = event.get("words") or []
        starts = []
        ends = []
        if isinstance(words, list):
            for word in words:
                if not isinstance(word, dict):
                    continue
                try:
                    starts.append(float(word.get("startTime", 0.0)))
                    ends.append(float(word.get("endTime", 0.0)))
                except (TypeError, ValueError):
                    continue
        start = max(0.0, min(starts)) if starts else 0.0
        end = max(ends) if ends else beat_duration
        start = min(start, beat_duration)
        end = min(max(start, end), beat_duration)
        result.append({
            "cue_start": round(start, 9),
            "duration": round(max(0.0, end - start), 9),
            "text": text,
            "event_index": event_index,
        })
    if not result:
        result.append({
            "cue_start": 0.0,
            "duration": round(beat_duration, 9),
            "text": beat_text,
        })
    return result


def _key_text(text: str) -> str:
    return "".join(ch for ch in str(text or "") if ch.isalnum())


def _word_stream(subtitle_events: list) -> list[dict]:
    words: list[dict] = []
    for event in subtitle_events:
        if not isinstance(event, dict):
            continue
        for word in event.get("words") or []:
            if not isinstance(word, dict):
                continue
            try:
                start = float(word.get("startTime", 0.0))
                end = float(word.get("endTime", start))
            except (TypeError, ValueError):
                continue
            words.append({
                "word": str(word.get("word") or ""),
                "start": start,
                "end": end,
            })
    words.sort(key=lambda item: (item["start"], item["end"]))
    return words


def _shared_group_ranges(
    cue_ids: list[str],
    cue_texts: dict[str, str],
    duration: float,
    subtitle_events: list,
) -> dict[str, tuple[float, float]]:
    """Resolve cue boundaries inside one shared TTS file from TTS word timing."""
    missing = [cue_id for cue_id in cue_ids if cue_id not in cue_texts]
    if missing:
        raise BuildError(
            "shared audio group references unknown cues: " + ", ".join(missing))
    if len(cue_ids) == 1:
        return {cue_ids[0]: (0.0, duration)}

    words = _word_stream(subtitle_events)
    boundaries = [0.0]
    if words:
        flat_parts: list[str] = []
        char_word: list[int] = []
        for word_index, word in enumerate(words):
            piece = _key_text(word["word"])
            flat_parts.append(piece)
            char_word.extend([word_index] * len(piece))
        flat = "".join(flat_parts)
        search_from = 0
        for cue_index in range(1, len(cue_ids)):
            needle = _key_text(cue_texts[cue_ids[cue_index]])
            if not needle:
                boundary = boundaries[-1]
            else:
                char_index = flat.find(needle, search_from)
                if char_index < 0:
                    # Deterministic text-length fallback when TTS word text and
                    # source text do not share exact character runs.
                    previous_chars = sum(
                        len(_key_text(cue_texts[cue_ids[i]]))
                        for i in range(cue_index)
                    )
                    total_chars = sum(len(_key_text(cue_texts[cid])) for cid in cue_ids)
                    ratio = previous_chars / max(1, total_chars)
                    boundary = duration * ratio
                else:
                    word_index = char_word[char_index]
                    next_start = words[word_index]["start"]
                    previous_end = words[word_index - 1]["end"] if word_index > 0 else 0.0
                    # Cut in the middle of the inter-word silence when there is
                    # one.  This keeps the whole pause and the next onset on
                    # both sides of the cue boundary without waveform search.
                    boundary = ((previous_end + next_start) / 2.0
                                if next_start > previous_end else next_start)
                    search_from = char_index + len(needle)
            boundary = min(duration, max(boundaries[-1], boundary))
            boundaries.append(round(boundary, 9))
    else:
        total_chars = sum(len(_key_text(cue_texts[cid])) for cid in cue_ids)
        cursor_chars = 0
        for cue_index in range(1, len(cue_ids)):
            cursor_chars += len(_key_text(cue_texts[cue_ids[cue_index - 1]]))
            ratio = cursor_chars / max(1, total_chars)
            boundaries.append(round(duration * ratio, 9))

    boundaries.append(round(duration, 9))
    return {
        cue_id: (boundaries[index], boundaries[index + 1])
        for index, cue_id in enumerate(cue_ids)
    }


def _build_cue_plan_set_from_tts(
    repo_root: Path,
    game: str,
    track: str,
    runtime: dict,
    compiled: dict,
    policy_document: dict,
) -> dict:
    """Build plans from one-whole-file-per-beat TTS assets.

    No source ranges are authored or inferred here.  Each audio segment refers
    to a complete generated file and starts at 0; cue-local order comes from the
    beat order.  Arbitrary wait/hold behavior is expressed by segment anchors or
    track content lengths, not by waveform search.
    """
    game_dir = repo_root / "content" / "games" / game
    manifest_rel = str(policy_document.get("tts_manifest") or "").strip()
    script_rel = str(policy_document.get("script_source") or "").strip()
    if not manifest_rel or not script_rel:
        raise BuildError(
            "cue_tts_manifest timing policy requires tts_manifest and script_source")
    tts_manifest_path = game_dir / manifest_rel
    script_path = game_dir / script_rel
    tts_manifest = _load_json(tts_manifest_path)
    script = _load_json(script_path)

    tts_by_beat = {}
    for entry in tts_manifest.get("cues") or []:
        if not isinstance(entry, dict):
            continue
        beat_id = str(entry.get("id") or "").strip()
        if not beat_id:
            continue
        tts_by_beat[beat_id] = entry

    cue_to_audio_group: dict[str, str] = {}
    group_specs: dict[str, dict] = {}
    for group_id, raw_group in (policy_document.get("audio_groups") or {}).items():
        if not isinstance(group_id, str) or not group_id:
            raise BuildError("audio group id must be a non-empty string")
        if not isinstance(raw_group, dict):
            raise BuildError(f"audio group {group_id!r} must be an object")
        cue_ids = raw_group.get("cue_ids") or []
        beat_id = str(raw_group.get("beat_id") or "").strip()
        if not isinstance(cue_ids, list) or not cue_ids or not beat_id:
            raise BuildError(
                f"audio group {group_id!r} requires cue_ids and beat_id")
        for cue_id in cue_ids:
            cue_id = str(cue_id)
            if cue_id in cue_to_audio_group:
                raise BuildError(f"cue {cue_id!r} belongs to multiple audio groups")
            cue_to_audio_group[cue_id] = group_id
        group_specs[group_id] = {
            "cue_ids": [str(cue_id) for cue_id in cue_ids],
            "beat_id": beat_id,
        }

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
    sample_rate = int(tts_manifest.get("sample_rate") or runtime.get("sample_rate") or 24000)
    cue_texts = {
        str(cue.get("id")): "".join(
            str(beat.get("text") or "")
            for beat in (cue.get("beats") or []) if isinstance(beat, dict)
        )
        for cue in script.get("cues") or [] if isinstance(cue, dict)
    }
    group_ranges: dict[str, dict[str, tuple[float, float]]] = {}
    for group_id, spec in group_specs.items():
        entry = tts_by_beat.get(spec["beat_id"])
        if entry is None:
            raise BuildError(
                f"missing TTS manifest entry for audio group {group_id!r}")
        group_duration = float(entry.get("duration", 0.0))
        if not math.isfinite(group_duration) or group_duration <= 0.0:
            raise BuildError(f"invalid TTS duration for audio group {group_id!r}")
        group_events = _load_tts_subtitle_events(
            entry.get("subtitle_file"), game_dir, game, "", group_duration)
        # Raw events are needed for word timing; _load_tts_subtitle_events
        # returns plan windows, so reload raw JSON for split resolution.
        raw_events = []
        subtitle_file = entry.get("subtitle_file")
        if subtitle_file:
            subtitle_rel = _normalize_game_relative_path(
                str(subtitle_file), game_dir, game)
            raw_events = json.loads((game_dir / subtitle_rel).read_text(encoding="utf-8"))
        group_ranges[group_id] = _shared_group_ranges(
            spec["cue_ids"],
            cue_texts,
            group_duration,
            raw_events,
        )

    slice_manifest_rel = str(policy_document.get("slice_manifest") or "").strip()
    slices_by_cue: dict[str, dict] = {}
    if slice_manifest_rel:
        slice_manifest_path = game_dir / slice_manifest_rel
        slice_manifest = _load_json(slice_manifest_path)
        for entry in slice_manifest.get("cues") or []:
            if not isinstance(entry, dict):
                continue
            cue_id = str(entry.get("cue_id") or "").strip()
            if not cue_id:
                continue
            asset = _normalize_game_relative_path(
                str(entry.get("file") or ""), game_dir, game)
            duration = float(entry.get("duration", 0.0))
            if duration <= 0.0:
                raise BuildError(
                    f"invalid sliced audio duration for cue {cue_id!r}")
            slices_by_cue[cue_id] = {
                "asset": asset,
                "duration": duration,
            }

    plans: list[dict] = []
    diagnostics: list[dict] = [{
        "code": "cue_audio_source",
        "severity": "info",
        "message": "audio segments are whole generated files starting at cue-local 0",
    }]
    for script_cue in script.get("cues") or []:
        if not isinstance(script_cue, dict):
            continue
        cue_id = str(script_cue.get("id") or "").strip()
        if not cue_id:
            raise BuildError("script cue id is empty")
        runtime_cue = runtime_by_id.get(cue_id, {})
        audio_segments: list[dict] = []
        subtitles: list[dict] = []
        cursor = 0.0
        last_speech_end = 0.0
        beats = script_cue.get("beats") or []
        cue_text = cue_texts.get(cue_id, "")
        group_id = cue_to_audio_group.get(cue_id)
        if group_id is not None:
            spec = group_specs[group_id]
            sliced = slices_by_cue.get(cue_id)
            if sliced is not None:
                segment_duration = float(sliced["duration"])
                asset = str(sliced["asset"])
                source_start = 0.0
                source_end = segment_duration
                playback_group = None
            else:
                entry = tts_by_beat[spec["beat_id"]]
                start, end = group_ranges[group_id][cue_id]
                segment_duration = end - start
                if segment_duration <= 0.0:
                    raise BuildError(
                        f"audio group {group_id!r} produced empty cue range for {cue_id!r}")
                asset = _normalize_game_relative_path(
                    str(entry.get("file") or ""), game_dir, game)
                source_start = start
                source_end = end
                playback_group = group_id
            segment = {
                "id": "audio.cue",
                "type": "audio_clip",
                "asset": asset,
                "cue_start": 0.0,
                "source_start": round(source_start, 9),
                "source_end": round(source_end, 9),
                "source_start_sample": int(round(source_start * sample_rate)),
                "source_end_sample": int(round(source_end * sample_rate)),
            }
            if playback_group is not None:
                segment["playback_group"] = playback_group
            audio_segments.append(segment)
            subtitles.append({
                "id": "subtitle.0",
                "cue_start": 0.0,
                "duration": round(segment_duration, 9),
                "text": cue_text,
            })
            cursor = segment_duration
            last_speech_end = segment_duration
        else:
            for beat_index, beat in enumerate(beats):
                if not isinstance(beat, dict):
                    continue
                beat_id = str(beat.get("id") or "").strip()
                beat_text = str(beat.get("text") or "")
                entry = tts_by_beat.get(beat_id)
                if entry is None:
                    raise BuildError(f"missing TTS manifest entry for beat {beat_id!r}")
                duration = float(entry.get("duration", 0.0))
                if not math.isfinite(duration) or duration <= 0.0:
                    raise BuildError(f"invalid TTS duration for beat {beat_id!r}")
                asset = _normalize_game_relative_path(
                    str(entry.get("file") or ""), game_dir, game)
                beat_speech_end = _tts_last_word_end_seconds(
                    entry.get("subtitle_file"), game_dir, game)
                if beat_speech_end is None:
                    beat_speech_end = duration
                last_speech_end = cursor + min(duration, max(0.0, beat_speech_end))
                audio_segments.append({
                    "id": f"audio.beat.{beat_index}",
                    "type": "audio_clip",
                    "asset": asset,
                    "cue_start": round(cursor, 9),
                    "source_start": 0.0,
                    "source_end": round(duration, 9),
                    "source_start_sample": 0,
                    "source_end_sample": int(round(duration * sample_rate)),
                })
                for event_index, event in enumerate(_load_tts_subtitle_events(
                    entry.get("subtitle_file"), game_dir, game, beat_text, duration)):
                    subtitles.append({
                        "id": f"subtitle.{beat_index}.{event_index}",
                        "cue_start": round(cursor + float(event["cue_start"]), 9),
                        "duration": round(float(event["duration"]), 9),
                        "text": str(event.get("text") or beat_text),
                    })
                cursor += duration

        post_speech_target = _cue_post_speech_silence_seconds(
            policy_document, cue_id)
        if post_speech_target is not None:
            existing_tail = max(0.0, cursor - last_speech_end)
            trailing = max(0.0, post_speech_target - existing_tail)
        else:
            trailing = _cue_trailing_silence_seconds(policy_document, cue_id)
        if trailing > 0.0:
            audio_segments.append({
                "id": "audio.trailing_silence",
                "type": "silence",
                "cue_start": round(cursor, 9),
                "duration": round(trailing, 9),
            })
            cursor += trailing

        visual_duration = 0.0
        compiled_cue = compiled_by_id.get(cue_id)
        if compiled_cue is not None:
            visual_duration = float(compiled_cue.get("duration", 0.0) or 0.0)
        tracks: list[dict] = []
        if visual_duration > 0.0:
            tracks.append({
                "id": "visual.main",
                "kind": "visual",
                "tail_policy": "hold_last_frame",
                "segments": [{
                    "id": "visual.compiled",
                    "type": "animation",
                    "animation": cue_id,
                    "cue_start": 0.0,
                    "source_start": 0.0,
                    "source_end": round(visual_duration, 9),
                    "after_end": "hold_last_frame",
                }],
            })
        if audio_segments:
            tracks.append({
                "id": "audio.main",
                "kind": "audio",
                "tail_policy": "silence",
                "segments": audio_segments,
            })

        group_path = script_cue.get("group_path") or runtime_cue.get("group_path") or []
        refs = script_cue.get("refs") or runtime_cue.get("refs") or []
        plan = {
            "schema": cue_timeline.PLAN_SCHEMA,
            "cue_id": cue_id,
            "entry": runtime_cue.get("entry"),
            "course": {
                "start": 0.0,
                "duration": round(max(cursor, visual_duration), 9),
            },
            "tracks": tracks,
            "subtitles": subtitles,
            "text": runtime_cue.get("text") or cue_text,
            "group_path": list(group_path),
            "refs": list(refs),
        }
        plans.append(plan)

    planned_ids = {str(plan.get("cue_id")) for plan in plans}
    unknown_group_cues = sorted(set(cue_to_audio_group) - planned_ids)
    if unknown_group_cues:
        raise BuildError(
            "audio groups reference unknown cue ids: "
            + ", ".join(unknown_group_cues))

    document = {
        "schema": cue_timeline.PLAN_SET_SCHEMA,
        "schema_version": cue_timeline.PLAN_SET_VERSION,
        "game_id": runtime.get("game_id") or game,
        "track": track,
        "course": {"start": 0.0, "duration": 0.0},
        "cues": plans,
        "diagnostics": diagnostics,
    }
    return document


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
        audio_source = str(timing_policy.get("audio_source") or "legacy_runtime")
        if audio_source == "cue_tts_manifest":
            document = _build_cue_plan_set_from_tts(
                repo_root, game, track, runtime, compiled, timing_policy)
            explicit_gaps = [0.0] * len(document.get("cues") or [])
        elif audio_source == "legacy_runtime":
            document = cue_timeline.adapt_legacy_runtime(runtime, compiled)
            boundaries = _load_audio_boundaries(repo_root, game, track)
            _apply_audio_boundaries(document, game, track, boundaries)
            explicit_gaps = _legacy_explicit_gaps(runtime)
        else:
            raise BuildError(
                f"unsupported timing policy audio_source {audio_source!r}")

        legacy_runtime_durations = {
            str(cue.get("id")): float(cue.get("duration", 0.0))
            for cue in runtime.get("cues") or []
            if isinstance(cue, dict) and cue.get("id") is not None
        }
        _apply_timing_policy(document, timing_policy, legacy_runtime_durations)
        _recompute_course_timeline(document, explicit_gaps)
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
