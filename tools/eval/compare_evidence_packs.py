#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Summarize an old-vs-new serialized evidence pack without calling a model.

Supports both pack shapes:
  - legacy: questions[].evidence[]
  - repaired: evidence_catalog[] + questions[].evidence_bindings[]
"""
from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path


def evidence_rows(pack: dict) -> list[dict]:
    if pack.get("evidence_catalog"):
        return list(pack["evidence_catalog"])
    rows = []
    for question in pack.get("questions") or []:
        for evidence in question.get("evidence") or []:
            row = dict(evidence)
            row.setdefault("_question_id", question.get("question_id"))
            rows.append(row)
    return rows


def binding_rows(pack: dict) -> list[dict]:
    rows = []
    for question in pack.get("questions") or []:
        if "evidence_bindings" in question:
            for binding in question.get("evidence_bindings") or []:
                item = dict(binding)
                item["_question_id"] = question.get("question_id")
                rows.append(item)
        else:
            for evidence in question.get("evidence") or []:
                rows.append({
                    "evidence_id": evidence.get("evidence_id"),
                    "role": evidence.get("role", "unknown"),
                    "parent_path": None,
                    "_question_id": question.get("question_id"),
                })
    return rows


def body_hash(evidence: dict) -> str:
    body = json.dumps(evidence.get("source"), ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(body.encode("utf-8")).hexdigest()[:16]


def summarize(path: Path, expected: list[str]) -> dict:
    raw = path.read_text(encoding="utf-8")
    pack = json.loads(raw)
    evidence = evidence_rows(pack)
    bindings = binding_rows(pack)
    paths = [item.get("canonical_path", "") for item in evidence]
    path_set = {path for path in paths if path}
    compact = json.dumps(pack, ensure_ascii=False, separators=(",", ":"))
    return {
        "path": str(path),
        "format": "repaired" if pack.get("evidence_catalog") is not None else "legacy",
        "compact_chars": len(compact),
        "evidence_records": len(evidence),
        "unique_evidence_ids": len({item.get("evidence_id") for item in evidence if item.get("evidence_id")}),
        "distinct_canonical_paths": len(path_set),
        "canonical_paths": sorted(path_set),
        "expected_paths_present": sorted(path_set & set(expected)),
        "expected_paths_missing": sorted(set(expected) - path_set),
        "omitted_evidence_count": pack.get("omitted_evidence_count", 0),
        "binding_count": len(bindings),
        "binding_roles": dict(Counter(binding.get("role") or "unknown" for binding in bindings)),
        "question_coverage": [
            {
                "question_id": question.get("question_id"),
                "retrieval_status": question.get("retrieval_status"),
                "coverage_status": question.get("coverage_status"),
                "evidence_bindings": len(question.get("evidence_bindings") or question.get("evidence") or []),
                "omitted_evidence_count": question.get("omitted_evidence_count", 0),
            }
            for question in pack.get("questions") or []
        ],
        "content_hashes": [
            {
                "evidence_id": item.get("evidence_id"),
                "canonical_path": item.get("canonical_path"),
                "body_hash": body_hash(item),
            }
            for item in evidence
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--new", type=Path, required=True)
    parser.add_argument("--expected", nargs="*", default=[])
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    result = {
        "expected_paths": args.expected,
        "old": summarize(args.old, args.expected),
        "new": summarize(args.new, args.expected),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "old_distinct_paths": result["old"]["distinct_canonical_paths"],
        "new_distinct_paths": result["new"]["distinct_canonical_paths"],
        "old_expected_present": result["old"]["expected_paths_present"],
        "new_expected_present": result["new"]["expected_paths_present"],
        "old_compact_chars": result["old"]["compact_chars"],
        "new_compact_chars": result["new"]["compact_chars"],
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
