#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic audit for the v2 Splendor tutorial animation.

This script deliberately works on the compiled snapshots and the v2 source
events without changing any animation data.  It covers the three cross-cue
blind spots of the existing checkers:

1. global physical conservation of development cards, nobles, gems and gold;
2. refill of the card market after a purchase/reserve removes a market card;
3. source ``point`` / ``highlight`` events that never become compiled pointer
   clips.

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
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent

sys.path.insert(0, str(ROOT / "animation"))
import anim_schema_v2 as schema  # noqa: E402

GEM_COLORS = ("diamond", "onyx", "emerald", "ruby", "sapphire")
INVENTORY_ORDER = (
    "L1",
    "L2",
    "L3",
    "noble",
    "diamond",
    "onyx",
    "emerald",
    "ruby",
    "sapphire",
    "gold",
)
INVENTORY_LABELS = {
    "L1": "L1",
    "L2": "L2",
    "L3": "L3",
    "noble": "贵族",
    "diamond": "宝石 diamond",
    "onyx": "宝石 onyx",
    "emerald": "宝石 emerald",
    "ruby": "宝石 ruby",
    "sapphire": "宝石 sapphire",
    "gold": "黄金",
}
CHECK_ORDER = {"boundary": 0, "demo": 1, "conservation": 2, "refill": 3, "pointer": 4}
LEVEL_CN = {1: "一", 2: "二", 3: "三"}


# ---------------------------------------------------------------------------
# generic helpers


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def as_list(value: Any) -> list:
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def _strip_angle(value: Any) -> str:
    return str(value or "").strip().strip("<>").strip()


def _component_color(comp: dict) -> str | None:
    """Return a gem/gold color from ``parts[color]`` or ``Palette``."""
    for part in comp.get("parts") or []:
        if isinstance(part, dict) and part.get("key") == "color":
            return _strip_angle(part.get("value"))
    palette = str(comp.get("Palette") or "")
    if palette == "gem_gold":
        return "gold"
    if palette.startswith("gem_"):
        return palette[4:]
    if str(comp.get("Concept") or "") == "gold":
        return "gold"
    return None


def _component_development_level(comp: dict) -> int | None:
    """Infer a development-card level from a compiled component."""
    concept = str(comp.get("Concept") or "")
    match = re.search(r"development_card_level_(\d+)", concept)
    if match:
        return int(match.group(1))
    palette = str(comp.get("Palette") or "")
    match = re.search(r"(?:^|_)card_level_(\d+)$", palette)
    if match:
        return int(match.group(1))
    template = str(comp.get("TemplateId") or "")
    match = re.search(r"(?:^|_)(?:market_card|blank_card)_(\d+)", template)
    if match:
        return int(match.group(1))
    return None


def count_inventory(components: Iterable[dict] | None) -> dict[str, int]:
    """Count the physical pieces governed by the conservation check.

    Development cards are counted by level.  Sample/showcase teaching props are
    excluded, while ``blank_card_*`` placeholders remain counted because they
    are part of the 40/30/20 deck complement.
    """
    counts = {key: 0 for key in INVENTORY_ORDER}
    for comp in components or []:
        if not isinstance(comp, dict):
            continue
        concept = str(comp.get("Concept") or "")
        if concept.startswith("development_card_level_"):
            template = str(comp.get("TemplateId") or "")
            zone = str(comp.get("ZoneId") or "")
            if template.startswith("sample") or zone.startswith("showcase"):
                pass
            else:
                level = _component_development_level(comp)
                if level in (1, 2, 3):
                    counts[f"L{level}"] += 1
        if concept == "noble":
            counts["noble"] += 1

        color = _component_color(comp)
        if color in GEM_COLORS:
            counts[color] += 1
        elif color == "gold":
            counts["gold"] += 1
    return counts


def expected_inventory(nobles: int) -> dict[str, int]:
    expected = {
        "L1": 40,
        "L2": 30,
        "L3": 20,
        "noble": int(nobles),
        "gold": 5,
    }
    for color in GEM_COLORS:
        expected[color] = 4
    return expected


def format_inventory_counts(counts: dict[str, int], reference: dict[str, int]) -> str:
    parts = []
    for key in INVENTORY_ORDER:
        if counts.get(key, 0) != reference.get(key, 0):
            parts.append(f"{INVENTORY_LABELS[key]} {counts.get(key, 0)}/{reference.get(key, 0)}")
    return "、".join(parts) if parts else "无差异"


def format_inventory_delta(
    previous: dict[str, int],
    current: dict[str, int],
    reference: dict[str, int],
) -> str:
    """Describe only the count keys that changed in this cue.

    This keeps the report focused on the first point where a total drifts and
    on later changes, instead of repeating every accumulating error at every
    subsequent cue.
    """
    parts = []
    for key in INVENTORY_ORDER:
        if current.get(key, 0) != previous.get(key, 0):
            parts.append(f"{INVENTORY_LABELS[key]} {current.get(key, 0)}/{reference.get(key, 0)}")
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


def resolve_start_index(cues: list[dict], from_cue: str | None) -> int:
    """Return the first compiled cue to audit.

    Without ``--from-cue`` the audit starts after the last cue whose id begins
    with ``setup.`` -- i.e. from the first post-setup state.  With
    ``--from-cue`` that cue becomes the first checkpoint.
    """
    if from_cue:
        for idx, cue in enumerate(cues):
            if cue.get("id") == from_cue:
                return idx
        raise ValueError(f"from-cue not found: {from_cue}")

    last_setup = -1
    for idx, cue in enumerate(cues):
        if str(cue.get("id") or "").startswith("setup."):
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
    ``entry`` wins over ``parent`` and may cross tree/world.  ``parent`` is only
    a same-tree default source.  ``cut`` / ``world_cut`` without explicit entry
    reset to the empty initial snapshot.  Track order is never used.
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
        entry = cue.get("entry")
        parent = cue.get("parent")
        transition = cue.get("transition", "continue")
        if entry:
            if str(entry) == "initial":
                source_kind = "initial"
                source_id = None
                source_label = "entry:initial"
            else:
                source_kind = "cue"
                source_id = str(entry)
                source_label = f"entry:{entry}"
        elif parent and str(parent) in source_by_id:
            source_kind = "cue"
            source_id = str(parent)
            source_label = f"parent:{parent}"
        elif transition in ("cut", "world_cut"):
            source_kind = "initial"
            source_id = None
            source_label = "cut:initial"
        elif idx == 0:
            # Legacy/synthetic single-cue documents are treated as an explicit
            # initial root.  Real tracks must declare entry/parent.
            source_kind = "initial"
            source_id = None
            source_label = "legacy:initial"
        else:
            source_kind = "none"
            source_id = None
            source_label = "none"

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
    errors: list[dict],
    stats: dict,
) -> None:
    """Replay physical totals along canonical state-graph edges.

    Each canonical cue is compared against its own start snapshot.  Because the
    boundary check has already asserted ``start_state == source.end_state`` this
    is the graph equivalent of the old previous-end comparison, but branches
    cannot borrow each other's deltas.  Demo branches are skipped here; their
    entry boundaries are still checked.
    """
    for offset, cue in enumerate(selected_cues):
        index = selected_start_indices[offset]
        cue_id = cue.get("id") or "?"
        info = graph.get(cue_id) or {}
        start_state = (cue.get("start_state") or {}).get("components") or []
        end_state = (cue.get("end_state") or {}).get("components") or []
        start_counts = count_inventory(start_state)
        end_counts = count_inventory(end_state)

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
                changed = format_inventory_delta(start_counts, end_counts, expected)
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"实物守恒变化: {changed}",
                ))
        else:
            baseline_bad = format_inventory_counts(start_counts, expected)
            if baseline_bad != "无差异":
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"守恒基线: 起始状态 {baseline_bad}（期望总数）",
                ))
            if end_counts != start_counts:
                changed = format_inventory_delta(start_counts, end_counts, expected)
                errors.append(_finding(
                    "ERR", "conservation", index, cue_id,
                    f"实物守恒变化: {changed}",
                ))
        stats["conservation_checkpoints"] += 1


# ---------------------------------------------------------------------------
# check 2: card_market refill


def infer_event_development_level(event: dict) -> int | None:
    """Infer level 1/2/3 from a v2 transfer event."""
    texts: list[str] = []
    for key in ("concept", "palette", "template", "source", "destination"):
        texts.extend(str(value) for value in as_list(event.get(key)))
    for part in event.get("parts") or []:
        if isinstance(part, dict):
            texts.extend(str(value) for value in part.values())
        else:
            texts.append(str(part))

    for text in texts:
        match = re.search(r"development_card_level_(\d+)", text)
        if match:
            return int(match.group(1))
        match = re.search(r"(?:card_level_|deck_level_|market_card_|blank_card_)(\d+)", text)
        if match:
            return int(match.group(1))
    return None


def infer_source_deck_level(event: dict) -> int | None:
    for source in as_list(event.get("source")):
        match = re.fullmatch(r"deck_level_(\d+)", str(source))
        if match:
            return int(match.group(1))
    return infer_event_development_level(event)


def component_market_level(comp: dict) -> int | None:
    return _component_development_level(comp)


def card_market_counts(components: Iterable[dict] | None) -> dict[int, int]:
    counts: dict[int, int] = defaultdict(int)
    for comp in components or []:
        if not isinstance(comp, dict) or comp.get("ZoneId") != "card_market":
            continue
        level = component_market_level(comp)
        if level in (1, 2, 3):
            counts[level] += 1
    return dict(counts)


def _empty_refill_pending() -> dict[int, list[tuple[int, str]]]:
    return {1: [], 2: [], 3: []}


def _copy_refill_pending(pending: dict[int, list[tuple[int, str]]]) -> dict[int, list[tuple[int, str]]]:
    return {level: list(entries) for level, entries in pending.items()}


def check_refill(
    selected_cues: list[dict],
    selected_start_indices: list[int],
    final_market_components: Iterable[dict] | None,
    graph: dict[str, dict],
    source_by_id: dict[str, dict],
    index_by_id: dict[str, int],
    errors: list[dict],
    warnings: list[dict],
    stats: dict,
) -> None:
    """Refill audit along the state graph, branch-locally."""
    memo: dict[str, dict[int, list[tuple[int, str]]]] = {}
    visiting: set[str] = set()

    def pending_for(cid: str) -> dict[int, list[tuple[int, str]]]:
        cid = str(cid)
        if cid in memo:
            return _copy_refill_pending(memo[cid])
        if cid in visiting:
            return _empty_refill_pending()
        visiting.add(cid)
        info = graph.get(cid) or {}
        if info.get("source_kind") == "cue" and info.get("source_id"):
            pending = pending_for(str(info["source_id"]))
        else:
            pending = _empty_refill_pending()

        cue = source_by_id.get(cid) or {}
        index = index_by_id.get(cid, -1)
        for event in cue.get("events") or []:
            if not isinstance(event, dict) or event.get("op") != "transfer":
                continue
            source = event.get("source")
            destination = event.get("destination")
            quantity = max(1, int(event.get("quantity") or 1))

            # Move out of the market into a player area -> expect a future
            # same-branch deck_level_N -> card_market refill.
            if source == "card_market" and isinstance(destination, str) and destination.startswith("player_"):
                level = infer_event_development_level(event)
                if level in (1, 2, 3):
                    for _ in range(quantity):
                        pending[level].append((index, cid))
                else:
                    warnings.append(_finding(
                        "WARN", "refill", index, cid,
                        f"无法解析 card_market -> {destination} 的发展卡等级，未纳入补牌审计",
                    ))

            # Refill event.  Only pending removals on this same branch can be
            # satisfied; sibling branches keep their own copies of the state.
            if isinstance(destination, str) and destination == "card_market":
                level = infer_source_deck_level(event)
                if level in (1, 2, 3) and pending[level]:
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

    final_market = card_market_counts(final_market_components)
    stats["refill_final_market"] = {str(level): final_market.get(level, 0) for level in (1, 2, 3)}
    for leaf_id in leaf_ids:
        pending = pending_for(leaf_id)
        for level in (1, 2, 3):
            for index, cue_id in pending[level]:
                stats["refill_missing"] += 1
                errors.append(_finding(
                    "ERR", "refill", index, cue_id,
                    f"缺{LEVEL_CN[level]}级补牌：从 card_market 移出后没有在后续事件用 "
                    f"deck_level_{level} -> card_market 补回；最终 card_market L{level} 数量="
                    f"{final_market.get(level, 0)}",
                ))


# ---------------------------------------------------------------------------
# check 3: pointer event / compilation-resolution reconciliation


def _source_pointer_events(source_cue: dict) -> list[tuple[int, dict]]:
    """Return ``(event_index, event)`` for every source point/highlight."""
    out = []
    for event_index, event in enumerate(source_cue.get("events") or []):
        if isinstance(event, dict) and event.get("op") in ("point", "highlight"):
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
                "源 track 缺少同名 cue，无法校验 point/highlight 解析",
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
            elif op == "point" and len(item_ids) != 1:
                reason = f"point 需要 1 个 item，实际 {len(item_ids)} 个"
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
    nobles: int = 3,
    game: str = "splendor",
    track: str = "full",
) -> dict:
    cues = compiled_doc.get("cues") or []
    if not cues:
        raise ValueError("compiled track has no cues")
    start_index = resolve_start_index(cues, from_cue)
    if start_index >= len(cues):
        raise ValueError("no cue to audit after setup")

    selected_cues = cues[start_index:]
    selected_start_indices = list(range(start_index, len(cues)))
    index_by_id = {str(cue.get("id")): idx for idx, cue in enumerate(cues) if cue.get("id")}
    _, graph, compiled_by_id, source_by_id = build_state_graph(track_doc, compiled_doc)

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
        expected_inventory(nobles),
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--from-cue", default=None)
    parser.add_argument("--nobles", type=int, default=3)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)

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
        result = audit_documents(
            track_doc,
            compiled_doc,
            from_cue=args.from_cue,
            nobles=args.nobles,
            game=args.game,
            track=args.track,
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
