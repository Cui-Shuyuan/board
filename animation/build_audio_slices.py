#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build a fixed audio-slice timeline from a generated section runtime.

This is a build-time adapter for section-level TTS tracks.  It never runs in
the player.  The runtime document provides cue order, cue-local subtitle data
and the declared audio source window; the fixed output file becomes the sole
source of timing refs and source ranges consumed by build_cue_timeline.py.

Usage:
    python3 animation/build_audio_slices.py --game splendor --track full
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from collections import OrderedDict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
SCHEMA = "tutorial-audio-slices/v1"
SCHEMA_VERSION = 4


class BuildError(RuntimeError):
    pass


def _load_json(path: Path) -> dict:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise BuildError(f"missing input file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise BuildError(f"invalid JSON in {path}: {exc}") from exc
    if not isinstance(document, dict):
        raise BuildError(f"{path}: expected a JSON object")
    return document


def _slug(value: str) -> str:
    text = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip().replace("/", "__"))
    text = text.strip("_")
    if not text:
        raise BuildError(f"invalid cue/slice id for slug: {value!r}")
    return text


def _finite(value: Any, path: str, *, minimum: float = 0.0) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BuildError(f"{path}: expected a finite number")
    number = float(value)
    if not math.isfinite(number) or number < minimum:
        raise BuildError(f"{path}: expected a finite number >= {minimum:g}")
    return number


def _round(value: float) -> float:
    return round(float(value), 9)


def _normalize_asset(raw: Any, game_dir: Path, game: str, path: str) -> str:
    text = str(raw or "").strip().replace("\\", "/")
    if not text:
        raise BuildError(f"{path}: audio asset is empty")
    prefix = f"content/games/{game}/"
    if text.startswith(prefix):
        text = text[len(prefix):]
    if text.startswith("/") or ".." in text.split("/"):
        raise BuildError(f"{path}: audio asset must be game-relative: {text!r}")
    if not (game_dir / text).is_file():
        raise BuildError(f"{path}: audio file not found: {text}")
    return text


def build_audio_slices(repo_root: Path, game: str, track: str) -> dict:
    game_dir = repo_root / "content" / "games" / game
    tutorial_dir = game_dir / "tutorial"
    runtime_path = tutorial_dir / f"{track}.runtime.json"
    compiled_path = tutorial_dir / "anim" / "v2" / f"{track}.compiled.json"
    runtime = _load_json(runtime_path)
    compiled = _load_json(compiled_path)
    output_path = tutorial_dir / f"{track}.audio-slices.json"
    existing = _load_json(output_path) if output_path.is_file() else {}
    existing_cue_timing = existing.get("cue_timing", {})
    if not isinstance(existing_cue_timing, dict):
        raise BuildError(f"{output_path}: cue_timing must be an object")

    if runtime.get("game_id") not in (None, game):
        raise BuildError(f"{runtime_path}: game_id mismatch")
    if runtime.get("track") not in (None, track):
        raise BuildError(f"{runtime_path}: track mismatch")
    raw_cues = runtime.get("cues")
    if not isinstance(raw_cues, list) or not raw_cues:
        raise BuildError(f"{runtime_path}: cues must be a non-empty array")
    sample_rate = runtime.get("sample_rate")
    if isinstance(sample_rate, bool) or not isinstance(sample_rate, int) \
            or sample_rate <= 0:
        raise BuildError(f"{runtime_path}: sample_rate must be positive")

    compiled_by_id = {
        str(cue.get("id")): cue
        for cue in compiled.get("cues") or []
        if isinstance(cue, dict) and cue.get("id")
    }

    timing_points: dict[str, dict] = {}
    cues: dict[str, list[dict]] = OrderedDict()
    cue_visuals: dict[str, list[dict]] = OrderedDict()
    groups: dict[str, dict] = OrderedDict()
    seen_cue_ids: set[str] = set()
    seen_slice_ids: set[str] = set()

    for index, raw_cue in enumerate(raw_cues):
        path = f"runtime.cues[{index}]"
        if not isinstance(raw_cue, dict):
            raise BuildError(f"{path}: expected an object")
        cue_id = str(raw_cue.get("id") or "").strip()
        if not cue_id:
            raise BuildError(f"{path}.id is empty")
        if cue_id in seen_cue_ids:
            raise BuildError(f"{path}.id: duplicate cue id {cue_id!r}")
        seen_cue_ids.add(cue_id)

        audio_start = _finite(raw_cue.get("audio_start"), f"{path}.audio_start")
        duration = _finite(raw_cue.get("duration"), f"{path}.duration",
                           minimum=1e-9)
        audio_end = _round(audio_start + duration)
        asset = _normalize_asset(
            raw_cue.get("audio"), game_dir, game, f"{path}.audio")
        slice_id = _slug(cue_id)
        if slice_id in seen_slice_ids:
            raise BuildError(f"{path}: duplicate generated slice id {slice_id!r}")
        seen_slice_ids.add(slice_id)

        subtitles: list[dict] = []
        speech_end: float | None = None
        raw_subtitles = raw_cue.get("subtitles") or []
        if not isinstance(raw_subtitles, list):
            raise BuildError(f"{path}.subtitles must be an array")
        for subtitle_index, raw_subtitle in enumerate(raw_subtitles):
            sub_path = f"{path}.subtitles[{subtitle_index}]"
            if not isinstance(raw_subtitle, dict):
                raise BuildError(f"{sub_path}: expected an object")
            start = audio_start + _finite(
                raw_subtitle.get("t"), f"{sub_path}.t")
            end = audio_start + _finite(
                raw_subtitle.get("end"), f"{sub_path}.end")
            if end <= start:
                raise BuildError(f"{sub_path}: subtitle range must be positive")
            if start < audio_start - 1e-9 or end > audio_end + 1e-9:
                raise BuildError(
                    f"{sub_path}: subtitle range is outside cue audio window")
            start_ref = f"{slice_id}.subtitle.{subtitle_index}.start"
            end_ref = f"{slice_id}.subtitle.{subtitle_index}.end"
            timing_points[start_ref] = {
                "time": _round(start), "description": "subtitle start"}
            timing_points[end_ref] = {
                "time": _round(end), "description": "subtitle end"}
            subtitles.append({
                "start_ref": start_ref,
                "end_ref": end_ref,
                "text": str(raw_subtitle.get("text") or ""),
            })
            for word in raw_subtitle.get("words") or []:
                if not isinstance(word, dict):
                    continue
                word_end = audio_start + _finite(
                    word.get("end"),
                    f"{sub_path}.words[].end")
                bounded = min(audio_end, word_end)
                speech_end = bounded if speech_end is None else max(
                    speech_end, bounded)

        start_ref = f"{slice_id}.start"
        end_ref = f"{slice_id}.end"
        speech_ref = f"{slice_id}.speech_end"
        timing_points[start_ref] = {
            "time": _round(audio_start), "description": "logical slice start"}
        timing_points[end_ref] = {
            "time": audio_end, "description": "logical slice end"}
        timing_points[speech_ref] = {
            "time": _round(audio_end if speech_end is None else speech_end),
            "description": "last TTS word end"}
        cues[cue_id] = [{
            "id": slice_id,
            "asset": asset,
            "start_ref": start_ref,
            "end_ref": end_ref,
            "speech_end_ref": speech_ref,
            "subtitles": subtitles,
        }]

        group_id = str(raw_cue.get("section") or "").strip()
        if group_id:
            group = groups.setdefault(group_id, {
                "cue_ids": [], "slice_ids": [], "assets": set()})
            group["cue_ids"].append(cue_id)
            group["slice_ids"].append(slice_id)
            group["assets"].add(asset)

        compiled_cue = compiled_by_id.get(cue_id)
        if compiled_cue is not None:
            visual_duration = float(compiled_cue.get("duration", 0.0) or 0.0)
            visual_start_ref = f"{slice_id}.visual.cue_start"
            timing_points[visual_start_ref] = {
                "time": 0.0, "description": "visual cue-local start"}
            if visual_duration > 0.0:
                visual_source_start_ref = f"{slice_id}.visual.source_start"
                visual_source_end_ref = f"{slice_id}.visual.source_end"
                timing_points[visual_source_start_ref] = {
                    "time": 0.0, "description": "visual source start"}
                timing_points[visual_source_end_ref] = {
                    "time": _round(visual_duration),
                    "description": "visual source end",
                }
                cue_visuals[cue_id] = [{
                    "id": f"visual.{slice_id}",
                    "type": "animation",
                    "animation": cue_id,
                    "cue_start_ref": visual_start_ref,
                    "source_start_ref": visual_source_start_ref,
                    "source_end_ref": visual_source_end_ref,
                    "after_end": "hold_last_frame",
                }]
            else:
                # Zero-duration compiled cues still contain instant
                # state/overlay changes that must be applied.
                cue_visuals[cue_id] = [{
                    "id": f"visual.{slice_id}",
                    "type": "state",
                    "animation": cue_id,
                    "cue_start_ref": visual_start_ref,
                    "after_end": "hold_last_frame",
                }]

    audio_groups: dict[str, dict] = {}
    for group_id, group in groups.items():
        if len(group["assets"]) != 1:
            raise BuildError(
                f"runtime section {group_id!r} spans multiple audio assets")
        audio_groups[group_id] = {
            "cue_ids": group["cue_ids"],
            "slice_ids": group["slice_ids"],
        }

    return {
        "schema": SCHEMA,
        "schema_version": SCHEMA_VERSION,
        "game_id": game,
        "track": track,
        "sample_rate": sample_rate,
        # Per-cue policies are authored in the fixed file and must survive
        # regeneration from runtime; otherwise title trim rules silently
        # revert to raw next-cue audio boundaries.
        "cue_timing": {
            cue_id: policy for cue_id, policy in existing_cue_timing.items()
            if cue_id in seen_cue_ids
        },
        "audio_groups": audio_groups,
        "timing_points": timing_points,
        "cues": cues,
        "cue_visuals": cue_visuals,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--track", required=True)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    args = parser.parse_args(argv)
    try:
        document = build_audio_slices(
            args.repo_root.resolve(), args.game.strip(), args.track.strip())
        output = (
            args.repo_root.resolve() / "content" / "games" / args.game.strip()
            / "tutorial" / f"{args.track.strip()}.audio-slices.json")
        output.write_text(
            json.dumps(document, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")
    except (BuildError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    print(
        f"wrote {output} cues={len(document['cues'])} "
        f"groups={len(document['audio_groups'])} "
        f"timing_points={len(document['timing_points'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
