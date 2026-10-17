#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Analyze a paired old/new run and emit JSON + Markdown summaries."""
from __future__ import annotations

import argparse
import json
import math
import random
import statistics
from collections import Counter, defaultdict
from pathlib import Path


def percentile(values, p: float) -> float:
    if not values:
        return 0.0
    values = sorted(values)
    if len(values) == 1:
        return values[0]
    index = (len(values) - 1) * p
    low = int(math.floor(index))
    high = int(math.ceil(index))
    if low == high:
        return values[low]
    return values[low] * (high - index) + values[high] * (index - low)


def bootstrap_mean_ci(values: list[float], seed: int = 20261017, n: int = 10000) -> tuple[float, float]:
    if not values:
        return 0.0, 0.0
    rng = random.Random(seed)
    means = []
    for _ in range(n):
        means.append(sum(values[rng.randrange(len(values))] for _ in range(len(values))) / len(values))
    means.sort()
    return means[int(0.025 * n)], means[int(0.975 * n) - 1]


def mcnemar_exact(old_success: list[bool], new_success: list[bool]) -> dict:
    old_only = sum(1 for a, b in zip(old_success, new_success) if a and not b)
    new_only = sum(1 for a, b in zip(old_success, new_success) if b and not a)
    n = old_only + new_only
    if n == 0:
        p = 1.0
    else:
        k = min(old_only, new_only)
        tail = sum(math.comb(n, i) for i in range(k + 1)) / (2 ** n)
        p = min(1.0, 2 * tail)
    return {"old_only": old_only, "new_only": new_only, "discordant": n, "p_value": p}


def bool_mean(values):
    return round(sum(1 for x in values if x) / len(values), 4) if values else 0.0


def summarize_runs(runs: list[dict], judged: dict[str, dict]) -> dict:
    merged = []
    for run in runs:
        j = judged.get(run["run_id"], {})
        merged.append({**run, "judge": j})
    pos = [r for r in merged if r.get("question_type") == "positive"]
    neg = [r for r in merged if r.get("question_type") != "positive"]

    def summary(rows):
        pass_flags = [bool(r["judge"].get("pass")) for r in rows]
        lat = [r["latency_ms"] for r in rows]
        direct = [bool((r.get("deterministic") or {}).get("direct_expected_hit")) for r in rows if r.get("question_type") == "positive"]
        top3 = [bool((r.get("deterministic") or {}).get("candidate_expected_top3") or (r.get("deterministic") or {}).get("direct_expected_hit")) for r in rows if r.get("question_type") == "positive"]
        tiers = Counter((r.get("deterministic") or {}).get("tier") for r in rows)
        outcomes = Counter((r.get("deterministic") or {}).get("orchestration_outcome") for r in rows if (r.get("deterministic") or {}).get("orchestration_outcome"))
        model_calls = [x for x in ((r.get("deterministic") or {}).get("model_call_count") for r in rows) if x is not None]
        supplements = [x for x in ((r.get("deterministic") or {}).get("supplement_count") for r in rows) if x is not None]
        repairs = [x for x in ((r.get("deterministic") or {}).get("repair_count") for r in rows) if x is not None]
        read_counts = [x for x in ((r.get("deterministic") or {}).get("evidence_read_count") for r in rows) if x is not None]
        understand_ms = [x for x in ((r.get("deterministic") or {}).get("understand_ms") for r in rows) if x is not None]
        retrieval_ms = [x for x in ((r.get("deterministic") or {}).get("retrieval_ms") for r in rows) if x is not None]
        respond_ms = [x for x in ((r.get("deterministic") or {}).get("respond_ms") for r in rows) if x is not None]
        pack_chars = [x for x in (((r.get("orchestration") or {}).get("evidencePackChars")) for r in rows) if x is not None]
        return {
            "n": len(rows),
            "pass_rate": bool_mean(pass_flags),
            "judge_error_count": sum(1 for r in rows if r["judge"].get("judge_error")),
            "http_error_count": sum(1 for r in rows if r.get("http_status") != 200),
            "mean_directness": round(statistics.mean([r["judge"].get("answer_directness", 0) for r in rows]), 3) if rows else 0,
            "mean_rule_support": round(statistics.mean([r["judge"].get("rule_support", 0) for r in rows]), 3) if rows else 0,
            "mean_completeness": round(statistics.mean([r["judge"].get("completeness", 0) for r in rows]), 3) if rows else 0,
            "unsupported_rate": bool_mean([r["judge"].get("unsupported_or_contradictory") for r in rows]),
            "unnecessary_clarification_rate": bool_mean([r["judge"].get("unnecessary_clarification") for r in rows]),
            "irrelevant_expansion_rate": bool_mean([r["judge"].get("irrelevant_expansion") for r in rows]),
            "player_count_inference_rate": bool_mean([r["judge"].get("player_count_inference") for r in rows]),
            "direct_expected_hit_rate_positive": bool_mean(direct),
            "recoverable_top3_rate_positive": bool_mean(top3),
            "tier1_rate": round(tiers.get("tier1", 0) / len(rows), 4) if rows else 0,
            "pending_zero_rate": bool_mean([(r.get("deterministic") or {}).get("pending_count") == 0 for r in rows]),
            "mean_query_count": round(statistics.mean([(r.get("deterministic") or {}).get("query_count") or 0 for r in rows]), 3) if rows else 0,
            "p50_latency_ms": round(percentile(lat, .5), 1),
            "p95_latency_ms": round(percentile(lat, .95), 1),
            "model_call_mean": round(statistics.mean(model_calls), 3) if model_calls else None,
            "model_call_p50": round(statistics.median(model_calls), 3) if model_calls else None,
            "supplement_mean": round(statistics.mean(supplements), 3) if supplements else None,
            "repair_mean": round(statistics.mean(repairs), 3) if repairs else None,
            "evidence_read_mean": round(statistics.mean(read_counts), 3) if read_counts else None,
            "understand_ms_mean": round(statistics.mean(understand_ms), 1) if understand_ms else None,
            "retrieval_ms_mean": round(statistics.mean(retrieval_ms), 1) if retrieval_ms else None,
            "respond_ms_mean": round(statistics.mean(respond_ms), 1) if respond_ms else None,
            "program_outcome_counts": dict(outcomes),
            "answer_rate": round(outcomes.get("answer", 0) / len(rows), 4) if rows else 0,
            "clarify_rate": round(outcomes.get("clarify", 0) / len(rows), 4) if rows else 0,
            "unavailable_rate": round(outcomes.get("unavailable", 0) / len(rows), 4) if rows else 0,
            "conversation_rate": round(outcomes.get("conversation", 0) / len(rows), 4) if rows else 0,
            "supplement_nonzero_rate": bool_mean([(x or 0) > 0 for x in supplements]) if supplements else None,
            "repair_nonzero_rate": bool_mean([(x or 0) > 0 for x in repairs]) if repairs else None,
            "evidence_pack_chars_mean": round(statistics.mean(pack_chars), 1) if pack_chars else None,
            "evidence_pack_chars_p95": round(percentile(pack_chars, .95), 1) if pack_chars else None,
        }

    return {"all": summary(merged), "positive": summary(pos), "negative": summary(neg), "merged": merged}


def paired_analysis(merged: list[dict]) -> dict:
    by_q_old = defaultdict(list)
    by_q_new = defaultdict(list)
    for run in merged:
        key = (run["question_id"], run["version"])
        target = by_q_old if run["version"] == "old" else by_q_new
        target[run["question_id"]].append(run)

    question_ids = sorted(set(by_q_old) | set(by_q_new))
    old_majority, new_majority = [], []
    old_delta_vals, new_delta_vals = [], []
    lat_deltas = []
    query_deltas = []
    per_question = []
    for qid in question_ids:
        old_rows, new_rows = by_q_old.get(qid, []), by_q_new.get(qid, [])
        if not old_rows or not new_rows:
            continue
        old_pass = sum(bool(r["judge"].get("pass")) for r in old_rows) >= (len(old_rows) / 2)
        new_pass = sum(bool(r["judge"].get("pass")) for r in new_rows) >= (len(new_rows) / 2)
        old_majority.append(old_pass)
        new_majority.append(new_pass)
        old_delta_vals.append(sum(bool(r["judge"].get("pass")) for r in old_rows) / len(old_rows))
        new_delta_vals.append(sum(bool(r["judge"].get("pass")) for r in new_rows) / len(new_rows))
        lat_deltas.append(statistics.mean([r["latency_ms"] for r in new_rows]) - statistics.mean([r["latency_ms"] for r in old_rows]))
        query_deltas.append(statistics.mean([(r.get("deterministic") or {}).get("query_count") or 0 for r in new_rows]) - statistics.mean([(r.get("deterministic") or {}).get("query_count") or 0 for r in old_rows]))
        per_question.append({
            "question_id": qid,
            "old_pass_majority": old_pass,
            "new_pass_majority": new_pass,
            "old_pass_rate": round(old_delta_vals[-1], 3),
            "new_pass_rate": round(new_delta_vals[-1], 3),
            "delta": round(new_delta_vals[-1] - old_delta_vals[-1], 3),
        })

    delta_values = [n - o for o, n in zip(old_delta_vals, new_delta_vals)]
    ci_low, ci_high = bootstrap_mean_ci(delta_values)
    return {
        "n_questions": len(question_ids),
        "old_majority_pass_rate": bool_mean(old_majority),
        "new_majority_pass_rate": bool_mean(new_majority),
        "delta_majority": round(bool_mean(new_majority) - bool_mean(old_majority), 4),
        "mean_run_pass_rate_delta": round(statistics.mean(delta_values), 4) if delta_values else 0,
        "mean_run_pass_rate_delta_ci95": [round(ci_low, 4), round(ci_high, 4)],
        "mcnemar": mcnemar_exact(old_majority, new_majority),
        "latency_delta_ms_mean": round(statistics.mean(lat_deltas), 1) if lat_deltas else 0,
        "latency_delta_ms_median": round(statistics.median(lat_deltas), 1) if lat_deltas else 0,
        "query_count_delta_mean": round(statistics.mean(query_deltas), 3) if query_deltas else 0,
        "per_question": per_question,
    }


def summarize_groups(merged: list[dict], key: str) -> list[dict]:
    groups = defaultdict(lambda: defaultdict(list))
    for r in merged:
        groups[r.get(key, "")][r["version"]].append(r)
    rows = []
    for name in sorted(groups):
        row = {"name": name}
        for version in ("old", "new"):
            rs = groups[name].get(version, [])
            row[f"{version}_n"] = len(rs)
            row[f"{version}_pass_rate"] = bool_mean([bool(r["judge"].get("pass")) for r in rs])
            row[f"{version}_p50_latency_ms"] = round(percentile([r["latency_ms"] for r in rs], .5), 1) if rs else 0
        row["delta_pass_rate"] = round(row.get("new_pass_rate", 0) - row.get("old_pass_rate", 0), 4)
        rows.append(row)
    return rows


def write_markdown(analysis: dict, path: Path):
    old_all = analysis["versions"]["old"]["all"]
    new_all = analysis["versions"]["new"]["all"]
    pair = analysis["paired"]
    lines = [
        "# 新旧版本全面对比：自动分析摘要",
        "",
        "## 总体",
        "",
        "| 指标 | 旧版 | 新版 | 差值 |",
        "|---|---:|---:|---:|",
        f"| 严格回答通过率 | {old_all['pass_rate']:.1%} | {new_all['pass_rate']:.1%} | {(new_all['pass_rate']-old_all['pass_rate'])*100:+.1f}pp |",
        f"| 错误/超时 | {old_all['http_error_count']} | {new_all['http_error_count']} | {new_all['http_error_count']-old_all['http_error_count']:+d} |",
        f"| 错误规则/编造率 | {old_all['unsupported_rate']:.1%} | {new_all['unsupported_rate']:.1%} | {(new_all['unsupported_rate']-old_all['unsupported_rate'])*100:+.1f}pp |",
        f"| 不必要追问率 | {old_all['unnecessary_clarification_rate']:.1%} | {new_all['unnecessary_clarification_rate']:.1%} | {(new_all['unnecessary_clarification_rate']-old_all['unnecessary_clarification_rate'])*100:+.1f}pp |",
        f"| 无关扩展率 | {old_all['irrelevant_expansion_rate']:.1%} | {new_all['irrelevant_expansion_rate']:.1%} | {(new_all['irrelevant_expansion_rate']-old_all['irrelevant_expansion_rate'])*100:+.1f}pp |",
        f"| 人数反推率 | {old_all['player_count_inference_rate']:.1%} | {new_all['player_count_inference_rate']:.1%} | {(new_all['player_count_inference_rate']-old_all['player_count_inference_rate'])*100:+.1f}pp |",
        f"| 正例直接命中率 | {old_all['direct_expected_hit_rate_positive']:.1%} | {new_all['direct_expected_hit_rate_positive']:.1%} | {(new_all['direct_expected_hit_rate_positive']-old_all['direct_expected_hit_rate_positive'])*100:+.1f}pp |",
        f"| 正例直达或候选前三 | {old_all['recoverable_top3_rate_positive']:.1%} | {new_all['recoverable_top3_rate_positive']:.1%} | {(new_all['recoverable_top3_rate_positive']-old_all['recoverable_top3_rate_positive'])*100:+.1f}pp |",
        f"| tier1 率 | {old_all['tier1_rate']:.1%} | {new_all['tier1_rate']:.1%} | {(new_all['tier1_rate']-old_all['tier1_rate'])*100:+.1f}pp |",
        f"| pending=0 率 | {old_all['pending_zero_rate']:.1%} | {new_all['pending_zero_rate']:.1%} | {(new_all['pending_zero_rate']-old_all['pending_zero_rate'])*100:+.1f}pp |",
        f"| 平均 query 数 | {old_all['mean_query_count']:.2f} | {new_all['mean_query_count']:.2f} | {new_all['mean_query_count']-old_all['mean_query_count']:+.2f} |",
        f"| 延迟中位数 | {old_all['p50_latency_ms']:.0f} ms | {new_all['p50_latency_ms']:.0f} ms | {new_all['p50_latency_ms']-old_all['p50_latency_ms']:+.0f} ms |",
        f"| 延迟 P95 | {old_all['p95_latency_ms']:.0f} ms | {new_all['p95_latency_ms']:.0f} ms | {new_all['p95_latency_ms']-old_all['p95_latency_ms']:+.0f} ms |",
        "",
        "## 程序控制编排指标（新版专属）",
        "",
        f"- 结果类型分布：{new_all.get('program_outcome_counts') or 'N/A'}",
        f"- 平均模型调用数 / 中位数：{new_all.get('model_call_mean') if new_all.get('model_call_mean') is not None else 'N/A'} / {new_all.get('model_call_p50') if new_all.get('model_call_p50') is not None else 'N/A'}",
        f"- 平均补充检索次数：{new_all.get('supplement_mean') if new_all.get('supplement_mean') is not None else 'N/A'}；触发补充比例：{new_all.get('supplement_nonzero_rate') if new_all.get('supplement_nonzero_rate') is not None else 'N/A'}",
        f"- 平均协议修复次数：{new_all.get('repair_mean') if new_all.get('repair_mean') is not None else 'N/A'}；触发修复比例：{new_all.get('repair_nonzero_rate') if new_all.get('repair_nonzero_rate') is not None else 'N/A'}",
        f"- 平均事实包字符数 / P95：{new_all.get('evidence_pack_chars_mean') if new_all.get('evidence_pack_chars_mean') is not None else 'N/A'} / {new_all.get('evidence_pack_chars_p95') if new_all.get('evidence_pack_chars_p95') is not None else 'N/A'}",
        f"- 平均 understand / retrieval / respond 阶段耗时：{new_all.get('understand_ms_mean') if new_all.get('understand_ms_mean') is not None else 'N/A'} ms / {new_all.get('retrieval_ms_mean') if new_all.get('retrieval_ms_mean') is not None else 'N/A'} ms / {new_all.get('respond_ms_mean') if new_all.get('respond_ms_mean') is not None else 'N/A'} ms",
        "",
        "## 配对分析",
        "",
        f"- 可配对问题数：{pair['n_questions']}",
        f"- 各问多数票通过率：旧版 {pair['old_majority_pass_rate']:.1%}，新版 {pair['new_majority_pass_rate']:.1%}，差值 {pair['delta_majority']*100:+.1f}pp",
        f"- 每次运行通过率差值均值：{pair['mean_run_pass_rate_delta']*100:+.1f}pp，问题级 Bootstrap 95% CI [{pair['mean_run_pass_rate_delta_ci95'][0]*100:+.1f}, {pair['mean_run_pass_rate_delta_ci95'][1]*100:+.1f}]pp",
        f"- McNemar 精确检验：旧版单独通过 {pair['mcnemar']['old_only']}，新版单独通过 {pair['mcnemar']['new_only']}，p={pair['mcnemar']['p_value']:.4f}",
        f"- 配对延迟差：均值 {pair['latency_delta_ms_mean']:+.0f} ms，中位数 {pair['latency_delta_ms_median']:+.0f} ms",
        f"- 配对 query 数差：均值 {pair['query_count_delta_mean']:+.2f}",
        "",
        "## 分游戏",
        "",
        "| 游戏 | 旧版通过率 | 新版通过率 | 差值 | 旧版中位延迟 | 新版中位延迟 |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for row in analysis["by_game"]:
        lines.append(
            f"| {row['name']} | {row.get('old_pass_rate',0):.1%} | {row.get('new_pass_rate',0):.1%} | "
            f"{row.get('delta_pass_rate',0)*100:+.1f}pp | {row.get('old_p50_latency_ms',0):.0f} ms | {row.get('new_p50_latency_ms',0):.0f} ms |"
        )
    lines += [
        "",
        "## 分问题类别",
        "",
        "| 类别 | 旧版通过率 | 新版通过率 | 差值 |",
        "|---|---:|---:|---:|",
    ]
    for row in analysis["by_category"]:
        lines.append(
            f"| {row['name']} | {row.get('old_pass_rate',0):.1%} | {row.get('new_pass_rate',0):.1%} | {row.get('delta_pass_rate',0)*100:+.1f}pp |"
        )
    lines += [
        "",
        "## 分数据切片",
        "",
        "| 切片 | 旧版通过率 | 新版通过率 | 差值 |",
        "|---|---:|---:|---:|",
    ]
    for row in analysis.get("by_split", []):
        lines.append(
            f"| {row['name']} | {row.get('old_pass_rate',0):.1%} | {row.get('new_pass_rate',0):.1%} | {row.get('delta_pass_rate',0)*100:+.1f}pp |"
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--judge", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, required=True)
    args = parser.parse_args()

    runs = [json.loads(line) for line in args.runs.read_text(encoding="utf-8").splitlines() if line.strip()]
    judged = {}
    for line in args.judge.read_text(encoding="utf-8").splitlines():
        if line.strip():
            item = json.loads(line)
            judged[item["run_id"]] = item

    summaries = {version: summarize_runs([r for r in runs if r["version"] == version], judged)
                 for version in ("old", "new")}
    merged = summaries["old"]["merged"] + summaries["new"]["merged"]
    analysis = {
        "versions": summaries,
        "paired": paired_analysis(merged),
        "by_game": summarize_groups(merged, "game"),
        "by_category": summarize_groups(merged, "category"),
        "by_split": summarize_groups(merged, "split"),
    }
    args.out_dir.mkdir(parents=True, exist_ok=True)
    (args.out_dir / "analysis.json").write_text(json.dumps(analysis, ensure_ascii=False, indent=2), encoding="utf-8")
    write_markdown(analysis, args.out_dir / "summary.md")
    print(json.dumps({v: analysis["versions"][v]["all"] for v in ("old", "new")}, ensure_ascii=False, indent=2))
    print(f"wrote {args.out_dir / 'analysis.json'} and summary.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
