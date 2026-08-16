# -*- coding: utf-8 -*-
"""验证「直呼类」工具的可行性：
策略 = 最长名字优先匹配（嵌套名字只取最长）+ 只看游戏概念。
Ground truth = agricola QA（fp32 run2）里每题 LLM 实际命中的概念。
正确 = 工具命中的概念出现在 ground truth 里；错误 = 不在（工具必须闭嘴）。
"""
import io
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BOARD = Path(__file__).resolve().parent.parent

# ---- 词典：只收游戏概念（排除 ontology 通用概念）----
game_names = {}  # name -> concept_id
for path in sorted((BOARD / "games").glob("*/concepts.json")):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    for arr in ["concepts", "objects", "actions", "triggers", "conditions"]:
        for c in data.get(arr, []):
            n = c.get("name") or {}
            if n.get("zh") and len(n["zh"]) >= 2:
                game_names[n["zh"]] = c.get("id")
            if n.get("en") and len(n["en"]) >= 2:
                game_names.setdefault(n["en"].lower(), c.get("id"))
print(f"游戏概念词典 {len(game_names)} 词\n")


def direct_hit(q: str):
    """最长名字优先：找出问题文本中包含的所有概念名，嵌套时只保留最长的。"""
    ql = q.lower()
    hits = {}  # span -> (name, cid, end)
    for n, cid in game_names.items():
        start = ql.find(n.lower())
        while start != -1:
            end = start + len(n)
            # 同一位置只保留最长的名字
            if end > hits.get(start, ("", "", 0))[2]:
                hits[start] = (n, cid, end)
            start = ql.find(n.lower(), end)
    # 去掉被更长命中覆盖的区间（区间去重：end 小的、start 在已保留区间内的丢弃）
    keep = []
    taken_until = -1
    for start, (n, cid, end) in sorted(hits.items(), key=lambda kv: (-kv[1][2], kv[0])):
        if start < taken_until:
            continue  # 被更长的名字覆盖
        keep.append((start, n, cid))
        taken_until = end
    return [(n, cid) for _, n, cid in keep]


# ---- ground truth：从 fp32 run2 的 QA 日志解析每题命中的概念 ----
LOG = Path(r"D:\Temp\claude\D--workspace-board\9eeb7490-393e-43ef-8ed4-e23ebcc4af23\tasks\bd3j6l8um.output")
raw = LOG.read_text(encoding="utf-8", errors="replace")


def brace_match(text, start):
    depth = 0; in_str = False; esc = False
    for i in range(start, len(text)):
        c = text[i]
        if in_str:
            if esc: esc = False
            elif c == "\\": esc = True
            elif c == '"': in_str = False
            continue
        if c == '"': in_str = True
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0: return text[start:i + 1]
    return None


num_by_q = {}
for line in (BOARD / "scripts" / "_qa_agricola_results.jsonl").read_text(encoding="utf-8").splitlines():
    r = json.loads(line); num_by_q[r["question"]] = r["num"]

truth = defaultdict(set)  # num -> set(concept_ids)
hdr = re.compile(r"\[Chat\] game: agricola, question: (.*?), count: \d+")
segs = {}
for m in hdr.finditer(raw):
    segs.setdefault(m.group(1), []).append((m.end(), raw.find("[Chat] game:", m.end())))
for q, spans in segs.items():
    for i, (s, e) in enumerate(spans):
        if e == -1 or e < s: spans[i] = (s, len(raw))
for q, spans in segs.items():
    num = num_by_q.get(q)
    if num is None: continue
    s, e = spans[-1]
    text = raw[s:e]
    for rm in re.finditer(r"Tool execute_plan result:\s*", text):
        b = text.find("{", rm.end())
        if b == -1: continue
        j = brace_match(text, b)
        if not j: continue
        try: res = json.loads(j)
        except Exception: continue
        for rq in res.get("Results", []):
            for m2 in rq.get("Matched") or []:
                truth[num].add(m2.get("Id") or m2.get("id") or "")

# ---- 逐题验证 ----
rows = [json.loads(l) for l in (BOARD / "scripts" / "_faq_all.jsonl").read_text(encoding="utf-8").splitlines()]
right, wrong, nohit = [], [], []
for r in rows:
    if r["game"] != "agricola": continue
    num = num_by_q.get(r["question"])
    if num is None or not truth.get(num): continue
    hits = direct_hit(r["question"])
    if not hits:
        nohit.append(r); continue
    names_hit = {cid for _, cid in hits}
    # 正确 = 全部命中都落在 ground truth 里（多命中只要有一个不在就判「不纯」→ 工具闭嘴）
    if names_hit <= truth[num]:
        right.append((r, hits))
    else:
        wrong.append((r, hits, truth[num]))

print(f"agricola 有 ground truth 的题: {len(right) + len(wrong) + len(nohit)}")
print(f"  直呼命中且全对（工具可拍板）: {len(right)}")
print(f"  直呼命中但不全对（工具闭嘴）: {len(wrong)}")
print(f"  无命中: {len(nohit)}")
print(f"\n===== 可拍板（工具会直接返回）=====")
for r, hits in right:
    print(f"  Q{r['question'][:44]}  -> {hits}")
print(f"\n===== 命中但不可拍板（前 20）=====")
for r, hits, t in wrong[:20]:
    print(f"  Q{r['question'][:40]}  hit={hits}  真={sorted(t)[:3]}")
