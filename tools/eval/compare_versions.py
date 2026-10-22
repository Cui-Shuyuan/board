#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Run a paired old/new Board API comparison over a frozen question bank.

This runner intentionally does not score answers. It only records raw requests,
responses, evidence, latency and deterministic retrieval checks. Answer quality
is judged separately with a version-blind rubric.

Usage:
  python3 tools/eval/compare_versions.py \
    --questions tools/qa/comprehensive_eval_questions.jsonl \
    --old-api http://localhost:5001 \
    --new-api http://localhost:5002 \
    --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/run-20261017 \
    --repeats 3
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import subprocess
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path


def post_json(url: str, body: dict, timeout: int = 150) -> tuple[int, dict | str]:
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            raw = response.read().decode("utf-8")
            return response.status, json.loads(raw)
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")
    except Exception as exc:  # noqa: BLE001
        return 0, str(exc)


def get_json(url: str, timeout: int = 30):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def collect_ids(items):
    out = []
    for item in items or []:
        cid = item.get("id") or item.get("Id")
        if cid:
            out.append(cid)
    return out


CANONICAL_MARKERS = ("::", "/", ".")


def _is_canonical_identity(value: str) -> bool:
    return any(marker in value for marker in CANONICAL_MARKERS)


def matches_expected(expected: str, values: list[str]) -> bool | None:
    """Canonical exact-path match. Historical bare ids that would only match
    via a last-segment heuristic return None (unknown) instead of a fake hit."""
    if not expected:
        return None
    if not values:
        return False
    if expected in values:
        return True
    expected_canonical = _is_canonical_identity(expected)
    values_canonical = any(_is_canonical_identity(value) for value in values)
    if expected_canonical and values_canonical:
        return False
    if expected_canonical or values_canonical:
        return None
    return False


def matches_any(expected: set[str], values: list[str]) -> bool | None:
    states = [matches_expected(exp, values) for exp in expected]
    if not states:
        return None
    if any(state is True for state in states):
        return True
    if all(state is False for state in states):
        return False
    return None


def analyze_evidence(question: dict, response: dict) -> dict:
    evidence = response.get("evidence") or {}
    orchestration = response.get("orchestration") or {}
    queries = evidence.get("queries") or []
    expected = set(question.get("expected_rule_ids") or [])
    all_matched = []
    candidate_top3 = []
    candidate_top1 = []
    unresolved_candidates = []
    for query in queries:
        all_matched.extend(collect_ids(query.get("matched")))
        candidates = collect_ids(query.get("candidates"))
        unresolved_candidates.extend(candidates)
        candidate_top1.extend(candidates[:1])
        candidate_top3.extend(candidates[:3])
    direct_hits = expected & set(all_matched)
    top1_hits = expected & set(candidate_top1)
    top3_hits = expected & set(candidate_top3)

    # Newer traces preserve each layer separately. Do not infer pack/citation
    # from Matched or Tier; old runs leave these as None.
    trace_questions = orchestration.get("questions") or []
    retrieval_paths = []
    packed_paths = []
    cited_paths = []
    retrieval_observable = False
    packed_observable = False
    cited_observable = False
    for item in trace_questions:
        if "retrievalCandidatePaths" in item:
            retrieval_observable = True
            retrieval_paths.extend(item.get("retrievalCandidatePaths") or [])
        if "evidenceCanonicalPaths" in item:
            packed_observable = True
            packed_paths.extend(item.get("evidenceCanonicalPaths") or [])
        if "citedCanonicalPaths" in item:
            cited_observable = True
            cited_paths.extend(item.get("citedCanonicalPaths") or [])
    retrieval_paths = list(dict.fromkeys(retrieval_paths))
    packed_paths = list(dict.fromkeys(packed_paths))
    cited_paths = list(dict.fromkeys(cited_paths))
    retrieval_layer_hit = (
        matches_any(expected, retrieval_paths)
        if expected and retrieval_observable else None
    )
    pack_layer_hit = (
        matches_any(expected, packed_paths)
        if expected and packed_observable else None
    )
    citation_layer_hit = (
        matches_any(expected, cited_paths)
        if expected and cited_observable else None
    )

    def flexible(obj, *names, default=None):
        if not isinstance(obj, dict):
            return default
        for name in names:
            if name in obj:
                return obj[name]
        return default

    timeline = orchestration.get("timeline") or orchestration.get("Timeline") or []
    timeline_spans = timeline if isinstance(timeline, list) else []
    timeline_categories = (
        orchestration.get("timelineCategories")
        or orchestration.get("TimelineCategories")
        or []
    )
    if not isinstance(timeline_categories, list):
        timeline_categories = []
    category_self_costs = {}
    for item in timeline_categories:
        if not isinstance(item, dict):
            continue
        category = flexible(item, "category", "Category", default="")
        self_ms = flexible(item, "selfMs", "SelfMs", default=None)
        if category and self_ms is not None:
            try:
                category_self_costs[category] = float(self_ms)
            except (TypeError, ValueError):
                pass

    def span_sum(category):
        total = 0.0
        for span in timeline_spans:
            if not isinstance(span, dict):
                continue
            if flexible(span, "category", "Category", default="") != category:
                continue
            try:
                total += float(flexible(span, "durationMs", "DurationMs", default=0) or 0)
            except (TypeError, ValueError):
                pass
        return round(total, 2)

    def span_self_sum(category):
        total = 0.0
        observed = False
        for span in timeline_spans:
            if not isinstance(span, dict):
                continue
            if flexible(span, "category", "Category", default="") != category:
                continue
            value = flexible(span, "selfDurationMs", "SelfDurationMs", default=None)
            if value is None:
                return None
            observed = True
            try:
                total += float(value or 0)
            except (TypeError, ValueError):
                return None
        return round(total, 2) if observed else 0.0

    def category_self(category):
        if category_self_costs:
            return round(category_self_costs.get(category, 0.0), 2)
        # Older traces may not have server-side category costs; fall back to
        # per-span self durations but mark observability separately.
        return span_self_sum(category)

    accounting = orchestration.get("timelineAccounting") or orchestration.get("TimelineAccounting") or {}
    return {
        "tier": evidence.get("tier"),
        "has_data": evidence.get("hasData"),
        "is_complete": evidence.get("isComplete"),
        "pending_count": evidence.get("pendingCount"),
        "query_count": evidence.get("queryCount"),
        "rules_version": evidence.get("rulesVersion"),
        "matched_ids": list(dict.fromkeys(all_matched)),
        "unresolved_candidate_ids": list(dict.fromkeys(unresolved_candidates)),
        "direct_expected_hit": bool(direct_hits),
        "candidate_expected_top1": bool(top1_hits),
        "candidate_expected_top3": bool(top3_hits),
        "direct_hit_ids": sorted(direct_hits),
        "candidate_top3_hit_ids": sorted(top3_hits),
        "orchestration_outcome": orchestration.get("outcome"),
        "model_call_count": orchestration.get("modelCallCount"),
        "supplement_count": orchestration.get("supplementCount"),
        "repair_count": orchestration.get("repairCount"),
        "evidence_read_count": orchestration.get("evidenceCount"),
        "understand_ms": orchestration.get("understandMs"),
        "retrieval_ms": orchestration.get("retrievalMs"),
        "respond_ms": orchestration.get("respondMs"),
        "retrieval_candidate_paths": retrieval_paths,
        "packed_canonical_paths": packed_paths if packed_observable else None,
        "cited_canonical_paths": cited_paths if cited_observable else None,
        "retrieval_layer_hit": retrieval_layer_hit,
        "pack_layer_hit": pack_layer_hit,
        "retrieval_layer_observable": retrieval_observable,
        "citation_layer_hit": citation_layer_hit,
        "pack_layer_observable": packed_observable,
        "citation_layer_observable": cited_observable,
        "coverage_statuses": [q.get("coverageStatus") for q in trace_questions if q.get("coverageStatus")],
        "question_outcome_statuses": [q.get("outcomeStatus") for q in trace_questions if q.get("outcomeStatus")],
        "evidence_read_attempts": orchestration.get("evidenceReadAttempts"),
        "successful_evidence_reads": orchestration.get("successfulEvidenceReads"),
        "binding_reuses": orchestration.get("evidenceBindingReuses"),
        "effective_max_pack_chars": orchestration.get("effectiveMaxEvidencePackChars"),
        "reused_tool_call_count": orchestration.get("reusedToolCallCount"),
        "executed_tool_call_count": orchestration.get("executedToolCallCount"),
        "delivered_evidence_chars": orchestration.get("deliveredEvidenceChars"),
        "delivered_evidence_count": orchestration.get("deliveredEvidenceCount"),
        "candidate_auto_read_count": orchestration.get("candidateAutoReadCount"),
        "protocol_validation_error_count": len(orchestration.get("protocolErrors") or []),
        "protocol_repair_model_call_count": orchestration.get("protocolRepairModelCallCount"),
        "protocol_repair_ms": orchestration.get("protocolRepairMs"),
        "protocol_repair_self_ms": orchestration.get("protocolRepairSelfMs"),
        "timeline_span_count": len(timeline_spans),
        "model_span_ms": span_sum("model"),
        "tool_span_ms": span_sum("tool"),
        "search_span_ms": span_sum("search"),
        "read_span_ms": span_sum("read"),
        "navigation_span_ms": span_sum("navigation"),
        "assembly_span_ms": span_sum("assembly"),
        "serialization_span_ms": span_sum("serialization"),
        "protocol_span_ms": span_sum("protocol"),
        "model_self_ms": category_self("model"),
        "tool_self_ms": category_self("tool"),
        "search_self_ms": category_self("search"),
        "read_self_ms": category_self("read"),
        "navigation_self_ms": category_self("navigation"),
        "assembly_self_ms": category_self("assembly"),
        "serialization_self_ms": category_self("serialization"),
        "protocol_self_ms": category_self("protocol"),
        "timeline_category_costs_observable": bool(category_self_costs),
        "timeline_covered_ms": flexible(accounting, "coveredMs", "CoveredMs"),
        "timeline_unaccounted_ms": flexible(accounting, "unaccountedMs", "UnaccountedMs"),
        "timeline_overlap_ms": flexible(accounting, "overlapMs", "OverlapMs"),
        "timeline_reconciled": flexible(accounting, "isReconciled", "IsReconciled"),
    }


def call_chat(api: str, question: dict, version: str, repeat: int, timeout: int) -> dict:
    run_id = f"{question['id']}::{version}::r{repeat}"
    body = {
        "game_id": question["game"],
        "messages": [{"role": "user", "content": question["question"]}],
    }
    started = datetime.now(timezone.utc).isoformat()
    t0 = time.perf_counter()
    attempts = 0
    status, payload = 0, ""
    while attempts < 2:
        attempts += 1
        status, payload = post_json(f"{api}/api/chat", body, timeout=timeout)
        if status == 200 and isinstance(payload, dict):
            break
        if attempts < 2:
            time.sleep(1.5)
    latency_ms = round((time.perf_counter() - t0) * 1000, 2)
    response = payload if isinstance(payload, dict) else {}
    return {
        "run_id": run_id,
        "version": version,
        "question_id": question["id"],
        "game": question["game"],
        "category": question.get("category", ""),
        "split": question.get("split", ""),
        "question_type": question.get("question_type", "positive"),
        "expected_behavior": question.get("expected_behavior", ""),
        "expected_rule_ids": question.get("expected_rule_ids") or [],
        "question": question["question"],
        "repeat": repeat,
        "started_at": started,
        "latency_ms": latency_ms,
        "attempts": attempts,
        "http_status": status,
        "error": None if status == 200 else (payload if isinstance(payload, str) else json.dumps(payload, ensure_ascii=False)),
        "reply": response.get("reply", ""),
        "evidence": response.get("evidence"),
        "orchestration": response.get("orchestration"),
        "deterministic": analyze_evidence(question, response),
    }


def warm_up(api: str, version: str, timeout: int):
    probes = [
        ("splendor", "请用一句话说明这个游戏的目标。"),
        ("agricola", "请用一句话说明这个游戏的目标。"),
    ]
    rows = []
    for game, prompt in probes:
        t0 = time.perf_counter()
        status, payload = post_json(
            f"{api}/api/chat",
            {"game_id": game, "messages": [{"role": "user", "content": prompt}]},
            timeout=timeout,
        )
        rows.append({"version": version, "game": game, "status": status,
                     "latency_ms": round((time.perf_counter() - t0) * 1000, 2),
                     "ok": status == 200 and isinstance(payload, dict)})
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--questions", type=Path, default=Path("tools/qa/comprehensive_eval_questions.jsonl"))
    parser.add_argument("--old-api", default="http://localhost:5001")
    parser.add_argument("--new-api", default="http://localhost:5002")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20261017)
    parser.add_argument("--timeout", type=int, default=150)
    parser.add_argument("--limit", type=int, default=0, help="Only run first N questions for a smoke test")
    parser.add_argument("--resume", action="store_true", help="Skip run_ids already present in out/runs.jsonl")
    parser.add_argument("--pair-workers", type=int, default=1, help="How many old/new pairs may be in flight at once")
    parser.add_argument("--budget-chats", type=int, default=1014,
                        help="Pre-registered maximum chat calls; planning fails closed if exceeded")
    parser.add_argument("--pre-registration", type=Path, default=None,
                        help="Frozen metric/threshold document to hash into the manifest")
    args = parser.parse_args()

    questions = [json.loads(line) for line in args.questions.read_text(encoding="utf-8").splitlines() if line.strip()]
    if args.limit:
        questions = questions[: args.limit]
    planned_chats = len(questions) * args.repeats * 2
    if planned_chats > args.budget_chats:
        parser.error(f"planned {planned_chats} chats exceeds pre-registered budget {args.budget_chats}")
    args.out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(args.seed)
    try:
        git_commit = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], stderr=subprocess.DEVNULL, text=True
        ).strip()
    except Exception:  # noqa: BLE001
        git_commit = "unknown"
    pre_registration_sha256 = None
    if args.pre_registration:
        pre_registration_sha256 = hashlib.sha256(args.pre_registration.read_bytes()).hexdigest()

    manifest = {
        "created_at": datetime.now(timezone.utc).isoformat(),
        "question_file": str(args.questions),
        "question_file_sha256": hashlib.sha256(args.questions.read_bytes()).hexdigest(),
        "question_count": len(questions),
        "repeats": args.repeats,
        "seed": args.seed,
        "planned_chat_count": planned_chats,
        "budget_chat_count": args.budget_chats,
        "git_commit": git_commit,
        "pre_registration_path": str(args.pre_registration) if args.pre_registration else None,
        "pre_registration_sha256": pre_registration_sha256,
        "pre_registered_metrics": [
            "paired answer pass rate: point estimate not below baseline; paired bootstrap 95% CI lower bound > -3pp",
            "fabricated/wrong-rule rate: point estimate not increased; manual review of new errors",
            "P50 chat latency at least 10% lower; P95 no more than 5% higher",
            "mechanical confirmation and repeated bottom-query counts lower",
            "legitimate alternative evidence accepted; evidence hit is not answer correctness",
        ],
        "old_api": args.old_api,
        "new_api": args.new_api,
        "warmups": {},
    }
    for name, api in (("old", args.old_api), ("new", args.new_api)):
        manifest[f"{name}_rules_games"] = get_json(f"{api}/api/rules/games")
        manifest["warmups"][name] = warm_up(api, name, args.timeout)
    (args.out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.out / "questions_snapshot.jsonl").write_text(
        "".join(json.dumps(q, ensure_ascii=False) + "\n" for q in questions),
        encoding="utf-8",
    )

    out_path = args.out / "runs.jsonl"
    existing_run_ids = set()
    if args.resume and out_path.exists():
        for line in out_path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                existing_run_ids.add(json.loads(line)["run_id"])
    lock = threading.Lock()
    written = 0
    total_pairs = len(questions) * args.repeats
    skipped = 0
    started_all = time.perf_counter()

    def run_pair(question: dict, versions: list[tuple[str, str]], repeat: int) -> list[dict]:
        with ThreadPoolExecutor(max_workers=len(versions)) as pair_pool:
            futures = [
                pair_pool.submit(call_chat, api, question, version, repeat, args.timeout)
                for version, api in versions
            ]
            return [future.result() for future in as_completed(futures)]

    pair_jobs = []
    for repeat in range(1, args.repeats + 1):
        ordered = questions[:]
        rng.shuffle(ordered)
        for question in ordered:
            versions = [("old", args.old_api), ("new", args.new_api)]
            if rng.random() < 0.5:
                versions.reverse()
            pending_versions = [
                (version, api) for version, api in versions
                if f"{question['id']}::{version}::r{repeat}" not in existing_run_ids
            ]
            skipped += len(versions) - len(pending_versions)
            if pending_versions:
                pair_jobs.append((question, pending_versions, repeat))

    with out_path.open("a" if args.resume else "w", encoding="utf-8") as out_file, \
         ThreadPoolExecutor(max_workers=max(1, args.pair_workers)) as pool:
        futures = [pool.submit(run_pair, question, versions, repeat) for question, versions, repeat in pair_jobs]
        for future in as_completed(futures):
            rows = future.result()
            with lock:
                for row in sorted(rows, key=lambda r: r["version"]):
                    out_file.write(json.dumps(row, ensure_ascii=False) + "\n")
                    existing_run_ids.add(row["run_id"])
                out_file.flush()
                written += len(rows)
                if written % 20 == 0:
                    elapsed = time.perf_counter() - started_all
                    print(f"[written+{written}/{total_pairs*2}] elapsed={elapsed:.0f}s pairWorkers={args.pair_workers} skipped={skipped}", flush=True)

    manifest["finished_at"] = datetime.now(timezone.utc).isoformat()
    manifest["elapsed_seconds"] = round(time.perf_counter() - started_all, 2)
    manifest["written_runs_this_invocation"] = written
    manifest["skipped_existing_runs"] = skipped
    manifest["total_run_ids"] = len(existing_run_ids)
    manifest_path = args.out / ("manifest.resume.json" if args.resume else "manifest.json")
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {out_path} (+{written} this invocation, total={len(existing_run_ids)}, skipped={skipped})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
