# -*- coding: utf-8 -*-
"""Ark Nova FAQ 批量测试：解析 doc/ark-nova/faq.md（### N. 分类 + 「- **原文**：」条目，
无条目级编号，按出现顺序全局编号），逐条 POST /api/chat（game_id=ark-nova），
边跑边落盘到 scripts/_qa_ark_results.jsonl。
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

API = "http://localhost:5000/api/chat"
FAQ = Path(r"D:\workspace\board\doc\ark-nova\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_ark_results.jsonl")

CAT_RE = re.compile(r"^### (\d+)\. (.+)$")
ORIG_RE = re.compile(r"^-\s*\*\*原文\*\*：(.*)$")
ZH_RE = re.compile(r"^\s*\*\*中文\*\*：(.*)$")
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*：(.*)$")

# 纯单人/扩展题目（本轮不写单人规则）——先全部收录，跑完由用户裁决
SKIP_NUM = set()


def parse_faq(text: str):
    items = []
    cat = ""
    i = 0
    num = 0
    lines = text.splitlines()
    while i < len(lines):
        s = lines[i].strip()
        m = CAT_RE.match(s)
        if m:
            cat = m.group(2)
            i += 1
            continue
        mo = ORIG_RE.match(s)
        if mo:
            num += 1
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if ORIG_RE.match(s2) or CAT_RE.match(s2):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            if zh:
                items.append({"num": num, "category": cat, "zh": zh, "faq_answer": ans})
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
    items = parse_faq(FAQ.read_text(encoding="utf-8"))
    print(f"解析到 {len(items)} 道题")
    with OUT.open("w", encoding="utf-8") as f:
        for it in items:
            reply, dt, err = ask("ark-nova", it["zh"])
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
            print(f"[{it['num']:>2}] {status} {it['zh'][:36]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
