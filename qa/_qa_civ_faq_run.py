# -*- coding: utf-8 -*-
"""Civolution FAQ 批量测试：解析 doc/civolution/faq.md 的编号条目（跳过单人模式分组），
逐条 POST /api/chat（game_id=civolution），边跑边落盘到 scripts/_qa_civ_faq_results.jsonl。
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

API = "http://localhost:5000/api/chat"
FAQ = Path(r"D:\workspace\board\doc\civolution\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_civ_faq_results.jsonl")

CAT_RE = re.compile(r"^### (.+)$")
Q_RE = re.compile(r"^(\d+)\. \*\*原文\*\*：(.*)$")
QNUM_RE = re.compile(r"^(\d+)\.\s*$")          # 编号独占一行（如 36-49 组）
ORIG_RE = re.compile(r"^\*\*原文\*\*：(.*)$")   # 编号行之后紧跟的原文行
NUM_START_RE = re.compile(r"^\d+\.(\s|$)")      # 任意编号开头行（条目边界；strip 后「36.」也认）
ZH_RE = re.compile(r"^\s*\*\*中文翻译\*\*：(.*)$")
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*：(.*)$")

SKIP_CATS = ("单人模式", "统计")


def parse_faq(text: str):
    items = []
    cat = ""
    skip = False
    i = 0
    lines = text.splitlines()
    while i < len(lines):
        s = lines[i].strip()
        m = CAT_RE.match(s)
        if m:
            cat = m.group(1)
            skip = any(x in cat for x in SKIP_CATS)
            i += 1
            continue
        # 两种编号格式：同行「N. **原文**：...」或编号独占行 + 下一行「**原文**：...」
        num = None
        m = Q_RE.match(s)
        if m:
            num = int(m.group(1))
        else:
            mq = QNUM_RE.match(s)
            if mq and i + 1 < len(lines):
                mo = ORIG_RE.match(lines[i + 1].strip())
                if mo:
                    num = int(mq.group(1))
                    i += 1  # 跳过原文行（j 循环从 i+1 开始）
        if num is not None:
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if NUM_START_RE.match(s2) or CAT_RE.match(s2):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            if not skip and zh:
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
            reply, dt, err = ask("civolution", it["zh"])
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
