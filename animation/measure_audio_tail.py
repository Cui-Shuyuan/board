#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Measure audible-tail timestamps for fixed audio slices.

The TTS subtitle event's final word end can be earlier than the real PCM
speech tail (observed with Doubao seed2 quick cue-level audio).  This tool
decodes the referenced audio to PCM, measures the last meaningful energy
window inside each authored source range, and writes the result back as the
slice's ``speech_end_ref`` timestamp.  Playback and trim never run energy
analysis; they only consume the stored timestamps.

Usage:
    python3 animation/measure_audio_tail.py \
        --game splendor --track quick \
        --ffmpeg /path/to/ffmpeg \
        --write --guard-seconds 0.1

The optional ``--guard-seconds`` also updates each analyzed cue's
``trim_tail_guard_seconds`` policy.  The guard stays outside the stored
speech-end timestamp, so the fixed timeline remains a plain timestamp file.
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
PEAK_RATIO = 0.005
ABS_RMS_FLOOR = 60.0
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


def last_voiced_end_seconds(
    samples: array,
    sample_rate: int,
    start_seconds: float,
    end_seconds: float,
    *,
    window_seconds: float = WINDOW_SECONDS,
    peak_ratio: float = PEAK_RATIO,
    abs_rms_floor: float = ABS_RMS_FLOOR,
) -> float:
    """Return the end of the last energy-active window in a source range.

    A window is active when its RMS exceeds both ``abs_rms_floor`` and
    ``peak_ratio`` of the loudest window in the range.  The returned value is
    clamped to the authored source range.
    """
    if sample_rate <= 0:
        raise MeasureError("sample_rate must be positive")
    if end_seconds <= start_seconds:
        raise MeasureError("source range must be positive")
    if window_seconds <= 0.0:
        raise MeasureError("window_seconds must be positive")
    if not (0.0 <= peak_ratio <= 1.0):
        raise MeasureError("peak_ratio must be in [0, 1]")
    if abs_rms_floor < 0.0:
        raise MeasureError("abs_rms_floor must be >= 0")

    sample_start = max(0, int(round(start_seconds * sample_rate)))
    sample_end = min(len(samples), int(round(end_seconds * sample_rate)))
    window_size = max(1, int(round(window_seconds * sample_rate)))
    if sample_end - sample_start < window_size:
        return max(start_seconds, min(end_seconds, sample_end / sample_rate))

    rms_values: list[float] = []
    for offset in range(sample_start, sample_end - window_size + 1, window_size):
        window = samples[offset:offset + window_size]
        rms_values.append(math.sqrt(
            sum(float(value) * float(value) for value in window) / window_size))
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


def _round_time(value: float) -> float:
    return round(float(value), 6)


def measure_timeline(
    tutorial_dir: Path,
    game_dir: Path,
    track: str,
    ffmpeg: str,
    *,
    guard_seconds: float | None = None,
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
            for ref in (start_ref, end_ref, speech_ref):
                point = timing_points.get(ref)
                if not isinstance(point, dict) or not isinstance(
                        point.get("time"), (int, float)):
                    raise MeasureError(
                        f"cue {cue_id!r}: missing timing point {ref!r}")
            source_start = float(timing_points[start_ref]["time"])
            source_end = float(timing_points[end_ref]["time"])
            old_speech_end = float(timing_points[speech_ref]["time"])
            measured = last_voiced_end_seconds(
                pcm_cache[asset], sample_rate, source_start, source_end)
            new_speech_end = _round_time(measured)
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
            guard_changed = False
            old_guard = 0.0
            if guard_seconds is not None:
                policy = cue_timing.setdefault(cue_id, {})
                if not isinstance(policy, dict):
                    raise MeasureError(
                        f"cue {cue_id!r}: cue_timing must be an object")
                old_guard = float(policy.get("trim_tail_guard_seconds", 0.0))
                policy["trim_tail_guard_seconds"] = float(guard_seconds)
                guard_changed = abs(old_guard - float(guard_seconds)) > EPSILON
            changes.append({
                "cue_id": cue_id,
                "slice_id": str(raw_slice.get("id") or ""),
                "old_speech_end": old_speech_end,
                "new_speech_end": new_speech_end,
                "remaining_to_source_end": _round_time(
                    source_end - new_speech_end),
                "subtitle_refs": subtitle_changes,
                "old_guard": old_guard,
                "guard": guard_seconds,
                "guard_changed": guard_changed,
            })
    return document, changes


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--track", required=True)
    parser.add_argument("--ffmpeg", default=None)
    parser.add_argument("--guard-seconds", type=float, default=None)
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
            guard_seconds=args.guard_seconds)
    except MeasureError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    output_path = tutorial_dir / f"{args.track.strip()}.audio-slices.json"
    changed = any(
        abs(change["new_speech_end"] - change["old_speech_end"]) > EPSILON
        or change["guard_changed"]
        for change in changes)
    for change in changes:
        guard_note = ""
        if change["guard_changed"]:
            guard_note = f"  guard {change['old_guard']:.3f} -> {change['guard']:.3f}"
        print(
            f"{change['cue_id']:35} "
            f"speech_end {change['old_speech_end']:7.3f} -> "
            f"{change['new_speech_end']:7.3f}  "
            f"tail={change['remaining_to_source_end']:6.3f}{guard_note}")
    if args.check:
        if changed:
            print(f"FAIL {output_path}: measured tail anchors differ")
            return 1
        print(f"OK   {output_path}: {len(changes)} tail anchors match")
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
