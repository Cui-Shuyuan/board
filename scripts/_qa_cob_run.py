# -*- coding: utf-8 -*-
"""勃艮第城堡 FAQ 批量测试：解析 doc/castles-of-burgundy/faq.md 的编号条目
（1-28、41-52 共 40 题）+ 手工整理的 29-40（黄色瓷砖）与 53-60（长尾）合并节条目
（12 题），逐条 POST /api/chat（game_id=castles-of-burgundy），
边跑边落盘到 scripts/_qa_cob_results.jsonl。
"""
import io
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

# Windows GBK 控制台 → utf-8，避免打印中文报 UnicodeEncodeError
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

API = "http://localhost:5000/api/chat"
FAQ = Path(r"D:\workspace\board\doc\castles-of-burgundy\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_cob_results.jsonl")

ITEM_RE = re.compile(r"^(\d+)\. \*\*原文\*\*：(.*)$".replace("：", ":"))
ZH_RE = re.compile(r"^\s*\*\*中文\*\*：(.*)$".replace("：", ":"))
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*：(.*)$".replace("：", ":"))

# 29-40（黄色瓷砖合并节）与 53-60（长尾合并节）手工整理为中文提问
TAIL_ITEMS = [
    ("黄色瓷砖 #3 或 #4 有什么用？", "卖货时额外得银币（#3）或工人（#4）。"),
    ("黄色瓷砖 #5 有什么用？", "放置船时可从两个相邻仓库取货物。"),
    ("黄色瓷砖 #8 到 #12 有什么用？", "放置特定颜色的瓷砖时骰子可 ±1 调整（如同工人）。"),
    ("黄色瓷砖 #13 或 #14 有什么用？", "取工人行动时额外得银币或更多工人。"),
    ("黄色瓷砖 #15 及以后的效果是什么？", "终局按已售货物类型或建筑类型加分。"),
    ("城堡的额外行动可以再拿瓷砖并立即放置吗？", "可以，立即获得一次任意点数的额外行动。"),
    ("卖货与放置仓库同时发生时银币和 VP 如何叠加？", "都结算：卖货得银币和 VP，仓库效果照常。"),
    ("黑色背面瓷砖与普通瓷砖有什么规则差异？", "黑色背面瓷砖在阶段转换时同样被移除（由黑色仓库补货）。"),
    ("起始工人数量与玩家顺序有什么关系？", "起始玩家 1 个，其余顺时针依次 2/3/4 个。"),
    ("阶段奖励与黄色效果的结算时机是什么顺序？", "先结算矿的银币收入，再结算黄色瓷砖效果。"),
    ("无法行动时是否必须跳过？", "不强制跳过，玩家选择仍可执行的行动即可。"),
    ("存储区满时强制丢弃的优先级是什么？", "没有优先级限制，玩家可自由选择丢弃哪一块。"),
]


def parse_faq(text: str):
    items = []
    cat = ""
    i = 0
    lines = text.splitlines()
    while i < len(lines):
        s = lines[i].strip()
        if s.startswith("### "):
            cat = s[4:]
            i += 1
            continue
        m = ITEM_RE.match(s)
        if m:
            orig = m.group(2).strip()
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if ITEM_RE.match(s2) or s2.startswith("### "):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            items.append({"category": cat, "original": orig, "zh": zh, "faq_answer": ans})
            i = j
            continue
        i += 1
    return items


def ask(game_id, q, timeout=300):
    body = json.dumps({
        "game_id": game_id,
        "messages": [{"role": "user", "content": q}],
    }, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        dt = time.time() - t0
        return data.get("reply", ""), dt, None
    except Exception as e:
        return "", time.time() - t0, str(e)


def main():
    text = FAQ.read_text(encoding="utf-8")
    items = parse_faq(text)
    for q, a in TAIL_ITEMS:
        items.append({"category": "合并节（29-40 黄色瓷砖 / 53-60 长尾）",
                      "original": "", "zh": q, "faq_answer": a})
    print(f"解析到 {len(items)} 道题")
    with OUT.open("w", encoding="utf-8") as f:
        for i, it in enumerate(items, 1):
            reply, dt, err = ask("castles-of-burgundy", it["zh"])
            rec = {
                "num": i,
                "category": it["category"],
                "question": it["zh"],
                "faq_answer": it["faq_answer"],
                "reply": reply,
                "elapsed": round(dt, 1),
                "error": err,
            }
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()
            status = "ERR" if err else f"{dt:.0f}s"
            print(f"[{i:>2}] {status} {it['zh'][:38]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
