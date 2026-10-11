#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Add section-level tts_asset names and remove per-cue tts_asset fields."""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
TUT = ROOT / "content/games/splendor/tutorial"

NAMES = {
    "intro": "bg.intro",
    "setup_cards": "setup.cards",
    "setup_gems": "setup.gems",
    "setup_players": "setup.players",
    "turn": "action.turn",
    "take_gems": "action.take",
    "cards": "action.cards",
    "nobles": "action.nobles",
    "reserve": "action.reserve",
    "purchase_reserved": "action.purchase_reserved",
    "summary": "action.summary",
    "end": "endgame",
    "quick_intro": "quick.intro",
    "quick_setup_cards": "quick.setup.cards",
    "quick_setup_gems": "quick.setup.gems",
    "quick_setup_players": "quick.setup.players",
    "quick_turn": "quick.turn",
    "quick_take_gems": "quick.take",
    "quick_gold": "quick.gold",
    "quick_cards": "quick.cards",
    "quick_reserve": "quick.reserve",
    "quick_nobles": "quick.nobles",
    "quick_end": "quick.end",
}


def write(path: Path, doc) -> None:
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    for sec_dir in sorted((TUT / "sections").iterdir()):
        if not sec_dir.is_dir():
            continue
        sid = sec_dir.name
        asset = NAMES.get(sid)
        if not asset:
            print("skip unknown section", sid)
            continue
        meta_path = sec_dir / "section.json"
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        meta["tts_asset"] = asset
        write(meta_path, meta)
        for script_path in sec_dir.glob("*.script.json"):
            doc = json.loads(script_path.read_text(encoding="utf-8"))
            changed = False
            for cue in doc.get("cues") or []:
                if isinstance(cue, dict) and "tts_asset" in cue:
                    cue.pop("tts_asset")
                    changed = True
            if changed:
                write(script_path, doc)
        print(sid, "->", asset)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
