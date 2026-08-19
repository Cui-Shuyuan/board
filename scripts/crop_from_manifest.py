#!/usr/bin/env python3
"""
Crop component images from rulebook pages based on manual manifest.json files.

Usage:
    python scripts/crop_from_manifest.py --game civolution --page 4
    python scripts/crop_from_manifest.py --game civolution --page 5
"""

import argparse
import json
import sys
from pathlib import Path

from PIL import Image


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def crop_image(src_path: Path, bbox_norm: list[float], dest_path: Path):
    img = Image.open(src_path)
    w, h = img.size
    x1 = int(bbox_norm[0] * w)
    y1 = int(bbox_norm[1] * h)
    x2 = int(bbox_norm[2] * w)
    y2 = int(bbox_norm[3] * h)
    x1, x2 = max(0, min(x1, x2)), max(0, min(w, max(x1, x2)))
    y1, y2 = max(0, min(y1, y2)), max(0, min(h, max(y1, y2)))
    if x2 - x1 < 10 or y2 - y1 < 10:
        raise ValueError(f"Bounding box too small: {bbox_norm} -> {x1},{y1},{x2},{y2}")
    cropped = img.crop((x1, y1, x2, y2))
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    cropped.save(dest_path)
    return (x1, y1, x2, y2), (w, h)


def main():
    parser = argparse.ArgumentParser(description="Crop components from manifest")
    parser.add_argument("--game", required=True)
    parser.add_argument("--page", type=int, required=True)
    args = parser.parse_args()

    root = get_project_root()
    game_dir = root / "games" / args.game
    src_path = game_dir / "_review_pages" / f"components-{args.page:02d}.png"
    out_dir = game_dir / "assets" / "components" / f"page-{args.page:02d}"
    manifest_path = out_dir / "manifest.json"

    if not src_path.exists():
        print(f"Source image not found: {src_path}", file=sys.stderr)
        sys.exit(1)
    if not manifest_path.exists():
        print(f"Manifest not found: {manifest_path}", file=sys.stderr)
        sys.exit(1)

    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    img = Image.open(src_path)
    img_w, img_h = img.size
    manifest["image_size"] = {"width": img_w, "height": img_h}

    for item in manifest.get("items", []):
        file_name = item.get("file")
        if not file_name:
            continue
        bbox_norm = item.get("bbox_norm")
        if not isinstance(bbox_norm, list) or len(bbox_norm) != 4:
            print(f"Skipping invalid bbox for {file_name}", file=sys.stderr)
            continue
        dest_path = out_dir / file_name
        try:
            (x1, y1, x2, y2), _ = crop_image(src_path, bbox_norm, dest_path)
            item["bbox_px"] = [x1, y1, x2, y2]
            item["review_status"] = item.get("review_status", "manual")
            print(f"  Saved {file_name}")
        except Exception as e:
            print(f"  Failed {file_name}: {e}", file=sys.stderr)

    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    print(f"Updated manifest: {manifest_path}")


if __name__ == "__main__":
    main()
