#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""**取景连续性检查** —— 补上"镜头怎么走"这一类（用户 2026-09-21 问："为什么没检查出来"）。

起因：`setup.cards.002.1`（三摞牌库+市场特写）之后紧跟的 `setup.cards.002.2` 退回整桌全局 ✗，
我四道检查全绿却没人报 —— 因为它们都是**状态**口径（状态/规则/契约 vs 采样/入镜是否声明），
**没有一条在看"镜头从哪切到哪"** ✗。这个脚本就管这件事。

口径（用户 2026-09-21 修正）：**取景连续性不是硬规则** —— 上一 cue 讲牌、这一 cue 讲宝石，
组件和镜头本来就该突变 ✓。所以只报一种情况：

  · **同主体来回跳**：特写 → 整桌 → 又回到**同一个**特写（连着三条）→ 几乎总是漏写取景 ✗

（"特写后这一条什么都没动却退全局"不再报警：那可能是作者特意回整桌看全局 ✓，
以后镜头会默认延续上一条、只有显式写 camera 才改，这类情况自然消失。）
用法：python3 scripts/check_framing_flow.py [--strict]   （--strict 时当作错误）
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ANIM = ROOT / "games/splendor/tutorial/anim/full.json"
RT = ROOT / "games/splendor/tutorial/full.runtime.json"
TOKENS = {"board", "supply", "cards"}


def frame_of(cue):
    for e in cue.get("events") or []:
        c = e.get("camera")
        if c:
            return c
    return None


def touched(cue):
    n = 0
    for e in cue.get("events") or []:
        if e.get("action") in ("transfer", "create", "destroy", "stack", "zone"):
            n += 1
    return n


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict", action="store_true")
    a = ap.parse_args()
    doc = json.loads(ANIM.read_text(encoding="utf-8"))
    order = [c["id"] for c in json.loads(RT.read_text(encoding="utf-8"))["cues"]]
    by = {c["cue"]: c for c in doc["cues"]}
    # 跨树是 cut，取景链不跨树 —— 把每棵树分开看。
    seq = []
    for i in order:
        c = by.get(i)
        if c and frame_of(c):
            seq.append((c.get("tree") or "main", c))
    warns = []
    for k in range(1, len(seq) - 1):
        t0, prev = seq[k - 1]
        t1, cur = seq[k]
        t2, nxt = seq[k + 1]
        if not (t0 == t1 == t2):
            continue
        pf, cf = frame_of(prev), frame_of(cur)
        prev_close = pf not in TOKENS and "," not in pf or ("," in pf)
        cur_global = cf == "board"
        if prev_close and cur_global and frame_of(nxt) == pf:
            warns.append(f"{cur['cue']}：特写 {pf} → 整桌 → 又回到同一个特写（跳切）")
    tag = "ERR " if a.strict else "WARN"
    for w in warns:
        print(f"{tag} {w}")
    print(f"\n{len(warns)} 处取景连续性问题（共 {len(seq)} 条有取景的 cue）")
    return 1 if (warns and a.strict) else 0


if __name__ == "__main__":
    sys.exit(main())
