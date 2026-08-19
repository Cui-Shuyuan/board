# -*- coding: utf-8 -*-
"""调试 Q12 最新一轮：plan 与 execute_plan 结果 + 最终回复。"""
import json
import re
import sys

LOG = r"D:\Temp\claude\D--workspace-board\04b87d4b-f215-47f7-9e52-78a411e99c01\tasks\blk0m82zn.output"

sys.stdout.reconfigure(encoding="utf-8")
raw = open(LOG, encoding="utf-8", errors="replace").read()

BACKSLASH = chr(92)


def brace_match(text, start):
    depth = 0
    in_str = False
    esc = False
    for i in range(start, len(text)):
        c = text[i]
        if in_str:
            if esc:
                esc = False
            elif c == BACKSLASH:
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


segs = {}
cur = None
last = None
ts_re = re.compile(r"^\d\d:\d\d:\d\d")
header_re = re.compile(r"\[Chat\] game: (\S+?), question: (.*?), count: \d+")
tag_re = re.compile(r"\[Chat\] Game (\S+?),")
for line in raw.splitlines():
    if not ts_re.match(line):
        if last == "ark-nova" and cur and cur in segs:
            segs[cur][-1] += line + "\n"
        continue
    m = header_re.search(line)
    if m:
        last = m.group(1)
        if last == "ark-nova":
            cur = m.group(2)
            segs.setdefault(cur, []).append("")
        continue
    t = tag_re.search(line)
    last = t.group(1) if t else None

for q in list(segs):
    if "释放时" in q:
        text = segs[q][-1]
        print("=" * 20, q[:40])
        for pm in re.finditer(r"execute_plan\(\{", text):
            j = brace_match(text, pm.end() - 1)
            if j:
                plan = json.loads(j)
                print(" PLAN:", json.dumps(plan["plan"]["queries"], ensure_ascii=False))
        for rm in re.finditer(r"Tool execute_plan result:\s*", text):
            b = text.find("{", rm.end())
            j = brace_match(text, b)
            if j:
                res = json.loads(j)
                for rq in res.get("Results", []):
                    print(" RES:", rq.get("Relation"), "|", rq.get("Entity"), "|", rq.get("Status"),
                          "| matched:", [m.get("Id") for m in rq.get("Matched", [])][:8],
                          "| src:", rq.get("Source"), "| cands:", len(rq.get("Candidates") or []))
        # 该段里出现的关键数据文本（Matched 概念 JSON 中带「释放」的片段）
        for m in re.finditer(r'"zh": "[^"]*释放[^"]*"', text):
            s = m.group(0)
            if len(s) < 400:
                print(" ZH:", s[:200])
