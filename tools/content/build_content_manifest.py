#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build a stable content manifest for one game.

Usage:
    python3 tools/content/build_content_manifest.py --game splendor

The manifest is written to content/manifests/{game}.json and contains a
deterministic version derived only from the file paths and their SHA-256
values.  Each file URL embeds that version so the backend can serve immutable
content safely.  Non-runtime cruft files such as .DS_Store, Thumbs.db, *.tmp
and *.log are skipped, as are the sampling scratch artefacts that .gitignore
already excludes (*.exitstate.json, *.v2sample.json).  Shipping those was a real
bug: the animation tooling regenerates them, they are absent from a clean
checkout, and including them made the manifest depend on which machine happened
to generate it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from urllib.parse import quote

SCHEMA = "board-content/v1"
SKIPPED_NAMES = {".DS_Store", "Thumbs.db"}
SKIPPED_SUFFIXES = {".tmp", ".log", ".exitstate.json", ".v2sample.json"}


def is_skipped_name(name: str) -> bool:
    if name in SKIPPED_NAMES:
        return True
    if name.startswith(".git"):
        return True
    return any(name.endswith(suffix) for suffix in SKIPPED_SUFFIXES)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def content_files(game_dir: Path) -> list[Path]:
    files: list[Path] = []
    for path in game_dir.rglob("*"):
        if not path.is_file():
            continue
        relative_parts = path.relative_to(game_dir).parts
        if any(is_skipped_name(part) for part in relative_parts):
            continue
        files.append(path)
    files.sort(key=lambda item: item.relative_to(game_dir).as_posix())
    return files


def build_manifest(game: str, repo_root: Path) -> dict:
    game_dir = repo_root / "content" / "games" / game
    if not game_dir.is_dir():
        raise FileNotFoundError(f"game content directory not found: {game_dir}")

    # Version is computed from path + sha256 only.  URL generation happens
    # after the version is known so versioned URLs stay deterministic too.
    file_specs: list[tuple[str, int, str]] = []
    version_source_lines: list[str] = []
    for path in content_files(game_dir):
        relative = path.relative_to(game_dir).as_posix()
        digest = sha256_file(path)
        file_specs.append((relative, path.stat().st_size, digest))
        version_source_lines.append(f"{relative}\t{digest}")

    version_source_lines.sort()
    version_source = "\n".join(version_source_lines).encode("utf-8")
    version = hashlib.sha256(version_source).hexdigest()[:16]
    safe_game = quote(game, safe="")
    safe_version = quote(version, safe="")

    entries = [
        {
            "path": relative,
            "size": size,
            "sha256": digest,
            "url": (
                f"/api/content/games/{safe_game}/files/{safe_version}/"
                f"{quote(relative, safe='/')}"
            ),
        }
        for relative, size, digest in file_specs
    ]

    return {
        "schema": SCHEMA,
        "game": game,
        "version": version,
        "files": entries,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, help="game id, e.g. splendor")
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args()

    repo_root = args.repo_root.resolve()
    game = args.game.strip()
    if not game:
        print("error: --game must not be empty", file=sys.stderr)
        return 2

    try:
        manifest = build_manifest(game, repo_root)
    except FileNotFoundError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    output = repo_root / "content" / "manifests" / f"{game}.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    print(
        f"wrote {output.relative_to(repo_root)} "
        f"version={manifest['version']} files={len(manifest['files'])}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
