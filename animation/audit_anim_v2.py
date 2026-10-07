#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic cross-cue audit for a v2 tutorial animation track.

This script deliberately works on the compiled snapshots and the v2 source
events without changing any animation data.  It covers three cross-cue blind
spots of the existing checkers:

1. global physical conservation of the profile's tracked pieces;
2. refill of the profile's market zone after pieces are removed from it;
3. source ``point`` / ``shape`` / ``highlight`` events that never become
   compiled pointer clips.

Game vocabulary lives in
``content/games/{game}/tutorial/animation/audit-profile.json``; the engine only
consumes that profile.  New games should not need code changes here.

Usage:
    python3 animation/audit_anim_v2.py --game splendor --track full
    python3 animation/audit_anim_v2.py --game splendor --track full --from-cue action.cards.intro.001
    python3 animation/audit_anim_v2.py --game splendor --track full --json

Exit codes:
    0  no ERR
    1  at least one ERR
    2  missing input file or invalid JSON
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent

sys.path.insert(0, str(ROOT / "animation"))
import anim_schema_v2 as schema  # noqa: E402
import compiled_state as logical_state  # noqa: E402

CHECK_ORDER = {"boundary": 0, "demo": 1, "conservation": 2, "refill": 3, "pointer": 4}
AUDIT_PROFILE_REL = Path("tutorial") / "animation" / "audit-profile.json"


# ---------------------------------------------------------------------------
# generic helpers


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def load_audit_profile(game: str, path: str | Path | None = None) -> dict:
    """Load a game's audit profile.

    The profile owns game-specific inventory and refill vocabulary; this audit
    engine must not hardcode Splendor's gem colors, deck sizes or zone names.
    """
    if path is not None:
        profile_path = Path(path)
    else:
        profile_path = ROOT / "content" / "games" / str(game) / AUDIT_PROFILE_REL
    if not profile_path.exists():
        raise FileNotFoundError(f"missing audit profile: {profile_path}")
    profile = load_json(profile_path)
    if not isinstance(profile, dict):
        raise ValueError(f"audit profile is not an object: {profile_path}")
    return profile


def as_list(value: Any) -> list:
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def _strip_angle(value: Any) -> str:
    return str(value or "").strip().strip("<>").strip()


def _profile_inventory(profile: dict) -> dict:
    return profile.get("inventory") if isinstance(profile.get("inventory"), dict) else {}


def _profile_refill(profile: dict) -> dict:
    return profile.get("refill") if isinstance(profile.get("refill"), dict) else {}


def _profile_gold(profile: dict) -> dict:
    gold = _profile_inventory(profile).get("gold")
    return gold if isinstance(gold, dict) else {}


def _gold_key(profile: dict) -> str:
    return str(_profile_gold(profile).get("key") or "gold")


def _inventory_order(profile: dict) -> list[str]:
    inv = _profile_inventory(profile)
    order = [str(item.get("key")) for item in inv.get("items") or []
             if isinstance(item, dict) and item.get("key") is not None]
    for color in (inv.get("gems") or {}).get("colors") or []:
        color = str(color)
        if color not in order:
            order.append(color)
    gold = _gold_key(profile)
    if gold not in order:
        order.append(gold)
    return order


def _inventory_labels(profile: dict) -> dict[str, str]:
    labels: dict[str, str] = {}
    for item in _profile_inventory(profile).get("items") or []:
        if isinstance(item, dict) and item.get("key") is not None:
            labels[str(item["key"])] = str(item.get("label") or item["key"])
    gems = _profile_inventory(profile).get("gems") or {}
    for color in gems.get("colors") or []:
        labels.setdefault(str(color), str((gems.get("labels") or {}).get(str(color)) or color))
    gold = _profile_gold(profile)
    labels[_gold_key(profile)] = str(gold.get("label") or _gold_key(profile))
    return labels


def _match_values(expected: Any) -> list[str]:
    return [str(value) for value in as_list(expected)]


def _component_field(comp: dict, field: str) -> str:
    attr = {
        "id": "Id",
        "concept": "Concept",
        "template": "TemplateId",
        "palette": "Palette",
        "zone": "ZoneId",
    }.get(field)
    return str(comp.get(attr) or "") if attr else ""


def _component_matches(comp: dict, spec: Any, profile: dict) -> bool:
    """Return True when every key in ``spec`` matches the component."""
    if not isinstance(spec, dict) or not spec:
        return False
    for key, expected in spec.items():
        key = str(key)
        if key == "color":
            actual = _component_color(comp, profile) or ""
            if actual not in _match_values(expected):
                return False
            continue
        if key.endswith("_prefix"):
            actual = _component_field(comp, key[:-len("_prefix")])
            if not any(actual.startswith(value) for value in _match_values(expected)):
                return False
            continue
        actual = _component_field(comp, key)
        if actual not in _match_values(expected):
            return False
    return True


def _component_excluded(comp: dict, spec: Any, profile: dict) -> bool:
    """Return True when any key in an exclusion spec matches the component."""
    if not isinstance(spec, dict) or not spec:
        return False
    return any(_component_matches(comp, {key: value}, profile)
               for key, value in spec.items())


def _component_color(comp: dict, profile: dict) -> str | None:
    """Derive the gem/gold color key from a compiled ComponentState.

    The profile controls the authoring vocabulary so a non-Splendor game can
    use different part keys, palette prefixes or gold concepts.
    """
    config = _profile_inventory(profile).get("component_color") or {}
    part_keys = [str(key) for key in (config.get("part_keys") or [])]
    for key in part_keys:
        for part in comp.get("parts") or []:
            if isinstance(part, dict) and str(part.get("key")) == key:
                value = _strip_angle(part.get("value"))
                if value:
                    return value
    palette = str(comp.get("Palette") or "")
    gold_palette = str(config.get("gold_palette") or "")
    if gold_palette and palette == gold_palette:
        return _gold_key(profile)
    palette_prefix = str(config.get("palette_prefix") or "")
    if palette_prefix and palette.startswith(palette_prefix):
        return palette[len(palette_prefix):]
    concept = str(comp.get("Concept") or "")
    if concept in {str(value) for value in config.get("gold_concepts") or []}:
        return _gold_key(profile)
    return None


def count_inventory(components: Iterable[dict] | None, profile: dict) -> dict[str, int]:
    """Count the physical pieces governed by the profile's inventory check."""
    inv = _profile_inventory(profile)
    item_specs = [item for item in inv.get("items") or [] if isinstance(item, dict)]
    counts = {key: 0 for key in _inventory_order(profile)}
    gem_colors = {str(color) for color in (inv.get("gems") or {}).get("colors") or []}
    gold_key = _gold_key(profile)

    for comp in components or []:
        if not isinstance(comp, dict):
            continue
        for item in item_specs:
            if _component_matches(comp, item.get("match"), profile) and not _component_excluded(
                    comp, item.get("exclude"), profile):
                key = str(item.get("key"))
                counts[key] = counts.get(key, 0) + 1
        color = _component_color(comp, profile)
        if color in gem_colors:
            counts[color] = counts.get(color, 0) + 1
        elif color == gold_key:
            counts[gold_key] = counts.get(gold_key, 0) + 1
    return counts


def expected_inventory(profile: dict, nobles: int | None = None) -> dict[str, int]:
    inv = _profile_inventory(profile)
    expected: dict[str, int] = {}
    for item in inv.get("items") or []:
        if isinstance(item, dict) and item.get("key") is not None:
            expected[str(item["key"])] = int(item.get("expected", 0) or 0)
    gems = inv.get("gems") or {}
    per_color = int(gems.get("expected_per_color", 0) or 0)
    per_color_map = gems.get("expected") if isinstance(gems.get("expected"), dict) else {}
    for color in gems.get("colors") or []:
        color = str(color)
        expected[color] = int(per_color_map.get(color, per_color) or 0)
    gold = _profile_gold(profile)
    expected[_gold_key(profile)] = int(gold.get("expected", 0) or 0)

    if nobles is not None:
        noble_key = str(inv.get("nobles_key") or "noble")
        if noble_key in expected:
            expected[noble_key] = int(nobles)
    return expected


def format_inventory_counts(counts: dict[str, int], reference: dict[str, int], profile: dict) -> str:
    labels = _inventory_labels(profile)
    parts = []
    for key in _inventory_order(profile):
        if counts.get(key, 0) != reference.get(key, 0):
            parts.append(f"{labels.get(key, key)} {counts.get(key, 0)}/{reference.get(key, 0)}")
    return "、".join(parts) if parts else "无差异"


def format_inventory_delta(
    previous: dict[str, int],
    current: dict[str, int],
    reference: dict[str, int],
    profile: dict,
) -> str:
    """Describe only the count keys that changed in this cue."""
    labels = _inventory_labels(profile)
    parts = []
    for key in _inventory_order(profile):
        if current.get(key, 0) != previous.get(key, 0):
            parts.append(f"{labels.get(key, key)} {current.get(key, 0)}/{reference.get(key, 0)}")
    return "、".join(parts) if parts else "无差异"


def _finding(level: str, check: str, index: int, cue_id: str | None, message: str) -> dict:
    return {
        "level": level,
        "check": check,
        "index": int(index),
        "cue_id": cue_id,
        "message": message,
    }


def _sort_findings(findings: list[dict]) -> list[dict]:
    return sorted(
        findings,
        key=lambda item: (
            int(item.get("index", 10**9)),
            CHECK_ORDER.get(item.get("check", ""), 9),
            str(item.get("cue_id") or ""),
        ),
    )


# ---------------------------------------------------------------------------
# input selection


def resolve_start_index(
    cues: list[dict],
    from_cue: str | None,
    start_after_cue_prefix: str = "setup.",
) -> int:
    """Return the first compiled cue to audit.

    Without ``--from-cue`` the audit starts after the last cue whose id matches
    the game's configured setup prefix.  With ``--from-cue`` that cue becomes
    the first checkpoint.
    """
    if from_cue:
        for idx, cue in enumerate(cues):
            if cue.get("id") == from_cue:
                return idx
        raise ValueError(f"from-cue not found: {from_cue}")

    last_setup = -1
    for idx, cue in enumerate(cues):
        if str(cue.get("id") or "").startswith(start_after_cue_prefix):
            last_setup = idx
    return last_setup + 1


def resolve_worlds(compiled_doc: dict) -> list[str | None]:
    """Resolve the active world for every compiled cue in track order."""
    tree_world = {
        str(tree.get("id")): tree.get("world")
        for tree in (compiled_doc.get("trees") or [])
        if isinstance(tree, dict) and tree.get("id") is not None
    }
    worlds: list[str | None] = []
    active_tree: str | None = None
    for cue in compiled_doc.get("cues") or []:
        if cue.get("tree"):
            active_tree = str(cue.get("tree"))
        world = tree_world.get(active_tree) if active_tree else None
        worlds.append(world or active_tree)
    return worlds


# ---------------------------------------------------------------------------
# check 1: physical conservation


def build_state_graph(track_doc: dict, compiled_doc: dict) -> tuple[dict, dict, dict, dict]:
    """Resolve each cue's single state source and branch identity.

    Returns ``(resolved_track, graph, compiled_by_id, source_by_id)``.
    ``entry`` wins over ``parent`` and may cross tree/world.  ``parent`` is the
    default state source regardless of tree.  With neither entry nor parent,
    the previous cue in track order is the source (tree is only stage/scope).
    Explicit ``cut`` / ``world_cut`` resets to the empty initial snapshot.
    """
    track = schema.resolve_track(track_doc)
    source_cues = [
        c for c in (track.get("cues") or [])
        if isinstance(c, dict) and c.get("id")
    ]
    source_by_id = {str(c["id"]): c for c in source_cues}
    compiled_by_id = {
        str(c.get("id")): c
        for c in (compiled_doc.get("cues") or [])
        if isinstance(c, dict) and c.get("id")
    }

    graph: dict[str, dict] = {}
    branch_ids: dict[str, str] = {}
    default_tree = str(track.get("default_tree") or "main")

    for idx, cue in enumerate(source_cues):
        cid = str(cue.get("id"))
        parent = cue.get("parent")
        entry = cue.get("entry")
        prev_id = str(source_cues[idx - 1].get("id")) if idx > 0 else None
        source = logical_state.resolve_effective_state_source(
            cue,
            by_id=source_by_id,
            prev_id=prev_id,
            implicit_initial=(idx == 0),
        )
        source_kind = source["kind"]
        source_id = source["id"]
        source_label = source["label"]

        is_demo = bool(cue.get("demo"))
        source_cue = source_by_id.get(source_id or "")
        source_is_demo = bool((source_cue or {}).get("demo")) if source_kind == "cue" else False
        if (source_kind == "cue"
                and str(parent) == source_id
                and source_id in branch_ids
                and is_demo == source_is_demo):
            branch_id = branch_ids[source_id]
        else:
            branch_id = cid
        branch_ids[cid] = branch_id

        compiled = compiled_by_id.get(cid) or {}
        tree_id = str(compiled.get("tree") or cue.get("tree") or "")
        graph[cid] = {
            "cue_id": cid,
            "tree": tree_id,
            "canonical": tree_id == default_tree,
            "stage": compiled.get("stage") or cue.get("stage"),
            "parent": parent,
            "entry": entry,
            "state_source": source_label,
            "source_kind": source_kind,
            "source_id": source_id,
            "source_negative": bool((source_cue or {}).get("negative")),
            "is_demo": is_demo,
            "branch_id": branch_id,
        }

    return track, graph, compiled_by_id, source_by_id


def check_boundary(
    selected_cues: list[dict],
    selected_start_indices: list[int],
    graph: dict[str, dict],
    compiled_by_id: dict[str, dict],
    source_by_id: dict[str, dict],
    errors: list[dict],
    stats: dict,
) -> None:
    """Check every explicit inherited edge copies the source snapshot exactly."""
    for offset, cue in enumerate(selected_cues):
        index = selected_start_indices[offset]
        cid = str(cue.get("id") or "?")
        info = graph.get(cid) or {}
        if info.get("source_kind") != "cue":
            continue
        source_id = str(info.get("source_id"))
        source_compiled = compiled_by_id.get(source_id)
        if source_compiled is None:
            errors.append(_finding(
                "ERR", "boundary", index, cid,
                f"状态来源 {source_id!r} 没有编译快照，无法核对 entry 边界",
            ))
            continue
        source_decl = source_by_id.get(source_id) or {}
        source_key = "start_state" if source_decl.get("negative") else "end_state"
        expected = source_compiled.get(source_key)
        if expected is not None and cue.get("start_state") != expected:
            errors.append(_finding(
                "ERR", "boundary", index, cid,
                f"start_state != {info.get('state_source')} 的 {source_key}",
            ))
        src_info = graph.get(source_id) or {}
        if not info.get("is_demo") and src_info.get("is_demo"):
            errors.append(_finding(
                "ERR", "demo", index, cid,
                f"canonical cue 不能把 demo cue {source_id!r} 当作状态来源；"
                f"请显式 entry 回 canonical 分支",
            ))
        stats["boundary_edges"] += 1


def check_conservation(
    selected_cues: list[dict],
    selected_start_indices: list[int],
    graph: dict[str, dict],
    expected: dict[str, int],
    profile: dict,
    errors: list[dict],
    stats: dict,
) -> None:
    """Replay physical totals along canonical state-graph edges.

    Each canonical cue is compared against its own start snapshot.  Because the
    boundary check has already asserted ``start_state == source.end_state`` this
    is equivalent to comparing against the source end_state, but branches cannot
    borrow each other's deltas.  Demo branches are skipped here; their entry
    boundaries are still checked.
    """
    for offset, cue in enumerate(selected_cues):
        index = selected_start_indices[offset]
        cue_id = cue.get("id") or "?"
        info = graph.get(cue_id) or {}
        start_state = (cue.get("start_state") or {}).get("components") or []
        end_state = (cue.get("end_state") or {}).get("components") or []
        start_counts = count_inventory(start_state, profile)
        end_counts = count_inventory(end_state, profile)

        if not info.get("canonical"):
            stats["noncanonical_cues_skipped"] += 1
            continue

        if info.get("is_demo"):
            stats["demo_cues_skipped"] += 1
            continue

        if info.get("source_kind") == "cue":
            # Boundary equality makes this equivalent to comparing against the
            # source cue's end_state, without relying on track order.
            if end_counts != start_counts:
                changed = format_inventory_delta(start_counts, end_counts, expected, profile)
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"实物守恒变化: {changed}",
                ))
        else:
            baseline_bad = format_inventory_counts(start_counts, expected, profile)
            if baseline_bad != "无差异":
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"守恒基线: 起始状态 {baseline_bad}（期望总数）",
                ))
            if end_counts != start_counts:
                changed = format_inventory_delta(start_counts, end_counts, expected, profile)
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"实物守恒变化: {changed}",
                ))
        stats["conservation_checkpoints"] += 1


# ---------------------------------------------------------------------------
# check 2: market refill


def _refill_levels(profile: dict) -> list[int]:
    raw = _profile_refill(profile).get("levels") or [1, 2, 3]
    return [int(value) for value in raw]


def _level_patterns(profile: dict) -> list[re.Pattern]:
    raw = _profile_refill(profile).get("level_patterns") or []
    return [re.compile(str(pattern)) for pattern in raw]


def _level_from_match(match: re.Match) -> int | None:
    group = match.groupdict().get("level")
    if group is not None:
        return int(group)
    if match.groups():
        return int(match.group(1))
    return None


def _extract_level(text: str, patterns: list[re.Pattern]) -> int | None:
    for pattern in patterns:
        match = pattern.search(text)
        if match:
            level = _level_from_match(match)
            if level is not None:
                return level
    return None


def infer_event_development_level(event: dict, profile: dict) -> int | None:
    """Infer a development-card level from a v2 transfer event."""
    texts: list[str] = []
    for key in ("concept", "palette", "template", "source", "destination"):
        texts.extend(str(value) for value in as_list(event.get(key)))
    for part in event.get("parts") or []:
        if isinstance(part, dict):
            texts.extend(str(value) for value in part.values())
        else:
            texts.append(str(part))

    patterns = _level_patterns(profile)
    for text in texts:
        level = _extract_level(text, patterns)
        if level is not None:
            return level
    return None


def infer_source_deck_level(event: dict, profile: dict) -> int | None:
    pattern = _profile_refill(profile).get("source_deck_pattern")
    if pattern:
        compiled = re.compile(str(pattern))
        for source in as_list(event.get("source")):
            match = compiled.fullmatch(str(source))
            if match:
                level = _level_from_match(match)
                if level is not None:
                    return level
    return infer_event_development_level(event, profile)


def component_development_level(comp: dict, profile: dict) -> int | None:
    """Infer a development-card level from a compiled component."""
    patterns = _level_patterns(profile)
    for text in (comp.get("Concept"), comp.get("Palette"), comp.get("TemplateId")):
        level = _extract_level(str(text or ""), patterns)
        if level is not None:
            return level
    return None


def component_market_level(comp: dict, profile: dict) -> int | None:
    return component_development_level(comp, profile)


def market_counts(components: Iterable[dict] | None, profile: dict) -> dict[int, int]:
    levels = _refill_levels(profile)
    market_zone = str(_profile_refill(profile).get("market_zone") or "")
    counts: dict[int, int] = {level: 0 for level in levels}
    if not market_zone:
        return counts
    for comp in components or []:
        if not isinstance(comp, dict) or comp.get("ZoneId") != market_zone:
            continue
        level = component_market_level(comp, profile)
        if level in counts:
            counts[level] += 1
    return counts


def _empty_refill_pending(profile: dict) -> dict[int, list[tuple[int, str]]]:
    return {level: [] for level in _refill_levels(profile)}


def _copy_refill_pending(
    pending: dict[int, list[tuple[int, str]]],
) -> dict[int, list[tuple[int, str]]]:
    return {level: list(entries) for level, entries in pending.items()}


def check_refill(
    selected_cues: list[dict],
    selected_start_indices: list[int],
    final_market_components: Iterable[dict] | None,
    graph: dict[str, dict],
    source_by_id: dict[str, dict],
    index_by_id: dict[str, int],
    profile: dict,
    errors: list[dict],
    warnings: list[dict],
    stats: dict,
) -> None:
    """Refill audit along the state graph, branch-locally."""
    refill_cfg = _profile_refill(profile)
    levels = _refill_levels(profile)
    market_zone = str(refill_cfg.get("market_zone") or "")
    player_prefixes = tuple(str(prefix) for prefix in refill_cfg.get("player_zone_prefixes") or [])
    level_labels = refill_cfg.get("level_labels") if isinstance(refill_cfg.get("level_labels"), dict) else {}
    deck_label = str(refill_cfg.get("source_deck_label") or "{level}")
    if not market_zone:
        stats["refill_final_market"] = {}
        return

    memo: dict[str, dict[int, list[tuple[int, str]]]] = {}
    visiting: set[str] = set()

    def pending_for(cid: str) -> dict[int, list[tuple[int, str]]]:
        cid = str(cid)
        if cid in memo:
            return _copy_refill_pending(memo[cid])
        if cid in visiting:
            return _empty_refill_pending(profile)
        visiting.add(cid)
        info = graph.get(cid) or {}
        if info.get("source_kind") == "cue" and info.get("source_id"):
            pending = pending_for(str(info["source_id"]))
        else:
            pending = _empty_refill_pending(profile)

        cue = source_by_id.get(cid) or {}
        index = index_by_id.get(cid, -1)
        for event in cue.get("events") or []:
            if not isinstance(event, dict) or event.get("op") != "transfer":
                continue
            source = event.get("source")
            destination = event.get("destination")
            quantity = max(1, int(event.get("quantity") or 1))

            # Move out of the market into a player area -> expect a future
            # same-branch deck -> market refill.
            if source == market_zone and isinstance(destination, str) and destination.startswith(player_prefixes):
                level = infer_event_development_level(event, profile)
                if level in levels:
                    for _ in range(quantity):
                        pending[level].append((index, cid))
                else:
                    warnings.append(_finding(
                        "WARN", "refill", index, cid,
                        f"无法解析 {market_zone} -> {destination} 的发展卡等级，未纳入补牌审计",
                    ))

            # Refill event.  Only pending removals on this same branch can be
            # satisfied; sibling branches keep their own copies of the state.
            if isinstance(destination, str) and destination == market_zone:
                level = infer_source_deck_level(event, profile)
                if level in levels and pending[level]:
                    for _ in range(min(quantity, len(pending[level]))):
                        pending[level].pop(0)

        visiting.discard(cid)
        memo[cid] = _copy_refill_pending(pending)
        return _copy_refill_pending(pending)

    selected_ids = [str(cue.get("id")) for cue in selected_cues if cue.get("id")]
    selected_set = set(selected_ids)
    child_ids: set[str] = set()
    for cid in selected_ids:
        info = graph.get(cid) or {}
        source_id = info.get("source_id")
        if (not info.get("is_demo")
                and info.get("source_kind") == "cue"
                and source_id in selected_set):
            child_ids.add(str(source_id))
    leaf_ids = [
        cid for cid in selected_ids
        if cid not in child_ids and not (graph.get(cid) or {}).get("is_demo")
    ]

    final_market = market_counts(final_market_components, profile)
    stats["refill_final_market"] = {str(level): final_market.get(level, 0) for level in levels}
    for leaf_id in leaf_ids:
        pending = pending_for(leaf_id)
        for level in levels:
            for index, cue_id in pending[level]:
                stats["refill_missing"] += 1
                errors.append(_finding(
                    "ERR", "refill", index, cue_id,
                    f"缺{level_labels.get(str(level), level)}级补牌：从 {market_zone} 移出后没有在后续事件用 "
                    f"{deck_label.format(level=level)} -> {market_zone} 补回；最终 {market_zone} L{level} 数量="
                    f"{final_market.get(level, 0)}",
                ))


# ---------------------------------------------------------------------------
# check 3: pointer event / compilation-resolution reconciliation


def _source_pointer_events(source_cue: dict) -> list[tuple[int, dict]]:
    """Return ``(event_index, event)`` for every source pointer annotation."""
    out = []
    for event_index, event in enumerate(source_cue.get("events") or []):
        if isinstance(event, dict) and event.get("op") in ("point", "shape", "highlight"):
            out.append((event_index, event))
    return out


def check_pointer(
    selected_compiled_cues: list[dict],
    selected_start_indices: list[int],
    source_by_id: dict[str, dict],
    errors: list[dict],
    warnings: list[dict],
    stats: dict,
) -> None:
    for offset, compiled_cue in enumerate(selected_compiled_cues):
        index = selected_start_indices[offset]
        cue_id = compiled_cue.get("id") or "?"
        source_cue = source_by_id.get(str(cue_id))
        if source_cue is None:
            warnings.append(_finding(
                "WARN", "pointer", index, cue_id,
                "源 track 缺少同名 cue，无法校验 pointer 解析",
            ))
            continue

        source_pointers = _source_pointer_events(source_cue)
        stats["pointer_src_total"] += len(source_pointers)
        if not source_pointers:
            continue

        resolution_by_index: dict[int, dict] = {}
        for record in compiled_cue.get("pointer_resolution") or []:
            if not isinstance(record, dict):
                continue
            event_index = record.get("event_index")
            if isinstance(event_index, int):
                resolution_by_index[event_index] = record

        unresolved: list[dict] = []
        for event_index, event in source_pointers:
            op = event.get("op")
            record = resolution_by_index.get(event_index)
            item_ids = record.get("item_ids") if isinstance(record, dict) else None
            if not isinstance(item_ids, list):
                item_ids = []

            reason = None
            if record is None:
                reason = "缺少 resolution record"
            elif not item_ids:
                reason = "item_ids 为空"
            elif op in ("point", "shape") and len(item_ids) != 1:
                reason = f"{op} 需要 1 个 item/overlay，实际 {len(item_ids)} 个"
            elif op == "highlight" and len(item_ids) == 0:
                reason = "highlight item_ids 为空"

            if reason is not None:
                unresolved.append({
                    "event_index": event_index,
                    "op": op,
                    "zone": event.get("zone"),
                    "order": event.get("order"),
                    "anchor": event.get("anchor"),
                    "offset": event.get("offset"),
                    "reason": reason,
                })

        if not unresolved:
            continue

        stats["pointer_unresolved_events"] += len(unresolved)
        stats["pointer_unresolved_cues"] += 1
        detail = "；".join(
            f"event_index={item['event_index']} op={item['op']} zone={item['zone']} "
            f"order={item['order']} anchor={item['anchor']!r} offset={item['offset']!r} "
            f"({item['reason']})"
            for item in unresolved
        )
        errors.append(_finding(
            "ERR", "pointer", index, cue_id,
            f"pointer 解析未完成: {detail}",
        ))


# ---------------------------------------------------------------------------
# orchestration


def audit_documents(
    track_doc: dict,
    compiled_doc: dict,
    from_cue: str | None = None,
    nobles: int | None = None,
    game: str | None = None,
    track: str | None = None,
    profile: dict | None = None,
) -> dict:
    game = str(game or compiled_doc.get("game") or track_doc.get("game") or "")
    track = str(track or compiled_doc.get("track") or track_doc.get("track") or "")
    if profile is None:
        profile = load_audit_profile(game)

    cues = compiled_doc.get("cues") or []
    if not cues:
        raise ValueError("compiled track has no cues")
    start_after = str(profile.get("start_after_cue_prefix") or "setup.")
    start_index = resolve_start_index(cues, from_cue, start_after)
    if start_index >= len(cues):
        raise ValueError("no cue to audit after setup")

    selected_cues = cues[start_index:]
    selected_start_indices = list(range(start_index, len(cues)))
    index_by_id = {str(cue.get("id")): idx for idx, cue in enumerate(cues) if cue.get("id")}
    _, graph, compiled_by_id, source_by_id = build_state_graph(track_doc, compiled_doc)
    expected = expected_inventory(profile, nobles=nobles)

    errors: list[dict] = []
    warnings: list[dict] = []
    stats = {
        "game": game,
        "track": track,
        "from_cue": from_cue,
        "start_index": start_index,
        "cues_checked": len(selected_cues),
        "boundary_edges": 0,
        "demo_cues_skipped": 0,
        "noncanonical_cues_skipped": 0,
        "conservation_checkpoints": 0,
        "refill_missing": 0,
        "refill_final_market": {},
        "pointer_src_total": 0,
        "pointer_unresolved_events": 0,
        "pointer_unresolved_cues": 0,
    }

    # Keep check order stable in the JSON output; human output is sorted by cue.
    check_boundary(
        selected_cues,
        selected_start_indices,
        graph,
        compiled_by_id,
        source_by_id,
        errors,
        stats,
    )
    check_conservation(
        selected_cues,
        selected_start_indices,
        graph,
        expected,
        profile,
        errors,
        stats,
    )
    final_market_components = (
        selected_cues[-1].get("end_state") or {}
    ).get("components") or []
    check_refill(
        selected_cues,
        selected_start_indices,
        final_market_components,
        graph,
        source_by_id,
        index_by_id,
        profile,
        errors,
        warnings,
        stats,
    )
    check_pointer(selected_cues, selected_start_indices, source_by_id, errors, warnings, stats)

    state_graph = []
    for cue in selected_cues:
        cid = str(cue.get("id"))
        info = graph.get(cid) or {}
        state_graph.append({
            "cue_id": cid,
            "tree": info.get("tree"),
            "stage": info.get("stage") or cue.get("stage"),
            "parent": info.get("parent"),
            "entry": info.get("entry"),
            "state_source": info.get("state_source"),
            "source_id": info.get("source_id"),
            "is_demo": bool(info.get("is_demo")),
            "branch_id": info.get("branch_id"),
        })

    return {
        "game": game,
        "track": track,
        "from_cue": from_cue,
        "state_graph": state_graph,
        "errors": _sort_findings(errors),
        "warnings": _sort_findings(warnings),
        "stats": stats,
    }


# ---------------------------------------------------------------------------
# CLI


def _print_findings(result: dict) -> None:
    for item in result.get("errors") or []:
        print(f"ERR  {item.get('index')} {item.get('cue_id')} {item.get('message')}")
    for item in result.get("warnings") or []:
        print(f"WARN {item.get('index')} {item.get('cue_id')} {item.get('message')}")


def _discover_audit_games() -> list[str]:
    games = set()
    for path in (ROOT / "content" / "games").glob(f"*/{AUDIT_PROFILE_REL.as_posix()}"):
        try:
            games.add(path.relative_to(ROOT / "content" / "games").parts[0])
        except (OSError, ValueError):
            continue
    return sorted(games)


def _discover_audit_tracks(game: str) -> list[str]:
    v2 = ROOT / "content" / "games" / game / "tutorial" / "anim" / "v2"
    return sorted(
        path.name[: -len(".anim.json")]
        for path in v2.glob("*.anim.json")
        if not path.name.startswith("_")
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", default=None)
    parser.add_argument("--track", default=None)
    parser.add_argument("--profile", default=None, help="override audit-profile.json path")
    parser.add_argument("--from-cue", default=None)
    parser.add_argument("--nobles", type=int, default=None, help="override the profile's noble count")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

    if args.game is None:
        games = _discover_audit_games()
        if len(games) != 1:
            print(
                f"ERR  input: --game is required; candidate games: {', '.join(games) or '(none)'}",
                file=sys.stderr,
            )
            return 2
        args.game = games[0]
    if args.track is None:
        tracks = _discover_audit_tracks(args.game)
        if len(tracks) != 1:
            print(
                f"ERR  input: --track is required; candidate tracks: {', '.join(tracks) or '(none)'}",
                file=sys.stderr,
            )
            return 2
        args.track = tracks[0]

    base = ROOT / "content" / "games" / args.game / "tutorial" / "anim" / "v2"
    source_path = base / f"{args.track}.anim.json"
    compiled_path = base / f"{args.track}.compiled.json"
    try:
        track_doc = load_json(source_path)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"ERR  source input: {source_path}: {exc}", file=sys.stderr)
        return 2
    try:
        compiled_doc = load_json(compiled_path)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"ERR  compiled input: {compiled_path}: {exc}", file=sys.stderr)
        return 2

    try:
        profile = load_audit_profile(args.game, args.profile)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"ERR  audit profile: {exc}", file=sys.stderr)
        return 2

    try:
        result = audit_documents(
            track_doc,
            compiled_doc,
            from_cue=args.from_cue,
            nobles=args.nobles,
            game=args.game,
            track=args.track,
            profile=profile,
        )
    except ValueError as exc:
        print(f"ERR  input: {exc}", file=sys.stderr)
        return 2

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        _print_findings(result)
        if result["errors"]:
            print(
                f"FAIL audit v2: {len(result['errors'])} errors, "
                f"{len(result['warnings'])} warnings",
                file=sys.stderr,
            )
        else:
            print(
                f"OK   audit v2: {result['stats']['cues_checked']} cues checked, "
                f"0 errors, {len(result['warnings'])} warnings"
            )
    return 1 if result["errors"] else 0


if __name__ == "__main__":
    sys.exit(main())
