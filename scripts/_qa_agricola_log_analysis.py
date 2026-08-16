# -*- coding: utf-8 -*-
"""解析 API 思考日志，判断农场主 FAQ 基线每题答案的数据来源：
A=直接检索答对  B=解析失败后兜底恢复答对  C=凭记忆答对  D=凭记忆答错  E=有数据仍答错
"""
import io
import json
import re
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\b6f37vvzf.output")
RESULTS = Path(r"D:\workspace\board\scripts\_qa_agricola_results.jsonl")

# 人工判分结果（判分后填写）：{题号: 简要原因}
# 48: 误读问题——答成行动格每回合只能占一次，未答「前一个成员拿到的资源同一回合可立即供后一个成员使用」
# 53: 答错——声称起始玩家标记每轮自动传递；数据明确「不会自动轮换，只有 Meeting Place 可夺取」
# 12/37: FAQ 答案本身有误（12 繁殖阶段不可烹饪；37 房间计分木0/泥1/石2），模型按规则书答对 → 不算错
BAD = {48: "误读问题（资源误当行动格）", 53: "起始玩家不会自动轮换"}


def brace_match(text: str, start: int) -> str | None:
    """从 start 处的 '{' 做花括号配对，返回子串。字符串感知——忽略字符串内的括号。"""
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

    # ---- 按请求头切分（同一问题文本保留最后一次出现 = 本次完整重跑）----
    header_re = re.compile(r"\[Chat\] game: agricola, question: (.*?), count: \d+")
    segs: dict[str, list[tuple[int, int]]] = {}
    for m in header_re.finditer(raw):
        segs.setdefault(m.group(1), []).append((m.end(), raw.find("[Chat] game:", m.end())))
    for q, spans in segs.items():
        for i, (s, e) in enumerate(spans):
            if e == -1 or e < s:
                spans[i] = (s, len(raw))

    # ---- 每题解析：plan 查询 + result 状态 ----
    report = {}
    for q, spans in segs.items():
        s, e = spans[-1]
        text = raw[s:e]

        plan_queries = []
        result_queries = []
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
                    plan_queries.append((cur_round, qq.get("relation", ""), qq.get("entity", "")))
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
                    matched_ids = [m.get("Id") or m.get("id") or "" for m in rq.get("Matched", []) if m.get("Id") or m.get("id")]
                    result_queries.append((
                        cur_round, rq.get("Relation", ""), rq.get("Entity", ""),
                        rq.get("Status", ""), matched_ids,
                        len(rq.get("Candidates") or []), rq.get("Catalog") is not None,
                        rq.get("Source", ""),
                    ))
        report[q] = {"plan": plan_queries, "results": result_queries}

    # ---- 读答案编号 ----
    answers = {}
    for line in RESULTS.read_text(encoding="utf-8").splitlines():
        r = json.loads(line)
        answers[r["question"]] = r["num"]

    # ---- 分类 ----
    stats = {"A": 0, "B": 0, "C": 0, "D": 0, "E": 0, "?": 0}
    rows = []
    for q, info in report.items():
        num = answers.get(q)
        if num is None:
            continue
        res = info["results"]
        ok_content = [r for r in res if r[3] == "ok" and r[4]]
        unresolved = [r for r in res if r[3] == "unresolved"]
        used_list = any(r[6] for r in res)
        rounds = max((r[0] for r in res), default=0) or max((p[0] for p in info["plan"]), default=0)
        correct = num not in BAD

        if correct:
            if ok_content:
                cat = "A"
            elif unresolved and used_list:
                cat = "B"
            else:
                cat = "C"
        else:
            if ok_content:
                cat = "E"
            else:
                cat = "D"
        stats[cat] += 1
        matched_names = sorted({m for r in ok_content for m in r[4]})
        rows.append((num, cat, rounds, len(ok_content), len(unresolved), used_list, matched_names))

    rows.sort(key=lambda x: x[0])
    print(f"{'Q':>3} {'类':>2} {'轮':>2} {'OK':>2} {'未解':>2} {'list':>4} 命中的概念")
    for num, cat, rounds, nok, nun, ul, names in rows:
        print(f"{num:>3} {cat:>2} {rounds:>2} {nok:>2} {nun:>2} {'是' if ul else '':>4} {', '.join(names)[:88]}")
    print()
    print("A 直接检索答对:", stats["A"])
    print("B 解析失败兜底恢复答对:", stats["B"])
    print("C 凭记忆答对:", stats["C"])
    print("D 凭记忆答错:", stats["D"])
    print("E 有数据仍错:", stats["E"])
    print("未匹配:", stats["?"])

    # ---- 拍板来源统计（程序自己拍板 vs 交给 LLM）----
    src_stats = {}
    for q, info in report.items():
        for r in info["results"]:
            if r[3] == "ok":
                src = r[7] or "llm_picked"
                src_stats[src] = src_stats.get(src, 0) + 1
    print("\n拍板来源（ok 结果中）:")
    total = sum(src_stats.values())
    for k, v in sorted(src_stats.items(), key=lambda x: -x[1]):
        print(f"  {k}: {v} ({v * 100 // max(total, 1)}%)")


if __name__ == "__main__":
    main()
