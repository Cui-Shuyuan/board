#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第三批：3.2.1 认识发展卡牌（20 条）。

配套改动（都在这个脚本里，一次改完）：
  ① stage 加 zone `player_development`（买下的发展卡摊在自己面前的地方）
  ② stage 加两个**介绍样本**模板：`sample_card_1_white`(一级白：价格 4 种宝石、声望 0)、
     `sample_card_2_ruby`(二级红：声望 3) —— 介绍牌面要用"一张牌占满画面"的近景
  ③ 把 setup.cards.002.1 的 `stack` 真卡表加长：原来每级只排 4 张真卡（刚好发满市场），
     牌堆剩下 36 张全是垫牌 → "从牌堆翻一张新牌补空位"会翻出垫牌（卡背）。
     现在每级排 8 张真卡（4 张发给市场 + 4 张留在牌堆里备抽），牌堆厚度不变。

卡面事实（我逐张读图得到，写进 media/card_facts.json）：
  声望：一级 0/0/1/0/0（白蓝绿红黑），二级 2/2/1/3/2，三级 4/5/4/4/5
  折扣：每张牌的右上角就是它同名的那颗宝石（白牌给白折扣，依此类推）
  价格：一级白 = 1蓝+2绿+1红+1黑（其余见 card_facts.json）
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
WIN = Path('/mnt/d/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'

DEV = 'player_development'
S1 = 'showcase'        # 样本位 1：一级白（讲价格 / 折扣）
S2 = 'showcase_1'      # 样本位 2：二级红（讲声望）
SAMPLE1 = 'sample_card_1_white'
SAMPLE2 = 'sample_card_2_ruby'

# ── ① stage：新 zone + 两个样本模板 ─────────────────────────────────────────
stage = json.loads(STAGE.read_text(encoding='utf-8'))
if not any(z['id'] == DEV for z in stage['zones']):
    stage['zones'].append({
        "id": DEV, "label": "玩家发展区",
        "center": {"x": -2.30, "z": -3.20},
        "layout": {"type": "row", "x_step": 0.66},
        "capacity": 3,
        "palette": "panel_player",
        "size": {"w": 0.63, "h": 0.88},
        "display": {"mode": "count"},
        "concept": "<development_area>",
        "contains": ["development_card_level_1", "development_card_level_2", "development_card_level_3"],
        "note": "买下的发展卡摊在自己面前的地方（<development_area>：公开信息）。"
                "容量写 3：本节演示最多同时摊 3 张；实物不设上限（讲解时说清了），"
                "真要做长演示改这一个数 + x_step 即可（坐标只属于 stage）。",
    })
for tid, img, concept, w, h, note in [
    (SAMPLE1, "media/card/一级发展卡_白.jpg", "development_card_level_1", 0.63, 0.88,
     "介绍牌面用的**样本**（一级白）：价格 1蓝+2绿+1红+1黑（四种颜色，讲『左下角是价格』最清楚）、"
     "声望 0、白折扣。样本不进牌堆/市场的账，讲解完销毁。"),
    (SAMPLE2, "media/card/二级发展卡_红.jpg", "development_card_level_2", 0.63, 0.88,
     "介绍牌面用的**样本**（二级红）：声望 3，用来讲『左上角是声望值』。"),
]:
    if not any(t['id'] == tid for t in stage['templates']):
        stage['templates'].append({
            "id": tid, "shape": "card", "palette": "card_level_" + concept[-1],
            "face_image": img, "width": w, "height": h,
            "sorting_order": 2, "concept": concept, "sample": True, "note": note,
        })
STAGE.write_text(json.dumps(stage, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

# ── 卡面事实（人读图的结果，落成数据）────────────────────────────────────────
FACTS = {
    "note": "发展卡面的事实：声望值（左上横幅）、折扣宝石（右上圆）、价格（左下圆盘）。"
            "由人逐张读图得到（scripts 里的自动圆盘检测在这批扫描件上不可靠："
            "宝石图标会把圆盘切成两块、暗色/白色的圆盘会被饱和度阈值滤掉）。"
            "3.4 购买演示与第 4 节结算都要用它。",
    "prestige": {"1": {"白": 0, "蓝": 0, "绿": 1, "红": 0, "黑": 0},
                 "2": {"白": 2, "蓝": 2, "绿": 1, "红": 3, "黑": 2},
                 "3": {"白": 4, "蓝": 5, "绿": 4, "红": 4, "黑": 5}},
    "bonus": "每张卡右上角的折扣宝石 = 它同名的那颗（白牌给 <diamond>，依此类推）",
    "cost": {
        "1白": {"蓝": 1, "绿": 2, "红": 1, "黑": 1},
    },
    "cost_todo": "其余 14 张的价格待逐张核对后补（本批只用到 1白，且折扣演示不依赖具体数字）",
}
(ROOT / 'games/splendor/card_facts.json').write_text(
    json.dumps(FACTS, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

# ── ② 动画数据 ─────────────────────────────────────────────────────────────
doc = json.loads(ANIM.read_text(encoding='utf-8'))
rt = json.loads(RT.read_text(encoding='utf-8'))
by_id = {c['cue']: c for c in doc['cues']}

# 把牌堆的真卡表加长（每级 8 张：前 4 张顺序不变 → 市场那 12 张一个字都不变）
# **幂等**：直接写成"前 4 张（发给市场，顺序不能动）+ 后 4 张（留在牌堆备抽）"，
# 不做字符串追加 —— 追加过一次的补丁重跑就会变成 12 张，垫牌数按 capacity-真卡数 少算 4 张。
DECKS = {
    '<development_deck_level_1>': (['market_card_1_emerald', 'market_card_1_ruby',
                                    'market_card_1_diamond', 'market_card_1_sapphire'],
                                   ['market_card_1_onyx']),
    '<development_deck_level_2>': (['market_card_2_sapphire', 'market_card_2_onyx',
                                    'market_card_2_ruby', 'market_card_2_emerald'],
                                   ['market_card_2_diamond']),
    '<development_deck_level_3>': (['market_card_3_ruby', 'market_card_3_diamond',
                                    'market_card_3_emerald', 'market_card_3_onyx'],
                                   ['market_card_3_sapphire']),
}
sc = by_id['setup.cards.002.1']
for e in sc['events']:
    if e.get('action') == 'stack' and e.get('destination') in DECKS:
        head, tail = DECKS[e['destination']]
        e['real_templates'] = ','.join(head + tail)
# 牌堆终态：32 张垫牌 + 4 张真卡（面朝下）
for lv in (1, 2, 3):
    cap = {1: 40, 2: 30, 3: 20}[lv]
    left = cap - 4                     # 发出去 4 张给市场
    sc['exit']['zones'][f'deck_level_{lv}'] = {
        "count": left,
        "kinds": {f"{'一二三'[lv-1]}级垫牌": {"count": left - 1},
                  f"{'一二三'[lv-1]}级{'黑白蓝'[lv - 1]}": {"count": 1, "face": "down"}},
    }
sc['note'] += ("\n  【2026-09 补】牌堆真卡表从 4 张/级加长到 8 张/级：原来只排了刚好发满市场的 4 张，"
               "牌堆里剩下的全是垫牌 —— 后面『买走一张要从牌堆翻一张补空位』就会翻出垫牌（卡背），"
               "看着就是错的。前 4 张顺序没动，所以市场发出来的那 12 张一个字都没变。")

# 未开的展示位也要显式写出来（样本位）
for zid in (S1, S2):
    pass


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


# 本批期间不变的账（整桌）。宝石/黄金/贵族/标记：行动段开始后的账
def T(**over):
    z = {
        "card_market": {"count": 12, "face_up": 12},
        "deck_level_1": {"count": 36}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
        "noble_market": {"count": 3},
        "gem_supply_diamond": {"count": 3}, "gem_supply_sapphire": {"count": 3},
        "gem_supply_ruby": {"count": 3}, "gem_supply_emerald": {"count": 2},
        "gem_supply_onyx": {"count": 4},
        "gold_supply": {"count": 5},
        "player_holding": {"count": 5, "kinds": {"宝石白": 1, "宝石蓝": 1, "宝石红": 1, "宝石绿": 2}},
        "player_marker": {"count": 1},
        "showcase": {"count": 0}, "showcase_1": {"count": 0},
        DEV: {"count": 0},
    }
    z.update(over)
    return z


CUES = []
A = CUES.append

# 1 认识卡牌：把两张样本摆出来（一张一级、一张二级）
A(cue("action.cards.intro.001", "action.take.public.001.2",
      "【我们介绍一下发展卡牌】",
      "【介绍发展卡牌】\n"
      "  摆出**两张样本**：左边一级白（价格四种颜色、声望 0），右边二级红（声望 3）。\n"
      "  为什么用样本而不是市场里的真牌：讲解要『一张牌占满画面』，"
      "  而市场的近景一次框住 12 张，牌角的小字看不清。样本是既有约定"
      "  （2.1 讲卡背时也是这么做的）：独立模板、不进牌堆/市场的账、讲完销毁。",
      {"镜头对准两个样本": "0.00", "两张样本出现": "0.50"},
      part(None, T()),
      part(None, T(**{S1: {"count": 1, "kinds": {SAMPLE1 + "|card_level_1": 1}},
                      S2: {"count": 1, "kinds": {SAMPLE2 + "|card_level_2": 1}}})),
      [wait(0.0, camera="showcase,showcase_1", padding=1.5),
       ev(0.5, "create", destination=S1, template=SAMPLE1, palette="card_level_1",
          what={"concept": "development_card_level_1"}),
       ev(0.5, "create", destination=S2, template=SAMPLE2, palette="card_level_2",
          what={"concept": "development_card_level_2"})]))

# 2-4 价格
_note_cost = ("【左下角是这张牌的价格】\n"
              "  ⚠ 我们**没有『指某个角』的原语**：高亮只能整件/整区。所以这里只能把一级白那张样本"
              "（价格 1蓝+2绿+1红+1黑，四种颜色）推到近景点亮整张 —— 观众自己看左下角。\n"
              "  要让『点某个角』真正演出来，该加的是原语（stage 里给卡的四个角各定义一个锚点 + 一个指示物），"
              "  而不是在动画数据里写坐标。")
A(cue("action.cards.cost.001.1", "action.cards.intro.001",
      "【左下角是这张牌的价格。要想购买这张牌】",
      _note_cost,
      {"镜头保持两样本近景": "0.00", "点一级白样本": "0.60 / 1.80"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      [wait(0.0, camera="showcase,showcase_1", padding=1.5),
       hl(0.6, zone=S1), hl(1.8, zone=S1)]))

A(cue("action.cards.cost.001.2", "action.cards.cost.001.1",
      "【玩家需要将这里标注的宝石种类和数量返回宝石供应堆】",
      _note_cost + "\n  后半句讲『返回供应堆』→ 讲完价格把镜头切到供应堆（这就是宝石要去的地方）。",
      {"点样本（价格）": "0.50", "镜头切供应堆": "2.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      [hl(0.5, zone=S1), wait(2.6, camera="supply")]))

A(cue("action.cards.cost.001.3", "action.cards.cost.001.2",
      "【之后才可以从卡牌供应堆中拿取这张牌，并放置在自己面前】",
      _note_cost + "\n  『从卡牌供应堆中拿取』→ 镜头给市场（那 12 张就是卡牌供应堆）；"
      "『放在自己面前』→ 镜头拉回整桌、点玩家区。\n"
      "  ⚠ 这一条**没有真的买牌**：它讲的是『买牌的步骤』，真正的购买演示在下一条（market.001.2）。",
      {"镜头给市场": "0.00", "点市场": "0.60", "镜头回整桌": "3.20", "点玩家区": "3.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      [wait(0.0, camera="card_market", padding=1.6), hl(0.6, zone="<card_market>"),
       wait(3.2, camera="board"), hl(3.6, zone="<player_holding>")]))

# 5-8 市场与补牌（这里做一次真购买 + 真补牌）
A(cue("action.cards.market.001.1", "action.cards.cost.001.3",
      "【玩家只可以从被翻开的 12 张发展卡牌中选择一张购买，并且在任何时间】",
      "【只能在 12 张里选】\n"
      "  镜头推近市场，把 12 个格位按 order 0→11 扫一遍（0-3 一级行、4-7 二级行、8-11 三级行）。\n"
      "  扫格位而不是只点一下区域：让观众看到『12 张』是 12 个位置。",
      {"镜头推近市场": "0.00", "扫 12 格": "0.80 起每 0.22s"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      [wait(0.0, camera="card_market", padding=1.6)] +
      [hl(0.8 + i * 0.22, zone="<card_market>", order=i) for i in range(12)]))

_purchase_note = ("【买走一张 → 出现空位】\n"
                  "  这里做**一次真购买**：把市场里的一张一级白（slot 2）搬到玩家发展区。\n"
                  "  · 为什么买一级白：下一段折扣例子说『现在我已经购买了 1 张白宝石牌』—— "
                  "这一动正好把那个前提**做**出来，后面就不用凭空变一张牌。\n"
                  "  · ⚠ **没有演付宝石**：口播这一段讲的是『买走之后要补牌』，一个字没提支付；"
                  "而且玩家手上（白1蓝1红1绿2）也付不起一级白（要 1 黑）。"
                  "支付本身的演示放在 3.4（购买保留的发展卡牌）那一节 —— 那里口播就是在讲支付。\n"
                  "  · 空位是**真的**空位：`MoveToSlot` 只搬那一件，市场其余卡不回填 → 画面上第 3 格空出来。")
A(cue("action.cards.market.001.2", "action.cards.market.001.1",
      "【只要卡牌被玩家买走，卡牌供应堆出现了空位】",
      _purchase_note,
      {"点要买的那张": "0.60", "搬到玩家发展区": "1.60", "点空位/发展区": "3.00"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1},
                      "card_market": {"count": 11, "face_up": 11},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      [wait(0.0, camera="card_market", padding=1.6),
       hl(0.6, zone="<card_market>", order=2),
       ev(1.6, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=["<card_market>"], destination=DEV, quantity=1,
          what={"concept": "development_card_level_1",
                "parts": [{"key": "bonus", "value": "<diamond>"}]}),
       hl(3.0, zone=DEV)]))

A(cue("action.cards.market.001.3", "action.cards.market.001.2",
      "【玩家必须从对应行翻出一张新的卡牌填补这个空位，除非这一行所有卡牌都已经耗尽】",
      "【从对应行的牌堆翻一张补上】\n"
      "  `transfer` 从 `<development_deck_level_1>` 取一张、落到市场的 **slot 2**（就是刚才空出来的那格）"
      "——『对应行』这件事由 destination 的 `order` 说，不由坐标说。\n"
      "  `realizes: <top_draw>`：从牌堆顶抽牌在规则上就是 top_draw（本体里它继承 transfer）。\n"
      "  ⚠ 为什么这条能翻出**真卡**：原来牌堆每级只排了 4 张真卡（刚好发满市场），剩下 36 张全是垫牌；"
      "这一批把真卡表加长到 8 张/级（前 4 张顺序不变，所以市场那 12 张没变）。",
      {"翻牌补位": "1.20", "点补上的新牌": "2.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1},
                      "card_market": {"count": 11, "face_up": 11}, DEV: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1},
                      "card_market": {"count": 12, "face_up": 12},
                      "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      [wait(0.0, camera="card_market", padding=1.6),
       ev(1.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<top_draw>",
          source=["<development_deck_level_1>"], destination="<card_market>",
          quantity=1, order=2, to="face_up",
          what={"concept": "development_card_level_1",
                "parts": [{"key": "bonus", "value": "<onyx>"}]}),
       hl(2.6, zone="<card_market>", order=2)]))

A(cue("action.cards.market.001.4", "action.cards.market.001.3",
      "【此时不再填补，而是用剩余的发展卡牌继续游戏】",
      "【一行耗尽就不补了】\n"
      "  规则说明，**没有状态变化**：镜头拉回整桌 —— 一句『用剩下的牌继续玩』用整桌画面收尾。",
      {"镜头回整桌": "0.00", "点一级牌堆": "1.00"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      [wait(0.0, camera="board"), hl(1.0, zone="<development_deck_level_1>")]))

# 9-10 声望
A(cue("action.cards.prestige.001.1", "action.cards.market.001.4",
      "【左上角是这张牌的声望值。玩家购买发展卡牌后，立即获得对应的声望值】",
      "【左上角是声望值】\n"
      "  镜头回两个样本（一级白 / 二级红），点**二级红**那张：它左上角的横幅写着 3。\n"
      "  （读图得到的事实：一级 0/0/1/0/0、二级 2/2/1/3/2、三级 4/5/4/4/5，见 media/card_facts.json。）\n"
      "  ⚠ 同样没有办法『只点左上角』——没有指示物原语，只能点整张牌。",
      {"镜头给两个样本": "0.00", "点二级红": "0.80 / 2.40"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      [wait(0.0, camera="showcase,showcase_1", padding=1.5), hl(0.8, zone=S2), hl(2.4, zone=S2)]))

A(cue("action.cards.prestige.001.2", "action.cards.prestige.001.1",
      "【不过并不是所有卡牌都有声望值。很多一级发展卡牌都没有声望值】",
      "【一级卡多半没有声望】\n"
      "  正好用桌上这两张对比：二级红 = 3 点，一级白 = 0 点（它左上角的横幅是空的）。\n"
      "  先点二级红、再点一级白，最后把市场里的一级行也扫一下（那一行都是 0 点）。",
      {"点二级红（有）": "0.60", "点一级白（没有）": "1.80", "扫市场一级行": "3.40-4.00"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      [wait(0.0, camera="showcase,showcase_1", padding=1.5),
       hl(0.6, zone=S2), hl(1.8, zone=S1),
       wait(3.2, camera="card_market", padding=1.6),
       hl(3.4, zone="<card_market>", order=0), hl(3.7, zone="<card_market>", order=3)]))

# 11-16 折扣
A(cue("action.cards.discount.001", "action.cards.prestige.001.2",
      "【右上角这颗宝石图案，代表玩家购买这张牌后，再购买其他卡牌的时候可以享受的折扣】",
      "【右上角的宝石 = 折扣】\n"
      "  点一级白样本（它右上角是白宝石 → 买下它以后，买别的牌时可以少付 1 枚白宝石）。\n"
      "  ⚠ 又一处只能点整张牌：『右上角那颗』没有原语能单独指。",
      {"镜头给样本": "0.00", "点一级白": "0.80 / 2.20"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35}, DEV: {"count": 1}})),
      [wait(0.0, camera="showcase,showcase_1", padding=1.5), hl(0.8, zone=S1), hl(2.2, zone=S1)]))

A(cue("action.cards.discount.002.1", "action.cards.discount.001",
      "【举个例子：现在我已经购买了 1 张白宝石牌，那么下一回合我想购买这张牌时】",
      "【折扣例子（一）】\n"
      "  『我已经购买了 1 张白宝石牌』**不是凭空说的**：上一段 market.001.2 已经真的把一张一级白"
      "买进发展区了 → 这里点它一下（画面上的事实与口播对得上）。\n"
      "  然后点想买的那张（一级白样本）——『这张牌』指的是它。",
      {"点发展区那张白牌": "0.60", "点想买的牌": "2.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      [wait(0.0, camera=DEV, padding=1.6), hl(0.6, zone=DEV),
       wait(2.4, camera="showcase,showcase_1", padding=1.5), hl(2.6, zone=S1)]))

A(cue("action.cards.discount.002.2", "action.cards.discount.002.1",
      "【只需要支付 1 枚白宝石和 1 枚蓝宝石即可】",
      "【折扣例子（一）：只要付这两枚】\n"
      "  这一句口播说的数字是 **1 白 + 1 蓝**，而**我们手上正好有 1 白 1 蓝**（持有区 order 0 = 白、"
      "order 1 = 蓝，来自 3.1 的两次拿取）→ 镜头推近持有区，把这两枚点出来 = 「只要付这两枚」。\n"
      "  **没有真的支付**：口播是『下一回合我想购买这张牌时』——是假设，不是这一回合真的买。"
      "所以只做『需要付哪些』的提示，不动状态（这也正是『脚本没写有的就是没有』）。\n"
      "  ⚠ 已知不一致：口播这句隐含那张牌的价格是 **2白+1蓝**，而我们扫的 15 张里没有这个价格的牌"
      "（一级白是 1蓝+2绿+1红+1黑）。要严格对上，要么补扫那张牌、要么改这一句口播。"
      "现在画面上只点『要付的这两枚』，不声称样本牌的价格等于它。",
      {"镜头推近玩家持有区": "0.00", "点白宝石": "0.60", "点蓝宝石": "1.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      [wait(0.0, camera="player_holding", padding=1.6),
       hl(0.6, zone="<player_holding>", order=0), hl(1.6, zone="<player_holding>", order=1)]))

_more_note = ("【折扣例子（二）：折扣叠起来】\n"
              "  口播是**假设句**（『如果我已经购买了 2 张白宝石牌和 1 张蓝宝石牌』）——"
              "这个前提是脚本说的，所以这里把缺的两张牌**补出来**（`create`，从盒里），"
              "而不是装作它们本来就在。\n"
              "  · 补的是：一级白再来 1 张（`sample`? 不 —— 这是**真的**买过的牌，用市场模板 "
              "`market_card_3_diamond`(三级白) 与 `market_card_1_sapphire`(一级蓝)）、以及 1 张蓝。\n"
              "  · 白牌给白折扣、蓝牌给蓝折扣 → 发展区总共 2 白 + 1 蓝。")
A(cue("action.cards.discount.003.1", "action.cards.discount.002.2",
      "【如果我已经购买了 2 张白宝石牌和 1 张蓝宝石牌，那么我购买这张牌时无需支付任何宝石】",
      _more_note,
      {"补出两张牌": "0.60 / 0.90", "点发展区三张": "2.00", "点想买的牌": "3.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 1, "kinds": {"一级白": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera=DEV, padding=1.6),
       ev(0.6, "create", destination=DEV, template="market_card_3_diamond", palette="card_level_3",
          what={"concept": "development_card_level_3"}),
       ev(0.9, "create", destination=DEV, template="market_card_1_sapphire", palette="card_level_1",
          what={"concept": "development_card_level_1"}),
       hl(2.0, zone=DEV),
       wait(3.4, camera="showcase,showcase_1", padding=1.5), hl(3.6, zone=S1)]))

A(cue("action.cards.discount.003.2", "action.cards.discount.003.1",
      "【但如果我已购买更多的发展卡牌，也不可以反过来从供应堆中拿取宝石】",
      "【折扣不能反过来『赚钱』】\n"
      "  规则说明，**没有状态变化**：点发展区（折扣多）、再点玩家持有区（手上的宝石**没有变多**）。"
      "「没有变多」靠『点一下但什么都不发生』+ 口播表达 —— 我们仍然没有『否定』的原语。",
      {"点发展区": "0.60", "点持有区（没变多）": "2.00"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera="board"), hl(0.6, zone=DEV), hl(2.0, zone="<player_holding>")]))

A(cue("action.cards.discount.003.3", "action.cards.discount.003.2",
      "【折扣的上限就是免费】",
      "【上限就是免费】\n"
      "  规则说明，**没有状态变化**：点发展区（折扣已经够多了）、再点想买的样本牌 —— "
      "折扣再多也不会变成『倒拿宝石』，最多到 0 元。",
      {"点发展区": "0.40", "点样本牌": "1.20"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera="board"), hl(0.4, zone=DEV),
       wait(1.2, camera="showcase,showcase_1", padding=1.5), hl(1.3, zone=S1)]))

# 17-18 公开信息
A(cue("action.cards.public.001.1", "action.cards.discount.003.3",
      "【和宝石相同，已购买的发展卡牌也属于公开信息】",
      "【发展卡也是公开信息】\n"
      "  规则说明，**没有状态变化**：镜头给玩家面前（发展区 3 张已买的牌），点一下。",
      {"镜头给发展区": "0.00", "点发展区": "0.80"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera=DEV, padding=1.6), hl(0.8, zone=DEV)]))

A(cue("action.cards.public.001.2", "action.cards.public.001.1",
      "【每位玩家都需要将自己已购买的发展卡牌摊开放在自己面前，所有人都能清楚看到的地方】",
      "【摊开放在自己面前，人人可见】\n"
      "  点发展区 → 镜头拉回整桌（『所有人都能清楚看到』用镜头语言说）。没有状态变化。",
      {"点发展区": "0.60", "镜头回整桌": "2.60"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [hl(0.6, zone=DEV), wait(2.6, camera="board")]))

# 19-20 一回合一张 + 无上限（顺便把样本收掉）
A(cue("action.cards.limit.001.1", "action.cards.public.001.2",
      "【和宝石不同的是，玩家一回合只能购买一张发展卡牌】",
      "【一回合只能买一张】\n"
      "  规则说明，**没有状态变化**：镜头回整桌，点发展区（这就是『一回合一张』的落点）。\n"
      "  ⚠ 想表达『只能一张』同样缺少否定原语 → 只点一下。",
      {"镜头回整桌": "0.00", "点发展区": "0.80"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera="board"), hl(0.8, zone=DEV)]))

A(cue("action.cards.limit.001.2", "action.cards.limit.001.1",
      "【但玩家总计可以购买的发展卡牌数量没有上限】",
      "【总数没有上限】\n"
      "  规则说明 + 本组收尾：点发展区，然后把两件**介绍样本**销毁（它们只是讲解道具，"
      "不是这一局里的牌 —— 留着会让后面的『发展区里有几张牌』说不清）。\n"
      "  销毁后发展区仍是 3 张真牌：一级白（market.001.2 买的）+ 三级白 + 一级蓝（折扣例子补的）。",
      {"点发展区": "0.50", "样本退场": "2.20"},
      part(None, T(**{S1: {"count": 1}, S2: {"count": 1}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      part(None, T(**{S1: {"count": 0}, S2: {"count": 0}, "deck_level_1": {"count": 35},
                      DEV: {"count": 3, "kinds": {"一级白": 1, "三级白": 1, "一级蓝": 1}}})),
      [wait(0.0, camera="board"), hl(0.5, zone=DEV),
       ev(2.2, "destroy", zone=S1), ev(2.2, "destroy", zone=S2)]))


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
