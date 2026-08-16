# -*- coding: utf-8 -*-
"""波多黎各 FAQ 批量测试：解析 doc/faq/puerto rico/faq.md 的 55 道题，
逐条 POST /api/chat（game_id=puerto-rico），记录回答/耗时/工具调用轮次，
边跑边落盘到 scripts/_qa_pr_results.jsonl。
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

API = "http://localhost:5000/api/chat"
FAQ = Path(r"D:\workspace\board\doc\puerto-rico\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_pr_results.jsonl")

# 类别名映射（### N. 标题）
CAT_RE = re.compile(r"^### (\d+)\. (.+)$")
Q_RE = re.compile(r"^(\d+)\. \*\*原文\*\*：(.*)$")
ZH_RE = re.compile(r"^\*\*中文\*\*：(.*)$")
ANS_RE = re.compile(r"^\*\*答案摘要\*\*：(.*)$")


def parse_faq(text: str):
    items = []
    cat = ""
    i = 0
    lines = text.splitlines()
    while i < len(lines):
        m = CAT_RE.match(lines[i].strip())
        if m:
            cat = f"{m.group(1)}. {m.group(2)}"
            i += 1
            continue
        m = Q_RE.match(lines[i].strip())
        if m:
            num, orig = m.group(1), m.group(2).strip()
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s = lines[j].strip()
                if Q_RE.match(s) or CAT_RE.match(s):
                    break  # 下一道题或下一章节（让外层循环更新类别）
                mz = ZH_RE.match(s)
                ma = ANS_RE.match(s)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            items.append({"num": int(num), "category": cat, "original": orig, "zh": zh, "faq_answer": ans})
            i = j
            continue
        i += 1
    return items


def ask(game_id, q, timeout=180):
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
        for it in items:
            reply, dt, err = ask("puerto-rico", it["zh"])
            rec = {
                "num": it["num"],
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
            print(f"[{it['num']:>2}] {status} {it['zh'][:38]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
