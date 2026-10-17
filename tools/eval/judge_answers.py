#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Version-blind answer judging for the comprehensive old/new comparison.

The judge receives only the user question, expected behavior, acceptable rule
snippets, and the final answer. It does not receive the version label, evidence
tier, latency, or candidate list.

Run this script where the model API key is available, e.g. from Windows cmd:
  D:\\Python\\Python312\\python.exe tools\\eval\\judge_answers.py \
    --runs .tmp\\comprehensive-eval\\main\\runs.jsonl \
    --ground-truth .tmp\\comprehensive-eval\\question_ground_truth.json \
    --out .tmp\\comprehensive-eval\\main\\judge.jsonl
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


SYSTEM_PROMPT = """你是桌游规则问答的严格盲评员。你只看到问题、可接受的规则证据片段和最终回答，不知道回答来自哪个系统版本。
请判断最终回答是否真正、直接地回答了用户问题，并且规则内容是否被证据支持。
你必须输出一个 JSON 对象，不要输出 markdown 或其他文字。"""


def build_prompt(run: dict, truth: dict, details: dict[str, dict]) -> str:
    snippets = truth.get("acceptable_rule_evidence") or []
    snippet_text = []
    for item in snippets[:8]:
        name = item.get("name") or item.get("rule_id")
        desc = (item.get("description") or "").strip()
        snippet_text.append(f"- {item.get('rule_id')} / {name}: {desc[:900]}")
    if not snippet_text:
        snippet_text.append("- 无标准规则片段；这是负例/歧义题。")

    matched = (run.get("deterministic") or {}).get("matched_ids") or []
    confirmed = []
    for cid in matched[:10]:
        detail = details.get(f"{run.get('game','')}::{cid}") or details.get(cid) or {}
        desc = (detail.get("description") or "").strip()
        structured = (detail.get("structured_evidence") or "").strip()
        name = detail.get("name") or ""
        if desc or structured:
            line = f"- {cid} / {name}: {desc[:700]}"
            if structured:
                line += f"\n  结构化正式证据: {structured[:1200]}"
            confirmed.append(line)
    confirmed_text = "\n".join(confirmed) if confirmed else "- 无额外已确认概念详情。"

    return f"""用户问题：
{run.get('question','')}

问题类型：{run.get('question_type','positive')}
期望行为：{run.get('expected_behavior','answer_from_rule')}
预期或可接受的目标规则 id：{json.dumps(run.get('expected_rule_ids') or [], ensure_ascii=False)}

可接受的标准规则证据片段：
{chr(10).join(snippet_text)}

本次工具已确认的概念详情（只作补充，不能证明回答必然面向用户问题）：
{confirmed_text}

待评审最终回答：
{run.get('reply','')}

请按以下 JSON 字段评分：
{{
  "answer_directness": 0,
  "rule_support": 0,
  "completeness": 0,
  "unsupported_or_contradictory": false,
  "unnecessary_clarification": false,
  "irrelevant_expansion": false,
  "player_count_inference": false,
  "pass": false,
  "reason": "一句话理由"
}}

评分解释：
- answer_directness：2=先直接回答问题；1=有绕行但能回答；0=没有回答/答非所问。
- rule_support：2=回答中的规则主张都有证据支持；1=部分支持；0=无支持或证据不足。
- completeness：2=问题核心被完整回答；1=部分回答；0=缺失。
- unsupported_or_contradictory：回答包含证据不支持、与证据矛盾或凭空补写的规则。
- unnecessary_clarification：反问用户已经说明的信息，或问了不影响当前结论的无关状态。
- irrelevant_expansion：主动展开未被问到的其他人数配置、另一阶段、另一种获得方式或后续计分。
- player_count_inference：把用户给出的组件数量反推/断言为具体玩家人数。
- 正例 pass 条件：answer_directness>=1，rule_support==2，completeness>=1，且四个 boolean 全为 false。
- 负例/歧义题：不要求命中规则；只要不编造规则，并明确表示查不到/需要澄清即可 pass；不要因为回答简短而扣分。
"""


def post_chat(base_url: str, api_key: str, model: str, prompt: str, timeout: int) -> dict:
    body = {
        "model": model,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        "temperature": 0,
    }
    req = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def parse_json_object(text: str) -> dict:
    text = (text or "").strip()
    if text.startswith("```"):
        text = re.sub(r"^```(?:json)?\s*", "", text)
        text = re.sub(r"\s*```$", "", text)
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end < start:
        raise ValueError(f"judge did not return a JSON object: {text[:300]}")
    return json.loads(text[start : end + 1])


def judge_one(run: dict, truth: dict, details: dict[str, dict], base_url: str, api_key: str,
              model: str, timeout: int) -> dict:
    prompt = build_prompt(run, truth, details)
    last_error = None
    for attempt in range(1, 4):
        try:
            raw = post_chat(base_url, api_key, model, prompt, timeout)
            content = raw["choices"][0]["message"]["content"]
            parsed = parse_json_object(content)
            return {
                "run_id": run["run_id"],
                "version": run["version"],
                "question_id": run["question_id"],
                "attempt": attempt,
                "judge_model": model,
                **parsed,
            }
        except Exception as exc:  # noqa: BLE001
            last_error = str(exc)
            time.sleep(1.5 * attempt)
    return {
        "run_id": run["run_id"],
        "version": run["version"],
        "question_id": run["question_id"],
        "attempt": 4,
        "judge_error": last_error,
        "pass": False,
        "answer_directness": 0,
        "rule_support": 0,
        "completeness": 0,
        "unsupported_or_contradictory": True,
        "unnecessary_clarification": False,
        "irrelevant_expansion": False,
        "player_count_inference": False,
        "reason": "judge_failed",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--ground-truth", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--rule-details", type=Path, default=None)
    parser.add_argument("--base-url", default=os.environ.get("LLM__BaseUrl", "https://api.deepseek.com/v1"))
    parser.add_argument("--model", default=os.environ.get("JUDGE_MODEL", "deepseek-v4-flash"))
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()

    api_key = os.environ.get("LLM__ApiKey") or os.environ.get("DEEPSEEK_API_KEY")
    if not api_key:
        raise SystemExit("Missing LLM__ApiKey or DEEPSEEK_API_KEY in environment")
    runs = [json.loads(line) for line in args.runs.read_text(encoding="utf-8").splitlines() if line.strip()]
    truth = json.loads(args.ground_truth.read_text(encoding="utf-8"))
    details = {}
    if args.rule_details and args.rule_details.exists():
        details = json.loads(args.rule_details.read_text(encoding="utf-8"))

    done = {}
    if args.resume and args.out.exists():
        for line in args.out.read_text(encoding="utf-8").splitlines():
            if line.strip():
                item = json.loads(line)
                done[item["run_id"]] = item
    pending = [run for run in runs if run["run_id"] not in done]
    if args.limit:
        pending = pending[: args.limit]
    args.out.parent.mkdir(parents=True, exist_ok=True)

    lock = threading.Lock()
    written = 0
    t0 = time.perf_counter()
    with args.out.open("a" if args.resume else "w", encoding="utf-8") as out_file, \
         concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = [
            pool.submit(judge_one, run, truth.get(run["question_id"], {}), details,
                        args.base_url, api_key, args.model, args.timeout)
            for run in pending
        ]
        for future in concurrent.futures.as_completed(futures):
            result = future.result()
            with lock:
                out_file.write(json.dumps(result, ensure_ascii=False) + "\n")
                out_file.flush()
                written += 1
            if written % 20 == 0:
                print(f"[{written}/{len(pending)}] judged, elapsed={time.perf_counter()-t0:.0f}s", flush=True)

    print(f"wrote {args.out} ({written} judged) elapsed={time.perf_counter()-t0:.0f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
