#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Freeze the diagnostic development set for the evidence-repair round.

The selection is intentionally based on already observed results. It is a
development/diagnostic set, not an untouched holdout. Each row keeps its
original question, game, category and expected rule evidence, plus an explicit
selection reason and historical status marker.

Usage:
  python3 tools/eval/build_repair_dev_set.py \
    --out tools/qa/program_evidence_repair_dev_questions.jsonl
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

SELECTION = {
    "unavailable_seen_positive": [
        "gold-013", "gold-017", "gold-036", "gold-039", "gold-044",
        "gold-050", "gold-052", "gold-054", "gold-060", "gold-064",
        "gold-066", "intent-01-intent-splendor-original",
        "intent-02-intent-splendor-oral-variant",
        "intent-09-intent-agricola-deal-occupations",
        "intent-12-intent-wingspan-play-bird",
        "regression-02", "regression-04",
        "holdout-agricola-multi-occupation", "holdout-brass-develop",
    ],
    "unavailable_seen_negative_or_ambiguous": [
        "negative-cross-001", "negative-cross-003", "negative-natural-050",
    ],
    "known_correct_control": [
        "gold-026", "gold-035", "gold-004", "gold-038", "gold-082",
        "gold-081", "gold-014",
    ],
    "slot_regression": [
        "regression-05", "holdout-civ-slot-point-bonus",
    ],
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=Path("tools/qa/comprehensive_eval_questions.jsonl"))
    parser.add_argument("--slot-source", type=Path, default=Path("tools/qa/program_orchestration_holdout.jsonl"))
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    sources = {}
    for path in (args.base, args.slot_source):
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                row = json.loads(line)
                sources[row["id"]] = (path, row)

    selected = []
    for reason, ids in SELECTION.items():
        for question_id in ids:
            if question_id not in sources:
                raise SystemExit(f"missing selected question: {question_id}")
            source_path, row = sources[question_id]
            item = dict(row)
            item["split"] = "evidence-repair-dev"
            item["dev_selection"] = {
                "reason": reason,
                "source_file": str(source_path),
                "not_holdout": True,
                "note": "Selected from previously observed results for diagnosis and bounded comparison.",
            }
            selected.append(item)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    payload = "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in selected)
    args.out.write_text(payload, encoding="utf-8")
    digest = hashlib.sha256(payload.encode("utf-8")).hexdigest()
    print(json.dumps({
        "out": str(args.out),
        "questions": len(selected),
        "games": sorted({row["game"] for row in selected}),
        "sha256": digest,
        "selection": {key: len(value) for key, value in SELECTION.items()},
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
