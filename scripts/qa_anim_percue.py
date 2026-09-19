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
    return "、".join(f"{CN.get(k, k)} {v} 颗" if k in CN else f"{k} {v} 颗"
                     for k, v in sorted(c.items())) or "无"


def record_action(ev, st, stage, facts):
    """把一个改状态的事件压成**判定所需的最小事实**（问句只用这些，不再整桌抄一遍）。"""
    a = ev.get("action")
    dest = R.resolve_zone_ref(stage, ev.get("destination") or ev.get("zone") or "")
    srcs = [R.resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]
    gold, color = R.is_gold_event(ev), R.color_of_event(ev, stage)
    qty = int(ev.get("quantity") or ev.get("count") or 1)
    if a == "create":
        return {"k": "premise", "tid": ev.get("template") or "", "qty": qty, "dest": dest}
    if a == "destroy":
        return {"k": "prop", "qty": qty}
    if a == "stack":
        return {"k": "stack", "dest": dest}
    if a != "transfer":
        return {"k": "other"}
    if srcs and "gold_supply" in srcs[0] and "holding" in dest:
        return {"k": "take_gold"}
    if color and srcs and "supply" in srcs[0] and "holding" in dest:
        piles = []
        for z in srcs:
            col = color
            for zz in stage.get("zones") or []:
                if zz.get("id") == z:
                    for p2 in (zz.get("parts") or []):
                        if p2.get("key") == "color":
                            col = str(p2.get("value", "")).strip("<>")
            n = sum(v for k, v in st.zones[z].items() if k.startswith(f"gem:{col}@"))
            piles.append({"color": col, "left": n})
        return {"k": "take", "piles": piles, "qty": qty, "hand_before": st.hand("player_holding")}
    if srcs and "holding" in srcs[0] and "supply" in dest:
        return {"k": "pay", "color": color, "qty": qty}
    if dest.endswith("nobles"):
        return {"k": "noble"}
    if "reserved" in dest:
        return {"k": "reserve"}
    if "development" in dest:
        lv = str((ev.get("what") or {}).get("concept") or "")[-1:]
        bonus = next((str(p2.get("value", "")).strip("<>")
                      for p2 in ((ev.get("what") or {}).get("parts") or [])
                      if p2.get("key") == "bonus"), None)
        tid = f"market_card_{lv}_{bonus}" if lv in "123" and bonus else None
        return {"k": "buy", "tid": tid}
    if "card_market" in dest:
        return {"k": "refill"}
    return {"k": "other"}


def focused_question(cid, recs, st, stage, facts):
    """**一 cue 只问这一件事 + 最小必要状态**（用户要求先过目再跑）。"""
    by = {}
    for r in recs:
        by.setdefault(r["k"], []).append(r)
    dev = st.zones["player_development"]
    if by.get("buy"):
        b = by["buy"][0]
        cost = R.card_cost(facts, b.get("tid")) or {}
        disc = Counter()
        for ident, n in dev.items():
            if ident.startswith("card:"):
                bb = R.card_bonus(stage, ident[5:])
                if bb:
                    disc[bb] += n
        bs = R.card_bonus(stage, b.get("tid") or "")
        if bs:
            disc[bs] = max(0, disc[bs] - 1)
        paid = Counter({r["color"]: r["qty"] for r in by.get("pay", []) if r.get("color")})
        g = sum(1 for r in by.get("pay", []) if r.get("color") is None)
        if g:
            paid["黄金"] = g
        return (f"璀璨宝石。我要买一张发展卡：价格是 {cnd(Counter(cost))}；我面前同色发展卡给出的"
                f"折扣是 {cnd(disc)}（被折扣抵掉的颜色不用付，差额可以用黄金顶）；"
                f"我实际付出去的是 {cnd(paid)}。这笔购买合法吗？"
                f"只回答「允许」或「不允许」，再说明该付什么。")
    if by.get("take"):
        t = by["take"][0]
        who = "、".join(f"{CN.get(p['color'], p['color'])}（那堆还剩 {p['left']} 颗）"
                        for p in t["piles"])
        return (f"璀璨宝石。我手上已经有 {t['hand_before']} 颗宝石和黄金；现在我从 {who} "
                f"一次共拿 {t['qty'] * len(t['piles'])} 颗。这样拿宝石合法吗？"
                f"只回答「允许」或「不允许」，再给一句话理由。")
    if by.get("take_gold"):
        return ("璀璨宝石。我保留了一张发展卡，并按规则从黄金供应堆顺带拿 1 颗黄金。"
                "这一步合法吗？只回答「允许」或「不允许」，再给一句话理由。")
    if by.get("reserve"):
        return (f"璀璨宝石。我准备再保留一张发展卡（朝下放在自己面前）。我之前已经保留了 "
                f"{max(0, st.count('player_reserved') - 1)} 张，黄金供应堆还剩 "
                f"{st.count('gold_supply')} 颗。这一步合法吗？"
                f"只回答「允许」或「不允许」，再给一句话理由。")
    if by.get("noble"):
        bonus = Counter()
        for ident, n in dev.items():
            if ident.startswith("card:"):
                bb = R.card_bonus(stage, ident[5:])
                if bb:
                    bonus[bb] += n
        return (f"璀璨宝石。这一回合结束时，我面前的发展卡按颜色是 {cnd(bonus)}；"
                f"桌上那块贵族要求四白四红，它自动归我。这一步合法吗？"
                f"只回答「允许」或「不允许」，再给一句话理由。")
    if by.get("premise"):
        bonus = Counter()
        for ident, n in dev.items():
            if ident.startswith("card:"):
                bb = R.card_bonus(stage, ident[5:])
                if bb:
                    bonus[bb] += n
        return (f"璀璨宝石。（脚本假设的前提）我面前现在有这些发展卡，按颜色统计是 {cnd(bonus)}；"
                f"我手里有 {st.hand('player_holding')} 颗。这个局面本身有没有违反规则的地方？"
                f"只回答「合法」或「有问题」，再给一句话理由。")
    if by.get("stack"):
        return ("璀璨宝石。两人局里发展卡按等级有固定的张数，我把三摞牌堆搭好、又发了 12 张到市场；"
                "这样对吗？只回答「合法」或「有问题」，再给一句话理由。")
    if by.get("refill"):
        return ("璀璨宝石。我买走一张牌之后，市场由规则自动补上一张新牌。这一步合法吗？"
                "只回答「允许」或「不允许」，再给一句话理由。")
    return None


def build():
    """重放动画，按 cue 收集"做了什么 + 做完什么样"，只留有状态变化的那几条。"""
    anim = json.loads(R.ANIM.read_text(encoding="utf-8"))
    stage = json.loads(R.STAGE.read_text(encoding="utf-8"))
    facts = json.loads(R.FACTS.read_text(encoding="utf-8")) if R.FACTS.exists() else {}
    zones = {z["id"]: z for z in stage.get("zones") or []}
    devs = [z for z in zones if "development" in z]
    out, per_cue, skipped = [], {}, []
    paid_seen = []          # 有没有发生过"把宝石付回供应堆"（决定要不要说"又付掉一些"）

    def describe_action(ev, st):
        a = ev.get("action")
        dest = R.resolve_zone_ref(stage, ev.get("destination") or ev.get("zone") or "")
        gold, color = R.is_gold_event(ev), R.color_of_event(ev, stage)
        srcs = [R.resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]
        qty = int(ev.get("quantity") or ev.get("count") or 1)
        if a == "create":
            tid = ev.get("template") or ""
            if gold or color:
                if tid.startswith("gem_sample") or dest in ("gem_display", "gold_display"):
                    return (f"（展示用）把 {qty} 颗{CN.get(color, '黄金')}**样本**摆到展示位"
                            f"（讲解道具，不进供应堆的账）")
                return f"（前提）桌上又拿出来 {qty} 颗{CN.get(color, '黄金')}"
            if tid.startswith("sample"):
                return "（展示用）把介绍用的样卡摆出来（不是游戏里的牌，只是讲解道具）"
            concept = str((stage.get("templates") or []) and next(
                (t.get("concept") for t in stage["templates"] if t.get("id") == tid), "") or "")
            where = {"noble_market": "桌上贵族供应堆", "card_market": "市场"}.get(
                dest, "对面玩家面前" if dest.startswith("player_b") else "我面前")
            if concept == "noble" or tid.startswith("noble"):
                return f"（前提）{where}摆出 {qty} 块贵族"
            if concept == "starting_player_marker" or tid.startswith("starting_marker"):
                return f"（前提）把 {qty} 枚起始玩家标记拿出来放到玩家面前"
            if not concept and tid.startswith("blank"):
                return f"（展示用）往 {dest} 里垫了 {qty} 张牌背（只为撑牌堆厚度）"
            lv = R.card_level(stage, tid)
            name = f"{'一二三'[int(lv)-1]}级发展卡" if lv in "123" else "发展卡"
            return f"（前提）{where}多出 {qty} 张{name}（脚本假设我本来就已经买了）"
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
        if srcs and "gold_supply" in srcs[0] and "holding" in dest:
            return "（保留的奖励）从黄金供应堆顺带拿 1 颗黄金"
        if dest in devs:
            # 卡的身份从事件本身推（等级取 concept 末位、颜色取 what.bonus），**不扫源区** ——
            # 早先扫源区那版取不到 → 问出来"价格是无"，已弃用。
            lv = str((ev.get("what") or {}).get("concept") or "")[-1:]
            bonus = next((str(p2.get("value", "")).strip("<>")
                          for p2 in ((ev.get("what") or {}).get("parts") or [])
                          if p2.get("key") == "bonus"), None)
            tid = f"market_card_{lv}_{bonus}" if lv in "123" and bonus else None
            cost = (R.card_cost(facts, tid) or {}) if tid else {}
            disc = Counter()
            for ident, n in st.zones[devs[0]].items():
                if ident.startswith("card:"):
                    b = R.card_bonus(stage, ident[5:])
                    if b:
                        disc[b] += n
            if cost:
                return (f"我买下一张价格是 {cnd(Counter(cost))} 的发展卡"
                        f"（买之前我面前的折扣是 {cnd(disc)}）")
            return f"我买下一张发展卡（买之前我面前的折扣是 {cnd(disc)}；价格这一版没查到）"
        if "card_market" in dest:
            return "从那一行牌堆翻出 1 张新牌补到市场空位"
        if "reserved" in dest:
            return "保留 1 张发展卡（朝下放自己面前）"
        if dest.endswith("_nobles"):
            return ("这一回合结束时，条件已满足的那块贵族**自动归我**"
                    "（贵族不是主动拿的行动，是回合结束自动到来）")
        return None

    def on_event(cid, where, ev, st, acc):
        if ev.get("action") not in STATE_ACTIONS:
            return
        d = per_cue.setdefault(cid, {"acts": [], "counts": {}, "who": where,
                                     "only_samples": True, "recs": []})
        # 「讲解道具」= 介绍用的样本件（sample_* / gem_sample）与展示位
        # （showcase* / gem_display / gold_display）。**只动这些东西的 cue 一律不问** ——
        # 用户 2026-09-20 的话："那些介绍用的临时对象就别问了"（引擎也不认识"样本"这个概念，
        # 硬问它只会顺着瞎推）。与"盒面介绍那种不用问"是同一类。
        PRESENTATION_ZONES = ("showcase", "gem_display", "gold_display")
        tid = ev.get("template") or ""
        zid = str(R.resolve_zone_ref(stage, ev.get("destination") or ev.get("zone") or ""))
        is_prop = (tid.startswith("sample") or tid.startswith("gem_sample")
                   or any(z in zid for z in PRESENTATION_ZONES))
        if not is_prop:
            d["only_samples"] = False
        line = describe_action(ev, st)
        if not line:
            return
        if line.startswith("把 ") and "付回供应堆" in line:
            paid_seen.append(True)
        d["recs"].append(record_action(ev, st, stage, facts))
        # 同一种动作合并计数（发牌是 12 条 transfer，写成"×12"就够）
        if d["acts"] and d["acts"][-1] == line:
            d["counts"][line] = d["counts"].get(line, 1) + 1
        else:
            d["counts"][line] = d["counts"].get(line, 1)
            d["acts"].append(line)

    def on_cue_end(cid, st):
        d = per_cue.get(cid)
        if not d or not d["acts"]:
            return
        if d.get("only_samples"):
            skipped.append({"cue": cid,
                            "why": "只摆/收讲解道具（样本件或展示位），不是对局状态 → 不问"})
            return
        q = focused_question(cid, d["recs"], st, stage, facts)
        if not q:
            skipped.append({"cue": cid, "why": "这一条没有需要引擎判定的动作类型 → 不问"})
            return
        out.append({"cue": cid, "expected": "脚本认为合法", "question": q,
                    "acts": d["acts"], "kinds": sorted({r["k"] for r in d["recs"]})})

    R.run(anim, stage, facts, R.Report(), on_event=on_event, on_cue_end=on_cue_end)
    return out, skipped


def verdict_of(reply):
    i_yes, i_no = reply.find("合法"), reply.find("有问题")
    if i_no >= 0 and (i_yes < 0 or i_no < i_yes):
        return "有问题"
    if i_yes >= 0:
        return "合法"
    return "?"


def run(questions, limit=0, only=False):
    QADIR.mkdir(parents=True, exist_ok=True)
    jl = QADIR / "legality_log.jsonl"
    # --only：把日志里已有的行读进来，只替换这次问的这几条（其余保留，不重问）
    old = []
    if only and jl.exists():
        old = [json.loads(l) for l in jl.read_text(encoding="utf-8").splitlines() if l.strip()]
        for o in old:
            o.pop("_note", None)
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
    if only and old:
        asked = {r["cue"] for r in rows}
        merged = rows + [o for o in old if o["cue"] not in asked]
        merged.sort(key=lambda r: next((i for i, q in enumerate(questions) if q["cue"] == r["cue"]), 999))
        rows = merged
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
    ap.add_argument("--only", default="", help="只重问这些 cue（逗号分隔）；日志里只替换这几行")
    a = ap.parse_args()
    qs, skipped = build()
    print(f"（另外跳过 {len(skipped)} 条只动讲解道具的 cue：" +
          "、".join(x['cue'] for x in skipped[:6]) + ("…" if len(skipped) > 6 else "") + "）")
    only = [x.strip() for x in a.only.split(",") if x.strip()]
    if only:
        qs = [x for x in qs if x["cue"] in only]
        print(f"（--only：只问 {len(qs)} 条：{', '.join(x['cue'] for x in qs)}）")
    print(f"（共 {len(qs)} 个问题 / 109 条 cue —— 只挑了改了状态的 cue）\n")
    if a.build or not a.run:
        for x in qs[:3]:
            print(f"[{x['cue']}]\n{x['question']}\n")
        return 0
    run(qs, a.limit, only=bool(only))
    return 0


if __name__ == "__main__":
    sys.exit(main())
