# -*- coding: utf-8 -*-
"""打印指定查询的分通道分数（TermScores/FullQueryScore），拆开向量与关键词贡献。"""
import io
import json
import re
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\bersh83xs.output")
RESULTS = Path(r"D:\workspace\board\scripts\_qa_agricola_results.jsonl")

# (题号, 实体) 关注点
WATCH = {(44, "烘焙"), (30, "职业牌"), (34, "计分"), (41, "主要改良行动"), (49, "乞讨卡 乞讨 负面卡 惩罚卡"),
         (16, "宠物"), (22, "翻修"), (26, "播种谷物")}


def brace_match(text: str, start: int) -> str | None:
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


def main():
    raw = LOG.read_text(encoding="utf-8", errors="replace")
    header_re = re.compile(r"\[Chat\] game: agricola, question: (.*?), count: \d+")
    segs: dict[str, list[tuple[int, int]]] = {}
    for m in header_re.finditer(raw):
        segs.setdefault(m.group(1), []).append((m.end(), raw.find("[Chat] game:", m.end())))
    for q, spans in segs.items():
        for i, (s, e) in enumerate(spans):
            if e == -1 or e < s:
                spans[i] = (s, len(raw))

    num_by_q = {}
    for line in RESULTS.read_text(encoding="utf-8").splitlines():
        r = json.loads(line)
        num_by_q[r["question"]] = r["num"]

    for q, spans in segs.items():
        num = num_by_q.get(q)
        if num is None:
            continue
        s, e = spans[-1]
        text = raw[s:e]
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
                ent = rq.get("Entity", "")
                if (num, ent) not in WATCH:
                    continue
                print(f"\n[{num}] {rq.get('Relation')} | {ent} | {rq.get('Status')}")
                for c in rq.get("Candidates") or []:
                    cid = c.get("Id") or c.get("id") or "?"
                    sc = c.get("Score")
                    ts = c.get("TermScores")
                    fq = c.get("FullQueryScore")
                    line = f"  {sc} {cid}"
                    if ts:
                        for t, ch in ts.items():
                            line += f" | {t}: V{ch.get('Vector')}/K{ch.get('Keyword')}"
                    if fq:
                        line += f" | full: V{fq.get('Vector')}/K{fq.get('Keyword')}"
                    print(line)


if __name__ == "__main__":
    main()
