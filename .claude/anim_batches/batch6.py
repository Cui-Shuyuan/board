#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第六批（收尾）：3.4 购买保留的卡牌（3）+ 3.5 回合结束与循环（3）+ 第 4 节 游戏结束（9）= 15 条。

真做的两件事：
  · purchase_reserved.002.1 把一张保留的牌买进发展区（`transfer` + `to: face_up` = 翻开）
  · endgame.trigger.001 补一张三级蓝（5 分）进发展区 → 声望到 18（口播说「达到 15 或更多」）

Section 4 全是规则/结算说明：桌面**只有一个玩家区**，所以"4 人局谁还没走完"这类话
只能靠口播 + 指着桌面/起始玩家标记；牌面上也没有分数累加显示（UI 只有字幕）——
两处缺口都写在 note 里，留给用户决定要不要补表现层。
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'

DEV, NOB, RES = 'player_development', 'player_nobles', 'player_reserved'

doc = json.loads(ANIM.read_text(encoding='utf-8'))
rt = json.loads(RT.read_text(encoding='utf-8'))
by_id = {c['cue']: c for c in doc['cues']}


def part(picture, zones):
    return {"picture": picture, "zones": zones}


def ev(at, action, dur=0.0, **kw):
    e = {"at": at, "dur": dur, "action": action}
    e.update(kw)
    return e


def hl(at, zone=None, order=None, peak=0.65, dur=0.5):
    e = ev(at, "highlight", dur, easing="easeInOutCubic", peak_alpha=peak)
    if zone:
        e["zone"] = zone
    if order is not None:
        e["order"] = order
    return e


def wait(at, camera=None, padding=None, dur=0.0):
    e = ev(at, "wait", dur)
    if camera:
        e["camera"] = camera
    if padding:
        e["camera_padding"] = padding
    return e


def cue(cid, parent, story, note, timing, enter, exit_, events):
    return {"cue": cid, "entry_from": parent, "story": story, "note": note, "timing": timing,
            "enter": enter, "exit": exit_, "start": {"set": []}, "events": events}


D9 = {"count": 9, "kinds": {"一级白": 3, "三级白": 1, "一级蓝": 1, "一级红": 4}}
D10 = {"count": 10, "kinds": {"一级白": 3, "三级白": 1, "一级蓝": 1, "一级红": 4, "二级红": 1}}
D11 = {"count": 11, "kinds": {"一级白": 3, "三级白": 1, "一级蓝": 1, "一级红": 4,
                              "二级红": 1, "三级蓝": 1}}


def T(market=10, gold=None, reserved=3, dev=None, pre=False, **over):
    """整桌账。pre=True = 3.4 那次**真付款之前**（持有区 8 枚含 3 黄金、黄金堆 2、黑宝石堆 4）；
    默认 = 付款之后（付了 3 黄金 + 3 黑，手上只剩 5 枚宝石、黄金堆回到 5、黑宝石堆 7）。"""
    if gold is None:
        gold = 2 if pre else 4          # 3.4 那一步付了 2 枚黄金（黄金堆 2 → 4）
    hold_gold = 3 if pre else 1
    onyx = 4
    hk = {"宝石白": 2, "宝石绿": 1}
    if hold_gold:
        hk["黄金"] = hold_gold
    z = {
        "card_market": {"count": market, "face_up": market},
        "deck_level_1": {"count": 34}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
        "noble_market": {"count": 0},
        "gem_supply_diamond": {"count": 2}, "gem_supply_sapphire": {"count": 4},
        "gem_supply_ruby": {"count": 4}, "gem_supply_emerald": {"count": 3},
        "gem_supply_onyx": {"count": onyx},
        "gold_supply": {"count": gold},
        "player_holding": {"count": 3 + hold_gold, "kinds": hk},
        "player_marker": {"count": 1},
        "showcase": {"count": 0}, "showcase_1": {"count": 0},
        DEV: dev or D9, NOB: {"count": 3},
        RES: {"count": reserved},
    }
    z.update(over)
    return z


CUES = []
A = CUES.append

# ── 3.4 购买保留的卡牌 ─────────────────────────────────────────────────────
A(cue("action.purchase_reserved.001", "action.reserve.not_purchase.001",
      "【不仅是供应堆，玩家也可以从自己保留的卡牌中选择一张购买】",
      "【买牌的两个来源】\n"
      "  规则说明，**没有状态变化**：点市场（供应堆那 12 张）、再点保留区（自己留的 3 张）——"
      "两条路都能买。",
      {"点市场": "0.60", "点保留区": "2.20"},
      part(None, T(pre=True)), part(None, T(pre=True)),
      [wait(0.0, camera="board"), hl(0.6, zone="<card_market>"), hl(2.2, zone=RES)]))

A(cue("action.purchase_reserved.002.1", "action.purchase_reserved.001",
      "【按照之前介绍过的规则，支付费用，将一张被保留的、背面朝上的卡牌反面（翻开）】",
      "【买下保留的那张：从保留区搬进发展区（顺便翻开）】\n"
      "  真搬一件：`transfer` 保留区 → 发展区，**带 `to: face_up`** ——\n"
      "  『翻开』不需要单独的翻面事件：一张牌进发展区本来就该正面朝上，写终态一次说清\n"
      "  （翻面原语是取反，重复 Seek/重播会翻来翻去，历史上正是它出过 bug）。\n"
      "  搬哪张：保留区 order 最小的那张（三级白）。\n"
      "  ⚠ **支付这一步没有真的演**：口播说『支付费用』，但手上这 8 枚（含 3 黄金）凑不出这张牌的费用，"
      "而且全套教程到现在**还没有一次真正的付宝石买牌** —— 见本项目收尾报告里的缺口清单。",
      {"点保留区那张": "0.80", "搬进发展区（翻开）": "2.40"},
      part(None, T(pre=True)),
      part(None, T(market=10, reserved=2, dev=D10)),
      [wait(0.0, camera=RES, padding=1.6),
       hl(0.8, zone=RES, order=0),
       ev(2.4, "transfer", dur=0.7, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=[RES], destination=DEV, quantity=1, to="face_up",
          what={"concept": "development_card_level_3",
                "parts": [{"key": "bonus", "value": "<diamond>"}]}),
       hl(3.6, zone=DEV)]))

A(cue("action.purchase_reserved.002.2", "action.purchase_reserved.002.1",
      "【正常结算它的声望值，查看是否满足贵族条件，并在后续的回合中适用它所带来的折扣】",
      "【买完之后的结算】\n"
      "  规则说明，**没有状态变化**：点发展区（声望 + 折扣）→ 点自己面前的贵族（看条件是否满足）。\n"
      "  · 这张三级白值 4 分（读图得到）：发展区现在 4 张白 + 4 张红 + 那么几张，"
      "贵族条件早就满足了；\n"
      "  · 但**贵族供应堆已经空了**（3.2.2 把 3 块都拿完了）→ 『查看是否满足』这一步现在没有对象可查。"
      "这是演示顺序造成的：先把贵族讲透，后面就没得抽了。",
      {"点发展区": "0.60", "点贵族": "2.40"},
      part(None, T(market=10, reserved=2, dev=D10)),
      part(None, T(market=10, reserved=2, dev=D10)),
      [wait(0.0, camera=DEV, padding=1.8), hl(0.6, zone=DEV),
       wait(2.4, camera=NOB, padding=1.6), hl(2.6, zone=NOB)]))

# ── 3.5 回合结束与循环 ─────────────────────────────────────────────────────
A(cue("action.summary.001", "action.purchase_reserved.002.2",
      "【以上就是玩家在回合内可以选择的行动，分别为拿取宝石、购买卡牌和保留卡牌】",
      "【三种行动：回顾一遍】\n"
      "  规则总结，**没有状态变化**：按口播的三样依次点：宝石供应堆 → 市场/发展区 → 保留区。",
      {"点供应堆": "0.60", "点市场": "1.80", "点保留区": "3.20"},
      part(None, T(market=10, reserved=2, dev=D10)),
      part(None, T(market=10, reserved=2, dev=D10)),
      [wait(0.0, camera="board"), hl(0.6, zone="<gem_supply|color=<diamond>>"),
       hl(1.8, zone="<card_market>"), hl(3.2, zone=RES)]))

A(cue("action.summary.002.1", "action.summary.001",
      "【一个回合内，只能选择一种行动进行，随后回合立刻结束，按照顺时针顺序】",
      "【一回合一件事，然后轮到下一位】\n"
      "  规则说明，**没有状态变化**：点市场（三种行动都在这里发生）、再点起始玩家标记"
      "（『按顺时针顺序』的锚点就是它）。",
      {"点市场": "0.60", "点起始玩家标记": "2.60"},
      part(None, T(market=10, reserved=2, dev=D10)),
      part(None, T(market=10, reserved=2, dev=D10)),
      [wait(0.0, camera="board"), hl(0.6, zone="<card_market>"), hl(2.6, zone="player_marker")]))

A(cue("action.summary.002.2", "action.summary.002.1",
      "【左手边的玩家开始他的回合。就这样一直循环，直到整局游戏结束】",
      "【循环到游戏结束】\n"
      "  规则说明，**没有状态变化**：镜头拉回整桌收尾。\n"
      "  ⚠ 『左手边的玩家』没法演：桌面只有**一位玩家**的位置（起始玩家标记只说明谁是起始玩家）。"
      "要演轮流，得先有第二个玩家区 —— 与 3.2.2 的 source 那条是同一个缺口。",
      {"镜头回整桌": "0.00", "点起始标记": "1.60"},
      part(None, T(market=10, reserved=2, dev=D10)),
      part(None, T(market=10, reserved=2, dev=D10)),
      [wait(0.0, camera="board"), hl(1.6, zone="player_marker")]))

# ── 4 游戏结束 ─────────────────────────────────────────────────────────────
_mp_gap = ("\n  ⚠ 这一句是**多人对比**（谁的声望到 15、谁还剩回合），而桌面只有一个玩家区、"
           "牌面也没有分数累加显示（UI 只有字幕）→ 只能指桌面与起始玩家标记，"
           "没法把『4 人局谁还没走完』画出来。要补就得加：玩家区 ×4 + 一个分数读数。")
A(cue("endgame.trigger.001", "action.summary.002.2",
      "【当一位玩家的声望值达到 15 或更多时，触发游戏结束】",
      "【到达 15 分 → 触发结束】\n"
      "  口播说的前提是『达到 15 或更多』→ 脚本给了这个前提，所以把分数**补到位**："
      "发展区再 `create` 一张三级蓝（读图：三级蓝 = 5 分）→\n"
      "  发展区 = 一级白×3(0) + 三级白×2(4+4) + 一级蓝(0) + 一级红×4(0) + 三级蓝(5) = 13 分，"
      "加上 3 块贵族各 3 分 = **22 分** ≥ 15 ✓。\n"
      "  · 为什么补的是三级蓝：现有牌里分数最高的那类，一张就够把 13 推到 15 以上；\n"
      "  · 分不在画面上累加（没有分数 UI）—— 数字靠口播，画面上是『这些牌和贵族』。",
      {"补一张三级蓝": "0.80", "点发展区（13 分）": "2.40", "点贵族（+9 分）": "3.60"},
      part(None, T(market=10, reserved=2, dev=D10)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera=DEV, padding=1.8),
       ev(0.8, "create", destination=DEV, template="market_card_3_sapphire", palette="card_level_3",
          what={"concept": "development_card_level_3",
                "parts": [{"key": "bonus", "value": "<sapphire>"}]}),
       hl(2.4, zone=DEV),
       wait(3.6, camera=NOB, padding=1.6), hl(3.8, zone=NOB)] + [wait(4.6)]))

A(cue("endgame.final_rounds.001.1", "endgame.trigger.001",
      "【为了让所有玩家都进行相同数量的回合，游戏会继续进行】",
      "【让所有人回合数相同】" + _mp_gap,
      {"镜头回整桌": "0.00", "点起始标记": "1.20"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(1.2, zone="player_marker")]))

A(cue("endgame.final_rounds.001.2", "endgame.final_rounds.001.1",
      "【直到起始玩家右手边的玩家结束他的回合时，整场游戏结束】",
      "【起始玩家右手边的人走完 → 结束】" + _mp_gap,
      {"点起始标记": "0.60", "点整桌": "2.60"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(0.6, zone="player_marker"), hl(2.6, zone="<card_market>")]))

A(cue("endgame.example.001.1", "endgame.final_rounds.001.2",
      "【以 4 人局为例：如果起始玩家的声望值达到 15】",
      "【举例：4 人局、起始玩家先到 15】" + _mp_gap,
      {"点起始标记": "0.60", "点发展区": "2.20"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(0.6, zone="player_marker"), hl(2.2, zone=DEV)]))

A(cue("endgame.example.001.2", "endgame.example.001.1",
      "【剩下 3 位玩家各自还有最后一回合可以行动，之后游戏才结束】",
      "【其余 3 人各还有最后一回合】" + _mp_gap,
      {"镜头整桌": "0.00", "点起始标记": "1.00"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(1.0, zone="player_marker")]))

A(cue("endgame.example.002", "endgame.example.001.2",
      "【但如果是起始玩家左手边的玩家声望值达到 15，就没有人可以行动了，游戏立刻结束】",
      "【换成末位玩家到 15 → 立刻结束】" + _mp_gap,
      {"点起始标记": "0.60", "点整桌": "2.60"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(0.6, zone="player_marker"), hl(2.6, zone=DEV)]))

A(cue("endgame.winner.001", "endgame.example.002",
      "【声望值最高的玩家获得胜利】",
      "【分最高者胜】\n"
      "  规则说明，**没有状态变化**：点发展区与三块贵族 —— 声望就来自这两处（13 + 9 = 22）。",
      {"点发展区": "0.60", "点贵族": "1.60"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board"), hl(0.6, zone=DEV), hl(1.6, zone=NOB)]))

A(cue("endgame.tie.001.1", "endgame.winner.001",
      "【如果有多位玩家声望值相同，购买发展卡牌最少的那名玩家获得胜利】",
      "【同分 → 买牌少的胜】\n"
      "  规则说明，**没有状态变化**：点发展区（要比的就是这里的**张数**）。" + _mp_gap,
      {"点发展区": "0.60"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera=DEV, padding=1.8), hl(0.6, zone=DEV)]))

A(cue("endgame.tie.001.2", "endgame.tie.001.1",
      "【如果有多位玩家声望值和购买发展卡牌数量都相同，则他们共享胜利】",
      "【还是并列 → 共享胜利】\n"
      "  规则说明，**没有状态变化**：镜头拉回整桌收尾（全套教程到这里结束）。" + _mp_gap,
      {"镜头回整桌": "0.00"},
      part(None, T(market=10, reserved=2, dev=D11)),
      part(None, T(market=10, reserved=2, dev=D11)),
      [wait(0.0, camera="board")]))


def main():
    order = [c['id'] for c in rt['cues']]
    new = 0
    for c in CUES:
        if c['cue'] not in by_id:
            new += 1
        by_id[c['cue']] = c
    doc['cues'] = [by_id[cid] for cid in order if cid in by_id]
    ANIM.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    miss = [cid for cid in order if cid not in by_id]
    print(f'本批新增 {new} 条；文件现有 {len(doc["cues"])} 条；仍未写 {len(miss)} 条')


if __name__ == '__main__':
    main()
