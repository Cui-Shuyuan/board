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

数据在哪：
    games/{game}/tutorial/script/{track}.json            契约 —— **人写**，整条 track 一个文件
    games/{game}/tutorial/script/{track}.exitstate.json  采样终态 —— 引擎生成，别手改
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
    # 兜底：当作模板 id 前缀
    return want_key == tmpl


def check_zone(name, want, got):
    """比对单个 zone，返回差异列表。"""
    diffs = []
    got = got or {}

    for field in ("count", "face_up", "face_down"):
        if field in want:
            w, g = want[field], got.get(field, 0)
            if w != g:
                diffs.append(f"{name}.{field}: 期望 {w}，实际 {g}")

    if "kinds" in want:
        got_kinds = got.get("kinds", {})
        for k, w in want["kinds"].items():
            g = sum(v for gk, v in got_kinds.items() if kind_matches(k, gk))
            if g != w:
                diffs.append(f"{name}.kinds[{k}]: 期望 {w}，实际 {g}")
        # 反向检查：引擎里有、契约没写 → 说明契约漏了（只在契约写了 kinds 时才查）
        for gk, gv in got_kinds.items():
            if not any(kind_matches(k, gk) for k in want["kinds"]):
                if gv:
                    diffs.append(f"{name}.kinds: 契约未列出的身份 {gk} 有 {gv} 件")
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

def resolve_paths(args):
    base = ROOT / "games" / args.game / "tutorial" / "script"
    contract = Path(args.script) if args.script else base / f"{args.track}.json"
    states = Path(args.states) if args.states else base / f"{args.track}.exitstate.json"
    return contract, states


def load_contracts(path):
    if not path.exists():
        print(f"没有契约文件: {path}", file=sys.stderr)
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
        return doc["cues"]
    if doc.get("cue"):
        return {doc["cue"]: {"zones": doc.get("zones") or {}}}
    return {}


def zones_of(contract, which):
    """取契约里 enter / exit 那一段。"""
    return (contract.get(which) or {}).get("zones") or {}


# ── 三种查法 ──────────────────────────────────────────────────────────

def check_single(args):
    cpath, spath = resolve_paths(args)
    _, contracts = load_contracts(cpath)
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

    diffs = diff_contract(zones_of(contract, args.which), states[src]["zones"])
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
    print(f"PASS  状态与契约一致（比对了 {len(zones_of(contract, args.which))} 个 zone）")
    return 0


def check_all(args):
    """把整条 track 查一遍：① 每条自己的出口 ② 跨 cue 的链（enter vs 父 exit）。"""
    cpath, spath = resolve_paths(args)
    doc, contracts = load_contracts(cpath)
    if doc is None:
        return 2
    states = load_states(spath)

    fails = skipped = 0
    # 按契约文件里的顺序（= 轨道顺序）走，不按字母序
    for cue in [c["cue"] for c in (doc.get("cues") or []) if c.get("cue")]:
        contract = contracts[cue]

        # ① 自己的出口
        if cue in states:
            want = zones_of(contract, "exit")
            diffs = diff_contract(want, states[cue]["zones"])
            if diffs:
                print(f"FAIL  {cue}  exit:")
                for d in diffs:
                    print(f"        - {d}")
                fails += 1
            else:
                print(f"PASS  {cue}  exit（{len(want)} 个 zone）")
        else:
            print(f"SKIP  {cue} exit（还没采样）")
            skipped += 1

        # ② 跨 cue：enter vs 父 exit
        parent = contract.get("entry_from")
        if parent:
            if parent in states:
                want = zones_of(contract, "enter")
                diffs = diff_contract(want, states[parent]["zones"])
                if diffs:
                    print(f"FAIL  {cue}  enter vs 父({parent}) exit:")
                    for d in diffs:
                        print(f"        - {d}")
                    fails += 1
                else:
                    print(f"PASS  {cue}  enter == 父({parent}) 的终态")
            else:
                print(f"SKIP  {cue} enter（父 cue 还没采样）")

    print("-" * 60)
    print(f"{'FAIL' if fails else 'PASS'}  {fails} 处不一致" + (f"，{skipped} 条待采样" if skipped else ""))
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
        diffs = diff_contract(zones_of(contract, "enter"), states[parent]["zones"])
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
    ap.add_argument("--script", help="契约文件（默认 games/{game}/tutorial/script/{track}.json）")
    ap.add_argument("--states", help="采样文件（默认 .../{track}.exitstate.json）")
    ap.add_argument("--state", help="单条 cue 的采样文件（临时查用，覆盖 --states）")
    ap.add_argument("--which", default="exit", choices=["exit", "enter"])
    ap.add_argument("-v", "--verbose", action="store_true", help="（保留）列出被忽略的 zone")
    ap.add_argument("--chain", action="store_true", help="跨 cue 对账：本 cue 的 enter vs 父 cue 的 exit")
    ap.add_argument("--all", action="store_true", help="整条 track 全查：自己的 exit + 跨 cue 的链")
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
