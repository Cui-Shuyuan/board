#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Evaluate query-intent regressions against execute-plan.

Each JSONL row:
  {"id","game","question","queries":[
    {"relation","entity","intent","expected":[concept_id,...],
     "max_rank":3,"note":"..."}
  ],"note":"..."}

The evaluator sends one plan containing all sub-queries so multi-question
isolation is checked in the same shape the LLM uses. A sub-query passes when:
  - status=ok and matched id is in expected, or
  - status=unresolved and an expected id is inside the allowed candidate rank.
Candidate descriptions are never treated as confirmed facts.
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from eval_retrieval import field, post_json  # noqa: E402


def ids(items):
    return [field(item, "Id", "id", default="") for item in items or [] if field(item, "Id", "id", default="")]


def evaluate_row(api: str, row: dict) -> dict:
    game = row["game"]
    specs = row["queries"]
    plan = {
        "queries": [
            {"relation": q["relation"], "entity": q.get("entity", ""), "intent": q.get("intent", "")}
            for q in specs
        ]
    }
    raw = post_json(
        f"{api}/api/rules/games/{game}/execute-plan",
        {"question": row.get("question", ""), "plan": plan},
    )
    results = field(raw, "Results", "results") or []
    subrows = []
    for index, spec in enumerate(specs):
        result = results[index] if index < len(results) else {}
        expected = set(spec.get("expected") or [])
        matched = ids(field(result, "Matched", "matched") or [])
        candidates = ids(field(result, "Candidates", "candidates") or [])
        max_rank = int(spec.get("max_rank", 3))
        status = field(result, "Status", "status", default="")
        direct_hit = status == "ok" and bool(expected & set(matched))
        candidate_hit = bool(expected & set(candidates[:max_rank]))
        wrong_confirm = status == "ok" and not direct_hit
        subrows.append(
            {
                "index": index,
                "relation": spec["relation"],
                "entity": spec.get("entity", ""),
                "intent": spec.get("intent", ""),
                "expected": sorted(expected),
                "status": status,
                "source": field(result, "Source", "source", default=""),
                "matched": matched,
                "candidates": candidates,
                "candidate_scores": field(result, "Candidates", "candidates") or [],
                "intent_echo": field(result, "Intent", "intent", default=""),
                "retrieval": field(result, "RetrievalQueries", "retrievalQueries") or [],
                "direct_hit": direct_hit,
                "candidate_hit": candidate_hit,
                "pass": direct_hit or candidate_hit,
                "wrong_confirm": wrong_confirm,
                "note": spec.get("note", ""),
            }
        )
    return {
        "id": row.get("id", ""),
        "game": game,
        "question": row.get("question", ""),
        "pass": all(item["pass"] for item in subrows),
        "wrong_confirm": any(item["wrong_confirm"] for item in subrows),
        "queries": subrows,
        "note": row.get("note", ""),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--gold", default="tools/qa/query_intent_regressions.jsonl")
    ap.add_argument("--api", default="http://localhost:5000")
    ap.add_argument("--out", default="")
    ap.add_argument("--show", choices=["all", "fail", "none"], default="fail")
    args = ap.parse_args()

    rows = [json.loads(line) for line in Path(args.gold).read_text(encoding="utf-8").splitlines() if line.strip()]
    evaluated = []
    for row in rows:
        try:
            evaluated.append(evaluate_row(args.api, row))
        except Exception as exc:  # noqa: BLE001
            evaluated.append({"id": row.get("id", ""), "game": row.get("game", ""), "pass": False,
                              "wrong_confirm": False, "error": str(exc), "queries": []})

    total = len(evaluated)
    passed = sum(row["pass"] for row in evaluated)
    wrong_confirms = sum(row["wrong_confirm"] for row in evaluated)
    subqueries = [query for row in evaluated for query in row.get("queries", [])]
    print(f"query-intent regressions: {args.gold}  rows={total}  passed={passed}/{total}")
    print(f"  direct_ok_hit      {sum(q['direct_hit'] for q in subqueries)}/{len(subqueries)}")
    print(f"  candidate_topk_hit {sum(q['candidate_hit'] for q in subqueries)}/{len(subqueries)}")
    print(f"  wrong_confirms     {wrong_confirms}/{len(subqueries)}")
    print(f"  failed_rows        {total - passed}/{total}")

    if args.show != "none":
        print("\n" + "=" * 80)
        for row in evaluated:
            if args.show == "fail" and row["pass"]:
                continue
            print(json.dumps(row, ensure_ascii=False))

    if args.out:
        Path(args.out).write_text(json.dumps(evaluated, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\nwrote {args.out}")
    return 0 if passed == total and wrong_confirms == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
