# -*- coding: utf-8 -*-
"""从农场主 QA 日志提取每次 execute_plan 的候选分数：
统计 top1/top2 差距分布，判断排序是否有意义；并对 15 个 C 类题打印完整候选列表。
"""
import io
import json
import re
import statistics
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\b3f83xyhc.output")
RESULTS = Path(r"D:\workspace\board\scripts\_qa_agricola_results.jsonl")

C_NUMS = {9, 16, 22, 23, 24, 26, 30, 34, 37, 41, 44, 45, 49, 51, 54}


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

    # ---- 收集所有查询事件 ----
    events = []  # (num, relation, entity, status, [(id, score)...])
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
                cands = []
                for c in rq.get("Candidates") or []:
                    cid = c.get("Id") or c.get("id") or "?"
                    cands.append((cid, c.get("Score") or 0))
                cands.sort(key=lambda x: -x[1])
                if cands:
                    events.append((num, rq.get("Relation", ""), rq.get("Entity", ""),
                                   rq.get("Status", ""), cands))

    # ---- 全局统计 ----
    print(f"共 {len(events)} 次带候选的检索事件\n")
    top1s = [e[4][0][1] for e in events]
    gaps = [e[4][0][1] - e[4][1][1] for e in events if len(e[4]) > 1]
    print("== top1 分数分布 ==")
    print(f"  均值 {statistics.mean(top1s):.3f}  中位 {statistics.median(top1s):.3f}  "
          f"最大 {max(top1s):.3f}  最小 {min(top1s):.3f}")
    for lo, hi in [(0, 0.5), (0.5, 0.6), (0.6, 0.7), (0.7, 0.8), (0.8, 1.1)]:
        n = sum(1 for x in top1s if lo <= x < hi)
        print(f"  [{lo:.1f}, {hi:.1f}): {n}")
    print(f"  >=0.50 (阈值): {sum(1 for x in top1s if x >= 0.5)}/{len(top1s)}")
    print("\n== top1-top2 差距 ==")
    print(f"  均值 {statistics.mean(gaps):.3f}  中位 {statistics.median(gaps):.3f}  "
          f"最大 {max(gaps):.3f}")
    print(f"  差距<0.02: {sum(1 for g in gaps if g < 0.02)}/{len(gaps)}  "
          f"<0.05: {sum(1 for g in gaps if g < 0.05)}/{len(gaps)}  "
          f"<0.10: {sum(1 for g in gaps if g < 0.10)}/{len(gaps)}")

    # ---- C 类题目完整候选 ----
    print("\n" + "=" * 100)
    print("C 类题目的全部候选列表（* = top1）")
    for num, rel, ent, status, cands in events:
        if num not in C_NUMS:
            continue
        print(f"\n[{num}] {rel} | {ent} | {status}")
        for i, (cid, sc) in enumerate(cands):
            mark = "*" if i == 0 else " "
            print(f"  {mark} {sc:.3f} {cid}")


if __name__ == "__main__":
    main()
