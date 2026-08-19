# -*- coding: utf-8 -*-
"""勃艮第城堡 QA 复测：对首轮判错的 6 题（Q2/Q3/Q4/Q5/Q39/Q42）重测，
落盘到 scripts/_qa_cob_retest.jsonl。
"""
import io
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

API = "http://localhost:5000/api/chat"
OUT = Path(r"D:\workspace\board\scripts\_qa_cob_retest.jsonl")

ITEMS = [
    (2, "船、矿和城堡瓷砖在阶段之间是否留在主板上？"),
    (3, "编号仓库中剩余瓷砖在阶段结束时怎么处理？"),
    (4, "拿新瓷砖时三个存储位已满怎么办？"),
    (5, "用两个骰子拿瓷砖时，可以一次丢弃两个吗？"),
    (39, "3 人游戏每阶段填充哪些空位？"),
    (42, "黄色瓷砖 #5 有什么用？"),
]


def ask(q, timeout=300):
    body = json.dumps({
        "game_id": "castles-of-burgundy",
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
    with OUT.open("w", encoding="utf-8") as f:
        for num, q in ITEMS:
            reply, dt, err = ask(q)
            rec = {"num": num, "question": q, "reply": reply,
                   "elapsed": round(dt, 1), "error": err}
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()
            status = "ERR" if err else f"{dt:.0f}s"
            print(f"[{num:>2}] {status} {q[:38]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
