#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""One-off: make stage templates point at the processed `_cutout.png` assets.

v1's runtime preferred `<name>_cutout.png` automatically.  v2's SpriteLibrary
loads the literal path, so the raw scans were being re-processed at runtime and
their rounded corners / token masks came back as white squares.  This script
writes the processed path explicitly into the stage files (and adds an explicit
palette->image table for gem/gold tokens which have no single face_image).
"""
from __future__ import annotations
import argparse, json, os
from pathlib import Path

V2 = Path(__file__).resolve().parent.parent / "games" / "splendor" / "tutorial" / "anim" / "v2"
GEM_MAP = {
    "gem_diamond": "media/card/白宝石_cutout.png",
    "gem_sapphire": "media/card/蓝宝石_cutout.png",
    "gem_emerald": "media/card/绿宝石_cutout.png",
    "gem_ruby": "media/card/红宝石_cutout.png",
    "gem_onyx": "media/card/黑宝石_cutout.png",
    "gem_gold": "media/card/黄金_cutout.png",
}


def cutout(path: str, media_root: Path) -> str:
    if not path or path.lower().endswith("_cutout.png"):
        return path
    base, _ext = os.path.splitext(path)
    candidate = base + "_cutout.png"
    return candidate if (media_root / candidate).exists() else path


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--media-root", default="/mnt/d/workspace/board/games/splendor",
                    help="directory containing media/ (needed only for existence checks)")
    a = ap.parse_args()
    media_root = Path(a.media_root)
    changed = 0
    for p in sorted(V2.glob("_stage/*.stage.json")):
        d = json.loads(p.read_text(encoding="utf-8"))
        for t in d.get("templates") or []:
            for key in ("face_image", "back_image"):
                old = t.get(key) or ""
                new = cutout(old, media_root)
                if new != old:
                    t[key] = new
                    changed += 1
            # gem/gold: one template per shape, image depends on item palette
            if t.get("id") in ("gem", "gem_sample"):
                if not t.get("face_image"):
                    table = []
                    for pal, img in GEM_MAP.items():
                        if (media_root / img).exists():
                            table.append({"palette": pal, "face_image": img})
                    if table and not t.get("face_image_by_palette"):
                        t["face_image_by_palette"] = table
                        changed += 1
            # noble: v1 used the first noble scan for the shared template
            if t.get("palette") == "noble" and not t.get("face_image"):
                img = "media/card/贵族_0001_cutout.png"
                if (media_root / img).exists():
                    t["face_image"] = img
                    changed += 1
        p.write_text(json.dumps(d, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"OK   cutout paths / palette tables updated ({changed} fields)")


if __name__ == "__main__":
    main()
