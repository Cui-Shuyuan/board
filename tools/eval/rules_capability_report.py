#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate a content/runtime capability checklist for every game.

This is a read-only deterministic report. It does not claim that a rule with a
description is programmatically computable; it only distinguishes available
structure from natural-language-only coverage.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

CONCEPT_ARRAY_TYPES = (
    "objects", "actions", "triggers", "conditions", "effects",
    "modules", "cards", "continent_tiles", "sites", "chips",
)
CONCEPT_REF = re.compile(r"<([^<>]+)>")
EXPLICIT_COLLECTION_FIELDS = ("members", "items", "options", "slots")
RELATION_FIELDS = ("specifies", "extends", "instance_of")


def load_json(path: Path):
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def collect_ids(root) -> list[tuple[str, str]]:
    out = []
    if isinstance(root, dict):
        for type_name in CONCEPT_ARRAY_TYPES:
            for item in root.get(type_name, []) or []:
                if isinstance(item, dict) and isinstance(item.get("id"), str):
                    out.append((item["id"], type_name))
    return out


def collect_refs(value, sink: Counter):
    for item in walk(value):
        if isinstance(item, str):
            for ref in CONCEPT_REF.findall(item):
                sink[ref] += 1
        elif isinstance(item, dict):
            for key in RELATION_FIELDS:
                rel = item.get(key)
                if isinstance(rel, str):
                    sink[rel.strip("<>")] += 1
    return sink


def walk_flow(value, state: dict, parent_id: str | None = None):
    if isinstance(value, dict):
        node_id = value.get("id")
        if isinstance(node_id, str) and node_id:
            state["nodes"] += 1
            state["local_ids"][node_id] += 1
            state["node_kinds"][str(value.get("specifies") or "flow_node")] += 1
            if "loop" in value:
                state["loops"] += 1
            if "condition" in value or "<ontology::condition>" in value:
                state["conditions"] += 1
            current_parent = node_id
        else:
            current_parent = parent_id
        for key, child in value.items():
            if key in ("id", "name", "description", "loop"):
                continue
            if isinstance(child, list) and any(isinstance(item, dict) and "id" in item for item in child):
                state["declared_arrays"] += 1
                if len(child) > 1:
                    state["branches"] += 1
            walk_flow(child, state, current_parent)
    elif isinstance(value, list):
        for child in value:
            walk_flow(child, state, parent_id)


def classify_game(game_dir: Path, ontology_ids: set[str]) -> dict:
    concepts = load_json(game_dir / "concepts.json")
    flow = load_json(game_dir / "flow.json")
    instances = load_json(game_dir / "instances.json")
    game_ids = collect_ids(concepts)
    instance_ids = collect_ids(instances)
    all_ids = [item[0] for item in game_ids + instance_ids]
    duplicate_ids = {key: count for key, count in Counter(all_ids).items() if count > 1}

    refs = Counter()
    collect_refs(concepts or {}, refs)
    collect_refs(instances or {}, refs)
    collect_refs(flow or {}, refs)
    resolvable = set(all_ids) | ontology_ids
    unresolved_refs = sorted(ref for ref in refs if ref.split("::")[-1] not in resolvable)

    flow_state = {
        "nodes": 0,
        "local_ids": Counter(),
        "node_kinds": Counter(),
        "declared_arrays": 0,
        "branches": 0,
        "loops": 0,
        "conditions": 0,
    }
    walk_flow(flow or {}, flow_state)
    duplicate_flow_ids = {
        key: count for key, count in flow_state["local_ids"].items() if count > 1
    }

    explicit_collections = 0
    type_relations = 0
    structured_conditions = 0
    structured_cost = 0
    structured_target = 0
    structured_content = 0
    natural_language_only = 0
    for item in (concepts or {}).get("objects", []) or []:
        if not isinstance(item, dict):
            continue
        if any(isinstance(item.get(field), list) for field in EXPLICIT_COLLECTION_FIELDS):
            explicit_collections += 1
        if any(field in item for field in RELATION_FIELDS):
            type_relations += 1
    for type_name in ("actions", "triggers"):
        for item in (concepts or {}).get(type_name, []) or []:
            if not isinstance(item, dict):
                continue
            has_condition = "condition" in item or "<ontology::condition>" in item
            has_cost = "cost" in item or "<ontology::cost>" in item
            has_target = "target" in item
            has_content = any("content" in key or "effect" in key for key in item)
            structured_conditions += int(has_condition)
            structured_cost += int(has_cost)
            structured_target += int(has_target)
            structured_content += int(has_content)
            if item.get("description") and not any((has_condition, has_cost, has_target, has_content)):
                natural_language_only += 1

    score_table = any(isinstance(item, dict) and "score_table" in item for item in walk(flow or {}))

    gaps = []
    if not flow:
        gaps.append("missing_flow_file")
    elif flow_state["nodes"] == 0:
        gaps.append("flow_has_no_identified_nodes")
    if duplicate_flow_ids:
        gaps.append("duplicate_flow_local_ids_require_canonical_paths")
    if unresolved_refs:
        gaps.append("unresolved_concept_references")
    if explicit_collections == 0 and type_relations == 0:
        gaps.append("no_provable_member_relation")
    if structured_cost == 0:
        gaps.append("no_structured_cost_fields")
    if structured_conditions == 0:
        gaps.append("no_structured_condition_fields")
    if not score_table:
        gaps.append("no_score_table")
    if natural_language_only:
        gaps.append("some_rules_are_natural_language_only")

    return {
        "game": game_dir.name,
        "concepts": {
            "declared_ids": len(game_ids),
            "instance_ids": len(instance_ids),
            "duplicate_ids": duplicate_ids,
            "unresolved_references": unresolved_refs[:20],
            "unresolved_reference_count": len(unresolved_refs),
        },
        "flow": {
            "present": bool(flow),
            "identified_nodes": flow_state["nodes"],
            "duplicate_local_ids": duplicate_flow_ids,
            "declared_arrays": flow_state["declared_arrays"],
            "branches": flow_state["branches"],
            "loops": flow_state["loops"],
            "conditions_on_nodes": flow_state["conditions"],
        },
        "members": {
            "explicit_collection_owners": explicit_collections,
            "schema_type_relations": type_relations,
            "provable_membership_basis": explicit_collections + type_relations > 0,
        },
        "rules": {
            "condition_fields": structured_conditions,
            "cost_fields": structured_cost,
            "target_fields": structured_target,
            "content_or_effect_fields": structured_content,
            "natural_language_only_nodes": natural_language_only,
            "score_table_present": score_table,
        },
        "capabilities": {
            "canonical_paths": not duplicate_ids,
            "scoped_flow": bool(flow) and flow_state["nodes"] > 0,
            "member_enumeration": explicit_collections + type_relations > 0,
            "condition_cost_target_reads": structured_conditions + structured_cost + structured_target > 0,
            "deterministic_scoring": score_table,
        },
        "actionable_gaps": gaps,
    }


def build_report(content_root: Path) -> dict:
    ontology = load_json(content_root / "ontology" / "concepts.json") or {}
    ontology_ids = {
        item["id"] for item in ontology.get("concepts", []) or []
        if isinstance(item, dict) and isinstance(item.get("id"), str)
    }
    games_dir = content_root / "games"
    games = []
    if games_dir.exists():
        for game_dir in sorted(path for path in games_dir.iterdir() if path.is_dir()):
            games.append(classify_game(game_dir, ontology_ids))
    return {
        "ontology_concept_count": len(ontology_ids),
        "game_count": len(games),
        "games": games,
        "summary": {
            "games_with_scoped_flow": sum(1 for game in games if game["capabilities"]["scoped_flow"]),
            "games_with_member_basis": sum(1 for game in games if game["capabilities"]["member_enumeration"]),
            "games_with_condition_cost_target": sum(1 for game in games if game["capabilities"]["condition_cost_target_reads"]),
            "games_with_score_table": sum(1 for game in games if game["capabilities"]["deterministic_scoring"]),
            "games_with_unresolved_refs": sum(1 for game in games if game["concepts"]["unresolved_reference_count"]),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--content-root", type=Path, default=Path("content"))
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    report = build_report(args.content_root)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
