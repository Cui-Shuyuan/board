# -*- coding: utf-8 -*-
"""统计：237 题 FAQ 中，问题文本直接包含概念名（zh/en）的比例。
1 命中 = 零歧义直呼类；≥2 命中 = 需要消歧；0 命中 = 必须语义检索。
"""
import io
import json
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BOARD = Path(__file__).resolve().parent.parent

# ---- 概念词典（所有游戏 + 本体）----
names = {}  # name -> concept_id（zh/en 分开存）
for path in [BOARD / "ontology" / "concepts.json"] + sorted((BOARD / "games").glob("*/concepts.json")):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    for arr in ["concepts", "objects", "actions", "triggers", "conditions"]:
        for c in data.get(arr, []):
            n = c.get("name") or {}
            if n.get("zh"):
                names.setdefault(n["zh"], c.get("id"))
            if n.get("en"):
                names.setdefault(n["en"].lower(), c.get("id"))

# 长度过滤：太短的通用词（1 字）不算命中，避免「田」「船」这类噪声
CAND = {n: cid for n, cid in names.items() if len(n) >= 2 or (len(n) == 1 and n in ("房", "栅", "篱"))}

rows = [json.loads(l) for l in (BOARD / "scripts" / "_faq_all.jsonl").read_text(encoding="utf-8").splitlines()]

hit0, hit1, hitN = [], [], []
for r in rows:
    q = r["question"].lower()
    hits = [cid for n, cid in CAND.items() if n.lower() in q]
    uniq = list(dict.fromkeys(hits))
    r["hits"] = uniq
    (hit0, hit1, hitN)[0 if not uniq else 1 if len(uniq) == 1 else 2].append(r)

print(f"概念词典 {len(CAND)} 词（zh+en，长度≥2）\n")
print(f"0 命中（必须语义检索）: {len(hit0)}")
print(f"1 命中（零歧义直呼）:   {len(hit1)}")
print(f"≥2 命中（需消歧）:      {len(hitN)}")

print(f"\n===== 单命中样例（前 25）=====")
for r in hit1[:25]:
    print(f"  [{r['game'][:9]}] {r['question'][:52]}  -> {r['hits']}")

print(f"\n===== 多命中样例（前 15）=====")
for r in hitN[:15]:
    print(f"  [{r['game'][:9]}] {r['question'][:52]}  -> {r['hits'][:4]}")
