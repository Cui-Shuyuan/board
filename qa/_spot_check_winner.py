# 一次性：FILTER 判胜改造后，对 5 款游戏各问一道 tiebreak 题做端到端抽查
import json
import urllib.request

API = "http://localhost:5000/api/chat"

CHECKS = [
    ("splendor", "游戏结束时如果两个玩家声望分数相同，谁赢？"),
    ("puerto-rico", "终局计分时如果两名玩家总分相同，怎么判定胜负？"),
    ("agricola", "游戏结束分数一样的话怎么分胜负？"),
    ("castles-of-burgundy", "终局如果分数打平，怎么判定胜者？"),
    ("brass-birmingham", "终局计分并列时怎么分胜负？"),
]


def ask(game_id, q):
    body = json.dumps({
        "game_id": game_id,
        "messages": [{"role": "user", "content": q}],
    })
    req = urllib.request.Request(API, data=body.encode("utf-8"), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as resp:
        return json.loads(resp.read().decode("utf-8"))


for game_id, q in CHECKS:
    try:
        data = ask(game_id, q)
        reply = data.get("reply", "")
        print(f"== {game_id}: {q}")
        print(f"   {reply}")
    except Exception as e:
        print(f"== {game_id}: ERROR {e}")
