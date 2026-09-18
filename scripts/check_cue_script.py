#!/usr/bin/env python3
"""对账：把「引擎采样出来的状态」和「脚本里写的契约」比一遍。

用法：
    # 单条查自己（默认查出口）
    python3 scripts/check_cue_script.py --cue setup.gems.003.1
    python3 scripts/check_cue_script.py --cue setup.gems.003.1 --which enter
    # 这一小节全查：每条自己的出口 + 跨 cue 的链（入口 vs 父 cue 的终态）
    python3 scripts/check_cue_script.py --all
    # 只查跨 cue 的链
    python3 scripts/check_cue_script.py --chain

数据在哪（一个动画一个文件）：
    games/{game}/tutorial/anim/{track}.json             **动画脚本**：每条 cue 的 story/enter/exit/timing
                                                        + start/events。本工具只读契约那半（story/enter/exit）；
                                                        引擎只读 start/events。
    games/{game}/tutorial/anim/{track}.exitstate.json   采样终态 —— 引擎生成，别手改
    --script / --states 覆盖这两条路径；--state 直接给一份单独的采样文件（临时查一条用）

为什么这么分：契约是人写的意图 + 首尾状态，采样是引擎跑出来的事实，两者结构相同 → 可 diff，
出现差异时配合契约里的 story 就能判断**是脚本写错了，还是动画做错了**。
以前契约一条 cue 一个文件，翻起来要开十几个文件；现在一个 track 一个文件、按轨道顺序排。

注意别和 `script.{track}.json`（口播稿编辑源）搞混：那个是口播链路的输入/输出，
本文件是动画契约，两条链路互不写对方。
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from framing_geometry import (   # noqa: E402  —— 取景几何只此一份，见模块开头
    CAMERA_TOKENS, frame_bounds, visible_rect, _slot_at, _zone_box, _union, _in_rect,
)

ROOT = Path(__file__).resolve().parent.parent


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


# 契约里用**语义名**（给人看），引擎给的是 模板id|色板（给机器用）。
# 这张表负责把语义名翻成"模板里应当出现的片段"。
COLORS = {"绿": "emerald", "红": "ruby", "白": "diamond", "蓝": "sapphire",
          "黑": "onyx", "gold": "gold", "黄金": "gold"}


def kind_matches(want_key, got_key):
    """语义名 → 引擎身份键。

    契约写 "一级绿"、"emerald"、"宝石绿" 这类**人能看懂的名字**；
    引擎给的是 "market_card_1_emerald|card_level_1"。
    两边对不上时，人看契约、机器看引擎，所以这里做翻译。
    """
    if want_key == got_key:
        return True
    tmpl = got_key.split("|")[0]

    # 纯宝石名（gem_emerald 这种色板）
    if want_key in COLORS.values():
        return got_key.endswith("|gem_" + want_key)
    # "宝石绿" 形式
    if want_key.startswith("宝石") and want_key[2:] in COLORS:
        return got_key.endswith("|gem_" + COLORS[want_key[2:]])
    # 黄金（和宝石同一个模板/样例模板，但本体上是 <gold> 不是 <gem>）
    if want_key in ("黄金", "gold", "金币"):
        return got_key.endswith("|gem_gold")
    # "一级绿"、"三级黑" 形式 → market_card_<级>_<色>
    for lv, num in (("一", 1), ("二", 2), ("三", 3)):
        if want_key.startswith(lv + "级") and want_key[2:] in COLORS:
            return tmpl == f"market_card_{num}_{COLORS[want_key[2:]]}"
    # "一级正面" / "一级卡背" → 展示位用的样本模板
    for lv, num in (("一", 1), ("二", 2), ("三", 3)):
        if want_key == f"{lv}级正面":
            return tmpl in (f"sample_card_{num}", f"market_card_{num}_")
        if want_key == f"{lv}级卡背":
            return tmpl == f"sample_back_{num}"
        # 牌堆里的垫牌（只为让牌堆看上去有几十张，永远发不出来）
        if want_key == f"{lv}级垫牌":
            return tmpl == f"blank_card_{num}"
    # 贵族板块：模板 id 就叫 noble
    if want_key in ("贵族", "贵族板块"):
        return tmpl == "noble"
    # 兜底：当作模板 id 前缀
    return want_key == tmpl


def _kind_record(v):
    """kinds 的值有两种写法：`3`（只报数量）或 `{"count":3,"face_up":3,...}`（带状态）。

    老采样文件里只有计数；新采样（2026-09 起）逐身份带 face/shows。
    两种都要能读 —— 但**读不出状态时要明说"采样太旧"**，不能让契约里的
    face 断言悄悄退化成"没查到就当对"。
    """
    return v if isinstance(v, dict) else {"count": v}


def _kind_state_fields(rec):
    return {k for k in rec if k in ("face_up", "face_down", "shows_face", "shows_back", "hidden")}


# 状态值 → 采样里的计数字段
_FACE_FIELD = {"up": "face_up", "down": "face_down", "正面": "face_up", "反面": "face_down"}
_SHOWS_FIELD = {"face": "shows_face", "back": "shows_back", "hidden": "hidden",
                "正面": "shows_face", "反面": "shows_back"}


def check_zone(name, want, got):
    """比对单个 zone，返回差异列表。

    可比的字段：
      count / face_up / face_down / shows_face / shows_back / hidden   —— 整个区域
      kinds[身份] = 数量，或 `{"count":n, "face":"up"/"down", "shows":"face"/"back", ...}`
        face  = 这一身份的件**全都**是某一面（写起来最像人话，推荐）
        shows = 画面上**实际显示**的是哪一面（这一条才是"翻面到底成没成"的证据）
    """
    diffs = []
    got = got or {}
    got_kinds = {k: _kind_record(v) for k, v in (got.get("kinds") or {}).items()}
    # 这个区域里的件**全都没有背图**吗 —— 是的话，face 这一维对它不存在（见 _TWO_SIDED）
    sides = [_two_sided(k) for k in got_kinds]
    face_absent = bool(sides) and all(s is False for s in sides)

    # 单面件断朝向：报一条就够，不要因为它写了 face_up 和 face_down 而报两遍
    if face_absent and ("face_up" in want or "face_down" in want):
        diffs.append(f"{name}.face_up/face_down: 契约断了朝向，但这里没有一件有背图（单面件）"
                     f"—— 这一维对它**不存在**，去掉这条断言；真要谈朝向就先给它一张背图")

    for field in ("count", "face_up", "face_down", "shows_face", "shows_back", "hidden"):
        if field not in want:
            continue
        if field in ("face_up", "face_down") and face_absent:
            continue
        w, g = want[field], got.get(field, 0)
        if w != g:
            diffs.append(f"{name}.{field}: 期望 {w}，实际 {g}")

    if "kinds" in want:
        for k, wv in want["kinds"].items():
            w = _kind_record(wv)
            # 同一语义名可能匹配到多个引擎身份（"宝石白" ↔ gem|gem_diamond），合并计
            matched = {gk: gv for gk, gv in got_kinds.items() if kind_matches(k, gk)}
            ga = {}
            for gv in matched.values():
                for f, n in gv.items():
                    if isinstance(n, int):
                        ga[f] = ga.get(f, 0) + n

            if not matched and w.get("count"):
                diffs.append(f"{name}.kinds[{k}]: 期望 {w['count']}，实际 0")
                continue

            for field in ("count", "face_up", "face_down", "shows_face", "shows_back", "hidden"):
                if field in w and ga.get(field, 0) != w[field]:
                    diffs.append(f"{name}.kinds[{k}].{field}: 期望 {w[field]}，实际 {ga.get(field, 0)}")

            # face / shows 简写：期望这一身份的件**全都**是某个状态
            asks_state = "face" in w or "shows" in w
            if asks_state and matched and not any(_kind_state_fields(gv) for gv in matched.values()):
                # 采样里根本没状态字段 = 采样是旧格式。报一条就够，不要接着报一堆
                # "期望 1 实际 0" —— 那是采样的问题，不是画面的问题。
                diffs.append(f"{name}.kinds[{k}]: 契约要求 face/shows，但采样里没有状态字段"
                             f"（采样文件是旧格式，重跑 scripts/dump_states.sh）")
                continue
            if "face" in w and matched and all(_two_sided(gk) is False for gk in matched):
                diffs.append(f"{name}.kinds[{k}].face: 契约断了朝向，但这件没有背图（单面件）"
                             f"—— 这一维对它**不存在**，去掉这条断言（它的身份名/`shows` 已经说清了）")
                continue
            if "face" in w:
                f = _FACE_FIELD.get(w["face"])
                if f is None:
                    diffs.append(f"{name}.kinds[{k}].face: 取值只能是 up/down，实际 {w['face']!r}")
                else:
                    other = "face_down" if f == "face_up" else "face_up"
                    if ga.get(other, 0):
                        want_word = "正面朝上" if f == "face_up" else "背面朝上"
                        diffs.append(f"{name}.kinds[{k}].face: 期望全部{want_word}"
                                     f"（face_{w['face']}），实际 {ga.get('face_up', 0)} 件朝上 / "
                                     f"{ga.get('face_down', 0)} 件朝下")
            if "shows" in w:
                f = _SHOWS_FIELD.get(w["shows"])
                if f is None:
                    diffs.append(f"{name}.kinds[{k}].shows: 取值只能是 face/back/hidden，"
                                 f"实际 {w['shows']!r}")
                elif ga.get(f, 0) != ga.get("count", 0):
                    diffs.append(f"{name}.kinds[{k}].shows: 期望全部显示 {w['shows']}，"
                                 f"实际 {ga.get('shows_face', 0)} 件显真面 / "
                                 f"{ga.get('shows_back', 0)} 件显背面")

        # 反向检查：引擎里有、契约没写 → 说明契约漏了（只在契约写了 kinds 时才查）
        for gk, gv in got_kinds.items():
            if not any(kind_matches(k, gk) for k in want["kinds"]):
                n = gv.get("count", 0)
                if n:
                    diffs.append(f"{name}.kinds: 契约未列出的身份 {gk} 有 {n} 件")
    return diffs


def diff_contract(want_zones, state_zones):
    """契约里声明的那些 zone，逐个和采样状态比。返回差异列表。

    契约**只需声明它关心的 zone**（跟本条无关的宝石盒、贵族等不必写），
    所以「契约没提但引擎里有东西」不算差异 —— 否则每条 cue 都要把整张桌子抄一遍，
    契约会膨胀到没人愿意维护，而没人维护的契约等于没有。
    想查"无关区域有没有被动过"，用不变量（invariants），那才是它的职责。
    """
    diffs = []
    for zname, wz in (want_zones or {}).items():
        diffs += check_zone(zname, wz, (state_zones or {}).get(zname))
    return diffs


# ── 数据装载 ──────────────────────────────────────────────────────────

# ── 取景 vs 契约（用户 2026-09 定的检查）────────────────────────────────
# 状态对账查的是"该在的在不在"，查不出**不该出现在画面里**的东西：
# 一级发展卡出现在宝石介绍的镜头里，状态完全正确、只是"入镜了"。
# 这条检查把两样都对得上的东西拼起来：
#   **脚本**说这一 cue 的相机框住哪（camera）、契约说这一 cue 有哪些组件，
#   **采样**说这一刻实际有什么（每件在哪个 zone、第几位）。
# 取景框内的每一件，如果契约（对应那一面）没提到它的 zone → 就是"没提到的组件入镜了"。
#
# 几何在 scripts/framing_geometry.py（引擎 FitCamera 的镜像，与校验器共用一个来源）。

# 模板 → 有没有背面贴图（= 这件**真的有正反面**）。由 load_stage 填充。
#
# 为什么要它：`face_up/face_down` 只对**有背图**的件成立。垫牌（`blank_card_*`）的
# "正面"就是卡背图、贵族板块只有一面 —— 它们没有"另一面"可翻，`Flipped` 只是个没被用到的
# 默认值。契约去断它，就会得到"期望 3 面朝上、实际 3 面朝下"这种**指向不存在状态**的差异，
# 让人白查半天（2026-09 真发生过两次：贵族、牌堆垫牌）。这条检查让这种断言当场现形。
_TWO_SIDED = None


def set_stage(stage):
    global _TWO_SIDED
    _TWO_SIDED = {t.get("id"): bool(t.get("back_image"))
                  for t in (stage or {}).get("templates", [])}


def _two_sided(kind_key):
    """这个引擎身份（"模板|色板"）真的有正反面吗。不知道 → None（不猜）。"""
    if _TWO_SIDED is None or not kind_key:
        return None
    return _TWO_SIDED.get(str(kind_key).split("|")[0])


def load_stage(args):
    base = ROOT / "games" / args.game / "tutorial" / "anim"
    doc = load(base / f"{args.track}.json")
    rel = (doc or {}).get("stage")
    if not rel:
        return None
    p = base / f"{rel}.json"
    return load(p) if p.exists() else None


def visible_items(stage, state, camera, padding=0.0):
    """这一刻**画面里真的看得见**的组件：取景框内的、且画面上没被隐藏的。

    用采样里的 `shows == "hidden"` 判隐藏（那是引擎自己说的），不另推一套 offstage 规则。
    """
    rect = visible_rect(stage, camera, padding)
    if rect is None:
        return None
    zones = {z.get("id"): z for z in stage.get("zones", [])}
    out = []
    for it in (state or {}).get("items") or []:
        if (it.get("shows") or "").lower() == "hidden":
            continue
        zone = zones.get(it.get("zone"))
        if zone is None or zone.get("role") == "offstage":
            continue
        x, z = _slot_at(stage, zone, int(it.get("order") or 0))
        size = zone.get("size") or {}
        if _in_rect(rect, x, z, float(size.get("w") or 0.2) / 2, float(size.get("h") or 0.2) / 2):
            out.append(it)
    return out


def framing_diffs(stage, cameras, state, declared_zones, label):
    """camera 队列 → 取景里出现了契约没提到的组件。

    cameras = (开头在场的取景, 结尾留下的取景)；都可能是 None（承接上一条）。
    用户定的级别是**警告**：这条说的是"画面里有你没提的东西"，
    处置办法有两个 —— 改取景（离远一点/藏起来），或在契约里把它写上（写上就是真的在比它了）。
    """
    act = cameras[1] if label == "exit" else cameras[0]
    if not act:
        return []
    items = visible_items(stage, state, act[0], act[1])
    if not items:
        return []
    stray = {}
    for it in items:
        if it.get("zone") in (declared_zones or {}):
            continue
        stray.setdefault(it["zone"], []).append(it)
    out = []
    for zone, its in sorted(stray.items()):
        kinds = {}
        for it in its:
            key = it.get("kind") or it.get("concept") or "?"
            kinds[key] = kinds.get(key, 0) + 1
        show = "、".join(f"{k}×{v}" for k, v in sorted(kinds.items()))
        out.append(f"{label} 取景 [{act[0]}] 里有 {zone}: {len(its)} 件（{show}），"
                   f"契约没提到这个 zone —— 要么改取景，要么在契约里声明它")
    return out


def resolve_paths(args):
    base = ROOT / "games" / args.game / "tutorial" / "anim"
    contract = Path(args.script) if args.script else base / f"{args.track}.json"
    states = Path(args.states) if args.states else base / f"{args.track}.exitstate.json"
    return contract, states


def load_contracts(path):
    if not path.exists():
        print(f"没有脚本文件: {path}", file=sys.stderr)
        return None, {}
    doc = load(path)
    return doc, {c["cue"]: c for c in (doc.get("cues") or []) if c.get("cue")}


def load_states(path):
    """采样文件 → {cue: {"zones": {...}}}。

    兼容两种形态：整条 track 的合并文件（cues 是对象），以及单条 cue 的 dump
    （顶层就是 cue/zones，老的单文件采样、或 --state 手给的都算）。
    """
    if path is None:
        return {}
    path = Path(path)
    if not path.exists():
        return {}
    doc = load(path)
    if isinstance(doc.get("cues"), dict):
        return doc["cues"]   # 每条 cue 里可能带 "problems"（引擎自己报的警告/错误）
    if doc.get("cue"):
        return {doc["cue"]: {"zones": doc.get("zones") or {}}}
    return {}


def part_of(contract, which):
    """取契约里 enter / exit 那一整段（含 picture 与 zones）。"""
    return contract.get(which) or {}


def diff_cue(want_part, state):
    """比对一条 cue 的一面（enter / exit）：**整幅图 + 各 zone**。

    整幅图（盒面等）是播放器级状态，2026-09-18 之前既没被采样、也不在契约里，
    于是"背景多出一张盒面"这类问题对账完全看不见（用户报的 cue 10 就是这么漏的）。
    现在它和 zone 状态一样是可比字段。
    """
    diffs = []
    want_part = want_part or {}
    # **整幅图默认比对**：契约里没写 picture 就等于"不该有图"（用户 2026-09-19 定的原则：
    # 该有的有、不该有的就没有；脚本里没写有的就是没有）。
    # 不能只在契约写了才比 —— 那样"忘了写"就会静默跳过这一维，正是上次漏掉盒面的原因。
    if "picture" not in (state or {}):
        diffs.append("picture: 契约要求比对整幅图，但采样里没有这个字段"
                     "（采样文件是旧格式，重跑 scripts/dump_states.sh）")
    else:
        want_pic = want_part.get("picture") or None
        got_pic = (state or {}).get("picture") or None
        if want_pic != got_pic:
            diffs.append(f"picture: 期望 {want_pic!r}，实际 {got_pic!r}"
                         + ("（契约没写 = 不该有图）" if "picture" not in want_part else ""))
    diffs += diff_contract(want_part.get("zones") or {}, (state or {}).get("zones"))
    return diffs


# ── 三种查法 ──────────────────────────────────────────────────────────

def check_single(args):
    cpath, spath = resolve_paths(args)
    _, contracts = load_contracts(cpath)
    set_stage(load_stage(args))
    if not contracts:
        return 2
    contract = contracts.get(args.cue)
    if contract is None:
        print(f"契约里没有这条 cue: {args.cue}", file=sys.stderr)
        return 2

    # --state 给了就单独读它（临时采样），否则用整条 track 的采样文件
    states = load_states(args.state) if args.state else load_states(spath)
    # 查出口 = 和**自己**采样出来的终态比；
    # 查入口 = 和**父 cue** 的终态比（入口本来就该等于父 cue 的出口）——
    # 与 --chain 用的是同一个判据，单条查和整条查不会互相矛盾。
    if args.which == "enter":
        src = contract.get("entry_from")
        if not src:
            print(f"{args.cue} 没有 entry_from，无法查入口", file=sys.stderr)
            return 2
    else:
        src = args.cue
    if src not in states:
        print(f"还没有 {src} 的采样状态（{spath}）", file=sys.stderr)
        return 3

    probs = (states.get(src) or {}).get("problems") or []
    if probs:
        print(f"引擎在 {src} 里报出 {len(probs)} 条问题（画面多半「什么都没发生」）：")
        for pb in probs:
            print(f"  - {pb}")

    diffs = diff_cue(part_of(contract, args.which), states[src])
    print(f"cue: {args.cue}   比对: {args.which}（对 {src} 的采样终态）")
    print("-" * 60)
    if diffs:
        print(f"FAIL  发现 {len(diffs)} 处差异：")
        for d in diffs:
            print(f"  - {d}")
        story = (contract.get("story") or "").strip().splitlines()
        if story:
            print("\n契约里写的意图（用来判断是脚本写错还是动画做错）：")
            for line in story[:6]:
                print(f"  {line}")
        return 1
    n = len(part_of(contract, args.which).get("zones") or {})
    pic = part_of(contract, args.which).get("picture", "（未声明）")
    print(f"PASS  状态与契约一致（{n} 个 zone；整幅图 {pic!r}）")
    return 0


def cue_cameras(events, prev_leave):
    """一条 cue 的取景：**开头**在场的（第一条 camera，没写就承接上一条）与**结尾**留下的。"""
    cams = [(e.get("camera"), float(e.get("camera_padding") or 0.0))
            for e in (events or []) if e.get("camera")]
    if not cams:
        return prev_leave, prev_leave
    return cams[0], cams[-1]


def check_all(args):
    """把整条 track 查一遍：① 每条自己的出口 ② 跨 cue 的链（enter vs 父 exit）③ 取景。"""
    cpath, spath = resolve_paths(args)
    doc, contracts = load_contracts(cpath)
    if doc is None:
        return 2
    states = load_states(spath)
    stage = load_stage(args)
    set_stage(stage)

    fails = skipped = frame_warns = 0
    prev_leave = None
    # 按契约文件里的顺序（= 轨道顺序）走，不按字母序
    for cue in [c["cue"] for c in (doc.get("cues") or []) if c.get("cue")]:
        contract = contracts[cue]
        first_cam, leave_cam = cue_cameras(contract.get("events"), prev_leave)

        # ⓪ 引擎自己在**这条 cue 里**报的警告/错误。
        #    "脚本要求的事没发生"（例如 highlight 点了一个已经被搬走的位置）状态是变不了的
        #    —— 对账比状态永远看不见，只有引擎知道。所以它随采样一起交出来，在这里报。
        probs = (states.get(cue) or {}).get("problems") or []
        if probs:
            print(f"FAIL  {cue}  引擎报出 {len(probs)} 条问题（画面多半「什么都没发生」）：")
            for pb in probs:
                print(f"        - {pb}")
            fails += 1

        # ① 自己的出口
        if cue in states:
            want = part_of(contract, "exit")
            diffs = diff_cue(want, states[cue])
            if diffs:
                print(f"FAIL  {cue}  exit:")
                for d in diffs:
                    print(f"        - {d}")
                fails += 1
            else:
                print(f"PASS  {cue}  exit（{len(want.get('zones') or {})} 个 zone）")
            if stage:
                fd = framing_diffs(stage, (first_cam, leave_cam), states[cue],
                                   want.get("zones") or {}, "exit")
                if fd:
                    loud = getattr(args, "strict_framing", False)
                    print(f"{'FAIL' if loud else 'WARN'}  {cue}  exit 取景:")
                    for d in fd:
                        print(f"        - {d}")
                    if loud:
                        fails += 1
                    frame_warns += 1
        else:
            print(f"SKIP  {cue} exit（还没采样）")
            skipped += 1

        # ② 跨 cue：enter vs 父 exit
        parent = contract.get("entry_from")
        if parent:
            if parent in states:
                want = part_of(contract, "enter")
                diffs = diff_cue(want, states[parent])
                if diffs:
                    print(f"FAIL  {cue}  enter vs 父({parent}) exit:")
                    for d in diffs:
                        print(f"        - {d}")
                    fails += 1
                else:
                    print(f"PASS  {cue}  enter == 父({parent}) 的终态")
                if stage:
                    fd = framing_diffs(stage, (first_cam, leave_cam), states[parent],
                                       want.get("zones") or {}, "enter")
                    if fd:
                        loud = getattr(args, "strict_framing", False)
                        print(f"{'FAIL' if loud else 'WARN'}  {cue}  enter 取景（入口画面）:")
                        for d in fd:
                            print(f"        - {d}")
                        if loud:
                            fails += 1
                        frame_warns += 1
            else:
                print(f"SKIP  {cue} enter（父 cue 还没采样）")

        prev_leave = leave_cam

    print("-" * 60)
    print(f"{'FAIL' if fails else 'PASS'}  {fails} 处不一致"
          + (f"，{skipped} 条待采样" if skipped else "")
          + (f"，{frame_warns} 条取景警告（画面里有契约没提到的组件；--strict-framing 可当作错误）"
             if frame_warns else ""))
    return 1 if fails else 0


def chain(args):
    """跨 cue 对账：本 cue 的 enter 应当等于父 cue 的 exit。

    这是「头尾都检查」的关键一步 —— 只查自己这条，看不出**上一条**错没错。
    采样父 cue 的真实终态，和本 cue 声明的 enter 比一遍，父 cue 错得离谱就会暴露。
    """
    cpath, spath = resolve_paths(args)
    doc, contracts = load_contracts(cpath)
    if doc is None:
        return 2
    set_stage(load_stage(args))
    states = load_states(spath)

    fails = 0
    for cue in [c["cue"] for c in (doc.get("cues") or []) if c.get("cue")]:
        contract = contracts[cue]
        parent = contract.get("entry_from")
        if not parent:
            continue
        if parent not in contracts:
            print(f"FAIL  {cue}: 契约里找不到父 cue {parent}")
            fails += 1
            continue
        if parent not in states:
            print(f"SKIP  {cue}: 还没有父 cue 的采样状态（{parent}）")
            continue
        diffs = diff_cue(part_of(contract, "enter"), states[parent])
        if diffs:
            print(f"FAIL  {cue}: 入口与父 cue({parent}) 的终态不一致：")
            for d in diffs:
                print(f"        - {d}")
            fails += 1
        else:
            print(f"PASS  {cue}: 入口 == 父 cue({parent}) 的终态")
    return 1 if fails else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="full")
    ap.add_argument("--cue", default=None)
    ap.add_argument("--script", help="动画脚本文件（默认 games/{game}/tutorial/anim/{track}.json）")
    ap.add_argument("--states", help="采样文件（默认 .../anim/{track}.exitstate.json）")
    ap.add_argument("--state", help="单条 cue 的采样文件（临时查用，覆盖 --states）")
    ap.add_argument("--which", default="exit", choices=["exit", "enter"])
    ap.add_argument("-v", "--verbose", action="store_true", help="（保留）列出被忽略的 zone")
    ap.add_argument("--chain", action="store_true", help="跨 cue 对账：本 cue 的 enter vs 父 cue 的 exit")
    ap.add_argument("--all", action="store_true", help="整条 track 全查：自己的 exit + 跨 cue 的链")
    ap.add_argument("--strict-framing", action="store_true",
                    help="把取景警告当成错误（默认只警告：入镜的东西不一定是错的）")
    args = ap.parse_args()
    if args.chain:
        return chain(args)
    if args.all:
        return check_all(args)
    if not args.cue:
        ap.error("要么给 --cue，要么用 --all / --chain")
    return check_single(args)


if __name__ == "__main__":
    sys.exit(main())
