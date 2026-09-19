#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""第二批：3.1 从宝石供应堆拿取宝石（8 条，接在 action.take.different.001 之后）。

起始状态（action.take.different.001 的终态）：
  宝石供应 白3 蓝3 红3 绿4 黑4、黄金 5、玩家持有 3（白1蓝1红1）
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
ANIM = ROOT / 'games/splendor/tutorial/anim/full.json'
RT = ROOT / 'games/splendor/tutorial/full.runtime.json'

SUP = {  # 供应堆的账（便于逐条微调）
    "d": 3, "s": 3, "r": 3, "e": 4, "o": 4,
}
COLOR_CN = {"d": "白", "s": "蓝", "r": "红", "e": "绿", "o": "黑"}
ZONE = {"d": "gem_supply_diamond", "s": "gem_supply_sapphire", "r": "gem_supply_ruby",
        "e": "gem_supply_emerald", "o": "gem_supply_onyx"}
REF = {k: f"<gem_supply|color=<{ {'d':'diamond','s':'sapphire','r':'ruby','e':'emerald','o':'onyx'}[k] }>>"
       for k in ZONE}


def supply_zones(**over):
    z = {ZONE[k]: {"count": over.get(k, v), "kinds": {f"宝石{COLOR_CN[k]}": over.get(k, v)}}
         for k, v in SUP.items()}
    return z


def holding(n_kinds):
    """玩家持有的账：n_kinds = {"d":1,"s":1,"r":1,"e":2} 这种。"""
    z = {"count": sum(n_kinds.values())}
    if n_kinds:
        z["kinds"] = {f"宝石{COLOR_CN[k]}": n for k, n in n_kinds.items()}
    return z


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


def gem_what(k):
    return {"concept": "gem",
            "parts": [{"key": "color",
                       "value": "<%s>" % {'d': 'diamond', 's': 'sapphire', 'r': 'ruby',
                                          'e': 'emerald', 'o': 'onyx'}[k]}]}


def T(e=4, hold=None, **over):
    """整桌账（本批的常量部分）：为何写整桌 —— 特写镜头里会入镜邻区，
    取景检查要求「画面里有的都得在契约里声明」，声明越全断言越强。"""
    h = hold if hold is not None else {"d": 1, "s": 1, "r": 1, "e": 2}
    z = supply_zones(e=e)
    z.update({
        "gold_supply": {"count": 5},
        "player_holding": holding(h),
        "card_market": {"count": 12, "face_up": 12},
        "deck_level_1": {"count": 36}, "deck_level_2": {"count": 26}, "deck_level_3": {"count": 16},
        "noble_market": {"count": 3},
        "player_marker": {"count": 1},
    })
    z.update(over)
    return z


HOLD0 = holding({"d": 1, "s": 1, "r": 1})
BASE_Z = dict(supply_zones(), gold_supply={"count": 5}, player_holding=HOLD0)

CUES = []
A = CUES.append

# ── 拿两枚相同的宝石 ────────────────────────────────────────────────────────
_same_story = "【当某种宝石在供应堆中的数量大于等于 4 时，也可以改为拿取这种宝石两枚】"
A(cue("action.take.same.001", "action.take.different.001", _same_story,
      "【拿两枚相同：门槛 >= 4】\n"
      "  演示拿**两枚绿宝石**：供应堆里现在正好绿 4 枚（>=4 够门槛），拿走后剩 2 —— 这 2 枚正好是\n"
      "  下一条（same.002）要讲的「至少剩 2」的下限，两条口播在同一堆上连着讲完。\n"
      "  `source` 写的是 zone（哪一堆），`quantity: 2` 说几枚，`what` 说**搬的是哪一类**（本体语言：\n"
      "  gem + color=emerald）—— 三个字段各说一件事，不看素材名。\n"
      "  没有写 `to`：宝石是单面件，朝向这一维对它不存在。",
      {"点绿宝石堆": "0.60", "拿两枚": "3.00", "点玩家持有": "4.20"},
      part(None, T(e=4, hold={"d": 1, "s": 1, "r": 1})),
      part(None, T(e=2)),
      [wait(0.0, camera="board"),
       hl(0.6, zone=REF["e"]), hl(1.8, zone=REF["e"]),
       ev(3.0, "transfer", dur=0.6, easing="easeInOutCubic", realizes="<ontology::transfer>",
          source=[REF["e"]], quantity=2, destination="<player_holding>",
          stagger=0.25, what=gem_what("e")),
       hl(4.2, zone="<player_holding>")]))

A(cue("action.take.same.002", "action.take.same.001",
      "【拿取之后必须保证这种宝石在供应堆中至少剩下 2 枚，否则就不能这样拿取】",
      "【拿两枚的第二个条件：至少剩 2】\n"
      "  这一条**没有状态变化**（是规则说明）：绿宝石堆刚被拿到只剩 2 枚，正好是「刚好合法」的样子。\n"
      "  然后把另外三堆只有 3 枚的点出来 —— 它们够不上「>=4」，也就不能一次拿两枚。\n"
      "  ⚠ 我们**还没有表示「不行/禁止」的表现原语**（没有摇头、没有打叉），所以这里只能靠「依次点出那三堆」\n"
      "  加上口播来表达。要是用户觉得不够清楚，该加的是原语（例如 `forbid` 高亮闪红），不是在这里凑动画。",
      {"点绿宝石堆（剩 2）": "0.60", "点三堆只有 3 枚的": "1.80 / 2.40 / 3.00"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="board"),
       hl(0.6, zone=REF["e"]),
       hl(1.8, zone=REF["d"]), hl(2.4, zone=REF["s"]), hl(3.0, zone=REF["r"])]))

# ── 不能故意少拿 ────────────────────────────────────────────────────────────
A(cue("action.take.draw_rule.001.1", "action.take.same.002",
      "【除非供应堆中已经没有三个种类的宝石，否则玩家不能故意少拿】",
      "【不能故意少拿】\n"
      "  规则说明，**没有状态变化**：镜头推到整排供应区，把五堆依次点一遍 ——\n"
      "  让观众看到「现在这里还有五个种类」，所以必须拿三种不同的。\n"
      "  （演示「只剩两种时才可以拿两种」会把供应堆掏空、破坏后面的账，所以只在口播里讲。）",
      {"镜头推近供应区": "0.00", "依次点五堆": "0.60 / 0.90 / 1.20 / 1.50 / 1.80"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="supply")] +
      [hl(0.6 + i * 0.3, zone=REF[k]) for i, k in enumerate(["d", "s", "r", "e", "o"])]))

A(cue("action.take.draw_rule.001.2", "action.take.draw_rule.001.1",
      "【当供应堆宝石不足时，尽可能拿取宝石】",
      "【不足时：尽可能拿】\n"
      "  同一句话的后半段 —— 承接上一条的供应区特写，把**只剩 3 枚的三堆**点出来，\n"
      "  表示「这些是拿不到两枚的」。没有状态变化。",
      {"保持供应区特写": "0.00", "点三堆各 3 枚": "0.50 / 1.00 / 1.50"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="supply"),
       hl(0.5, zone=REF["d"]), hl(1.0, zone=REF["s"]), hl(1.5, zone=REF["r"])]))

# ── 持有上限 10 ─────────────────────────────────────────────────────────────
_limit_note = ("【持有上限 10】\n"
               "  规则说明，**没有状态变化**。玩家现在手里是 %d 枚 —— 讲上限本来更该演示「超过 10 再还回去」，\n"
               "  但那需要**凭空多出 6 枚宝石**（口播并没有让人拿这些），而本项目的铁律是\n"
               "  「脚本里没写有的那就是没有」+ 每色实物总数 7 枚的账要平 → 所以这里不凑那个演示，\n"
               "  只把镜头推到玩家持有区、把「手上的宝石」点出来。\n"
               "  （前作宝石的出现都有出处：`create` = 从盒里拿，`transfer` = 从供应堆拿。）")
A(cue("action.take.limit.001.1", "action.take.draw_rule.001.2",
      "【玩家持有的宝石和黄金总数上限为 10 个。任何时候当持有数量超过上限时】",
      _limit_note % 5,
      {"镜头推近玩家持有区": "0.00", "点手上的宝石": "1.20"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="player_holding", padding=1.6), hl(1.2, zone="<player_holding>")]))

A(cue("action.take.limit.001.2", "action.take.limit.001.1",
      "【必须挑选一定数量的宝石或黄金回到供应堆，直到恰好剩下 10 个为止】",
      _limit_note % 5 + "\n  这一句讲「还回供应堆」→ 把手上点一下、再把供应堆点一下（一来一回的方向感）。",
      {"点手上": "0.50", "点供应堆": "1.60", "再点手上": "3.00"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="player_holding", padding=1.6),
       hl(0.5, zone="<player_holding>"), hl(1.6, zone=REF["d"]), hl(3.0, zone="<player_holding>")]))

# ── 宝石是公开信息 ──────────────────────────────────────────────────────────
A(cue("action.take.public.001.1", "action.take.limit.001.2",
      "【宝石属于公开信息，每位玩家都需要将自己持有的宝石放在自己面前】",
      "【宝石是公开信息】\n"
      "  规则说明，**没有状态变化**：镜头停在玩家持有区（上一条已经推近），把「放在自己面前」这件事点出来。",
      {"保持玩家区特写": "0.00", "点持有区": "0.80 / 2.40"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [wait(0.0, camera="player_holding", padding=1.6),
       hl(0.8, zone="<player_holding>"), hl(2.4, zone="<player_holding>")]))

A(cue("action.take.public.001.2", "action.take.public.001.1",
      "【所有人都能清楚看到的地方】",
      "【公开 = 大家都看得见】\n"
      "  「所有人都能清楚看到」用**镜头语言**表达：从持有区特写拉回整桌 —— 手上有多少、别人一眼就看到。\n"
      "  没有状态变化。",
      {"点持有区": "0.40", "拉回整桌": "1.60"},
      part(None, T(e=2)),
      part(None, T(e=2)),
      [hl(0.4, zone="<player_holding>"), wait(1.6, camera="board")]))


def main():
    doc = json.loads(ANIM.read_text(encoding='utf-8'))
    rt = json.loads(RT.read_text(encoding='utf-8'))
    order = [c['id'] for c in rt['cues']]
    by_id = {c['cue']: c for c in doc['cues']}
    added = 0
    for c in CUES:
        if c['cue'] not in by_id:
            added += 1
        by_id[c['cue']] = c
    missing = [cid for cid in order if cid not in by_id]
    doc['cues'] = [by_id[cid] for cid in order if cid in by_id]
    ANIM.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'新增 {added} 条；文件现有 {len(doc["cues"])} 条；仍未写 {len(missing)} 条')


if __name__ == '__main__':
    main()
