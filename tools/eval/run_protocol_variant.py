#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Run one candidate API variant for protocol-only small comparisons.

This intentionally does not call an online judge. It records raw responses,
protocol/tool traces, latency and token usage so variants can be compared
without changing evidence or answer strategy between runs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path

from compare_versions import call_chat, get_json, post_json


def warm_up(api: str, timeout: int) -> list[dict]:
    rows = []
    for game, prompt in [("splendor", "请用一句话说明这个游戏的目标。"),
                         ("agricola", "请用一句话说明这个游戏的目标。")]:
        started = time.perf_counter()
        status, payload = post_json(
            f"{api}/api/chat",
            {"game_id": game, "messages": [{"role": "user", "content": prompt}]},
            timeout=timeout,
        )
        rows.append({
            "game": game,
            "status": status,
            "latency_ms": round((time.perf_counter() - started) * 1000, 2),
            "ok": status == 200 and isinstance(payload, dict),
        })
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--questions", type=Path, required=True)
    parser.add_argument("--api", default="http://localhost:5002")
    parser.add_argument("--label", required=True)
    parser.add_argument("--thinking", required=True)
    parser.add_argument("--tool-choice", default="")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20261017)
    parser.add_argument("--timeout", type=int, default=150)
    args = parser.parse_args()

    questions = [json.loads(line) for line in args.questions.read_text(encoding="utf-8").splitlines() if line.strip()]
    planned = len(questions) * args.repeats
    args.out.mkdir(parents=True, exist_ok=True)
    manifest = {
        "created_at": datetime.now(timezone.utc).isoformat(),
        "api": args.api,
        "label": args.label,
        "thinking": args.thinking,
        "tool_choice": args.tool_choice,
        "question_file": str(args.questions),
        "question_file_sha256": hashlib.sha256(args.questions.read_bytes()).hexdigest(),
        "question_count": len(questions),
        "repeats": args.repeats,
        "planned_chat_count": planned,
        "warmups": warm_up(args.api, args.timeout),
        "rules_games": get_json(f"{args.api}/api/rules/games"),
    }
    (args.out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    rng = random.Random(args.seed)
    jobs = []
    for repeat in range(1, args.repeats + 1):
        ordered = questions[:]
        rng.shuffle(ordered)
        for question in ordered:
            jobs.append((question, repeat))

    rows = []
    lock = threading.Lock()
    with (args.out / "runs.jsonl").open("w", encoding="utf-8") as out_file:
        with ThreadPoolExecutor(max_workers=1) as pool:
            futures = [
                pool.submit(call_chat, args.api, question, args.label, repeat, args.timeout)
                for question, repeat in jobs
            ]
            for future in as_completed(futures):
                row = future.result()
                with lock:
                    rows.append(row)
                    out_file.write(json.dumps(row, ensure_ascii=False) + "\n")
                    out_file.flush()
                if len(rows) % 20 == 0:
                    print(f"[{len(rows)}/{planned}]", flush=True)

    manifest["finished_at"] = datetime.now(timezone.utc).isoformat()
    manifest["actual_chat_count"] = len(rows)
    manifest["http_error_count"] = sum(1 for row in rows if row.get("http_status") != 200)
    manifest["attempts_gt1_count"] = sum(1 for row in rows if (row.get("attempts") or 0) > 1)
    (args.out / "manifest.final.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: manifest[k] for k in ("label", "actual_chat_count", "http_error_count", "attempts_gt1_count")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
