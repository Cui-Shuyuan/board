#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build-time slicing for audio groups shared by serial cues.

Usage:
    python animation/split_shared_cue_audio.py --game splendor --track quick

The timing policy's ``audio_groups`` describe one TTS output that covers
multiple serial cues.  This tool reads the TTS word timings, cuts the source
audio at cue boundaries (inside the inter-word silence, not by waveform search),
and writes one standalone WAV per cue.  The plan then references whole files
starting at 0.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import soundfile as sf

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import build_cue_timeline as builder  # noqa: E402
from section_source import cue_id_to_slug  # noqa: E402

SLICES_SCHEMA = "tutorial-cue-audio-slices/v1"
SLICES_VERSION = 1


def _load_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path}: expected JSON object")
    return value


def _load_raw_subtitle_events(game_dir: Path, game: str, raw_path: str | None) -> list:
    if not raw_path:
        return []
    rel = builder._normalize_game_relative_path(raw_path, game_dir, game)
    value = json.loads((game_dir / rel).read_text(encoding="utf-8"))
    return value if isinstance(value, list) else []


def split_track(repo_root: Path, game: str, track: str) -> dict:
    game_dir = repo_root / "content" / "games" / game
    policy = builder._load_timing_policy(repo_root, game, track)
    if str(policy.get("audio_source") or "") != "cue_tts_manifest":
        raise ValueError("timing policy audio_source must be cue_tts_manifest")
    tts_manifest_rel = str(policy.get("tts_manifest") or "").strip()
    script_rel = str(policy.get("script_source") or "").strip()
    if not tts_manifest_rel or not script_rel:
        raise ValueError("timing policy requires tts_manifest and script_source")
    tts_manifest = _load_json(game_dir / tts_manifest_rel)
    script = _load_json(game_dir / script_rel)
    tts_by_beat = {
        str(entry.get("id")): entry
        for entry in tts_manifest.get("cues") or []
        if isinstance(entry, dict) and entry.get("id")
    }
    cue_texts = {
        str(cue.get("id")): "".join(
            str(beat.get("text") or "")
            for beat in (cue.get("beats") or []) if isinstance(beat, dict)
        )
        for cue in script.get("cues") or [] if isinstance(cue, dict)
    }
    sample_rate = int(tts_manifest.get("sample_rate") or 24000)
    output_dir = game_dir / "media" / "tts" / f"{track}.cues" / "split"
    output_dir.mkdir(parents=True, exist_ok=True)

    entries: list[dict] = []
    groups: list[dict] = []
    for group_id, raw_group in (policy.get("audio_groups") or {}).items():
        if not isinstance(raw_group, dict):
            raise ValueError(f"audio group {group_id!r} must be an object")
        cue_ids = [str(value) for value in (raw_group.get("cue_ids") or [])]
        beat_id = str(raw_group.get("beat_id") or "").strip()
        if not cue_ids or not beat_id:
            raise ValueError(f"audio group {group_id!r} is incomplete")
        tts_entry = tts_by_beat.get(beat_id)
        if tts_entry is None:
            raise ValueError(f"missing TTS entry for group {group_id!r}: {beat_id}")
        source_rel = builder._normalize_game_relative_path(
            str(tts_entry.get("file") or ""), game_dir, game)
        subtitle_events = _load_raw_subtitle_events(
            game_dir, game, tts_entry.get("subtitle_file"))
        duration = float(tts_entry.get("duration", 0.0))
        if not math.isfinite(duration) or duration <= 0.0:
            raise ValueError(f"invalid TTS duration for group {group_id!r}")
        ranges = builder._shared_group_ranges(
            cue_ids, cue_texts, duration, subtitle_events)

        audio, source_rate = sf.read(game_dir / source_rel, dtype="float32")
        if source_rate != sample_rate:
            raise ValueError(
                f"sample-rate mismatch for {source_rel}: manifest={sample_rate}, file={source_rate}")
        for cue_id in cue_ids:
            start, end = ranges[cue_id]
            start_frame = max(0, int(round(start * source_rate)))
            end_frame = min(len(audio), int(round(end * source_rate)))
            if end_frame <= start_frame:
                raise ValueError(f"empty audio slice for cue {cue_id!r}")
            output_path = output_dir / f"{cue_id_to_slug(cue_id)}.wav"
            sf.write(
                str(output_path),
                audio[start_frame:end_frame],
                source_rate,
                format="WAV",
                subtype="PCM_16",
            )
            relative = output_path.relative_to(game_dir).as_posix()
            entries.append({
                "cue_id": cue_id,
                "group_id": group_id,
                "source_file": source_rel,
                "source_start": round(start, 9),
                "source_end": round(end, 9),
                "file": relative,
                "duration": round((end_frame - start_frame) / float(source_rate), 9),
            })
        groups.append({
            "group_id": group_id,
            "beat_id": beat_id,
            "cue_ids": cue_ids,
            "source_file": source_rel,
            "source_duration": round(duration, 9),
        })

    manifest = {
        "schema": SLICES_SCHEMA,
        "schema_version": SLICES_VERSION,
        "game_id": game,
        "track": track,
        "format": "wav",
        "sample_rate": sample_rate,
        "groups": groups,
        "cues": entries,
    }
    manifest_path = output_dir / "slices_manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"wrote {manifest_path} groups={len(groups)} cues={len(entries)}"
    )
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--track", required=True)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    args = parser.parse_args(argv)
    try:
        split_track(args.repo_root.resolve(), args.game.strip(), args.track.strip())
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
