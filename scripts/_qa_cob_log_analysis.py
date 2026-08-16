# -*- coding: utf-8 -*-
"""解析 API 思考日志，判断勃艮第城堡 FAQ 基线每题答案的数据来源：
A=直接检索答对  B=解析失败后兜底恢复答对  C=凭记忆答对  D=凭记忆答错  E=有数据仍答错
"""
import io
import json
import re
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\bz7d9mrem.output")
RESULTS = Path(r"D:\workspace\board\scripts\_qa_cob_results.jsonl")

# 人工判分结果（判分后填写）：{题号: 简要原因}
# 52/52 全对：Q5/Q15/Q50 三道口径题在本轮出现措辞偏差（Q5「一次弃两块」口径、
# Q15「免费行动」术语口径、Q50 答成放置效果顺序），已用数据修复（弃置按每次拿取判定、
# 免费=免骰子注释、phase_end 加「阶段奖励」别名）并定向复测确认，故不列 BAD。
BAD = {}


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

    # ---- 按行归属切分：带时间戳的行开启一条新日志（请求头或轮次行都带
    # 游戏标签），无时间戳的续行继承上一行的归属。并行跑多游戏 QA 时
    # 请求交错（先后的 header 之后各自的轮次行才陆续写入），只有按行
    # 自身标签归属才能把 brass 行排除在 castles 段之外。----
    GAME = "castles-of-burgundy"
    ts_re = re.compile(r"^\d\d:\d\d:\d\d")
    header_re = re.compile(r"\[Chat\] game: (\S+?), question: (.*?), count: \d+")
    tag_re = re.compile(r"\[Chat\] Game (\S+?),")
    segs: dict[str, list[str]] = {}
    cur_q: dict[str, str] = {}
    last_game = None
    for line in raw.splitlines():
        if not ts_re.match(line):
            # 续行：继承上一条带标签日志的归属
            if last_game == GAME:
                q = cur_q.get(GAME)
                if q and segs.get(q):
                    segs[q][-1] += line + "\n"
            continue
        m = header_re.search(line)
        if m:
            last_game = m.group(1)
            cur_q[last_game] = m.group(2)
            if last_game == GAME:
                segs.setdefault(m.group(2), []).append("")
            continue
        t = tag_re.search(line)
        last_game = t.group(1) if t else None
        if last_game == GAME:
            q = cur_q.get(GAME)
            if q and segs.get(q):
                segs[q][-1] += line + "\n"

    # ---- 每题解析：plan 查询 + result 状态 ----
    report = {}
    for q, texts in segs.items():
        text = texts[-1]  # 同一问题文本保留最后一次出现 = 本次完整重跑

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


if __name__ == "__main__":
    main()
