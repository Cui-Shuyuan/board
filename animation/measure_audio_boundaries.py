#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Measure audible start/end timestamps for fixed audio slices.

The TTS subtitle event's word boundaries can be earlier than the real PCM
speech tail (observed with Doubao seed2 audio).  This tool decodes the
referenced audio to PCM, measures the first and last meaningful energy windows
inside each authored source range, and writes the results back as the slice's
``speech_start_ref`` and ``speech_end_ref`` timestamps.  Playback and trim
never run energy analysis; they only consume the stored timestamps.

Usage:
    # quick: measure each authored cue-level source window
    python3 animation/measure_audio_boundaries.py \
        --game splendor --track quick \
        --ffmpeg /path/to/ffmpeg \
        --write --guard-seconds 0.1

    # full: measure raw runtime section windows before rebuilding audio-slices
    python3 animation/measure_audio_boundaries.py \
        --game splendor --track full --runtime-windows \
        --ffmpeg /path/to/ffmpeg \
        --write --guard-seconds 0.1

The optional ``--guard-seconds`` updates both the cue's
``trim_start_guard_seconds`` and ``trim_tail_guard_seconds`` policy.  Guards
stay outside the stored speech timestamps, so the fixed timeline remains a
plain timestamp file.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import subprocess
import sys
from array import array
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
WINDOW_SECONDS = 0.010
TAIL_PEAK_RATIO = 0.005
TAIL_ABS_RMS_FLOOR = 60.0
START_PEAK_RATIO = 0.05
START_ABS_RMS_FLOOR = 80.0
START_MIN_RUN_WINDOWS = 3
EPSILON = 1e-9


class MeasureError(RuntimeError):
    pass


def _load_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise MeasureError(f"missing file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise MeasureError(f"invalid JSON in {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise MeasureError(f"{path}: expected a JSON object")
    return value


def _resolve_ffmpeg(explicit: str | None) -> str:
    candidate = explicit or os.environ.get("FFMPEG_EXE") or shutil.which("ffmpeg")
    if not candidate:
        raise MeasureError(
            "ffmpeg not found; pass --ffmpeg, set FFMPEG_EXE, or put ffmpeg on PATH")
    resolved = shutil.which(candidate) if not Path(candidate).is_file() else candidate
    if not resolved:
        raise MeasureError(f"ffmpeg not found: {candidate}")
    return resolved


def _decode_pcm(
    ffmpeg: str,
    audio_path: Path,
    sample_rate: int,
    *,
    timeout_seconds: float = 120.0,
) -> array:
    command = [
        ffmpeg,
        "-hide_banner",
        "-loglevel", "error",
        "-i", str(audio_path),
        "-f", "s16le",
        "-ac", "1",
        "-ar", str(sample_rate),
        "-",
    ]
    try:
        result = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=timeout_seconds,
        )
    except OSError as exc:
        raise MeasureError(f"cannot execute ffmpeg: {exc}") from exc
    except subprocess.TimeoutExpired as exc:
        raise MeasureError(f"ffmpeg timed out for {audio_path}") from exc
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", "replace").strip()
        raise MeasureError(f"ffmpeg failed for {audio_path}: {detail}")
    if not result.stdout:
        raise MeasureError(f"ffmpeg produced no PCM for {audio_path}")
    samples = array("h")
    samples.frombytes(result.stdout)
    if sys.byteorder != "little":
        samples.byteswap()
    return samples


def _rms_windows(
    samples: array,
    sample_rate: int,
    start_seconds: float,
    end_seconds: float,
    window_seconds: float,
) -> tuple[int, int, list[float]]:
    if sample_rate <= 0:
        raise MeasureError("sample_rate must be positive")
    if end_seconds <= start_seconds:
        raise MeasureError("source range must be positive")
    if window_seconds <= 0.0:
        raise MeasureError("window_seconds must be positive")
    sample_start = max(0, int(round(start_seconds * sample_rate)))
    sample_end = min(len(samples), int(round(end_seconds * sample_rate)))
    window_size = max(1, int(round(window_seconds * sample_rate)))
    rms_values: list[float] = []
    for offset in range(sample_start, sample_end - window_size + 1, window_size):
        window = samples[offset:offset + window_size]
        rms_values.append(math.sqrt(
            sum(float(value) * float(value) for value in window) / window_size))
    return sample_start, window_size, rms_values


def last_voiced_end_seconds(
    samples: array,
    sample_rate: int,
    start_seconds: float,
    end_seconds: float,
    *,
    window_seconds: float = WINDOW_SECONDS,
    peak_ratio: float = TAIL_PEAK_RATIO,
    abs_rms_floor: float = TAIL_ABS_RMS_FLOOR,
) -> float:
    """Return the end of the last energy-active window in a source range."""
    if not (0.0 <= peak_ratio <= 1.0):
        raise MeasureError("peak_ratio must be in [0, 1]")
    if abs_rms_floor < 0.0:
        raise MeasureError("abs_rms_floor must be >= 0")
    sample_start, window_size, rms_values = _rms_windows(
        samples, sample_rate, start_seconds, end_seconds, window_seconds)
    if not rms_values:
        sample_end = min(len(samples), int(round(end_seconds * sample_rate)))
        return max(start_seconds, min(end_seconds, sample_end / sample_rate))
    peak = max(rms_values)
    threshold = max(abs_rms_floor, peak * peak_ratio)
    last_active: int | None = None
    for index, value in enumerate(rms_values):
        if value > threshold:
            last_active = index
    if last_active is None:
        return start_seconds
    measured = (
        sample_start + last_active * window_size + window_size) / sample_rate
    return min(end_seconds, max(start_seconds, measured))


def first_voiced_start_seconds(
    samples: array,
    sample_rate: int,
    start_seconds: float,
    end_seconds: float,
    *,
    window_seconds: float = WINDOW_SECONDS,
    peak_ratio: float = START_PEAK_RATIO,
    abs_rms_floor: float = START_ABS_RMS_FLOOR,
    min_run_windows: int = START_MIN_RUN_WINDOWS,
) -> float:
    """Return the start of the first sustained energy run in a source range.

    A run is active when each 10ms window exceeds both ``abs_rms_floor`` and
    ``peak_ratio`` of the loudest window.  Requiring a short run avoids
    treating a single click or breath onset as speech.
    """
    if not (0.0 <= peak_ratio <= 1.0):
        raise MeasureError("peak_ratio must be in [0, 1]")
    if abs_rms_floor < 0.0:
        raise MeasureError("abs_rms_floor must be >= 0")
    if min_run_windows < 1:
        raise MeasureError("min_run_windows must be >= 1")
    sample_start, window_size, rms_values = _rms_windows(
        samples, sample_rate, start_seconds, end_seconds, window_seconds)
    if not rms_values:
        return start_seconds
    peak = max(rms_values)
    threshold = max(abs_rms_floor, peak * peak_ratio)
    run = 0
    for index, value in enumerate(rms_values):
        if value > threshold:
            run += 1
            if run >= min_run_windows:
                first_index = index - run + 1
                measured = (sample_start + first_index * window_size) / sample_rate
                return min(end_seconds, max(start_seconds, measured))
        else:
            run = 0
    return start_seconds


def _round_time(value: float) -> float:
    return round(float(value), 6)


def measure_timeline(
    tutorial_dir: Path,
    game_dir: Path,
    track: str,
    ffmpeg: str,
    *,
    guard_seconds: float | None = None,
    runtime_windows: bool = False,
) -> tuple[dict, list[dict]]:
    slices_path = tutorial_dir / f"{track}.audio-slices.json"
    document = _load_json(slices_path)
    if document.get("track") not in (None, track):
        raise MeasureError(f"{slices_path}: track mismatch")
    timing_points = document.get("timing_points")
    cues = document.get("cues")
    cue_timing = document.get("cue_timing")
    if not isinstance(timing_points, dict) or not isinstance(cues, dict) \
            or not isinstance(cue_timing, dict):
        raise MeasureError(
            f"{slices_path}: timing_points, cues and cue_timing are required")
    sample_rate = document.get("sample_rate")
    if isinstance(sample_rate, bool) or not isinstance(sample_rate, int) \
            or sample_rate <= 0:
        raise MeasureError(f"{slices_path}: sample_rate must be positive")

    runtime_by_id: dict[str, dict] = {}
    if runtime_windows:
        runtime_path = tutorial_dir / f"{track}.runtime.json"
        runtime = _load_json(runtime_path)
        for runtime_cue in runtime.get("cues") or []:
            if not isinstance(runtime_cue, dict):
                raise MeasureError(f"{runtime_path}: cue must be an object")
            cue_id = str(runtime_cue.get("id") or "").strip()
            if not cue_id:
                raise MeasureError(f"{runtime_path}: cue id is empty")
            runtime_by_id[cue_id] = runtime_cue

    pcm_cache: dict[str, array] = {}
    changes: list[dict] = []
    for cue_id, raw_slices in cues.items():
        if not isinstance(raw_slices, list) or not raw_slices:
            raise MeasureError(f"cue {cue_id!r}: slices must be non-empty")
        for raw_slice in raw_slices:
            if not isinstance(raw_slice, dict):
                raise MeasureError(f"cue {cue_id!r}: slice must be an object")
            asset = str(raw_slice.get("asset") or "").strip()
            if not asset:
                raise MeasureError(f"cue {cue_id!r}: asset is empty")
            if asset not in pcm_cache:
                pcm_cache[asset] = _decode_pcm(
                    ffmpeg, game_dir / asset, sample_rate)
            start_ref = str(raw_slice.get("start_ref") or "")
            end_ref = str(raw_slice.get("end_ref") or "")
            speech_ref = str(raw_slice.get("speech_end_ref") or "")
            speech_start_ref = str(
                raw_slice.get("speech_start_ref") or "").strip()
            if not speech_start_ref:
                slice_id = str(raw_slice.get("id") or "").strip()
                if not slice_id:
                    raise MeasureError(f"cue {cue_id!r}: slice id is required")
                speech_start_ref = f"{slice_id}.speech_start"
                raw_slice["speech_start_ref"] = speech_start_ref
            for ref in (start_ref, end_ref, speech_ref):
                point = timing_points.get(ref)
                if not isinstance(point, dict) or not isinstance(
                        point.get("time"), (int, float)):
                    raise MeasureError(
                        f"cue {cue_id!r}: missing timing point {ref!r}")
            if runtime_windows:
                runtime_cue = runtime_by_id.get(cue_id)
                if runtime_cue is None:
                    raise MeasureError(
                        f"cue {cue_id!r}: missing from runtime cues")
                source_start = float(runtime_cue.get("audio_start", 0.0))
                source_end = source_start + float(
                    runtime_cue.get("duration", 0.0))
                if source_end <= source_start:
                    raise MeasureError(
                        f"cue {cue_id!r}: invalid runtime audio window")
            else:
                source_start = float(timing_points[start_ref]["time"])
                source_end = float(timing_points[end_ref]["time"])
            start_point = timing_points.get(speech_start_ref)
            if not isinstance(start_point, dict):
                start_point = {"description": "measured speech start"}
                timing_points[speech_start_ref] = start_point
            old_speech_start = float(start_point.get("time", source_start))
            measured_start = first_voiced_start_seconds(
                pcm_cache[asset], sample_rate, source_start, source_end)
            new_speech_start = _round_time(measured_start)
            start_point["time"] = new_speech_start

            old_speech_end = float(timing_points[speech_ref]["time"])
            measured_end = last_voiced_end_seconds(
                pcm_cache[asset], sample_rate, source_start, source_end)
            new_speech_end = _round_time(measured_end)
            timing_points[speech_ref]["time"] = new_speech_end

            subtitle_changes = []
            for subtitle in raw_slice.get("subtitles") or []:
                if not isinstance(subtitle, dict):
                    continue
                subtitle_end_ref = str(subtitle.get("end_ref") or "")
                point = timing_points.get(subtitle_end_ref)
                if not isinstance(point, dict):
                    continue
                old_end = float(point["time"])
                if abs(old_end - old_speech_end) <= 1e-6:
                    point["time"] = new_speech_end
                    subtitle_changes.append(subtitle_end_ref)
            policy = cue_timing.setdefault(cue_id, {})
            if not isinstance(policy, dict):
                raise MeasureError(
                    f"cue {cue_id!r}: cue_timing must be an object")
            tail_guard_changed = False
            start_guard_changed = False
            old_tail_guard = 0.0
            old_start_guard = 0.0
            if guard_seconds is not None:
                old_tail_guard = float(
                    policy.get("trim_tail_guard_seconds", 0.0))
                old_start_guard = float(
                    policy.get("trim_start_guard_seconds", 0.0))
                policy["trim_tail_guard_seconds"] = float(guard_seconds)
                policy["trim_start_guard_seconds"] = float(guard_seconds)
                tail_guard_changed = (
                    abs(old_tail_guard - float(guard_seconds)) > EPSILON)
                start_guard_changed = (
                    abs(old_start_guard - float(guard_seconds)) > EPSILON)
            old_tail_measured = bool(
                policy.get("use_measured_speech_end", False))
            old_start_measured = bool(
                policy.get("use_measured_speech_start", False))
            policy["use_measured_speech_end"] = True
            policy["use_measured_speech_start"] = True
            policy["trim_to_speech_start"] = True
            tail_preserve_changed = not old_tail_measured
            start_preserve_changed = not old_start_measured
            changes.append({
                "cue_id": cue_id,
                "slice_id": str(raw_slice.get("id") or ""),
                "old_speech_start": old_speech_start,
                "new_speech_start": new_speech_start,
                "old_speech_end": old_speech_end,
                "new_speech_end": new_speech_end,
                "remaining_to_source_end": _round_time(
                    source_end - new_speech_end),
                "subtitle_refs": subtitle_changes,
                "old_tail_guard": old_tail_guard,
                "old_start_guard": old_start_guard,
                "guard": guard_seconds,
                "tail_guard_changed": tail_guard_changed,
                "start_guard_changed": start_guard_changed,
                "tail_preserve_changed": tail_preserve_changed,
                "start_preserve_changed": start_preserve_changed,
            })
    return document, changes


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--track", required=True)
    parser.add_argument("--ffmpeg", default=None)
    parser.add_argument(
        "--guard-seconds", type=float, default=None,
        help="guard before measured speech start and after measured speech end")
    parser.add_argument(
        "--runtime-windows",
        action="store_true",
        help="measure from runtime audio_start/duration instead of trimmed slice refs")
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    if not args.write and not args.check:
        parser.error("choose --check or --write")
    if args.guard_seconds is not None and args.guard_seconds < 0.0:
        parser.error("--guard-seconds must be >= 0")

    game_dir = ROOT / "content" / "games" / args.game.strip()
    tutorial_dir = game_dir / "tutorial"
    try:
        ffmpeg = _resolve_ffmpeg(args.ffmpeg)
        document, changes = measure_timeline(
            tutorial_dir, game_dir, args.track.strip(), ffmpeg,
            guard_seconds=args.guard_seconds,
            runtime_windows=args.runtime_windows)
    except MeasureError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    output_path = tutorial_dir / f"{args.track.strip()}.audio-slices.json"
    changed = any(
        abs(change["new_speech_start"] - change["old_speech_start"]) > EPSILON
        or abs(change["new_speech_end"] - change["old_speech_end"]) > EPSILON
        or change["tail_guard_changed"]
        or change["start_guard_changed"]
        or change["tail_preserve_changed"]
        or change["start_preserve_changed"]
        for change in changes)
    for change in changes:
        notes = []
        if change["start_guard_changed"] or change["tail_guard_changed"]:
            notes.append(
                f"start_guard {change['old_start_guard']:.3f} -> "
                f"{change['guard']:.3f}; "
                f"tail_guard {change['old_tail_guard']:.3f} -> "
                f"{change['guard']:.3f}")
        if change["start_preserve_changed"] or change["tail_preserve_changed"]:
            notes.append("mark measured")
        note = ("  " + "; ".join(notes)) if notes else ""
        print(
            f"{change['cue_id']:35} "
            f"start {change['old_speech_start']:7.3f} -> "
            f"{change['new_speech_start']:7.3f}  "
            f"end {change['old_speech_end']:7.3f} -> "
            f"{change['new_speech_end']:7.3f}  "
            f"tail={change['remaining_to_source_end']:6.3f}{note}")
    if args.check:
        if changed:
            print(f"FAIL {output_path}: measured boundaries differ")
            return 1
        print(f"OK   {output_path}: {len(changes)} measured boundaries match")
        return 0

    if changed:
        output_path.write_text(
            json.dumps(document, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")
        print(
            f"wrote {output_path} slices={len(changes)} "
            f"guard_seconds={args.guard_seconds}")
    else:
        print(f"OK   {output_path}: already up to date")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
