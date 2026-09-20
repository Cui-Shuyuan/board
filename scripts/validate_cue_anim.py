#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Validate the zone-based cue animation data.

Data layout
-----------
    games/{game}/tutorial/{track}.runtime.json             cue order / audio / duration
    games/{game}/tutorial/anim/_stage/{game}.table.json    table facts: zones, templates, initial
    games/{game}/tutorial/anim/{track}.json                一条 cue 一段：story/enter/exit + start/events

Model
-----
Animation = maintaining component state.  A component's state is (zone, order);
world position is derived from the zone layout.  So a cue contains no coordinates:
it only says things like "move gem#1 from its pile to player_holding".

Checks
------
* cue maps to a runtime cue and header fields agree
* stage file exists and is well formed (zones / templates / anchors / initial)
* every zone / template referenced by the cue exists
* events use the 8 primitives with the fields that primitive needs
* zone targets exist, `from` zones exist, `target` actor ids resolve
* timeline stays inside the cue audio duration
* warnings: unknown palette, animation ending too close to the audio end

Usage
-----
    python scripts/validate_cue_anim.py --game splendor --track full
    python scripts/validate_cue_anim.py --game splendor --track full --cue action.take.different.001
    python scripts/validate_cue_anim.py --cue setup.gems.003.1
    python scripts/validate_cue_anim.py --game splendor --track full --json

Exit codes: 0 = ok (warnings allowed), 1 = errors, 2 = file not found.
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from framing_geometry import (   # noqa: E402  —— 取景几何只此一份
    visible_rect, _zone_box, overlaps, resolve_zone_ref, zone_ref_hint,
)

# 原语名尽量与本体对齐：transfer = <ontology::transfer>、flip = <flip>、shuffle = <shuffle>。
ACTIONS = {"transfer", "flip", "rotate", "scale", "fade", "highlight", "point", "shuffle", "zone",
           "showbox", "create", "destroy", "wait", "stack"}

# 原语 → 它在规则上**默认**是哪个本体事件（None = 本体没有对应事件）。
# 事件可以写 `realizes` 说得更精确（发牌是 <top_draw>，而 <top_draw> 继承 <transfer>，
# 所以照样挂在 transfer 原语上）；表现层原语没有对应事件，写了 realizes 就是错的。
PRIMITIVE_EVENT = {
    "point": None,          # 指示物：纯表现（箭头/圈/禁止/叉），本体里没有"指着看"这件事
    "transfer": "<ontology::transfer>",
    "flip": "<flip>",
    "shuffle": "<ontology::shuffle>",
    "rotate": None, "scale": None, "fade": None, "highlight": None,
    "wait": None, "showbox": None, "create": None, "destroy": None, "stack": None,
}
SHAPES = {"panel", "gem", "shadow", "dot", "card"}

# camera 可以写的**组取景 token**（与 TutorialCueAnimPlayer.SetFraming 保持一致）。
#   board  = 整桌取景
#   cards  = 展示位三张卡背并排
#   supply = 整排供应区（按 panel_supply 色板判定成员）
# 除这些之外只能写某个已存在的 zone id。
# 为什么要校验：camera 写错时 SetFraming 会把它当 zone id 查不到 → **静默**沿用上一次取景，
# 画面悄悄不对，而引擎不报错。这正是本项目最怕的「静默失败」。
CAMERA_TOKENS = {"board", "cards", "supply"}

EASINGS = {
    "linear", "easeInQuad", "easeOutQuad", "easeInOutQuad",
    "easeInCubic", "easeOutCubic", "easeInOutCubic",
    "easeInBack", "easeOutBack", "easeInOutBack",
}

# Kept in sync with client/Assets/Scripts/Tutorial/Palette.cs
PALETTES = {
    "gem_diamond", "gem_sapphire", "gem_emerald", "gem_ruby", "gem_onyx",
    "gem_gold", "panel_supply", "panel_player", "panel_market", "panel_card",
    "panel_neutral", "accent_green", "accent_yellow", "accent_blue",
    "card_level_1", "card_level_2", "card_level_3", "noble", "marker", "shadow", "white",
}

MIN_TAIL_MARGIN = 0.15


class Report:
    def __init__(self, path: Path, name: str = None):
        self.path = path
        self.name = name or path.name
        self.errors = []
        self.warnings = []

    def error(self, where, message):
        self.errors.append({"where": where, "message": message})

    def warn(self, where, message):
        self.warnings.append({"where": where, "message": message})


MARKET_COLS = 4          # 市场每行几格（与 stage 的 layout.cols 一致）

# ── 字段归属（2026-09：把"单独声明的变量"逐个交代清楚）────────────────────
# 目标：**任何一个字段都必须能说出它属于哪一层**，没有归属的字段就是"单独声明的变量"，
# 要报错。三层分别是：
#
#   common       事件的通用部分：哪个原语、规则上是哪个事件、时间轴、取景、选择器
#   ontology     本体概念的字段 —— **由 realizes（或原语的默认概念）现场推导**，
#                不在代码里抄一遍。这才是"参数来自 concept"
#   presentation 表现层：本体对它没有也不该有话说（不影响任何组件状态）
#   impl         实现层：本体没有"出现/消失"这类事件，也没有"第几格"这个字段
#
# 这份清单本身就是"能否去掉单独声明的变量"的答案：
#   能去掉的 → 已经在 ontology 里（source/destination/quantity/what/to/target）
#   去不掉的 → 明确挂在 presentation / impl 名下，而不是混在原语参数里
COMMON_FIELDS = {"action", "realizes", "at", "dur", "lead", "easing",
                 "camera", "camera_padding", "target", "zone", "container"}

PRESENTATION_FIELDS = {
    "camera_fill": "取景填充率（这几个 zone 占画面中央的比例）",
    "camera_padding": "取景留白倍率",
    "part": "点哪个部位（模板 part_anchors 的 id）",
    "indicator": "指示物形状（arrow/circle/forbid/cross）",
    "grow": "高亮放大倍率",
    "peak_alpha": "高亮峰值透明度",
    "scale": "缩放倍率",
    "scale_mode": "缩放模式（by/to）",
    "to_alpha": "目标透明度",
    "angle": "旋转角（orientation 本体还没有这一维）",
    "on": "整幅图显示/隐藏",
    "picture": "整幅图是哪一张（盒面）",
    "amount": "洗混强度",
    "stagger": "同一批件错开起飞（纯节奏）",
    "op": "`zone` 原语：add（开出区域）/ remove（关掉）——实现层",
    "index": "`zone` 原语：`zone_defs` 里第几个实例——实现层",
    "group": "整组一起搬（同一段位移）",
    "fade_in": "搬运途中淡入",
}

IMPLEMENTATION_FIELDS = {
    "order": "落位序号 —— 「位置也是状态」，但本体 <zone> 还没有有序表字段",
    "slot": "order=-2 时的目标格位",
    "template": "确切素材；transfer 该用 what，这个只给 create/stack",
    "palette": "确切色板（同上）",
    "destination": "create/stack 的落点 —— 本体没有「出现/消失」这类事件，所以同名字段在这里"
                   "只有摆放含义（transfer 的 destination 才是本体 <transfer>.destination）。"
                   "这也是「牌堆该 create 还是 transfer」那个待决问题的体现",
    "count": "create 一次建几件",
    "plain": "create 不区分色板（一整摞牌）",
    "capacity": "stack 这一摞共几张",
    "real_templates": "stack 的真牌顺序",
    "pad_template": "stack 的垫牌模板",
}

# 字段名与本体的差异（本体叫 <object>，JSON 叫 what —— 见 TutorialCueAnimData.ConceptRef）
ONTOLOGY_FIELD_ALIAS = {"what": "<object>", "target": "target"}

# 复合字段：一个动画事件其实实现了**两个**本体事件，写起来合并成一条。
# 必须单独列出来 —— 否则它会以"说不出归属"的样子出现（这正是审计第一次跑出来的结果：
# 12 个发牌事件上的 `to` 找不到家，因为 <transfer> 没有 to，它是 <state_change> 的字段）。
COMPOSITE_FIELDS = {
    "to": "隐含的 <state_change>：搬运/创建的同时把状态设到某个值（本体要求拆成两个事件，"
          "动画里合并写；取值按 <state_change>.to 校验）",
}


def color_of_target(target: str):
    """从组件 id 里取出颜色名：market_card_1_emerald#1 → emerald。取不到返回 None。"""
    if not target:
        return None
    base = target.split("#")[0]
    for c in ("emerald", "ruby", "diamond", "sapphire", "onyx", "gold"):
        if base.endswith("_" + c):
            return c
    return None


def load_json(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


# ── 概念层：动画实例 ↔ 本体概念（2026-09 起）────────────────────────────
# 本体（ontology/concepts.json）和游戏概念（games/{game}/concepts.json）描述的是
# **同一个世界**：动画里的每个模板/区域都是本体某个概念的一个实例。这里的检查就是
# 让这份对等关系变成可验证的，而不是靠人记着。
_WORLD_CACHE: dict[str, object] = {}


def load_world(game_id: str):
    """加载本体 + 游戏概念（带缓存）。概念解析逻辑见 scripts/concept_ref.py。"""
    if game_id not in _WORLD_CACHE:
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        import concept_ref
        _WORLD_CACHE[game_id] = concept_ref.World(game_id)
    return _WORLD_CACHE[game_id]


def concept_of_template(stage, tpl_id, palette=None):
    """模板（+色板）实例化的是哪个概念。找不到/纯视觉返回 None。

    `concept_by_palette` 写成**列表**（`[{"palette":…,"concept":…}]`）而不是对象：
    JsonUtility 不支持字典，写成对象会被引擎**静默丢弃**。
    """
    for tpl in stage.get("templates", []):
        if tpl.get("id") != tpl_id:
            continue
        by_pal = tpl.get("concept_by_palette") or []
        if palette:
            for e in by_pal:
                if isinstance(e, dict) and e.get("palette") == palette and e.get("concept"):
                    return e["concept"]
        return tpl.get("concept")
    return None


def _parts_key(concept, parts):
    items = sorted(f"{p.get('key')}={p.get('value')}"
                   for p in (parts or []) if isinstance(p, dict) and p.get("key"))
    return concept + "|" + ",".join(items)


def concept_index(stage):
    """概念(+属性) → [(模板, 色板, 是不是样本)]。

    **与引擎 ZoneStore.BuildConceptIndex 是同一套规则**（互为镜像）：两边都从模板的
    concept / concept_by_palette / parts 推导，谁也不另存索引。改了一边要改另一边 ——
    所以两边的注释里都写着对方在哪。
    """
    idx = {}
    for tpl in stage.get("templates", []):
        smp = bool(tpl.get("sample"))
        by_pal = tpl.get("concept_by_palette") or []
        if by_pal:
            for e in by_pal:
                if not isinstance(e, dict) or not e.get("concept"):
                    continue
                parts = e.get("parts") or tpl.get("parts") or []
                idx.setdefault(_parts_key(e["concept"], parts), []).append(
                    (tpl["id"], e.get("palette"), smp))
            continue
        if not tpl.get("concept"):
            continue
        idx.setdefault(_parts_key(tpl["concept"], tpl.get("parts") or []), []).append(
            (tpl["id"], tpl.get("palette"), smp))
    return idx


def _real_only(cands):
    """样本（`sample: true`，介绍用的替身）不参与"要一件真件"的竞争。

    与引擎 `ZoneStore.ConceptCandidatesReal` 同一条规则：样本与真件绑同一套概念，
    所以全局点名会同时命中；但样本只活在介绍用的展示位里，真件区里不可能有它。
    """
    real = [c for c in cands if not (len(c) > 2 and c[2])]
    return real or cands


def _zone_concept(stage, zone_id):
    """这个 zone 绑定的是哪个本体概念（stage 里写的那个）。找不到 → None。

    `zone_id` 可以是**引用**（`<gem_supply|color=<diamond>>`）—— 先解析成 id 再查。
    """
    zone_id = resolve_zone_ref(stage, zone_id)
    if not zone_id:
        return None
    for z in (stage.get("zones") or []):
        if z.get("id") == zone_id:
            return z.get("concept")
    return None


def _zone_ctx(ev):
    """这条事件"在哪找/放到哪"的 zone —— what 的**唯一性**只在这个范围内成立。

    引擎解析 what 时是**在具体区域里**选的：transfer 从 source 里挑（`PickFront`），
    预置和 create 落在 zone。所以"全局有两个候选"不等于说不清 ——
    真件与样本（`gem` / `gem_sample`）概念相同、永远同时命中，
    只要事件写清了 zone，运行时就不存在歧义。反过来，一个 zone 都不给才真的说不清。
    """
    if ev.get("zone"):
        return ev["zone"]   # 可能是引用；调用方只把它当"作用域标签"用
    src = ev.get("source")
    if isinstance(src, list) and src:
        return src[0]
    if isinstance(src, str) and src:
        return src
    return ev.get("destination")


def what_candidates(stage, what, real_only=False):
    """本体语言的引用 → **候选** [(模板, 色板, 样本?)]。

    与引擎 `ZoneStore.ConceptCandidates` 同一套规则（互为镜像）：精确匹配（概念+属性）
    优先，没有就退回"同概念、属性不限"。返回空 = 概念名或属性写错了。

    real_only=True 时排除样本（`sample: true`）—— 用于"要搬/要补一件真件"的判定
    （transfer 的 what、start.set 预置）。高亮/销毁**不排除**：它们本来就该能点到展示位上的样本。
    """
    if not isinstance(what, dict) or not what.get("concept"):
        return []
    concept, parts = what["concept"], what.get("parts") or []
    idx = concept_index(stage)
    key = _parts_key(concept, parts)
    if idx.get(key):
        cands = list(idx[key])
    else:
        cands = []
        for k, v in idx.items():
            if k.startswith(concept + "|"):
                for c in v:
                    if c not in cands:
                        cands.append(c)
    return _real_only(cands) if real_only else cands


def contains_of_zone(stage, zone_id):
    """区域允许存放哪些概念。空/缺省 = 不限（本体 <zone>.contains 的语义）。"""
    zone_id = resolve_zone_ref(stage, zone_id)
    for z in stage.get("zones", []):
        if z.get("id") == zone_id:
            return z.get("contains") or []
    return []


def is_a(world, ref, ancestor) -> bool:
    """ref 是不是 ancestor 的后代（含自身）。概念之间靠 extends/specifies 连成链。"""
    cid, aid = world.resolve(ref), world.resolve(ancestor)
    return bool(cid and aid and aid in world.chain(cid))


def check_concept_binding(report, where, entity, world):
    """模板/区域必须显式声明它实例化的概念（可以显式写 null = 纯视觉件）。"""
    if "concept" not in entity and "concept_by_palette" not in entity:
        report.error(where, "没有 concept/concept_by_palette —— 必须显式写概念，"
                            "纯视觉件也要显式写 null（不然没人知道它属于世界观的哪一块）")
        return
    if entity.get("concept") and not world.resolve(entity["concept"]):
        report.error(where, f"concept = {entity['concept']!r} 在本体/游戏概念里找不到")


def derive_actor_ids(stage):
    """
    Reproduce ZoneStore.Spawn's id scheme: '{template}#{n}' with a per-template counter,
    numbered in stage.initial order regardless of zone.  This is the authoritative id set
    the runtime assigns, so cue targets are checked against it.
    """
    counters = {}
    ids = []
    for entry in stage.get("initial", []):
        tpl = entry.get("template", "")
        for _ in range(max(1, int(entry.get("count", 1)))):
            counters[tpl] = counters.get(tpl, 0) + 1
            ids.append(f"{tpl}#{counters[tpl]}")
    return ids


def validate_stage(stage_path: Path, report: Report, game_id: str):
    if not stage_path.exists():
        report.error("stage", f"牌桌文件不存在: {stage_path}")
        return None, set(), set()

    stage = load_json(stage_path)
    zones = {z["id"] for z in stage.get("zones", []) if z.get("id")}
    templates = {t["id"] for t in stage.get("templates", []) if t.get("id")}

    if stage.get("game_id") and stage["game_id"] != game_id:
        report.error("stage", f"game_id = {stage['game_id']!r}，应为 {game_id!r}")

    for i, zone in enumerate(stage.get("zones", [])):
        where = f"stage.zones[{i}]"
        if not zone.get("id"):
            report.error(where, "缺少 id")
            continue
        if "center" not in zone:
            report.error(where, f"zone {zone['id']} 缺少 center")
        layout = zone.get("layout") or {}
        for key in ("x_step", "z_step"):
            if float(layout.get(key, 0)) < 0:
                report.error(where, f"zone {zone['id']} 的 {key} 不能为负")
        if int(layout.get("cols", 1)) < 1:
            report.error(where, f"zone {zone['id']} 的 cols 至少为 1")

    for i, tpl in enumerate(stage.get("templates", [])):
        where = f"stage.templates[{i}]"
        if not tpl.get("id"):
            report.error(where, "缺少 id")
            continue
        if tpl.get("shape", "gem") not in SHAPES:
            report.error(where, f"未知 shape {tpl['shape']!r}")
        if tpl.get("palette") and tpl["palette"] not in PALETTES:
            report.warn(where, f"未知 palette {tpl['palette']!r}（见 Palette.cs）")

    # ── 概念绑定（动画 ↔ 本体）──────────────────────────────────────────
    # 规则：每个模板/区域都必须**显式**说清它实例化的是哪个本体概念，或者显式写 null
    # （纯视觉件）。不许省 —— 省了就没人知道新加的东西属于世界观的哪一块，
    # 而「牌堆里出现宝石」这类错误正是靠这条链才能自动查出来。
    world = load_world(game_id)
    for i, tpl in enumerate(stage.get("templates", [])):
        if not tpl.get("id"):
            continue
        where = f"stage.templates[{i}] {tpl['id']}"
        check_concept_binding(report, where, tpl, world)
        by_pal = tpl.get("concept_by_palette") or []
        for e in by_pal:
            if not isinstance(e, dict):
                report.error(where, "concept_by_palette 的每一项都要是 {palette, concept} 对象")
                continue
            pal, ref = e.get("palette"), e.get("concept")
            if pal and pal not in PALETTES:
                report.warn(where, f"concept_by_palette 的 palette {pal!r} 不是已知色板")
            if not world.resolve(ref):
                report.error(where, f"concept_by_palette[{pal!r}] = {ref!r} 在本体/游戏概念里找不到")
        for e in tpl.get("parts") or []:
            if isinstance(e, dict) and e.get("value") and not world.resolve(e["value"]):
                report.error(where, f"parts[{e.get('key')!r}] = {e['value']!r} 在本体/游戏概念里找不到")
    for i, zone in enumerate(stage.get("zones", [])):
        if not zone.get("id"):
            continue
        where = f"stage.zones[{i}] {zone['id']}"
        check_concept_binding(report, where, zone, world)
        for ref in zone.get("contains") or []:
            if not world.resolve(ref):
                report.error(where, f"contains 里的 {ref!r} 在本体/游戏概念里找不到")

    for i, anchor in enumerate(stage.get("anchors", [])):
        where = f"stage.anchors[{i}]"
        if not anchor.get("id"):
            report.error(where, "缺少 id")
        if anchor.get("template") not in templates:
            report.error(where, f"anchor {anchor.get('id')!r} 引用了未知 template {anchor.get('template')!r}")

    for i, entry in enumerate(stage.get("initial", [])):
        where = f"stage.initial[{i}]"
        if entry.get("template") not in templates:
            report.error(where, f"未知 template {entry.get('template')!r}")
        if entry.get("zone") not in zones:
            report.error(where, f"未知 zone {entry.get('zone')!r}")
        if entry.get("palette") and entry["palette"] not in PALETTES:
            report.warn(where, f"未知 palette {entry['palette']!r}")
        if int(entry.get("count", 1)) < 1:
            report.error(where, "count 至少为 1")

    return stage, zones, templates


def collect_created_ids(cue_docs):
    """扫一遍：哪些组件 id 会在某条 cue 里被 create 出来。

    跨 cue 的 create/destroy 是正常写法（cue 10 销毁 cue 9 创建的展示卡），
    所以判据要跨整条轨道，不能只看本条。
    """
    created = {}
    for doc in cue_docs:
        for ev in doc.get("events") or []:
            if not isinstance(ev, dict) or ev.get("action") != "create":
                continue
            tpl = ev.get("template")
            if not tpl:
                continue
            n = int(ev.get("count") or 1)
            created[tpl] = max(created.get(tpl, 0), n)
    return created


# 全轨道累计：字段 → 用了它的原语集合（--fields 报告用）
used_fields: dict[str, set[str]] = {}


def print_field_report():
    """把所有出现过的字段按归属层列出来 —— "能否去掉单独声明的变量"的答案。"""
    w = load_world("splendor")
    ontology_fields = {}
    for act, ref in sorted(PRIMITIVE_EVENT.items()):
        if not ref:
            continue
        for f in w.merged_fields(ref):
            ontology_fields.setdefault(f, set()).add(act)
    print("字段归属审计（全轨道实际用到的字段）")
    print("-" * 72)
    for title, bucket in (("本体概念字段（参数来自 concept）", ontology_fields),
                          ("复合字段（一条动画事件 = 两个本体事件）", COMPOSITE_FIELDS),
                          ("表现层（本体没有也不该有）", PRESENTATION_FIELDS),
                          ("实现层（本体没有对应事件）", IMPLEMENTATION_FIELDS)):
        print(f"\n【{title}】")
        for f, info in sorted(bucket.items()):
            names = {f} | {a for a, real in ONTOLOGY_FIELD_ALIAS.items() if real == f}
            used = "✔ 用到" if names & set(used_fields) else "·  未用"
            print(f"  {used}  {f:22s} {info if isinstance(info, str) else ''}")
    print("\n【通用字段】", ", ".join(sorted(COMMON_FIELDS)))
    stray = sorted(set(used_fields) - set(COMMON_FIELDS) - set(PRESENTATION_FIELDS)
                   - set(IMPLEMENTATION_FIELDS) - set(COMPOSITE_FIELDS)
                   - set(ontology_fields) - set(ONTOLOGY_FIELD_ALIAS))
    print("\n没有归属的字段:", stray if stray else "无 ✓")


def validate_cue(doc, cue_id, runtime_cues, track, game_id, report: Report,
                 created_ids=None, track_stage=None):
    where = cue_id

    dealt_slots = {}

    if cue_id not in runtime_cues:
        report.error(where, f"runtime 中不存在该 cue（{track}.runtime.json）")
        return
    duration = float(runtime_cues[cue_id].get("duration") or 0.0)

    stage_rel = doc.get("stage") or track_stage or f"_stage/{game_id}.table"
    stage_path = ROOT / "games" / game_id / "tutorial" / "anim" / (stage_rel + ".json")
    stage, zones, templates = validate_stage(stage_path, report, game_id)
    if stage is None:
        return
    world = load_world(game_id)

    known_ids = set(derive_actor_ids(stage)) | set((created_ids or {}).keys())
    # cue 自己 start.set 出来的组件（如发牌前预置在盒里的正面卡）也是合法目标
    for seed in (doc.get("start") or {}).get("set") or []:
        tpl = seed.get("template")
        if not tpl:
            continue
        n = int(seed.get("expand_to") or seed.get("count") or 1)
        for i in range(1, max(1, n) + 1):
            known_ids.add(f"{tpl}#{i}")

    start = doc.get("start") or {}
    for i, seed in enumerate(start.get("set") or []):
        sw = f"{where} start.set[{i}]"
        # 预置用 what（本体语言）说清是哪一种；预置是凭空造，候选必须唯一
        # —— 但"唯一"是**在 zone 里**唯一：同一概念的真件和样本（如 `gem` 与 `gem_sample`）
        # 全局必然都命中，写清 zone 就已经说清了是哪一件。
        if seed.get("what") is not None:
            cands = what_candidates(stage, seed["what"], real_only=True)
            if not cands:
                report.error(sw, f"start.set 的 what={seed['what'].get('concept')!r} 一个候选都没有"
                                 f"（概念名或属性写错了？）")
            elif len(cands) != 1 and not seed.get("zone"):
                report.error(sw, f"start.set 的 what={seed['what'].get('concept')!r} 有 {len(cands)} 个候选"
                                 f" {cands}，又没给 zone —— 预置说不清是哪一件")
            if seed.get("template"):
                report.warn(sw, "start.set 同时写了 what 和 template：what 才是本体语言，"
                                "template 只在没有对应概念时用")
        elif seed.get("template") not in templates:
            report.error(sw, f"未知 template {seed.get('template')!r}")
        if resolve_zone_ref(stage, seed.get("zone")) not in zones:
            report.error(sw, f"未知 zone {seed.get('zone')!r}"
                             f"（按引用解析成 {resolve_zone_ref(stage, seed.get('zone'))!r}）")
        if seed.get("palette") and seed["palette"] not in PALETTES:
            report.warn(sw, f"未知 palette {seed['palette']!r}")
        if int(seed.get("count", 1)) < 1:
            report.error(sw, "count 至少为 1")

    # 防重复：stage.initial 已经放过的 (template,palette,zone)，cue.start 再无条件 set 一次
    # 就会凭空多出一份组件（一份在 zone 里、一份留在原处），表现为「画面外有东西飘」。
    initialized = {}
    for entry in stage.get("initial", []):
        key = (entry.get("template"), entry.get("palette"), entry.get("zone"))
        initialized[key] = initialized.get(key, 0) + int(entry.get("count", 1))
    for i, seed in enumerate(start.get("set") or []):
        key = (seed.get("template"), seed.get("palette"), seed.get("zone"))
        if key in initialized and not seed.get("expand_to"):
            report.warn(f"{where} start.set[{i}]: stage.initial 已在 {seed.get('zone')} 放了 "
                        f"{initialized[key]} 个 {seed.get('template')}，这里又无条件 set {seed.get('count', 1)} 个，"
                        f"会重复生成；若只是想让它们就位，删掉这条即可，或改用 expand_to")

    events = doc.get("events")
    # 这一 cue 里"此刻可用的 zone"：stage 里声明的（一开始就全部建好）+ 本 cue 运行时开出来的
    available = set(zones)
    added_in_cue = {e.get("zone") for e in (events or [])
                    if isinstance(e, dict) and e.get("action") == "zone"
                    and (e.get("op") or "add") == "add" and e.get("zone")}
    # ── 契约覆盖：事件碰过的东西，契约必须声明（用户 2026-09-19 的原则）──────
    # "该有的有，不该有的就没有；脚本里没写有的那就是没有"。
    # 推论：**事件改动了哪个 zone，契约的 enter/exit 里就必须有它**（哪怕是 `count: 0`）。
    # 不声明的话，这一维根本没人比 —— 上一次漏掉盒面就是因为整幅图不在被比的集合里，
    # 而不是比较逻辑写错了。这条是静态检查：事件与契约现在同在一个文件里，不需要采样。
    _check_contract_coverage(doc, cue_id, events, report, stage)
    # 契约里声明的 zone 必须真的存在 —— 否则它是在**对着空气断言**：
    # 采样里没有这个区域、比较时按 0 算，于是"这里应该有几件"永远对不上（或永远没人比），
    # 而删掉一个 zone（例如游戏盒没有实体之后删掉 box_*）时，旧契约会静静地留在那儿。
    declared = set(((doc.get("enter") or {}).get("zones") or {}).keys()) | \
               set(((doc.get("exit") or {}).get("zones") or {}).keys())
    for zid in sorted(declared - set(zones) - added_in_cue):
        report.error(cue_id, f"契约声明了不存在的 zone {zid!r} —— "
                             f"它既不在 stage 的 zones 里、也不是本 cue 用 `zone add` 开出来的"
                             f"（改名/删掉之后忘了改契约？）"
                             f"对着不存在的区域断言，等于没人比这一维")

    if not isinstance(events, list) or not events:
        # 契约写了、动画还没写（例如 setup.nobles.001.1）：这不是错误，跳过动画检查。
        # 两者都没有才是真错误 —— 那条 cue 什么都不说。
        if not (doc.get("enter") or doc.get("exit")):
            report.error(where, "既没有 events，也没有契约（enter/exit）—— 这条 cue 什么都没说")
        return

    prev_at = -1.0
    last_end = 0.0
    for i, ev in enumerate(events):
        ew = f"{where} events[{i}]"
        action = ev.get("action", "move")
        # `what` 必须在这里就取：下面 <zone>.contains 的检查要用它判断"搬的是哪一类"。
        # 曾经它在循环末尾才赋值 → 那次检查读到的是**上一条事件的 what**（跨 cue 还会读到别的 cue 的），
        # 于是把「往持有区搬一枚黄金」误报成「把一张发展卡搬进持有区」。
        what = ev.get("what")
        if action not in ACTIONS:
            report.error(ew, f"未知 action {action!r}，只能是 {sorted(ACTIONS)}")
            continue

        at = float(ev.get("at", 0.0))
        dur = float(ev.get("dur", 0.0))
        lead = float(ev.get("lead", 0.0))
        if at < 0:
            report.error(ew, f"at 不能为负: {at}")
        if dur < 0:
            report.error(ew, f"dur 不能为负: {dur}")
        if at + 1e-6 < prev_at:
            report.warn(ew, f"事件未按 at 升序（上一条 at={prev_at:g}）")
        prev_at = max(prev_at, at)

        easing = ev.get("easing")
        if easing and easing not in EASINGS:
            report.error(ew, f"未知 easing {easing!r}")

        cam = ev.get("camera")
        if cam:
            # "a,b" = **一块取景框同时框住几个 zone**（引擎 SetFraming 的分支）。
            # 用途：介绍宝石的展示位和黄金展示位隔开一段距离，但要同框出现。
            # zone 之间不挨着也没关系 —— 框住的是它们的**外接矩形**，中间夹着的
            # 其他区域会一起入镜，所以那条检查（取景里出现了没提到的组件）同样适用。
            for part in [p.strip() for p in cam.split(",") if p.strip()]:
                if part in CAMERA_TOKENS or resolve_zone_ref(stage, part) in zones:
                    continue
                report.error(ew, f"未知 camera {part!r}（在 {cam!r} 里；每段只能是 "
                                 f"{sorted(CAMERA_TOKENS)} 之一，或某个已存在的 zone id；"
                                 f"写错会让取景静默退回上一次）")

        target = ev.get("target")
        zone = ev.get("zone")

        # ── `zone` 原语：运行时开/关一个区域（世界会长大）────────────────────
        # 定义必须先在 stage 的 `zone_defs` 里（**坐标只写在 stage**），
        # 脚本只说"现在把它开出来"；同一个定义要多个就给 `index`（id = `定义#N`）。
        if action == "zone":
            op = ev.get("op") or "add"
            defs = {d.get("id") for d in (stage.get("zone_defs") or [])}
            if op not in ("add", "remove"):
                report.error(ew, f"zone 的 op 只能是 add / remove，实际 {op!r}")
            elif not zone:
                report.error(ew, "zone 事件要写 zone（要开/关哪个区域）")
            elif op == "add":
                if zone in zones:
                    report.error(ew, f"zone {zone!r} 在 stage.zones 里**已经有了**（一开始就建好了），"
                                     f"不需要再 add；要后来才长出来的区域请写进 `zone_defs`")
                elif zone not in defs:
                    report.error(ew, f"zone add 的 {zone!r} 不在 stage.zone_defs 里 —— "
                                     f"新增区域的**定义**（坐标/布局/容量）必须写在 stage 这一层")
                else:
                    available.add(zone)
            elif zone not in available:
                report.error(ew, f"zone remove 的 {zone!r} 此刻并不存在（没在 stage 里、也没在本 cue 前文开出来）")
            else:
                available.discard(zone)
            continue   # zone 事件不改件，后面的检查与它无关

        # zone / destination 允许写**引用**（`<gem_supply|color=<diamond>>`）—— 先解析成 id
        zone_id = resolve_zone_ref(stage, zone) if zone else None
        if zone_id and zone_id not in available:
            report.error(ew, f"未知 zone {zone!r}" +
                             ("" if zone_id in zones else "（它是运行时开出来的区域吗？"
                              "那必须先在本 cue 前文写 `{\"action\":\"zone\",\"op\":\"add\",\"zone\":...}`）")
                             + ("" if zone_id == zone else f"（按引用解析成 {zone_id!r}）")
                             + zone_ref_hint(stage, zone))
        dest = ev.get("destination")
        dest_id = resolve_zone_ref(stage, dest) if dest else None
        if dest_id and dest_id not in available:
            report.error(ew, f"未知 destination {dest!r}" +
                             ("" if dest_id in zones else "（运行时开出来的区域要先 add 再用）")
                             + ("" if dest_id == dest else f"（按引用解析成 {dest_id!r}）")
                             + zone_ref_hint(stage, dest))

        # create 出来的组件 id 也算已知（模板名#序号），否则同一 cue 后续 target 会被误报
        if action == "create" and ev.get("template"):
            known_ids.add(f"{ev['template']}#1")
        if dest and zone:
            report.warn(ew, "同时写了 zone 和 destination：zone 是**选择器**（该区域全部），"
                            "转移的目的地请只写 destination")
        if target:
            # 引擎实例 id 是"第几个被创建"的产物：顺序播放（不靠 start.set 预置）时
            # 拿到的 id 完全不同。点名一件请用 what（+ zone/order）。
            report.warn(ew, f"用引擎实例 id target={target!r} 点名：id 随创建顺序变，"
                            f"顺序播放与单条预置会对不上；请改用 what（+ zone/order）")
        if target and target not in known_ids:
            report.error(ew, f"target {target!r} 不是牌桌上已知的组件 id")

        if action == "transfer":
            if not target and not ev.get("source"):
                report.error(ew, "transfer 需要 target（指定某件）或 source（本体 <transfer>.source，源 zone）")
            # source 必须是**数组**。写成字符串时 JsonUtility 会静默丢弃整个字段
            # （类型不匹配不报错），表现为 transfer 永远拿不到源 zone、牌堆搭不起来。
            raw_from = ev.get("source")
            if isinstance(raw_from, str):
                report.error(ew, f'transfer.source 必须写成数组：["{raw_from}"]（字符串会被静默丢弃）')
            sources = raw_from if isinstance(raw_from, list) else ([raw_from] if raw_from else [])
            if isinstance(raw_from, list) and not raw_from:
                report.error(ew, "transfer.source 是空数组")
            for source in sources:
                if resolve_zone_ref(stage, source) not in available:
                    report.error(ew, f"source zone {source!r} 不存在"
                                     + ("" if resolve_zone_ref(stage, source) == source
                                        else f"（按引用解析成 {resolve_zone_ref(stage, source)!r}）")
                                     + zone_ref_hint(stage, source))
            if not dest and not target:
                report.error(ew, "transfer 缺少目的地 destination")
            if int(ev.get("quantity", 0)) < 0:
                report.error(ew, "quantity 不能为负")
            # source+quantity 是按顺序取件：同一个 zone 里混放多种组件时极易取错，
            # 例如盒子里同时有宝石和卡片。用 quantity 时建议显式给 template。
            if (ev.get("source") and int(ev.get("quantity", 0) or 0) > 0
                    and not ev.get("what") and not ev.get("template")):
                report.warn(ew, "transfer 用 source+quantity 按顺序取件，却没写 what（也说不出是哪一类）；"
                                "若该 zone 混放多种组件，可能取到不该动的东西")
            if ev.get("order") == -2 and int(ev.get("slot", -1)) < 0:
                report.error(ew, "order=-2 需要同时给 slot（目标格位）")

            # ── 概念层：这次转移在规则上合不合法 ────────────────────────
            # 本体 <zone>.contains 的原话：「程序校验 <transfer> 时以此过滤——
            # 若 what 的类型不在 contains 中，<transfer> 非法」。动画的 move 就是
            # <transfer>，所以这条本来就该在这里查。空 = 不限（纯视觉区/镜头外通道）。
            # what = 本体语言的引用（推荐）；template = 实现层的素材名（transfer 不该用）
            if ev.get("template"):
                report.warn(ew, "transfer 用 template 指定素材：规则层请用 what（本体语言），"
                                "template 只留给 create/stack 这类实现层动作 —— 写模板名等于把"
                                "本作专用素材写进了动画数据")

            if dest:
                dest_ok = contains_of_zone(stage, dest)
                if what is not None and what.get("concept"):
                    moved = what["concept"]
                else:
                    moved = concept_of_template(stage, ev.get("template"), ev.get("palette")) \
                        if ev.get("template") else None
                if moved and dest_ok and not any(is_a(world, moved, c) for c in dest_ok):
                    report.error(ew, f"要把 {moved} 移进 {dest}，但该区域只允许 {dest_ok}"
                                     f"（本体 <zone>.contains）")
                elif not moved and dest_ok and sources:
                    # 事件没写 template，说不出搬的是哪一类：至少要求源区与目标区
                    # 允许的类型有交集，否则必然是把不该进去的东西搬进去了。
                    cands = [contains_of_zone(stage, s) for s in sources]
                    cands = [c for c in cands if c]
                    if cands and not any(is_a(world, a, b) for c in cands for a in c for b in dest_ok):
                        report.warn(ew, f"说不出移动的是什么（没写 template）：源区允许 "
                                        f"{cands}，目标区 {dest} 只允许 {dest_ok}，两者没有交集")
        elif action == "rotate":
            if "angle" not in ev:
                report.error(ew, "rotate 需要 angle")
        elif action == "flip":
            if ev.get("to") not in ("face_up", "face_down"):
                report.error(ew, "flip 需要 to（本体 <flip> 的写法）：'face_up' 或 'face_down'"
                                 "—— 不要用「取反」，那正是历史上一堆朝向问题的根因")
            # 本体 <flip>：「将 <card> 或 <tile> 翻至另一面」——只有**声明了 face 的
            # 概念**才谈得上翻面。宝石没有正反面（<card>.face 的说明里写「null 表示
            # 不区分正反」），对宝石 flip 是无声的空动作，要报出来。
            ref = concept_of_template(stage, ev.get("template"), ev.get("palette")) \
                if ev.get("template") else None
            if ref and not world.has_field(ref, "face"):
                report.warn(ew, f"对 {ref} 翻面，但该概念没有 face（本体 <card>/<tile> 才有）")
        elif action == "scale":
            if "scale" not in ev:
                report.error(ew, "scale 需要 scale（倍率）")
            if ev.get("scale_mode") not in (None, "to", "by"):
                report.error(ew, f"scale_mode 只能是 by/to，得到 {ev.get('scale_mode')!r}")
        elif action == "highlight":
            if not zone and not target and ev.get("what") is None:
                report.warn(ew, "highlight 既没有 zone/target 也没有 what，会对全体生效")
            peak = ev.get("peak_alpha")
            if peak is not None and not 0.0 <= float(peak) <= 1.0:
                report.error(ew, f"peak_alpha 超出 [0,1]: {peak}")
        elif action == "point":
            # 指示物：箭头/圈/禁止/叉。它指着**件的某个部位**，部位坐标写在模板的 part_anchors 里
            # （动画独有的"局部"概念 —— 本体只说这张牌印着什么，不说在牌面哪儿）。
            ind = ev.get("indicator") or "arrow"
            if ind not in POINTER_SHAPES:
                report.error(ew, f"未知 indicator {ind!r}，只能是 {sorted(POINTER_SHAPES)}")
            if not ev.get("target") and not ev.get("zone") and not ev.get("what") \
                    and not ev.get("container"):
                report.error(ew, "point 需要 target/zone/what/container（指哪一件）")
            part = ev.get("part")
            if part:
                # 部位必须在**可能被选中的模板**上存在，否则画面上什么都没有（静默失败）
                cands = what_candidates(stage, ev["what"], real_only=False) if ev.get("what") else None
                ids = [c.TemplateId for c in cands] if cands else None
                if ids is None:
                    zid = resolve_zone_ref(stage, ev.get("zone") or "") if ev.get("zone") else None
                    z = next((x for x in (stage.get("zones") or []) if x.get("id") == zid), None)
                    if z:
                        ids = [t.get("id") for t in (stage.get("templates") or [])
                               if _template_zone_compatible(t, z)]
                if ids:
                    have = [tid for tid in ids if part in _part_ids(stage, tid)]
                    if not have:
                        report.error(ew, f"point 的部位 {part!r} 在这些模板上都没定义：{ids}"
                                         f"（stage 模板的 part_anchors 里加它 —— 部位只有相对坐标，没有桌面坐标）")
        elif action == "create":
            if not ev.get("template"):
                report.error(ew, "create 需要 template（要创建什么）")
            elif ev["template"] not in templates:
                report.error(ew, f"create 的 template {ev['template']!r} 不在 stage.templates 里")
            if not ev.get("destination"):
                report.error(ew, "create 需要 zone（创建到哪里）")
            elif resolve_zone_ref(stage, ev.get("destination") or "offstage") not in available:
                report.error(ew, f"create 的 destination {ev.get('destination')!r} 不存在"
                                 f"（运行时开出来的区域要先 `zone add` 再用）"
                                 + zone_ref_hint(stage, ev.get("destination")))
        elif action == "destroy":
            if not ev.get("target") and not ev.get("zone") and not ev.get("template"):
                report.error(ew, "destroy 需要 target 或 zone/template（否则要销毁什么不明确）")
        elif action == "showbox":
            on = ev.get("on", 1)
            # 隐藏时不需要 picture（沿用当前显示的那张）
            if on not in (0, 1, 0.0, 1.0):
                report.warn(ew, f"showbox.on 只能写 0 或 1，当前 {on!r}")
            if on and not ev.get("picture"):
                report.error(ew, "showbox 显示时需要 picture（相对 games/{game} 的图片路径）")
        elif action == "shuffle":
            if not zone and not target:
                report.warn(ew, "shuffle 既没有 zone 也没有 target，会对全体生效")
            # 洗牌只对**牌堆/暗池**有意义。供应堆是公开、可互换的一堆东西，没有"洗"这回事 ——
            # 两者外观都是"一摞"，很容易顺手写错（用户 2026-09 特意提醒过）。
            zc = _zone_concept(stage, zone)
            if zc and is_a(world, zc, "<ontology::supply>") and not is_a(world, zc, "<ontology::deck>"):
                report.error(ew, f"zone {zone!r} 是**供应堆**（{zc}），不是牌堆 —— 供应堆不洗牌"
                                 f"（它是公开的一堆，拿哪一枚都一样）")

        # ── 字段归属：这个事件的每个字段都属于某一层吗 ──────────────────
        concept_ref = ev.get("realizes") or PRIMITIVE_EVENT.get(action)
        allowed = (set(COMMON_FIELDS) | set(PRESENTATION_FIELDS)
                   | set(IMPLEMENTATION_FIELDS) | set(COMPOSITE_FIELDS))
        if concept_ref and world.resolve(concept_ref):
            for f in world.merged_fields(concept_ref):
                allowed.add(f)
                for alias, real in ONTOLOGY_FIELD_ALIAS.items():
                    if real == f:
                        allowed.add(alias)
        # `what` 是本体 <object> 槽位 —— <transfer> 的字段说明里写着它
        # "同时承担 <event> 中 target 的语义"。所以**任何动作**都可以用它点名组件，
        # 哪怕这个动作本身（highlight/destroy）在本体里没有对应事件：
        # 它点名的是**组件**，不是事件。这就是"用概念+位置选件"取代引擎 id 的落点。
        allowed.add("what")

        # what 的解析检查对**任何动作**都做（highlight/destroy 也用它点名组件）
        what = ev.get("what")
        if what is not None:
            cands = what_candidates(stage, what, real_only=(action == "transfer"))
            if not cands:
                report.error(ew, f"what 一个候选都没有：concept={what.get('concept')!r}"
                                 f"（概念名或属性写错了？）")
            elif len(cands) > 1 and not _zone_ctx(ev) and ev.get("order", -1) < 0:
                report.error(ew, f"what 全局有 {len(cands)} 个候选 {cands}，又没给 zone/order"
                                 f" —— 说不清要哪一件")

        stray = sorted(set(ev) - allowed)
        if stray:
            report.error(ew, f"字段 {stray} 说不出归属层 —— 一个字段要么是本体概念"
                             f"（{concept_ref}）的字段，要么明确属于表现层/实现层；"
                             f"没有归属的就是「单独声明的变量」")
        for f in ev:
            used_fields.setdefault(f, set()).add(action)

        # ── realizes：这个动画事件在规则上是哪个本体事件 ────────────────
        wants = PRIMITIVE_EVENT.get(action)
        got = ev.get("realizes")
        if got:
            if wants is None:
                report.warn(ew, f"{action} 在本体里没有对应事件，不该写 realizes={got!r}")
            elif not world.resolve(got):
                report.error(ew, f"realizes={got!r} 在本体/游戏概念里找不到")
            elif not is_a(world, got, "<ontology::event>"):
                report.error(ew, f"realizes={got!r} 不是 <ontology::event> 的后代")
            elif not is_a(world, got, wants):
                report.error(ew, f"realizes={got!r} 不是 {wants} 的后代 —— {action} 原语只能"
                                 f"实现 {wants} 及其子类（例：发牌写 <top_draw>，它是 <transfer> 的子类）")
        # 源区是供应堆还是牌堆 —— **无论有没有写 realizes 都要查**。
        # 曾经把它挂在 `elif`（"没写 realizes 才提醒"）上，于是"写了 realizes 但写成抽牌"
        # 从旁边溜过去了（金丝雀验出来的）：写了 ≠ 写对了。
        if action == "transfer":
            # 「抽」与「搬」的分界在**源区是什么**，而这只能看**本体概念**，不能看外观：
            # 供应堆和牌堆**长得一模一样**（都是一摞），机制也共用（都是从一摞里取）——
            # 但供应堆是 <supply>（公开、可互换、没有"顶"），牌堆是 <deck>（有顶、抽取前身份未知）。
            # 用户 2026-09 特意提醒："它们绝对不是一个东西"。所以这条检查按概念判，不按 display.mode。
            for src in (ev.get("source") or []):
                c = _zone_concept(stage, src)
                if not c:
                    continue
                is_deck = is_a(world, c, "<ontology::deck>") or is_a(world, c, "<ontology::pool>")
                is_supply = is_a(world, c, "<ontology::supply>") and not is_deck
                if is_deck:
                    if not got:
                        report.warn(ew, f"从暗堆 {src}（{c}）取件却没写 realizes；按本体的判据这是 "
                                        f"<top_draw>（抽取前身份未知），不是普通 <ontology::transfer>")
                    elif not is_a(world, got, "<ontology::draw>"):
                        report.error(ew, f"从牌堆 {src}（{c}）取件，realizes 写的是 {got!r} —— "
                                         f"从牌堆取出只能**抽**（<draw> 的后代，如 <top_draw>）："
                                         f"牌堆有「顶」、抽取前身份未知")
                elif is_supply and got and is_a(world, got, "<ontology::draw>"):
                    report.error(ew, f"源区 {src} 是**供应堆**（{c}），供应堆没有「顶」可抽 —— "
                                     f"realizes={got!r} 把它记成了抽牌。供应堆取出就是普通 "
                                     f"<ontology::transfer>（公开的一堆，拿哪一枚都一样）。"
                                     f"⚠️ 供应堆与牌堆长得一样、机制也共用，但**不是一个东西**")

        end = at + lead + dur
        if action != "wait":
            last_end = max(last_end, end)

        # 记录发牌格位：用于检查「每行颜色组合是否雷同」
        slot = ev.get("slot")
        if action == "transfer" and (ev.get("destination") or target) and \
                (ev.get("destination") == "card_market") and isinstance(slot, int) and slot >= 0:
            dealt_slots[slot] = target or ""

    # 市场是多行网格（4 列）：两行颜色顺序完全相同会误导观众，
    # 让人以为「必须这样摆」。真实市场是发牌结果，不会整齐成列。
    if dealt_slots:
        rows = {}
        for slot, target in dealt_slots.items():
            rows.setdefault(slot // MARKET_COLS, [None] * MARKET_COLS)[slot % MARKET_COLS] = target
        sigs = {}
        for row in sorted(rows):
            colors = tuple(color_of_target(t) for t in rows[row])
            sigs.setdefault(colors, []).append(row)
        for colors, rowlist in sigs.items():
            if len(rowlist) > 1 and any(c for c in colors):
                report.warn(where, f"市场第 {'、'.join(str(r+1) for r in rowlist)} 行的颜色顺序完全相同"
                                   f"（{' '.join(str(c) for c in colors)}）—— "
                                   f"真实市场不会这么整齐，容易让人误以为必须这样摆")

        if duration > 0 and end > duration + 1e-6:
            report.error(ew, f"事件结束于 {end:.2f}s，超出 cue 音频时长 {duration:.2f}s")

    # 展示卡的「位置即身份」约定：sample_back_N 必须放在 showcase_N。
    # 讲解发展卡时靠**位置**区分三个级别（卡背画作颜色不可靠：三张扫描图都偏蓝）。
    # 这条规则防止将来把某张挪到别的展示位、导致"高亮的那张"与台词不对应。
    for ev in doc.get("events") or []:
        if not isinstance(ev, dict) or ev.get("action") != "create":
            continue
        tpl = ev.get("template") or ""
        zone = ev.get("zone") or ""
        if tpl.startswith("sample_back_") and zone.startswith("showcase_"):
            want = "showcase_" + tpl[len("sample_back_"):]
            if zone != want:
                report.error(where, f"{tpl} 应放在 {want}（位置即身份），实际放在 {zone}")

    if duration > 0 and last_end > 0 and duration - last_end < MIN_TAIL_MARGIN:
        report.warn(where, f"动画结束 {last_end:.2f}s 距音频结束 {duration:.2f}s 不足 {MIN_TAIL_MARGIN:.2f}s")



def check_no_game_box_zone(stage, world, report):
    """**游戏盒是抽象概念，没有实体**（用户 2026-09）。

    说"从盒子里拿出来" = `create`；"放回盒子" = `destroy`。
    所以 stage 里不该有 concept 是 `<ontology::game_box>`（或其后代）的 zone ——
    那种 zone 会把盒子变成一个**看得见吗？看不见**的容器：件能在里面躺着、被搬进搬出，
    而观众什么都看不到，于是"凭空多一件/少一件"在**状态对账里也看不出来**
    （它只是从一个看不见的 zone 挪到另一个看不见的 zone）。

    这条把那个"藏东西的地方"从根上堵掉 —— 每一枚件都必须在被契约断言着的区域里。
    """
    for z in stage.get("zones") or []:
        c = z.get("concept")
        if c and is_a(world, c, "<ontology::game_box>"):
            report.error("stage", f"zone {z.get('id')!r} 的 concept 是 {c}（游戏盒）—— "
                                  f"游戏盒是抽象概念、没有实体：「从盒里拿出来」写 `create`、"
                                  f"「放回盒子」写 `destroy`，不要建「盒子 zone」")


def check_cleanup_timing(files, report, stage):
    """**切取景之后才清场** = 切换后的第一帧是脏的（用户 2026-09 报的 cue 18）。

    cue 18（`setup.gems.003.1`）曾经这么写：`at=0.0` 切到供应区特写、`at=0.3` 才销毁
    展示位上那 6 枚样本 —— 镜头已经对着供应区了，样本还在画面里停了 0.3 秒。

    规则（既有那条"改画面的动作要与改取景同帧"的反方向）：

      - **新增内容**可以在切镜头**之后**（观众等着看它出现）；
      - **清掉内容**必须在切镜头**之前或同帧**，否则切换后的第一帧里它还在。

    为什么必须放在**跨 cue** 这一层：判据是"取景**变了**没有"。
    一条 cue 里写了 `camera: "supply"`、而上一条结尾本来就是 `supply`，
    那就没有切换、也就没有脏帧 —— 单条 cue 的检查看不到上一条的取景，
    会把这种写法误报（`setup.nobles.001.1` 第一次就误报了）。
    """
    prev_camera = None
    for doc in files:
        events = doc.get("events") or []
        cue_id = doc.get("cue") or "?"
        cur = prev_camera
        for i, ev in enumerate(events):
            if not isinstance(ev, dict):
                continue
            cam = ev.get("camera")
            if cam:
                changed = (cam != cur)
                cur = cam
                if not changed:
                    continue
                rect = visible_rect(stage, cam, float(ev.get("camera_padding") or 0.0),
                                    float(ev.get("camera_fill") or 0.0))
                if rect is None:
                    continue                       # 整桌取景：没有"出框"可言
                t_cam = float(ev.get("at", 0.0))
                for j, later in enumerate(events):
                    if j <= i or not isinstance(later, dict):
                        continue
                    action = later.get("action")
                    clears = (action == "destroy"
                              or (action == "showbox" and float(later.get("on") or 0) == 0))
                    if not clears or float(later.get("at", 0.0)) <= t_cam:
                        continue
                    zid = resolve_zone_ref(stage, later.get("zone"))
                    z = next((x for x in (stage.get("zones") or []) if x.get("id") == zid), None)
                    if not overlaps(rect, _zone_box(stage, z) if z else None):
                        continue                   # 不在取景里：这一下清场看不见
                    what = f"销毁 {zid}" if action == "destroy" else "关掉整幅图"
                    report.error(
                        cue_id,
                        f"events[{j}]: 取景已在 at={t_cam:.2f} 切到 {cam!r}（上一条留下的是 "
                        f"{prev_camera!r}），{float(later.get('at', 0.0)):.2f} 才{what}，而它在取景框里 —— "
                        f"切换后的第一帧里画面还留着它，看起来就是「初始帧多了几个东西」。"
                        f"清场要与切取景**同帧**（写同一个 at）")
        cams = [e for e in events if isinstance(e, dict) and e.get("camera")]
        prev_camera = cams[-1].get("camera") if cams else prev_camera


def check_framing_chain(files, report):
    """跨 cue 跟踪取景链。

    **不写 camera = 自动继承上一条的最终取景**，这是多棵树/镜头继承设计的正常用法，
    不再因为“第一条改状态的事件在 at>0”而报 warning：那正好证明本条没有动镜头。

    真正需要硬保证的是**换树/起树**第一条 cue 必须显式给出 at=0 camera；
    那条现在由 `check_tree_entry_camera` 报 error。
    """
    prev_camera = None
    for doc in files:
        events = doc.get("events") or []
        cams = [e for e in events if e.get("camera")]
        prev_camera = cams[-1].get("camera") if cams else prev_camera

def check_tree_entry_camera(cues, report):
    """换树的第一条 cue 必须在 at=0 声明 camera（哪怕只是 board）。

    用户 2026-09-21 反复踩到“先把树切了、再等 Update 切镜头”：stage 切完到 camera 生效之间
    会露出一帧旧/全局镜头。stage 与镜头必须同一帧切，所以把这条变成数据硬规则：
    每条 **tree 变化**的第一条 cue 都要有 `at=0` 的 camera 事件；靠“上一条/默认镜头”
    在跨树时没有任何意义。
    """
    prev_tree = None
    for c in cues:
        tree = c.get("tree") or "main"
        first_or_changed = prev_tree is None or tree != prev_tree
        if first_or_changed:
            cams = [e for e in (c.get("events") or [])
                    if isinstance(e, dict) and e.get("camera")]
            hit = any(float(e.get("at", 0.0)) <= 1e-6 for e in cams)
            if not hit:
                report.error(c.get("cue") or "?",
                             "换树/起树的第一条 cue 必须在 at=0 显式声明 camera"
                             "（哪怕写 `camera: \"board\"`）—— stage 与镜头必须同一帧切换；"
                             "跨树时“继承上一条/默认镜头”没有意义")
        prev_tree = tree


# 会**改变组件状态**的动作：它们碰过的 zone，契约必须声明
STATE_CHANGING = {"transfer", "create", "destroy", "stack"}

# 指示物形状（与 TutorialCueAnimPlayer.Pointers.cs 的 PointerSprite 一致）
POINTER_SHAPES = {"arrow", "circle", "forbid", "cross"}


def _part_ids(stage, template_id):
    for t in (stage.get("templates") or []):
        if t.get("id") == template_id:
            return {p.get("id") for p in (t.get("part_anchors") or [])}
    return set()


def _template_zone_compatible(tpl, zone):
    """这个模板有没有可能出现在这个 zone 里（按本体 contains 判）。"""
    contains = zone.get("contains") or []
    if not contains:
        return True
    concept = tpl.get("concept")
    if not concept:
        return True
    return any(concept == c for c in contains)


# 会**改变组件状态**的动作：它们碰过的 zone，契约必须声明
STATE_CHANGING = {"transfer", "create", "destroy", "stack"}


def _check_cleanup_timing(cue_id, events, stage, report):
    """**切取景之后才清场** = 切换后的第一帧是脏的（用户 2026-09 报的 cue 18）。

    那一 cue 是这么写的：`at=0.0` 切到供应区特写、`at=0.30` 才销毁展示位上那 6 枚样本。
    于是镜头已经对着供应区了，展示位的 6 枚宝石还在画面里停了 0.3 秒 ——
    用户一眼就看见"初始帧多了好几个宝石"，而不是"有个东西被清掉了"。

    规则（是既有那条"改画面的动作要与改取景同帧"的**反方向**，两条合起来才完整）：

      - **新增内容**可以在切镜头之后（观众等着看它出现）；
      - **清掉内容**必须在切镜头**之前或同帧**，否则切换后的第一帧里它还在。

    为什么上一轮的对账没查出来：对账比的是"契约声明的 zone vs 采样状态"，
    而那 6 枚样本**确实在**入口状态里、契约也**如实声明了**（gem_display: 5）——
    状态是对的，错的是**先后**。所以这条必须查"时间"，不能只查"状态"。

    只对**特写取景**查（整桌取景没有"出框"可言，清场看得见也是正常的）；
    只查 `destroy` 与"关掉整幅图"这类**纯移除**，不查移动（移动是给人看的动作）。
    """
    cams = [(i, ev) for i, ev in enumerate(events)
            if isinstance(ev, dict) and ev.get("camera")]
    if not cams or not stage:
        return
    for i, cev in cams:
        cam = cev.get("camera")
        rect = visible_rect(stage, cam, float(cev.get("camera_padding") or 0.0),
                            float(cev.get("camera_fill") or 0.0))
        if rect is None:          # 整桌取景：不限制，跳过
            continue
        t_cam = float(cev.get("at", 0.0))
        for j, ev in enumerate(events):
            if j <= i or not isinstance(ev, dict):
                continue
            action = ev.get("action")
            clears = action == "destroy" or (action == "showbox" and float(ev.get("on") or 0) == 0)
            if not clears:
                continue
            t_ev = float(ev.get("at", 0.0))
            if t_ev <= t_cam:
                continue          # 同帧或更早 = 合格
            zone = ev.get("zone")
            box = _zone_box(stage, next((z for z in (stage.get("zones") or [])
                                         if z.get("id") == zone), None)) if zone else None
            if not box or not overlaps(rect, box):
                continue          # 不在取景里：这一下清场看不见
            what = f"销毁 {zone}" if action == "destroy" else "关掉整幅图"
            report.error(
                f"events[{j}]",
                f"取景已在 at={t_cam:.2f} 切到 {cam!r}，{t_ev:.2f} 才{what}，"
                f"而它在取景框里 —— 切换后的第一帧（{t_ev - t_cam:.2f}s 内）画面里还留着它，"
                f"看起来就是「初始帧多了几个东西」。清场要与切取景**同帧**（写同一个 at）")


def _check_contract_coverage(doc, cue_id, events, report, stage):
    """事件碰过的维度，契约里必须声明过 —— 否则那一维无法比对。

    只查"改状态"的动作：highlight/fade/scale/wait 是表现层，不改变"谁在哪、几件"。
    showbox 单列一条：它改的是整幅图，契约必须声明 `picture`（哪怕写 null）。
    """
    declared = set()
    for part in ("enter", "exit"):
        declared |= set(((doc.get(part) or {}).get("zones") or {}).keys())
    declared_picture = any("picture" in (doc.get(part) or {}) for part in ("enter", "exit"))

    touched = set()
    showbox = False
    for ev in events:
        if not isinstance(ev, dict):
            continue
        action = ev.get("action")
        if action == "showbox":
            showbox = True
            continue
        if action not in STATE_CHANGING:
            continue
        # 解析成 id：契约的 zone 键是 id，写引用时也要能对上
        for src in ev.get("source") or []:
            touched.add(resolve_zone_ref(stage, src))
        if ev.get("destination"):
            touched.add(resolve_zone_ref(stage, ev["destination"]))
        if action == "destroy" and ev.get("zone"):
            touched.add(resolve_zone_ref(stage, ev["zone"]))

    missing = sorted(t for t in touched if t and t not in declared)
    if missing:
        report.warn(cue_id, f"这些 zone 被事件改动了，但契约里没声明：{missing} —— "
                            f"「没写就是不该有」，不声明就没法比对（空也要写 count: 0）")
    if showbox and not declared_picture:
        report.warn(cue_id, "有 showbox 事件，但契约没声明 picture —— 整幅图这一维没法比对")


def tree_id_of_cue(cue):
    """cue 属于哪棵树；空 = 默认主树。与引擎 TreeIdForCue 同一口径。"""
    return (cue or {}).get("tree") or "main"


def tree_stage_of(doc, tree_id):
    """树 id → stage 相对路径；没登记时退回 track 级默认 stage。"""
    for tree in (doc or {}).get("trees") or []:
        if isinstance(tree, dict) and tree.get("id") == tree_id:
            return tree.get("stage")
    return (doc or {}).get("stage")


def load_script(args):
    """读这条 track 的脚本（一个动画一个文件）。返回 (路径, 文档, runtime 里的 cue 表)。"""
    game_root = ROOT / "games" / args.game
    path = Path(args.file) if args.file else game_root / "tutorial" / "anim" / (args.track + ".json")
    if not path.exists():
        print(f"脚本不存在: {path}", file=sys.stderr)
        return path, None, {}
    try:
        doc = load_json(path)
    except json.JSONDecodeError as exc:
        print(f"JSON 解析失败: {path}: {exc}", file=sys.stderr)
        return path, None, {}
    runtime_path = game_root / "tutorial" / (args.track + ".runtime.json")
    runtime_cues = {}
    if runtime_path.exists():
        runtime_cues = {c["id"]: c for c in load_json(runtime_path).get("cues", [])}
    return path, doc, runtime_cues


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--cue", help="只校验这一条 cue")
    parser.add_argument("--file", help="直接校验指定文件")
    parser.add_argument("--json", action="store_true", help="机器可读输出")
    parser.add_argument("--fields", action="store_true",
                        help="字段归属审计：每个事件字段属于本体/复合/表现层/实现层哪一层")
    args = parser.parse_args()

    if args.fields:
        # 只会走一遍脚本把字段收集齐，然后打印归属表（不判对错）
        f0, doc0, _ = load_script(args)
        if doc0:
            created0 = collect_created_ids(doc0.get("cues") or [])
            for c in doc0.get("cues") or []:
                if c.get("cue"):
                    stage_rel = tree_stage_of(doc0, tree_id_of_cue(c))
                    validate_cue(c, c["cue"], {}, args.track, args.game,
                                 Report(f0, c["cue"]), created0, stage_rel)
        print_field_report()
        return 0

    script_path, doc, runtime_cues = load_script(args)
    if doc is None:
        return 2
    if not runtime_cues:
        print(f"runtime 不存在或为空: {script_path.with_name(args.track + '.runtime.json')}",
              file=sys.stderr)
        return 2

    # **按脚本里的顺序**（= 轨道顺序）走，不按文件名字母序：取景是延续状态，
    # 跨 cue 检查必须走真实播放顺序。
    cues = [c for c in (doc.get("cues") or []) if c.get("cue")]
    if args.cue:
        cues = [c for c in cues if c["cue"] == args.cue]
        if not cues:
            print(f"脚本里没有这条 cue: {args.cue}", file=sys.stderr)
            return 2
    if not cues:
        print(f"脚本里没有 cue（{script_path}）")
        return 0

    # track 级的自述（一个动画一个文件之后，schema/game/track 只在这里声明一次）
    if doc.get("schema_version") != 1:
        print(f"warn  schema_version = {doc.get('schema_version')!r}，当前校验器针对 1", file=sys.stderr)
    if doc.get("game_id") and doc["game_id"] != args.game:
        print(f"error game_id = {doc['game_id']!r}，应为 {args.game!r}", file=sys.stderr)
        return 2
    if doc.get("track") and doc["track"] != args.track:
        print(f"error track = {doc['track']!r}，应为 {args.track!r}", file=sys.stderr)
        return 2

    created = collect_created_ids(doc.get("cues") or [])
    reports = []
    for c in cues:
        report = Report(script_path, c["cue"])
        stage_rel = tree_stage_of(doc, tree_id_of_cue(c))
        validate_cue(c, c["cue"], runtime_cues, args.track, args.game,
                     report, created, stage_rel)
        reports.append(report)

    # 跨 cue 检查：取景是延续状态，只有按顺序比才看得出来
    # （只校验整条轨道时做，单条 --cue 没有上下文）
    if not args.cue and reports:
        # 取景链、清场时机、舞台检查都必须**按树分治**：跨树是 cut，
        # 上一棵树的 camera/状态不延续到下一棵树。
        groups = {}
        for c in cues:
            groups.setdefault(tree_id_of_cue(c), []).append(c)
        for tree_id, group in groups.items():
            chain = Report(script_path, f"（跨 cue 取景链 {tree_id}）")
            check_framing_chain(group, chain)
            stage_doc = None
            rel = tree_stage_of(doc, tree_id)
            if rel:
                sp = ROOT / "games" / args.game / "tutorial" / "anim" / f"{rel}.json"
                if sp.exists():
                    stage_doc = json.loads(sp.read_text(encoding="utf-8"))
            if stage_doc:
                check_no_game_box_zone(stage_doc, load_world(args.game), chain)
                check_cleanup_timing(group, chain, stage_doc)
            reports.append(chain)
        cut = Report(script_path, "（换树镜头口径）")
        check_tree_entry_camera(cues, cut)
        reports.append(cut)

    total_errors = sum(len(r.errors) for r in reports)
    total_warnings = sum(len(r.warnings) for r in reports)

    if args.json:
        errors, warnings = [], []
        for r in reports:
            for e in r.errors:
                errors.append({"cue": r.name, **e})
            for w in r.warnings:
                warnings.append({"cue": r.name, **w})
        print(json.dumps({"ok": total_errors == 0, "cues": len(cues),
                          "errors": errors, "warnings": warnings}, ensure_ascii=False, indent=2))
    else:
        for r in reports:
            print(f"{'OK ' if not r.errors else 'ERR'} {r.name}"
                  + (f"  ({len(r.warnings)} warning)" if r.warnings else ""))
            for e in r.errors:
                print(f"    error  {e['where']}: {e['message']}")
            for w in r.warnings:
                print(f"    warn   {w['where']}: {w['message']}")
        print(f"\n{len(cues)} 条 cue，{total_errors} 个错误，{total_warnings} 个警告")

    return 1 if total_errors else 0


if __name__ == "__main__":
    sys.exit(main())
