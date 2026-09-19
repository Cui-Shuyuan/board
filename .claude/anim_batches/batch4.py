#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第四批：3.2.2 贵族结算（17 条）+ 玩家区扩容。

贵族事实（读图得到，五块都是 3 分）：
  0001: 4白+4红   0002: 4白+4黑   0003: 4蓝+4绿   0004: 4蓝+4白   0005: 3黑+3红+3白
桌上现在这 3 块是**同一个模板**（都显示 贵族_0001）→ 演示按 0001 的条件（4白+4红）来，
观众看到的条件与"满足"的账就对得上。五张贵族扫描件还没分别接进舞台（遗留项）。

舞台扩容（坐标只属于 stage）：
  · extent.min_z -3.78 → -5.60：玩家面前要摆得下买下的 9~12 张牌 + 3 块贵族
  · player_development 改成 4 列网格（z_step 0.46 半叠，像真人把牌摊成几行）、容量 12
  · 新增 player_nobles（结识的贵族摆在自己面前）
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'

DEV = 'player_development'
NOB = 'player_nobles'

stage = json.loads(STAGE.read_text(encoding='utf-8'))
stage['board']['extent']['min_z'] = -5.60
for z in stage['zones']:
    if z['id'] == DEV:
        z['center'] = {"x": -1.10, "z": -4.20}
        z['layout'] = {"type": "grid", "cols": 4, "x_step": 0.66, "z_step": 0.46}
        z['capacity'] = 12
        z['note'] = ("买下的发展卡摊在自己面前的地方（<development_area>，公开信息）。"
                     "4 列网格、z_step 0.46（两行半叠）：卡片上半部（含右上角折扣）互相让出来，"
                     "所以『摊开』仍看得清，但占地只有 2.6x1.4 —— 真人摆不下时也是这样叠的。"
                     "容量 12 是本节演示『满足一块贵族（4白+4红=8 张）』所需。")
if not any(z['id'] == NOB for z in stage['zones']):
    stage['zones'].append({
        "id": NOB, "label": "玩家结识的贵族",
        "center": {"x": -1.10, "z": -5.25},
        "layout": {"type": "row", "x_step": 0.66},
        "capacity": 3,
        "palette": "panel_player",
        "size": {"w": 0.6, "h": 0.6},
        "display": {"mode": "count"},
        "concept": "<player_zone>",
        "contains": ["noble"],
        "note": "结识的贵族放在自己面前（公开信息）。容量 3 = 一局最多勾到 3 块（本作贵族数=人数+1，"
                "四人局 5 块；这里按演示需要写 3，真要更多改这一个数 + x_step）。",
    })
for a in stage['anchors']:
    if a['id'] == 'board_player_area' and NOB not in a['zones']:
        a['zones'].append(NOB)
STAGE.write_text(json.dumps(stage, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

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


D3 = {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}     # 3.2.1 结束时发展区的 3 张
D9 = {"count": 9, "kinds": {"一级白": 3, "三级白": 1, "一级蓝": 1, "一级红": 4}}  # 满足 4白+4红


def T(**over):
    z = {
        "card_market": {"count": 12, "face_up": 12},
        "deck_level_1": {"count": 35}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
        "noble_market": {"count": 3},
        "gem_supply_diamond": {"count": 2}, "gem_supply_sapphire": {"count": 4},
        "gem_supply_ruby": {"count": 4}, "gem_supply_emerald": {"count": 3},
        "gem_supply_onyx": {"count": 4},
        "gold_supply": {"count": 5},
        "player_holding": {"count": 3, "kinds": {"宝石白": 2, "宝石绿": 1}},
        "player_marker": {"count": 1},
        "showcase": {"count": 0}, "showcase_1": {"count": 0},
        DEV: D3,
        NOB: {"count": 0},
        "player_b_nobles": {"count": 1},
    }
    z.update(over)
    return z


CUES = []
A = CUES.append

# 1-3 贵族是什么
A(cue("action.nobles.intro.001", "action.cards.limit.001.2",
      "【我们再介绍一下贵族板块】",
      "【介绍贵族板块】\n"
      "  镜头推近贵族供应堆（3 块）。贵族不是买来的牌，是**条件达成后自动到你面前**的板块 ——"
      "所以这里只摆出来看。\n"
      "  ⚠ 桌上这 3 块是同一个模板（都显示 贵族_0001）：五张贵族扫描件还没分别接进舞台。"
      "本节的演示按 0001 的条件（4白+4红）算，所以画面与账自洽。",
      {"镜头推近贵族": "0.00", "点一下": "0.80"},
      part(None, T(**{"player_b_nobles": {"count": 0}})),
      part(None, T(**{"player_b_nobles": {"count": 1}})),
      [wait(0.0, camera="noble_market", padding=1.25), hl(0.8, zone="<noble_market>")]))

A(cue("action.nobles.value.001.1", "action.nobles.intro.001",
      "【左上角是这位贵族的声望值，下方则是结识这位贵族所需要的条件】",
      "【贵族的两个信息】\n"
      "  左上角 = 声望值（这五块都是 **3 分**，读图得到）；下方 = 条件（0001 是 4白+4红）。\n"
      "  ⚠ 又是同一个缺口：没有『指某个角』的指示物原语，只能把整块依次点亮。",
      {"依次点三块": "0.60 / 1.80 / 3.00"},
      part(None, T()), part(None, T()),
      [wait(0.0, camera="noble_market", padding=1.25),
       hl(0.6, zone="<noble_market>", order=0),
       hl(1.8, zone="<noble_market>", order=1),
       hl(3.0, zone="<noble_market>", order=2)]))

A(cue("action.nobles.value.001.2", "action.nobles.value.001.1",
      "【也就是已购买的卡牌数量】",
      "【条件 = 已购买卡牌的种类和数量】\n"
      "  点贵族、再点自己面前的已购卡（发展区那 3 张）——『条件看的是这些牌』。\n"
      "  这一条**没有状态变化**：3 张还差得远，只是说明『条件』指的是什么。",
      {"点贵族": "0.50", "点已购卡": "1.60"},
      part(None, T()), part(None, T()),
      [wait(0.0, camera="board"),
       hl(0.5, zone="<noble_market>"), hl(1.6, zone=DEV)]))

# 4-6 什么时候拿、拿哪一块
A(cue("action.nobles.trigger.001.1", "action.nobles.value.001.2",
      "【当玩家结束自己的回合后】",
      "【回合结束时检查】\n"
      "  规则说明，**没有状态变化**：镜头给玩家面前（发展区）—— 检查的对象就是这些牌。",
      {"镜头给玩家面前": "0.00", "点发展区": "0.80"},
      part(None, T()), part(None, T()),
      [wait(0.0, camera=DEV, padding=1.8), hl(0.8, zone=DEV)]))

A(cue("action.nobles.trigger.001.2", "action.nobles.trigger.001.1",
      "【如果已购买的发展卡牌种类和数量均已满足一位供应堆中的贵族的条件】",
      "【把『满足条件』这一步真的做出来】\n"
      "  口播是假设句（『如果已购买…满足』）→ 脚本说了这个前提，所以这里把它**补出来**："
      "发展区再补 2 张白（一级白）与 4 张红（一级红）→ 凑齐 4 白 + 4 红，"
      "正好满足桌上那块贵族（0001：4白+4红）。\n"
      "  · 为什么用 `create`：这些牌是脚本假设『你已经买过』的，不是这一步从市场买的；"
      "盒子里有牌 = create（游戏盒抽象、没有 box_* zone）。\n"
      "  · 为什么补红牌：发展区原有 3 张是 2 白 + 1 蓝，缺的正好是 4 红与 2 白。",
      {"补 2 张白": "0.60", "补 4 张红": "1.20", "点发展区（条件达成）": "2.60"},
      part(None, T()), part(None, T(**{DEV: D9})),
      [wait(0.0, camera=DEV, padding=1.8),
       ev(0.6, "create", destination=DEV, template="market_card_1_diamond", palette="card_level_1",
          count=3, what={"concept": "development_card_level_1",
                         "parts": [{"key": "bonus", "value": "<diamond>"}]}),
       ev(1.2, "create", destination=DEV, template="market_card_1_ruby", palette="card_level_1",
          count=4, what={"concept": "development_card_level_1",
                         "parts": [{"key": "bonus", "value": "<ruby>"}]}),
       hl(2.6, zone=DEV)]))
# 说明：create 的语义是「补齐到 N」→ 一级白已有 1 张 + 三级白 1 张（不同模板），
# 所以这里要写明目标数（3 张一级白 = 原有 1 + 补 2）；见 D9 的账。

A(cue("action.nobles.trigger.001.3", "action.nobles.trigger.001.2",
      "【他必须立即拿取这张贵族板块并立刻获得其声望值】",
      "【立刻拿走那块贵族】\n"
      "  真搬一件：`transfer` 从贵族供应堆 → `player_nobles`（自己面前）。\n"
      "  『必须立即』体现为**同一 cue 内**、条件一达成就搬（2.60 点完条件、3.20 就搬）。\n"
      "  搬哪一块：供应堆里 order 最小的那块（三块条件相同，所以拿哪块都对）。",
      {"点那块贵族": "1.20", "搬到玩家面前": "3.20"},
      part(None, T(**{DEV: D9})), part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      [wait(0.0, camera="noble_market", padding=1.25),
       hl(1.2, zone="<noble_market>", order=0),
       ev(3.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<noble_market>"], destination=NOB, quantity=1, what={"concept": "noble"}),
       hl(4.0, zone=NOB)]))

# 7-9 附加行动 / 强制 / 只能拿供应堆
A(cue("action.nobles.forced.001.1", "action.nobles.trigger.001.3",
      "【请注意，拿取贵族板块并非一个独立行动，而是购买一张发展卡牌之后的附加行动】",
      "【不是独立行动，是附加行动】\n"
      "  规则说明，**没有状态变化**：把三个位置点一遍（市场买牌 → 发展区 → 贵族）"
      "——顺序就是『买牌之后附加』。",
      {"点市场": "0.60", "点发展区": "2.00", "点贵族": "3.40"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      [wait(0.0, camera="board"),
       hl(0.6, zone="<card_market>"), hl(2.0, zone=DEV), hl(3.4, zone=NOB)]))

A(cue("action.nobles.forced.001.2", "action.nobles.forced.001.1",
      "【并且这个行动是强制性的，玩家不可以选择跳过】",
      "【强制，不能跳过】\n"
      "  规则说明，**没有状态变化**：点剩下那两块贵族、再点自己面前那块 ——"
      "『条件够了就必须拿』。\n"
      "  ⚠ 『不可以跳过』这一层仍然只能靠口播：我们没有否定原语。",
      {"点供应堆两块": "0.60 / 1.60", "点自己面前那块": "2.60"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      [wait(0.0, camera="noble_market", padding=1.25),
       hl(0.6, zone="<noble_market>", order=0), hl(1.6, zone="<noble_market>", order=1),
       hl(2.6, zone=NOB)]))

A(cue("action.nobles.source.001", "action.nobles.forced.001.2",
      "【玩家只能从供应堆中拿取贵族，不可以从其他人那里拿取】",
      "【只能拿供应堆里的】\n"
      "  规则说明，**没有状态变化**：镜头在贵族供应堆，点一下 —— 『贵族只有这一个来源』。\n"
      "  ⚠ 『不可以从其他人那里拿』没法演：我们的桌面只有**一位玩家**的位置，"
      "没有『别人面前的贵族』这个东西可指（要演就得先有第二个玩家区）。",
      {"镜头贵族供应堆": "0.00", "点供应堆": "0.80"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      [wait(0.0, camera="noble_market", padding=1.25), hl(0.8, zone="<noble_market>")]))

# 10-11 同时满足多名 → 挑一个
A(cue("action.nobles.choice.001.1", "action.nobles.source.001",
      "【当玩家购买一张发展卡牌后，如果同时满足了多名贵族的条件】",
      "【同时满足多名贵族】\n"
      "  规则说明，**没有状态变化**：桌上剩的两块贵族条件相同（都是 4白+4红），"
      "发展区早就满足 → 现在就是『同时满足多名』的局面，两块一起点出来。\n"
      "  顺带点一下市场：这句的前提是『购买一张发展卡牌后』。",
      {"点市场": "0.60", "点两块贵族": "1.80 / 2.60"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      [wait(0.0, camera="board"),
       hl(0.6, zone="<card_market>"),
       wait(1.6, camera="noble_market", padding=1.4),
       hl(1.8, zone="<noble_market>", order=0), hl(2.6, zone="<noble_market>", order=1)]))

A(cue("action.nobles.choice.001.2", "action.nobles.choice.001.1",
      "【他必须从这些贵族板块中挑选一个拿取】",
      "【挑一个拿走】\n"
      "  真搬一件：从两块里挑一块（拿 order 0 的那块）→ 搬到 `player_nobles`。\n"
      "  『挑』这件事在数据里体现为：源是**供应堆**、每次只搬一件 —— 具体搬哪块由 order 决定，"
      "脚本不写坐标也不用点名到那一件（三块条件相同，拿哪块都对）。",
      {"点要挑的那块": "0.60", "搬到自己面前": "1.60"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 2}, NOB: {"count": 1}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 1}, NOB: {"count": 2}})),
      [wait(0.0, camera="noble_market", padding=1.4),
       hl(0.6, zone="<noble_market>", order=0),
       ev(1.6, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<noble_market>"], destination=NOB, quantity=1, what={"concept": "noble"}),
       hl(2.6, zone=NOB)]))

# 12-14 每回合循环拿
A(cue("action.nobles.repeat.001.1", "action.nobles.choice.001.2",
      "【当他的下一个回合结束时，不管这回合他做了什么行动】",
      "【下一个回合结束时再检查一次】\n"
      "  规则说明，**没有状态变化**：镜头给玩家面前（发展区 + 已经拿到的贵族）。",
      {"镜头给玩家面前": "0.00", "点发展区/贵族": "0.80 / 1.80"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 1}, NOB: {"count": 2}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 1}, NOB: {"count": 2}})),
      [wait(0.0, camera=DEV, padding=1.8), hl(0.8, zone=DEV), hl(1.8, zone=NOB)]))

A(cue("action.nobles.repeat.001.2", "action.nobles.repeat.001.1",
      "【只要供应堆中还保留着其他满足条件的贵族，他必须再从这些贵族板块中挑选一个拿取】",
      "【还剩就再拿一块】\n"
      "  真搬一件：供应堆还剩最后一块（条件同样满足）→ 再搬到 `player_nobles`（此时 3 块）。\n"
      "  这一动把『每回合都要再检查、够了就必须再拿』演成可见的事实。",
      {"点剩下那块": "1.00", "搬到自己面前": "3.20"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 1}, NOB: {"count": 2}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      [wait(0.0, camera="noble_market", padding=1.25),
       hl(1.0, zone="<noble_market>", order=0),
       ev(3.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<noble_market>"], destination=NOB, quantity=1, what={"concept": "noble"}),
       hl(4.0, zone=NOB)]))

A(cue("action.nobles.repeat.001.3", "action.nobles.repeat.001.2",
      "【就这样一直循环，直到某一回合结束时，供应堆中没有满足条件的贵族为止】",
      "【循环到供应堆里没有满足条件的贵族】\n"
      "  镜头拉回整桌，点一下已经空了的贵族供应堆 —— 现在那里一块都不剩，循环就到这里。\n"
      "  没有状态变化（上一条已经搬完）。",
      {"镜头回整桌": "0.00", "点空了的贵族堆": "1.60"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      [wait(0.0, camera="board"), hl(1.6, zone="<noble_market>")]))

# 15-17 本节总结
A(cue("action.cards.summary.001.1", "action.nobles.repeat.001.3",
      "【我们做个总结：玩家可以花费费用购买一张发展卡牌】",
      "【总结（一）：花费用买牌】\n"
      "  规则总结，**没有状态变化**：点市场（要买的那 12 张）、再点发展区（买来放的）。",
      {"点市场": "0.60", "点发展区": "2.00"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      [wait(0.0, camera="board"), hl(0.6, zone="<card_market>"), hl(2.0, zone=DEV)]))

A(cue("action.cards.summary.001.2", "action.cards.summary.001.1",
      "【已购买的发展卡牌会为玩家带来声望值和折扣】",
      "【总结（二）：买的牌给声望与折扣】\n"
      "  规则总结，**没有状态变化**：点发展区（那些牌就是声望与折扣的来源）。",
      {"点发展区": "0.60"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      [wait(0.0, camera=DEV, padding=1.8), hl(0.6, zone=DEV)]))

A(cue("action.cards.summary.001.3", "action.cards.summary.001.2",
      "【当满足贵族开出的条件后，玩家将会结识这名贵族并获得他带来的声望值】",
      "【总结（三）：条件够了就结识贵族】\n"
      "  规则总结，**没有状态变化**：点发展区、再点自己面前的三块贵族（这就是本节的结果）。\n"
      "  本节结束时：发展区 9 张、贵族 3 块（供应堆空了）—— 下一节（3.3 保留卡牌）从这里继续。",
      {"点发展区": "0.60", "点三块贵族": "2.00 / 2.60 / 3.20"},
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      part(None, T(**{DEV: D9, "noble_market": {"count": 0}, NOB: {"count": 3}})),
      [wait(0.0, camera="board"), hl(0.6, zone=DEV),
       hl(2.0, zone=NOB)]))   # 用户：一次点整块


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
