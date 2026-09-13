# -*- coding: utf-8 -*-
"""Wingspan FAQ 批量测试：解析 doc/wingspan/faq.md 的 40 道编号题 + 10 条长尾，
逐条 POST /api/chat（game_id=wingspan），边跑边落盘到 qa/_qa_wingspan_results.jsonl。
"""
import io
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
API = "http://localhost:5000/api/chat"
FAQ = ROOT / "doc" / "wingspan" / "faq.md"
OUT = ROOT / "qa" / "_qa_wingspan_results.jsonl"

CAT_RE = re.compile(r"^### (\d+)\. (.+)$")
Q_RE = re.compile(r"^(\d+)\. \*\*原文\*\*[:：](.*)$")
ZH_RE = re.compile(r"^\s*\*\*中文\*\*[:：](.*)$")
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*[:：](.*)$")
TAIL_RE = re.compile(r"^[-*]\s*(.*?)\s*→\s*(.*)$")


def parse_faq(text: str):
    items = []
    cat = ""
    i = 0
    lines = text.splitlines()
    while i < len(lines):
        s = lines[i].strip()
        m = CAT_RE.match(s)
        if m:
            cat = f"{m.group(1)}. {m.group(2)}"
            i += 1
            continue
        m = Q_RE.match(s)
        if m:
            num = int(m.group(1))
            orig = m.group(2).strip()
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if Q_RE.match(s2) or CAT_RE.match(s2):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            # 跳过“（见粉色部分）”这类交叉引用，不是独立问题
            if zh and zh not in ("（见粉色部分）", "见粉色部分"):
                items.append({"num": num, "category": cat, "original": orig,
                              "zh": zh, "faq_answer": ans})
            i = j
            continue
        i += 1

    # 末尾长尾条目（41–50+）
    in_tail = False
    for line in lines:
        s = line.strip()
        if "额外长尾" in s or "41–50" in s or "41-50" in s:
            in_tail = True
            continue
        if in_tail:
            m = TAIL_RE.match(s)
            if m:
                q = m.group(1).strip()
                a = m.group(2).strip()
                items.append({"num": 50 + len([x for x in items if x["num"] >= 50]) + 1,
                              "category": "12. 额外长尾 / 常见困惑",
                              "original": "", "zh": q, "faq_answer": a})
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
    items = parse_faq(FAQ.read_text(encoding="utf-8"))
    # 重新编号：从 1 开始连续编号
    for idx, it in enumerate(items, 1):
        it["num"] = idx
    print(f"解析到 {len(items)} 道题")
    with OUT.open("w", encoding="utf-8") as f:
        for it in items:
            reply, dt, err = ask("wingspan", it["zh"])
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
