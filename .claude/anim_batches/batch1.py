#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第一批动画（19 条）：背景介绍 7 + 2.1 收尾 1 + 2.3 收尾 1 + 2.4 起始玩家 3 + 2.5 设置完成 5 + 3 行动总述 2。

同时给 stage 加一件实物：起始玩家标记（template `starting_marker` + zone `player_marker`）。
写回 anim/full.json 时**按运行时轨道顺序**重排 cues，保证 entry_from 链与轨道一致。
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'

BOX = 'media/box.png'

EMPTY_ZONES = {
    "deck_level_1": {"count": 0}, "deck_level_2": {"count": 0}, "deck_level_3": {"count": 0},
    "card_market": {"count": 0}, "noble_market": {"count": 0},
    "gem_supply_diamond": {"count": 0}, "gem_supply_sapphire": {"count": 0},
    "gem_supply_ruby": {"count": 0}, "gem_supply_emerald": {"count": 0},
    "gem_supply_onyx": {"count": 0}, "gold_supply": {"count": 0},
    "player_holding": {"count": 0},
}
# 设置完成后（本批结束时）桌面各区的账：卡牌区 12、牌堆 36/26/16、宝石各 4、黄金 5、贵族 3
SETUP_DONE = {
    "card_market": {"count": 12, "face_up": 12},
    "deck_level_1": {"count": 36}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
    "noble_market": {"count": 3},
    "gem_supply_diamond": {"count": 4, "kinds": {"宝石白": 4}},
    "gem_supply_sapphire": {"count": 4, "kinds": {"宝石蓝": 4}},
    "gem_supply_ruby": {"count": 4, "kinds": {"宝石红": 4}},
    "gem_supply_emerald": {"count": 4, "kinds": {"宝石绿": 4}},
    "gem_supply_onyx": {"count": 4, "kinds": {"宝石黑": 4}},
    "gold_supply": {"count": 5},
    "player_holding": {"count": 0},
}


def part(picture, zones):
    return {"picture": picture, "zones": zones}


def ev(at, action, dur=0.0, **kw):
    e = {"at": at, "dur": dur, "action": action}
    e.update(kw)
    return e


def hl(at, zone=None, order=None, target=None, peak=0.65, dur=0.5):
    e = ev(at, "highlight", dur, easing="easeInOutCubic", peak_alpha=peak)
    if zone:
        e["zone"] = zone
    if target:
        e["target"] = target
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
    return {
        "cue": cid, "entry_from": parent, "story": story, "note": note, "timing": timing,
        "enter": enter, "exit": exit_, "start": {"set": []}, "events": events,
    }


CUES = []
A = CUES.append

# ── 1 背景介绍（7 条）：桌上还是空的，画面就是 2024 新版的盒面 ──────────────────
# 这一段的共同点：**没有任何件可以动**（设置还没开始），口播讲的是版本与主题。
# 所以动画就是「把盒面显示出来、停住」——不编造老版画面（用户铁律：该有的有，不该有的就没有）。
INTRO_TIMING = {"盒面": "0.00（本条画面 = 2024 新版盒面）"}
intro_note = ("【版本介绍】口播在交代版本/主题。**这一 cue 不搬运任何件**：设置还没开始，桌上本来就该是空的"
              "（用户铁律：该有的有、不该有的就没有）。画面保持 2024 新版的盒面，不编造没有素材的老版画面。\n"
              "  为什么再写一次 showbox：让「这一条的画面是盒面」留在数据里自洽（契约的 picture 也这么写），"
              "而不是靠上一条没关掉。\n"
              "  为什么不写 camera：盒面是**显示时**贴在相机前面的，此刻移动相机会把盒面留在原处 → 画面变空。")
A(cue("bg.intro.001.2", "bg.intro.001.1",
      "【2015 年出版，2024 年再版；我手里的这一版是 2024 年的新版】",
      intro_note, INTRO_TIMING,
      part(BOX, EMPTY_ZONES), part(BOX, EMPTY_ZONES),
      [ev(0.0, "showbox", picture=BOX, on=1), wait(0.4)]))

for cid, story in [
    ("bg.intro.002.1", "【老版主题：玩家扮演文艺复兴时期的富商，收购宝石矿】"),
    ("bg.intro.002.2", "【老版主题：开采宝石、雇佣工匠加工成饰品，最后在城市中售卖】"),
    ("bg.intro.002.3", "【老版主题：获得声望的同时收获贵族的青睐】"),
]:
    A(cue(cid, "bg.intro.001.2" if cid == "bg.intro.002.1" else "bg.intro.002." + str(int(cid[-1]) - 1),
          story,
          intro_note + "\n  这一段讲的是**老版**（文艺复兴）的主题。我们手上只有 2024 新版素材 → 画面不换、不编造。",
          INTRO_TIMING, part(BOX, EMPTY_ZONES), part(BOX, EMPTY_ZONES),
          [ev(0.0, "showbox", picture=BOX, on=1), wait(0.4)]))

for cid, parent, story in [
    ("bg.intro.003.1", "bg.intro.002.3", "【新版主题：由文艺复兴改为丝绸之路】"),
    ("bg.intro.003.2", "bg.intro.003.1", "【新版主题：扮演丝路商人，买卖宝石】"),
    ("bg.intro.003.3", "bg.intro.003.2", "【新版主题：结识各地贵族，建立自己的贸易帝国】"),
]:
    A(cue(cid, parent, story,
          intro_note + "\n  这一段讲的是 **2024 新版**（丝绸之路）的主题 —— 画面上的盒面正是这一版。",
          INTRO_TIMING, part(BOX, EMPTY_ZONES), part(BOX, EMPTY_ZONES),
          [ev(0.0, "showbox", picture=BOX, on=1), wait(0.4)]))

# ── 2.1 收尾：3 乘 4 的发展卡牌供应堆 ────────────────────────────────────────
A(cue("setup.cards.002.2", "setup.cards.002.1",
      "【最终组成一个 3 乘 4 的发展卡牌供应堆】",
      "【3 乘 4 的发展卡牌供应堆】\n"
      "  这一句**不做搬运**：12 张牌在上一条（002.1）已经发完并翻正面朝上。\n"
      "  它要做的是把「3 行 × 4 列」这个结构点出来 —— 所以按 **order 0→11 扫一遍**：\n"
      "  0-3 = 一级那一行、4-7 = 二级、8-11 = 三级（行的划分由 stage 的 `card_market` 布局决定，"
      "脚本只说「第几格」，不说坐标）。\n"
      "  为什么不是「高亮整个市场」一下：那样观众看不出这是 12 个**格位**、每行 4 个。",
      {"不动作": "整条只把镜头放在整桌，让 12 张牌安静待着"},
      part(None, {"card_market": {"count": 12, "face_up": 12},
                  "deck_level_1": {"count": 36}, "deck_level_2": {"count": 26},
                  "deck_level_3": {"count": 16}}),
      part(None, {"card_market": {"count": 12, "face_up": 12}}),
      [wait(0.0, camera="board", dur=3.4)]))

# ── 2.3 收尾：下一局要重新混洗并抽取贵族板块 ──────────────────────────────────
A(cue("setup.nobles.002", "setup.nobles.001.2",
      "【这一局结束之后，下一局设置时还需要重新混洗并抽取贵族板块】",
      "【贵族板块：下一局要重新混洗、重新抽】\n"
      "  口播说的是**未来那一局**的设置，不是本局牌桌的状态 —— 所以这里**没有状态变化**，\n"
      "  只把镜头推近贵族、把三块依次点一下（观众要把这句话和桌上的东西对上）。\n"
      "  为什么**不**在这里 shuffle：现在洗会让人以为本局要重抽贵族（本局这 3 块已经定了）。",
      {"镜头推近贵族": "0.00", "依次点三块": "0.60 / 1.60 / 2.60"},
      part(None, {"noble_market": {"count": 3}, "card_market": {"count": 12, "face_up": 12},
                  "deck_level_3": {"count": 16}}),
      part(None, {"noble_market": {"count": 3}, "card_market": {"count": 12, "face_up": 12},
                  "deck_level_3": {"count": 16}}),
      [wait(0.0, camera="noble_market", padding=1.25),
       hl(0.6, zone="<noble_market>"),
       hl(1.6, zone="<noble_market>", order=1),
       hl(2.6, zone="<noble_market>", order=2)]))

# ── 2.4 起始玩家（3 条）─────────────────────────────────────────────────────
A(cue("setup.starting_player.001.1", "setup.nobles.002",
      "【现在需要决定起始玩家。规则书中规定最年轻的玩家作为起始玩家】",
      "【决定起始玩家】\n"
      "  谁最年轻是**桌外的事**，桌上没有对应的件可以动 → 镜头回到整桌，把「玩家这一侧」点一下，"
      "  表示这句话说的是玩家而不是牌桌。\n"
      "  标记要等下一句才出现（001.3 才说「这是起始玩家标记」）—— 这里先建出来会抢台词。",
      {"镜头回整桌": "0.00", "点玩家区": "1.00"},
      part(None, {"player_holding": {"count": 0}, "player_marker": {"count": 0}}),
      part(None, {"player_holding": {"count": 0}, "player_marker": {"count": 0}}),
      [wait(0.0, camera="board"), hl(1.0, zone="player_holding")]))

A(cue("setup.starting_player.001.2", "setup.starting_player.001.1",
      "【也可以使用所有人都认可的方式决定谁是起始玩家】",
      "【也可以用别的方式决定起始玩家】\n"
      "  同样是桌外的约定 → 没有状态变化，保持整桌取景，把玩家区再点一下。",
      {"镜头整桌": "0.00（承接上一条）", "点玩家区": "0.80"},
      part(None, {"player_holding": {"count": 0}, "player_marker": {"count": 0}}),
      part(None, {"player_holding": {"count": 0}, "player_marker": {"count": 0}}),
      [wait(0.0, camera="board"), hl(0.8, zone="player_holding")]))

A(cue("setup.starting_player.001.3", "setup.starting_player.001.2",
      "【这是起始玩家标记，起始玩家获得这枚标记】",
      "【起始玩家标记出场】\n"
      "  这是本作**第一件不是宝石/卡牌的实物**：一张菱形纸板标记。\n"
      "  · 从盒里拿出来 = `create`（**游戏盒是抽象概念**，没有 box_* zone，也不写 source）；\n"
      "  · 放到玩家面前 = 新 zone `player_marker`（位置/大小在 stage 里，脚本不写坐标）；\n"
      "  · 单面件（没有背图）→ 不写 `to`/朝向断言（朝向这一维对它不存在）。\n"
      "  ⚠ 实物尺寸**还没实测**：`components.json` 里记的是 50×62mm 估值（`confirmed_by: estimated`），"
      "  拿到用户实测值以后改那一个数即可（stage 的 width/height 也跟着换算）。",
      {"镜头推近玩家区": "0.00", "标记出现": "1.00（『这是起始玩家标记』）", "点亮标记": "2.20"},
      part(None, {"player_holding": {"count": 0}, "player_marker": {"count": 0},
                  "gold_supply": {"count": 5}}),
      part(None, {"player_marker": {"count": 1}, "gold_supply": {"count": 5},
                  "player_holding": {"count": 0}}),
      [wait(0.0, camera="player_marker", padding=1.6),
       ev(1.0, "create", dur=0.0, destination="player_marker",
          template="starting_marker", what={"concept": "starting_player_marker"}),
       hl(2.2, zone="player_marker")]))

# ── 2.5 设置完成（5 条）────────────────────────────────────────────────────
A(cue("setup.end.001.1", "setup.starting_player.001.3",
      "【至此，整个设置环节就大功告成了】",
      "【设置完成】\n"
      "  收尾的一句 → 镜头拉回整桌，然后把设置出来的**四块东西**依次点一遍：发展卡牌区、贵族、"
      "  五堆宝石供应、玩家区。这就是「设置完成」的画面定义。\n"
      "  没有状态变化：设置到上一条（起始玩家标记）就全做完了。",
      {"镜头回整桌": "0.00", "点四块": "0.40 / 0.80 / 1.20-1.80 / 2.20"},
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      [wait(0.0, camera="board"),
       hl(0.4, zone="<card_market>"), hl(0.8, zone="<noble_market>")]
      + [hl(1.2 + i * 0.15, zone=z) for i, z in enumerate(
          ["<gem_supply|color=<diamond>>", "<gem_supply|color=<sapphire>>",
           "<gem_supply|color=<ruby>>", "<gem_supply|color=<emerald>>",
           "<gem_supply|color=<onyx>>"])]
      + [hl(2.2, zone="player_holding")]))

_seat_note = ("【座位建议】口播在说「怎么坐」——这是**桌外的事**（谁坐哪一侧），不是牌桌状态。\n"
              "  所以这一 cue 没有搬运，只是把镜头留在整桌、把被提到的区域点一下，帮观众对上位置。\n"
              "  为什么不摆出一个「3-4 人版桌面」：那需要另一套台面图（我们没有），"
              "  编一张假的台面比不动更容易教错。")
A(cue("setup.end.001.2", "setup.end.001.1",
      "【这个设置方式适合两名玩家并排坐在桌子同一侧进行游戏时使用】",
      _seat_note + "\n  这一句讲两人并排 → 点公共区（市场）。",
      {"镜头整桌": "0.00", "点市场": "1.00", "点玩家区": "2.50"},
      part(None, SETUP_DONE), part(None, SETUP_DONE),
      [wait(0.0, camera="board"), hl(1.0, zone="<card_market>"), hl(2.5, zone="player_holding")]))

A(cue("setup.end.001.3", "setup.end.001.2",
      "【如果三到四位玩家同时进行游戏，更推荐使用这种设置方式】",
      _seat_note + "\n  这一句讲三四人围坐 → 点市场（放中间）与贵族。",
      {"镜头整桌": "0.00", "点市场": "0.80", "点贵族": "2.00"},
      part(None, SETUP_DONE), part(None, SETUP_DONE),
      [wait(0.0, camera="board"), hl(0.8, zone="<card_market>"), hl(2.0, zone="<noble_market>")]))

A(cue("setup.end.001.4", "setup.end.001.3",
      "【这样可以避免对侧玩家阅读卡牌吃力】",
      _seat_note + "\n  这一句讲「卡牌要人人看得清」→ 镜头推近市场，让观众看清卡面。",
      {"镜头推近市场": "0.00", "点市场": "0.60"},
      part(None, SETUP_DONE), part(None, SETUP_DONE),
      [wait(0.0, camera="card_market", padding=1.6), hl(0.6, zone="<card_market>")]))

A(cue("setup.end.002", "setup.end.001.4",
      "【接下来可以正式开始游戏了】",
      "【设置结束，准备开打】\n"
      "  镜头拉回整桌（这是「一切就绪」的画面），点一下市场 —— 下一段讲的就是行动。",
      {"镜头回整桌": "0.00", "点市场": "0.60"},
      part(None, SETUP_DONE), part(None, SETUP_DONE),
      [wait(0.0, camera="board"), hl(0.6, zone="<card_market>")]))

# ── 3 行动总述（2 条）──────────────────────────────────────────────────────
A(cue("action.turn.001", "setup.end.002",
      "【整场游戏将持续若干个回合，从起始玩家开始，按顺时针顺序所有玩家依次进行行动】",
      "【回合与顺序】\n"
      "  「从起始玩家开始」在桌上**有一件东西能指**：起始玩家标记 → 点它。\n"
      "  「按顺时针依次」是桌外的事 → 再点一下玩家区表示轮到玩家。\n"
      "  没有状态变化（规则说明，不是某一步操作）。",
      {"镜头整桌": "0.00", "点起始玩家标记": "1.00", "点玩家区": "3.00"},
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      [wait(0.0, camera="board"), hl(1.0, zone="player_marker"), hl(3.0, zone="player_holding")]))

A(cue("action.turn.002", "action.turn.001",
      "【玩家在自己回合内可以从如下几种行动中选择其中一种进行】",
      "【一个回合做一件事：三种行动】\n"
      "  三种行动在桌面对应**三个地方**，依次点出来（这是本节后面要展开的目录）：\n"
      "    ① 拿宝石 → 宝石供应堆；② 买发展卡 → 市场（正面朝上的 12 张）；③ 保留发展卡 → 牌堆。\n"
      "  没有状态变化 —— 只是把目录指给人看。",
      {"镜头整桌": "0.00", "点供应堆": "0.80", "点市场": "1.80", "点牌堆": "2.80"},
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      part(None, SETUP_DONE | {"player_marker": {"count": 1}}),
      [wait(0.0, camera="board"),
       hl(0.8, zone="<gem_supply|color=<diamond>>"),
       hl(1.8, zone="<card_market>"),
       hl(2.8, zone="<development_deck_level_1>")]))


def main():
    doc = json.loads(ANIM.read_text(encoding='utf-8'))
    rt = json.loads(RT.read_text(encoding='utf-8'))
    order = [c['id'] for c in rt['cues']]
    by_id = {c['cue']: c for c in doc['cues']}
    added = 0
    for c in CUES:
        if c['cue'] in by_id:
            print('  覆盖已有', c['cue'])
        else:
            added += 1
        by_id[c['cue']] = c
    missing = [cid for cid in order if cid not in by_id]
    doc['cues'] = [by_id[cid] for cid in order if cid in by_id]
    ANIM.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'新增 {added} 条；文件现有 {len(doc["cues"])} 条；仍未写 {len(missing)} 条')


if __name__ == '__main__':
    main()
