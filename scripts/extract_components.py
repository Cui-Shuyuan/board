#!/usr/bin/env python3
"""
Extract individual component images from a rulebook components overview page.

Usage:
    python scripts/extract_components.py --game civolution --page 4
    python scripts/extract_components.py --game civolution --page 5 --review

Requires MOONSHOT_API_KEY environment variable (or .env file).
"""

import argparse
import base64
import io
import json
import os
import sys
from pathlib import Path
from typing import Any

import requests
from PIL import Image


def load_env():
    """Load .env from project root if present."""
    env_path = Path(__file__).resolve().parent.parent / ".env"
    if env_path.exists():
        with open(env_path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, value = line.split("=", 1)
                if key not in os.environ:
                    os.environ[key] = value.strip().strip('"').strip("'")


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def load_candidate_concepts(game_dir: Path) -> list[dict[str, Any]]:
    """Load object ids/names from concepts.json and instances.json."""
    candidates = []
    concepts_path = game_dir / "concepts.json"
    if concepts_path.exists():
        with open(concepts_path, "r", encoding="utf-8") as f:
            concepts = json.load(f)
        for obj in concepts.get("objects", []):
            candidates.append({
                "id": obj.get("id"),
                "name_zh": obj.get("name", {}).get("zh", ""),
                "name_en": obj.get("name", {}).get("en", ""),
            })
    instances_path = game_dir / "instances.json"
    if instances_path.exists():
        with open(instances_path, "r", encoding="utf-8") as f:
            instances = json.load(f)
        for key in ["effects", "modules", "cards", "continent_tiles", "sites", "chips"]:
            for item in instances.get(key, []):
                candidates.append({
                    "id": item.get("id"),
                    "name_zh": item.get("name", {}).get("zh", ""),
                    "name_en": item.get("name", {}).get("en", ""),
                })
    # Deduplicate by id
    seen = set()
    unique = []
    for c in candidates:
        if c["id"] and c["id"] not in seen:
            seen.add(c["id"])
            unique.append(c)
    return unique


def encode_image(image_path: Path) -> str:
    with open(image_path, "rb") as f:
        return base64.b64encode(f.read()).decode("utf-8")


def build_prompt(candidates: list[dict[str, Any]]) -> str:
    candidate_lines = "\n".join(
        f'- {c["id"]} (zh:"{c["name_zh"]}", en:"{c["name_en"]}")'
        for c in candidates
    )
    return (
        "You are analyzing a board game rulebook page that shows an overview of all components.\n"
        "Identify every distinct component area on the page. For each component, return:\n"
        "- concept_id: the best matching id from the candidate list, or null if none matches\n"
        "- label_en: the English label shown in the image\n"
        "- label_zh: the Chinese name from the candidate list, or your best Chinese translation\n"
        "- bbox_norm: normalized bounding box [x1, y1, x2, y2] (values 0-1, top-left origin)\n"
        "- description: one-sentence description of what is visible\n"
        "\n"
        "Important:\n"
        "- Return ONLY a JSON array, no markdown, no explanation.\n"
        "- Make bounding boxes tight around each component illustration, including its label/quantity text if directly adjacent.\n"
        "- If multiple components share a single boxed area (e.g. '2 game boards'), create one entry for the whole box with the most general concept_id.\n"
        "- Do not merge separate boxes.\n"
        "\n"
        "Candidate concepts (id, names):\n"
        f"{candidate_lines}\n"
    )


def call_vision_api(image_path: Path, prompt: str, model: str) -> list[dict[str, Any]]:
    api_key = os.environ.get("MOONSHOT_API_KEY")
    if not api_key:
        raise RuntimeError(
            "MOONSHOT_API_KEY not found. Set it as environment variable or in .env file."
        )

    base_url = os.environ.get("MOONSHOT_BASE_URL", "https://api.moonshot.ai/v1")
    b64 = encode_image(image_path)
    mime = "image/png" if image_path.suffix.lower() == ".png" else "image/jpeg"

    messages = [
        {
            "role": "system",
            "content": "You are a precise image annotation assistant. Always return valid JSON.",
        },
        {
            "role": "user",
            "content": [
                {
                    "type": "image_url",
                    "image_url": {"url": f"data:{mime};base64,{b64}"},
                },
                {"type": "text", "text": prompt},
            ],
        },
    ]

    response = requests.post(
        f"{base_url}/chat/completions",
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        json={
            "model": model,
            "messages": messages,
            "temperature": 0.1,
        },
        timeout=120,
    )
    response.raise_for_status()
    data = response.json()
    content = data["choices"][0]["message"]["content"]

    # Strip markdown code fences if present
    content = content.strip()
    if content.startswith("```"):
        lines = content.splitlines()
        if lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].startswith("```"):
            lines = lines[:-1]
        content = "\n".join(lines).strip()

    return json.loads(content)


def normalize_filename(label: str) -> str:
    """Turn any label into a safe filename base."""
    s = label.strip().lower()
    for ch in [" ", "-", "/", "\\", ":", "(", ")", "[", "]", "."]:
        s = s.replace(ch, "_")
    while "__" in s:
        s = s.replace("__", "_")
    return s.strip("_")


def crop_image(src_path: Path, bbox_norm: list[float], dest_path: Path):
    img = Image.open(src_path)
    w, h = img.size
    x1 = int(bbox_norm[0] * w)
    y1 = int(bbox_norm[1] * h)
    x2 = int(bbox_norm[2] * w)
    y2 = int(bbox_norm[3] * h)
    # Ensure min size and correct order
    x1, x2 = min(x1, x2), max(x1, x2)
    y1, y2 = min(y1, y2), max(y1, y2)
    if x2 - x1 < 10 or y2 - y1 < 10:
        raise ValueError(f"Bounding box too small: {bbox_norm}")
    cropped = img.crop((x1, y1, x2, y2))
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    cropped.save(dest_path)


def merge_with_existing(
    detected: list[dict[str, Any]], existing_manifest: dict[str, Any]
) -> list[dict[str, Any]]:
    """Preserve manual entries from an existing manifest, append new auto entries."""
    existing_items = {item.get("file"): item for item in existing_manifest.get("items", [])}
    merged = []
    for item in detected:
        file_name = item.get("file")
        if file_name in existing_items:
            existing = existing_items[file_name]
            if existing.get("review_status") == "manual":
                merged.append(existing)
                continue
        merged.append(item)
    # Add manual-only items that model did not detect
    detected_files = {item.get("file") for item in detected}
    for file_name, item in existing_items.items():
        if file_name not in detected_files and item.get("review_status") == "manual":
            merged.append(item)
    return merged


def main():
    parser = argparse.ArgumentParser(description="Extract component images from rulebook pages")
    parser.add_argument("--game", required=True, help="Game directory name under games/")
    parser.add_argument("--page", type=int, required=True, help="Rulebook page number")
    parser.add_argument("--model", default="kimi-k3", help="Moonshot vision model name")
    parser.add_argument("--dry-run", action="store_true", help="Run detection but do not save files")
    parser.add_argument("--review", action="store_true", help="Open review mode (keep manual fixes)")
    args = parser.parse_args()

    load_env()

    root = get_project_root()
    game_dir = root / "games" / args.game
    if not game_dir.exists():
        print(f"Game directory not found: {game_dir}", file=sys.stderr)
        sys.exit(1)

    src_path = game_dir / "_review_pages" / f"components-{args.page:02d}.png"
    if not src_path.exists():
        print(f"Source image not found: {src_path}", file=sys.stderr)
        sys.exit(1)

    out_dir = game_dir / "assets" / "components" / f"page-{args.page:02d}"
    manifest_path = out_dir / "manifest.json"

    print(f"Loading candidate concepts from {game_dir.name}...")
    candidates = load_candidate_concepts(game_dir)
    print(f"Found {len(candidates)} candidate concepts.")

    existing_manifest = {}
    if args.review and manifest_path.exists():
        with open(manifest_path, "r", encoding="utf-8") as f:
            existing_manifest = json.load(f)
        print(f"Loaded existing manifest with {len(existing_manifest.get('items', []))} entries.")

    print(f"Calling {args.model} vision API for {src_path.name}...")
    prompt = build_prompt(candidates)
    detected = call_vision_api(src_path, prompt, args.model)
    print(f"Detected {len(detected)} component regions.")

    # Validate and enrich detected items
    img = Image.open(src_path)
    img_w, img_h = img.size
    items = []
    for idx, d in enumerate(detected):
        concept_id = d.get("concept_id")
        label_en = d.get("label_en", "unknown")
        label_zh = d.get("label_zh", "")
        bbox_norm = d.get("bbox_norm")
        if not isinstance(bbox_norm, list) or len(bbox_norm) != 4:
            print(f"Skipping invalid bbox for {label_en}: {bbox_norm}", file=sys.stderr)
            continue
        # Clamp
        bbox_norm = [max(0.0, min(1.0, float(v))) for v in bbox_norm]
        # Use concept_id as filename base if valid, else normalized English label
        file_base = concept_id if concept_id else normalize_filename(label_en)
        file_name = f"{file_base}.png"
        dest_path = out_dir / file_name

        item = {
            "concept_id": concept_id,
            "label_en": label_en,
            "label_zh": label_zh,
            "bbox_norm": bbox_norm,
            "bbox_px": [
                int(bbox_norm[0] * img_w),
                int(bbox_norm[1] * img_h),
                int(bbox_norm[2] * img_w),
                int(bbox_norm[3] * img_h),
            ],
            "file": file_name,
            "description": d.get("description", ""),
            "review_status": "auto",
        }
        items.append(item)

        if not args.dry_run:
            try:
                crop_image(src_path, bbox_norm, dest_path)
                print(f"  Saved {dest_path.name}")
            except Exception as e:
                print(f"  Failed to crop {file_name}: {e}", file=sys.stderr)

    if args.review and existing_manifest:
        items = merge_with_existing(items, existing_manifest)

    manifest = {
        "source": str(src_path.relative_to(root)),
        "page": args.page,
        "image_size": {"width": img_w, "height": img_h},
        "model": args.model,
        "items": items,
    }

    if not args.dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)
        with open(manifest_path, "w", encoding="utf-8") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=2)
        print(f"Manifest saved to {manifest_path}")
    else:
        print("Dry run complete. Would save:")
        for item in items:
            print(f"  {item['file']} -> {item['concept_id']}")


if __name__ == "__main__":
    main()
