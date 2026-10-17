#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Merge regression questions and a self-contained holdout question bank."""
from __future__ import annotations

import argparse
import json
from pathlib import Path


def read_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--main-questions", type=Path, required=True)
    parser.add_argument("--main-ground-truth", type=Path, required=True)
    parser.add_argument("--holdout-questions", type=Path, required=True)
    parser.add_argument("--out-questions", type=Path, required=True)
    parser.add_argument("--out-ground-truth", type=Path, required=True)
    args = parser.parse_args()

    main_questions = read_jsonl(args.main_questions)
    holdout_questions = read_jsonl(args.holdout_questions)
    main_truth = json.loads(args.main_ground_truth.read_text(encoding="utf-8"))
    all_questions = main_questions + holdout_questions
    seen = set()
    for question in all_questions:
        if question["id"] in seen:
            raise ValueError(f"duplicate question id: {question['id']}")
        seen.add(question["id"])

    combined_truth = {qid: value for qid, value in main_truth.items()}
    for question in holdout_questions:
        combined_truth[question["id"]] = {
            **question,
            "acceptable_rule_evidence": question.get("acceptable_rule_evidence", []),
        }

    args.out_questions.parent.mkdir(parents=True, exist_ok=True)
    args.out_ground_truth.parent.mkdir(parents=True, exist_ok=True)
    args.out_questions.write_text(
        "".join(json.dumps(q, ensure_ascii=False) + "\n" for q in all_questions),
        encoding="utf-8",
    )
    args.out_ground_truth.write_text(
        json.dumps(combined_truth, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(f"questions={len(all_questions)} ground_truth={len(combined_truth)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
