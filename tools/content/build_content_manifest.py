#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build an immutable, runtime-only content package for one game.

Usage:
    python3 tools/content/build_content_manifest.py --game splendor

The package is collected from explicit runtime references rather than by
recursively copying the game directory.  Unity reads:

  * tutorial/{track}.runtime.json
  * tutorial/anim/v2/{track}.compiled.json
  * media paths referenced by the runtime/compiled JSON (TTS mp3, card scans,
    marker scans, etc.)

QA logs, animation sources, stage sources, README files, Python tools,
byte-code caches and LRC source/editor files are deliberately not runtime
dependencies and are never shipped.  In particular Unity's subtitle data comes
from ``*.runtime.json``; ``*.lrc`` is an authoring artefact only.

Publication is a two-phase atomic operation:

  1. ``content/releases/{game}/{version}/`` is staged as
     ``{version}.tmp`` and verified file-by-file;
  2. the verified directory is renamed into place, then the manifest is
     written through ``{game}.json.tmp`` + atomic rename.

A failed publish never replaces the old manifest or existing release.  An
existing release with the same version/name but different bytes is a hard
error; immutable URLs must never change bytes.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import quote

SCHEMA = "board-content/v1"
RELEASE_HISTORY_KEEP = 2  # current release + two historical releases

# Files inside these directories are tooling / QA / sources, never runtime data.
EXCLUDED_DIR_NAMES = {
    ".git",
    "__pycache__",
    "_qa",
    "checks",
    "animation",
}

EXCLUDED_NAMES = {".DS_Store", "Thumbs.db"}

EXCLUDED_SUFFIXES = {
    ".md",
    ".py",
    ".pyc",
    ".lrc",
    ".tmp",
    ".log",
    ".exitstate.json",
    ".v2sample.json",
}

# Allowed runtime media references.  ``.lrc`` is intentionally absent: the
# Unity player reads subtitle timing from ``*.runtime.json``.
RUNTIME_MEDIA_SUFFIXES = {
    ".bmp",
    ".gif",
    ".jpeg",
    ".jpg",
    ".png",
    ".tga",
    ".webp",
    ".aac",
    ".flac",
    ".m4a",
    ".mp3",
    ".ogg",
    ".wav",
    ".ass",
    ".srt",
    ".ssa",
    ".vtt",
}

# JSON keys whose string values are known runtime file references.  Unknown
# keys still allow existing media paths to be picked up when the value looks
# like a packaged media path.
FILE_REFERENCE_KEYS = {
    "audio",
    "back_image",
    "background",
    "face_image",
    "file",
    "href",
    "image",
    "picture",
    "src",
    "subtitle",
    "subtitle_file",
    "texture",
}


@dataclass(frozen=True)
class PackageFile:
    """One runtime file and its immutable identity."""

    path: str
    size: int
    sha256: str

    @property
    def version_line(self) -> str:
        return f"{self.path}\t{self.sha256}"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_excluded_path(relative: str) -> bool:
    """Return True when a relative path is known tooling/source-only data."""
    normalized = relative.replace("\\", "/")
    parts = normalized.split("/")
    if any(part in EXCLUDED_DIR_NAMES for part in parts):
        return True
    if parts[-1] in EXCLUDED_NAMES:
        return True
    lower = normalized.lower()
    return any(lower.endswith(suffix.lower()) for suffix in EXCLUDED_SUFFIXES)


def _normalize_reference(raw: str) -> str | None:
    value = raw.strip().replace("\\", "/")
    if not value or "://" in value:
        return None
    if value.startswith("/"):
        return None

    parts = value.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        return None
    return "/".join(parts)


def _is_file_reference_key(key: str) -> bool:
    return key in FILE_REFERENCE_KEYS or key.endswith("_image") or key.endswith("_audio")


def _walk_references(
    value: Any,
    source: Path,
    game_dir: Path,
    key: str,
    found: set[str],
) -> None:
    if isinstance(value, dict):
        for child_key, child_value in value.items():
            _walk_references(child_value, source, game_dir, str(child_key), found)
        return

    if isinstance(value, list):
        for item in value:
            _walk_references(item, source, game_dir, key, found)
        return

    if not isinstance(value, str):
        return

    raw = value.strip()
    suffix = Path(raw).suffix.lower()
    if suffix not in RUNTIME_MEDIA_SUFFIXES:
        return

    relative = _normalize_reference(raw)
    if relative is None:
        if _is_file_reference_key(key):
            raise FileNotFoundError(
                f"{source}: unsafe runtime file reference under key '{key}': {raw!r}"
            )
        return

    if is_excluded_path(relative):
        if _is_file_reference_key(key):
            raise FileNotFoundError(
                f"{source}: runtime reference points at excluded path: {relative}"
            )
        return

    candidate = game_dir / relative
    if candidate.is_file():
        found.add(relative)
    elif _is_file_reference_key(key):
        raise FileNotFoundError(
            f"{source}: referenced runtime file not found: {relative}"
        )


def collect_runtime_files(game_dir: Path) -> list[Path]:
    """Collect the explicit runtime dependency closure for one game.

    A game with no ``tutorial/*.runtime.json`` files has no tutorial package;
    an empty list is returned so the caller can decide whether to publish it.
    Every runtime track must have its matching v2 compiled animation.  Any
    referenced media file must exist, or collection fails loudly instead of
    publishing a broken package.
    """
    tutorial_dir = game_dir / "tutorial"
    if not tutorial_dir.is_dir():
        return []

    runtime_files = sorted(tutorial_dir.glob("*.runtime.json"))
    if not runtime_files:
        return []

    package: dict[str, Path] = {}

    for runtime_path in runtime_files:
        track = runtime_path.name[: -len(".runtime.json")]
        if not track or "/" in track or "\\" in track:
            raise ValueError(f"invalid runtime track name: {runtime_path.name}")

        runtime_relative = runtime_path.relative_to(game_dir).as_posix()
        if is_excluded_path(runtime_relative):
            raise ValueError(f"runtime file is in an excluded path: {runtime_relative}")
        package[runtime_relative] = runtime_path

        compiled_relative = f"tutorial/anim/v2/{track}.compiled.json"
        compiled_path = game_dir / compiled_relative
        if not compiled_path.is_file():
            raise FileNotFoundError(
                f"runtime track '{track}' has no v2 compiled animation: "
                f"{compiled_relative}"
            )
        package[compiled_relative] = compiled_path

        for source_path in (runtime_path, compiled_path):
            try:
                document = json.loads(source_path.read_text(encoding="utf-8"))
            except json.JSONDecodeError as exc:
                raise ValueError(f"invalid runtime JSON: {source_path}: {exc}") from exc

            references: set[str] = set()
            _walk_references(document, source_path, game_dir, "", references)
            for relative in sorted(references):
                full_path = game_dir / relative
                if not full_path.is_file():
                    raise FileNotFoundError(
                        f"{source_path}: referenced runtime file not found: {relative}"
                    )
                if is_excluded_path(relative):
                    raise FileNotFoundError(
                        f"{source_path}: referenced runtime path is excluded: {relative}"
                    )
                package[relative] = full_path

    ordered = sorted(package, key=lambda value: value)
    return [package[relative] for relative in ordered]


def describe_package(game_dir: Path, files: Iterable[Path]) -> list[PackageFile]:
    specs: list[PackageFile] = []
    seen: set[str] = set()

    for path in files:
        relative = path.relative_to(game_dir).as_posix()
        if relative in seen:
            raise ValueError(f"duplicate runtime file in package: {relative}")
        if not path.is_file():
            raise FileNotFoundError(f"runtime file not found: {relative}")
        if is_excluded_path(relative):
            raise ValueError(f"excluded path leaked into runtime package: {relative}")
        seen.add(relative)
        specs.append(
            PackageFile(
                path=relative,
                size=path.stat().st_size,
                sha256=sha256_file(path),
            )
        )

    specs.sort(key=lambda item: item.path)
    return specs


def compute_version(specs: Iterable[PackageFile]) -> str:
    version_source = "\n".join(
        sorted(spec.version_line for spec in specs)
    ).encode("utf-8")
    return hashlib.sha256(version_source).hexdigest()[:16]


def build_manifest(game: str, repo_root: Path) -> dict:
    """Compute the deterministic manifest for the current runtime package.

    This function intentionally only reads/checks source files.  Use
    :func:`publish` to create the immutable release and atomically replace the
    manifest pointer.
    """
    game_dir = repo_root / "content" / "games" / game
    if not game_dir.is_dir():
        raise FileNotFoundError(f"game content directory not found: {game_dir}")

    runtime_files = collect_runtime_files(game_dir)
    if not runtime_files:
        raise FileNotFoundError(
            f"no tutorial runtime package found for game '{game}': "
            "rules-only games must not publish an empty tutorial manifest"
        )

    specs = describe_package(game_dir, runtime_files)
    version = compute_version(specs)
    safe_game = quote(game, safe="")
    safe_version = quote(version, safe="")

    entries = [
        {
            "path": spec.path,
            "size": spec.size,
            "sha256": spec.sha256,
            "url": (
                f"/api/content/games/{safe_game}/files/{safe_version}/"
                f"{quote(spec.path, safe='/')}"
            ),
        }
        for spec in specs
    ]

    return {
        "schema": SCHEMA,
        "game": game,
        "version": version,
        "runtime": {
            "kind": "runtime-only",
            "file_count": len(specs),
            "release_history_keep": RELEASE_HISTORY_KEEP,
        },
        "files": entries,
    }


def _release_root(repo_root: Path, game: str) -> Path:
    return repo_root / "content" / "releases" / game


def _release_dir(repo_root: Path, game: str, version: str) -> Path:
    return _release_root(repo_root, game) / version


def _release_temp_dir(repo_root: Path, game: str, version: str) -> Path:
    return _release_root(repo_root, game) / f"{version}.tmp"


def verify_release(release_dir: Path, specs: list[PackageFile]) -> None:
    """Verify that a release directory contains exactly the expected files."""
    if not release_dir.is_dir():
        raise FileNotFoundError(f"release directory not found: {release_dir}")

    expected = {spec.path: spec for spec in specs}
    actual_paths = sorted(
        path.relative_to(release_dir).as_posix()
        for path in release_dir.rglob("*")
        if path.is_file()
    )

    if actual_paths != sorted(expected):
        missing = sorted(set(expected) - set(actual_paths))
        extra = sorted(set(actual_paths) - set(expected))
        raise ValueError(
            f"release directory does not match manifest for {release_dir}: "
            f"missing={missing[:5]} extra={extra[:5]}"
        )

    for spec in specs:
        path = release_dir / spec.path
        actual_size = path.stat().st_size
        if actual_size != spec.size:
            raise ValueError(
                f"release size mismatch for {spec.path}: "
                f"expected {spec.size}, got {actual_size}"
            )
        actual_hash = sha256_file(path)
        if actual_hash != spec.sha256:
            raise ValueError(
                f"release sha256 mismatch for {spec.path}: "
                f"expected {spec.sha256}, got {actual_hash}"
            )


def publish_release(
    game: str,
    repo_root: Path,
    specs: list[PackageFile],
    version: str,
) -> Path:
    """Create or reuse the immutable release directory for a package."""
    release_dir = _release_dir(repo_root, game, version)
    if release_dir.exists():
        verify_release(release_dir, specs)
        return release_dir

    root = _release_root(repo_root, game)
    root.mkdir(parents=True, exist_ok=True)
    temp_dir = _release_temp_dir(repo_root, game, version)

    if temp_dir.exists():
        shutil.rmtree(temp_dir)

    try:
        temp_dir.mkdir(parents=True)
        game_dir = repo_root / "content" / "games" / game
        for spec in specs:
            source = game_dir / spec.path
            target = temp_dir / spec.path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)

        verify_release(temp_dir, specs)

        try:
            temp_dir.rename(release_dir)
        except OSError:
            # A concurrent publisher may have won the race.  Reuse only if
            # the resulting release is byte-identical.
            verify_release(release_dir, specs)
            shutil.rmtree(temp_dir, ignore_errors=True)
        return release_dir
    except Exception:
        shutil.rmtree(temp_dir, ignore_errors=True)
        raise


def _write_manifest_atomic(repo_root: Path, game: str, manifest: dict) -> Path:
    output = repo_root / "content" / "manifests" / f"{game}.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_name(output.name + ".tmp")

    payload = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    temp.write_text(payload, encoding="utf-8")

    # Validate before replacing the live pointer.
    parsed = json.loads(temp.read_text(encoding="utf-8"))
    if parsed.get("schema") != SCHEMA:
        raise ValueError(f"refusing to publish manifest with invalid schema: {temp}")
    temp.replace(output)
    return output


def cleanup_old_releases(
    game: str,
    repo_root: Path,
    current_version: str,
    keep_old: int = RELEASE_HISTORY_KEEP,
) -> list[str]:
    """Keep the current release and the newest ``keep_old`` historical releases.

    Removing an old release does not affect the active manifest because the
    versioned route only serves the current version.  Keeping a small history
    makes the documented rollback path possible: restore the old source commit,
    rerun the builder, and it reuses the verified release directory.
    """
    root = _release_root(repo_root, game)
    if not root.is_dir():
        return []

    release_dirs = [
        path
        for path in root.iterdir()
        if path.is_dir() and path.name != current_version and not path.name.endswith(".tmp")
    ]
    release_dirs.sort(key=lambda path: path.stat().st_mtime, reverse=True)
    removed: list[str] = []
    for path in release_dirs[keep_old:]:
        shutil.rmtree(path)
        removed.append(path.name)
    return removed


def verify_manifest_release(
    repo_root: Path,
    game: str,
    manifest: dict,
    specs: list[PackageFile],
) -> None:
    release_dir = _release_dir(repo_root, game, manifest["version"])
    verify_release(release_dir, specs)


def publish(game: str, repo_root: Path) -> dict:
    """Build, stage, verify and atomically publish one immutable package."""
    game_dir = repo_root / "content" / "games" / game
    if not game_dir.is_dir():
        raise FileNotFoundError(f"game content directory not found: {game_dir}")

    runtime_files = collect_runtime_files(game_dir)
    if not runtime_files:
        raise FileNotFoundError(
            f"no tutorial runtime package found for game '{game}': "
            "rules-only games must not publish an empty tutorial manifest"
        )

    specs = describe_package(game_dir, runtime_files)
    manifest = build_manifest(game, repo_root)

    # Re-derive specs from the manifest to guarantee version and entries agree.
    manifest_specs = [
        PackageFile(
            path=entry["path"],
            size=entry["size"],
            sha256=entry["sha256"],
        )
        for entry in manifest["files"]
    ]
    if manifest_specs != specs:
        raise RuntimeError("internal error: manifest and collected package differ")

    publish_release(game, repo_root, manifest_specs, manifest["version"])
    verify_manifest_release(repo_root, game, manifest, manifest_specs)

    # The release is now immutable and verified.  Only after that do we switch
    # the live manifest pointer.
    output = _write_manifest_atomic(repo_root, game, manifest)

    try:
        cleanup_old_releases(game, repo_root, manifest["version"])
    except Exception as exc:  # cleanup is best-effort; active release is safe
        print(
            f"warning: failed to clean old releases for {game}: {exc}",
            file=sys.stderr,
        )

    return manifest | {"_manifest_path": output.as_posix()}


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
        manifest = publish(game, repo_root)
    except (FileNotFoundError, ValueError, OSError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    output = Path(manifest["_manifest_path"])
    print(
        f"wrote {output.relative_to(repo_root)} "
        f"version={manifest['version']} files={len(manifest['files'])} "
        f"release={Path('content') / 'releases' / game / manifest['version']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
