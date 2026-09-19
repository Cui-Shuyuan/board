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
import subprocess as _sp


def _api():
    import os
    if os.environ.get("BOARDAI_API"):
        return os.environ["BOARDAI_API"]
    try:
        host = _sp.run(["ip", "route", "show", "default"], capture_output=True,
                       text=True, timeout=3).stdout.split()[2]
        if host:
            return f"http://{host}:5000/api/chat"     # WSL → Windows 宿主
    except Exception:
        pass
    return "http://localhost:5000/api/chat"


API = _api()


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
    """抽出裁决：**只看回答里第一个出现的明确裁决词**。

    第一版用"含『不能』就算不允许"的启发式，把
    「允许。……贵族会自动来访，不能拒绝」误判成不允许 —— 关键词会撞上理由里的词。
    问的时候已经要求它"只回答允许或不允许"，所以取**最先出现**的那个即可。
    """
    i_yes = reply.find("允许")
    i_no = reply.find("不允许")
    if i_no >= 0 and (i_yes < 0 or i_no < i_yes):
        return "不允许"
    if i_yes >= 0:
        return "允许"
    if reply.startswith("可以") or "可以。" in reply[:12]:
        return "允许"
    if reply.startswith("不可以") or "不可以。" in reply[:12]:
        return "不允许"
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
