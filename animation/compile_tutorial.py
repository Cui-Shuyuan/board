#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Incremental top-level compiler for a tutorial track.

Source (section-first):
    content/games/{game}/tutorial/tracks/{track}.track.json    ordered sections
    content/games/{game}/tutorial/sections/<id>/{track}.script.json  narration cues
    content/games/{game}/tutorial/sections/<id>/{track}.anim.json    animation graph/events

Generated TTS asset names come from each cue's explicit ``tts_asset``; files
live together under ``media/tts/{track}/`` and keep that original name.

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
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))

import anim_schema_v2 as schema  # noqa: E402
import compile_animation_v2 as animation_compiler  # noqa: E402
import lrc as lrc_utils  # noqa: E402
import section_source  # noqa: E402
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
        "section": cue.get("section"),
        "tree": cue.get("tree"),
        "transition": cue.get("transition", "continue"),
        "negative": cue.get("negative"),
        "demo": cue.get("demo"),
        "qa_exempt": cue.get("qa_exempt"),
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


def _git_show_bytes(rel: str) -> bytes | None:
    try:
        proc = subprocess.run(
            ["git", "-C", str(ROOT), "show", f"HEAD:{rel}"],
            capture_output=True, check=True,
        )
    except (OSError, subprocess.CalledProcessError):
        return None
    return proc.stdout


def _load_section_baseline_from_head(game: str, track: str) -> dict | None:
    """Resolve the section source as committed at HEAD, without touching the worktree."""
    tutorial_rel = f"content/games/{game}/tutorial"
    descriptor_rel = f"{tutorial_rel}/tracks/{track}.track.json"
    try:
        listed = subprocess.run(
            ["git", "-C", str(ROOT), "ls-tree", "-r", "--name-only", "-z", "HEAD", "--", tutorial_rel],
            capture_output=True, check=True,
        ).stdout.decode("utf-8").split("\0")
    except (OSError, subprocess.CalledProcessError, UnicodeDecodeError):
        return None
    files = [rel for rel in listed if rel]
    if descriptor_rel not in files:
        return None
    with tempfile.TemporaryDirectory(prefix="boardai_section_baseline_") as tmp:
        root = Path(tmp)
        for rel in files:
            base = rel[len(tutorial_rel) + 1:] if rel.startswith(tutorial_rel + "/") else rel
            keep = (
                base == f"tracks/{track}.track.json"
                or base.startswith("shared/")
                or base.startswith("sections/")
            )
            if not keep:
                continue
            data = _git_show_bytes(rel)
            if data is None:
                return None
            target = root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        saved_root = section_source.ROOT
        try:
            section_source.ROOT = root  # type: ignore[attr-defined]
            source = section_source.load_track_source(game, track)
        except Exception:  # noqa: BLE001 - malformed historical snapshot means no QA baseline
            return None
        finally:
            section_source.ROOT = saved_root  # type: ignore[attr-defined]
        return source.anim_doc


def load_baseline_track_doc(game: str, track: str) -> dict | None:
    """Read the committed baseline animation doc from HEAD (not mtime/whole-file diff)."""
    if section_source.has_section_source(game, track):
        section_baseline = _load_section_baseline_from_head(game, track)
        if section_baseline is not None:
            return section_baseline
    rel = f"content/games/{game}/tutorial/anim/v2/{track}.anim.json"
    raw = _git_show_bytes(rel)
    if raw is None:
        return None
    try:
        doc = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError):
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


TTS_ASSET_KEY = "tts_asset"


def cue_tts_asset(cue: dict, cue_id: str | None = None) -> str:
    """Return the explicit TTS asset base name for one cue.

    Assets are stored together under ``media/tts/{track}/`` and keep their
    authored/original names; a cue references the asset by name instead of the
    build deriving a section/cue-specific file name.  Legacy fixtures without
    an explicit reference fall back to the old stable slug.
    """
    raw = (cue or {}).get(TTS_ASSET_KEY)
    name = str(raw).strip() if raw is not None else ""
    if name:
        if name in {".", ".."} or "/" in name or "\\" in name or "\x00" in name:
            raise SystemExit(f"invalid {TTS_ASSET_KEY} {raw!r}: use a plain file base name")
        return name
    cid = str(cue_id or (cue or {}).get("id") or "").strip()
    return section_source.cue_id_to_slug(cid)


def tts_asset_rel(game: str, track: str, asset_name: str, ext: str) -> str:
    """Repository-relative manifest path for one generated TTS asset.

    ``asset_name`` is the cue's explicit ``tts_asset`` (original asset name),
    not a cue-id-derived slug.
    """
    return f"content/games/{game}/media/tts/{track}/{asset_name}{ext}"


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
    by_change = {c["id"]: c for c in changed}
    assets = {
        c["id"]: str(c.get("asset") or cue_tts_asset(c, c.get("id"))).strip()
        for c in changed
    }
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
    # tts_doubao writes one file per cue id.  The cue owns the asset name, so
    # move the temp output to its original asset name before copying it to the
    # shared TTS folder.  No re-encoding happens here.
    for cid, asset in assets.items():
        temp_stem = section_source.cue_id_to_slug(cid)
        if temp_stem == asset:
            continue
        for ext in (".mp3", ".subtitle.json", ".wav"):
            src = media / f"{temp_stem}{ext}"
            dst = media / f"{asset}{ext}"
            if src.exists():
                src.replace(dst)
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
        asset = assets.get(cid) or section_source.cue_id_to_slug(cid)
        for ext in (".mp3", ".subtitle.json"):
            src = media / f"{asset}{ext}"
            if src.exists():
                shutil.copy2(src, full_media / src.name)
        by_id[cid] = {
            "id": cid,
            "asset": asset,
            "text": dm.get("text") or cue_text(by_change.get(cid, {})),
            "group": dm.get("group", ""),
            "group_path": dm.get("group_path") or [],
            "cue_ids": dm.get("cue_ids") or by_change.get(cid, {}).get("cue_ids") or [],
            "start": 0.0,
            "duration": round(float(dm.get("duration", 0.0) or 0.0), 3),
            "file": tts_asset_rel(game, track, asset, ".mp3"),
            "subtitle_file": tts_asset_rel(game, track, asset, ".subtitle.json"),
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
        manifest_by_id = {c["id"]: c for c in manifest.get("cues") or [] if isinstance(c, dict) and c.get("id")}
        for cid in removed:
            m = manifest_by_id.get(cid) or {}
            asset = m.get("asset") or section_source.cue_id_to_slug(cid)
            for key, ext in (("file", ".mp3"), ("subtitle_file", ".subtitle.json")):
                rel = normalize_manifest_path(m.get(key) or "")
                p = (ROOT / rel) if rel else (full_media / f"{asset}{ext}")
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
    (fail closed), unless the cue explicitly declares ``qa_exempt: true``;
    stale external cue references always fail.
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

    qa_doc = _qa_effective_track_doc(track_doc or {})
    valid_cue_ids = {
        cue.get("id") for cue in (qa_doc or {}).get("cues") or []
        if isinstance(cue, dict) and cue.get("id")
    }
    exempt_ids = {
        str(cue.get("id")) for cue in (qa_doc or {}).get("cues") or []
        if isinstance(cue, dict) and cue.get("id") and cue.get("qa_exempt") is True
    }
    external_asks: list[dict] = []
    if section_source.has_section_source(game, track):
        try:
            section_spec = qa_anim_ask.load_section_asks(game, track)
        except FileNotFoundError as exc:
            print(f"[qa] cannot load section QA: {exc}", file=sys.stderr)
            return 2
        merged = list(section_spec.get("asks") or [])
        anim_label = f"content/games/{game}/tutorial/tracks/{track}.track.json"
    else:
        qa_path = qa_questions_path(game)
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
        merged = merge_qa_asks(cue_qa_asks(qa_doc, anim_label), external_asks)

    if ids is None:
        selected = merged
    else:
        wanted = set(ids)
        selected = [ask for ask in merged if ask.get("cue") in wanted]
        available = {ask.get("cue") for ask in merged}
        missing = [cid for cid in ids
                   if cid not in available and str(cid) not in exempt_ids]
        if missing:
            source_hint = anim_label if section_source.has_section_source(game, track) else (
                _qa_source_label(qa_questions_path(game)))
            print(f"[qa] missing required QA for cue(s): {', '.join(missing)} "
                  f"(hand-written sources: cue.qa + {source_hint})",
                  file=sys.stderr)
            return 1
        exempt_without_question = [cid for cid in ids
                                   if str(cid) in exempt_ids and cid not in available]
        if exempt_without_question:
            print(f"[qa] qa_exempt=true, no handwritten question required: "
                  f"{', '.join(exempt_without_question)}")

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

def _valid_tts_asset_name(name: str) -> str:
    value = str(name or "").strip()
    if not value or value in {".", ".."} or "/" in value or "\\" in value or "\x00" in value:
        raise SystemExit(f"invalid section tts_asset {name!r}: use a plain file base name")
    return value


def build_section_tts_entries(track_source) -> list[dict]:
    """One TTS entry per authored section; cue texts are concatenated for prosody."""
    cues_by_section: dict[str, list[dict]] = {}
    for cue in track_source.script_doc.get("cues") or []:
        if not isinstance(cue, dict) or not cue.get("id"):
            continue
        cues_by_section.setdefault(str(cue.get("section") or ""), []).append(cue)

    entries: list[dict] = []
    for sec in track_source.sections:
        cues = cues_by_section.get(sec.id) or []
        if not cues:
            raise SystemExit(f"section {sec.id!r} has no script cues")
        text = "".join(cue_text(c) for c in cues)
        if not text.strip():
            raise SystemExit(f"section {sec.id!r} has empty TTS text")
        asset = _valid_tts_asset_name(sec.meta.get("tts_asset") or sec.id)
        refs: list[str] = []
        for c in cues:
            for ref in c.get("refs") or []:
                if ref not in refs:
                    refs.append(ref)
        first = cues[0]
        group = first.get("group") or sec.meta.get("title") or sec.id
        group_path = first.get("group_path") or sec.meta.get("group_path") or [group]
        entries.append({
            "id": sec.id,
            "asset": asset,
            "text": text,
            "group": group,
            "group_path": list(group_path),
            "refs": refs,
            "pause_after": 0.0,
            "cue_ids": [str(c["id"]) for c in cues],
            "beats": [{"id": f"{sec.id}.tts", "text": text}],
        })
    return entries


def _main_section_mode(args, track_source, *, script: dict, source_root: Path | None = None) -> int:
    """Section-level TTS pipeline: one continuous audio passage per section."""
    game_dir = ROOT / "content" / "games" / args.game
    tutorial = game_dir / "tutorial"
    manifest_path = game_dir / "media" / "tts" / args.track / "tts_manifest.json"
    tts_lrc_path = tutorial / f"{args.track}.tts.lrc"
    if not manifest_path.exists():
        print(f"missing TTS manifest: {manifest_path}", file=sys.stderr)
        return 2

    entries = build_section_tts_entries(track_source)
    pseudo_script = {
        "title": track_source.script_doc.get("title") or f"{args.game} {args.track}",
        "game_id": args.game,
        "track": args.track,
        "version": track_source.script_doc.get("version"),
        "cues": entries,
    }
    manifest = normalize_manifest_paths(load_json(manifest_path))
    tts_text = read_current_tts_text(tts_lrc_path)
    manifest_by = {c["id"]: c for c in manifest.get("cues") or [] if isinstance(c, dict) and c.get("id")}

    changed: list[dict] = []
    refs_changed: list[str] = []
    for entry in entries:
        cid = entry["id"]
        m = manifest_by.get(cid)
        old_text = tts_text.get(cid)
        if (args.force_full_tts or old_text is None or old_text != entry["text"]
                or not manifest_audio_ok(m)):
            changed.append(entry)
        elif (m.get("refs") or []) != entry["refs"]:
            refs_changed.append(cid)

    if not args.dry_run and (args.validate_qa or args.validate_qa_all):
        if args.validate_qa_all:
            qa_ids = None
        else:
            baseline_doc = load_baseline_track_doc(args.game, args.track)
            qa_ids = select_qa_changed_ids(
                track_source.anim_doc,
                baseline_doc,
                [cid for entry in changed for cid in entry["cue_ids"]],
            )
        rc = run_qa_gate(args.game, args.track, qa_ids, track_doc=track_source.anim_doc)
        if rc != 0:
            return rc

    manifest, removed = prune_removed(game_dir, args.track, {"cues": entries}, manifest, args.dry_run)
    if args.dry_run:
        print(
            f"[dry-run] changed text sections: {len(changed)}, removed: {len(removed)}, "
            f"ref-only: {len(refs_changed)}"
        )
        return 0

    if changed:
        if args.skip_tts:
            print(
                f"[tts] {len(changed)} section(s) need regeneration, but --skip-tts is set",
                file=sys.stderr,
            )
            return 1
        manifest = run_tts_delta(
            game_dir, args.game, args.track, pseudo_script, manifest, changed,
            args.tts_python, args.dry_run,
        )

    # Normalize manifest to the current section list while keeping TTS timing.
    by_id = {c["id"]: c for c in manifest.get("cues") or [] if isinstance(c, dict) and c.get("id")}
    ordered: list[dict] = []
    for entry in entries:
        m = by_id.get(entry["id"])
        if m is None:
            # Complete sections can be present without being regenerated above;
            # if a manifest is missing one, fail instead of silently dropping it.
            print(f"missing TTS manifest section: {entry['id']}", file=sys.stderr)
            return 2
        for key in ("asset", "text", "group", "group_path", "refs", "cue_ids"):
            m[key] = entry[key]
        ordered.append(m)
    manifest["units"] = "section"
    manifest["cues"] = ordered
    recompute_starts(manifest)
    save_json(manifest_path, manifest)

    rebuild_lrc(script, tutorial / f"{args.track}.lrc")
    write_tts_lrc(game_dir, args.track, pseudo_script, manifest)
    build_runtime(args.game, args.track, force=True)

    rc = animation_compiler.main(["--game", args.game, "--track", args.track])
    if rc != 0:
        return rc
    rc = rule_ledger.main(["--game", args.game, "--track", args.track])
    if rc != 0:
        return rc
    print(
        f"OK   compiled {args.game}/{args.track}: section-tts_regenerated={len(changed)} "
        f"removed={len(removed)}"
    )
    return 0


def games_with_track(track: str) -> list[str]:
    """Return games that have a complete authored track source for ``track``."""
    games = ROOT / "content" / "games"
    found = []
    for game_dir in sorted(games.iterdir() if games.exists() else []):
        if not game_dir.is_dir():
            continue
        game = game_dir.name
        if section_source.has_section_source(game, track):
            found.append(game)
            continue
        has_script = (game_dir / "tutorial" / f"script.{track}.json").exists()
        has_anim = (game_dir / "tutorial" / "anim" / "v2" / f"{track}.anim.json").exists()
        has_manifest = (game_dir / "media" / "tts" / track / "tts_manifest.json").exists()
        if has_script and has_anim and has_manifest:
            found.append(game)
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

    track_source = None
    try:
        if section_source.has_section_source(args.game, args.track):
            track_source = section_source.load_track_source(args.game, args.track)
            anim_doc = track_source.anim_doc
            script = track_source.script_doc
            source_is_section = True
            source_root = track_source.tutorial
            source_hash = track_source.source_hash
        else:
            anim_doc, script, source_root, source_hash, source_is_section = section_source.load_track_docs(
                args.game, args.track, allow_legacy=True, warn=True
            )
    except Exception as exc:  # noqa: BLE001 - CLI should surface source errors
        print(f"missing source for {args.game}/{args.track}: {exc}", file=sys.stderr)
        return 2
    if not script.get("cues"):
        print(f"missing narration script for {args.game}/{args.track}", file=sys.stderr)
        return 2
    if track_source is not None and any(
        isinstance(sec.meta, dict) and sec.meta.get("tts_asset")
        for sec in track_source.sections
    ):
        return _main_section_mode(args, track_source, script=script, source_root=source_root)

    game_dir = ROOT / "content" / "games" / args.game
    tutorial = game_dir / "tutorial"
    manifest_path = game_dir / "media" / "tts" / args.track / "tts_manifest.json"
    tts_lrc_path = tutorial / f"{args.track}.tts.lrc"
    if not manifest_path.exists():
        print(f"missing TTS manifest: {manifest_path}", file=sys.stderr)
        return 2

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
        asset = cue_tts_asset(cue, cid)
        old_text = tts_text.get(cid)
        m = manifest_by.get(cid)
        audio_ok = manifest_audio_ok(m)
        asset_changed = bool(m and m.get("asset") and m.get("asset") != asset)
        if (args.force_full_tts or old_text is None or old_text != text
                or not audio_ok or asset_changed):
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
    for m in manifest["cues"]:
        cue = script_by.get(m["id"])
        if cue is not None:
            m["asset"] = cue_tts_asset(cue, m["id"])
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
