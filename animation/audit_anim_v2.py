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
CHECK_ORDER = {"conservation": 0, "refill": 1, "pointer": 2}
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


def check_conservation(
    selected_cues: list[dict],
    selected_worlds: list[str | None],
    selected_start_indices: list[int],
    expected: dict[str, int],
    errors: list[dict],
    stats: dict,
) -> None:
    previous_counts: dict[str, int] | None = None
    previous_world: str | None | object = object()

    for offset, cue in enumerate(selected_cues):
        index = selected_start_indices[offset]
        cue_id = cue.get("id") or "?"
        world = selected_worlds[offset]
        start_state = (cue.get("start_state") or {}).get("components") or []
        end_state = (cue.get("end_state") or {}).get("components") or []
        start_counts = count_inventory(start_state)
        end_counts = count_inventory(end_state)

        if previous_counts is None:
            # First selected checkpoint: use start_state as the baseline, as
            # required by --from-cue.  Still make the baseline itself loud if it
            # already violates physical totals.
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
            previous_counts = end_counts
            previous_world = world
            stats["conservation_checkpoints"] += 1
            continue

        # Re-establish the baseline across a world cut / tree-world switch and
        # skip the cross-world comparison itself.
        if world != previous_world or cue.get("transition") == "world_cut":
            previous_counts = end_counts
            previous_world = world
            continue

        if end_counts != previous_counts:
            changed = format_inventory_delta(previous_counts, end_counts, expected)
            errors.append(_finding(
                "ERR", "conservation", index, cue_id,
                f"实物守恒变化: {changed}",
            ))
        previous_counts = end_counts
        previous_world = world
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


def check_refill(
    selected_cues: list[dict],
    selected_start_indices: list[int],
    final_market_components: Iterable[dict] | None,
    errors: list[dict],
    warnings: list[dict],
    stats: dict,
) -> None:
    pending: dict[int, list[tuple[int, str]]] = {1: [], 2: [], 3: []}

    for offset, cue in enumerate(selected_cues):
        index = selected_start_indices[offset]
        cue_id = cue.get("id") or "?"
        for event in cue.get("events") or []:
            if not isinstance(event, dict) or event.get("op") != "transfer":
                continue
            source = event.get("source")
            destination = event.get("destination")
            quantity = max(1, int(event.get("quantity") or 1))

            # Move out of the market into a player area -> expect a future
            # deck_level_N -> card_market refill.
            if source == "card_market" and isinstance(destination, str) and destination.startswith("player_"):
                level = infer_event_development_level(event)
                if level in (1, 2, 3):
                    for _ in range(quantity):
                        pending[level].append((index, str(cue_id)))
                else:
                    warnings.append(_finding(
                        "WARN", "refill", index, cue_id,
                        f"无法解析 card_market -> {destination} 的发展卡等级，未纳入补牌审计",
                    ))

            # Refill event.  If there is no pending entry it may be the initial
            # setup deal (deck -> market) or an over-refill; only pending
            # removals are audited here.
            if isinstance(destination, str) and destination == "card_market":
                level = infer_source_deck_level(event)
                if level in (1, 2, 3) and pending[level]:
                    for _ in range(min(quantity, len(pending[level]))):
                        pending[level].pop(0)

    final_market = card_market_counts(final_market_components)
    stats["refill_final_market"] = {str(level): final_market.get(level, 0) for level in (1, 2, 3)}
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
# check 3: pointer event / clip reconciliation


def _compiled_pointer_ids(compiled_cue: dict) -> list[str]:
    item_ids = []
    for clip in compiled_cue.get("clips") or []:
        if not isinstance(clip, dict):
            continue
        if clip.get("kind") in ("point", "highlight"):
            item_ids.append(str(clip.get("item_id") or ""))
    return item_ids


def _source_pointer_events(source_cue: dict) -> list[dict]:
    return [
        event for event in (source_cue.get("events") or [])
        if isinstance(event, dict) and event.get("op") in ("point", "highlight")
    ]


def _zone_item_count(compiled_cue: dict, zone: str | None) -> int:
    if not zone:
        return 0
    state = compiled_cue.get("end_state") or {}
    return sum(1 for comp in state.get("components") or []
               if isinstance(comp, dict) and comp.get("ZoneId") == zone)


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
        compiled_pointers = _compiled_pointer_ids(compiled_cue)
        src_n = len(source_pointers)
        comp_n = len(compiled_pointers)
        stats["pointer_src_total"] += src_n
        stats["pointer_comp_total"] += comp_n

        if comp_n >= src_n:
            continue

        item_ids = compiled_pointers
        source_zones = [event.get("zone") for event in source_pointers if event.get("zone")]
        same_source_zone = (
            len(source_zones) == len(source_pointers)
            and len(set(source_zones)) == 1
        )
        zone = str(source_zones[0]) if same_source_zone else None
        zone_items = _zone_item_count(compiled_cue, zone)

        # A repeated pointer to the same single physical item is redundant
        # source data, not a silently dropped target.  Only downgrade when the
        # compiled cue explicitly resolves that one item and the source events
        # all point at the same one-item zone.
        duplicate_same_item = (
            comp_n == 1
            and len(set(item_ids)) == 1
            and same_source_zone
            and zone_items == 1
        )
        if duplicate_same_item:
            stats["pointer_duplicate_warnings"] += 1
            warnings.append(_finding(
                "WARN", "pointer", index, cue_id,
                f"pointer 解析降级 WARN: src={src_n} comp={comp_n}, 编译后唯一 "
                f"item_id={item_ids}; 源事件均指向 {zone} 且该 zone 在 compiled "
                f"end_state 中仅 1 件实物，判定为重复指向同一件，按 WARN 处理",
            ))
            continue

        stats["pointer_deficit_cues"] += 1
        errors.append(_finding(
            "ERR", "pointer", index, cue_id,
            f"pointer 解析: src={src_n} comp={comp_n}, 编译后 pointer item_id={item_ids}",
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
    worlds = resolve_worlds(compiled_doc)
    selected_worlds = worlds[start_index:]

    source_by_id = {
        str(cue.get("id")): cue
        for cue in (track_doc.get("cues") or [])
        if isinstance(cue, dict) and cue.get("id")
    }
    selected_source_cues = [source_by_id.get(str(cue.get("id"))) or {} for cue in selected_cues]

    errors: list[dict] = []
    warnings: list[dict] = []
    stats = {
        "game": game,
        "track": track,
        "from_cue": from_cue,
        "start_index": start_index,
        "cues_checked": len(selected_cues),
        "conservation_checkpoints": 0,
        "refill_missing": 0,
        "refill_final_market": {},
        "pointer_src_total": 0,
        "pointer_comp_total": 0,
        "pointer_deficit_cues": 0,
        "pointer_duplicate_warnings": 0,
    }

    # Keep check order stable in the JSON output; human output is sorted by cue.
    check_conservation(
        selected_cues,
        selected_worlds,
        selected_start_indices,
        expected_inventory(nobles),
        errors,
        stats,
    )
    final_market_components = (
        selected_cues[-1].get("end_state") or {}
    ).get("components") or []
    check_refill(
        selected_source_cues,
        selected_start_indices,
        final_market_components,
        errors,
        warnings,
        stats,
    )
    check_pointer(selected_cues, selected_start_indices, source_by_id, errors, warnings, stats)

    return {
        "game": game,
        "track": track,
        "from_cue": from_cue,
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
