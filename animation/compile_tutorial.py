#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Incremental top-level compiler for a tutorial track.

Source:
    content/games/{game}/tutorial/script.{track}.json      narration cues
    content/games/{game}/tutorial/anim/v2/{track}.anim.json animation graph/events

Generated:
    media/tts/{track}/*.mp3 + *.subtitle.json
    tutorial/{track}.lrc           estimated narration timeline
    tutorial/{track}.tts.lrc       actual TTS timeline
    media/tts/{track}/tts_manifest.json
    tutorial/{track}.runtime.json
    tutorial/anim/v2/{track}.compiled.json

Usage:
    python animation/compile_tutorial.py --game splendor --track full --dry-run
    python animation/compile_tutorial.py --game splendor --track full --skip-tts
    python animation/compile_tutorial.py --game splendor --track full
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))

import anim_schema_v2 as schema  # noqa: E402
import compile_animation_v2 as animation_compiler  # noqa: E402
import lrc as lrc_utils  # noqa: E402
import qa_anim_ask  # noqa: E402
import validate_anim_rules_v2 as rule_ledger  # noqa: E402
from build_tutorial_runtime import build_runtime  # noqa: E402
from tutorial_script_tool import rebuild_lrc  # noqa: E402
from validate_timed_script import parse_file  # noqa: E402


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def normalize_manifest_path(path_value: str) -> str:
    """Normalize manifest paths to forward slashes for cross-platform reads."""
    return str(path_value or "").replace("\\", "/")


def normalize_manifest_paths(manifest: dict) -> dict:
    """Normalize the file fields in-place and return the manifest."""
    for cue in manifest.get("cues") or []:
        if not isinstance(cue, dict):
            continue
        for key in ("file", "subtitle_file"):
            value = cue.get(key)
            if isinstance(value, str):
                cue[key] = normalize_manifest_path(value)
    return manifest


def manifest_audio_ok(manifest_cue: dict, root: Path | None = None) -> bool:
    """Return True when a manifest cue's audio file exists after path normalization."""
    if not isinstance(manifest_cue, dict):
        return False
    rel = normalize_manifest_path(manifest_cue.get("file", ""))
    if not rel:
        return False
    return ((root or ROOT) / rel).exists()


def cue_text(cue: dict) -> str:
    return "".join((b.get("text") or "") for b in (cue.get("beats") or []))


def text_hash(text: str, refs: list) -> str:
    raw = text + "\n" + "|".join(refs or [])
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()[:16]


def read_current_tts_text(lrc_path: Path) -> dict:
    if not lrc_path.exists():
        return {}
    doc = parse_file(lrc_path)
    out = {}
    for cue in doc.get("cues") or []:
        out[cue.get("id")] = cue.get("text", "")
    return out


def recompute_starts(manifest: dict):
    cursor = 0.0
    gap = float(manifest.get("gap_seconds", 0.0) or 0.0)
    for cue in manifest.get("cues") or []:
        cue["start"] = round(cursor, 3)
        cursor += float(cue.get("duration", 0.0) or 0.0) + gap


def write_tts_lrc(game_dir: Path, track: str, script: dict, manifest: dict) -> Path:
    """Write {track}.tts.lrc from script order + manifest durations."""
    return lrc_utils.write_manifest_tts_lrc(game_dir, track, script, manifest)


def tts_asset_rel(game: str, track: str, cue_id: str, ext: str) -> str:
    """Repository-relative manifest path for one generated TTS asset."""
    return f"content/games/{game}/media/tts/{track}/{cue_id}{ext}"


def qa_questions_path(game: str) -> Path:
    return ROOT / "content" / "games" / game / "tutorial" / "anim" / "_qa" / "questions.json"


def write_delta_lrc(path: Path, cues: list[dict], refs_by_id: dict, game: str, track: str):
    lrc_utils.write_delta_lrc(path, cues, refs_by_id, game, track)


def _load_dotenv_if_present() -> None:
    env_path = ROOT / ".env"
    if not env_path.exists():
        return
    for raw in env_path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def _looks_like_standard_voice(voice: str | None) -> bool:
    value = (voice or "").strip().lower()
    return value.startswith(("bv", "br")) and value.endswith("_streaming")


def _current_tts_provider() -> str:
    return (os.environ.get("DOUBAO_TTS_PROVIDER", "") or "standard").strip().lower()


def run_tts_delta(game_dir: Path, game: str, track: str, script: dict, manifest: dict,
                  changed: list[dict], tts_python: str, dry_run: bool):
    if not changed:
        return manifest
    if dry_run:
        print(f"[dry-run] TTS would regenerate: {', '.join(c['id'] for c in changed)}")
        return manifest
    delta_dir = game_dir / "tutorial" / "_tts_delta"
    if delta_dir.exists():
        shutil.rmtree(delta_dir)
    delta_dir.mkdir(parents=True, exist_ok=True)
    lrc = delta_dir / "delta.lrc"
    media = delta_dir / "media"
    write_delta_lrc(lrc, changed, {c["id"]: c.get("refs") or [] for c in script.get("cues") or []}, game, track)
    provider = _current_tts_provider()
    manifest_provider = (manifest.get("provider") or "").strip().lower()
    if manifest_provider not in {"standard", "seed2"}:
        manifest_provider = "standard" if _looks_like_standard_voice(manifest.get("voice")) else "seed2"

    cmd = [
        tts_python, str(ROOT / "animation" / "tts_doubao.py"),
        "--provider", provider,
        "--input", str(lrc), "--out-dir", str(media),
        "--force",
    ]
    # Only pass the manifest's voice/resource id when that manifest was
    # generated by the same provider.  Otherwise let tts_doubao.py use
    # DOUBAO_TTS_VOICE/defaults for the selected provider instead of passing a
    # stale seed-tts-2.0 voice (e.g. zh_female_vv_uranus_bigtts) to the
    # standard small-model endpoint.
    if manifest_provider == provider:
        if manifest.get("voice"):
            cmd += ["--voice", str(manifest["voice"])]
        if manifest.get("resource_id"):
            cmd += ["--resource-id", str(manifest["resource_id"])]

    # Keep TTS in a subprocess: --tts-python may deliberately point at a
    # different interpreter/venv that owns the provider SDK and credentials.
    print("[tts] " + " ".join(cmd))
    subprocess.run(cmd, cwd=ROOT, check=True)
    delta_manifest = load_json(media / "tts_manifest.json")

    # Propagate provider-level metadata so future incremental runs keep the
    # voice/resource selected for this delta.
    for key in ("provider", "voice", "resource_id", "format", "sample_rate", "subtitle_timing", "generator"):
        if key in delta_manifest:
            manifest[key] = delta_manifest[key]
    full_media = game_dir / "media" / "tts" / track
    full_media.mkdir(parents=True, exist_ok=True)
    by_id = {c["id"]: c for c in manifest.get("cues") or []}
    for dm in delta_manifest.get("cues") or []:
        cid = dm["id"]
        for ext in (".mp3", ".subtitle.json"):
            src = media / f"{cid}{ext}"
            if src.exists():
                shutil.copy2(src, full_media / src.name)
        by_id[cid] = {
            "id": cid,
            "group": dm.get("group", ""),
            "start": 0.0,
            "duration": round(float(dm.get("duration", 0.0) or 0.0), 3),
            "file": tts_asset_rel(game, track, cid, ".mp3"),
            "subtitle_file": tts_asset_rel(game, track, cid, ".subtitle.json"),
            "refs": dm.get("refs") or [],
        }
    manifest["cues"] = [by_id[c["id"]] for c in script.get("cues") or [] if c["id"] in by_id]
    shutil.rmtree(delta_dir, ignore_errors=True)
    print(f"[tts] regenerated {len(changed)} cue(s)")
    return manifest


def prune_removed(game_dir: Path, track: str, script: dict, manifest: dict, dry_run: bool):
    keep = {c["id"] for c in script.get("cues") or [] if c.get("id")}
    removed = [c["id"] for c in manifest.get("cues") or [] if c["id"] not in keep]
    if not removed:
        return manifest, []
    if dry_run:
        print(f"[dry-run] TTS would prune: {', '.join(removed)}")
    else:
        full_media = game_dir / "media" / "tts" / track
        for cid in removed:
            for ext in (".mp3", ".subtitle.json"):
                p = full_media / f"{cid}{ext}"
                if p.exists():
                    p.unlink()
        manifest["cues"] = [c for c in manifest.get("cues") or [] if c["id"] in keep]
        print(f"[tts] pruned {len(removed)} cue(s)")
    return manifest, removed


def update_lrc_refs(lrc_path: Path, script: dict):
    lrc_utils.update_lrc_refs(lrc_path, script)


def run_qa_gate(game: str, track: str, ids: list[str] | None = None) -> int:
    """Ask BoardAI the handwritten QA questions before compiling.

    ids=None -> all questions; ids=[...] -> only questions belonging to those
    cue ids (including action.cards.market.001.1#1 style suffixes).
    """
    import os
    import tempfile
    qa_path = qa_questions_path(game)
    if not qa_path.exists():
        print(f"[qa] missing questions file: {qa_path}", file=sys.stderr)
        return 2
    spec = load_json(qa_path)
    asks = spec.get("asks") or []
    if ids is not None:
        wanted = set(ids)
        selected = [a for a in asks if (a.get("cue", "").split("#", 1)[0] in wanted)]
    else:
        selected = asks
    if not selected:
        print("[qa] no matching questions; skip")
        return 0
    tmp = Path(tempfile.mkstemp(prefix="compile_qa_", suffix=".json")[1])
    try:
        tmp.write_text(json.dumps({"note": "compile gate", "asks": selected}, ensure_ascii=False, indent=2),
                       encoding="utf-8")
        cmd = ["--game", game, "--track", track,
               "--in", str(tmp), "--jobs", "4", "--tag", "compile_validation", "--strict"]
        print(f"[qa] validating {len(selected)} question(s) via BoardAI ...")
        had_api = os.environ.get("BOARDAI_API")
        os.environ.setdefault("BOARDAI_API", "http://localhost:5000/api/chat")
        try:
            rc = qa_anim_ask.main(cmd)
        finally:
            if had_api is None and "BOARDAI_API" in os.environ:
                del os.environ["BOARDAI_API"]
        if rc != 0:
            print(f"[qa] validation failed (rc={rc}); compile aborted", file=sys.stderr)
            return rc
        return 0
    finally:
        tmp.unlink(missing_ok=True)


def games_with_track(track: str) -> list[str]:
    """Return games that have the complete source/generated set for a track."""
    games = ROOT / "content" / "games"
    found = []
    for game_dir in sorted(games.iterdir() if games.exists() else []):
        if not game_dir.is_dir():
            continue
        has_script = (game_dir / "tutorial" / f"script.{track}.json").exists()
        has_anim = (game_dir / "tutorial" / "anim" / "v2" / f"{track}.anim.json").exists()
        has_manifest = (game_dir / "media" / "tts" / track / "tts_manifest.json").exists()
        if has_script and has_anim and has_manifest:
            found.append(game_dir.name)
    return sorted(found)


def resolve_game(game: str | None, track: str) -> str:
    """Use --game when given, otherwise pick the unique game that has the track."""
    if game:
        return game
    candidates = games_with_track(track)
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        raise SystemExit(f"找不到包含 track {track!r} 的游戏；请用 --game 指定")
    raise SystemExit(
        f"检测到多个候选游戏 {', '.join(candidates)}；请用 --game 指定")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=None, help="游戏 id；未指定时自动发现唯一的 track 来源")
    ap.add_argument("--track", default="full")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--skip-tts", action="store_true")
    ap.add_argument("--force-full-tts", action="store_true")
    ap.add_argument("--tts-python", default=sys.executable)
    ap.add_argument("--validate-qa", action="store_true", help="run qa_anim_ask.py for changed cues before compiling")
    ap.add_argument("--validate-qa-all", action="store_true", help="run the full handwritten QA set before compiling")
    args = ap.parse_args()
    _load_dotenv_if_present()

    try:
        args.game = resolve_game(args.game, args.track)
    except SystemExit as exc:
        print(str(exc), file=sys.stderr)
        return 2

    game_dir = ROOT / "content" / "games" / args.game
    tutorial = game_dir / "tutorial"
    script_path = tutorial / f"script.{args.track}.json"
    anim_path = tutorial / "anim" / "v2" / f"{args.track}.anim.json"
    manifest_path = game_dir / "media" / "tts" / args.track / "tts_manifest.json"
    tts_lrc_path = tutorial / f"{args.track}.tts.lrc"
    if not script_path.exists() or not anim_path.exists() or not manifest_path.exists():
        print(f"missing source: {script_path} / {anim_path} / {manifest_path}", file=sys.stderr)
        return 2

    script = load_json(script_path)
    manifest = normalize_manifest_paths(load_json(manifest_path))
    tts_text = read_current_tts_text(tts_lrc_path)
    manifest_by = {c["id"]: c for c in manifest.get("cues") or []}

    changed = []
    refs_changed = []
    for cue in script.get("cues") or []:
        cid = cue.get("id")
        if not cid:
            continue
        text = cue_text(cue)
        refs = cue.get("refs") or []
        old_text = tts_text.get(cid)
        m = manifest_by.get(cid)
        audio_ok = manifest_audio_ok(m)
        if args.force_full_tts or old_text is None or old_text != text or not audio_ok:
            changed.append(cue)
        elif (m.get("refs") or []) != refs:
            refs_changed.append(cid)

    if not args.dry_run:
        qa_ids = None if args.validate_qa_all else ([c["id"] for c in changed] if args.validate_qa else [])
        if qa_ids is None or qa_ids:
            if qa_ids == []:
                pass
            else:
                rc = run_qa_gate(args.game, args.track, qa_ids)
                if rc != 0:
                    return rc

    manifest, removed = prune_removed(game_dir, args.track, script, manifest, args.dry_run)
    if changed:
        if args.skip_tts:
            print(f"[tts] {len(changed)} cue(s) need regeneration, but --skip-tts is set", file=sys.stderr)
            return 1
        manifest = run_tts_delta(game_dir, args.game, args.track, script, manifest, changed, args.tts_python, args.dry_run)

    # Ref-only changes still need manifest update.
    script_by = {c["id"]: c for c in script.get("cues") or []}
    for m in manifest.get("cues") or []:
        c = script_by.get(m["id"])
        if c is not None and (m.get("refs") or []) != (c.get("refs") or []):
            m["refs"] = c.get("refs") or []

    if args.dry_run:
        print(f"[dry-run] changed text cues: {len(changed)}, removed: {len(removed)}, ref-only: {len(refs_changed)}")
        return 0

    # Source-of-truth is script order; align manifest with it.
    manifest["cues"] = [m for m in manifest.get("cues") or [] if m["id"] in script_by]
    manifest["cues"].sort(key=lambda m: [c["id"] for c in script["cues"]].index(m["id"]))
    if changed or removed:
        recompute_starts(manifest)
    save_json(manifest_path, manifest)

    # Derived narration timeline + runtime.
    rebuild_lrc(script, tutorial / f"{args.track}.lrc")
    narration_changed = bool(changed or removed)
    if narration_changed:
        write_tts_lrc(game_dir, args.track, script, manifest)
        build_runtime(args.game, args.track, force=True)
    elif refs_changed:
        update_lrc_refs(tts_lrc_path, script)
        build_runtime(args.game, args.track, force=True)

    # Animation compiler (cheap; always deterministic for the whole track).
    rc = animation_compiler.main(["--game", args.game, "--track", args.track])
    if rc != 0:
        return rc

    # Lightweight checks.
    rc = rule_ledger.main(["--game", args.game, "--track", args.track])
    if rc != 0:
        return rc
    print(f"OK   compiled {args.game}/{args.track}: tts_regenerated={len(changed)} removed={len(removed)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
