#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Assemble a track's monolithic source files from section directories.

Source layout (track descriptor present):
  content/games/{game}/tutorial/tracks/{track}.track.json
  content/games/{game}/tutorial/shared/{track}.base.anim.json
  content/games/{game}/tutorial/sections/{section_id}/{track}.anim.json
  content/games/{game}/tutorial/sections/{section_id}/{track}.script.json

The compiler and runtime still consume the existing monolithic files.  This
module keeps that compatibility while making sections the authoring unit.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent


def _read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _write_json(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    tmp.replace(path)


def track_descriptor_path(game: str, track: str) -> Path:
    return ROOT / "content" / "games" / game / "tutorial" / "tracks" / f"{track}.track.json"


def _resolve(base: Path, value: str) -> Path:
    p = Path(value)
    return p if p.is_absolute() else (base / p).resolve()


def assemble_track(game: str, track: str, *, write: bool = True) -> tuple[dict, dict] | None:
    descriptor_path = track_descriptor_path(game, track)
    if not descriptor_path.is_file():
        return None

    tutorial = descriptor_path.parent.parent
    descriptor = _read_json(descriptor_path)
    base_anim_path = _resolve(descriptor_path.parent, descriptor.get("base_anim", ""))
    base_anim = _read_json(base_anim_path)

    anim: dict[str, Any] = {
        k: base_anim[k]
        for k in ("schema", "kind", "game", "track", "default_tree",
                  "annotation_style", "worlds", "trees")
        if k in base_anim
    }
    anim["game"] = game
    anim["track"] = track

    sections_out: list[dict] = []
    cues_out: list[dict] = []
    anchors_out: list[dict] = []
    script_cues_out: list[dict] = []
    seen_cue_ids: set[str] = set()

    for section_id in descriptor.get("sections") or []:
        section_id = str(section_id).strip()
        if not section_id:
            raise ValueError("track descriptor has an empty section id")
        section_dir = tutorial / "sections" / section_id
        section_path = section_dir / "section.json"
        anim_path = section_dir / f"{track}.anim.json"
        script_path = section_dir / f"{track}.script.json"
        for p in (section_path, anim_path, script_path):
            if not p.is_file():
                raise FileNotFoundError(f"missing section source: {p}")

        section = _read_json(section_path)
        if str(section.get("id") or "") != section_id:
            raise ValueError(f"{section_path}: section.id must be {section_id!r}")
        sections_out.append(section)

        section_anim = _read_json(anim_path)
        for cue in section_anim.get("cues") or []:
            cid = str((cue or {}).get("id") or "").strip()
            if not cid:
                raise ValueError(f"{anim_path}: cue missing id")
            if cid in seen_cue_ids:
                raise ValueError(f"duplicate cue id across sections: {cid}")
            seen_cue_ids.add(cid)
            cue = dict(cue)
            cue["section"] = section_id
            cues_out.append(cue)

        anchors_out.extend(section_anim.get("time_anchors") or [])

        section_script = _read_json(script_path)
        script_cues_out.extend(section_script.get("cues") or [])

    anim["sections"] = sections_out
    anim["cues"] = cues_out
    anim["time_anchors"] = anchors_out

    script: dict[str, Any] = dict(descriptor.get("script_meta") or {})
    script.setdefault("schema_version", 1)
    script["game_id"] = game
    script["track"] = track
    script["title"] = descriptor.get("title") or script.get("title") or f"{game} {track}"
    script["version"] = descriptor.get("version") or script.get("version") or "0.1.0"
    script["cues"] = script_cues_out

    if write:
        tutorial_dir = ROOT / "content" / "games" / game / "tutorial"
        _write_json(tutorial_dir / "anim" / "v2" / f"{track}.anim.json", anim)
        _write_json(tutorial_dir / f"script.{track}.json", script)
    return anim, script


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Assemble monolithic track files from section sources")
    ap.add_argument("--game", required=True)
    ap.add_argument("--track", required=True)
    ap.add_argument("--check", action="store_true", help="assemble in memory but do not write")
    args = ap.parse_args(argv)
    try:
        result = assemble_track(args.game, args.track, write=not args.check)
    except Exception as exc:  # noqa: BLE001 - CLI should print a useful error
        print(f"assemble failed: {exc}", file=sys.stderr)
        return 2
    if result is None:
        print(f"no track descriptor for {args.game}/{args.track}", file=sys.stderr)
        return 2
    anim, script = result
    print(f"OK   assembled {args.game}/{args.track}: {len(anim['cues'])} cues, {len(script['cues'])} script cues")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
