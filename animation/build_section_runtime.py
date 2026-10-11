#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build a track runtime where one continuous TTS passage serves one section.

The section TTS manifest stores one entry per section.  This module expands that
back into per-cue runtime entries so the existing native timeline and Unity
playback/seek interfaces can keep using cue ids and cue-local seconds, while the
audio clock itself stays continuous inside each section.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))

from section_source import load_track_source, tutorial_dir  # noqa: E402
from build_tutorial_runtime import load_subtitles, normalize_relative  # noqa: E402


def key_text(text: str) -> str:
    return "".join(ch for ch in str(text or "") if ch.isalnum())


def cue_text(cue: dict) -> str:
    return "".join((b.get("text") or "") for b in cue.get("beats") or [])


def _load_section_subtitles(game_dir: Path, cue_entry: dict, duration: float) -> list[dict]:
    rel = cue_entry.get("subtitle_file")
    path = game_dir / normalize_relative(rel, game_dir) if rel else Path()
    if path and path.exists():
        events = load_subtitles(path, "", duration)
        # load_subtitles returns normalized t/end/text/words already.
        return [e for e in events if e.get("words")]
    return []


def _word_stream(events: list[dict]) -> list[dict]:
    words: list[dict] = []
    for event in events:
        words.extend(event.get("words") or [])
    words.sort(key=lambda w: (float(w.get("start", 0.0) or 0.0), float(w.get("end", 0.0) or 0.0)))
    return words


def _match_cue_starts(cue_texts: list[str], words: list[dict], duration: float) -> list[float]:
    """Return cue audio-relative starts.  First cue always starts at 0."""
    if not cue_texts:
        return []
    if not words:
        # No subtitle timing: distribute by text weight.
        weights = [max(1, len(key_text(t))) for t in cue_texts]
        total = sum(weights)
        starts = []
        cursor = 0.0
        for weight in weights:
            starts.append(cursor)
            cursor += duration * weight / total
        return starts

    flat_parts: list[str] = []
    char_word: list[int] = []
    for wi, word in enumerate(words):
        piece = key_text(word.get("word", ""))
        flat_parts.append(piece)
        char_word.extend([wi] * len(piece))
    flat = "".join(flat_parts)

    starts: list[float] = []
    cursor = 0
    for index, text in enumerate(cue_texts):
        needle = key_text(text)
        if not needle:
            starts.append(starts[-1] if starts else 0.0)
            continue
        pos = flat.find(needle, cursor)
        if pos < 0:
            pos = flat.find(needle)
        if pos < 0:
            # Fallback: proportional continuation from the current cursor.
            remaining = cue_texts[index:]
            weights = [max(1, len(key_text(t))) for t in remaining]
            tail = duration - (starts[-1] if starts else 0.0)
            starts.append((starts[-1] if starts else 0.0))
            starts[-1] += 0.0
            # Use a conservative linear split; later cues still have words.
            if len(starts) > 1:
                starts[-1] = starts[-2] + 0.0
            continue
        word_index = char_word[pos]
        starts.append(0.0 if index == 0 else float(words[word_index].get("start", 0.0) or 0.0))
        cursor = pos + len(needle)
    # Guard against out-of-order/zero starts caused by imperfect matches.
    for i in range(1, len(starts)):
        if starts[i] <= starts[i - 1]:
            starts[i] = starts[i - 1] + 0.001
    return starts


def _slice_subtitles(events: list[dict], cue_start: float, cue_end: float) -> list[dict]:
    out: list[dict] = []
    for event in events:
        event_start = float(event.get("t", 0.0) or 0.0)
        event_end = float(event.get("end", event_start) or event_start)
        if event_end <= cue_start + 1e-6 or event_start >= cue_end - 1e-6:
            continue
        t = max(event_start, cue_start) - cue_start
        end = min(event_end, cue_end) - cue_start
        words: list[dict] = []
        for word in event.get("words") or []:
            ws = float(word.get("start", 0.0) or 0.0)
            we = float(word.get("end", ws) or ws)
            if we <= cue_start + 1e-6 or ws >= cue_end - 1e-6:
                continue
            words.append({
                "word": word.get("word", ""),
                "start": round(max(ws, cue_start) - cue_start, 3),
                "end": round(max(we, cue_start) - cue_start, 3),
                "confidence": float(word.get("confidence", 0.0) or 0.0),
            })
        if not words and event.get("text"):
            # Keep a timing shell so subtitle display still works.
            words = []
        out.append({
            "t": round(t, 3),
            "end": round(max(end, t), 3),
            "text": event.get("text", ""),
            "words": words,
        })
    return out


def _pseudo_words(text: str, duration: float) -> list[dict]:
    chars = [ch for ch in str(text or "") if ch.isalnum()]
    if not chars:
        return []
    step = max(0.08, float(duration) / max(1, len(chars)))
    out = []
    for i, ch in enumerate(chars):
        out.append({
            "word": ch,
            "start": round(i * step, 3),
            "end": round((i + 1) * step, 3),
            "confidence": 0.0,
        })
    return out


def build_section_runtime(game: str, track: str, *, force: bool = False,
                          manifest: dict | None = None) -> Path:
    game_dir = ROOT / "content" / "games" / game
    manifest_path = game_dir / "media" / "tts" / track / "tts_manifest.json"
    output_path = game_dir / "tutorial" / f"{track}.runtime.json"
    if manifest is None:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    source = load_track_source(game, track)

    manifest_by = {
        str(c.get("id")): c
        for c in manifest.get("cues") or []
        if isinstance(c, dict) and c.get("id")
    }
    gap = float(manifest.get("gap_seconds", 0.0) or 0.0)

    # Source cue lookup by section, in authoring order.
    source_cues_by_section: dict[str, list[dict]] = {}
    for cue in source.script_doc.get("cues") or []:
        if not isinstance(cue, dict) or not cue.get("id"):
            continue
        source_cues_by_section.setdefault(str(cue.get("section") or ""), []).append(cue)

    sections_out: list[dict[str, Any]] = []
    cues_out: list[dict[str, Any]] = []
    section_cursor = 0.0
    global_index = 0
    for sec in source.sections:
        entry = manifest_by.get(sec.id)
        if entry is None:
            raise SystemExit(f"missing section TTS manifest entry: {sec.id}")
        source_cues = source_cues_by_section.get(sec.id) or []
        if not source_cues:
            raise SystemExit(f"section {sec.id}: no script cues")
        audio_rel = normalize_relative(str(entry.get("file") or ""), game_dir)
        audio_path = game_dir / audio_rel
        if not audio_path.exists():
            raise SystemExit(f"missing section audio: {audio_path}")
        duration = float(entry.get("duration", 0.0) or 0.0)
        if duration <= 0.0:
            raise SystemExit(f"section {sec.id}: invalid TTS duration")
        events = _load_section_subtitles(game_dir, entry, duration)
        cue_texts = [cue_text(c) for c in source_cues]
        starts = _match_cue_starts(cue_texts, _word_stream(events), duration)
        section_start = section_cursor

        section_meta = {
            "id": sec.id,
            "title": str(sec.meta.get("title") or ""),
            "group_path": list(sec.meta.get("group_path") or []),
            "order": sec.order,
            "asset": str(entry.get("asset") or sec.id),
            "audio": audio_rel,
            "start": round(section_start, 3),
            "duration": round(duration, 3),
            "cue_ids": [str(c.get("id")) for c in source_cues],
        }
        sections_out.append(section_meta)

        for i, (source_cue, cue_start) in enumerate(zip(source_cues, starts)):
            local_end = starts[i + 1] if i + 1 < len(starts) else duration
            cue_end = max(cue_start + 0.001, local_end)
            cue_duration = max(0.001, cue_end - cue_start)
            text = cue_texts[i]
            subtitles = _slice_subtitles(events, cue_start, cue_end)
            if not subtitles:
                subtitles = [{
                    "t": 0.0,
                    "end": round(cue_duration, 3),
                    "text": text,
                    "words": _pseudo_words(text, cue_duration),
                }]
            group = source_cue.get("group") or sec.meta.get("title") or sec.id
            group_path = source_cue.get("group_path") or sec.meta.get("group_path") or [group]
            cues_out.append({
                "id": str(source_cue.get("id")),
                "section": sec.id,
                "local_id": str(source_cue.get("local_id") or source_cue.get("id")),
                "section_index": sec.order,
                "local_index": i,
                "cue_index": global_index,
                "group": group,
                "group_path": list(group_path),
                "text": text,
                "audio": audio_rel,
                "audio_start": round(cue_start, 3),
                "duration": round(cue_duration, 3),
                "start": round(section_start + cue_start, 3),
                "refs": list(source_cue.get("refs") or []),
                "subtitles": subtitles,
                "animation": None,
                "section_audio": True,
            })
            global_index += 1

        section_cursor = section_start + duration + gap

    runtime = {
        "schema_version": 2,
        "game_id": game,
        "track": track,
        "title": source.script_doc.get("title") or f"{game} {track}",
        "voice": manifest.get("voice", ""),
        "format": manifest.get("format", "mp3"),
        "sample_rate": manifest.get("sample_rate", 24000),
        "generator": "animation/build_section_runtime.py",
        "units": "section",
        "sections": sections_out,
        "cues": cues_out,
    }
    if output_path.exists() and not force:
        raise SystemExit(f"output exists, use --force: {output_path}")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(runtime, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"[runtime] {output_path}")
    print(f"sections: {len(sections_out)}  cues: {len(cues_out)}")
    print(f"duration: {section_cursor:.2f}s")
    return output_path
