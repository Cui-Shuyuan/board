#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""**逐 cue 的合法性问答**：把每条"真的改了状态"的 cue 用玩家口吻描述出来问规则引擎，日志留档。

口径（用户 2026-09-20 定）：**不必每条 cue 都硬挤一个问题** ——
只问"这一步真的改了状态"的 cue（盒面介绍、纯讲解那类状态没变的就不问：状态在产生它的那条
cue 上已经查过了）。所以问题数 < cue 数，这是**有意为之**。

问法（用户要的"带着状态问"）：把这一条里做的操作 + 做完之后的状态，用玩家会说的话讲一遍，
然后问"有没有违反规则的地方"。开头必须给「合法 / 有问题」两个词之一，便于程序粗筛；
完整回答进日志，供人复核。

产出（给以后的会话当参考）：
    games/splendor/tutorial/anim/_qa/legality_log.jsonl   一行一个问题（原始问答）
    games/splendor/tutorial/anim/_qa/legality_log.md      人读版：按 cue 分节
    games/splendor/tutorial/anim/_qa/README.md            口径、怎么重跑

用法：
    python3 scripts/qa_anim_percue.py --build          # 只生成问题清单（看看问法对不对）
    python3 scripts/qa_anim_percue.py --run            # 生成 + 问 + 写日志
    python3 scripts/qa_anim_percue.py --run --limit 5  # 先试前 5 条
"""
from __future__ import annotations

import argparse
import io
import json
import subprocess
import sys
import time
import urllib.request
from collections import Counter
from pathlib import Path

if (sys.stdout.encoding or "").lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import validate_anim_rules as R                      # noqa: E402

QADIR = ROOT / "games/splendor/tutorial/anim/_qa"
CN = R.CN
TEXT = {"take": "拿宝石", "pay": "付宝石", "buy": "买发展卡", "reserve": "保留发展卡",
        "noble": "拿走贵族", "create": "补件（脚本给的前提）", "destroy": "收走介绍样本",
        "stack": "搭牌堆"}
# 会改状态的 action（只有这些 cue 才值得问）
STATE_ACTIONS = {"transfer", "create", "destroy", "stack"}


def api_url():
    import os
    if os.environ.get("BOARDAI_API"):
        return os.environ["BOARDAI_API"]
    try:
        host = subprocess.run(["ip", "route", "show", "default"], capture_output=True,
                              text=True, timeout=3).stdout.split()[2]
        if host:
            return f"http://{host}:5000/api/chat"
    except Exception:                                  # noqa: BLE001
        pass
    return "http://localhost:5000/api/chat"


def cnd(c):
    return "、".join(f"{CN.get(k, k)} {v} 颗" if k in CN else f"{k} {v} 张"
                     for k, v in sorted(c.items())) or "无"


def build():
    """重放动画，按 cue 收集"做了什么 + 做完什么样"，只留有状态变化的那几条。"""
    anim = json.loads(R.ANIM.read_text(encoding="utf-8"))
    stage = json.loads(R.STAGE.read_text(encoding="utf-8"))
    facts = json.loads(R.FACTS.read_text(encoding="utf-8")) if R.FACTS.exists() else {}
    zones = {z["id"]: z for z in stage.get("zones") or []}
    devs = [z for z in zones if "development" in z]
    out, per_cue = [], {}

    def describe_action(ev, st):
        a = ev.get("action")
        dest = R.resolve_zone_ref(stage, ev.get("destination") or ev.get("zone") or "")
        gold, color = R.is_gold_event(ev), R.color_of_event(ev, stage)
        srcs = [R.resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]
        qty = int(ev.get("quantity") or ev.get("count") or 1)
        if a == "create":
            tid = ev.get("template") or ""
            if gold or color:
                return f"（前提）桌上又拿出来 {qty} 颗{CN.get(color, '黄金')}"
            if tid.startswith("sample"):
                return "（展示用）把介绍用的样卡摆出来"
            lv = R.card_level(stage, tid)
            name = f"{'一二三'[int(lv)-1]}级发展卡" if lv in "123" else "发展卡"
            return f"（前提）我面前多出 {qty} 张{name}（脚本假设我本来就已经买了）"
        if a == "destroy":
            return f"把 {qty} 件介绍用的样本收走"
        if a == "stack":
            return f"把 {dest} 这摞牌搭起来"
        if a != "transfer":
            return None
        if color and "supply" in (srcs[0] if srcs else "") and "holding" in dest:
            parts = []
            for z in srcs:
                col = color
                for p2 in (zones[z].get("parts") or []):
                    if p2.get("key") == "color":
                        col = str(p2.get("value", "")).strip("<>")
                pile = sum(n for k, n in st.zones[z].items() if k.startswith(f"gem:{col}@"))
                parts.append(f"从{CN.get(col, col)}堆（当时还剩 {pile} 颗）拿 {qty} 颗")
            return "、".join(parts)
        if "holding" in (srcs[0] if srcs else "") and "supply" in dest:
            return f"把 {qty} 颗{CN.get(color, '黄金')}付回供应堆"
        if dest in devs:
            tid = None
            for sid in srcs:
                for k in st.zones[sid]:
                    if k.startswith("card:"):
                        wb = next((str(p.get("value", "")).strip("<>")
                                   for p in ((ev.get("what") or {}).get("parts") or [])
                                   if p.get("key") == "bonus"), None)
                        if wb and R.card_bonus(stage, k[5:]) != wb:
                            continue
                        tid = k[5:]
                        break
                if tid:
                    break
            cost = R.card_cost(facts, tid) or {}
            disc = Counter()
            for ident, n in st.zones[devs[0]].items():
                if ident.startswith("card:"):
                    b = R.card_bonus(stage, ident[5:])
                    if b:
                        disc[b] += n
            return (f"买下一张价格是 {cnd(Counter(cost))} 的发展卡"
                    f"（买之前我面前的折扣是 {cnd(disc)}）")
        if "card_market" in dest:
            return "从那一行牌堆翻出 1 张新牌补到市场空位"
        if "reserved" in dest:
            return "保留 1 张发展卡（朝下放自己面前）"
        if dest.endswith("_nobles"):
            return "把桌上那块要求四白四红的贵族拿走"
        return None

    def on_event(cid, where, ev, st, acc):
        if ev.get("action") not in STATE_ACTIONS:
            return
        d = per_cue.setdefault(cid, {"acts": [], "who": where})
        line = describe_action(ev, st)
        if line:
            d["acts"].append(line)

    def on_cue_end(cid, st):
        d = per_cue.get(cid)
        if not d or not d["acts"]:
            return                                    # 状态没变 → **不硬挤问题**
        hand = Counter()
        for ident, n in st.zones["player_holding"].items():
            if ident.startswith("gem:"):
                hand[ident[4:].split("@")[0]] += n
            elif ident.startswith("gold@"):
                hand["黄金"] += n
        hand_total = sum(hand.values())
        gems = st.gems()
        gems_line = "、".join(f"{CN.get(c, c)} {n} 颗" for c, n in sorted(gems.items()))
        gem_part = (f"桌上每种颜色的宝石在场总数是 {gems_line}（供应堆加我手里加我面前的，"
                    f"一共 {sum(gems.values())} 颗）；" if gems else "")
        q = (f"我们两个人玩璀璨宝石。我刚刚做了这些事：{'；'.join(d['acts'])}。\n"
             f"做完之后：我手里一共 {hand_total} 颗（{cnd(hand)}）；{gem_part}"
             f"我面前保留着 {st.count('player_reserved')} 张发展卡，"
             f"已经认识 {st.count('player_nobles')} 块贵族。\n"
             f"请检查：我做的这些操作、以及现在的这个局面，有没有违反规则的地方？"
             f"如果全部合规，请用「合法」开头；有问题就用「有问题」开头并指出哪里不对。")
        out.append({"cue": cid, "expected": "脚本认为合法", "question": q,
                    "acts": d["acts"]})

    R.run(anim, stage, facts, R.Report(), on_event=on_event, on_cue_end=on_cue_end)
    return out


def verdict_of(reply):
    i_yes, i_no = reply.find("合法"), reply.find("有问题")
    if i_no >= 0 and (i_yes < 0 or i_no < i_yes):
        return "有问题"
    if i_yes >= 0:
        return "合法"
    return "?"


def run(questions, limit=0):
    QADIR.mkdir(parents=True, exist_ok=True)
    jl = QADIR / "legality_log.jsonl"
    rows = []
    with jl.open("w", encoding="utf-8") as f:
        for i, it in enumerate(questions[:limit] if limit else questions, 1):
            body = json.dumps({"game_id": "splendor",
                               "messages": [{"role": "user", "content": it["question"]}]},
                              ensure_ascii=False).encode("utf-8")
            req = urllib.request.Request(api_url(), data=body,
                                         headers={"Content-Type": "application/json"})
            t0 = time.time()
            try:
                with urllib.request.urlopen(req, timeout=150) as r:
                    it["reply"] = json.loads(r.read().decode("utf-8")).get("reply", "")
            except Exception as e:                     # noqa: BLE001
                it["reply"] = f"<失败：{e}>"
            it["seconds"] = round(time.time() - t0, 1)
            it["verdict"] = verdict_of(it["reply"])
            rows.append(it)
            f.write(json.dumps(it, ensure_ascii=False) + "\n")
            f.flush()
            print(f"{i}/{len(questions)} [{it['verdict']}] {it['cue']} ({it['seconds']}s)")
    (QADIR / "legality_log.json").write_text(
        json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    bad = [r for r in rows if r["verdict"] != "合法"]
    md = ["# 动画合法性问答日志（规则引擎当裁判）", "",
          f"- 引擎：`http://localhost:5000/api/chat`（Windows 侧 `backend/BoardAI.Api`）",
          f"- 口径：**只问「真的改了状态」的 cue**（盒面介绍/纯讲解那类状态没变的跳过，"
          f"状态在产生它的 cue 上已查过）→ 共 {len(rows)} 问 / 109 条 cue",
          "- 演示局：**两人局**（每色在场 4 颗、黄金 5、手上限 10、保留上限 3）",
          f"- 结果：合法 {len(rows)-len(bad)}，可疑 {len(bad)}", ""]
    if bad:
        md += ["## 需要人工看一眼的", ""]
        for r in bad:
            md += [f"### {r['cue']}（引擎说：{r['verdict']}）", "",
                   "问：", "", "> " + r["question"].replace("\n", "\n> "), "",
                   "答：", "", "> " + r["reply"].replace("\n", "\n> "), ""]
    md += ["## 全部问答", ""]
    for r in rows:
        md += [f"### {r['cue']} — {r['verdict']}", "",
               "问：", "", "> " + r["question"].replace("\n", "\n> "), "",
               "答：", "", "> " + r["reply"].replace("\n", "\n> "), ""]
    (QADIR / "legality_log.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"\n问 {len(rows)} 条；引擎说合法的 {len(rows)-len(bad)}，可疑 {len(bad)}")
    print(f"日志 → {QADIR/'legality_log.md'} / .jsonl / .json")
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--build", action="store_true", help="只生成问题（打印前几条）")
    ap.add_argument("--run", action="store_true", help="生成 + 问 + 写日志")
    ap.add_argument("--limit", type=int, default=0)
    a = ap.parse_args()
    qs = build()
    print(f"（共 {len(qs)} 个问题 / 109 条 cue —— 只挑了改了状态的 cue）\n")
    if a.build or not a.run:
        for x in qs[:3]:
            print(f"[{x['cue']}]\n{x['question']}\n")
        return 0
    run(qs, a.limit)
    return 0


if __name__ == "__main__":
    sys.exit(main())
