#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Incremental top-level compiler for a tutorial track.

Source:
    games/{game}/tutorial/script.{track}.json      narration cues
    games/{game}/tutorial/anim/v2/{track}.anim.json animation graph/events

Generated:
    media/tts/{track}/*.mp3 + *.subtitle.json
    tutorial/{track}.lrc           estimated narration timeline
    tutorial/{track}.tts.lrc       actual TTS timeline
    media/tts/{track}/tts_manifest.json
    tutorial/{track}.runtime.json
    tutorial/anim/v2/{track}.compiled.json

Usage:
    python scripts/compile_tutorial.py --game splendor --track full --dry-run
    python scripts/compile_tutorial.py --game splendor --track full --skip-tts
    python scripts/compile_tutorial.py --game splendor --track full
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import anim_schema_v2 as schema  # noqa: E402
from build_tutorial_runtime import build_runtime  # noqa: E402
from tutorial_script_tool import rebuild_lrc  # noqa: E402
from validate_timed_script import parse_file  # noqa: E402


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, doc):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


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
    lrc_path = game_dir / "tutorial" / f"{track}.tts.lrc"
    manifest_by = {c["id"]: c for c in manifest.get("cues") or []}
    lines = [
        f"[ti:{script.get('title','')}]",
        f"[game:{script.get('game_id','')}]",
        f"[track:{script.get('track','')}]",
        "[timing:tts]",
        "[generator:scripts/compile_tutorial.py]",
    ]
    if script.get("version"):
        lines.append(f"[version:{script['version']}]")

    cursor = 0.0
    last_path = []
    gap = float(manifest.get("gap_seconds", 0.0) or 0.0)
    for cue in script.get("cues") or []:
        cid = cue.get("id")
        if not cid:
            continue
        m = manifest_by.get(cid)
        if m is None:
            raise SystemExit(f"manifest missing cue: {cid}")
        path = cue.get("group_path") or ([cue.get("group")] if cue.get("group") else [])
        common = 0
        while common < len(path) and common < len(last_path) and path[common] == last_path[common]:
            common += 1
        for title in path[common:]:
            lines.append(f"[group:{title}]")
        last_path = path
        start = float(m.get("start", cursor) or 0.0)
        cs = int(round(start * 100))
        stamp = f"[{cs // 6000:02d}:{(cs % 6000) / 100:05.2f}]"
        refs = cue.get("refs") or []
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"{stamp}[id:{cid}]{ref_tag}{cue_text(cue)}")
        cursor = start + float(m.get("duration", 0.0) or 0.0) + gap
    cs = int(round(cursor * 100))
    lines.append(f"[length:{cs // 6000:02d}:{(cs % 6000) / 100:05.2f}]")
    lrc_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return lrc_path


def write_delta_lrc(path: Path, cues: list[dict], refs_by_id: dict):
    lines = ["[ti:delta]", "[game:splendor]", "[track:full]", "[timing:estimated]"]
    last_group = None
    for cue in cues:
        group = cue.get("group") or ""
        if group != last_group:
            lines.append(f"[group:{group}]")
            last_group = group
        refs = refs_by_id.get(cue["id"]) or []
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"[00:00.00][id:{cue['id']}]{ref_tag}{cue_text(cue)}")
    lines.append("[length:23:59.99]")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def run_tts_delta(game_dir: Path, track: str, script: dict, manifest: dict,
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
    write_delta_lrc(lrc, changed, {c["id"]: c.get("refs") or [] for c in script.get("cues") or []})
    cmd = [
        tts_python, str(ROOT / "scripts" / "tts_doubao.py"),
        "--input", str(lrc), "--out-dir", str(media),
        "--voice", manifest.get("voice", ""),
        "--resource-id", manifest.get("resource_id", ""),
        "--force",
    ]
    print("[tts] " + " ".join(cmd))
    subprocess.run(cmd, cwd=ROOT, check=True)
    delta_manifest = load_json(media / "tts_manifest.json")
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
            "file": f"games/splendor/media/tts/{track}/{cid}.mp3",
            "subtitle_file": f"games/splendor/media/tts/{track}/{cid}.subtitle.json",
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
    script_by = {c["id"]: c for c in script.get("cues") or []}
    lines = lrc_path.read_text(encoding="utf-8").splitlines()
    out = []
    for line in lines:
        m = re.match(r'^(\[[0-9:.]+\]\[id:([^\]]+)\])(?:\[ref:[^\]]*\])?(.*)$', line)
        if m and m.group(2) in script_by:
            refs = script_by[m.group(2)].get("refs") or []
            ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
            out.append(m.group(1) + ref_tag + m.group(3))
        else:
            out.append(line)
    lrc_path.write_text("\n".join(out) + "\n", encoding="utf-8")


def run_qa_gate(ids: list[str] | None = None) -> int:
    """Ask BoardAI the handwritten QA questions before compiling.

    ids=None -> all questions; ids=[...] -> only questions belonging to those
    cue ids (including action.cards.market.001.2#1 style suffixes).
    """
    import os
    import tempfile
    qa_path = ROOT / "games" / "splendor" / "tutorial" / "anim" / "_qa" / "questions.json"
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
        env = os.environ.copy()
        env.setdefault("BOARDAI_API", "http://localhost:5000/api/chat")
        cmd = [sys.executable, str(ROOT / "scripts" / "qa_anim_ask.py"),
               "--in", str(tmp), "--jobs", "4", "--tag", "compile_validation", "--strict"]
        print(f"[qa] validating {len(selected)} question(s) via BoardAI ...")
        rc = subprocess.run(cmd, cwd=ROOT, env=env).returncode
        if rc != 0:
            print(f"[qa] validation failed (rc={rc}); compile aborted", file=sys.stderr)
            return rc
        return 0
    finally:
        tmp.unlink(missing_ok=True)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="full")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--skip-tts", action="store_true")
    ap.add_argument("--force-full-tts", action="store_true")
    ap.add_argument("--tts-python", default=sys.executable)
    ap.add_argument("--validate-qa", action="store_true", help="run qa_anim_ask.py for changed cues before compiling")
    ap.add_argument("--validate-qa-all", action="store_true", help="run the full handwritten QA set before compiling")
    args = ap.parse_args()

    game_dir = ROOT / "games" / args.game
    tutorial = game_dir / "tutorial"
    script_path = tutorial / f"script.{args.track}.json"
    anim_path = tutorial / "anim" / "v2" / f"{args.track}.anim.json"
    manifest_path = game_dir / "media" / "tts" / args.track / "tts_manifest.json"
    tts_lrc_path = tutorial / f"{args.track}.tts.lrc"
    if not script_path.exists() or not anim_path.exists() or not manifest_path.exists():
        print(f"missing source: {script_path} / {anim_path} / {manifest_path}", file=sys.stderr)
        return 2

    script = load_json(script_path)
    manifest = load_json(manifest_path)
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
        audio_ok = bool(m) and (ROOT / m.get("file", "")).exists()
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
                rc = run_qa_gate(qa_ids)
                if rc != 0:
                    return rc

    manifest, removed = prune_removed(game_dir, args.track, script, manifest, args.dry_run)
    if changed:
        if args.skip_tts:
            print(f"[tts] {len(changed)} cue(s) need regeneration, but --skip-tts is set", file=sys.stderr)
            return 1
        manifest = run_tts_delta(game_dir, args.track, script, manifest, changed, args.tts_python, args.dry_run)

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
    subprocess.run([sys.executable, str(ROOT / "scripts" / "compile_animation_v2.py"),
                    "--game", args.game, "--track", args.track], check=True)

    # Lightweight checks.
    subprocess.run([sys.executable, str(ROOT / "scripts" / "validate_anim_rules_v2.py"),
                    "--game", args.game, "--track", args.track], check=True)
    print(f"OK   compiled {args.game}/{args.track}: tts_regenerated={len(changed)} removed={len(removed)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
