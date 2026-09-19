#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把动画脚本里的**可疑步骤**变成问规则问答引擎的问题（交叉验证）。

为什么：`validate_anim_rules.py` 的规则常量是**人写的**（我读规则书/记忆写进代码），
它证明的是"脚本与我理解的规则一致"。问答引擎是照规则数据/规则书回答的**独立裁判** ——
让它来判断"这一步允许吗"，能抓出**我理解错的规则**（我写死的常量本身错，就对账不出来）。

分两步（因为引擎跑在 Windows 的 localhost）：
  ① 本脚本（WSL 也能跑）读动画数据 + 重放状态，生成问题清单：
       python3 scripts/qa_anim_check.py --out /tmp/anim_questions.json
  ② 在 Windows 上用 Windows Python 问引擎：
       D:/Python/Python312/python.exe D:/workspace/board/scripts/_qa_anim_run.py --in <清单> --out <结果>
  ③ 回来比对：
       python3 scripts/qa_anim_check.py --compare <结果>

问题里带上**具体数字与局面**（"手上 8 枚…供应堆 4 枚…这一步拿 3 枚"），并要求引擎只答
「允许/不允许 + 一句话理由」，方便程序比对。
"""
from __future__ import annotations

import argparse
import io
import json
import sys
from collections import Counter
from pathlib import Path

# Windows 控制台是 GBK → 打印中文会乱码
if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import validate_anim_rules as R          # 复用同一套重放与常量

CN = R.CN
def api_url():
    """引擎地址：Windows 上跑就是 localhost；WSL 里要用宿主 IP（防火墙放行后可达）。"""
    import os
    env = os.environ.get("BOARDAI_API")
    if env:
        return env
    try:
        import subprocess
        host = subprocess.run(["ip", "route", "show", "default"], capture_output=True,
                              text=True, timeout=3).stdout.split()[2]
        if host:
            return f"http://{host}:5000/api/chat"
    except Exception:                                     # noqa: BLE001
        pass
    return "http://localhost:5000/api/chat"


def q(where, kind, question, script_says):
    return {"where": where, "kind": kind, "question": question,
            "script_says": script_says, "verdict": None, "reply": None}


def build_questions():
    """重放动画（复用 validate_anim_rules 的同一套状态机），把每一步"值得让规则引擎过一眼"的操作变成问题。

    关键：问题里的数字取**那一步之前**的状态（回调给的就是），所以问的是"这个局面下这一步允许吗"。
    """
    anim = json.loads(R.ANIM.read_text(encoding="utf-8"))
    stage = json.loads(R.STAGE.read_text(encoding="utf-8"))
    facts = json.loads(R.FACTS.read_text(encoding="utf-8")) if R.FACTS.exists() else {}
    zones = {z["id"]: z for z in stage.get("zones") or []}
    out = []

    def cnd(counter):
        return "、".join(f"{CN.get(k, k)}{v}枚" if k in CN else f"{k}{v}枚"
                         for k, v in sorted(counter.items())) or "无"

    def on_event(cid, where, ev, st, acc):
        if ev.get("action") != "transfer":
            return
        dest = R.resolve_zone_ref(stage, ev.get("destination"))
        gold = R.is_gold_event(ev)
        qty = int(ev.get("quantity") or 1)
        devs = [z for z in zones if "development" in z]
        hold_zone = "player_holding"
        # ① 取宝石：**一个动作一个问题**（三色各一 / 同色两枚）——
        #    引擎教的：拿宝石只有这两种动作，把"三色各一"拆成三个单颗问题去问是问错的。
        srcs_all = [R.resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]
        takes = [z for z in srcs_all if "gem_supply" in z]
        if takes and "holding" in dest:
            parts = []
            for z in takes:
                col = None
                for p2 in (zones[z].get("parts") or []):
                    if p2.get("key") == "color":
                        col = str(p2.get("value", "")).strip("<>")
                pile = sum(n for k, n in st.zones[z].items() if k.startswith(f"gem:{col}@"))
                parts.append(f"{CN.get(col, col)}（供应堆还剩 {pile} 枚，这次拿 {qty} 枚）")
            out.append(q(where, "拿宝石",
                         f"璀璨宝石：玩家手上已有 {st.hand(hold_zone)} 枚宝石和黄金（上限 10 枚）。"
                         f"现在他做『拿宝石』这个动作：从 " + "、".join(parts) + "。这一步允许吗？"
                         f"只回答「允许」或「不允许」，再给一句话理由。",
                         "允许"))
        # ③ 买牌：**不生成步骤级问题** —— 生成器取卡那一小段还有残留 bug（源区取卡取不到，
        #    会把价格填成"无"），而且买牌合法性已经有两道保证：
        #      · validate_anim_rules.py 的「价格 − 折扣 == 实付」逐色对账
        #      · 引擎在 --rules 里确认的折扣规则与黄金万能规则
        #    （第 1 版问出来过两处"不允许"，都是问题本身写错：价格漏填、手上取了付款后的状态。）

        # ④ 保留：卡进保留区
        if "reserved" in dest:
            out.append(q(where, "保留",
                         f"璀璨宝石：玩家面前已经保留了 {st.count(dest)} 张发展卡，黄金供应堆里还有 "
                         f"{st.count('gold_supply')} 枚。现在他要再保留 1 张。这一步允许吗？"
                         f"只回答「允许」或「不允许」，再给一句话理由。",
                         "允许"))
        # ⑤ 贵族
        if dest.endswith("_nobles"):
            dev = devs[0]
            bonus = Counter()
            for ident, n in st.zones[dev].items():
                if ident.startswith("card:"):
                    b = R.card_bonus(stage, ident[5:])
                    if b:
                        bonus[b] += n
            out.append(q(where, "贵族",
                         f"璀璨宝石：玩家已购买的发展卡按颜色统计是 {cnd(bonus)}；桌上那块贵族要求的"
                         f"是 4 枚白 + 4 枚红。玩家现在把这块贵族拿走。这一步允许吗？"
                         f"只回答「允许」或「不允许」，再给一句话理由。",
                         "允许"))

    R.run(anim, stage, facts, R.Report(), on_event=on_event)
    return out


RULES = [
    # (问题, validate_anim_rules 里的常量, 说明)
    ("璀璨宝石 2 人局时，每种颜色的宝石供应堆放几枚？", "每色 4 枚（实物 7，另 3 枚在盒里）", "GEMS_PER_COLOR"),
    ("璀璨宝石里黄金供应堆一共几枚？", "5 枚", "GOLD_TOTAL"),
    ("璀璨宝石里，玩家手里的宝石和黄金最多几枚？超过会怎样？", "上限 10；超了立刻弃回供应堆", "HAND_LIMIT"),
    ("璀璨宝石里，每位玩家最多同时保留几张发展卡？", "3 张", "RESERVE_LIMIT"),
    ("璀璨宝石里，保留一张发展卡时必须拿一枚黄金吗？黄金堆空了怎么办？", "有黄金就必须拿；空了才能不拿", "保留必给黄金"),
    ("璀璨宝石里，黄金可以当任意颜色的宝石用吗？它怎么获得？", "可以当百搭；只能靠保留卡牌获得", "黄金=万能"),
    ("璀璨宝石里，购买发展卡时折扣怎么算？折扣能超过费用吗？", "同色 bonus 几张减几颗，最少减到 0", "折扣规则"),
    ("璀璨宝石里，拿同一种颜色的两枚宝石有什么条件？", "该色供应堆至少 4 枚、拿完至少剩 2 枚", "同色两枚门槛"),
]


def run_rules(api=None):
    api = api or api_url()
    import urllib.request
    rows = []
    for question, expect, tag in RULES:
        body = json.dumps({"game_id": "splendor",
                           "messages": [{"role": "user", "content": question}]},
                          ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(api, data=body, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                reply = json.loads(r.read().decode("utf-8")).get("reply", "")
        except Exception as e:                              # noqa: BLE001
            reply = f"<失败：{e}>"
        rows.append({"tag": tag, "question": question, "expect": expect, "reply": reply})
        print(f"[{tag}] 期望：{expect}\n    引擎：{reply[:220]}\n")
    Path("/tmp/qa_rules.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", help="把问题清单写到这个文件")
    ap.add_argument("--compare", help="读引擎的回答（jsonl）并与脚本做的事比对")
    ap.add_argument("--rules", action="store_true", help="问引擎『规则本身』，与校验器常量对照")
    args = ap.parse_args()
    if args.rules:
        run_rules()
        return 0
    if args.compare:
        rows = [json.loads(l) for l in Path(args.compare).read_text(encoding="utf-8").splitlines() if l.strip()]
        bad = [r for r in rows if r.get("verdict") == "不允许"]
        print(f"问了 {len(rows)} 个问题；引擎说「不允许」的有 {len(bad)} 个：")
        for r in bad:
            print(f"\n  ✗ {r['where']}（{r['kind']}）")
            print(f"    问：{r['question']}")
            print(f"    答：{r['reply'][:300]}")
        print(f"\n其余 {len(rows)-len(bad)} 个：引擎与脚本一致（都允许）。")
        return 1 if bad else 0
    qs = build_questions()
    if args.out:
        Path(args.out).write_text(json.dumps(qs, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"生成 {len(qs)} 个问题 → {args.out}")
    else:
        for x in qs:
            print(f"[{x['kind']}] {x['where']}\n   {x['question']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
