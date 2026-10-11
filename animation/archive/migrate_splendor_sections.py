#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""One-shot migration: Splendor full/quick monolith -> section-first sources.

Run from the repo root:

    python3 animation/archive/migrate_splendor_sections.py --write --keep-legacy
    # validate, then remove the old monolith files:
    python3 animation/archive/migrate_splendor_sections.py --write --delete-legacy

The script preserves cue order, state-inheritance references, events, anchors,
TTS audio/subtitles and handwritten QA.  It never regenerates TTS; it renames
existing generated assets according to the new qualified-id slug.
"""
from __future__ import annotations

import argparse
import copy
import json
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / "animation"))
import section_source  # noqa: E402
import lrc as lrc_utils  # noqa: E402

GAME = "splendor"
TUT = ROOT / "content" / "games" / GAME / "tutorial"
ANIM_DIR = TUT / "anim"
V2 = ANIM_DIR / "v2"
SHARED = TUT / "shared"
SECTIONS = TUT / "sections"
TRACKS = TUT / "tracks"


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def write(path: Path, doc, *, pretty=True):
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=2 if pretty else None,
                      separators=None if pretty else (",", ":")) + "\n"
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    tmp.replace(path)


def copy_file(src: Path, dst: Path) -> None:
    if not src.exists():
        return
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.resolve() == dst.resolve():
        return
    if not dst.exists():
        shutil.copy2(src, dst)


# ── old id -> (section, local id) ──────────────────────────────────────────

FULL_RULES = [
    ("setup.cards.", "setup_cards"),
    ("setup.gems.", "setup_gems"),
    ("setup.nobles.", "setup_players"),
    ("setup.starting_player.", "setup_players"),
    ("setup.end.", "setup_players"),
    ("action.turn.", "turn"),
    ("action.take.", "take_gems"),
    ("action.nobles.", "nobles"),
    ("action.reserve.", "reserve"),
    ("action.purchase_reserved.", "purchase_reserved"),
    ("action.summary.", "summary"),
    ("endgame.", "end"),
]


def full_target(cid: str) -> tuple[str, str]:
    if cid == "bg.intro.001":
        return "intro", "001"
    if cid.startswith("action.cards.summary."):
        return "nobles", "summary." + cid[len("action.cards.summary."):]
    if cid.startswith("action.cards."):
        return "cards", cid[len("action.cards."):]
    for prefix, section in FULL_RULES:
        if cid.startswith(prefix):
            local = cid[len(prefix):]
            if local:
                return section, local
    raise KeyError(f"no full-section mapping for cue id {cid!r}")


def quick_target(cid: str) -> tuple[str, str]:
    if cid.startswith("quick.intro."):
        return "quick_intro", cid[len("quick.intro."):]
    if cid.startswith("setup.quick."):
        suffix = cid[len("setup.quick."):]
        section = {"001": "quick_setup_cards", "002": "quick_setup_gems",
                   "003": "quick_setup_players"}.get(suffix)
        if section:
            return section, suffix
    for prefix, section in [
        ("quick.turn.", "quick_turn"),
        ("quick.take.", "quick_take_gems"),
        ("quick.gold.", "quick_gold"),
        ("quick.cards.", "quick_cards"),
        ("quick.reserve.", "quick_reserve"),
        ("quick.nobles.", "quick_nobles"),
        ("quick.end.", "quick_end"),
    ]:
        if cid.startswith(prefix):
            return section, cid[len(prefix):]
    raise KeyError(f"no quick-section mapping for cue id {cid!r}")


def build_maps(anim_doc: dict, target_fn) -> dict[str, tuple[str, str]]:
    out = {}
    for cue in anim_doc.get("cues") or []:
        cid = cue.get("id")
        if not cid or cid in out:
            raise ValueError(f"bad/duplicate cue id {cid!r}")
        out[cid] = target_fn(cid)
    return out


# ── reference and anchor rewriting ─────────────────────────────────────────

def map_cue_ref(value, target_fn) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text or text == "initial":
        return text or None
    if text.startswith("section:"):
        ref = text.split(":", 1)[1].strip()
        if ref in section_old_ids:
            return "section:" + section_old_ids[ref]
        return text
    try:
        section, local = target_fn(text)
    except KeyError as exc:
        raise ValueError(f"cannot map cue reference {text!r}: {exc}") from exc
    return section_source.qualified_cue_id(section, local)


def map_beat_or_anchor_id(old_cue: str, old_value: str) -> str:
    """Return a globally unique new id for an old global beat/anchor id."""
    new_qid = section_source.qualified_cue_id(*cue_targets[old_cue])
    text = str(old_value)
    if text == old_cue:
        return new_qid
    if text.startswith(old_cue + "."):
        return new_qid + text[len(old_cue):]
    base = old_cue.rsplit(".", 1)[0]
    if base and text.startswith(base + "."):
        suffix = text[len(base) + 1:]
        return new_qid + "." + suffix
    return new_qid + "__" + text


cue_targets: dict[str, tuple[str, str]] = {}
section_old_ids: dict[str, str] = {}


def transform_anim_cues(anim_doc: dict, track: str, target_fn) -> list[dict]:
    """Split mono cues into per-section cue objects and build anchor maps."""
    global cue_targets
    cue_targets = build_maps(anim_doc, target_fn)
    cues = list(anim_doc.get("cues") or [])
    # First pass: all anchor id mappings, keyed by old anchor id.
    anchor_map: dict[str, str] = {}
    for cue in cues:
        old_cid = cue.get("id")
        for anchor in anim_doc.get("time_anchors") or []:
            if anchor.get("cue") != old_cid:
                continue
            old_aid = str(anchor.get("id") or "").strip()
            if not old_aid:
                raise ValueError(f"{old_cid}: anchor missing id")
            anchor_map.setdefault(old_aid, map_beat_or_anchor_id(old_cid, old_aid))
    # Anchor ids for multiple anchors with the same id are already unique by
    # construction; 0x duplicate is a source bug.
    grouped: dict[str, list[dict]] = {}
    for cue in cues:
        old_cid = cue.get("id")
        section, local = cue_targets[old_cid]
        new_cue = copy.deepcopy(cue)
        new_cue["id"] = local
        new_cue.pop("section", None)
        for key in ("parent", "entry"):
            if key in new_cue:
                value = new_cue.get(key)
                if value is not None:
                    mapped = map_cue_ref(value, target_fn)
                    if mapped is not None:
                        new_cue[key] = mapped
                else:
                    new_cue.pop(key, None)
        # Same-section references are written as local ids; cross-section refs
        # must be qualified. This exercises both forms in the loader.
        for key in ("parent", "entry"):
            value = new_cue.get(key)
            if not isinstance(value, str) or value == "initial" or value.startswith("section:"):
                continue
            try:
                ref_sec, ref_local = section_source.split_qualified_cue_id(value)
            except ValueError:
                continue
            if ref_sec == section:
                new_cue[key] = ref_local
        for event in new_cue.get("events") or []:
            if isinstance(event, dict) and isinstance(event.get("anchor"), str):
                aid = event["anchor"]
                if aid in anchor_map:
                    event["anchor"] = anchor_map[aid]
        grouped.setdefault(section, []).append(new_cue)
    return grouped


def transform_script_cues(script_doc: dict, target_fn) -> dict[str, list[dict]]:
    cues = script_doc.get("cues") or []
    grouped: dict[str, list[dict]] = {}
    for cue in cues:
        old_cid = cue.get("id")
        section, local = target_fn(old_cid)
        new_cue = copy.deepcopy(cue)
        new_cue["id"] = local
        for beat in new_cue.get("beats") or []:
            if isinstance(beat, dict) and beat.get("id"):
                beat["id"] = map_beat_or_anchor_id(old_cid, str(beat["id"]))
        grouped.setdefault(section, []).append(new_cue)
    return grouped


def transform_anchors(anim_doc: dict, target_fn) -> dict[str, list[dict]]:
    grouped: dict[str, list[dict]] = {}
    for anchor in anim_doc.get("time_anchors") or []:
        old_cid = anchor.get("cue")
        if not old_cid:
            raise ValueError(f"anchor without cue: {anchor!r}")
        section, local = target_fn(old_cid)
        new = copy.deepcopy(anchor)
        new["cue"] = local
        old_aid = str(new.get("id") or "").strip()
        if not old_aid:
            raise ValueError(f"anchor missing id: {anchor!r}")
        new["id"] = map_beat_or_anchor_id(old_cid, old_aid)
        if new.get("beat"):
            new["beat"] = map_beat_or_anchor_id(old_cid, str(new["beat"]))
        grouped.setdefault(section, []).append(new)
    return grouped


# ── section/track descriptors ──────────────────────────────────────────────

FULL_SECTION_TITLES = {
    "intro": "开始",
    "setup_cards": "设置：发展卡",
    "setup_gems": "设置：宝石与黄金",
    "setup_players": "设置：贵族与起始玩家",
    "turn": "回合与行动",
    "take_gems": "拿宝石",
    "cards": "购买与认识发展卡",
    "nobles": "贵族结算与购买总结",
    "reserve": "保留发展卡",
    "purchase_reserved": "购买保留的发展卡",
    "summary": "回合结束与循环",
    "end": "游戏结束",
}
QUICK_TITLES = {
    "quick_intro": "开始",
    "quick_setup_cards": "设置：发展卡",
    "quick_setup_gems": "设置：宝石",
    "quick_setup_players": "设置：贵族与起始玩家",
    "quick_turn": "回合",
    "quick_take_gems": "拿宝石",
    "quick_gold": "黄金",
    "quick_cards": "购买发展卡",
    "quick_reserve": "保留发展卡",
    "quick_nobles": "贵族",
    "quick_end": "游戏结束",
}


def section_order(grouped: dict[str, list[dict]], ordered_ids: list[str]) -> list[str]:
    out = []
    for cid in ordered_ids:
        sec, _ = cue_targets[cid]
        if sec not in out:
            out.append(sec)
    return out


def first_cue_group(script_doc: dict, section_grouped: dict[str, list[dict]], sec: str):
    for cue in script_doc.get("cues") or []:
        old_cid = cue.get("id")
        if cue_targets[old_cid][0] == sec:
            return cue.get("group"), cue.get("group_path") or ([cue.get("group")] if cue.get("group") else [])
    return None, []


def build_section_files(track: str, anim_grouped, script_grouped, anchors_grouped,
                        sections_order, section_meta_source, old_to_new, target_fn):
    old_sections_by_id = {s.get("id"): s for s in (section_meta_source or [])}
    meta_docs = {}
    for sec in sections_order:
        old_ref = None
        # Full sections don't have old section meta; quick uses old quick sections.
        if track == "quick":
            for old_sid, old_meta in old_sections_by_id.items():
                mapped_sid = section_old_ids.get(old_sid, old_sid)
                if mapped_sid == sec:
                    old_meta = copy.deepcopy(old_meta)
                    entry = old_meta.get("entry")
                    if entry:
                        try:
                            if str(entry).startswith("section:"):
                                old_meta["entry"] = "section:" + section_old_ids.get(
                                    str(entry).split(":", 1)[1].strip(),
                                    str(entry).split(":", 1)[1].strip())
                            else:
                                old_meta["entry"] = map_cue_ref(entry, target_fn)
                        except Exception:
                            pass
                    meta_docs[sec] = old_meta
                    break
        if sec not in meta_docs:
            meta_docs[sec] = {}
    return meta_docs


# ── TTS / runtime / QA helpers ─────────────────────────────────────────────

def migrate_manifest(track: str, target_fn, *, delete_old: bool) -> None:
    manifest_path = ROOT / "content" / "games" / GAME / "media" / "tts" / track / "tts_manifest.json"
    manifest = read(manifest_path)
    media_dir = manifest_path.parent
    changed = []
    for cue in manifest.get("cues") or []:
        old_id = cue.get("id")
        if old_id not in cue_targets:
            raise ValueError(f"manifest cue {old_id!r} not in animation")
        section, local = cue_targets[old_id]
        qid = section_source.qualified_cue_id(section, local)
        stem = section_source.cue_id_to_slug(qid)
        changed.append((old_id, qid, stem))
    # First verify/clone every referenced file.
    for old_id, qid, stem in changed:
        old_stem = old_id
        for ext in (".mp3", ".subtitle.json", ".wav"):
            src = media_dir / f"{old_stem}{ext}"
            if not src.exists():
                continue
            dst = media_dir / f"{stem}{ext}"
            copy_file(src, dst)
            if not dst.exists():
                raise RuntimeError(f"failed to create TTS asset {dst}")
    # Update manifest.
    for cue, (old_id, qid, stem) in zip(manifest.get("cues") or [], changed):
        cue["id"] = qid
        ext = Path(cue.get("file", "")).suffix or ".mp3"
        if cue.get("file"):
            cue["file"] = f"content/games/{GAME}/media/tts/{track}/{stem}{ext}"
        if cue.get("subtitle_file"):
            sub_ext = Path(cue["subtitle_file"]).suffix or ".subtitle.json"
            cue["subtitle_file"] = f"content/games/{GAME}/media/tts/{track}/{stem}{sub_ext}"
    write(manifest_path, manifest)
    if delete_old:
        for old_id, qid, stem in changed:
            for ext in (".mp3", ".subtitle.json", ".wav"):
                old = media_dir / f"{old_id}{ext}"
                if old.exists() and old.resolve() != (media_dir / f"{stem}{ext}").resolve():
                    old.unlink()
    print(f"[tts] migrated {len(changed)} manifest cues for {track}")


def rewrite_tts_lrc(track: str) -> None:
    path = TUT / f"{track}.tts.lrc"
    text = path.read_text(encoding="utf-8")
    out_lines = []
    for line in text.splitlines():
        m = re.search(r"\[id:([^\]]+)\]", line)
        if m:
            old_id = m.group(1)
            if old_id in cue_targets:
                section, local = cue_targets[old_id]
                new_id = section_source.qualified_cue_id(section, local)
                line = line.replace(f"[id:{old_id}]", f"[id:{new_id}]")
        out_lines.append(line)
    path.write_text("\n".join(out_lines) + "\n", encoding="utf-8")


def migrate_qa_questions(target_fn) -> None:
    old_path = TUT / "anim" / "_qa" / "questions.json"
    if not old_path.exists():
        return
    spec = read(old_path)
    grouped: dict[str, list[dict]] = {}
    for item in spec.get("asks") or []:
        if not isinstance(item, dict):
            continue
        old_ref = str(item.get("cue") or "")
        old_cid, _, suffix = old_ref.partition("#")
        if old_cid not in cue_targets:
            raise ValueError(f"QA cue {old_cid!r} not in track mapping")
        section, local = cue_targets[old_cid]
        ask = {k: copy.deepcopy(v) for k, v in item.items() if k not in ("cue",)}
        ask["cue"] = local if not suffix else f"{local}#{suffix}"
        grouped.setdefault(section, []).append(ask)
    for section, asks in grouped.items():
        qa_doc = {
            "schema": "tutorial-section-qa/v1",
            "game": GAME,
            "track": "full",
            "section": section,
            "note": "手写的 cue 合法性问句；cue 为 section 内 local_id。",
            "asks": asks,
        }
        write(SECTIONS / section / "qa.json", qa_doc)


def migrate_sample() -> None:
    path = V2 / "full.v2sample.json"
    if not path.exists():
        return
    doc = read(path)
    for cue in doc.get("cues") or []:
        old_id = cue.get("cue") or cue.get("id")
        if old_id in cue_targets:
            section, local = cue_targets[old_id]
            qid = section_source.qualified_cue_id(section, local)
            if "cue" in cue:
                cue["cue"] = qid
            else:
                cue["id"] = qid
    write(path, doc)


# ── main migration ─────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--write", action="store_true", default=False)
    ap.add_argument("--delete-legacy", action="store_true")
    ap.add_argument("--keep-legacy", action="store_true")
    args = ap.parse_args()
    if args.dry_run:
        args.write = False
    if not args.write and not args.dry_run:
        ap.error("pass --write (and optionally --delete-legacy) or --dry-run")

    full_anim = read(V2 / "full.anim.json")
    full_script = read(TUT / "script.full.json")
    quick_anim = read(V2 / "quick.anim.json")
    quick_script = read(TUT / "script.quick.json")

    old_quick_sections = quick_anim.get("sections") or []
    global section_old_ids
    section_old_ids = {s.get("id"): f"quick_{s.get('id')}" for s in old_quick_sections if s.get("id")}

    # Build target functions bound to the maps.
    full_map = build_maps(full_anim, full_target)
    quick_map = build_maps(quick_anim, quick_target)
    if set(full_map) != {c.get("id") for c in full_script.get("cues") or []}:
        raise ValueError("full anim/script cue ids differ")
    if set(quick_map) != {c.get("id") for c in quick_script.get("cues") or []}:
        raise ValueError("quick anim/script cue ids differ")

    all_stage_files = list((V2 / "_stage").glob("*.stage.json")) if (V2 / "_stage").is_dir() else []
    if args.dry_run:
        print(f"full cues: {len(full_map)} -> sections {sorted({s for s, _ in full_map.values()})}")
        print(f"quick cues: {len(quick_map)} -> sections {sorted({s for s, _ in quick_map.values()})}")
        print(f"stage files: {len(all_stage_files)} -> shared/stage/")
        return 0

    # 0. Remove old section directories; everything is regenerated below from
    # the monolith snapshot on this one-shot migration.
    if SECTIONS.exists():
        for child in list(SECTIONS.iterdir()):
            if child.is_dir():
                shutil.rmtree(child)
    # 1. shared base animations + stage moves.
    SHARED.mkdir(parents=True, exist_ok=True)
    (SHARED / "stage").mkdir(parents=True, exist_ok=True)
    for stage in all_stage_files:
        copy_file(stage, SHARED / "stage" / stage.name)
    for track, mono in (("full", full_anim), ("quick", quick_anim)):
        base = {k: copy.deepcopy(v) for k, v in mono.items()
                if k not in ("cues", "time_anchors", "sections")}
        base["game"] = GAME
        base["track"] = track
        for tree in base.get("trees") or []:
            if tree.get("stage") == "_stage/splendor.table.stage.json":
                tree["stage"] = "shared/stage/splendor.table.stage.json"
        for tree in base.get("trees") or []:
            if norm_stage := tree.get("stage"):
                base_stage_ref = str(norm_stage).replace("\\", "/")
                if base_stage_ref.startswith("_stage/"):
                    tree["stage"] = "shared/stage/" + base_stage_ref[len("_stage/"):]
        write(SHARED / f"{track}.base.anim.json", base)

    # 2. Build section data.
    global cue_targets
    cue_targets = full_map
    full_anim_grouped = transform_anim_cues(full_anim, "full", full_target)
    full_script_grouped = transform_script_cues(full_script, full_target)
    full_anchor_grouped = transform_anchors(full_anim, full_target)
    full_order = section_order(full_anim_grouped, [c["id"] for c in full_anim.get("cues") or []])

    cue_targets = quick_map
    quick_anim_grouped = transform_anim_cues(quick_anim, "quick", quick_target)
    quick_script_grouped = transform_script_cues(quick_script, quick_target)
    quick_anchor_grouped = transform_anchors(quick_anim, quick_target)
    quick_order = section_order(quick_anim_grouped, [c["id"] for c in quick_anim.get("cues") or []])

    # Track descriptors use old script metadata but section order from cues.
    for track, order, script_doc in (("full", full_order, full_script), ("quick", quick_order, quick_script)):
        script_meta = {k: copy.deepcopy(v) for k, v in script_doc.items() if k != "cues"}
        if track == "full":
            script_meta["version"] = script_meta.get("version") or "0.1.0"
        descriptor = {
            "schema": "tutorial-track/v1",
            "game": GAME,
            "track": track,
            "title": script_meta.get("title") or f"璀璨宝石 {track}",
            "version": script_meta.get("version") or "0.1.0",
            "base_anim": f"../shared/{track}.base.anim.json",
            "sections": order,
            "script_meta": {k: copy.deepcopy(v) for k, v in script_meta.items() if k != "sections"},
        }
        write(TRACKS / f"{track}.track.json", descriptor)

    # 3. Write section dirs/files.
    def emit_sections(track, order, anim_grouped, script_grouped, anchors_grouped, target_fn):
        for sec in order:
            sec_dir = SECTIONS / sec
            sec_dir.mkdir(parents=True, exist_ok=True)
            title = (FULL_SECTION_TITLES if track == "full" else QUICK_TITLES).get(sec, sec)
            script_cues = script_grouped.get(sec) or []
            if not script_cues:
                raise ValueError(f"{track}: no script cues mapped to section {sec}")
            first_script = script_cues[0]
            group = first_script.get("group")
            group_path = first_script.get("group_path") or ([group] if group else [])
            meta = {
                "id": sec,
                "title": title,
                "group_path": group_path,
                "order": order.index(sec),
                "tree": "main",
            }
            # Quick sections carry their old section entry.  Full sections
            # inherit the already-normalised first cue reference.
            entry = None
            entry_needs_map = False
            if track == "quick":
                old_sid = next((k for k, v in section_old_ids.items() if v == sec), None)
                old_meta = old_sections_by_id.get(old_sid) or {}
                entry = old_meta.get("entry")
                entry_needs_map = entry is not None
            if entry is None and anim_grouped.get(sec):
                first = anim_grouped[sec][0]
                entry = first.get("entry") or first.get("parent")
            if entry:
                if str(entry).startswith("section:"):
                    ref = str(entry).split(":", 1)[1].strip()
                    meta["entry"] = "section:" + section_old_ids.get(ref, ref)
                elif str(entry) == "initial":
                    meta["entry"] = "initial"
                elif entry_needs_map:
                    mapped = map_cue_ref(str(entry), target_fn)
                    if mapped:
                        meta["entry"] = mapped
                else:
                    # Already in section-first form from the cue transform.
                    meta["entry"] = str(entry)
            write(sec_dir / "section.json", meta)

            anim_cues = anim_grouped.get(sec) or []
            if not anim_cues:
                raise ValueError(f"{track}: no cues mapped to section {sec}")
            anim_doc = {
                "schema": "tutorial-section-anim/v1",
                "game": GAME,
                "track": track,
                "section": sec,
                "cues": anim_cues,
                "time_anchors": anchors_grouped.get(sec) or [],
            }
            write(sec_dir / f"{track}.anim.json", anim_doc)
            script_doc_out = {
                "schema_version": 1,
                "game_id": GAME,
                "track": track,
                "section": sec,
                "cues": script_cues,
            }
            write(sec_dir / f"{track}.script.json", script_doc_out)

    old_sections_by_id = {s.get("id"): s for s in (old_quick_sections or [])}
    cue_targets = full_map
    emit_sections("full", full_order, full_anim_grouped, full_script_grouped,
                  full_anchor_grouped, full_target)
    cue_targets = quick_map
    emit_sections("quick", quick_order, quick_anim_grouped, quick_script_grouped,
                  quick_anchor_grouped, quick_target)

    # 4. Refs: map old full/quick ids inside base? none.
    # Track quick section entries are already mapped above.

    # 5. TTS manifest + file migration.
    cue_targets = full_map
    migrate_manifest("full", full_target, delete_old=bool(args.delete_legacy and not args.keep_legacy))
    rewrite_tts_lrc("full")
    cue_targets = quick_map
    migrate_manifest("quick", quick_target, delete_old=bool(args.delete_legacy and not args.keep_legacy))
    rewrite_tts_lrc("quick")

    # 6. QA questions + sample.
    cue_targets = full_map
    migrate_qa_questions(full_target)
    migrate_sample()
    # Old estimated lrc is a generated artifact; rebuild it from migrated source
    # later through compile_tutorial.  Full/quick runtime are rebuilt there too.

    # 7. Delete old monolith source files (and old QA source) if requested.
    if args.delete_legacy and not args.keep_legacy:
        for path in (V2 / "full.anim.json", V2 / "quick.anim.json",
                     TUT / "script.full.json", TUT / "script.quick.json",
                     ANIM_DIR / "_qa" / "questions.json"):
            if path.exists():
                path.unlink()
                print(f"[legacy] removed {path}")
    else:
        print("[legacy] kept monolith files (--keep-legacy)")
    print("migration complete; run section_source loader / compilers next")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
