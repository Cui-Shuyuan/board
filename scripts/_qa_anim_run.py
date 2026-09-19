# -*- coding: utf-8 -*-
"""在 **Windows 侧**跑：把问题清单逐条问规则问答引擎，结果落 jsonl。

    D:/Python/Python312/python.exe D:/workspace/board/scripts/_qa_anim_run.py \
        --in D:/workspace/board/scripts/_qa_anim_questions.json \
        --out D:/workspace/board/scripts/_qa_anim_results.jsonl

只问不改：脚本做的事已经在动画数据里；这里只是把「这一步允许吗」丢给独立裁判。
"""
import argparse
import io
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
API = "http://localhost:5000/api/chat"


def ask(game_id, question, timeout=180):
    body = json.dumps({"game_id": game_id,
                       "messages": [{"role": "user", "content": question}]},
                      ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        return data.get("reply", ""), time.time() - t0, None
    except Exception as e:                     # noqa: BLE001
        return "", time.time() - t0, str(e)


def verdict_of(reply):
    """从回答里抽出「允许 / 不允许」。注意「不允许」要先判（它包含「允许」三个字）。"""
    if "不允许" in reply or "不可以" in reply or "不能" in reply:
        return "不允许"
    if "允许" in reply or "可以" in reply or "合法" in reply:
        return "允许"
    return "?"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True)
    ap.add_argument("--out", dest="out", required=True)
    ap.add_argument("--game", default="splendor")
    a = ap.parse_args()
    qs = json.loads(Path(a.inp).read_text(encoding="utf-8"))
    outp = Path(a.out)
    with outp.open("w", encoding="utf-8") as f:
        for i, it in enumerate(qs, 1):
            reply, dt, err = ask(a.game, it["question"])
            if err:
                reply = f"<失败：{err}>"
            it["reply"], it["verdict"] = reply, verdict_of(reply)
            f.write(json.dumps(it, ensure_ascii=False) + "\n")
            f.flush()
            print(f"{i}/{len(qs)} [{it['kind']}] {it['verdict']} ({dt:.1f}s) {it['where']}")
    print(f"\n结果 → {outp}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
