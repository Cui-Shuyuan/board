#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Summarize protocol-variant runs without calling a judge.

Each input is one ``runs.jsonl`` produced by ``run_protocol_variant.py`` (or a
directory containing it). The output intentionally keeps protocol failures,
repair calls, token usage, latency and timeline categories in separate fields
so protocol compliance is not confused with answer quality.
"""
from __future__ import annotations

import argparse
import json
import math
import statistics
from collections import Counter
from pathlib import Path
from typing import Any


def percentile(values: list[float], fraction: float) -> float | None:
    """Linear-interpolated percentile, matching the comparison reports."""
    if not values:
        return None
    ordered = sorted(values)
    position = fraction * (len(ordered) - 1)
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return round(ordered[lower], 1)
    value = ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)
    return round(value, 1)


def mean_or_none(values: list[float]) -> float | None:
    return round(statistics.mean(values), 1) if values else None


def median_or_none(values: list[float]) -> float | int | None:
    if not values:
        return None
    value = statistics.median(values)
    return int(value) if float(value).is_integer() else round(value, 3)


def _token_total(row: dict[str, Any], field: str) -> int:
    calls = (row.get("orchestration") or {}).get("modelCalls") or []
    return sum(int(call.get(field) or 0) for call in calls)


def _value(row: dict[str, Any], path: tuple[str, ...], default: Any = 0) -> Any:
    node: Any = row
    for key in path:
        if not isinstance(node, dict):
            return default
        node = node.get(key)
    return default if node is None else node


def analyze_runs(rows: list[dict[str, Any]]) -> dict[str, Any]:
    if not rows:
        raise ValueError("cannot analyze an empty run set")

    outcomes = Counter(_value(row, ("orchestration", "outcome"), "") for row in rows)
    protocol_errors = Counter(
        error
        for row in rows
        for error in (_value(row, ("orchestration", "protocolErrors"), []) or [])
    )
    plain_final_runs = sum(
        "plain_final_submission" in (_value(row, ("orchestration", "protocolErrors"), []) or [])
        for row in rows
    )
    validation_error_runs = sum(
        "protocol_validation_error" in (_value(row, ("orchestration", "protocolErrors"), []) or [])
        for row in rows
    )
    repair_calls = [
        int(_value(row, ("orchestration", "protocolRepairModelCallCount"), 0))
        for row in rows
    ]
    repair_runs = sum(count > 0 for count in repair_calls)
    repair_ms = [
        float(_value(row, ("orchestration", "protocolRepairMs"), 0))
        for row in rows
    ]
    repaired_ms = [value for value, calls in zip(repair_ms, repair_calls) if calls > 0]

    latencies = [float(row.get("latency_ms") or 0) for row in rows]
    model_calls = [int(_value(row, ("orchestration", "modelCallCount"), 0)) for row in rows]
    tool_calls = [int(_value(row, ("orchestration", "executedToolCallCount"), 0)) for row in rows]
    delivered_chars = [
        int(_value(row, ("orchestration", "deliveredEvidenceChars"), 0)) for row in rows
    ]
    auto_reads = [
        int(_value(row, ("orchestration", "candidateAutoReadCount"), 0)) for row in rows
    ]

    prompt_tokens = [_token_total(row, "promptTokens") for row in rows]
    completion_tokens = [_token_total(row, "completionTokens") for row in rows]
    reasoning_tokens = [_token_total(row, "reasoningTokens") for row in rows]

    category_values: dict[str, list[float]] = {}
    for row in rows:
        for item in _value(row, ("orchestration", "timelineCategories"), []) or []:
            category = str(item.get("category") or "")
            if category:
                category_values.setdefault(category, []).append(float(item.get("selfMs") or 0))
    category_self_ms_mean = {
        category: mean_or_none(values)
        for category, values in sorted(category_values.items())
    }

    reconciled = [
        bool(_value(row, ("orchestration", "timelineAccounting", "isReconciled"), False))
        for row in rows
    ]

    return {
        "runs": len(rows),
        "http_error_count": sum(1 for row in rows if row.get("http_status") != 200),
        "attempts_gt1_count": sum(1 for row in rows if int(row.get("attempts") or 0) > 1),
        "outcomes": dict(sorted(outcomes.items())),
        "protocol_errors": dict(sorted(protocol_errors.items())),
        "protocol_error_runs": sum(1 for row in rows
                                   if (_value(row, ("orchestration", "protocolErrors"), []) or [])),
        "plain_final_runs": plain_final_runs,
        "protocol_validation_error_runs": validation_error_runs,
        "protocol_repair_runs": repair_runs,
        "protocol_repair_rate": round(repair_runs / len(rows), 4),
        "protocol_repair_ms_overall_mean": mean_or_none(repair_ms),
        "protocol_repair_ms_among_repaired_mean": mean_or_none(repaired_ms),
        "latency_p50_ms": percentile(latencies, 0.50),
        "latency_p95_ms": percentile(latencies, 0.95),
        "latency_mean_ms": mean_or_none(latencies),
        "model_calls_mean": round(statistics.mean(model_calls), 3),
        "model_calls_median": median_or_none(model_calls),
        "executed_tool_calls_mean": round(statistics.mean(tool_calls), 3),
        "delivered_evidence_chars_mean": mean_or_none(delivered_chars),
        "candidate_auto_read_mean": round(statistics.mean(auto_reads), 3),
        "prompt_tokens_mean": mean_or_none(prompt_tokens),
        "completion_tokens_mean": mean_or_none(completion_tokens),
        "reasoning_tokens_mean": mean_or_none(reasoning_tokens),
        "reasoning_zero_runs": sum(value == 0 for value in reasoning_tokens),
        "category_self_ms_mean": category_self_ms_mean,
        "timeline_reconciled_rate": round(sum(reconciled) / len(rows), 4),
    }


def load_runs(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    runs_file = path / "runs.jsonl" if path.is_dir() else path
    rows = [
        json.loads(line)
        for line in runs_file.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    manifest: dict[str, Any] = {}
    manifest_file = runs_file.parent / "manifest.final.json"
    if manifest_file.exists():
        manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    elif (runs_file.parent / "manifest.json").exists():
        manifest = json.loads((runs_file.parent / "manifest.json").read_text(encoding="utf-8"))
    return rows, {
        "runs_file": str(runs_file),
        "label": manifest.get("label"),
        "thinking": manifest.get("thinking"),
        "tool_choice": manifest.get("tool_choice"),
        "question_file_sha256": manifest.get("question_file_sha256"),
    }


def parse_variant(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("variant must be NAME=PATH")
    name, path = value.split("=", 1)
    if not name.strip() or not path.strip():
        raise argparse.ArgumentTypeError("variant must be NAME=PATH")
    return name.strip(), Path(path.strip())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--variant", action="append", type=parse_variant, required=True,
        metavar="NAME=PATH", help="e.g. A=.tmp/protocol-comparison/A-thinking-low",
    )
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    result: dict[str, Any] = {"online_judge_calls_added": 0, "variants": {}}
    for name, path in args.variant:
        rows, provenance = load_runs(path)
        result["variants"][name] = {**provenance, **analyze_runs(rows)}

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.out}; variants={len(result['variants'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
