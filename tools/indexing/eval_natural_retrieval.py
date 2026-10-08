#!/usr/bin/env python3
"""Replay source-anchored natural questions against search and explain, without an LLM.

Strict primary-concept metrics measure retrieval, not correctness of answers or rules.
Unsupported/ambiguous questions are reported separately; candidates are not confirmations.
"""
from __future__ import annotations

import argparse
import json
import urllib.parse
import urllib.request
from pathlib import Path

from eval_retrieval import field, post_json
from retrieval_lab import metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", default="http://localhost:5000")
    parser.add_argument("--questions", type=Path, default=Path("tools/qa/retrieval_questions.jsonl"))
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    rows = []
    for item in map(json.loads, args.questions.read_text(encoding="utf-8").splitlines()):
        query = urllib.parse.urlencode({"q": item["query"]})
        with urllib.request.urlopen(f'{args.api}/api/rules/games/{item["game"]}/search?{query}', timeout=60) as resp:
            search = json.load(resp)
        plan = post_json(f'{args.api}/api/rules/games/{item["game"]}/execute-plan', {
            "question": item["query"], "plan": {"queries": [{"relation": "explain", "entity": item["query"]}]}
        })
        result = field(plan, "results", "Results")[0]
        candidates = field(result, "candidates", "Candidates") or []
        matched = field(result, "matched", "Matched") or []
        search_ids = [field(x, "id", "Id") for x in field(search, "results", "Results")]
        plan_ids = [field(x, "id", "Id") for x in matched or candidates]
        # Metrics only require ID order; retain complete diagnostics separately.
        rows.append({**item, "search": search, "plan": plan,
                     "metrics": {"search": metrics(item, [{"path": cid} for cid in search_ids]),
                                 "explain": metrics(item, [{"path": cid} for cid in plan_ids])}})
    summary = {}
    for split in ("dev", "holdout", "all"):
        positives = [r for r in rows if r["kind"] == "answerable" and (split == "all" or r["split"] == split)]
        summary[split] = {channel: {"n": len(positives), **{
            k: round(sum(r["metrics"][channel][k] for r in positives) / len(positives), 4)
            for k in positives[0]["metrics"][channel]
        }} for channel in ("search", "explain")}
    summary["negative"] = [{"id": r["id"], "kind": r["kind"],
                            "status": field(field(r["plan"], "results", "Results")[0], "status", "Status")}
                           for r in rows if r["kind"] != "answerable"]
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "traces.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.out / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
