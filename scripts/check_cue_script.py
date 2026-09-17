#!/usr/bin/env python3
"""对账：把「引擎采样出来的状态」和「脚本里写的契约」比一遍。

用法：
    python3 scripts/check_cue_script.py --game splendor --cue setup.cards.002.1 \
        --state client/CaptureOut/state_cue12.json [--which exit]

为什么要这么做：
    以前判断"这一 cue 演得对不对"靠**看渲染出来的像素**，而像素会骗人
    （采样坐标写错、读到旧帧、离屏渲染里某些效果根本不发生）。
    而"谁在哪个 zone、几件、朝上还是朝下"是**数据**，可以用程序精确比对。

    契约（script/full/<cue>.json）由人写，状态由引擎采样，两者结构相同 → 可 diff。
    出现差异时，配合契约里的 story 就能判断**是我脚本写错了，还是动画做错了**。
"""
import argparse, json, sys
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


args_verbose = False


def main():
    global args_verbose
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--cue", default=None)
    ap.add_argument("--state", help="引擎采样出来的状态 JSON（--chain 模式可省）")
    ap.add_argument("--which", default="exit", choices=["exit", "enter"])
    ap.add_argument("--script-dir", default=None)
    ap.add_argument("-v", "--verbose", action="store_true", help="同时列出被忽略的 zone")
    ap.add_argument("--chain", action="store_true", help="跨 cue 对账：本 cue 的 enter vs 父 cue 的 exit")
    args = ap.parse_args()
    args_verbose = args.verbose
    if args.chain:
        return chain(args)

    sdir = Path(args.script_dir) if args.script_dir else ROOT / "games" / args.game / "tutorial" / "script" / "full"
    cpath = sdir / f"{args.cue}.json"
    if not cpath.exists():
        print(f"没有这条 cue 的脚本: {cpath}", file=sys.stderr)
        return 2

    contract = load(cpath)
    state = load(args.state)

    want = (contract.get(args.which) or {}).get("zones") or {}
    got = state.get("zones") or {}

    diffs = []
    for zname, wz in want.items():
        diffs += check_zone(zname, wz, got.get(zname))

    # 契约**只需声明它关心的 zone**（跟本条无关的宝石盒、贵族等不必写）。
    # 所以「契约没提但引擎里有东西」不算差异 —— 否则每条 cue 都要把整张桌子抄一遍，
    # 契约会膨胀到没人愿意维护，而没人维护的契约等于没有。
    # 想查"无关区域有没有被动过"，用不变量（invariants），那才是它的职责。
    ignored = [z for z in got if z not in want and got[z].get("count")]
    if ignored and args_verbose:
        print(f"（忽略未声明的 zone：{', '.join(sorted(ignored))}）")

    print(f"cue: {args.cue}   比对: {args.which}")
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

    print(f"PASS  状态与契约一致（比对了 {len(want)} 个 zone）")
    return 0


def chain(args):
    """跨 cue 对账：本 cue 的 enter 应当等于父 cue 的 exit。

    这是「头尾都检查」的关键一步 —— 只查自己这条，看不出**上一条**错没错。
    采样父 cue 的真实终态，和本 cue 声明的 enter 比一遍，父 cue 错得离谱就会暴露。
    """
    sdir = Path(args.script_dir) if args.script_dir else ROOT / "games" / args.game / "tutorial" / "script" / "full"
    sdir = Path(sdir)
    fails = 0
    for cpath in sorted(sdir.glob("*.json")):
        contract = load(cpath)
        parent = contract.get("entry_from")
        if not parent:
            continue
        ppath = sdir / f"{parent}.json"
        if not ppath.exists():
            print(f"FAIL  {contract['cue']}: 找不到父 cue 的脚本 {parent}")
            fails += 1
            continue
        pstate = sdir / f"{parent}.exitstate.json"
        if not pstate.exists():
            print(f"SKIP  {contract['cue']}: 还没有父 cue 的采样状态（{pstate.name}）")
            continue
        want = (contract.get("enter") or {}).get("zones") or {}
        got = (load(pstate).get("zones") or {})
        diffs = []
        for zname, wz in want.items():
            diffs += check_zone(zname, wz, got.get(zname))
        if diffs:
            print(f"FAIL  {contract['cue']}: 入口与父 cue({parent}) 的终态不一致：")
            for d in diffs:
                print(f"        - {d}")
            fails += 1
        else:
            print(f"PASS  {contract['cue']}: 入口 == 父 cue({parent}) 的终态")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
