# -*- coding: utf-8 -*-
"""临时探针：重问 Q5/Q15/Q50 三道口径争议题，验证数据修复后模型回答是否与 FAQ 一致。"""
import io
import json
import sys
import time
import urllib.request

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

API = "http://localhost:5000/api/chat"

QUESTIONS = {
    5: ("用两个骰子拿瓷砖时，可以一次丢弃两个吗？", "可以，每次拿瓷砖时若存储满就先丢一个。"),
    15: ("从中央黑色仓库买瓷砖是免费行动（不需骰子）吗？", "是，每回合最多一次，花 2 银币，放入存储区。"),
    50: ("阶段奖励与黄色效果的结算时机是什么顺序？", "先结算矿的银币收入，再结算黄色瓷砖效果。"),
}


def ask(q, timeout=300):
    body = json.dumps({
        "game_id": "castles-of-burgundy",
        "messages": [{"role": "user", "content": q}],
    }, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    return data.get("reply", ""), time.time() - t0


for num in (5, 15, 50):
    q, a = QUESTIONS[num]
    reply, dt = ask(q)
    print(f"== Q{num} {q}")
    print(f"   FAQ: {a}")
    print(f"   REP: {reply}")
    print(f"   ({dt:.0f}s)")
    print()
