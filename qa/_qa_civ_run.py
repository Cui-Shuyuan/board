# -*- coding: utf-8 -*-
"""Civolution 本地题集复测：合并 _qa_test.py（基础 52）与 _qa_test_new.py（深水 51），
逐条 POST /api/chat（game_id=civolution），结果落盘 _qa_civ_results.jsonl。
"""
import json
import re
import time
import urllib.request
from pathlib import Path

API = "http://localhost:5000/api/chat"
OUT = Path(r"D:\workspace\board\scripts\_qa_civ_results.jsonl")


def load_questions(script: str) -> list[str]:
    src = Path(rf"D:\workspace\board\scripts\{script}").read_text(encoding="utf-8")
    return re.findall(r'^\s+"(.+)",$', src, re.M)


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
        return data.get("reply", ""), time.time() - t0, None
    except Exception as e:
        return "", time.time() - t0, str(e)


def main():
    questions = load_questions("_qa_test.py") + load_questions("_qa_test_new.py")
    print(f"共 {len(questions)} 题")
    with OUT.open("w", encoding="utf-8") as f:
        for i, q in enumerate(questions, 1):
            reply, dt, err = ask("civolution", q)
            rec = {"set": "基础" if i <= 52 else "深水", "num": i, "question": q,
                   "reply": reply, "elapsed": round(dt, 1), "error": err}
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()
            print(f"[{i:>3}] {'ERR' if err else f'{dt:.0f}s'} {q[:36]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
