#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Splendor 规则合法性账本：拿动画事件流把状态机重放一遍，逐步判断每一步是否合法。

本文件是游戏专用数据/规则，放在 `content/games/splendor/tutorial/checks/`；
通用 v2 适配入口仍是 `animation/validate_anim_rules_v2.py`。

本作演示的是 **2 人局**（由 setup 的"把多余的宝石放回盒子"定下来）：
  每色宝石在场 4 枚（实物 7，另 3 枚在盒里且**永不回场**）、黄金 5、贵族 3、
  玩家手上限 10、保留上限 3、保留一张牌必须拿一枚黄金（除非黄金堆空了）。

做法：从空桌开始，按轨道顺序重放每条 cue 的事件，维护"每个 zone 里有什么身份、场上每色几枚"。
买牌那一步额外对账：**价格 − 折扣 == 实际付出去的宝石**（价格来自 content/games/splendor/card_facts.json，
没有价格的牌会报 warning 提醒补数据）。

`validate_anim_rules_v2.py` 负责把 v2 事件适配成账本输入，并提供命令行入口。
"""
from __future__ import annotations

from collections import Counter, defaultdict


def resolve_zone_ref(stage, ref):
    """v2 events already carry concrete zone ids; resolve them as-is."""
    return ref

CN = {"diamond": "白", "sapphire": "蓝", "emerald": "绿", "ruby": "红", "onyx": "黑"}
GEMS_PER_COLOR = 4
GEM_TOTAL = 7
GOLD_TOTAL = 5
HAND_LIMIT = 10
RESERVE_LIMIT = 3
LEVEL_CAP = {"1": 40, "2": 30, "3": 20}
FROZEN_FROM = "setup.gems.003.1"


class Report:
    def __init__(self):
        self.errors, self.warnings = [], []

    def error(self, where, msg):
        self.errors.append((where, msg))

    def warn(self, where, msg):
        self.warnings.append((where, msg))


def color_of_event(ev, stage):
    """事件里要搬/建的宝石颜色（本体 what 的 color，或色板名）。"""
    for p in ((ev.get("what") or {}).get("parts") or []):
        if p.get("key") == "color":
            return str(p.get("value", "")).strip("<>")
    pal = ev.get("palette") or ""
    return pal[4:] if pal.startswith("gem_") and pal != "gem_gold" else None


def is_gold_event(ev):
    return (ev.get("what") or {}).get("concept") == "gold" or (ev.get("palette") or "") == "gem_gold"


def card_bonus(stage, tid):
    for t in stage.get("templates") or []:
        if t.get("id") == tid:
            for p in t.get("parts") or []:
                if p.get("key") == "bonus":
                    return str(p.get("value", "")).strip("<>")
    return None


def template_shape(stage, tid):
    for t in stage.get("templates") or []:
        if t.get("id") == tid:
            return t.get("shape")
    return None


def card_level(stage, tid):
    for t in stage.get("templates") or []:
        if t.get("id") == tid:
            return str(t.get("concept") or "")[-1:]
    return ""


def card_cost(facts, tid):
    return dict(((facts or {}).get("cost_by_template") or {}).get(tid) or {}) or None


class State:
    def __init__(self):
        self.zones = defaultdict(Counter)

    def clone(self):
        out = State()
        for zid, items in self.zones.items():
            out.zones[zid] = Counter(items)
        return out

    def add(self, zid, ident, n=1):
        if n:
            self.zones[zid][ident] += n

    def remove(self, zid, ident, n=1):
        have = self.zones[zid].get(ident, 0)
        take = min(have, n)
        if take:
            self.zones[zid][ident] -= take
            if self.zones[zid][ident] <= 0:
                self.zones[zid].pop(ident, None)
        return take

    def count(self, zid):
        return sum(self.zones[zid].values())

    def gems(self):
        c = Counter()
        for items in self.zones.values():
            for ident, n in items.items():
                if ident.startswith("gem:"):
                    c[ident[4:].split("@")[0]] += n
        return c

    def gold(self):
        return sum(n for items in self.zones.values() for ident, n in items.items()
                   if ident.startswith("gold"))

    def hand(self, zid):
        return sum(n for ident, n in self.zones[zid].items()
                   if ident.startswith("gem:") or ident.startswith("gold"))


class _CueContext:
    """Mutable per-cue state shared by the Ledger event handlers."""

    __slots__ = (
        "cid", "tree", "world", "stage", "zones", "dev_zones", "st", "frozen",
        "is_demo", "over_limit_demo", "paid", "bought", "reserved",
        "gold_taken", "where", "ev",
    )

    def __init__(self, **kw):
        for key in self.__slots__:
            setattr(self, key, kw.get(key))


class Ledger:
    """Replay a v2 event stream through the Splendor rule ledger.

    ``run`` owns only orchestration; each event action is a small handler and
    the end-of-cue checks are split into maintenance (`_finish_cue`) and
    purchase settlement (`_check_purchases`).  Assertions stay in this
    game-specific module, separate from the compiler.
    """

    def __init__(self, anim, default_stage, stages, facts, rep: Report = None,
                 on_event=None, on_cue_end=None, cue_start_states=None):
        self.anim = anim
        self.default_stage = default_stage
        self.stages = stages
        self.facts = facts
        self.rep = rep or Report()
        self.on_event = on_event
        self.on_cue_end = on_cue_end
        self.cue_start_states = cue_start_states
        self.frozen_by_tree = {}

    def run(self):
        """按轨道顺序重放；**跨 tree 不重置状态**，跨 world 状态天然 cut。

        `stages` 是 tree id → StageDoc；没写 tree 的 cue（以及没登记的 tree）走 default_stage。
        """
        self.frozen_by_tree = {}
        tree_worlds = {}
        for t in self.anim.get("trees") or []:
            tid = t.get("id") or "main"
            tree_worlds[tid] = t.get("world") or tid
        tree_states = {}

        for cue in self.anim.get("cues") or []:
            cid = cue.get("cue")
            tree = cue.get("tree") or "main"
            world = tree_worlds.get(tree, tree)
            stage = self.stages.get(tree, self.default_stage)
            zones = {z["id"]: z for z in (stage.get("zones") or [])}
            dev_zones = [z for z in zones if "development" in z]
            if self.cue_start_states is not None and cid in self.cue_start_states:
                st = self.cue_start_states[cid].clone()
                tree_states[world] = st
            else:
                st = tree_states.setdefault(world, State())

            ctx = _CueContext(
                cid=cid, tree=tree, world=world, stage=stage, zones=zones,
                dev_zones=dev_zones, st=st,
                frozen=self.frozen_by_tree.get(world),
                is_demo=bool(cue.get("demo")), over_limit_demo=False,
                paid=Counter(), bought=[], reserved=[], gold_taken=0,
                where="", ev=None,
            )
            handlers = {
                "stack": self._on_stack,
                "create": self._on_create,
                "destroy": self._on_destroy,
                "transfer": self._on_transfer,
            }
            for i, ev in enumerate(cue.get("events") or []):
                ctx.ev = ev
                ctx.where = f"{cid} events[{i}]"
                if ev.get("over_limit_demo"):
                    ctx.over_limit_demo = True
                if self.on_event is not None:
                    # 回调拿到的是**这一步之前**的状态（未包括这一动）—— 生成问题正好要这个
                    self.on_event(cid, ctx.where, ev, ctx.st, {
                        "paid": ctx.paid, "bought": ctx.bought,
                        "reserved": ctx.reserved, "gold_taken": ctx.gold_taken,
                    })
                handler = handlers.get(ev.get("action"))
                if handler is not None:
                    handler(ctx)
            self._finish_cue(ctx)
        return self.rep

    # ── event handlers ──────────────────────────────────────────────────

    def _on_stack(self, ctx):
        rep, stage, st, ev, where = self.rep, ctx.stage, ctx.st, ctx.ev, ctx.where
        zid = resolve_zone_ref(stage, ev.get("destination"))
        cap = int(ev.get("capacity") or 0)
        real = [s for s in (ev.get("real_templates") or "").split(",") if s]
        for tid in real:
            if st.zones[zid].get(f"card:{tid}", 0) == 0:
                st.add(zid, f"card:{tid}")
        cur = st.count(zid)
        if ev.get("pad_template") and cur < cap:
            st.add(zid, f"card:{ev['pad_template']}", cap - cur)
        lv = card_level(stage, real[0]) if real else ""
        if lv in LEVEL_CAP and st.count(zid) > LEVEL_CAP[lv]:
            rep.error(where, f"牌堆 {zid} 共 {st.count(zid)} 张 > 该级实物 {LEVEL_CAP[lv]} 张")

    def _on_create(self, ctx):
        rep, stage, st, ev, where = self.rep, ctx.stage, ctx.st, ctx.ev, ctx.where
        zid = resolve_zone_ref(stage, ev.get("destination"))
        tid = ev.get("template")
        n = int(ev.get("count") or 1)
        gold = is_gold_event(ev)
        color = color_of_event(ev, stage)
        if gold or color:
            key = f"{tid}@{ev.get('palette') or ''}"
            ident = "gold@" + key if gold else f"gem:{color}@{key}"
            have = st.zones[zid].get(ident, 0)
            st.add(zid, ident, max(0, n - have))       # create = 补齐到 N（幂等）
            if color:
                tot = st.gems().get(color, 0)
                if tot > GEM_TOTAL:
                    rep.error(where, f"create {CN[color]}宝石 → 场上共 {tot} 枚 > 实物 {GEM_TOTAL} 枚")
                if not ctx.is_demo and ctx.frozen is not None and tot > ctx.frozen.get(color, GEMS_PER_COLOR):
                    rep.error(where, f"create {CN[color]}宝石 → 场上 {tot} 枚 > 本局在场 "
                                     f"{ctx.frozen.get(color, GEMS_PER_COLOR)} 枚（**盒子不该再打开**）")
        else:
            ident = f"card:{tid}"
            have = st.zones[zid].get(ident, 0)
            st.add(zid, ident, max(0, n - have))
            # create 进发展区 = 脚本给的前提（"如果我已经买了…"）→ **不要求付款**，
            # 但真卡总数不能超过这一级的实物张数（下面统一查）

    def _on_destroy(self, ctx):
        stage, st, ev, where = ctx.stage, ctx.st, ctx.ev, ctx.where
        zid = resolve_zone_ref(stage, ev.get("zone"))
        tid = ev.get("template")
        explicit = ev.get("count") or ev.get("quantity")
        color = color_of_event(ev, stage)
        if tid and template_shape(stage, tid) == "gem":
            match = (lambda k: k.startswith(f"gem:{color}@")) if color else \
                    (lambda k: k.startswith("gem:") or k.startswith("gold@"))
        elif tid:
            match = (lambda k, _t=f"card:{tid}": k == _t)
        else:
            match = (lambda k: k.startswith(f"gem:{color}@")) if color else (lambda k: True)
        # 不带 count 的 destroy = 把该区里匹配的件**全清掉**（引擎语义：展示位那 5 枚样本
        # 就是一条 destroy 清空的）；带 count 就按**件数**删（不是按身份种类数）
        remain = int(explicit) if explicit else None
        for k in list(st.zones[zid]):
            if not match(k):
                continue
            take = st.zones[zid][k] if remain is None else min(remain, st.zones[zid][k])
            st.remove(zid, k, take)
            if remain is not None:
                remain -= take
                if remain <= 0:
                    break

    def _on_transfer(self, ctx):
        self._check_take_shape(ctx)
        self._move_transfer_items(ctx)

    def _check_take_shape(self, ctx):
        rep, stage, st, ev, where = self.rep, ctx.stage, ctx.st, ctx.ev, ctx.where
        dest = resolve_zone_ref(stage, ev.get("destination"))
        # —— 取宝石这个**动作**的构成（引擎教的：只有"三色各一"或"同色两枚"两种）——
        srcs0 = [resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]
        # setup transfers establish a cue's starting premise; they are not
        # player take actions, so the take-action shape check does not apply.
        takes = [] if ev.get("setup") else [z for z in srcs0 if "supply" in z and "gem_supply" in z]
        if takes and "holding" in dest:
            qty0 = int(ev.get("quantity") or 1)
            colors0 = [(ctx.zones[z].get("parts") or [{}])[0].get("value", "") for z in takes]
            colors0 = [str(c).strip("<>") for c in colors0]
            total = qty0 * len(takes)
            if len(set(colors0)) == len(takes) and qty0 == 1 and 2 <= len(takes) <= 3:
                pass                                   # 三色各一（或两色各一，供应不足时）
            elif len(takes) == 1 and qty0 == 2:
                col = colors0[0]
                pile = sum(n for k, n in st.zones[takes[0]].items()
                           if k.startswith(f"gem:{col}@"))
                if pile < 4:
                    rep.error(where, f"同色拿两枚要求该堆 ≥4，实际 {pile}")
                elif pile - 2 < 2:
                    rep.error(where, f"同色拿两枚后必须剩 ≥2，实际会剩 {pile - 2}")
            else:
                rep.error(where, f"取宝石的动作不合法：从 {len(takes)} 堆共拿 {total} 枚"
                                    f"（合法只有『三色各一』或『同色两枚』）")

    def _move_transfer_items(self, ctx):
        rep, stage, st, ev, where = self.rep, ctx.stage, ctx.st, ctx.ev, ctx.where
        dest = resolve_zone_ref(stage, ev.get("destination"))
        qty = int(ev.get("quantity") or 1)
        gold, color = is_gold_event(ev), color_of_event(ev, stage)
        tid = ev.get("template")
        want_bonus = None
        for p in ((ev.get("what") or {}).get("parts") or []):
            if p.get("key") == "bonus":
                want_bonus = str(p.get("value", "")).strip("<>")
        want_lv = str((ev.get("what") or {}).get("concept") or "")[-1:]
        for sid in [resolve_zone_ref(stage, x) for x in (ev.get("source") or [])]:
            # State is inherited across tree/stage boundaries.  A cue may
            # operate on a logical zone that is not rendered by the current
            # stage (for example moving one inherited market card into the
            # card-intro showcase).  The zone must exist in the inherited
            # state even if it is absent from this stage.
            if sid not in ctx.zones and sid not in getattr(st, "zones", {}):
                rep.error(where, f"source zone {sid!r} 不存在")
                continue
            src_color = color
            if src_color is None and sid in ctx.zones:
                for p2 in (ctx.zones[sid].get("parts") or []):
                    if p2.get("key") == "color":
                        src_color = str(p2.get("value", "")).strip("<>")
                        break
            for _ in range(qty):
                ident = None
                keys = list(st.zones[sid])
                if gold:
                    ident = next((k for k in keys if k.startswith("gold@")), None)
                elif src_color:
                    ident = next((k for k in keys if k.startswith(f"gem:{src_color}@")), None)
                else:
                    for k in keys:
                        if not k.startswith("card:"):
                            continue
                        kt = k[5:]
                        if tid and kt != tid:
                            continue
                        if want_bonus and card_bonus(stage, kt) != want_bonus:
                            continue
                        if not tid and want_lv in ("1", "2", "3") and card_level(stage, kt) != want_lv:
                            continue
                        ident = k
                        break
                if ident is None or st.remove(sid, ident, 1) == 0:
                    rep.error(where, f"要从 {sid} 搬的东西场上没有/不够"
                                     f"（{'黄金' if gold else (CN.get(color, color) + '宝石') if color else '卡牌'}"
                                     f"{'，模板 ' + tid if tid else ''}）")
                    break
                st.add(dest, ident, 1)
                if ident.startswith("gem:"):
                    if "supply" in dest:
                        ctx.paid[ident[4:].split("@")[0]] += 1
                elif ident.startswith("gold@"):
                    if dest == "gold_supply":
                        ctx.paid["gold"] += 1
                    if "holding" in dest:
                        ctx.gold_taken += 1
                elif ident.startswith("card:"):
                    if dest in ctx.dev_zones:
                        ctx.bought.append((ident[5:], where))
                    if "reserved" in dest:
                        ctx.reserved.append((ident[5:], dest))
        if not ev.get("over_limit_demo"):
            for zid in ctx.zones:
                if "holding" in zid and st.hand(zid) > HAND_LIMIT:
                    rep.error(where, f"{zid} 手上 {st.hand(zid)} 枚 > 上限 {HAND_LIMIT}")

    # ── end-of-cue checks ───────────────────────────────────────────────

    def _finish_cue(self, ctx):
        rep, cid, st, is_demo = self.rep, ctx.cid, ctx.st, ctx.is_demo
        if cid == FROZEN_FROM:
            ctx.frozen = dict(st.gems())
            self.frozen_by_tree[ctx.world] = ctx.frozen
        gems = st.gems()
        if ctx.frozen is not None and not is_demo:
            for col, want in ctx.frozen.items():
                n = gems.get(col, 0)
                if n != want:
                    rep.error(cid, f"本 cue 结束时 {CN.get(col, col)}宝石在场 {n} 枚 ≠ 本局在场 {want} 枚"
                                   f"（盒子里的宝石不该回到场上）")
        if st.gold() > GOLD_TOTAL:
            rep.error(cid, f"黄金在场 {st.gold()} 枚 > 实物 {GOLD_TOTAL} 枚")
        # 每级真卡总数 ≤ 实物张数（市场 + 牌堆 + 玩家手里的真卡）
        lvl_total = Counter()
        for zid, items in st.zones.items():
            for ident, n in items.items():
                if ident.startswith("card:") and not ident[5:].startswith("blank_card"):
                    lv = card_level(ctx.stage, ident[5:])
                    if lv in LEVEL_CAP:
                        lvl_total[lv] += n
        for lv, n in lvl_total.items():
            if n > LEVEL_CAP[lv]:
                rep.error(cid, f"{lv} 级发展卡共 {n} 张 > 实物 {LEVEL_CAP[lv]} 张")
        for zid in ctx.zones:
            # demo 分支允许假设性/错误示范状态；保留上限等只在 canonical 分支严格校验。
            if not is_demo and "reserved" in zid and st.count(zid) > RESERVE_LIMIT:
                rep.error(cid, f"{zid} 保留 {st.count(zid)} 张 > 上限 {RESERVE_LIMIT}")
            if not ctx.over_limit_demo and "holding" in zid and st.hand(zid) > HAND_LIMIT:
                rep.error(cid, f"{zid} 手上 {st.hand(zid)} 枚 > 上限 {HAND_LIMIT}")
        if (not is_demo and len(ctx.reserved) > ctx.gold_taken
                and st.count("gold_supply") + ctx.gold_taken >= len(ctx.reserved)):
            rep.error(cid, f"保留了 {len(ctx.reserved)} 张牌却只拿了 {ctx.gold_taken} 枚黄金（黄金堆还有，必须给）")
        if self.on_cue_end is not None:
            self.on_cue_end(cid, st)
        self._check_purchases(ctx)

    def _check_purchases(self, ctx):
        rep, stage, st = self.rep, ctx.stage, ctx.st
        for tid, where in ctx.bought:
            cost = card_cost(self.facts, tid)
            if cost is None:
                rep.warn(where, f"买了 {tid}，但 card_facts.json 没有它的价格 → 这步没法对账"
                                f"（请补价格；不补就是让它蒙混过关）")
                continue
            dev = ctx.dev_zones[0]
            disc = Counter()
            for ident, n in st.zones[dev].items():
                if ident.startswith("card:"):
                    b = card_bonus(stage, ident[5:])
                    if b:
                        disc[b] += n
            b_self = card_bonus(stage, tid)
            if b_self:
                disc[b_self] = max(0, disc[b_self] - 1)     # 它自己不算自己的折扣
            need = {c: max(0, n - disc.get(c, 0)) for c, n in cost.items()}
            need = {c: n for c, n in need.items() if n}
            got = {c: n for c, n in ctx.paid.items() if c != "gold" and n}
            gold_used = ctx.paid.get("gold", 0)
            # 黄金是万能替代：宝石可以只付需要的一部分，差额必须正好由黄金补齐。
            # 但不能多付，也不能付出费用里没有的宝石种类。
            need_total = sum(need.values())
            exact_total = sum(got.values())
            over = {c: n for c, n in got.items() if n > need.get(c, 0)}
            if over or exact_total > need_total:
                rep.error(where, f"买 {tid}：价格 {cost} − 折扣 {dict(disc)} → 应实付 {need}，"
                                 f"实际付了 {got}（宝石种类/数量不符，不能超额支付）")
            else:
                short = need_total - exact_total
                if gold_used != short:
                    rep.error(where, f"买 {tid}：价格 {cost} − 折扣 {dict(disc)} → 应实付 {need}，"
                                     f"其中可用黄金替代 {short} 枚；实际付了 {got or '{}'} + {gold_used} 枚黄金")
                elif not got and not gold_used and need:
                    rep.error(where, f"买 {tid}：需要 {need}，但这一步**一枚宝石都没付**")


def run(anim, default_stage, stages, facts, rep: Report = None,
        on_event=None, on_cue_end=None, cue_start_states=None):
    """Compatibility entry point; use :class:`Ledger` for new code."""
    return Ledger(
        anim, default_stage, stages, facts, rep=rep,
        on_event=on_event, on_cue_end=on_cue_end,
        cue_start_states=cue_start_states,
    ).run()
