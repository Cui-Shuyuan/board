#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Group program `unavailable` runs into deterministic diagnostic buckets.

The bucket uses observable trace data only. It does not claim that evidence
presence proves sufficiency; `expected_target_in_pack` still needs manual review.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path


def matches(expected: str, values: list[str]) -> bool:
    for value in values:
        local = value.split("::")[-1] if "::" in value else value
        if value == expected or local == expected or local.endswith("." + expected):
            return True
    return False


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
        trace_questions = orchestration.get("questions") or []
        packed = []
        retrieval = []
        for question in trace_questions:
            packed.extend(question.get("evidenceCanonicalPaths") or [])
            retrieval.extend(question.get("retrievalCandidatePaths") or [])
        has_layer_fields = any(
            ("evidenceCanonicalPaths" in question) or ("retrievalCandidatePaths" in question)
            for question in trace_questions
        )
        if not trace_questions or not has_layer_fields:
            for query in (run.get("evidence") or {}).get("queries") or []:
                packed.extend((item.get("id") or "") for item in query.get("matched") or [])
                retrieval.extend((item.get("id") or "") for item in query.get("candidates") or [])
        expected = run.get("expected_rule_ids") or []
        retrieval_expected = any(matches(exp, retrieval) for exp in expected) if expected else None
        pack_expected = any(matches(exp, packed) for exp in expected) if expected else None
        if not expected:
            bucket = "unknown_no_expected_target"
            earliest_failure_hint = "unknown"
        elif not packed:
            bucket = "no_formal_evidence_packed"
            earliest_failure_hint = "pack_or_before"
        elif pack_expected is True:
            bucket = "expected_target_packed_but_unavailable"
            earliest_failure_hint = "respond_or_evidence_sufficiency"
        elif pack_expected is False:
            bucket = "other_evidence_packed_target_missing"
            earliest_failure_hint = "pack_or_before"
        else:
            bucket = "unknown_no_expected_target"
            earliest_failure_hint = "unknown"

        # This is only a triage hint. A target can be retrieved and packed yet
        # still be insufficient for the user's actual intent; manual review is
        # required before calling it a Respond failure.
        if retrieval_expected is True and pack_expected is False:
            earliest_failure_hint = "retrieval_to_pack"
        elif retrieval_expected is False:
            earliest_failure_hint = "plan_or_retrieval"
        rows.append({
            "run_id": run["run_id"],
            "question_id": run["question_id"],
            "game": run["game"],
            "question": run["question"],
            "expected_rule_ids": expected,
            "retrieval_paths": retrieval,
            "packed_paths": packed,
            "retrieval_expected": retrieval_expected,
            "pack_expected": pack_expected,
            "earliest_failure_hint": earliest_failure_hint,
            "manual_review_required": True,
            "outcome": orchestration.get("outcome"),
            "evidence_count": orchestration.get("evidenceCount"),
            "supplement_count": orchestration.get("supplementCount"),
            "repair_count": orchestration.get("repairCount"),
            "question_trace": trace_questions,
            "judge_pass": judged.get(run["run_id"], {}).get("pass"),
            "judge_reason": judged.get(run["run_id"], {}).get("reason"),
            "bucket": bucket,
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
