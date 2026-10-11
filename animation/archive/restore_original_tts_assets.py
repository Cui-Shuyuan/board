#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Restore Splendor TTS assets to one shared pool with original names.

After the section-first migration we briefly named generated TTS files with a
``section__local`` slug.  The asset policy is now: all TTS files live together
under ``media/tts/{track}/``, keep their original asset name, and each cue
references its audio with the explicit ``tts_asset`` field.

This script is idempotent: it adds/updates ``tts_asset`` in section script
files and rewrites the TTS manifest paths back to the original file names.
It never invokes TTS.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / "animation"))
import section_source  # noqa: E402

GAME = "splendor"
TUT = ROOT / "content" / "games" / GAME / "tutorial"
MEDIA_ROOT = ROOT / "content" / "games" / GAME / "media" / "tts"


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def write(path: Path, doc) -> None:
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def original_name(track: str, section_id: str, local_id: str) -> str:
    """Invert the one-shot section migration for the old global cue id."""
    if track == "full":
        if section_id == "intro":
            return "bg.intro." + local_id
        if section_id == "setup_cards":
            return "setup.cards." + local_id
        if section_id == "setup_gems":
            return "setup.gems." + local_id
        if section_id == "setup_players":
            if local_id.startswith("nobles."):
                return "setup.nobles." + local_id[len("nobles."):]
            if local_id.startswith("starting."):
                return "setup.starting_player." + local_id[len("starting."):]
            if local_id.startswith("end."):
                return "setup.end." + local_id[len("end."):]
        if section_id == "turn":
            return "action.turn." + local_id
        if section_id == "take_gems":
            return "action.take." + local_id
        if section_id == "cards":
            return "action.cards." + local_id
        if section_id == "nobles":
            if local_id.startswith("summary."):
                return "action.cards.summary." + local_id[len("summary."):]
            return "action.nobles." + local_id
        if section_id == "reserve":
            return "action.reserve." + local_id
        if section_id == "purchase_reserved":
            return "action.purchase_reserved." + local_id
        if section_id == "summary":
            return "action.summary." + local_id
        if section_id == "end":
            return "endgame." + local_id
    if track == "quick":
        prefixes = {
            "quick_intro": "quick.intro.",
            "quick_setup_cards": "setup.quick.",
            "quick_setup_gems": "setup.quick.",
            "quick_setup_players": "setup.quick.",
            "quick_turn": "quick.turn.",
            "quick_take_gems": "quick.take.",
            "quick_gold": "quick.gold.",
            "quick_cards": "quick.cards.",
            "quick_reserve": "quick.reserve.",
            "quick_nobles": "quick.nobles.",
            "quick_end": "quick.end.",
        }
        if section_id in prefixes:
            return prefixes[section_id] + local_id
    raise KeyError(f"cannot invert section mapping for {track}/{section_id}/{local_id}")


def update_script_sources(track: str) -> dict[str, str]:
    """Add explicit tts_asset to every section script cue; return qid -> asset."""
    qid_to_asset: dict[str, str] = {}
    for script_path in sorted((TUT / "sections").glob(f"*/{track}.script.json")):
        doc = read(script_path)
        section_id = str(doc.get("section") or script_path.parent.name)
        for cue in doc.get("cues") or []:
            if not isinstance(cue, dict) or cue.get("id") is None:
                continue
            local_id = str(cue["id"])
            asset = original_name(track, section_id, local_id)
            qid = section_source.qualified_cue_id(section_id, local_id)
            qid_to_asset[qid] = asset
            ordered = {"id": local_id, "tts_asset": asset}
            for key, value in cue.items():
                if key not in ordered:
                    ordered[key] = value
            cue.clear()
            cue.update(ordered)
        write(script_path, doc)
    return qid_to_asset


def migrate_manifest(track: str, qid_to_asset: dict[str, str]) -> None:
    media_dir = MEDIA_ROOT / track
    manifest_path = media_dir / "tts_manifest.json"
    manifest = read(manifest_path)
    for cue in manifest.get("cues") or []:
        qid = cue.get("id")
        asset = qid_to_asset.get(qid)
        if not asset:
            raise KeyError(f"manifest cue {qid!r} has no original asset mapping")
        cue["asset"] = asset
        for key, ext in (("file", ".mp3"), ("subtitle_file", ".subtitle.json")):
            old_path = media_dir / f"{asset}{ext}"
            new_path = media_dir / f"{section_source.cue_id_to_slug(qid)}{ext}"
            if not old_path.exists() and new_path.exists():
                shutil.copy2(new_path, old_path)
            if not old_path.exists() and key == "file":
                raise FileNotFoundError(f"missing original TTS asset: {old_path}")
            rel = f"content/games/{GAME}/media/tts/{track}/{asset}{ext}"
            cue[key] = rel if old_path.exists() else None
    write(manifest_path, manifest)
    print(f"[tts] {track}: restored {len(manifest.get('cues') or [])} manifest paths to original asset names")


def cleanup_slug_copies(track: str) -> int:
    """Remove the short-lived section__cue copies after originals are verified."""
    removed = 0
    media_dir = MEDIA_ROOT / track
    manifest = read(media_dir / "tts_manifest.json")
    for cue in manifest.get("cues") or []:
        qid = cue.get("id")
        if not qid:
            continue
        slug = section_source.cue_id_to_slug(qid)
        for key, ext in (("file", ".mp3"), ("subtitle_file", ".subtitle.json")):
            rel = cue.get(key)
            orig = media_dir / Path(rel).name if rel else None
            slug_path = media_dir / f"{slug}{ext}"
            if slug_path.is_file() and orig is not None and orig.is_file() and slug_path != orig:
                slug_path.unlink()
                removed += 1
    return removed


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--track", choices=["full", "quick"], action="append")
    ap.add_argument("--cleanup-slug-copies", action="store_true")
    args = ap.parse_args()
    tracks = args.track or ["full", "quick"]
    for track in tracks:
        qid_to_asset = update_script_sources(track)
        migrate_manifest(track, qid_to_asset)
        if args.cleanup_slug_copies:
            n = cleanup_slug_copies(track)
            print(f"[tts] {track}: removed {n} section/cue-slug copy(ies)")
    # Rebuild runtimes after manifest paths changed.
    import build_tutorial_runtime
    for track in tracks:
        build_tutorial_runtime.build_runtime(GAME, track, force=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
