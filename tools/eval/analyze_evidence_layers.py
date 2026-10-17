#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Report retrieval / packed-evidence / citation / answer layers separately.

Legacy evidence has no formal pack/citation trace, so those layers are reported
as unavailable rather than silently mapped from Matched.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path


def ids(items):
    out = []
    for item in items or []:
        cid = item.get("id") or item.get("Id") or item.get("path") or item.get("Path")
        if cid:
            out.append(cid)
    return out


def matches(expected: str, values: list[str]) -> bool:
    for value in values:
        if value == expected:
            return True
        local = value.split("::")[-1] if "::" in value else value
        if local == expected:
            return True
        if local.endswith("." + expected):
            return True
    return False


def trace_questions(run: dict) -> list[dict]:
    orchestration = run.get("orchestration") or {}
    return orchestration.get("questions") or []


def layer_paths(run: dict) -> tuple[list[str], list[str] | None, list[str] | None]:
    questions = trace_questions(run)
    has_layer_fields = any(
        ("retrievalCandidatePaths" in question)
        or ("evidenceCanonicalPaths" in question)
        or ("citedCanonicalPaths" in question)
        for question in questions
    )
    if questions and has_layer_fields:
        retrieval = []
        packed = []
        cited = []
        packed_observable = False
        cited_observable = False
        for question in questions:
            retrieval.extend(question.get("retrievalCandidatePaths") or [])
            if "evidenceCanonicalPaths" in question:
                packed_observable = True
                packed.extend(question.get("evidenceCanonicalPaths") or [])
            if "citedCanonicalPaths" in question:
                cited_observable = True
                cited.extend(question.get("citedCanonicalPaths") or [])
        # Empty lists are observable and mean "nothing packed/cited"; missing
        # fields mean the historical run did not record that layer.
        return retrieval, packed if packed_observable else None, cited if cited_observable else None

    # Historical program/legacy fallback: evidence.candidates is raw retrieval,
    # evidence.queries.matched is a formal-read proxy only for program; legacy
    # Matched is intentionally not called a pack.
    evidence = run.get("evidence") or {}
    retrieval, packed = [], []
    for query in evidence.get("queries") or []:
        retrieval.extend(ids(query.get("candidates")))
        if run.get("version") == "new":
            packed.extend(ids(query.get("matched")))
    return retrieval, packed or None, None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--judge", type=Path, default=None)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    runs = [json.loads(line) for line in args.runs.read_text(encoding="utf-8").splitlines() if line.strip()]
    judged = {}
    if args.judge:
        for line in args.judge.read_text(encoding="utf-8").splitlines():
            if line.strip():
                item = json.loads(line)
                judged[item["run_id"]] = item

    rows = []
    for run in runs:
        if run.get("question_type") != "positive":
            continue
        retrieval, packed, cited = layer_paths(run)
        expected = run.get("expected_rule_ids") or []
        orchestration = run.get("orchestration") or {}
        question_trace = trace_questions(run)
        row = {
            "run_id": run["run_id"],
            "version": run["version"],
            "question_id": run["question_id"],
            "game": run["game"],
            "expected": expected,
            "retrieval_paths": list(dict.fromkeys(retrieval)),
            "packed_paths": None if packed is None else list(dict.fromkeys(packed)),
            "cited_paths": None if cited is None else list(dict.fromkeys(cited)),
            "retrieval_hit": any(matches(exp, retrieval) for exp in expected),
            "pack_hit": None if packed is None else any(matches(exp, packed) for exp in expected),
            "citation_hit": None if cited is None else any(matches(exp, cited) for exp in expected),
            "answer_pass": judged.get(run["run_id"], {}).get("pass"),
            "orchestration_outcome": orchestration.get("outcome"),
            "coverage_statuses": [q.get("coverageStatus") for q in question_trace if q.get("coverageStatus")],
            "outcome_statuses": [q.get("outcomeStatus") for q in question_trace if q.get("outcomeStatus")],
            "evidence_read_attempts": orchestration.get("evidenceReadAttempts"),
            "successful_evidence_reads": orchestration.get("successfulEvidenceReads"),
            "binding_reuses": orchestration.get("evidenceBindingReuses"),
            "evidence_pack_chars": orchestration.get("evidencePackChars"),
        }
        rows.append(row)

    summary = {}
    for version in ("old", "new"):
        subset = [row for row in rows if row["version"] == version]
        def rate(key):
            vals = [row[key] for row in subset if row[key] is not None]
            return round(sum(bool(v) for v in vals) / len(vals), 4) if vals else None
        coverage = Counter(
            status for row in subset for status in row["coverage_statuses"]
        )
        outcomes = Counter(
            status for row in subset for status in row["outcome_statuses"]
        )
        summary[version] = {
            "positive_runs": len(subset),
            "retrieval_hit_rate": rate("retrieval_hit"),
            "pack_hit_rate": rate("pack_hit"),
            "citation_hit_rate": rate("citation_hit"),
            "answer_pass_rate": rate("answer_pass"),
            "pack_observable_runs": sum(row["pack_hit"] is not None for row in subset),
            "citation_observable_runs": sum(row["citation_hit"] is not None for row in subset),
            "pack_nonempty_runs": sum(bool(row["packed_paths"]) for row in subset),
            "citation_nonempty_runs": sum(bool(row["cited_paths"]) for row in subset),
            "coverage_status_counts": dict(coverage),
            "question_outcome_counts": dict(outcomes),
            "orchestration_outcome_counts": dict(Counter(
                row["orchestration_outcome"] for row in subset if row["orchestration_outcome"]
            )),
        }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps({"summary": summary, "runs": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
