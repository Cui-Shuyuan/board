#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Combine blind judge outputs conservatively.

The consensus ``pass`` is true only when every judge passes that run. Score
fields are averaged; boolean violation flags are OR-ed. This avoids allowing
one lenient judge to mark an unsupported answer as successful.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path


def load_judges(paths: list[Path]) -> list[dict[str, dict]]:
    judges = []
    for path in paths:
        rows = {}
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                item = json.loads(line)
                rows[item["run_id"]] = item
        judges.append(rows)
    return judges


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--judge", type=Path, action="append", required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--summary", type=Path, required=True)
    args = parser.parse_args()

    judges = load_judges(args.judge)
    ids = set.intersection(*(set(j.keys()) for j in judges))
    out_rows = []
    full_agreement = 0
    for run_id in sorted(ids):
        items = [j[run_id] for j in judges]
        versions = {item.get("version") for item in items}
        questions = {item.get("question_id") for item in items}
        if len(versions) != 1 or len(questions) != 1:
            raise ValueError(f"judge metadata mismatch for {run_id}")
        passes = [bool(item.get("pass")) for item in items]
        if len(set(passes)) == 1:
            full_agreement += 1
        out_rows.append({
            "run_id": run_id,
            "version": items[0]["version"],
            "question_id": items[0]["question_id"],
            "judge_pass_vector": passes,
            "judge_models": [item.get("judge_model") for item in items],
            "pass": all(passes),
            "answer_directness": round(sum(item.get("answer_directness", 0) for item in items) / len(items), 3),
            "rule_support": round(sum(item.get("rule_support", 0) for item in items) / len(items), 3),
            "completeness": round(sum(item.get("completeness", 0) for item in items) / len(items), 3),
            "unsupported_or_contradictory": any(bool(item.get("unsupported_or_contradictory")) for item in items),
            "unnecessary_clarification": any(bool(item.get("unnecessary_clarification")) for item in items),
            "irrelevant_expansion": any(bool(item.get("irrelevant_expansion")) for item in items),
            "player_count_inference": any(bool(item.get("player_count_inference")) for item in items),
            "reason": "consensus",
        })

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8") as f:
        for row in out_rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

    summary = {
        "judge_files": [str(path) for path in args.judge],
        "run_count": len(out_rows),
        "run_level_full_agreement": round(full_agreement / len(out_rows), 4) if out_rows else 0,
        "by_version": {},
    }
    for version in ("old", "new"):
        rows = [row for row in out_rows if row["version"] == version]
        summary["by_version"][version] = {
            "n": len(rows),
            "consensus_pass_rate": round(sum(row["pass"] for row in rows) / len(rows), 4) if rows else 0,
            "individual_judge_pass_rates": [None] * len(judges),
        }
    # Fill individual rates without relying on fragile transposition expressions.
    for index in range(len(judges)):
        for version in ("old", "new"):
            rows = [row for row in out_rows if row["version"] == version]
            rate = sum(row["judge_pass_vector"][index] for row in rows) / len(rows) if rows else 0
            summary["by_version"][version]["individual_judge_pass_rates"][index] = round(rate, 4)
    summary["consensus_pass_counts"] = dict(Counter(row["version"] for row in out_rows if row["pass"]))
    args.summary.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
