#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Section-first tutorial authoring source loader/resolver.

The source of truth for a track is:

    content/games/{game}/tutorial/
      shared/{track}.base.anim.json
      sections/<section_id>/section.json
      sections/<section_id>/{track}.anim.json
      sections/<section_id>/{track}.script.json
      sections/<section_id>/qa.json                (optional)
      tracks/{track}.track.json

The track descriptor only orders sections.  A cue's id inside a section file is
its *local* id.  Loading a track produces a resolved in-memory document whose
cue ids are qualified as ``<section_id>/<local_id>``.  Compilers and checkers
consume that resolved document directly; ``build/*.json`` is a derived,
non-authoritative dump for debugging only.

Assets are intentionally not section/cue scoped: authored media lives in one
shared game media pool, generated TTS lives in ``media/tts/{track}/``, and each
cue references the assets it uses.  TTS cues use an explicit ``tts_asset``
field naming the original audio file base name.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
TRACK_SOURCE_SCHEMA = "tutorial-track/v1"
ANIM_SOURCE_SCHEMA = "tutorial-anim/v2"
SCRIPT_SOURCE_SCHEMA_VERSION = 1
SAFE_ID_RE = re.compile(r"[^A-Za-z0-9._-]+")


# ── small filesystem/id helpers ─────────────────────────────────────────────

def _read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _write_json(path: Path, data: Any, *, pretty: bool = True) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = (
        json.dumps(data, ensure_ascii=False, indent=2, sort_keys=False) + "\n"
        if pretty
        else json.dumps(data, ensure_ascii=False, separators=(",", ":")) + "\n"
    )
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    tmp.replace(path)


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_sha256(path: Path) -> str:
    return _sha256_bytes(path.read_bytes())


def tutorial_dir(game: str) -> Path:
    return ROOT / "content" / "games" / game / "tutorial"


def track_descriptor_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / "tracks" / f"{track}.track.json"


def legacy_anim_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / "anim" / "v2" / f"{track}.anim.json"


def legacy_script_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / f"script.{track}.json"


def compiled_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / "anim" / "v2" / f"{track}.compiled.json"


def build_anim_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / "build" / f"{track}.anim.json"


def build_script_path(game: str, track: str) -> Path:
    return tutorial_dir(game) / "build" / f"{track}.script.json"


def has_section_source(game: str, track: str) -> bool:
    return track_descriptor_path(game, track).is_file()


def qualified_cue_id(section_id: str, local_id: str) -> str:
    section_id = str(section_id).strip()
    local_id = str(local_id).strip()
    if not section_id or not local_id:
        raise ValueError("qualified cue id needs non-empty section and local id")
    if "/" in section_id:
        raise ValueError(f"section id may not contain '/': {section_id!r}")
    if "/" in local_id:
        raise ValueError(
            f"cue local id may not contain '/': {local_id!r}; "
            f"write the qualified id as <section_id>/<local_id>"
        )
    return f"{section_id}/{local_id}"


def split_qualified_cue_id(cue_id: str) -> tuple[str, str]:
    text = str(cue_id or "").strip()
    if "/" not in text:
        raise ValueError(f"not a qualified cue id: {text!r}")
    section_id, local_id = text.rsplit("/", 1)
    if not section_id or not local_id:
        raise ValueError(f"not a qualified cue id: {text!r}")
    return section_id, local_id


def cue_id_to_slug(cue_id: str) -> str:
    """Filesystem-safe, stable encoding for a qualified cue id.

    '/' becomes '__' (the documented common case); any other unsafe run is
    replaced by '_'.  The encoding is one-way and only used for generated TTS
    file names; JSON keeps the qualified id.
    """
    text = str(cue_id or "").strip()
    text = text.replace("/", "__")
    return SAFE_ID_RE.sub("_", text)


def _section_id_from_dir(name: str) -> str:
    return str(name or "").strip()


def _local_id_from_cue(raw: dict, section_id: str, where: str) -> str:
    if not isinstance(raw, dict):
        raise ValueError(f"{where}: cue must be an object")
    explicit = raw.get("local_id")
    if explicit is not None:
        local = str(explicit).strip()
    elif raw.get("id") is not None:
        raw_id = str(raw.get("id")).strip()
        if "/" in raw_id:
            try:
                sec, local = split_qualified_cue_id(raw_id)
            except ValueError as exc:
                raise ValueError(f"{where}: {exc}") from exc
            if sec != section_id:
                raise ValueError(
                    f"{where}: qualified cue id {raw_id!r} belongs to section {sec!r}, "
                    f"not {section_id!r}"
                )
        else:
            local = raw_id
    else:
        raise ValueError(f"{where}: cue needs id/local_id")
    if not local:
        raise ValueError(f"{where}: cue local id is empty")
    qualified_cue_id(section_id, local)  # validates
    return local


def _ref_in_section(section_id: str, value: str) -> str:
    """Qualify one cue reference, allowing same-section locals and qualified ids."""
    text = str(value or "").strip()
    if not text:
        raise ValueError("empty cue reference")
    if "/" in text:
        sec, local = split_qualified_cue_id(text)
        return qualified_cue_id(sec, local)
    return qualified_cue_id(section_id, text)


# ── source containers ───────────────────────────────────────────────────────

@dataclass
class SectionSource:
    id: str
    dir: Path
    meta: dict
    anim: dict
    script: dict
    qa: dict | None
    order: int
    files: list[Path]
    local_anim_ids: list[str] = field(default_factory=list)
    local_script_ids: list[str] = field(default_factory=list)
    anim_by_local: dict[str, dict] = field(default_factory=dict)
    script_by_local: dict[str, dict] = field(default_factory=dict)
    local_to_qid: dict[str, str] = field(default_factory=dict)


@dataclass
class TrackSource:
    game: str
    track: str
    descriptor_path: Path
    descriptor: dict
    base_path: Path
    base_anim: dict
    sections: list[SectionSource]
    anim_doc: dict
    script_doc: dict
    source_hash: str
    section_by_id: dict[str, SectionSource]
    qid_to_section: dict[str, str]
    qid_to_local: dict[str, str]
    qid_to_cue: dict[str, dict]
    qa_asks: list[dict]

    @property
    def tutorial(self) -> Path:
        return tutorial_dir(self.game)

    @property
    def qa_by_section(self) -> dict[str, dict | None]:
        return {sec.id: sec.qa for sec in self.sections}

    def write_build(self) -> tuple[Path, Path]:
        """Write derived build/ intermediates.  Never read back as source."""
        anim_path = build_anim_path(self.game, self.track)
        script_path = build_script_path(self.game, self.track)
        _write_json(anim_path, self.anim_doc, pretty=True)
        _write_json(script_path, self.script_doc, pretty=True)
        return anim_path, script_path


# ── load / resolve ──────────────────────────────────────────────────────────

def load_track_source(game: str, track: str, *, verbose: bool = False) -> TrackSource:
    game = str(game or "").strip()
    track = str(track or "").strip()
    if not game or not track:
        raise ValueError("game and track are required")

    descriptor_path = track_descriptor_path(game, track)
    if not descriptor_path.is_file():
        raise FileNotFoundError(f"missing track descriptor: {descriptor_path}")
    descriptor = _read_json(descriptor_path)
    if not isinstance(descriptor, dict):
        raise ValueError(f"{descriptor_path}: descriptor must be an object")
    if str(descriptor.get("game") or game) != game:
        raise ValueError(
            f"{descriptor_path}: descriptor.game {descriptor.get('game')!r} != {game!r}"
        )
    if str(descriptor.get("track") or track) != track:
        raise ValueError(
            f"{descriptor_path}: descriptor.track {descriptor.get('track')!r} != {track!r}"
        )

    base_value = descriptor.get("base_anim")
    if base_value:
        base_path = (descriptor_path.parent / str(base_value)).resolve()
    else:
        base_path = (descriptor_path.parent / ".." / "shared" / f"{track}.base.anim.json").resolve()
    if not base_path.is_file():
        raise FileNotFoundError(f"missing shared base animation: {base_path}")
    base_anim = _read_json(base_path)
    if not isinstance(base_anim, dict):
        raise ValueError(f"{base_path}: base animation must be an object")
    if base_anim.get("cues"):
        raise ValueError(
            f"{base_path}: shared base anim may not contain cues; put them in sections/"
        )
    if base_anim.get("sections"):
        raise ValueError(
            f"{base_path}: shared base anim may not contain sections; the track descriptor orders sections"
        )

    raw_sections = descriptor.get("sections")
    if not isinstance(raw_sections, list) or not raw_sections:
        raise ValueError(f"{descriptor_path}: sections must be a non-empty list")
    section_ids: list[str] = []
    for i, raw in enumerate(raw_sections):
        if isinstance(raw, dict):
            sid = str(raw.get("id") or "").strip()
        else:
            sid = str(raw or "").strip()
        if not sid:
            raise ValueError(f"{descriptor_path}: sections[{i}] has no section id")
        if "/" in sid:
            raise ValueError(f"{descriptor_path}: section id may not contain '/': {sid!r}")
        if sid in section_ids:
            raise ValueError(f"{descriptor_path}: duplicate section id {sid!r}")
        section_ids.append(sid)

    sections: list[SectionSource] = []
    all_files: list[tuple[Path, str]] = []
    tutorial = tutorial_dir(game)

    def _remember(path: Path) -> None:
        try:
            rel = path.resolve().relative_to(tutorial.resolve()).as_posix()
        except ValueError:
            rel = path.resolve().as_posix()
        all_files.append((path.resolve(), rel))

    _remember(descriptor_path)
    _remember(base_path)

    for order, sid in enumerate(section_ids):
        section_dir = tutorial / "sections" / sid
        section_meta_path = section_dir / "section.json"
        anim_path = section_dir / f"{track}.anim.json"
        script_path = section_dir / f"{track}.script.json"
        qa_path = section_dir / "qa.json"
        if not section_meta_path.is_file():
            raise FileNotFoundError(f"missing section metadata: {section_meta_path}")
        if not anim_path.is_file():
            raise FileNotFoundError(f"missing section animation: {anim_path}")
        if not script_path.is_file():
            raise FileNotFoundError(f"missing section script: {script_path}")
        meta = _read_json(section_meta_path)
        anim = _read_json(anim_path)
        script = _read_json(script_path)
        if not isinstance(meta, dict):
            raise ValueError(f"{section_meta_path}: must be an object")
        if not isinstance(anim, dict) or not isinstance(anim.get("cues"), list):
            raise ValueError(f"{anim_path}: cues list required")
        if not isinstance(script, dict) or not isinstance(script.get("cues"), list):
            raise ValueError(f"{script_path}: cues list required")
        meta_id = str(meta.get("id") or "").strip()
        if meta_id != sid:
            raise ValueError(f"{section_meta_path}: id must be {sid!r}, got {meta_id!r}")
        if str(anim.get("section") or sid) != sid:
            raise ValueError(f"{anim_path}: section must be {sid!r}")
        if str(script.get("section") or sid) != sid:
            raise ValueError(f"{script_path}: section must be {sid!r}")
        qa = _read_json(qa_path) if qa_path.is_file() else None
        if qa is not None and not isinstance(qa, dict):
            raise ValueError(f"{qa_path}: qa must be an object when present")

        section = SectionSource(
            id=sid,
            dir=section_dir,
            meta=copy.deepcopy(meta),
            anim=copy.deepcopy(anim),
            script=copy.deepcopy(script),
            qa=copy.deepcopy(qa),
            order=order,
            files=[section_meta_path.resolve(), anim_path.resolve(), script_path.resolve()],
        )
        if qa_path.is_file():
            section.files.append(qa_path.resolve())
        for path in section.files:
            _remember(path)

        seen_local: dict[str, int] = {}
        for i, raw in enumerate(anim.get("cues") or []):
            local = _local_id_from_cue(raw, sid, f"{anim_path}.cues[{i}]")
            if local in seen_local:
                raise ValueError(f"{anim_path}: duplicate local cue id {local!r}")
            seen_local[local] = i
            section.local_anim_ids.append(local)
            section.anim_by_local[local] = copy.deepcopy(raw)
        if not section.local_anim_ids:
            raise ValueError(f"{anim_path}: at least one cue is required")

        seen_script: dict[str, int] = {}
        for i, raw in enumerate(script.get("cues") or []):
            local = _local_id_from_cue(raw, sid, f"{script_path}.cues[{i}]")
            if local in seen_script:
                raise ValueError(f"{script_path}: duplicate local cue id {local!r}")
            seen_script[local] = i
            section.local_script_ids.append(local)
            section.script_by_local[local] = copy.deepcopy(raw)

        if section.local_anim_ids != section.local_script_ids:
            raise ValueError(
                f"{sid}: animation/script cue order does not match; "
                f"anim={section.local_anim_ids} script={section.local_script_ids}"
            )
        section.local_to_qid = {
            local: qualified_cue_id(sid, local) for local in section.local_anim_ids
        }
        sections.append(section)

    section_by_id = {sec.id: sec for sec in sections}
    qid_to_section: dict[str, str] = {}
    qid_to_local: dict[str, str] = {}
    qid_to_cue: dict[str, dict] = {}
    first_qid_by_section: dict[str, str] = {}
    ordered_qids: list[str] = []
    for sec in sections:
        for local in sec.local_anim_ids:
            qid = sec.local_to_qid[local]
            if qid in qid_to_cue:
                raise ValueError(f"duplicate qualified cue id {qid!r}")
            qid_to_section[qid] = sec.id
            qid_to_local[qid] = local
            qid_to_cue[qid] = sec.anim_by_local[local]
            ordered_qids.append(qid)
        first_qid_by_section[sec.id] = sec.local_to_qid[sec.local_anim_ids[0]]

    def _validate_qid(qid: str, where: str) -> str:
        if qid not in qid_to_cue:
            raise ValueError(f"{where}: unknown cue {qid!r}")
        return qid

    def _section_default_entry_ref(sid: str) -> str | None:
        sec = section_by_id.get(sid)
        if sec is None:
            raise ValueError(f"unknown section {sid!r}")
        value = sec.meta.get("entry")
        if value is None:
            return None
        return str(value).strip()

    def _resolve_ref_in_section(sid: str, value: str, where: str) -> str:
        text = str(value or "").strip()
        if not text:
            raise ValueError(f"{where}: empty cue reference")
        if "/" in text:
            qid = _ref_in_section(sid, text)
            return _validate_qid(qid, where)
        local = text
        if local not in section_by_id[sid].anim_by_local:
            raise ValueError(
                f"{where}: same-section cue {local!r} does not exist in section {sid!r}; "
                f"cross-section references must use <section_id>/<cue_id>"
            )
        return qualified_cue_id(sid, local)

    # Raw references are normalised in a second pass so section-aware defaults
    # can refer to any cue regardless of file order.
    raw_parent: dict[str, str | None] = {}
    raw_entry: dict[str, str | None] = {}
    for sec in sections:
        for local in sec.local_anim_ids:
            raw = sec.anim_by_local[local]
            where = f"sections/{sec.id}/{track}.anim.json cue {local!r}"
            parent = raw.get("parent")
            raw_parent[qualified_cue_id(sec.id, local)] = (
                None if parent is None else str(parent).strip()
            )
            entry = raw.get("entry")
            raw_entry[qualified_cue_id(sec.id, local)] = (
                None if entry is None else str(entry).strip()
            )

    normal_parent: dict[str, str | None] = {}
    for qid in ordered_qids:
        value = raw_parent.get(qid)
        if value is None:
            normal_parent[qid] = None
        else:
            sid = qid_to_section[qid]
            if value.startswith("section:"):
                raise ValueError(f"cue {qid}: parent may not use section: syntax")
            normal_parent[qid] = _resolve_ref_in_section(sid, value, f"cue {qid}.parent")

    section_start_cache: dict[str, str] = {}
    section_start_visiting: set[str] = set()

    def _resolve_section_meta_entry(sid: str, value: str) -> str:
        text = str(value or "").strip()
        if text.startswith("section:"):
            ref = text.split(":", 1)[1].strip()
            return _section_start(ref)
        if text == "initial":
            return "initial"
        return _resolve_ref_in_section(sid, text, f"section {sid}.entry")

    def _section_start(sid: str) -> str:
        sid = str(sid or "").strip()
        if sid in section_start_cache:
            return section_start_cache[sid]
        if sid not in section_by_id:
            raise ValueError(f"entry:section references unknown section {sid!r}")
        if sid in section_start_visiting:
            raise ValueError(f"section entry cycle involving {sid!r}")
        section_start_visiting.add(sid)
        try:
            value = _section_default_entry_ref(sid)
            if value is not None:
                result = _resolve_section_meta_entry(sid, value)
                section_start_cache[sid] = result
                return result
            first_qid = first_qid_by_section[sid]
            first_parent = normal_parent.get(first_qid)
            if first_parent is not None:
                result = first_parent
            else:
                first_entry = raw_entry.get(first_qid)
                if first_entry is not None:
                    result = _resolve_entry_value(first_qid, first_entry)
                else:
                    idx = ordered_qids.index(first_qid)
                    result = ordered_qids[idx - 1] if idx > 0 else "initial"
            section_start_cache[sid] = result
            return result
        finally:
            section_start_visiting.discard(sid)

    def _resolve_entry_value(qid: str, value: str) -> str:
        sid = qid_to_section[qid]
        text = str(value or "").strip()
        if not text:
            raise ValueError(f"cue {qid}: empty entry reference")
        if text == "initial":
            return "initial"
        if text.startswith("section:"):
            ref = text.split(":", 1)[1].strip()
            if not ref:
                raise ValueError(f"cue {qid}: entry:section needs a section id")
            return _section_start(ref)
        if "/" in text:
            return _validate_qid(_ref_in_section(sid, text), f"cue {qid}.entry")
        return _resolve_ref_in_section(sid, text, f"cue {qid}.entry")

    # First cue in a section with neither entry nor parent inherits section.entry.
    normal_entry: dict[str, str | None] = {}
    first_global = ordered_qids[0]
    for qid in ordered_qids:
        sid = qid_to_section[qid]
        value = raw_entry.get(qid)
        if value is None and normal_parent.get(qid) is None:
            if first_qid_by_section[sid] == qid:
                value = _section_default_entry_ref(sid)
        if value is None:
            if qid == first_global:
                raise ValueError(
                    f"cue {qid}: the first cue of a track must provide entry 'initial' "
                    f"or a section entry"
                )
            normal_entry[qid] = None
        else:
            normal_entry[qid] = _resolve_entry_value(qid, value)

    # Materialise the resolved animation cues.
    resolved_anim_cues: list[dict] = []
    resolved_script_cues: list[dict] = []
    resolved_anchors: list[dict] = []
    seen_anchor_ids: set[str] = set()
    for sec in sections:
        sec_anchor_map: dict[str, str] = {}
        for i, local in enumerate(sec.local_anim_ids):
            qid = sec.local_to_qid[local]
            raw = copy.deepcopy(sec.anim_by_local[local])
            cue: dict[str, Any] = {}
            cue.update(raw)
            cue["id"] = qid
            cue["local_id"] = local
            cue["section"] = sec.id
            cue["section_index"] = sec.order
            cue["local_index"] = i
            cue["cue_index"] = ord(ordered_qids.index(qid)) if False else ordered_qids.index(qid)
            if normal_parent.get(qid) is not None:
                cue["parent"] = normal_parent[qid]
            else:
                cue.pop("parent", None)
            if normal_entry.get(qid) is not None:
                cue["entry"] = normal_entry[qid]
            else:
                cue.pop("entry", None)
            if cue.get("tree") is None and sec.meta.get("tree"):
                cue["tree"] = copy.deepcopy(sec.meta.get("tree"))
            if cue.get("stage") is None and sec.meta.get("stage"):
                cue["stage"] = copy.deepcopy(sec.meta.get("stage"))
            resolved_anim_cues.append(cue)

        for anchor in sec.anim.get("time_anchors") or []:
            if not isinstance(anchor, dict):
                raise ValueError(f"{sec.id}: time_anchor must be an object")
            item = copy.deepcopy(anchor)
            raw_cue = item.get("cue")
            if raw_cue is not None:
                text = str(raw_cue).strip()
                if "/" in text:
                    item["cue"] = _validate_qid(text, f"section {sec.id} anchor.cue")
                else:
                    item["cue"] = _validate_qid(
                        qualified_cue_id(sec.id, text), f"section {sec.id} anchor.cue"
                    )
            aid = str(item.get("id") or "").strip()
            if not aid:
                raise ValueError(f"section {sec.id}: time_anchor needs id")
            if aid in seen_anchor_ids:
                raise ValueError(f"duplicate time anchor id across sections: {aid!r}")
            seen_anchor_ids.add(aid)
            resolved_anchors.append(item)

        for local in sec.local_script_ids:
            raw = copy.deepcopy(sec.script_by_local[local])
            qid = sec.local_to_qid[local]
            script_cue: dict[str, Any] = {}
            script_cue.update(raw)
            script_cue["id"] = qid
            script_cue["local_id"] = local
            script_cue["section"] = sec.id
            script_cue["section_index"] = sec.order
            script_cue["local_index"] = sec.local_anim_ids.index(local)
            script_cue["cue_index"] = ordered_qids.index(qid)
            if not script_cue.get("group_path") and sec.meta.get("group_path"):
                script_cue["group_path"] = copy.deepcopy(sec.meta.get("group_path"))
            if not script_cue.get("group"):
                if isinstance(sec.meta.get("group_path"), list) and sec.meta.get("group_path"):
                    script_cue["group"] = str(sec.meta["group_path"][-1])
                elif sec.meta.get("title"):
                    script_cue["group"] = str(sec.meta["title"])
            resolved_script_cues.append(script_cue)

    section_meta_out: list[dict] = []
    for sec in sections:
        meta = copy.deepcopy(sec.meta)
        meta["id"] = sec.id
        meta["order"] = sec.order
        try:
            meta["entry"] = _section_start(sec.id)
        except ValueError:
            # Leave the authored value for the checker to report; don't break
            # the build just because a draft section has no reachable start.
            pass
        meta["cue_ids"] = [sec.local_to_qid[local] for local in sec.local_anim_ids]
        meta["cue_count"] = len(meta["cue_ids"])
        section_meta_out.append(meta)

    anim_doc: dict[str, Any] = copy.deepcopy(base_anim)
    anim_doc.update(
        {
            "schema": ANIM_SOURCE_SCHEMA,
            "kind": base_anim.get("kind") or "animation_track",
            "game": game,
            "track": track,
            "sections": section_meta_out,
            "cues": resolved_anim_cues,
            "time_anchors": resolved_anchors,
        }
    )

    script_meta = copy.deepcopy(descriptor.get("script_meta") or {})
    script_meta.setdefault("schema_version", SCRIPT_SOURCE_SCHEMA_VERSION)
    script_meta["game_id"] = game
    script_meta["track"] = track
    script_meta["title"] = descriptor.get("title") or script_meta.get("title") or f"{game} {track}"
    script_meta["version"] = descriptor.get("version") or script_meta.get("version") or "0.1.0"
    script_meta["sections"] = copy.deepcopy(section_meta_out)
    script_meta["cues"] = resolved_script_cues

    qa_asks: list[dict] = []
    for sec in sections:
        qa = sec.qa
        if not qa:
            continue
        raw_asks = qa.get("asks") if isinstance(qa, dict) else None
        if raw_asks is None and isinstance(qa, list):
            raw_asks = qa
        if raw_asks is None:
            continue
        if not isinstance(raw_asks, list):
            raise ValueError(f"{sec.dir / 'qa.json'}: asks must be a list")
        for i, item in enumerate(raw_asks):
            if not isinstance(item, dict):
                continue
            ask = copy.deepcopy(item)
            raw_cue = ask.get("cue")
            main = str(raw_cue or "").split("#", 1)[0].strip()
            if not main:
                raise ValueError(f"{sec.dir / 'qa.json'} asks[{i}]: cue required")
            if "/" in main:
                qid = _validate_qid(main, f"section {sec.id} qa.asks[{i}].cue")
            else:
                qid = _validate_qid(
                    qualified_cue_id(sec.id, main),
                    f"section {sec.id} qa.asks[{i}].cue",
                )
            ask["cue"] = qid
            ask.setdefault("source", f"{sec.dir.name}/qa.json")
            qa_asks.append(ask)

    # Deterministic hash over every authored source file.
    h = hashlib.sha256()
    h.update(b"boardai-section-source/v1\n")
    for path, rel in sorted(all_files, key=lambda x: x[1]):
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        h.update(bytes.fromhex(file_sha256(path)))
    source_hash = h.hexdigest()

    return TrackSource(
        game=game,
        track=track,
        descriptor_path=descriptor_path,
        descriptor=descriptor,
        base_path=base_path,
        base_anim=base_anim,
        sections=sections,
        anim_doc=anim_doc,
        script_doc=script_meta,
        source_hash=source_hash,
        section_by_id=section_by_id,
        qid_to_section=qid_to_section,
        qid_to_local=qid_to_local,
        qid_to_cue=qid_to_cue,
        qa_asks=qa_asks,
    )


# ── compatibility entry points for tools ────────────────────────────────────

def _legacy_warning(game: str, track: str, path: Path) -> None:
    print(
        f"[legacy monolith] {game}/{track}: no section descriptor, reading {path}; "
        f"migrate this track to sections/ + tracks/ when possible",
        file=sys.stderr,
    )


def load_track_docs(
    game: str,
    track: str,
    *,
    explicit_source: str | Path | None = None,
    allow_legacy: bool = True,
    warn: bool = True,
) -> tuple[dict, dict, Path | None, str, bool]:
    """Load a track's animation and script docs.

    Returns ``(anim_doc, script_doc, source_root, source_hash, is_section)``.
    ``source_root`` is the directory stage references should be resolved from.
    Section-first is always preferred.  The legacy monolith branch exists only
    for unmigrated tracks and tests; it is announced on stderr and never used
    when a track descriptor exists.
    """
    if explicit_source is not None:
        path = Path(explicit_source)
        anim_doc = _read_json(path)
        return anim_doc, {}, None, file_sha256(path), False

    if has_section_source(game, track):
        src = load_track_source(game, track)
        return src.anim_doc, src.script_doc, src.tutorial, src.source_hash, True

    if allow_legacy:
        anim_path = legacy_anim_path(game, track)
        if anim_path.is_file():
            if warn:
                _legacy_warning(game, track, anim_path)
            script_path = legacy_script_path(game, track)
            script_doc = _read_json(script_path) if script_path.is_file() else {}
            anim_doc = _read_json(anim_path)
            return anim_doc, script_doc, None, file_sha256(anim_path), False
    raise FileNotFoundError(
        f"no section source or legacy monolith for {game}/{track}; "
        f"expected {track_descriptor_path(game, track)}"
    )


def load_anim_doc(game: str, track: str, *, explicit_source: str | Path | None = None) -> dict:
    return load_track_docs(
        game, track, explicit_source=explicit_source, allow_legacy=True, warn=True
    )[0]


def load_script_doc(game: str, track: str, *, explicit_source: str | Path | None = None) -> dict:
    return load_track_docs(
        game, track, explicit_source=explicit_source, allow_legacy=True, warn=True
    )[1]


def discover_tracks(game: str) -> list[str]:
    """Return track ids that have a descriptor in tracked source order."""
    tracks_dir = tutorial_dir(game) / "tracks"
    found: list[str] = []
    if tracks_dir.is_dir():
        for path in sorted(tracks_dir.glob("*.track.json")):
            try:
                doc = _read_json(path)
            except Exception:
                continue
            track = str(doc.get("track") or path.name[: -len(".track.json")]).strip()
            if track and track not in found:
                found.append(track)
    return found


def resolve_stage_path(rel: str, *roots: Path | None) -> Path:
    """Resolve a stage reference against tutorial/build/anim roots."""
    value = str(rel or "").strip().replace("\\", "/")
    if not value:
        raise ValueError("stage path is empty")
    candidates: list[Path] = []
    for root in roots:
        if root is None:
            continue
        root = Path(root)
        candidates.extend([root / value, root / (value + ".json")])
        candidates.extend([root / ".." / value, root / ".." / (value + ".json")])
        candidates.extend([root / ".." / ".." / value, root / ".." / ".." / (value + ".json")])
    for path in candidates:
        try:
            if path.is_file():
                return path.resolve()
        except OSError:
            continue
    raise FileNotFoundError(f"stage not found: {rel!r} (roots: {', '.join(str(r) for r in roots)})")


# ── migration-friendly assembly / CLI ───────────────────────────────────────

def assemble_track(game: str, track: str, *, write: bool = True) -> tuple[dict, dict]:
    """Resolve a section track and optionally write build/ intermediates.

    Kept for CLI compatibility; unlike the old implementation this never writes
    or reads the legacy monolithic source paths.
    """
    src = load_track_source(game, track)
    if write:
        src.write_build()
    return src.anim_doc, src.script_doc


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Resolve/build a section-first tutorial track")
    ap.add_argument("--game", required=True)
    ap.add_argument("--track", required=True)
    ap.add_argument("--check", action="store_true", help="resolve in memory; do not write build/")
    args = ap.parse_args(argv)
    try:
        result = assemble_track(args.game, args.track, write=not args.check)
    except Exception as exc:  # noqa: BLE001 - CLI should print a useful error
        print(f"section resolve failed: {exc}", file=sys.stderr)
        return 2
    anim, script = result
    print(
        f"OK   resolved {args.game}/{args.track}: "
        f"{len(anim.get('cues') or [])} cues / "
        f"{len(anim.get('sections') or [])} sections / "
        f"{len(script.get('cues') or [])} script cues"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
