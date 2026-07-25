#!/usr/bin/env python3
"""
Link cropped component images to concepts.json via media fields.

Usage:
    python scripts/link_media_to_concepts.py --game civolution
"""

import argparse
import json
import sys
from pathlib import Path


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def load_manifests(game_dir: Path) -> list[dict]:
    items = []
    components_dir = game_dir / "assets" / "components"
    if not components_dir.exists():
        return items
    for page_dir in sorted(components_dir.iterdir()):
        if not page_dir.is_dir():
            continue
        manifest_path = page_dir / "manifest.json"
        if not manifest_path.exists():
            continue
        with open(manifest_path, "r", encoding="utf-8") as f:
            manifest = json.load(f)
        page = manifest.get("page")
        for item in manifest.get("items", []):
            item["page"] = page
            items.append(item)
    return items


def build_media_path(item: dict) -> str:
    page = item.get("page")
    file_name = item.get("file")
    return f"assets/components/page-{page:02d}/{file_name}"


def apply_media(concepts: dict, game_dir: Path, id_mapping: dict[str, str]) -> tuple[list[str], list[str]]:
    items = load_manifests(game_dir)
    matched = []
    unmatched = []

    # Build id -> object index map
    objects = concepts.get("objects", [])
    id_to_index = {obj.get("id"): idx for idx, obj in enumerate(objects) if obj.get("id")}

    for item in items:
        concept_id = item.get("concept_id")
        if not concept_id:
            unmatched.append(f"(no concept_id) {item.get('file')}")
            continue

        # Apply explicit mapping overrides
        target_id = id_mapping.get(concept_id, concept_id)

        if target_id not in id_to_index:
            unmatched.append(f"{concept_id} -> {target_id} (file: {item.get('file')})")
            continue

        idx = id_to_index[target_id]
        obj = objects[idx]
        if "media" not in obj:
            obj["media"] = {}
        obj["media"]["component"] = build_media_path(item)
        matched.append(f"{target_id} -> {obj['media']['component']}")

    return matched, unmatched


def main():
    parser = argparse.ArgumentParser(description="Link component images to concepts")
    parser.add_argument("--game", required=True)
    args = parser.parse_args()

    root = get_project_root()
    game_dir = root / "games" / args.game
    concepts_path = game_dir / "concepts.json"

    if not concepts_path.exists():
        print(f"concepts.json not found: {concepts_path}", file=sys.stderr)
        sys.exit(1)

    with open(concepts_path, "r", encoding="utf-8") as f:
        concepts = json.load(f)

    # Explicit id mappings where manifest concept_id differs from concepts.json id
    id_mapping = {
        "module_tile": "module",
    }

    matched, unmatched = apply_media(concepts, game_dir, id_mapping)

    print(f"Matched {len(matched)} concepts:")
    for m in matched:
        print(f"  {m}")

    if unmatched:
        print(f"\nUnmatched {len(unmatched)} items:")
        for u in unmatched:
            print(f"  {u}")

    # Backup and write
    backup_path = concepts_path.with_suffix(".json.bak")
    with open(backup_path, "w", encoding="utf-8") as f:
        json.dump(concepts, f, ensure_ascii=False, indent=2)
    print(f"\nBackup saved to {backup_path}")

    with open(concepts_path, "w", encoding="utf-8") as f:
        json.dump(concepts, f, ensure_ascii=False, indent=2)
    print(f"Updated {concepts_path}")


if __name__ == "__main__":
    main()
