#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第七批：把新原语回填到已有 cue（用户 5 条指示的落地）。

① 部位指示物：卡面「左上角声望 / 左下角价格 / 右上角折扣」、贵族「声望 / 条件」
   → `{"action":"point","part":"...","indicator":"arrow"|"circle"}`，
     替掉原来"只能整张牌点亮"的做法。
② 否定：`indicator:"forbid"`（圈+斜杠）/ `"cross"`。用在
   · 3.1 三堆只有 3 枚 → 拿不了两枚（forbid）
   · 3.2.2 满足条件的贵族不能留着不拿（forbid）
   · 3.2.2 不能从玩家B那里拿贵族（cross 在 player_b_nobles 上）
   · 3.3 保留满 3 张后不能再保留（forbid 在保留区）
   （「错误示范 + 叉掉」需要"撤销"原语，见 note —— 现在只做符号那一半。）
③ 玩家B 的区已经建好：source.001 那句终于有东西可指了。
④ 结算：用 circle 把发展区每张牌的声望 + 三块贵族的声望圈起来（红圈）。
⑤ 真付一次钱：3.4 买三级白（读图：3白+3红+6黑，发展区折扣白5/红4/蓝2 → 实付 6 黑）——
   先 create 3 枚黑补齐，再把 3 黄金 + 3 黑付回供应堆（同一帧完成，手上稳态不超 10），
   然后把牌搬进发展区、圈出它的价格与声望。
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
doc = json.loads(ANIM.read_text(encoding='utf-8'))
by_id = {c['cue']: c for c in doc['cues']}


# ── ⓪ 舞台有了玩家A/B 两份持有区 → 演示里那句本体引用变歧义，改成点名 zone ──
_take = by_id['action.take.different.001']
for _e in _take['events']:
    for _k in ('zone', 'destination'):
        if _e.get(_k) == '<player_holding>':
            _e[_k] = 'player_holding'
_note_take = ("\n  【2026-09 补】演示玩家持有区改成写死 zone id `player_holding`："
              "舞台现在有玩家A/玩家B 两个持有区（concept 都是 <player_holding>），"
              "写本体引用会歧义 —— 涉及多份实例的引用，本体身份不够用，得点名 zone。")
if _note_take.strip()[:24] not in _take.get('note', ''):
    _take['note'] = _take.get('note', '') + _note_take

TAGS = {
    'cost.001.1': ('cost', 'arrow'),
}


def ev(at, action, dur=0.0, **kw):
    e = {"at": at, "dur": dur, "action": action}
    e.update(kw)
    return e


def pt(at, part=None, indicator="arrow", zone=None, order=None, what=None, dur=0.0):
    """指示物事件（箭头/圈/禁止/叉）。"""
    e = ev(at, "point", dur)
    if part:
        e["part"] = part
    if indicator:
        e["indicator"] = indicator
    if zone:
        e["zone"] = zone
    if order is not None:
        e["order"] = order
    if what:
        e["what"] = what
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


def set_cue(cid, events, note_add=None, note_replace=None):
    c = by_id[cid]
    c['events'] = events
    if note_replace is not None:
        c['note'] = note_replace
    elif note_add:
        # 幂等：已经加过就不再加
        if note_add.strip()[:24] not in c.get('note', ''):
            c['note'] = c.get('note', '') + note_add


PART_NOTE = ("\n  【2026-09 用户指示①：加了『部位的相对坐标』+ 指示物原语】\n"
             "  原来这里只能把整张牌点亮 —— 因为高亮是「整件/整区」，而口播在说「左上角/左下角/右上角」。\n"
             "  现在 stage 的模板上有了 `part_anchors`（声望/价格/折扣/条件各自的**相对件中心**偏移，\n"
             "  世界单位、件平面内），脚本写 `{\"action\":\"point\",\"part\":\"cost\",\"indicator\":\"arrow\"}`\n"
             "  就能把箭头指到那个角上；牌被搬到哪、转多少度，指示物跟着走。\n"
             "  部位坐标只在模板里（相对件），桌面坐标仍然只属于 zone —— 两层各管各的。")

# ── ① 3.2.1 卡面解剖：改用指示物 ────────────────────────────────────────────
set_cue('action.cards.cost.001.1', [
    wait(0.0, camera="showcase,showcase_1", padding=1.5),
    pt(0.6, part="cost", indicator="arrow", zone="showcase"),
    pt(1.6, part="cost", indicator="circle", zone="showcase"),
    pt(3.0, part="cost", indicator="arrow", zone="showcase"),
], note_add=PART_NOTE)

set_cue('action.cards.cost.001.2', [
    pt(0.5, part="cost", indicator="circle", zone="showcase"),
    wait(2.6, camera="supply"),
], note_add="\n  （这一条的后半句把镜头切到供应堆：价格圈出来的那些宝石，付掉之后就是回到这里。）")

set_cue('action.cards.cost.001.3', [
    wait(0.0, camera="card_market", padding=1.6),
    hl(0.6, zone="<card_market>"),
    wait(3.2, camera="board"),
    pt(3.6, part="cost", indicator="arrow", zone="player_development"),
    pt(4.4, part="cost", indicator="arrow", zone="player_development", order=0),
], note_add="\n  （『拿取并放在自己面前』→ 箭头最后落到自己面前那些牌上。）")

set_cue('action.cards.prestige.001.1', [
    wait(0.0, camera="showcase,showcase_1", padding=1.5),
    pt(0.8, part="prestige", indicator="arrow", zone="showcase_1"),
    pt(1.8, part="prestige", indicator="circle", zone="showcase_1"),
    pt(3.0, part="prestige", indicator="arrow", zone="showcase_1"),
], note_add=PART_NOTE)

set_cue('action.cards.prestige.001.2', [
    wait(0.0, camera="showcase,showcase_1", padding=1.5),
    pt(0.6, part="prestige", indicator="circle", zone="showcase_1"),
    pt(1.8, part="prestige", indicator="cross", zone="showcase"),
    wait(3.2, camera="card_market", padding=1.6),
    pt(3.4, part="prestige", indicator="cross", zone="<card_market>", order=0),
    pt(3.8, part="prestige", indicator="cross", zone="<card_market>", order=3),
], note_add="\n  （用**叉**表示「这张一级牌没有声望值」：叉在左上角那个位置上。）")

set_cue('action.cards.discount.001', [
    wait(0.0, camera="showcase,showcase_1", padding=1.5),
    pt(0.8, part="bonus", indicator="arrow", zone="showcase"),
    pt(1.8, part="bonus", indicator="circle", zone="showcase"),
    pt(3.0, part="bonus", indicator="arrow", zone="showcase"),
], note_add=PART_NOTE)

# ── ② 3.2.2 贵族：指部位 + 否定 ─────────────────────────────────────────────
set_cue('action.nobles.value.001.1', [
    wait(0.0, camera="noble_market", padding=1.25),
    pt(0.6, part="prestige", indicator="arrow", zone="<noble_market>", order=0),
    pt(1.6, part="prestige", indicator="circle", zone="<noble_market>", order=0),
    pt(2.6, part="condition", indicator="arrow", zone="<noble_market>", order=0),
    pt(3.6, part="condition", indicator="circle", zone="<noble_market>", order=0),
], note_add=PART_NOTE)

set_cue('action.nobles.forced.001.2', [
    wait(0.0, camera="noble_market", padding=1.25),
    pt(0.6, indicator="forbid", zone="<noble_market>", order=0),
    pt(1.6, indicator="forbid", zone="<noble_market>", order=1),
    pt(2.6, zone="player_nobles", order=0),
], note_add=("\n  【2026-09 用户指示②：否定用禁止符号】供应堆里满足条件的那两块打上禁止符号 ="
             "『不能留着不拿』。\n  ⚠ 另一半（『错误示范然后叉掉』）还做不了：错误示范会改状态，"
             "而现在没有撤销/回滚原语 —— 要做就得先有『临时状态』这一层。"))

set_cue('action.nobles.source.001', [
    wait(0.0, camera="noble_market,player_b_nobles", padding=1.5),
    hl(0.8, zone="<noble_market>"),
    pt(2.0, indicator="cross", zone="player_b_nobles", order=0),
    pt(2.6, indicator="cross", zone="player_b_nobles", order=1),
    pt(3.2, indicator="cross", zone="player_b_nobles", order=2),
], note_add=("\n  【2026-09 用户指示③：加了玩家B 的区】这一句以前没法演 —— 桌面只有一位玩家。"
             "现在 `player_b_nobles`（对面那位的贵族）存在了，用**叉**把 B 的贵族叉掉 ="
             "『不可以从其他人那里拿』。镜头同时框住两边的贵族区，对比才成立。"))

set_cue('action.reserve.limit_hand.001.2', [
    wait(0.0, camera="card_market", padding=1.6),
    ev(1.6, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
       source=["<card_market>"], destination="player_reserved", quantity=1, to="face_down",
       what={"concept": "development_card_level_2",
             "parts": [{"key": "bonus", "value": "<ruby>"}]}),
    ev(3.2, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
       source=["<gold_supply>"], destination="player_holding", quantity=1,
       what={"concept": "gold"}),
    hl(4.6, zone="player_reserved"),
    pt(5.2, indicator="forbid", zone="player_reserved"),
], note_add=("\n  【2026-09 用户指示②】保留区满 3 张后打上禁止符号 =『不能再保留』。"))

# ── ④ 第 4 节结算：红圈圈出所有声望 ─────────────────────────────────────────
_score_note = ("\n  【2026-09 用户指示④：结算就是圈声望】发展区每张牌的左上角 + 每块贵族的左上角，"
               "用**红圈**一起圈出来 —— 两部分加起来就是总分。\n"
               "  用的是①的部位指示物：`point` 带 `zone` 会**对区域里每一件**都画一个圈。")
set_cue('endgame.trigger.001', [
    wait(0.0, camera="player_development", padding=1.8),
    ev(0.8, "create", destination="player_development", template="market_card_3_sapphire",
       palette="card_level_3",
       what={"concept": "development_card_level_3",
             "parts": [{"key": "bonus", "value": "<sapphire>"}]}),
    hl(2.4, zone="player_development"),
    pt(3.0, part="prestige", indicator="circle", zone="player_development"),
    wait(4.2, camera="player_nobles", padding=1.6),
    pt(4.6, part="prestige", indicator="circle", zone="player_nobles"),
], note_add=_score_note)

set_cue('endgame.winner.001', [
    wait(0.0, camera="board"),
    pt(0.6, part="prestige", indicator="circle", zone="player_development"),
    pt(1.6, part="prestige", indicator="circle", zone="player_nobles"),
], note_add=_score_note)

set_cue('endgame.tie.001.1', [
    wait(0.0, camera="player_development", padding=1.8),
    pt(0.6, part="prestige", indicator="circle", zone="player_development"),
    hl(1.8, zone="player_development"),
], note_add=_score_note + "\n  （同分时比的是**买牌张数**：这一条把发展区整体点亮，圈的是声望，点的是张数。）")

# ── ⑤ 3.4 真付一次钱 ───────────────────────────────────────────────────────
set_cue('action.purchase_reserved.002.1', [
    wait(0.0, camera="player_reserved", padding=1.6),
    hl(0.8, zone="player_reserved", order=0),
    # 实付 6 黑（三级白 3白+3红+6黑；发展区折扣 白5/红4/蓝2 → 白红全抵，只剩 6 黑）
    ev(1.6, "create", destination="player_holding", template="gem", palette="gem_onyx",
       count=3, what={"concept": "gem", "parts": [{"key": "color", "value": "<onyx>"}]}),
    ev(2.0, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
       source=["player_holding"], destination="<gem_supply|color=<onyx>>", quantity=3,
       what={"concept": "gem", "parts": [{"key": "color", "value": "<onyx>"}]}),
    ev(2.0, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
       source=["player_holding"], destination="<gold_supply>", quantity=3,
       what={"concept": "gold"}),
    # 付完再把牌翻开搬进发展区（口播顺序：先支付，再翻开）
    ev(3.4, "transfer", dur=0.7, easing="easeInOutCubic", realizes="<ontology::transfer>",
       source=["player_reserved"], destination="player_development", quantity=1, to="face_up",
       what={"concept": "development_card_level_3",
             "parts": [{"key": "bonus", "value": "<diamond>"}]}),
    pt(4.6, part="cost", indicator="circle", zone="player_development", order=10),
], note_replace=None)
c = by_id['action.purchase_reserved.002.1']
c['note'] = (
    "【买下保留的那张：这一次**真的付钱**了（用户指示⑤）】\n"
    "  那张三级白的实际价格（读图）：**3白 + 3红 + 6黑**。发展区现在的折扣是白 5、红 4、蓝 2\n"
    "  （5 张白牌 + 4 张红牌 + 2 张蓝牌），白红被折扣全抵掉 → **实付 6 枚黑宝石**。\n"
    "  所以动画做三件事：\n"
    "    ① `create` 3 枚黑进持有区（口播 `支付费用` 的前提是「你得付得出」，用户批准了这种补法）；\n"
    "    ② 把 **3 枚黄金 + 3 枚黑** 付回供应堆（黄金回黄金堆、黑回黑堆）——\n"
    "       这两笔和①**同一帧**完成：否则持有区会短暂到 11 枚，与它自己讲的『上限 10』打架；\n"
    "    ③ 牌从保留区搬进发展区（`to: face_up` 就是口播说的『翻开』），再圈出它的价格位置。\n"
    "  数学上为什么是 6 黑：价格 12 枚 − 折扣抵掉的 3白+3红（折扣白 5 ≥ 3、红 4 ≥ 3）→ 6 黑；\n"
    "  蓝折扣 2 用不上。这套算式**没有写进动画**（没有分数/账单 UI），画面上就是"
    "『补 3 黑 → 付 3 金 3 黑 → 牌翻开进发展区』，与口播的『支付费用』一致。")

ANIM.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('batch7 完成：卡面指示物 6 条、贵族 3 条、否定 3 处、结算红圈 3 条、真付款 1 条')
