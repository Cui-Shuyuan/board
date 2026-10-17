#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Group program/legacy unavailable runs into deterministic diagnostic buckets.

The bucketing order is deliberately "later layers first": once acceptable formal
evidence was delivered to the model, a retrieval-candidate miss must not be
reported as the earliest failure. Path matching uses canonical path identity.
Historical values without canonical identity are marked unknown rather than
being guessed from their last segment.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path

CANONICAL_MARKERS = ("::", "/", ".")


def _is_canonical_identity(value: str) -> bool:
    return any(marker in value for marker in CANONICAL_MARKERS)


def match_identity(expected: str, values: list[str]) -> bool | None:
    """Return True/False when identity is observable, None when only legacy
    non-canonical values exist and a suffix heuristic would be needed."""
    if not expected:
        return None
    if not values:
        return False
    if expected in values:
        return True
    expected_canonical = _is_canonical_identity(expected)
    values_canonical = any(_is_canonical_identity(value) for value in values)
    # Canonical-vs-canonical is a real miss. Mixed formats cannot prove a miss
    # without the prohibited last-segment heuristic, so report unknown.
    if expected_canonical and values_canonical:
        return False
    if expected_canonical or values_canonical:
        return None
    return False


def _collect_run_layers(run: dict) -> dict:
    orchestration = run.get("orchestration") or {}
    trace_questions = orchestration.get("questions") or []
    packed: list[str] = []
    retrieval: list[str] = []
    packed_observable = False
    retrieval_observable = False

    for question in trace_questions:
        if "evidenceCanonicalPaths" in question:
            packed_observable = True
            packed.extend(question.get("evidenceCanonicalPaths") or [])
        if "retrievalCandidatePaths" in question:
            retrieval_observable = True
            retrieval.extend(question.get("retrievalCandidatePaths") or [])

    # Legacy/old program traces expose evidence.queries. Matched is a formal-read
    # and tool-delivery proxy; candidates are raw retrieval candidates.
    for query in (run.get("evidence") or {}).get("queries") or []:
        if "matched" in query:
            packed_observable = True
            packed.extend((item.get("id") or "") for item in query.get("matched") or [])
        if "candidates" in query:
            retrieval_observable = True
            retrieval.extend((item.get("id") or "") for item in query.get("candidates") or [])

    return {
        "packed": [p for p in packed if p],
        "retrieval": [p for p in retrieval if p],
        "packed_observable": packed_observable,
        "retrieval_observable": retrieval_observable,
    }


def classify(run: dict) -> dict:
    layers = _collect_run_layers(run)
    expected = run.get("expected_rule_ids") or []
    retrieval_hits = [match_identity(exp, layers["retrieval"]) for exp in expected]
    packed_hits = [match_identity(exp, layers["packed"]) for exp in expected]

    retrieval_expected = _combine_observability(retrieval_hits) if layers["retrieval_observable"] else None
    pack_expected = _combine_observability(packed_hits) if layers["packed_observable"] else None

    # Earliest-failure order: delivered evidence wins over retrieval diagnostics.
    if not expected:
        bucket = "unknown_no_expected_target"
        earliest = "unknown"
    elif pack_expected is True:
        bucket = "expected_target_delivered_but_unavailable"
        earliest = "respond_or_sufficiency"
    elif not layers["packed_observable"] or not layers["retrieval_observable"]:
        bucket = "unknown_observability"
        earliest = "unknown"
    elif retrieval_expected is True:
        bucket = "retrieval_hit_not_formally_delivered"
        earliest = "navigation_or_formal_read"
    elif retrieval_expected is False:
        bucket = "no_retrieval_or_formal_hit"
        earliest = "plan_or_retrieval"
    else:
        bucket = "unknown_identity_format"
        earliest = "unknown"

    return {
        "retrieval_paths": layers["retrieval"],
        "packed_paths": layers["packed"],
        "retrieval_expected": retrieval_expected,
        "pack_expected": pack_expected,
        "earliest_failure_hint": earliest,
        "bucket": bucket,
        "manual_review_required": True,
        "match_method": "canonical_exact_only",
    }


def _combine_observability(values: list[bool | None]) -> bool | None:
    if not values:
        return None
    if any(value is True for value in values):
        return True
    if all(value is False for value in values):
        return False
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--judge", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    runs = [json.loads(line) for line in args.runs.read_text(encoding="utf-8").splitlines() if line.strip()]
    judged = {row["run_id"]: row for row in (
        json.loads(line) for line in args.judge.read_text(encoding="utf-8").splitlines() if line.strip()
    )}
    rows = []
    for run in runs:
        if run.get("version") != "new":
            continue
        orchestration = run.get("orchestration") or {}
        if orchestration.get("outcome") != "unavailable":
            continue
        classification = classify(run)
        expected = run.get("expected_rule_ids") or []
        rows.append({
            "run_id": run["run_id"],
            "question_id": run["question_id"],
            "game": run["game"],
            "question": run["question"],
            "expected_rule_ids": expected,
            **classification,
            "outcome": orchestration.get("outcome"),
            "evidence_count": orchestration.get("evidenceCount"),
            "supplement_count": orchestration.get("supplementCount"),
            "repair_count": orchestration.get("repairCount"),
            "question_trace": orchestration.get("questions") or [],
            "judge_pass": judged.get(run["run_id"], {}).get("pass"),
            "judge_reason": judged.get(run["run_id"], {}).get("reason"),
        })

    by_question = defaultdict(list)
    for row in rows:
        by_question[row["question_id"]].append(row)
    question_summary = []
    for qid, items in sorted(by_question.items()):
        question_summary.append({
            "question_id": qid,
            "game": items[0]["game"],
            "question": items[0]["question"],
            "expected_rule_ids": items[0]["expected_rule_ids"],
            "runs": len(items),
            "buckets": dict(Counter(item["bucket"] for item in items)),
            "packed_paths": sorted({p for item in items for p in item["packed_paths"]}),
            "retrieval_paths": sorted({p for item in items for p in item["retrieval_paths"]}),
        })

    result = {
        "total_runs": len(rows),
        "unique_questions": len(by_question),
        "buckets": dict(Counter(row["bucket"] for row in rows)),
        "earliest_failure_hints": dict(Counter(row["earliest_failure_hint"] for row in rows)),
        "games": dict(Counter(row["game"] for row in rows)),
        "question_summary": question_summary,
        "runs": rows,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("total_runs", "unique_questions", "buckets")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
