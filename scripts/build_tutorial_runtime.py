#!/usr/bin/env python3
"""
把 TTS 产物编译成运行时 cue 播放数据。

输入：
- games/{game}/tutorial/{track}.tts.lrc
- games/{game}/media/tts/{track}/tts_manifest.json
- games/{game}/media/tts/{track}/{cue_id}.subtitle.json

输出：
- games/{game}/tutorial/{track}.runtime.json

运行时文件只描述「播放什么」：cue 顺序、音频、时长、字幕、路径上下文。
动画数据后续按 cue id 挂到独立文件，不塞进这里。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from validate_timed_script import parse_file  # noqa: E402


def build_group_paths(groups: list[str]) -> dict[str, list[str]]:
    """根据 "3.1 xxx" 这样的数字前缀恢复层级路径。"""
    code_to_path: dict[str, list[str]] = {}
    title_to_path: dict[str, list[str]] = {}

    for title in groups:
        code_match = re.match(r"^([0-9]+(?:\.[0-9]+)*)\s+", title)
        code = code_match.group(1) if code_match else None

        if code and "." in code:
            parent_code = code.rsplit(".", 1)[0]
            parent_path = code_to_path.get(parent_code, [])
            path = parent_path + [title]
        else:
            path = [title]

        if code:
            code_to_path[code] = path
        title_to_path[title] = path

    return title_to_path


def load_subtitles(subtitle_path: Path, cue_text: str, duration: float) -> list[dict[str, Any]]:
    if not subtitle_path.exists():
        return [{"t": 0.0, "end": round(duration, 3), "text": cue_text, "words": []}]

    raw = json.loads(subtitle_path.read_text(encoding="utf-8"))
    events: list[dict[str, Any]] = []

    for event in raw:
        words = event.get("words") or []
        if words:
            starts = [float(w.get("startTime", 0.0)) for w in words]
            ends = [float(w.get("endTime", s)) for w, s in zip(words, starts)]
            start = min(starts)
            end = max(ends)
        else:
            start = float(event.get("startTime", 0.0))
            end = float(event.get("endTime", start))

        normalized_words = []
        for word in words:
            normalized_words.append({
                "word": word.get("word", ""),
                "start": round(float(word.get("startTime", 0.0)), 3),
                "end": round(float(word.get("endTime", 0.0)), 3),
                "confidence": round(float(word.get("confidence", 0.0)), 4),
            })

        events.append({
            "t": round(start, 3),
            "end": round(max(end, start), 3),
            "text": event.get("text", ""),
            "words": normalized_words,
        })

    events.sort(key=lambda x: x["t"])
    return events


def normalize_relative(path_value: str, game_dir: Path) -> str:
    """把 manifest 里的路径统一成相对 game_dir 的路径。

    manifest 路径可能是：
    - 相对仓库根：games/splendor/media/...
    - 相对游戏目录：media/tts/...
    """
    raw = Path(path_value)
    candidates = [raw] if raw.is_absolute() else [ROOT / raw, game_dir / raw]
    game_dir = game_dir.resolve()
    for candidate in candidates:
        try:
            return str(candidate.resolve().relative_to(game_dir)).replace("\\", "/")
        except ValueError:
            continue
    return path_value.replace("\\", "/")


def build_runtime(game: str, track: str, force: bool = False) -> Path:
    game_dir = ROOT / "games" / game
    lrc_path = game_dir / "tutorial" / f"{track}.tts.lrc"
    manifest_path = game_dir / "media" / "tts" / track / "tts_manifest.json"
    output_path = game_dir / "tutorial" / f"{track}.runtime.json"

    if not lrc_path.exists():
        raise SystemExit(f"missing lrc: {lrc_path}")
    if not manifest_path.exists():
        raise SystemExit(f"missing manifest: {manifest_path}")

    doc = parse_file(lrc_path)
    errors = [issue for issue in doc["issues"] if issue["level"] == "error"]
    if errors:
        raise SystemExit(f"lrc validation failed: {errors}")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest_by_id = {cue["id"]: cue for cue in manifest["cues"]}
    group_paths = build_group_paths(doc["groups"])

    cues: list[dict[str, Any]] = []
    seen: set[str] = set()
    for cue in doc["cues"]:
        cue_id = cue["id"]
        if cue_id in seen:
            raise SystemExit(f"duplicate cue id: {cue_id}")
        seen.add(cue_id)

        manifest_cue = manifest_by_id.get(cue_id)
        if manifest_cue is None:
            raise SystemExit(f"missing manifest cue: {cue_id}")

        audio_rel = normalize_relative(manifest_cue["file"], game_dir)
        subtitle_rel = normalize_relative(manifest_cue["subtitle_file"], game_dir) if manifest_cue.get("subtitle_file") else None
        audio_path = game_dir / audio_rel
        if not audio_path.exists():
            raise SystemExit(f"missing audio: {audio_path}")

        duration = float(manifest_cue["duration"])
        subtitles = load_subtitles(
            game_dir / subtitle_rel if subtitle_rel else Path(),
            cue["text"],
            duration,
        )

        cues.append({
            "id": cue_id,
            "group": cue["group"],
            "group_path": group_paths.get(cue["group"] or "", []),
            "text": cue["text"],
            "audio": audio_rel,
            "duration": round(duration, 3),
            "start": round(float(cue["start"]), 3),
            "refs": cue["refs"],
            "subtitles": subtitles,
            "animation": None,
        })

    runtime = {
        "schema_version": 1,
        "game_id": game,
        "track": track,
        "title": doc["meta"].get("ti", ""),
        "voice": manifest.get("voice", ""),
        "format": manifest.get("format", "mp3"),
        "sample_rate": manifest.get("sample_rate", 24000),
        "generator": "scripts/build_tutorial_runtime.py",
        "cues": cues,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    if output_path.exists() and not force:
        raise SystemExit(f"output exists, use --force: {output_path}")
    output_path.write_text(json.dumps(runtime, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"[runtime] {output_path}")
    print(f"cues: {len(cues)}")
    print(f"duration: {sum(c['duration'] for c in cues):.2f}s")
    return output_path


def main() -> int:
    parser = argparse.ArgumentParser(description="Build runtime cue player data from TTS assets.")
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    try:
        build_runtime(args.game, args.track, force=args.force)
    except SystemExit:
        raise
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
