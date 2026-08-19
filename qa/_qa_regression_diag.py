# -*- coding: utf-8 -*-
"""诊断回归题：打印每题 LLM 发送的查询 + 返回的候选/命中与分数（新日志）。"""
import io
import json
import re
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\bs4kxr4r5.output")
RESULTS = Path(r"D:\workspace\board\scripts\_qa_agricola_results.jsonl")
WANT = {4, 5, 6, 9, 13, 22, 23, 25, 31, 36, 44, 45, 47, 51, 54}


def brace_match(text: str, start: int):
    depth = 0
    in_str = False
    esc = False
    for i in range(start, len(text)):
        c = text[i]
        if in_str:
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                in_str = False
            continue
        if c == '"':
            in_str = True
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
    return None


raw = LOG.read_text(encoding="utf-8", errors="replace")
header_re = re.compile(r"\[Chat\] game: agricola, question: (.*?), count: \d+")
segs = {}
for m in header_re.finditer(raw):
    segs.setdefault(m.group(1), []).append((m.end(), raw.find("[Chat] game:", m.end())))
for q, spans in segs.items():
    for i, (s, e) in enumerate(spans):
        if e == -1 or e < s:
            spans[i] = (s, len(raw))

ans = {}
for line in RESULTS.read_text(encoding="utf-8").splitlines():
    r = json.loads(line)
    ans[r["question"]] = r["num"]

for q, spans in segs.items():
    num = ans.get(q)
    if num not in WANT:
        continue
    s, e = spans[-1]
    text = raw[s:e]
    print(f"===== Q{num}: {q[:46]}")
    for pm in re.finditer(r"execute_plan\(\{", text):
        j = brace_match(text, pm.end() - 1)
        if not j:
            continue
        try:
            plan = json.loads(j)
        except Exception:
            continue
        for qq in plan.get("plan", {}).get("queries", []):
            print(f"  query: rel={qq.get('relation')} ent={qq.get('entity')!r}")
    for rm in re.finditer(r"Tool execute_plan result:\s*", text):
        b = text.find("{", rm.end())
        if b == -1:
            continue
        j = brace_match(text, b)
        if not j:
            continue
        try:
            res = json.loads(j)
        except Exception:
            continue
        for rq in res.get("Results", []):
            matched = [(m.get("Id"), round(m.get("Score", 0), 3)) for m in (rq.get("Matched") or [])]
            cands = [(c.get("Id"), round(c.get("Score", 0), 3)) for c in (rq.get("Candidates") or [])][:8]
            print(f"  result: rel={rq.get('Relation')} ent={rq.get('Entity')!r} status={rq.get('Status')} matched={matched}")
            print(f"     cands: {cands}")
