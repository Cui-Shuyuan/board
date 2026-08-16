# -*- coding: utf-8 -*-
"""定向复测受影响题目：老编号 Q9/Q12/Q21/Q22/Q23（按问题文本匹配）。
结果追加写入 scripts/_qa_ark_retest.jsonl。
"""
import json
import sys
import time
import urllib.request
from pathlib import Path

API = "http://localhost:5000/api/chat"
RESULTS = Path(r"D:\workspace\board\scripts\_qa_ark_results.jsonl")
OUT = Path(r"D:\workspace\board\scripts\_qa_ark_retest.jsonl")

# 老编号 → 复测原因
RETEST = {9: "检索未命中（相邻规则）", 12: "释放流程重构验证", 21: "答错（10保护分）", 22: "负分口径", 23: "检索未命中（最终计分卡4分）"}


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
        return data.get("reply", ""), time.time() - t0, None
    except Exception as e:
        return "", time.time() - t0, str(e)


def main():
    old = {}
    for line in RESULTS.read_text(encoding="utf-8").splitlines():
        r = json.loads(line)
        old[r["num"]] = r
    with OUT.open("a", encoding="utf-8") as f:
        for num in sorted(RETEST):
            r = old[num]
            reply, dt, err = ask("ark-nova", r["question"])
            rec = {"old_num": num, "reason": RETEST[num], "question": r["question"],
                   "faq_answer": r["faq_answer"], "reply": reply, "elapsed": round(dt, 1), "error": err}
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()
            print(f"[{num}] {dt:.0f}s {r['question'][:30]}")
            print("   ", (reply or err)[:220].replace("\n", " "))
    print("done")


if __name__ == "__main__":
    main()
