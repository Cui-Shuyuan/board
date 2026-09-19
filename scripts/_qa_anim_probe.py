# -*- coding: utf-8 -*-
"""探针：拿"规则常量"问题问规则问答引擎（用于交叉验证动画脚本的合法性判据）。

按仓库约定在 **Windows 侧**跑（打 localhost:5000）：
    D:/Python/Python312/python.exe D:/workspace/board/scripts/_qa_anim_probe.py
"""
import io, json, sys, time, urllib.request

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
API = "http://localhost:5000/api/chat"

QUESTIONS = [
    "璀璨宝石 2 人局时，每种宝石的供应堆放几枚？",
    "璀璨宝石里，玩家手里的宝石和黄金最多能有几枚？",
    "璀璨宝石里，玩家最多可以同时保留几张发展卡？",
    "璀璨宝石里，黄金可以当任意颜色的宝石使用吗？",
    "璀璨宝石里，保留一张发展卡的时候必须拿一枚黄金吗？",
    "璀璨宝石里，购买发展卡时折扣是怎么算的？",
    "璀璨宝石里，玩家手里已经有 10 枚宝石了，还能再拿宝石吗？",
    "璀璨宝石里，供应堆里某种宝石只剩 3 枚时，可以一次拿这一种的两枚吗？",
]


def ask(game_id, q, timeout=180):
    body = json.dumps({"game_id": game_id, "messages": [{"role": "user", "content": q}]},
                      ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8")).get("reply", ""), time.time() - t0, None
    except Exception as e:
        return "", time.time() - t0, str(e)


if __name__ == "__main__":
    for q in QUESTIONS:
        reply, dt, err = ask("splendor", q)
        print(f"\nQ（{dt:.1f}s）: {q}")
        print("A:", (reply or f"<失败 {err}>")[:400].replace("\n", " "))
