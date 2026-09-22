#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Entity-resolution / retrieval evaluation against a gold JSONL.

Gold line:
  {"game","relation","entity","question","expected":[concept_id,...],"note":...}

Usage:
  python scripts/eval_retrieval.py --gold qa/retrieval_gold.jsonl
  python scripts/eval_retrieval.py --gold qa/retrieval_gold.jsonl --api http://localhost:5000
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.request
from collections import Counter, defaultdict
from pathlib import Path


def post_json(url: str, body: dict, timeout: int = 60):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def ids(items):
    out = []
    for x in items or []:
        cid = x.get("Id") or x.get("id")
        if cid:
            out.append(cid)
    return out


def field(obj, *names, default=None):
    for n in names:
        if isinstance(obj, dict) and n in obj:
            return obj[n]
    return default


def evaluate_one(api: str, item: dict):
    game = item["game"]
    body = {
        "question": item.get("question", ""),
        "plan": {"queries": [{"relation": item["relation"], "entity": item["entity"]}]},
    }
    raw = post_json(f"{api}/api/rules/games/{game}/execute-plan", body)
    result = (field(raw, "Results", "results") or [{}])[0]
    matched = ids(field(result, "Matched", "matched") or [])
    candidates = ids(field(result, "Candidates", "candidates") or [])
    expected = set(item.get("expected") or [])
    status = field(result, "Status", "status", default="")
    return {
        "game": game,
        "relation": item["relation"],
        "entity": item["entity"],
        "question": item.get("question", ""),
        "expected": sorted(expected),
        "status": status,
        "source": field(result, "Source", "source", default=""),
        "matched": matched,
        "candidates": candidates,
        # resolution is "hit" if an expected id is resolved outright ...
        "ok_hit": status == "ok" and bool(expected & set(matched)),
        "ok_wrong": status == "ok" and not (expected & set(matched)),
        # ... or if expected appears in the candidate list (unresolved but recoverable)
        "cand_top1": bool(candidates) and candidates[0] in expected,
        "cand_top3": bool(expected & set(candidates[:3])),
        "unresolved": status == "unresolved",
        "no_match": status == "no_match",
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--gold", default="qa/retrieval_gold.jsonl")
    ap.add_argument("--api", default="http://localhost:5000")
    ap.add_argument("--out", default="")
    ap.add_argument("--show", choices=["all", "fail", "none"], default="fail")
    args = ap.parse_args()

    path = Path(args.gold)
    items = [json.loads(l) for l in path.read_text(encoding="utf-8").splitlines() if l.strip()]
    rows = []
    for item in items:
        try:
            rows.append(evaluate_one(args.api, item))
        except Exception as e:  # noqa: BLE001
            rows.append({"game": item["game"], "entity": item["entity"], "status": "error",
                         "error": str(e), "expected": item.get("expected", []), "ok_hit": False,
                         "ok_wrong": False, "cand_top1": False, "cand_top3": False,
                         "unresolved": False, "no_match": False})

    games = sorted({r["game"] for r in rows})
    print(f"gold: {path}  n={len(rows)}")
    for game in games + ["__ALL__"]:
        rs = rows if game == "__ALL__" else [r for r in rows if r["game"] == game]
        if not rs:
            continue
        n = len(rs)
        print(f"\n[{game}] n={n}")
        print(f"  resolved_hit        {sum(r['ok_hit'] for r in rs)}/{n}")
        print(f"  resolved_wrong      {sum(r['ok_wrong'] for r in rs)}/{n}")
        print(f"  candidate_top1_hit  {sum(r['cand_top1'] for r in rs)}/{n}")
        print(f"  candidate_top3_hit  {sum(r['cand_top3'] for r in rs)}/{n}")
        print(f"  unresolved           {sum(r['unresolved'] for r in rs)}/{n}")
        print(f"  no_match             {sum(r['no_match'] for r in rs)}/{n}")

    if args.show != "none":
        print("\n" + "=" * 80)
        for r in rows:
            if args.show == "fail" and (r.get("ok_hit") or r.get("cand_top3")):
                continue
            print(json.dumps(r, ensure_ascii=False))

    if args.out:
        Path(args.out).write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\nwrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
