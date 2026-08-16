# -*- coding: utf-8 -*-
"""诊断农场主 QA 基线 C 类（凭记忆）题目：打印每题每轮的 plan 查询与 result 状态，
找出为什么没有命中数据。
"""
import io
import json
import re
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

    for q, spans in segs.items():
        num = num_by_q.get(q)
        if num not in C_NUMS:
            continue
        print(f"\n{'=' * 90}\n[{num}] {q}")
        s, e = spans[-1]
        text = raw[s:e]
        round_blocks = re.split(r"Round (\d+) reasoning|Round (\d+) tool calls", text)
        cur_round = None
        for piece in round_blocks:
            if piece and piece.isdigit():
                cur_round = int(piece)
                continue
            if piece is None or cur_round is None:
                continue
            for pm in re.finditer(r"execute_plan\(\{", piece):
                j = brace_match(piece, pm.end() - 1)
                if not j:
                    continue
                try:
                    plan = json.loads(j)
                except Exception:
                    continue
                for qq in plan.get("plan", {}).get("queries", []):
                    print(f"  R{cur_round} 查询: {qq.get('relation', '')} | {qq.get('entity', '')}")
            for rm in re.finditer(r"Tool execute_plan result:\s*", piece):
                b = piece.find("{", rm.end())
                if b == -1:
                    continue
                j = brace_match(piece, b)
                if not j:
                    continue
                try:
                    res = json.loads(j)
                except Exception:
                    continue
                for rq in res.get("Results", []):
                    n = len(rq.get("Candidates") or [])
                    cand_txt = ""
                    if rq.get("Status") == "ok" and rq.get("Candidates"):
                        cand_txt = " | " + ", ".join(
                            f"{c.get('Id', '?')}({round(c.get('Score', 0), 2)})"
                            for c in rq["Candidates"][:3])
                    print(f"  R{cur_round} 结果: {rq.get('Relation', '')} | {rq.get('Entity', '')} | "
                          f"{rq.get('Status', '')} | 候选{n}{cand_txt}")


if __name__ == "__main__":
    main()
