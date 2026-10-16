#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Reusable tutorial-material library for the section-first source model.

A material is a small, game-scoped authoring asset.  The library deliberately
keeps four responsibilities separate:

* ``example``      -- concrete teaching situation: stable object identities,
                      ownership/zone/face/order facts, variants and phase refs.
* ``action``       -- deterministic state changes (STATE_OPS only).
* ``presentation`` -- camera/overlay/annotation visuals (PRESENTATION_OPS only).
* cue/section/track -- teaching order, narration and version-specific timing.

Materials live under ``content/games/<game>/tutorial/library/``.  Section cue
files call them through::

    "use": [{
      "ref": "splendor.action.seed_initial_market",
      "bind": {"market": "card_market"},
      "timing": {"deal": [{"anchor": "b2.start", "offset": 0.32}, ...]},
      "insert_at": 1
    }]

The module expands material events into the cue's ordinary event stream before
the existing schema/compiler sees them.  Source mapping is returned as
``MaterialUsage`` records so CLIs can explain why a generated event exists.
"""
from __future__ import annotations

import copy
import fnmatch
import json
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

MATERIAL_SCHEMA = "tutorial-material/v1"
MATERIAL_KINDS = {"example", "action", "presentation"}
MATERIAL_ID_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.:\-]*$")
SAFE_NAMESPACE_RE = re.compile(r"[^A-Za-z0-9_]+")
PLACEHOLDER_RE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_.\-]*)\}")
PARAM_TYPES = {"string", "number", "integer", "boolean", "zone", "entity", "screen_object", "any"}
STATE_OPS = {
    "ensure", "create", "destroy", "transfer", "stack", "shuffle",
    "move_order", "set_order", "set_face",
}
SEMANTIC_STATE_OPS = {"take", "move", "pay", "flip", "draw"}
PRESENTATION_OPS = {
    "show", "hide", "highlight", "point", "shape", "fade", "scale", "wait",
    "camera", "label", "magnifier", "overlay_show", "overlay_hide",
}
ALL_OPS = STATE_OPS | SEMANTIC_STATE_OPS | PRESENTATION_OPS
STATE_EVENT_OPS = STATE_OPS | SEMANTIC_STATE_OPS
# Explicit composition matrix at material dependency boundaries.
# Examples may compose actions (and other examples), but not presentations; a
# presentation is a cue-level visual companion, not a fact or state fragment.
ALLOWED_CHILD_KINDS = {
    "action": frozenset({"action"}),
    "presentation": frozenset({"presentation"}),
    "example": frozenset({"action", "example"}),
}


class MaterialError(ValueError):
    """Authoring error with a source-chain friendly message."""


def _deep_copy(value: Any) -> Any:
    return copy.deepcopy(value)


def _load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise MaterialError(f"{path}: invalid JSON: {exc}") from exc


def _material_root(tutorial_dir: Path) -> Path:
    return Path(tutorial_dir) / "library"


def _iter_material_files(tutorial_dir: Path) -> list[Path]:
    root = _material_root(tutorial_dir)
    if not root.is_dir():
        return []
    return sorted(
        p for p in root.rglob("*.json")
        if p.is_file() and not p.name.startswith(".")
    )


def _norm_local_id(cue: dict, section_id: str, where: str) -> str:
    explicit = cue.get("local_id")
    raw = explicit if explicit is not None else cue.get("id")
    if raw is None or not str(raw).strip():
        raise MaterialError(f"{where}: cue needs id/local_id")
    text = str(raw).strip()
    if "/" in text:
        sec, local = text.rsplit("/", 1)
        if sec != section_id:
            raise MaterialError(
                f"{where}: qualified cue id {text!r} belongs to section {sec!r}, not {section_id!r}"
            )
        text = local
    if not text:
        raise MaterialError(f"{where}: cue local id is empty")
    if "/" in text:
        raise MaterialError(f"{where}: cue local id may not contain '/': {text!r}")
    return text


def _zone_values(value: Any) -> list[str]:
    if isinstance(value, str):
        return [value] if value else []
    if isinstance(value, dict):
        zones = value.get("zones")
        if isinstance(zones, list):
            return [str(z) for z in zones if z]
        z = value.get("zone")
        return [str(z)] if z else []
    return []


def _matches_accepts(values: Iterable[str], accepts: list[str]) -> bool:
    values = [str(v) for v in values if v]
    if not values or not accepts:
        return True
    for value in values:
        for pattern in accepts:
            if pattern == value or pattern == "*" or fnmatch.fnmatchcase(value, pattern):
                return True
    return False


@dataclass
class MaterialAsset:
    id: str
    kind: str
    game: str
    path: Path
    raw: dict
    title: str = ""
    description: str = ""
    instructions: list[str] = field(default_factory=list)
    roles: dict[str, dict] = field(default_factory=dict)
    params: dict[str, dict] = field(default_factory=dict)
    steps: list[dict] = field(default_factory=list)
    facts: dict = field(default_factory=dict)
    variants: list[dict] = field(default_factory=list)
    actions: list[dict] = field(default_factory=list)
    contract: dict = field(default_factory=dict)
    timing: dict = field(default_factory=dict)
    rules: list[str] = field(default_factory=list)
    dependencies: set[str] = field(default_factory=set)

    @property
    def scope(self) -> str:
        return str((self.raw.get("scope") or "game")).strip() or "game"

    @property
    def default_variant(self) -> str | None:
        value = self.raw.get("default_variant")
        return str(value) if value is not None and str(value).strip() else None

    def describe(self) -> str:
        return f"{self.kind} {self.id} ({self.path})"


class MaterialLibrary:
    """Loaded, validated game material index."""

    def __init__(self, tutorial_dir: Path, game: str, assets: dict[str, MaterialAsset],
                 files: list[Path], warnings: list[str]):
        self.tutorial_dir = Path(tutorial_dir)
        self.game = str(game)
        self.assets = assets
        self.files = [Path(p) for p in files]
        self.warnings = list(warnings)

    @classmethod
    def load(cls, tutorial_dir: Path, game: str) -> "MaterialLibrary":
        assets: dict[str, MaterialAsset] = {}
        warnings: list[str] = []
        files = _iter_material_files(tutorial_dir)
        for path in files:
            raw = _load_json(path)
            if not isinstance(raw, dict):
                raise MaterialError(f"{path}: material root must be an object")
            # A library README/index.json is allowed but not a material.
            if raw.get("schema") != MATERIAL_SCHEMA:
                if raw.get("schema") is None and path.name.lower() in {"readme.json", "index.json"}:
                    continue
                raise MaterialError(
                    f"{path}: schema must be {MATERIAL_SCHEMA!r}, got {raw.get('schema')!r}"
                )
            mid = str(raw.get("id") or "").strip()
            if not MATERIAL_ID_RE.match(mid):
                raise MaterialError(f"{path}: invalid material id {mid!r}")
            if mid in assets:
                raise MaterialError(f"{path}: duplicate material id {mid!r}")
            mgame = str(raw.get("game") or "").strip()
            if mgame != game:
                raise MaterialError(
                    f"{path}: material game {mgame!r} does not match track game {game!r}"
                )
            if not mid.startswith(mgame + "."):
                warnings.append(
                    f"{path}: material id {mid!r} should be namespaced as {mgame}.<local-id>"
                )
            kind = str(raw.get("kind") or "").strip()
            if kind not in MATERIAL_KINDS:
                raise MaterialError(
                    f"{path}: kind must be one of {sorted(MATERIAL_KINDS)}, got {kind!r}"
                )
            asset = MaterialAsset(id=mid, kind=kind, game=mgame, path=path.resolve(), raw=raw)
            cls._parse_asset(asset, warnings)
            assets[mid] = asset

        lib = cls(Path(tutorial_dir), game, assets, files, warnings)
        cls._validate_dependencies(lib)
        return lib

    # ── parsing / static validation ─────────────────────────────────────

    @classmethod
    def _parse_asset(cls, asset: MaterialAsset, warnings: list[str]) -> None:
        raw = asset.raw
        where = str(asset.path)
        asset.title = str(raw.get("title") or "")
        asset.description = str(raw.get("description") or "")
        instr = raw.get("instructions")
        if instr is not None:
            if not isinstance(instr, list) or not all(isinstance(x, str) for x in instr):
                raise MaterialError(f"{where}: instructions must be a list of strings")
            asset.instructions = list(instr)

        asset.roles = cls._parse_declarations(raw.get("roles"), where, "role")
        asset.params = cls._parse_declarations(raw.get("params"), where, "param")

        steps = raw.get("steps")
        if steps is not None:
            if not isinstance(steps, list):
                raise MaterialError(f"{where}: steps must be a list")
            seen: set[str] = set()
            for i, step in enumerate(steps):
                cls._validate_step(step, f"{where} steps[{i}]", seen)
                asset.steps.append(copy.deepcopy(step))

        if asset.kind == "example":
            facts = raw.get("facts")
            if not isinstance(facts, dict):
                raise MaterialError(f"{where}: example needs a facts object")
            cls._validate_facts(facts, where)
            asset.facts = copy.deepcopy(facts)
            variants = raw.get("variants") or []
            if not isinstance(variants, list):
                raise MaterialError(f"{where}: variants must be a list")
            seen_variants: set[str] = set()
            for i, variant in enumerate(variants):
                if isinstance(variant, str):
                    vid = variant
                    entry = {"id": variant}
                elif isinstance(variant, dict):
                    vid = str(variant.get("id") or "").strip()
                    entry = copy.deepcopy(variant)
                else:
                    raise MaterialError(f"{where}: variants[{i}] must be a string or object")
                if not vid:
                    raise MaterialError(f"{where}: variants[{i}].id is required")
                if vid in seen_variants:
                    raise MaterialError(f"{where}: duplicate variant id {vid!r}")
                seen_variants.add(vid)
                if "steps" in entry:
                    vsteps = entry.get("steps")
                    if not isinstance(vsteps, list):
                        raise MaterialError(f"{where}: variant {vid!r}.steps must be a list")
                    vseen: set[str] = set()
                    for si, step in enumerate(vsteps):
                        cls._validate_step(step, f"{where} variant {vid} steps[{si}]", vseen)
                asset.variants.append(entry)
            actions = raw.get("actions") or raw.get("uses") or []
            if not isinstance(actions, list):
                raise MaterialError(f"{where}: example actions must be a list")
            for i, action in enumerate(actions):
                if isinstance(action, str):
                    item = {"ref": action}
                elif isinstance(action, dict):
                    ref = str(action.get("ref") or "").strip()
                    if not ref:
                        raise MaterialError(f"{where}: example actions[{i}].ref is required")
                    item = copy.deepcopy(action)
                else:
                    raise MaterialError(f"{where}: example actions[{i}] must be string or object")
                asset.actions.append(item)
        else:
            if raw.get("facts") is not None:
                warnings.append(f"{where}: facts on {asset.kind} material are ignored by design")
            if raw.get("variants") is not None:
                raise MaterialError(f"{where}: only examples may declare variants")
            if raw.get("actions") is not None:
                raise MaterialError(f"{where}: only examples may declare actions")

        contract = raw.get("contract")
        if contract is not None:
            if not isinstance(contract, dict):
                raise MaterialError(f"{where}: contract must be an object")
            cls._validate_contract(contract, f"{where} contract")
            asset.contract = copy.deepcopy(contract)
        timing = raw.get("timing")
        if timing is not None:
            if not isinstance(timing, dict):
                raise MaterialError(f"{where}: timing must be an object")
            allowed_timing = {"min_span", "min_event_duration", "conflict_check"}
            unknown_timing = sorted(set(timing) - allowed_timing)
            if unknown_timing:
                raise MaterialError(
                    f"{where}: unknown timing field(s): {', '.join(unknown_timing)}"
                )
            for field_name in ("min_span", "min_event_duration"):
                if field_name in timing:
                    try:
                        value = float(timing[field_name])
                    except (TypeError, ValueError):
                        raise MaterialError(f"{where}: timing.{field_name} must be numeric")
                    if value < 0:
                        raise MaterialError(f"{where}: timing.{field_name} must be >= 0")
            if "conflict_check" in timing and not isinstance(timing["conflict_check"], bool):
                raise MaterialError(f"{where}: timing.conflict_check must be boolean")
            asset.timing = copy.deepcopy(timing)
        rules = raw.get("rules") or []
        if not isinstance(rules, list) or not all(isinstance(x, str) for x in rules):
            raise MaterialError(f"{where}: rules must be a list of strings")
        asset.rules = list(rules)

        # Static dependency discovery (used by impact/order checks).
        asset.dependencies = set()
        all_steps = list(asset.steps)
        for variant in asset.variants:
            if isinstance(variant, dict):
                all_steps.extend(variant.get("steps") or [])
        for step in all_steps:
            for use in (step.get("use", step.get("uses")) or []):
                if isinstance(use, dict) and use.get("ref"):
                    asset.dependencies.add(str(use["ref"]))
        for action in asset.actions:
            asset.dependencies.add(str(action.get("ref") or ""))

        steps_for_kind = list(asset.steps)
        for variant in asset.variants:
            if isinstance(variant, dict):
                vlabel = f"variant {variant.get('id')!r} "
                for step in variant.get("steps") or []:
                    steps_for_kind.append((vlabel, step))
        for step in steps_for_kind:
            if isinstance(step, tuple):
                prefix, real_step = step
            else:
                prefix, real_step = "", step
            for ev in real_step.get("events") or []:
                op = ev.get("op")
                if asset.kind == "action" and op not in STATE_EVENT_OPS:
                    raise MaterialError(
                        f"{where}: action {prefix}step {real_step.get('id')!r} has non-state op "
                        f"{op!r}; move camera/annotation visuals to a presentation material"
                    )
                if asset.kind == "presentation" and op not in PRESENTATION_OPS:
                    raise MaterialError(
                        f"{where}: presentation {prefix}step {real_step.get('id')!r} has "
                        f"non-presentation op {op!r}; use an action material for state changes"
                    )
                if asset.kind == "example" and op not in STATE_EVENT_OPS:
                    raise MaterialError(
                        f"{where}: example {prefix}step {real_step.get('id')!r} has non-state "
                        f"op {op!r}; examples declare facts with state actions, while "
                        f"presentation stays in the cue or a presentation material"
                    )

    @classmethod
    def _parse_declarations(cls, raw: Any, where: str, label: str) -> dict[str, dict]:
        out: dict[str, dict] = {}
        if raw is None:
            return out
        if not isinstance(raw, list):
            raise MaterialError(f"{where}: {label}s must be a list")
        for i, item in enumerate(raw):
            if not isinstance(item, dict):
                raise MaterialError(f"{where}: {label}s[{i}] must be an object")
            name = str(item.get("name") or "").strip()
            if not name:
                raise MaterialError(f"{where}: {label}s[{i}].name is required")
            if name in out:
                raise MaterialError(f"{where}: duplicate {label} {name!r}")
            typ = str(item.get("type") or ("zone" if label == "role" else "any")).strip()
            if typ not in PARAM_TYPES:
                raise MaterialError(
                    f"{where}: {label} {name!r} type must be one of {sorted(PARAM_TYPES)}"
                )
            decl = copy.deepcopy(item)
            decl["name"] = name
            decl["type"] = typ
            if "required" in decl and not isinstance(decl["required"], bool):
                raise MaterialError(f"{where}: {label} {name!r}.required must be boolean")
            accepts = decl.get("accepts")
            if accepts is not None:
                if not isinstance(accepts, list) or not all(isinstance(x, str) for x in accepts):
                    raise MaterialError(f"{where}: {label} {name!r}.accepts must be a list of strings")
            out[name] = decl
        return out

    @classmethod
    def _validate_event_template(cls, ev: Any, where: str) -> None:
        if not isinstance(ev, dict):
            raise MaterialError(f"{where}: event must be an object")
        op = ev.get("op")
        if op not in ALL_OPS:
            raise MaterialError(f"{where}: unknown op {op!r}")
        if "anchor" not in ev:
            raise MaterialError(
                f"{where}: material events use semantic anchors like '@deal'; "
                f"numeric 'at' is not allowed in reusable materials"
            )
        anchor = ev.get("anchor")
        if not isinstance(anchor, str) or not anchor.startswith("@"):
            raise MaterialError(
                f"{where}: material event anchor must be a semantic marker starting with '@', "
                f"got {anchor!r}"
            )
        if "at" in ev:
            raise MaterialError(f"{where}: use anchor (semantic time), not numeric 'at'")
        if "offset" in ev and not isinstance(ev["offset"], (int, float)):
            # mapping offsets are spatial annotation nudges; keep them allowed
            if not isinstance(ev["offset"], dict):
                raise MaterialError(f"{where}: offset must be numeric or mapping")

    @classmethod
    def _validate_step(cls, step: Any, where: str, seen: set[str]) -> None:
        if not isinstance(step, dict):
            raise MaterialError(f"{where}: must be an object")
        sid = str(step.get("id") or "").strip()
        if not sid:
            raise MaterialError(f"{where}.id is required")
        if sid in seen:
            raise MaterialError(f"duplicate step id {sid!r} in {where}")
        seen.add(sid)
        status = str(step.get("status") or "required").strip()
        if status not in ("required", "optional"):
            raise MaterialError(f"{where}: step {sid!r} status must be required/optional")
        events = step.get("events")
        uses = step.get("use", step.get("uses"))
        if events is not None and not isinstance(events, list):
            raise MaterialError(f"{where}: step {sid!r}.events must be a list")
        if uses is not None and not isinstance(uses, list):
            raise MaterialError(f"{where}: step {sid!r}.use must be a list")
        if events is None and uses is None:
            raise MaterialError(f"{where}: step {sid!r} needs events or use")
        for j, ev in enumerate(events or []):
            cls._validate_event_template(ev, f"{where} step {sid} events[{j}]")
        for j, use in enumerate(uses or []):
            cls._validate_use_item(use, f"{where} step {sid} use[{j}]")

    @classmethod
    def _validate_use_item(cls, use: Any, where: str) -> None:
        if isinstance(use, str):
            if not use.strip():
                raise MaterialError(f"{where}: ref may not be empty")
            return
        if not isinstance(use, dict):
            raise MaterialError(f"{where}: use item must be a string or object")
        ref = str(use.get("ref") or "").strip()
        if not ref:
            raise MaterialError(f"{where}: ref is required")
        allowed = {
            "ref", "bind", "timing", "omit", "mode", "variant", "as", "insert_at",
            "note", "namespace",
        }
        unknown = sorted(set(use) - allowed)
        if unknown:
            raise MaterialError(f"{where}: unknown use field(s): {', '.join(unknown)}")
        if "bind" in use and not isinstance(use["bind"], dict):
            raise MaterialError(f"{where}: bind must be an object")
        if "timing" in use and not isinstance(use["timing"], dict):
            raise MaterialError(f"{where}: timing must be an object")
        if "omit" in use and (not isinstance(use["omit"], list) or
                              not all(isinstance(x, str) for x in use["omit"])):
            raise MaterialError(f"{where}: omit must be a list of step ids")
        if "insert_at" in use:
            try:
                idx = int(use["insert_at"])
            except (TypeError, ValueError) as exc:
                raise MaterialError(f"{where}: insert_at must be an integer") from exc
            if idx < 0:
                raise MaterialError(f"{where}: insert_at must be >= 0")
        mode = str(use.get("mode") or "continue").strip()
        if mode not in ("continue", "isolated"):
            raise MaterialError(f"{where}: mode must be continue/isolated, got {mode!r}")

    @classmethod
    def _validate_facts(cls, facts: dict, where: str) -> None:
        objects = facts.get("objects") or []
        if not isinstance(objects, list):
            raise MaterialError(f"{where}: facts.objects must be a list")
        seen: set[str] = set()
        for i, obj in enumerate(objects):
            if not isinstance(obj, dict):
                raise MaterialError(f"{where}: facts.objects[{i}] must be an object")
            oid = str(obj.get("id") or "").strip()
            if not oid:
                raise MaterialError(f"{where}: facts.objects[{i}].id is required")
            if oid in seen:
                raise MaterialError(f"{where}: duplicate fact object id {oid!r}")
            seen.add(oid)
        zones = facts.get("zones") or {}
        if zones is not None and not isinstance(zones, dict):
            raise MaterialError(f"{where}: facts.zones must be an object")

    @classmethod
    def _validate_contract(cls, contract: dict, where: str) -> None:
        allowed = {"preconditions", "postconditions", "rules", "notes"}
        unknown = sorted(set(contract) - allowed)
        if unknown:
            raise MaterialError(f"{where}: unknown field(s): {', '.join(unknown)}")

    @classmethod
    def _validate_dependencies(cls, lib: "MaterialLibrary") -> None:
        for asset in lib.assets.values():
            for ref in sorted(asset.dependencies):
                if ref not in lib.assets:
                    raise MaterialError(f"{asset.path}: unknown material dependency {ref!r}")
            for ref in sorted(asset.dependencies):
                if ref == asset.id:
                    raise MaterialError(
                        f"{asset.path}: material dependency cycle: {asset.id} -> {asset.id}"
                    )
        # Kind boundaries are enforced at dependency edges, recursively.  A
        # presentation cannot smuggle a state action in through a nested use,
        # and an action cannot smuggle visuals in the same way.
        for asset in lib.assets.values():
            allowed = ALLOWED_CHILD_KINDS[asset.kind]
            steps = list(asset.steps)
            for variant in asset.variants:
                if isinstance(variant, dict):
                    steps.extend(variant.get("steps") or [])
            for step in steps:
                sid = str(step.get("id") or "")
                for use in (step.get("use", step.get("uses")) or []):
                    if not isinstance(use, dict):
                        continue
                    ref = str(use.get("ref") or "").strip()
                    dep = lib.assets.get(ref)
                    if dep is None:
                        continue
                    if dep.kind not in allowed:
                        raise MaterialError(
                            f"{asset.path}: {asset.id} ({asset.kind}) step {sid!r} "
                            f"references {dep.id} ({dep.kind}); allowed child kinds: "
                            f"{sorted(allowed)}"
                        )
        # Cycle detection over the static step-use graph.
        visiting: set[str] = set()
        done: set[str] = set()

        def visit(mid: str, chain: list[str]) -> None:
            if mid in done:
                return
            if mid in visiting:
                raise MaterialError(
                    "material dependency cycle: " + " -> ".join(chain + [mid])
                )
            visiting.add(mid)
            asset = lib.assets[mid]
            for dep in sorted(asset.dependencies):
                visit(dep, chain + [mid])
            visiting.discard(mid)
            done.add(mid)

        for mid in sorted(lib.assets):
            visit(mid, [])

    # ── expansion ───────────────────────────────────────────────────────

    def expand_section(self, section_id: str, track: str, anim: dict, *,
                       where: str | None = None) -> tuple[dict, list["MaterialUsage"], list[Path]]:
        """Expand all ``use`` references in one section animation document.

        Returns a mutated copy, usage records and the material files that must
        participate in the track source hash.
        """
        section_id = str(section_id).strip()
        if not section_id:
            raise MaterialError("section id is required for material expansion")
        out = copy.deepcopy(anim)
        cues = out.get("cues")
        if not isinstance(cues, list):
            raise MaterialError(f"{where or section_id}: cues must be a list")
        existing_anchors = {
            str(a.get("id")): copy.deepcopy(a)
            for a in (out.get("time_anchors") or [])
            if isinstance(a, dict) and a.get("id")
        }
        generated_anchors: dict[str, dict] = {}
        usages: list[MaterialUsage] = []
        files: set[Path] = set()
        local_by_cue: dict[int, str] = {}
        for i, cue in enumerate(cues):
            if not isinstance(cue, dict):
                continue
            local = _norm_local_id(cue, section_id, f"{where or section_id} cue[{i}]")
            local_by_cue[i] = local
            qid = f"{section_id}/{local}"
            raw_entry = cue.get("entry")
            explicit_entry = str(raw_entry).strip() if raw_entry is not None and str(raw_entry).strip() else None
            if cue.get("local_id") is not None:
                del out["cues"][i]["local_id"]
            used_namespaces: set[str] = set()
            # `example` / `examples` declarations are cue metadata checked here.
            declared_examples: list[dict] = []
            raw_example = cue.get("example")
            if raw_example is not None:
                declared_examples.append(raw_example if isinstance(raw_example, dict)
                                        else {"ref": str(raw_example)})
            raw_examples = cue.get("examples")
            if raw_examples is not None:
                if not isinstance(raw_examples, list):
                    raise MaterialError(f"{qid}: examples must be a list")
                for item in raw_examples:
                    declared_examples.append(item if isinstance(item, dict) else {"ref": str(item)})
            for j, item in enumerate(declared_examples):
                ref = str(item.get("ref") or "").strip()
                if not ref:
                    raise MaterialError(f"{qid}.examples[{j}]: ref is required")
                asset = self.resolve(ref, where=f"{qid}.examples[{j}]")
                if asset.kind != "example":
                    raise MaterialError(
                        f"{qid}.examples[{j}]: {ref!r} is {asset.kind}, expected example"
                    )
                variant = self._select_variant(asset, item, f"{qid}.examples[{j}]")
                usages.append(MaterialUsage(
                    track=track, section=section_id, cue=qid, use_index=-1,
                    step=None, material=asset.id, kind=asset.kind, variant=variant,
                    mode=str(item.get("mode") or "continue"), path=asset.path,
                    where=f"{qid}.examples[{j}]",
                    event_start=0, event_count=0, explicit_entry=explicit_entry,
                    call_path=[asset.id],
                ))
                files.add(asset.path)

            uses = cue.get("use") or []
            if not isinstance(uses, list):
                raise MaterialError(f"{qid}: use must be a list")
            inline_events = cue.get("events") or []
            if not isinstance(inline_events, list):
                raise MaterialError(f"{qid}: events must be a list")
            insertions: dict[int, list[dict]] = {}
            for use_index, use in enumerate(uses):
                # Re-validate inline declarations (they may not have gone through parse).
                self._validate_use_item(use, f"{qid}.use[{use_index}]")
                expanded, expanded_usages, used_files = self._expand_use(
                    use, qid=qid, section_id=section_id, track=track,
                    use_index=use_index, existing_anchors=existing_anchors,
                    generated_anchors=generated_anchors,
                    cue=cue, used_namespaces=used_namespaces,
                    explicit_entry=explicit_entry,
                    where=f"{qid}.use[{use_index}]",
                )
                files.update(used_files)
                insert_at = int(use.get("insert_at", len(inline_events))) if isinstance(use, dict) else len(inline_events)
                insert_at = max(0, min(insert_at, len(inline_events)))
                insertions.setdefault(insert_at, []).append({
                    "events": expanded,
                    "usages": expanded_usages,
                })
            merged_events: list[dict] = []
            for pos in range(len(inline_events) + 1):
                for item in insertions.get(pos, []):
                    base = len(merged_events)
                    for usage in item["usages"]:
                        usage.event_start += base
                    merged_events.extend(item["events"])
                    usages.extend(item["usages"])
                if pos < len(inline_events):
                    merged_events.append(inline_events[pos])
            cue["events"] = merged_events
            # Example declarations must be backed by the required action refs.
            for j, item in enumerate(declared_examples):
                ref = str(item.get("ref") or "").strip()
                asset = self.assets[ref]
                used_refs = {
                    str(u.get("ref"))
                    for u in uses if isinstance(u, dict) and u.get("ref")
                }
                for action in asset.actions:
                    required = bool(action.get("required", True))
                    aref = str(action.get("ref") or "")
                    if not aref:
                        raise MaterialError(f"{qid}.examples[{j}]: {asset.id} has an action without ref")
                    if required and aref not in used_refs:
                        raise MaterialError(
                            f"{qid}.examples[{j}]: example {asset.id} requires action {aref!r} "
                            f"to be used in the same cue"
                        )
            if uses:
                cue.pop("use", None)
        # material declarations are authoring metadata, not compiler input
        for cue in out["cues"]:
            if isinstance(cue, dict):
                cue.pop("example", None)
                cue.pop("examples", None)
        merged_anchors = list(out.get("time_anchors") or [])
        by_id = {str(a.get("id")): a for a in merged_anchors if isinstance(a, dict)}
        for aid, definition in generated_anchors.items():
            if aid in by_id:
                continue
            merged_anchors.append(definition)
            by_id[aid] = definition
        out["time_anchors"] = merged_anchors
        return out, usages, sorted(files)

    def resolve(self, ref: str, *, where: str = "") -> MaterialAsset:
        text = str(ref or "").strip()
        if not text:
            raise MaterialError(f"{where}: material ref is empty")
        asset = self.assets.get(text)
        if asset is None:
            raise MaterialError(f"{where}: unknown material {text!r}")
        return asset

    def _variant_entry(self, asset: MaterialAsset, variant: str | None) -> dict | None:
        if variant is None:
            return None
        for entry in asset.variants:
            if str(entry.get("id")) == str(variant):
                return entry
        return None

    def _select_variant(self, asset: MaterialAsset, use: dict, where: str) -> str | None:
        variant = use.get("variant")
        if variant is not None:
            text = str(variant).strip()
        else:
            text = asset.default_variant
        if asset.variants:
            ids = [str(v.get("id")) for v in asset.variants]
            if text is None:
                if len(ids) == 1:
                    return ids[0]
                raise MaterialError(
                    f"{where}: material {asset.id} has variants {ids}; select one with variant"
                )
            if text not in ids:
                raise MaterialError(
                    f"{where}: material {asset.id} has no variant {text!r}; expected one of {ids}"
                )
        elif text:
            raise MaterialError(f"{where}: material {asset.id} has no variants")
        return text

    def _resolve_use_variant_steps(self, asset: MaterialAsset, use: dict,
                                   where: str) -> tuple[str | None, list[dict]]:
        """Resolve use.variant and the concrete step list for top/nested calls.

        Nested calls must use the exact same default/missing/illegal variant
        semantics as top-level calls.  Omit/status checks operate on the
        selected variant steps, not on the base steps that are replaced.
        """
        variant = None
        if asset.kind == "example":
            variant = self._select_variant(asset, use, where)
        elif use.get("variant") is not None:
            raise MaterialError(f"{where}: only examples accept a variant")
        selected_steps = asset.steps
        if variant is not None:
            entry = self._variant_entry(asset, variant)
            if entry is not None and "steps" in entry:
                selected_steps = entry.get("steps") or []
        return variant, selected_steps

    @staticmethod
    def _normalize_namespace(raw, fallback: str, where: str) -> str:
        text = str(raw or "").strip()
        if text:
            text = SAFE_NAMESPACE_RE.sub("_", text).strip("_")
            if not text:
                raise MaterialError(f"{where}: namespace/alias normalizes to empty")
            return text
        return SAFE_NAMESPACE_RE.sub("_", fallback).strip("_") or "u"

    @staticmethod
    def _claim_namespace(namespace: str, used: set[str], where: str) -> None:
        if namespace in used:
            raise MaterialError(
                f"{where}: namespace/alias {namespace!r} collides with another "
                f"material call in the same cue"
            )
        used.add(namespace)

    @staticmethod
    def _all_steps(asset: "MaterialAsset") -> list[dict]:
        steps = list(asset.steps)
        for variant in asset.variants:
            if isinstance(variant, dict):
                steps.extend(variant.get("steps") or [])
        return steps

    @staticmethod
    def _nested_segment(step: dict, use_index: int, raw_alias, where: str) -> str:
        if raw_alias is not None and str(raw_alias).strip():
            return str(raw_alias)
        step_id = SAFE_NAMESPACE_RE.sub("_", str(step.get("id") or "step")).strip("_") or "step"
        return f"s_{step_id}_u{use_index + 1}"

    def _expand_use(self, use: Any, *, qid: str, section_id: str, track: str,
                    use_index: int, existing_anchors: dict[str, dict],
                    generated_anchors: dict[str, dict], cue: dict,
                    used_namespaces: set[str], explicit_entry: str | None,
                    where: str) -> tuple[list[dict], list["MaterialUsage"], list[Path]]:
        if isinstance(use, str):
            use = {"ref": use}
        if not isinstance(use, dict):
            raise MaterialError(f"{where}: use item must be a string or object")
        ref = str(use.get("ref") or "").strip()
        asset = self.resolve(ref, where=where)
        variant, selected_steps = self._resolve_use_variant_steps(asset, use, where)

        alias = use.get("namespace") if "namespace" in use else use.get("as")
        namespace = self._normalize_namespace(alias, f"u{use_index + 1}", where)
        self._claim_namespace(namespace, used_namespaces, where)

        scope = self._build_scope(asset, use.get("bind"), where)
        timing = self._resolve_value(copy.deepcopy(use.get("timing") or {}), scope,
                                     f"{where}.timing")
        if not isinstance(timing, dict):
            raise MaterialError(f"{where}.timing must resolve to an object")
        mode = str(use.get("mode") or "continue").strip() or "continue"
        omit_raw = use.get("omit") or []
        omit = {str(x) for x in omit_raw}
        valid_steps = {str(s.get("id")) for s in selected_steps}
        unknown_omit = sorted(omit - valid_steps)
        if unknown_omit:
            raise MaterialError(f"{where}: omit references unknown step(s): {', '.join(unknown_omit)}")
        for step in selected_steps:
            sid = str(step.get("id"))
            if sid in omit and str(step.get("status") or "required") != "optional":
                raise MaterialError(f"{where}: step {sid!r} is required and cannot be omitted")

        ctx = _ExpansionContext(
            library=self, qid=qid, section_id=section_id, track=track,
            namespace=namespace, timing=timing, existing_anchors=existing_anchors,
            generated_anchors=generated_anchors, omit=omit, where=where,
            files=set(), counters={}, depth=0, active=[],
            used_namespaces=used_namespaces, explicit_entry=explicit_entry,
        )
        ctx.call_path = [asset.id]
        expand_asset = asset
        if selected_steps is not asset.steps:
            expand_asset = copy.copy(asset)
            expand_asset.steps = copy.deepcopy(selected_steps)
        event_usages: list[MaterialUsage] = []
        if asset.kind == "example" and not expand_asset.steps:
            events: list[dict] = []
        else:
            events, event_usages = self._expand_asset(expand_asset, ctx, scope, [asset.id])
        contract = None
        if asset.kind == "action" and asset.contract:
            contract = self._resolve_value(
                copy.deepcopy(asset.contract), scope, f"{where}.contract"
            )
        usage = MaterialUsage(
            track=track, section=section_id, cue=qid, use_index=use_index,
            step=None, material=asset.id, kind=asset.kind, variant=variant,
            mode=mode, path=asset.path, where=where,
            events=events, bindings=copy.deepcopy(use.get("bind") or {}),
            timing=copy.deepcopy(use.get("timing") or {}),
            contract=contract, event_start=0, event_count=len(events),
            explicit_entry=explicit_entry,
            call_path=[asset.id],
        )
        return events, [usage] + event_usages, sorted(ctx.files)

    def _expand_asset(self, asset: MaterialAsset, ctx: "_ExpansionContext",
                      scope: dict, chain: list[str]) -> tuple[list[dict], list["MaterialUsage"]]:
        if asset.id in chain[:-1]:
            raise MaterialError(
                f"{ctx.where}: material dependency cycle: " + " -> ".join(chain)
            )
        if len(chain) > 8:
            raise MaterialError(f"{ctx.where}: material expansion depth > 8 at {asset.id}")
        out: list[dict] = []
        child_usages: list[MaterialUsage] = []
        ctx.files.add(asset.path)
        for step in asset.steps:
            sid = str(step.get("id"))
            if sid in ctx.omit:
                continue
            uses = step.get("use", step.get("uses")) or []
            for j, use in enumerate(uses):
                nested_where = f"{ctx.where} step {sid} use[{j}]"
                nested, nested_usages, nested_files = self._expand_nested_use(
                    use, asset=asset, step=step, use_index=j,
                    parent_scope=scope, ctx=ctx,
                    where=nested_where, chain=chain,
                )
                base = len(out)
                for nested_usage in nested_usages:
                    nested_usage.event_start += base
                out.extend(nested)
                child_usages.extend(nested_usages)
                ctx.files.update(nested_files)
            for j, ev in enumerate(step.get("events") or []):
                where_ev = f"{ctx.where} step {sid} events[{j}]"
                out.append(self._expand_event(asset, step, ev, scope, ctx, where_ev))
        return out, child_usages

    def _expand_nested_use(self, use: Any, *, asset: MaterialAsset, step: dict,
                           use_index: int, parent_scope: dict,
                           ctx: "_ExpansionContext", where: str,
                           chain: list[str]) -> tuple[list[dict], list["MaterialUsage"], list[Path]]:
        if isinstance(use, str):
            use = {"ref": use}
        if not isinstance(use, dict):
            raise MaterialError(f"{where}: nested use must be string or object")
        ref = str(self._resolve_value(use.get("ref"), parent_scope, f"{where}.ref") or "")
        nested_asset = self.resolve(ref, where=where)
        nested_variant, selected_steps = self._resolve_use_variant_steps(
            nested_asset, use, where
        )
        bind = self._resolve_value(use.get("bind") or {}, parent_scope, f"{where}.bind")
        if not isinstance(bind, dict):
            raise MaterialError(f"{where}.bind must resolve to an object")
        scope = self._build_scope(nested_asset, bind, where)
        timing = self._resolve_value(copy.deepcopy(use.get("timing") or {}), parent_scope,
                                     f"{where}.timing")
        if not isinstance(timing, dict):
            raise MaterialError(f"{where}.timing must resolve to an object")
        # Nested timing markers inherit the parent invocation unless overridden.
        # The counter is intentionally scoped to the top-level call context so a
        # caller can distribute a marker list across sibling nested calls.
        merged_timing = dict(ctx.timing)
        merged_timing.update(timing)
        previous_timing, previous_omit = ctx.timing, ctx.omit
        previous_ns = ctx.namespace
        nested_omit = set(str(x) for x in use.get("omit") or [])
        valid_steps = {str(s.get("id")) for s in selected_steps}
        if not nested_omit <= valid_steps:
            raise MaterialError(
                f"{where}: omit references unknown step(s): "
                + ", ".join(sorted(nested_omit - valid_steps))
            )
        for step_decl in selected_steps:
            if (str(step_decl.get("id")) in nested_omit
                    and str(step_decl.get("status") or "required") != "optional"):
                raise MaterialError(
                    f"{where}: step {step_decl.get('id')!r} is required and cannot be omitted"
                )
        alias = use.get("namespace") if "namespace" in use else use.get("as")
        segment = self._nested_segment(step, use_index, alias, where)
        child_namespace = f"{ctx.namespace}__{self._normalize_namespace(segment, '', where)}"
        self._claim_namespace(child_namespace, ctx.used_namespaces, where)
        ctx.timing = merged_timing
        ctx.omit = nested_omit
        ctx.namespace = child_namespace
        previous_path = ctx.call_path
        nested_call_path = list(ctx.call_path) + [
            f"{asset.id}::{step.get('id')}[{use_index}]",
            nested_asset.id,
        ]
        ctx.call_path = nested_call_path
        expand_asset = nested_asset
        if selected_steps is not nested_asset.steps:
            expand_asset = copy.copy(nested_asset)
            expand_asset.steps = copy.deepcopy(selected_steps)
        try:
            events, nested_usages = self._expand_asset(
                expand_asset, ctx, scope, chain + [asset.id]
            )
        finally:
            ctx.timing, ctx.omit = previous_timing, previous_omit
            ctx.namespace = previous_ns
            ctx.call_path = previous_path
        contract = None
        if nested_asset.kind == "action" and nested_asset.contract:
            contract = self._resolve_value(
                copy.deepcopy(nested_asset.contract), scope, f"{where}.contract"
            )
        usage = MaterialUsage(
            track=ctx.track, section=ctx.section_id, cue=ctx.qid, use_index=-1,
            step=str(step.get("id") or ""), material=nested_asset.id,
            kind=nested_asset.kind, variant=nested_variant,
            mode=str(use.get("mode") or "continue"), path=nested_asset.path,
            where=where, events=events, bindings=copy.deepcopy(bind),
            timing=copy.deepcopy(use.get("timing") or {}), contract=contract,
            event_start=0, event_count=len(events),
            explicit_entry=ctx.explicit_entry,
            call_path=list(nested_call_path),
        )
        return events, [usage] + nested_usages, [nested_asset.path]

    def _build_scope(self, asset: MaterialAsset, bind_raw: Any, where: str) -> dict:
        if bind_raw is None:
            bind_raw = {}
        if not isinstance(bind_raw, dict):
            raise MaterialError(f"{where}: bind must be an object")
        unknown = sorted(set(bind_raw) - set(asset.roles) - set(asset.params))
        if unknown:
            raise MaterialError(
                f"{where}: unknown bind/param(s) {', '.join(unknown)} for {asset.id}"
            )
        scope: dict[str, Any] = {}
        for name, decl in asset.params.items():
            if name in bind_raw:
                value = bind_raw[name]
            elif "default" in decl:
                value = copy.deepcopy(decl["default"])
            elif decl.get("required", True):
                raise MaterialError(f"{where}: missing required param {name!r} for {asset.id}")
            else:
                value = None
            scope[name] = value
        for name, decl in asset.roles.items():
            if name in bind_raw:
                value = bind_raw[name]
            elif "default" in decl:
                value = copy.deepcopy(decl["default"])
            elif decl.get("required", True):
                raise MaterialError(f"{where}: missing required role {name!r} for {asset.id}")
            else:
                value = None
            self._check_role_binding(name, decl, value, f"{where}.bind.{name}")
            scope[name] = value
        return scope

    @staticmethod
    def _check_role_binding(name: str, decl: dict, value: Any, where: str) -> None:
        if value is None:
            return
        typ = str(decl.get("type") or "any")
        accepted = [str(x) for x in (decl.get("accepts") or [])]
        if typ == "zone":
            zones = _zone_values(value)
            if not zones:
                raise MaterialError(f"{where}: role {name!r} needs a zone string or {{zone}} object")
            if not _matches_accepts(zones, accepted):
                raise MaterialError(
                    f"{where}: role {name!r} bound to {zones!r}, accepts {accepted!r}"
                )
        elif typ == "entity":
            if not isinstance(value, dict) or str(value.get("space") or "entity") != "entity":
                raise MaterialError(f"{where}: role {name!r} needs an entity selector object")
            zones = _zone_values(value)
            if accepted and not _matches_accepts(zones, accepted):
                concept = str(value.get("concept") or value.get("template") or "")
                if not (concept and any(fnmatch.fnmatchcase(concept, p) for p in accepted)):
                    raise MaterialError(
                        f"{where}: role {name!r} bound to {value!r}, accepts {accepted!r}"
                    )
        elif typ == "screen_object":
            if isinstance(value, str):
                if not value.strip():
                    raise MaterialError(f"{where}: role {name!r} screen id is empty")
            elif not isinstance(value, dict) or not (value.get("id") or value.get("overlay")):
                raise MaterialError(f"{where}: role {name!r} needs a screen id/overlay")
        elif typ == "number":
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                raise MaterialError(f"{where}: role/param {name!r} needs a number")
        elif typ == "integer":
            if isinstance(value, bool) or not isinstance(value, int):
                raise MaterialError(f"{where}: role/param {name!r} needs an integer")
        elif typ == "boolean":
            if not isinstance(value, bool):
                raise MaterialError(f"{where}: role/param {name!r} needs a boolean")
        elif typ == "string":
            if not isinstance(value, str):
                raise MaterialError(f"{where}: role/param {name!r} needs a string")
        elif typ == "any":
            pass

    def _resolve_value(self, value: Any, scope: dict, where: str) -> Any:
        if isinstance(value, str):
            if value.startswith("$") and value[1:] in scope:
                return copy.deepcopy(scope[value[1:]])
            full = PLACEHOLDER_RE.fullmatch(value)
            if full:
                name = full.group(1)
                if name not in scope:
                    raise MaterialError(f"{where}: unknown placeholder ${{{name}}}")
                return copy.deepcopy(scope[name])
            def repl(match: re.Match) -> str:
                name = match.group(1)
                if name not in scope:
                    raise MaterialError(f"{where}: unknown placeholder ${{{name}}}")
                replacement = scope[name]
                if isinstance(replacement, (dict, list)):
                    raise MaterialError(
                        f"{where}: placeholder ${{{name}}} cannot embed an object/list; "
                        f"use the exact value form '${name}'"
                    )
                return str(replacement)
            return PLACEHOLDER_RE.sub(repl, value)
        if isinstance(value, dict):
            if len(value) == 1:
                key, item = next(iter(value.items()))
                if key in {"$param", "$role", "$bind", "$ref"}:
                    name = str(item or "")
                    if name not in scope:
                        raise MaterialError(f"{where}: unknown placeholder {key}: {name!r}")
                    return copy.deepcopy(scope[name])
            out: dict = {}
            for k, v in value.items():
                nk = k
                if isinstance(k, str) and ("${" in k or k.startswith("$")):
                    nk = self._resolve_value(k, scope, f"{where}.<key>")
                    if not isinstance(nk, str):
                        raise MaterialError(f"{where}: dict key placeholder must resolve to a string")
                out[nk] = self._resolve_value(v, scope, f"{where}.{nk}")
            return out
        if isinstance(value, list):
            return [self._resolve_value(v, scope, f"{where}[{i}]")
                    for i, v in enumerate(value)]
        return value

    def _expand_event(self, asset: MaterialAsset, step: dict, template: dict,
                      scope: dict, ctx: "_ExpansionContext", where: str) -> dict:
        ev = self._resolve_value(copy.deepcopy(template), scope, where)
        if not isinstance(ev, dict):
            raise MaterialError(f"{where}: event template did not resolve to an object")
        anchor = str(ev.get("anchor") or "")
        if not anchor.startswith("@"):
            raise MaterialError(f"{where}: material event anchor must start with '@'")
        marker = anchor[1:]
        anchor_id, bind_offset, definition, message, local_at = ctx.resolve_timing(marker)
        if local_at is not None:
            ev.pop("anchor", None)
            ev["at"] = local_at
            self._expand_local_ids(ev, ctx.namespace, where)
            return ev
        if anchor_id is None:
            raise MaterialError(f"{where}: {message}")
        ev["anchor"] = anchor_id
        if bind_offset is not None:
            base = ev.get("offset")
            if base is None:
                ev["offset"] = bind_offset
            elif isinstance(base, (int, float)):
                ev["offset"] = float(base) + float(bind_offset)
            elif isinstance(base, dict):
                # spatial nudge does not change event time; keep it and drop the
                # timing offset rather than silently producing two kinds of offset.
                raise MaterialError(
                    f"{where}: anchor timing offset cannot be combined with a mapping "
                    f"annotation nudge; move the nudge to its own inline event"
                )
            else:
                raise MaterialError(f"{where}: event offset must be numeric or mapping")
        if definition is not None:
            ctx.register_anchor(definition, where)
        self._expand_local_ids(ev, ctx.namespace, where)
        return ev

    @staticmethod
    def _expand_local_ids(value: Any, namespace: str, where: str) -> None:
        """Rewrite ``@name`` local screen/label ids inside one expanded event.

        Materials use ``"id": "@pin"`` for temporary screen objects.  Every use
        instance gets its own namespace so repeated instantiations in one cue do
        not collide.  Entity identities are bindings, never local placeholders.
        """
        id_keys = {"id", "overlay", "label_key", "marker_key"}
        if isinstance(value, dict):
            for key, item in list(value.items()):
                if key in id_keys and isinstance(item, str) and item.startswith("@"):
                    value[key] = f"{namespace}__{item[1:]}"
                else:
                    MaterialLibrary._expand_local_ids(item, namespace, where)
        elif isinstance(value, list):
            for item in value:
                MaterialLibrary._expand_local_ids(item, namespace, where)


@dataclass
class MaterialUsage:
    track: str
    section: str
    cue: str
    use_index: int
    step: str | None
    material: str
    kind: str
    variant: str | None
    mode: str
    path: Path
    where: str
    events: list[dict] = field(default_factory=list)
    bindings: dict = field(default_factory=dict)
    timing: dict = field(default_factory=dict)
    contract: dict | None = None
    event_start: int = 0
    event_count: int = 0
    explicit_entry: str | None = None
    call_path: list[str] = field(default_factory=list)

    def source_chain(self) -> str:
        pieces = [self.cue, f"use[{self.use_index}] {self.material}"]
        if self.variant:
            pieces.append(f"variant={self.variant}")
        pieces.append(f"mode={self.mode}")
        if self.call_path:
            pieces.append("path=" + " -> ".join(self.call_path))
        return " ".join(pieces)

    def to_dict(self) -> dict:
        return {
            "track": self.track,
            "section": self.section,
            "cue": self.cue,
            "use_index": self.use_index,
            "step": self.step,
            "material": self.material,
            "kind": self.kind,
            "variant": self.variant,
            "mode": self.mode,
            "where": self.where,
            "source": str(self.path),
            "event_start": self.event_start,
            "event_count": self.event_count,
            "explicit_entry": self.explicit_entry,
            "call_path": list(self.call_path),
        }


class _ExpansionContext:
    def __init__(self, *, library: MaterialLibrary, qid: str, section_id: str,
                 track: str, namespace: str, timing: dict, existing_anchors: dict,
                 generated_anchors: dict, omit: set[str], where: str,
                 files: set[Path], counters: dict, depth: int, active: list[str],
                 used_namespaces: set[str], explicit_entry: str | None):
        self.library = library
        self.qid = qid
        self.section_id = section_id
        self.track = track
        self.namespace = namespace
        self.timing = timing
        self.existing_anchors = existing_anchors
        self.generated_anchors = generated_anchors
        self.omit = set(omit)
        self.where = where
        self.files = files
        self.counters = counters
        self.depth = depth
        self.active = active
        self.used_namespaces = used_namespaces
        self.explicit_entry = explicit_entry
        self.call_path: list[str] = []
        self._local_beat_verified: set[str] = set()

    def resolve_timing(self, marker: str) -> tuple[str | None, float | None, dict | None, str, float | None]:
        binding = self.timing.get(marker)
        if binding is None and marker.startswith("@"):
            binding = self.timing.get("@" + marker)
        if binding is None and marker in ("self.start", "start"):
            return f"{self.qid}.start", None, {
                "id": f"{self.qid}.start", "cue": self._local(),
                "edge": "cue_start",
            }, "", None
        if binding is None and marker in ("self.end", "end"):
            return f"{self.qid}.end", None, {
                "id": f"{self.qid}.end", "cue": self._local(),
                "edge": "cue_end",
            }, "", None
        if binding is None:
            # repeated markers may use a list; otherwise this is missing.
            return None, None, None, f"timing marker '@{marker}' is not bound by the caller", None
        # deterministic list consumption for repeated markers
        idx = self.counters.get(marker, 0)
        if isinstance(binding, list):
            if idx >= len(binding):
                return None, None, None, (
                    f"timing marker '@{marker}' has only {len(binding)} binding(s), "
                    f"but the material uses it again"
                ), None
            item = binding[idx]
            self.counters[marker] = idx + 1
        else:
            item = binding
        if not isinstance(item, dict):
            return None, None, None, (
                f"timing marker '@{marker}' must bind to an object like "
                f"{{anchor: 'b1.start', offset: 0.2}} or {{at: 1.2}}"
            ), None
        return self._timing_from_binding(marker, item)

    def _timing_from_binding(self, marker: str, item: dict) -> tuple[str | None, float | None, dict | None, str, float | None]:
        if "at" in item:
            allowed = {"at", "offset"}
            unknown = sorted(set(item) - allowed)
            if unknown:
                return None, None, None, (
                    f"timing marker '@{marker}' has unknown field(s): {', '.join(unknown)}"
                ), None
            try:
                at = float(item["at"])
                off = float(item.get("offset", 0.0) or 0.0)
            except (TypeError, ValueError):
                return None, None, None, f"timing marker '@{marker}' at/offset must be numeric", None
            if at < 0 or at + off < 0:
                return None, None, None, f"timing marker '@{marker}' resolves to negative time", None
            return "@at", None, None, "", at + off
        allowed = {"anchor", "beat", "edge", "offset"}
        unknown = sorted(set(item) - allowed)
        if unknown:
            return None, None, None, (
                f"timing marker '@{marker}' has unknown field(s): {', '.join(unknown)}"
            ), None
        offset = item.get("offset")
        if offset is not None:
            try:
                offset = float(offset)
            except (TypeError, ValueError):
                return None, None, None, f"timing marker '@{marker}' offset must be numeric", None
        anchor = item.get("anchor")
        beat = item.get("beat")
        edge = str(item.get("edge") or "").strip()
        if edge in ("", "start", "cue_start") and anchor in (None, "", "start", "cue_start") and not beat:
            aid = f"{self.qid}.start"
            return aid, offset, {"id": aid, "cue": self._local(), "edge": "cue_start"}, "", None
        if edge in ("end", "cue_end") and anchor in (None, "", "end", "cue_end") and not beat:
            aid = f"{self.qid}.end"
            return aid, offset, {"id": aid, "cue": self._local(), "edge": "cue_end"}, "", None
        if anchor:
            text = str(anchor).strip()
            if text in ("start", "cue_start"):
                aid = f"{self.qid}.start"
                return aid, offset, {"id": aid, "cue": self._local(), "edge": "cue_start"}, "", None
            if text in ("end", "cue_end"):
                aid = f"{self.qid}.end"
                return aid, offset, {"id": aid, "cue": self._local(), "edge": "cue_end"}, "", None
            if "/" in text:
                # absolute anchor id (author explicitly bound it in the track cue)
                if text not in self.existing_anchors and text not in self.generated_anchors:
                    return None, None, None, f"absolute anchor {text!r} is not declared in this section", None
                return text, offset, None, "", None
            if text.endswith(".start") or text.endswith(".end"):
                edge_v = "start" if text.endswith(".start") else "end"
                stem = text[: -len(".start" if edge_v == "start" else ".end")]
                full_beat = f"{self.qid}.{stem}"
                aid = f"{full_beat}.{edge_v}"
                return aid, offset, {
                    "id": aid, "cue": self._local(), "beat": full_beat, "edge": edge_v,
                }, "", None
            return None, None, None, f"timing anchor {text!r} must end with .start/.end", None
        if beat:
            beat_text = str(beat).strip()
            full_beat = beat_text if beat_text.startswith(self.qid + ".") else f"{self.qid}.{beat_text}"
            edge_v = str(edge or "start")
            if edge_v not in ("start", "end"):
                return None, None, None, (
                    f"timing marker '@{marker}' edge must be start/end when beat is used"
                ), None
            aid = f"{full_beat}.{edge_v}"
            return aid, offset, {
                "id": aid, "cue": self._local(), "beat": full_beat, "edge": edge_v,
            }, "", None
        return None, None, None, f"timing marker '@{marker}' needs anchor or beat", None

    def _local(self) -> str:
        return self.qid.rsplit("/", 1)[-1]

    def register_anchor(self, definition: dict, where: str) -> None:
        aid = str(definition.get("id") or "")
        if not aid:
            raise MaterialError(f"{where}: generated anchor definition has no id")
        existing = self.existing_anchors.get(aid) or self.generated_anchors.get(aid)
        if existing is not None:
            for key in ("cue", "beat", "edge", "offset"):
                if key in existing and key in definition and existing.get(key) != definition.get(key):
                    raise MaterialError(
                        f"{where}: anchor {aid!r} conflicts with an existing definition "
                        f"({existing.get(key)!r} != {definition.get(key)!r})"
                    )
            return
        self.generated_anchors[aid] = copy.deepcopy(definition)


def load_materials(game: str, tutorial_dir: Path) -> MaterialLibrary:
    return MaterialLibrary.load(Path(tutorial_dir), str(game))


def expand_anim_with_materials(game: str, tutorial_dir: Path, section_id: str,
                               track: str, anim: dict) -> tuple[dict, list[MaterialUsage], list[Path]]:
    lib = load_materials(game, tutorial_dir)
    return lib.expand_section(section_id, track, anim)


def usage_records(track_source) -> list[dict]:
    """Return JSON-ready material usage records from a section TrackSource."""
    return [u.to_dict() for u in getattr(track_source, "material_usages", [])]


if __name__ == "__main__":
    raise SystemExit("Use animation/material_cli.py for material tooling")
