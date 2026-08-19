# -*- coding: utf-8 -*-
"""FAQ 问题类型第一遍自动聚类：按疑问词/句法模式打标签（可多标签），统计分布。"""
import io
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROWS = [json.loads(l) for l in Path("scripts/_faq_all.jsonl").read_text(encoding="utf-8").splitlines()]

# 类型信号：命中即打标签（多标签）
PATTERNS = {
    "数量": re.compile(r"多少|几个|几张|几块|几种|几艘|几格|几枚|几名|几次|数量|上限|上限是多少"),
    "时序": re.compile(r"何时|什么时候|哪一(?:阶段|步|轮|时刻)|先后|顺序|之后|之前|立即|同时|紧接着"),
    "许可": re.compile(r"能否|能不能|可以.{0,4}吗|是否允许|允许.{0,6}吗|能.{0,8}吗|可不可以"),
    "强制": re.compile(r"必须.{0,6}吗|一定要|强制|是否必须|必须选择"),
    "计分": re.compile(r"计分|得分|分数|算分|终局|获胜|赢"),
    "条件": re.compile(r"前置条件|前提|要求|需要什么|需要满足"),
    "位置": re.compile(r"相邻|连接|连通|距离|放在哪|哪里|何处|位置|相邻要求"),
    "定义": re.compile(r"是什么|什么是|什么区别|区别|定义|什么意思"),
    "方式": re.compile(r"怎么|如何|怎样"),
    "假设": re.compile(r"如果|假设|假如"),
}

stats = Counter()
per_type = defaultdict(list)
for r in ROWS:
    q = r["question"]
    tags = [t for t, p in PATTERNS.items() if p.search(q)]
    r["tags"] = tags or ["其他"]
    for t in tags or ["其他"]:
        stats[t] += 1
        per_type[t].append(r)

print(f"总题数 {len(ROWS)}\n")
for t, n in stats.most_common():
    print(f"{t}: {n}")
print()

# 各类型样例（最多 6 条）
for t, n in stats.most_common():
    print(f"\n{'=' * 70}\n[{t}] {n} 题")
    for r in per_type[t][:6]:
        print(f"  · {r['question'][:58]}")
