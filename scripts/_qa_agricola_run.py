# -*- coding: utf-8 -*-
"""农场主 FAQ 批量测试：解析 doc/agricola/faq.md 的编号条目
（1-42 结构化 + 43-55 简式共 55 题），逐条 POST /api/chat（game_id=agricola），
边跑边落盘到 scripts/_qa_agricola_results.jsonl。
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
FAQ = Path(r"D:\workspace\board\doc\agricola\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_agricola_results.jsonl")

ITEM_RE = re.compile(r"^(\d+)\. \*\*原文\*\*：(.*)$")
ZH_RE = re.compile(r"^\s*\*\*中文\*\*：(.*)$")
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*：(.*)$")
# 43-55 简式：`43. 问题？（答案）`
TAIL_RE = re.compile(r"^(\d+)\. (.*?)（(.*)）\s*$")


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
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if ITEM_RE.match(s2) or s2.startswith("### ") or TAIL_RE.match(s2):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            items.append({"category": cat, "zh": zh, "faq_answer": ans})
            i = j
            continue
        mt = TAIL_RE.match(s)
        if mt:
            items.append({"category": cat or "长尾补充", "zh": mt.group(2).strip(),
                          "faq_answer": mt.group(3).strip()})
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
    print(f"解析到 {len(items)} 道题")
    with OUT.open("w", encoding="utf-8") as f:
        for i, it in enumerate(items, 1):
            reply, dt, err = ask("agricola", it["zh"])
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
