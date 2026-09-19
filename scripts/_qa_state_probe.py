# -*- coding: utf-8 -*-
"""按**玩家口吻带着状态**问规则引擎——专门测"它能不能当合法性裁判"。

与之前那版步骤级提问的区别：这次用**自然语言描述局面**（像玩家自己会说的那样），
并且**故意混入若干非法状态/非法操作当负例**，看它能不能认出来。
`--from-anim` 会从动画重放里取**真实**快照来造句（数字保证是脚本里那一刻的真实状态）。
"""
import argparse
import io
import json
import sys
import urllib.request
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8") if \
    (sys.stdout.encoding or "").lower() not in ("utf-8", "utf8") else sys.stdout

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))


def api_url():
    import os
    import subprocess
    if os.environ.get("BOARDAI_API"):
        return os.environ["BOARDAI_API"]
    try:
        host = subprocess.run(["ip", "route", "show", "default"], capture_output=True,
                              text=True, timeout=3).stdout.split()[2]
        if host:
            return f"http://{host}:5000/api/chat"
    except Exception:                                        # noqa: BLE001
        pass
    return "http://localhost:5000/api/chat"


def ask(question, game="splendor", timeout=120):
    body = json.dumps({"game_id": game, "messages": [{"role": "user", "content": question}]},
                      ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(api_url(), data=body,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8")).get("reply", "")
    except Exception as e:                                   # noqa: BLE001
        return f"<失败：{e}>"


# ── 问题集：一半是"脚本里真实发生的合法状态"，一半是"故意非法的负例" ──────────
CASES = [
    # (标签, 期望裁决, 问题)
    ("真实状态·正常手上", "合法",
     "我现在手里有两颗白宝石、一颗绿宝石，一共三颗。这个状态有问题吗？"),
    ("真实状态·买牌", "能买",
     "我面前已经买了四张红牌、三张白牌和一张蓝牌；我现在手里有两颗白宝石、一颗绿宝石、"
     "还有一颗黄金。我想买一张费用是三颗白、三颗红、六颗黑的发展卡，"
     "费用里被折扣抵掉的部分不用付。我能买吗？要付哪些？"),
    ("负例·手上超上限", "不合法",
     "我现在手里有八颗彩色宝石，另外还有三颗黄金，一共十一颗。这个状态合法吗？"),
    ("负例·买不起", "不能买",
     "我面前一张发展卡都没有。我想买一张费用是六颗红宝石的发展卡。我现在手里只有"
     "两颗白宝石和一颗绿宝石，也没有黄金。我能买吗？"),
    ("负例·供应堆拿两枚", "不能拿",
     "某种颜色的宝石在供应堆里只剩三颗了，我想一次拿这种颜色的两颗，可以吗？"),
    ("负例·保留满了还想留", "不能留",
     "我面前已经保留了三张发展卡。我现在还想再保留一张，可以吗？"),
    ("负例·贵族条件不够", "不能拿",
     "我面前买下的发展卡只有三张白宝石牌和两张绿宝石牌。桌上那块贵族要求的是四颗白和四颗红。"
     "我能把这块贵族拿走吗？"),
    ("负例·场上宝石太多", "不对劲",
     "我们两个人玩。桌上白色宝石的供应堆里放着四颗，我手里又拿着三颗白宝石，"
     "另外还有两颗白宝石摊在我面前。这个局面有没有问题？"),
]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    for tag, expect, q in CASES:
        if a.only and a.only not in tag:
            continue
        reply = ask(q)
        print(f"\n===== {tag}（期望：{expect}）")
        print("问：" + q)
        print("答：" + reply.replace("\n", " ")[:420])
    return 0


if __name__ == "__main__":
    sys.exit(main())
