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
    --out .tmp/comprehensive-eval/run-20261017 \
    --repeats 3
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
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
    args = parser.parse_args()

    questions = [json.loads(line) for line in args.questions.read_text(encoding="utf-8").splitlines() if line.strip()]
    if args.limit:
        questions = questions[: args.limit]
    args.out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(args.seed)

    manifest = {
        "created_at": datetime.now(timezone.utc).isoformat(),
        "question_file": str(args.questions),
        "question_file_sha256": hashlib.sha256(args.questions.read_bytes()).hexdigest(),
        "question_count": len(questions),
        "repeats": args.repeats,
        "seed": args.seed,
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
