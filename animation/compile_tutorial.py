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


# ── QA source loading and change detection ─────────────────────────────────
# Presentation-only event ops do not require board-state QA.  Anything else
# (including future primitive ops) is treated as stateful, so this gate fails
# closed rather than silently skipping an unknown operation.
_QA_PRESENTATION_EVENT_OPS = frozenset({
    "camera", "wait", "label", "point", "shape", "fade", "scale",
    "highlight", "overlay_show", "overlay_hide", "magnifier", "show", "hide",
})


def _canonical_json(value) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"), default=str)


def event_changes_board_state(event: dict) -> bool:
    """Return True when an event can change board state (QA-relevant)."""
    op = str((event or {}).get("op") or "")
    return op not in _QA_PRESENTATION_EVENT_OPS


def qa_change_signature(cue: dict) -> dict:
    """Extract the cue parts whose change requires a QA question refresh."""
    cue = cue or {}
    script = cue.get("script") if isinstance(cue.get("script"), dict) else {}
    events = cue.get("events") if isinstance(cue.get("events"), list) else []
    return {
        "entry": cue.get("entry"),
        "parent": cue.get("parent"),
        "tree": cue.get("tree"),
        "transition": cue.get("transition", "continue"),
        "negative": cue.get("negative"),
        "demo": cue.get("demo"),
        "script_enter": script.get("enter"),
        "script_exit": script.get("exit"),
        "state_events": [
            event for event in events
            if isinstance(event, dict) and event_changes_board_state(event)
        ],
    }


def cue_has_qa_relevant_change(before: dict | None, after: dict | None) -> bool:
    """Canonical comparison of one cue against its baseline version."""
    if before is None or after is None:
        return True
    return _canonical_json(qa_change_signature(before)) != _canonical_json(qa_change_signature(after))


def load_baseline_track_doc(game: str, track: str) -> dict | None:
    """Read the committed baseline anim JSON from HEAD (not mtime/whole-file diff)."""
    rel = f"content/games/{game}/tutorial/anim/v2/{track}.anim.json"
    try:
        proc = subprocess.run(
            ["git", "-C", str(ROOT), "show", f"HEAD:{rel}"],
            capture_output=True, text=True, check=True,
        )
    except (OSError, subprocess.CalledProcessError):
        return None
    try:
        doc = json.loads(proc.stdout)
    except json.JSONDecodeError:
        return None
    return doc if isinstance(doc, dict) else None


def _qa_effective_track_doc(doc: dict) -> dict:
    """Resolve inheritance for QA selection, falling back to the raw document."""
    try:
        resolved = schema.resolve_track(doc)
    except Exception:  # noqa: BLE001 - QA selection should not crash on malformed drafts
        return doc
    if isinstance(resolved, dict) and isinstance(resolved.get("cues"), list):
        return resolved
    return doc


def select_qa_changed_ids(track_doc: dict, baseline_doc: dict | None,
                          tts_changed_ids: list[str] | None = None) -> list[str]:
    """Select cue IDs that require QA for the incremental `--validate-qa` gate.

    Text/narration changes are passed in through ``tts_changed_ids`` (they are
    already detected by the TTS/LRC comparison).  This function adds any cue
    whose contract / inheritance / state-changing events changed vs HEAD.
    """
    track_doc = _qa_effective_track_doc(track_doc or {})
    if isinstance(baseline_doc, dict):
        baseline_doc = _qa_effective_track_doc(baseline_doc)

    current_by: dict[str, dict] = {}
    ordered: list[str] = []
    for cue in (track_doc or {}).get("cues") or []:
        if not isinstance(cue, dict):
            continue
        cid = cue.get("id")
        if cid and cid not in current_by:
            current_by[cid] = cue
            ordered.append(cid)

    changed = {cid for cid in (tts_changed_ids or []) if cid in current_by}
    if not isinstance(baseline_doc, dict):
        changed.update(ordered)
        return ordered[:]

    baseline_by: dict[str, dict] = {}
    for cue in baseline_doc.get("cues") or []:
        if isinstance(cue, dict) and cue.get("id"):
            baseline_by[cue["id"]] = cue

    for cid in ordered:
        old = baseline_by.get(cid)
        if old is None or cue_has_qa_relevant_change(old, current_by[cid]):
            changed.add(cid)
    return [cid for cid in ordered if cid in changed]


def _normalise_qa_question(item, cue_id: str, original_cue: str, source: str) -> dict:
    if isinstance(item, str):
        question = item.strip()
        expect = None
    elif isinstance(item, dict):
        question = str(item.get("q") or "").strip()
        expect = item.get("expect")
        if expect is not None:
            expect = str(expect)
    else:
        return {}
    if not question:
        return {}
    return {
        "cue": cue_id,
        "q": question,
        "expect": expect,
        "source": source,
        "original_cue": original_cue,
    }


def cue_qa_asks(track_doc: dict, source_label: str) -> list[dict]:
    """Extract hand-written QA embedded in each cue's ``qa`` field."""
    asks: list[dict] = []
    for cue in (track_doc or {}).get("cues") or []:
        if not isinstance(cue, dict) or not cue.get("id"):
            continue
        cid = cue["id"]
        raw = cue.get("qa")
        if raw is None:
            continue
        items = raw if isinstance(raw, list) else [raw]
        for index, item in enumerate(items):
            ask = _normalise_qa_question(
                item, cue_id=cid, original_cue=cid,
                source=f"{source_label}#{cid}.qa[{index}]",
            )
            if ask:
                asks.append(ask)
    return asks


def external_qa_asks(spec: dict, source_label: str) -> list[dict]:
    """Extract hand-written QA from the legacy ``_qa/questions.json`` file."""
    asks: list[dict] = []
    raw_items = spec.get("asks") if isinstance(spec, dict) else None
    if not isinstance(raw_items, list):
        return asks
    for index, item in enumerate(raw_items):
        if not isinstance(item, dict):
            continue
        original_cue = str(item.get("cue") or "").strip()
        main_cue = original_cue.split("#", 1)[0]
        ask = _normalise_qa_question(
            item, cue_id=main_cue, original_cue=original_cue,
            source=f"{source_label}#asks[{index}]",
        )
        if ask:
            asks.append(ask)
    return asks


def merge_qa_asks(*groups: list[dict]) -> list[dict]:
    """Merge asks from all sources, de-duplicating by (main cue, question)."""
    merged: dict[tuple[str, str], dict] = {}
    for group in groups:
        for ask in group:
            key = (ask.get("cue", ""), ask.get("q", ""))
            existing = merged.get(key)
            if existing is None:
                item = dict(ask)
                item["sources"] = [ask.get("source", "")]
                item["original_cues"] = [ask.get("original_cue", "")]
                merged[key] = item
                continue
            source = ask.get("source", "")
            original_cue = ask.get("original_cue", "")
            if source and source not in existing.setdefault("sources", []):
                existing["sources"].append(source)
            if original_cue and original_cue not in existing.setdefault("original_cues", []):
                existing["original_cues"].append(original_cue)
            # Preserve a hand-written expected verdict even when the duplicate
            # from another source omitted it.
            if not existing.get("expect") and ask.get("expect"):
                existing["expect"] = ask["expect"]
    return list(merged.values())


def _qa_source_label(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return path.as_posix()


def invalid_external_qa_cues(spec: dict, valid_cue_ids: set[str]) -> list[str]:
    """Return external cue references that do not exist in the current track."""
    invalid: list[str] = []
    raw_items = spec.get("asks") if isinstance(spec, dict) else None
    if not isinstance(raw_items, list):
        return invalid
    for index, item in enumerate(raw_items):
        if not isinstance(item, dict):
            continue
        original = str(item.get("cue") or "").strip()
        main = original.split("#", 1)[0]
        if not main or main not in valid_cue_ids:
            invalid.append(original or f"<asks[{index}] missing cue>")
    return invalid


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


def run_qa_gate(game: str, track: str, ids: list[str] | None = None,
                  track_doc: dict | None = None) -> int:
    """Ask BoardAI the handwritten QA questions before compiling.

    Questions are merged from two hand-written sources:
      1. each cue's ``qa`` field in ``{track}.anim.json``;
      2. the legacy ``_qa/questions.json`` file.
    ``ids=None`` -> all merged questions; ``ids=[...]`` -> only questions for
    those main cue IDs.  A selected cue without any merged question is an error
    (fail closed), and stale external cue references always fail.
    """
    import os
    import tempfile

    if track_doc is None:
        anim_path = ROOT / "content" / "games" / game / "tutorial" / "anim" / "v2" / f"{track}.anim.json"
        try:
            track_doc = load_json(anim_path)
        except (OSError, json.JSONDecodeError) as exc:
            print(f"[qa] cannot read track for cue validation: {anim_path}: {exc}", file=sys.stderr)
            return 2

    valid_cue_ids = {
        cue.get("id") for cue in (track_doc or {}).get("cues") or []
        if isinstance(cue, dict) and cue.get("id")
    }
    qa_path = qa_questions_path(game)
    external_asks: list[dict] = []
    if qa_path.exists():
        try:
            spec = load_json(qa_path)
        except (OSError, json.JSONDecodeError) as exc:
            print(f"[qa] cannot read questions file: {qa_path}: {exc}", file=sys.stderr)
            return 2
        invalid = invalid_external_qa_cues(spec, valid_cue_ids)
        if invalid:
            unique = ", ".join(sorted(set(invalid)))
            print(f"[qa] stale external cue id(s) in {_qa_source_label(qa_path)}: {unique}",
                  file=sys.stderr)
            return 1
        external_asks = external_qa_asks(spec, _qa_source_label(qa_path))
    else:
        print(f"[qa] external questions not found; using cue qa fields only: "
              f"{_qa_source_label(qa_path)}", file=sys.stderr)

    anim_label = f"content/games/{game}/tutorial/anim/v2/{track}.anim.json"
    merged = merge_qa_asks(cue_qa_asks(track_doc, anim_label), external_asks)

    if ids is None:
        selected = merged
    else:
        wanted = set(ids)
        selected = [ask for ask in merged if ask.get("cue") in wanted]
        available = {ask.get("cue") for ask in merged}
        missing = [cid for cid in ids if cid not in available]
        if missing:
            print(f"[qa] missing required QA for cue(s): {', '.join(missing)} "
                  f"(hand-written sources: cue.qa + {_qa_source_label(qa_path)})",
                  file=sys.stderr)
            return 1

    if not selected:
        print("[qa] no matching questions; skip")
        return 0

    fd, tmp_name = tempfile.mkstemp(prefix="compile_qa_", suffix=".json")
    os.close(fd)  # Windows: close the handle before qa_anim_ask opens/writes it
    tmp = Path(tmp_name)
    try:
        tmp.write_text(json.dumps({"note": "compile gate", "asks": selected},
                                  ensure_ascii=False, indent=2), encoding="utf-8")
        cmd = ["--game", game, "--track", track,
               "--in", str(tmp), "--jobs", "4", "--tag", "compile_validation", "--strict"]
        cue_ids = sorted({ask.get("cue", "?") for ask in selected})
        print(f"[qa] validating {len(selected)} question(s) via BoardAI for {len(cue_ids)} cue(s) ...")
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
    anim_doc = load_json(anim_path)
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

    if not args.dry_run and (args.validate_qa or args.validate_qa_all):
        if args.validate_qa_all:
            qa_ids = None
            print("[qa] loading all merged hand-written questions (cue.qa + external)")
        else:
            baseline_doc = load_baseline_track_doc(args.game, args.track)
            if baseline_doc is None:
                cue_count = len(anim_doc.get("cues") or [])
                print(f"[qa] no committed baseline for {args.game}/{args.track}; "
                      f"treating all {cue_count} cue(s) as changed", file=sys.stderr)
            qa_ids = select_qa_changed_ids(
                anim_doc, baseline_doc, [c["id"] for c in changed])
            preview = ", ".join(qa_ids[:10])
            if len(qa_ids) > 10:
                preview += f", ... (+{len(qa_ids) - 10})"
            print(f"[qa] selected {len(qa_ids)} changed cue(s) for QA: {preview or '(none)'}")
        rc = run_qa_gate(args.game, args.track, qa_ids, track_doc=anim_doc)
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
