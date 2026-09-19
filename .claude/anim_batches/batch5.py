#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第五批：3.3 保留一张发展卡牌（13 条）。

真做的三件事（其余是规则说明）：
  · intro.001.2 从市场保留一张 **三级白** → 保留区（正面朝下）+ 从黄金堆拿 1 枚
  · deck.001   从**牌堆顶**再保留一张（正面朝下，观众也看不到是哪张）+ 再拿 1 枚黄金
  · limit_hand.001.2 保留第 3 张 → 正好到上限（每人最多 3 张）

舞台：新增 player_reserved（自己面前摊保留牌的地方）；发展区整体左移让出位置。
保留的 3 张牌**都朝下**：口播说保留是个人信息 —— 观众看得见"有几张"，看不见"是哪张"，
这与实物一致（我们没给"只有自己能看"这件事建模，所以画面上就一直朝下）。
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'

DEV, NOB, RES = 'player_development', 'player_nobles', 'player_reserved'

stage = json.loads(STAGE.read_text(encoding='utf-8'))
for z in stage['zones']:
    if z['id'] == DEV:
        z['center'] = {"x": -1.45, "z": -4.20}      # 左移，给右侧的保留区让位
if not any(z['id'] == RES for z in stage['zones']):
    stage['zones'].append({
        "id": RES, "label": "玩家保留区",
        "center": {"x": 0.60, "z": -4.20},
        "layout": {"type": "row", "x_step": 0.35},   # 半叠：3 张只占 1.3 宽
        "capacity": 3,
        "palette": "panel_player",
        "size": {"w": 0.63, "h": 0.88},
        "display": {"mode": "count"},
        "concept": "<reserve>",
        "contains": ["development_card_level_1", "development_card_level_2", "development_card_level_3"],
        "note": "保留的发展卡放的地方（<reserve>）。x_step 0.35 = 卡片半叠成扇形："
                "保留区**必须**放得下 3 张（规则上限就是 3），而桌子横向不够摊开 —— "
                "真人遇到这种情况也是这样错开叠的，而且朝下的牌只需要露出边缘。",
    })
for a in stage['anchors']:
    if a['id'] == 'board_player_area' and RES not in a['zones']:
        a['zones'].append(RES)
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


D9 = {"count": 9, "kinds": {"一级白": 3, "三级白": 1, "一级蓝": 1, "一级红": 4}}


def T(market=12, gold=5, hold_gold=0, reserved=0, deck1=35, **over):
    hk = {"宝石白": 1, "宝石蓝": 1, "宝石红": 1, "宝石绿": 2}
    if hold_gold:
        hk["黄金"] = hold_gold
    z = {
        "card_market": {"count": market, "face_up": market},
        "deck_level_1": {"count": deck1}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
        "noble_market": {"count": 0},
        "gem_supply_diamond": {"count": 3}, "gem_supply_sapphire": {"count": 3},
        "gem_supply_ruby": {"count": 3}, "gem_supply_emerald": {"count": 2},
        "gem_supply_onyx": {"count": 4},
        "gold_supply": {"count": gold},
        "player_holding": {"count": 5 + hold_gold, "kinds": hk},
        "player_marker": {"count": 1},
        "showcase": {"count": 0}, "showcase_1": {"count": 0},
        DEV: D9, NOB: {"count": 3},
        RES: {"count": reserved},
    }
    z.update(over)
    return z


L3_DIAMOND = {"concept": "development_card_level_3", "parts": [{"key": "bonus", "value": "<diamond>"}]}
L2_RUBY = {"concept": "development_card_level_2", "parts": [{"key": "bonus", "value": "<ruby>"}]}

CUES = []
A = CUES.append

# 1-2 保留是什么：不付钱拿走一张 → 朝下放自己面前 + 拿一枚黄金
A(cue("action.reserve.intro.001.1", "action.cards.summary.001.3",
      "【除购买外，玩家也可以选择不支付费用，将一张发展卡牌从供应堆中拿走】",
      "【保留（一）：不付钱，把牌拿走】\n"
      "  口播前半句先把『保留』这件事说出来 → 镜头推近市场，点一下要保留的那张（三级白）。\n"
      "  ⚠ 这一句**没有搬运**：口播讲到『拿走』就切到下一句，牌是下一句（001.2）才真正离桌的 —— "
      "「一句话一条状态变化」比抢半句台词更清楚。",
      {"镜头推近市场": "0.00", "点要保留的牌": "1.20 / 3.20"},
      part(None, T()), part(None, T()),
      [wait(0.0, camera="card_market", padding=1.6),
       hl(1.2, zone="<card_market>", order=9), hl(3.2, zone="<card_market>", order=9)]))

A(cue("action.reserve.intro.001.2", "action.reserve.intro.001.1",
      "【正面朝下放置在自己面前，并且从黄金供应堆中拿取一枚黄金】",
      "【保留（二）：朝下放自己面前 + 拿一枚黄金】\n"
      "  两件真事，一句口播两个动作：\n"
      "    ① `transfer` 那张三级白：市场 → `player_reserved`，**带 `to: face_down`**（终态朝下）；\n"
      "    ② `transfer` 一枚黄金：黄金供应堆 → 持有区（保留的奖励）。\n"
      "  『朝下』写成终态而不是『翻一下』：翻面是取反、谁最后执行谁赢（历史上正是它出过 bug），"
      "写终态则重复 Seek / 重播都得到同一结果。\n"
      "  ⚠ 我们没给『只有自己能看』建模：牌在整个 3.3 里一直朝下，观众看到的是『有几张、看不见是哪张』——"
      "和实物桌上别人看到的一样。",
      {"搬牌到保留区（朝下）": "0.80", "拿一枚黄金": "2.60", "点保留区": "4.40"},
      part(None, T()),
      part(None, T(market=11, gold=4, hold_gold=1, reserved=1)),
      [wait(0.0, camera="card_market", padding=1.6),
       ev(0.8, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<card_market>"], destination=RES, quantity=1, to="face_down", what=L3_DIAMOND),
       ev(2.6, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<gold_supply>"], destination="<player_holding>", quantity=1,
          what={"concept": "gold"}),
       hl(4.4, zone=RES)]))

# 3 黄金是什么
A(cue("action.reserve.gold.001", "action.reserve.intro.001.2",
      "【黄金相当于万能宝石，它可以在玩家购买发展卡牌时成为任意一种宝石的替代】",
      "【黄金 = 万能宝石】\n"
      "  规则说明，**没有状态变化**：先点持有区里那枚黄金（刚拿到的），再点保留的那张牌 ——"
      "『买它的时候，这枚黄金可以当任意一种宝石』。",
      {"点持有区（黄金）": "0.80", "点保留的牌": "2.60"},
      part(None, T(market=11, gold=4, hold_gold=1, reserved=1)),
      part(None, T(market=11, gold=4, hold_gold=1, reserved=1)),
      [wait(0.0, camera="board"),
       hl(0.8, zone="<player_holding>", order=5), hl(2.6, zone=RES)]))

# 4 也可以从牌堆顶保留（盲抽）+ 再拿一枚黄金
A(cue("action.reserve.deck.001", "action.reserve.gold.001",
      "【除了供应堆的 12 张卡牌外，玩家也可以从 3 摞牌堆顶选择一张未被翻开的发展卡牌保留】",
      "【也可以从牌堆顶保留】\n"
      "  真做一次：`transfer` 从 `<development_deck_level_1>` 顶取一张 → 保留区（**朝下**）+ 再拿一枚黄金。\n"
      "  · 取的是牌堆顶：`transfer` 从牌堆取件取的就是 order 最小的那张（= 顶牌），脚本不用点名。\n"
      "  · 这一批之前把牌堆真卡表加到了 5 张/级，所以这次翻出来的是一张**真卡**（垫牌只是撑厚度）。\n"
      "  · 画面上它朝下 —— 观众和对手一样看不到它是哪张（这正是下一条要讲的信息）。",
      {"镜头推近牌堆": "0.00", "从牌堆顶保留": "1.40", "拿第二枚黄金": "3.20"},
      part(None, T(market=11, gold=4, hold_gold=1, reserved=1)),
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      [wait(0.0, camera="deck_level_1,deck_level_2,deck_level_3",
            padding=1.4),
       ev(1.4, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<top_draw>",
          source=["<development_deck_level_1>"], destination=RES, quantity=1, to="face_down",
          what={"concept": "development_card_level_1"}),
       ev(3.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<gold_supply>"], destination="<player_holding>", quantity=1,
          what={"concept": "gold"}),
       hl(4.6, zone=RES, order=1)]))

# 5-13 注意事项
A(cue("action.reserve.notes_intro.001", "action.reserve.deck.001",
      "【注意事项如下】",
      "【注意事项：先把保留区指出来】\n"
      "  1.6 秒的过渡句：镜头给保留区（现在有 2 张，都朝下），下面几条都在讲它。没有状态变化。",
      {"镜头给保留区": "0.00"},
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      [wait(0.0, camera=RES, padding=1.6), hl(0.6, zone=RES)]))

A(cue("action.reserve.limit_gold.001.1", "action.reserve.notes_intro.001",
      "【拿取黄金后，如果已持有的宝石和黄金总数超过 10 枚，也需要弃至还剩 10 个为止】",
      "【注意事项一：拿黄金也可能超上限】\n"
      "  规则说明，**没有状态变化**：现在持有 7 枚（5 宝石 + 2 黄金），还远没到 10 ——"
      "所以这里只点持有区说明『它和宝石一起算在 10 枚里』。\n"
      "  ⚠ 不演示『超过 10 再弃回』：那要凭空多出 4 枚（口播没让人拿），违反"
      "『脚本没写有的就是没有』+ 每色 7 枚的账。与 3.1 的 limit 那条同一处理。",
      {"点持有区": "0.80 / 2.60"},
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      [wait(0.0, camera="player_holding", padding=1.6),
       hl(0.8, zone="<player_holding>"), hl(2.6, zone="<player_holding>")]))

A(cue("action.reserve.limit_gold.001.2", "action.reserve.limit_gold.001.1",
      "【如果供应堆中没有黄金了，也可以选择保留卡牌，但不拿取黄金】",
      "【注意事项二：黄金拿完了也能保留，只是不给黄金】\n"
      "  规则说明，**没有状态变化**：镜头给黄金供应堆（还有 3 枚）→ 点它、再点保留区。\n"
      "  ⚠ 不演示『黄金没了』：那要把供应堆掏空（口播没让人拿那 3 枚）。"
      "规则本身靠口播说清，画面只指位置。",
      {"镜头黄金供应堆": "0.00", "点黄金堆": "0.80", "点保留区": "2.60"},
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      [wait(0.0, camera="gold_supply", padding=1.6), hl(0.8, zone="<gold_supply>"),
       hl(2.6, zone=RES)]))

A(cue("action.reserve.limit_hand.001.1", "action.reserve.limit_gold.001.2",
      "【每位玩家最多同时保留 3 张发展卡牌】",
      "【注意事项三：最多保留 3 张】\n"
      "  规则说明（前半句），**没有状态变化**：点保留区（现在 2 张）—— 下一句再加一张就正好到 3。",
      {"点保留区": "0.60"},
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      [wait(0.0, camera=RES, padding=1.6), hl(0.6, zone=RES)]))

A(cue("action.reserve.limit_hand.001.2", "action.reserve.limit_hand.001.1",
      "【如果一位玩家已经保留了 3 张发展卡牌，就不能继续保留了。此时他必须选择其他行动】",
      "【到 3 张就满了】\n"
      "  真做一次：再从市场保留一张（二级红 → 保留区朝下）+ 拿第三枚黄金 → 保留区正好 3 张。\n"
      "  『不能继续保留』这一层**没法演**：我们没有否定原语（没有拒绝/变灰），"
      "只能靠『正好到 3 张』这个可见事实 + 口播。\n"
      "  ⚠ 这是本节演示的最后一张：3 张后不再演示保留（也正好符合规则）。",
      {"再从市场保留一张": "1.60", "拿第三枚黄金": "3.20", "点满 3 张的保留区": "4.60"},
      part(None, T(market=11, gold=3, hold_gold=2, reserved=2, deck1=34)),
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      [wait(0.0, camera="card_market", padding=1.6),
       ev(1.6, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<card_market>"], destination=RES, quantity=1, to="face_down", what=L2_RUBY),
       ev(3.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<gold_supply>"], destination="<player_holding>", quantity=1,
          what={"concept": "gold"}),
       hl(4.6, zone=RES)]))

A(cue("action.reserve.private.001.1", "action.reserve.limit_hand.001.2",
      "【已保留的发展卡牌属于个人信息，只有保留这张卡的玩家才可以确认卡牌内容】",
      "【保留的牌是个人信息】\n"
      "  规则说明，**没有状态变化**：镜头推近保留区（3 张都朝下）—— 画面本身就说明了这件事："
      "观众看得见『有几张』，看不见『是哪张』。",
      {"镜头推近保留区": "0.00", "点三张": "0.80 / 1.80 / 2.80"},
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      [wait(0.0, camera=RES, padding=1.6),
       hl(0.8, zone=RES, order=0), hl(1.8, zone=RES, order=1), hl(2.8, zone=RES, order=2)]))

A(cue("action.reserve.private.001.2", "action.reserve.private.001.1",
      "【不可以给其他玩家看，其他玩家也不可以主动去查看】",
      "【不给别人看、别人也不能翻】\n"
      "  规则说明，**没有状态变化**：继续停在保留区近景，点一下。\n"
      "  ⚠ 『不可以』两层意思（不给看 / 别人不主动翻）都没有对应画面 —— 仍然是没有否定原语。",
      {"点保留区": "0.60"},
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      [wait(0.0, camera=RES, padding=1.6), hl(0.6, zone=RES)]))

A(cue("action.reserve.private.002", "action.reserve.private.001.2",
      "【如果选择从牌堆顶保留卡牌，在保留之前不可以查看那张卡牌的信息】",
      "【从牌堆顶保留：保留之前也不能看】\n"
      "  规则说明，**没有状态变化**：镜头给三摞牌堆（都朝下）→ 再给保留区（也朝下）。"
      "『保留前不能看』与『保留后只能自己看』在画面上是同一件事：那张牌一直朝下。\n"
      "  ⚠ 认不出哪张是从牌堆保留的：三张保留牌都朝下，没有『来自牌堆』的标记 ——"
      "要区分得给件加来源标记（本作暂时不需要）。",
      {"镜头给牌堆": "0.00", "点牌堆顶": "1.00", "点保留区": "3.00"},
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      [wait(0.0, camera="deck_level_1,deck_level_2,deck_level_3",
            padding=1.4),
       hl(1.0, zone="<development_deck_level_1>"),
       wait(3.0, camera=RES, padding=1.6), hl(3.2, zone=RES)]))

A(cue("action.reserve.not_purchase.001", "action.reserve.private.002",
      "【保留不是购买，玩家保留一张发展卡牌时，不能获得它带来的折扣和声望值】",
      "【保留 ≠ 购买：没有折扣、没有声望】\n"
      "  规则说明，**没有状态变化**：先点保留区（3 张朝下的牌），再点发展区（真正买到的那 9 张）——"
      "对比就是这句话：只有进了发展区的牌才给折扣和声望。\n"
      "  保留区的牌**不在**发展区，所以怎么点发展区都数不到它们。",
      {"镜头回整桌": "0.00", "点保留区": "0.80", "点发展区": "2.40"},
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      part(None, T(market=10, gold=2, hold_gold=3, reserved=3, deck1=34)),
      [wait(0.0, camera="board"), hl(0.8, zone=RES), hl(2.4, zone=DEV)]))


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
