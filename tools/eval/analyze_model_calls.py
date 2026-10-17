#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Count LLM/tool calls per chat request from Board API logs.

This is useful for the legacy baseline, whose HTTP response does not contain a
program-controlled trace. Request ids are taken from the ASP.NET log prefix.
"""
from __future__ import annotations

import argparse
import json
import re
import statistics
from datetime import datetime, timedelta, timezone
from pathlib import Path

LINE_RE = re.compile(r"^(\d{2}:\d{2}:\d{2}\.\d{3}) \[([^\]:]+):\d+\]")
LLM_SEND = "Sending HTTP request POST https://api.deepseek.com/"
START_MARKERS = ("[Chat] game:", "[Chat] program path:")
TOOL_MARKER = "Tool execute_plan result:"
WARMUP_MARKER = "请用一句话说明这个游戏的目标"


def percentile(values, p):
    if not values:
        return 0
    values = sorted(values)
    index = min(len(values) - 1, int(round((len(values) - 1) * p)))
    return values[index]


def analyze(log_path: Path, start_utc: str, timezone_offset_hours: int):
    start_local = datetime.fromisoformat(start_utc.replace("Z", "+00:00")).astimezone(
        timezone(timedelta(hours=timezone_offset_hours))
    )
    groups = {}
    for line in log_path.read_text(encoding="utf-8", errors="replace").splitlines():
        match = LINE_RE.match(line)
        if not match:
            continue
        parsed_time = datetime.strptime(match.group(1), "%H:%M:%S.%f").replace(
            year=start_local.year, month=start_local.month, day=start_local.day,
            tzinfo=timezone(timedelta(hours=timezone_offset_hours))
        )
        if parsed_time < start_local:
            continue
        request_id = match.group(2)
        group = groups.setdefault(request_id, {"llm": 0, "tool": 0, "chat_request": False, "warmup": False})
        if WARMUP_MARKER in line:
            group["warmup"] = True
        if START_MARKERS[0] in line or START_MARKERS[1] in line:
            group["chat_request"] = True
        if LLM_SEND in line:
            group["llm"] += 1
        if TOOL_MARKER in line:
            group["tool"] += 1
    requests = [g for g in groups.values() if g["chat_request"] and not g["warmup"]]
    llm = [g["llm"] for g in requests]
    tools = [g["tool"] for g in requests]
    return {
        "log": str(log_path),
        "start_utc": start_utc,
        "requests": len(requests),
        "llm_calls_total": sum(llm),
        "llm_calls_mean": round(statistics.mean(llm), 3) if llm else 0,
        "llm_calls_median": statistics.median(llm) if llm else 0,
        "llm_calls_p90": percentile(llm, 0.9),
        "llm_calls_max": max(llm) if llm else 0,
        "llm_call_distribution": {str(k): llm.count(k) for k in sorted(set(llm))},
        "tool_calls_total": sum(tools),
        "tool_calls_mean": round(statistics.mean(tools), 3) if tools else 0,
        "tool_calls_median": statistics.median(tools) if tools else 0,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-log", type=Path, required=True)
    parser.add_argument("--new-log", type=Path, required=True)
    parser.add_argument("--baseline-start-utc", required=True)
    parser.add_argument("--new-start-utc", required=True)
    parser.add_argument("--timezone-offset-hours", type=int, default=8)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    result = {
        "baseline": analyze(args.baseline_log, args.baseline_start_utc, args.timezone_offset_hours),
        "new": analyze(args.new_log, args.new_start_utc, args.timezone_offset_hours),
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
