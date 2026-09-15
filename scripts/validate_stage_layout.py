#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Check the table layout in a stage file for problems that are invisible in JSON.

Why this exists
---------------
Framing and overlap mistakes in the stage file are cheap to catch here and
expensive to catch from a screenshot.  A single wrong `layout.cols` once made
the camera fit a fictional box and shrank the whole table.

Checks
------
* every zone has a sane layout (cols >= 1, non-negative steps)
* zone bounding boxes do not overlap (cards / piles colliding is silent in Unity)
* the content of every zone fits inside board.extent
* board.extent exists and is consistent with the zones (within a tolerance)
* offstage zones are excluded from the frame

Usage
-----
    python scripts/validate_stage_layout.py --game splendor
    python scripts/validate_stage_layout.py --file games/splendor/tutorial/anim/_stage/splendor.table.json

Exit codes: 0 = ok (warnings allowed), 1 = errors, 2 = file not found.
"""

import argparse
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 卡牌按最大件估算（Splendor 发展卡 63x88mm）；其他件都很小，用 pad 覆盖。
CARD_HALF_W = 0.315
CARD_HALF_H = 0.44


def load_json(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def zone_box(zone):
    """zone 的世界包围盒 (min_x, max_x, min_z, max_z) 与是否用卡牌尺寸估算。"""
    layout = zone.get("layout") or {}
    cols = max(1, int(layout.get("cols", 1)))
    capacity = int(zone.get("capacity", 1)) or 1
    x_step = float(layout.get("x_step", 0.09))
    z_step = float(layout.get("z_step", 0.09))
    typ = layout.get("type", "pile")

    rows = max(1, math.ceil(capacity / cols))
    half_w = max(0.05, (cols - 1) * 0.5 * x_step)
    half_h = max(0.05, (rows - 1) * 0.5 * z_step)

    # row 是单行；grid 的纵向跨度由行数决定；pile 是小堆
    if typ == "row":
        half_h = 0.05
    cards = typ == "grid"
    if cards:
        half_w = max(half_w, CARD_HALF_W)
        half_h = max(half_h, CARD_HALF_H)
    else:
        half_w += 0.07
        half_h += 0.07

    cx = zone["center"]["x"]
    cz = zone["center"]["z"]
    return (cx - half_w, cx + half_w, cz - half_h, cz + half_h), cards


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--file", help="直接指定 stage 文件")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--tolerance", type=float, default=0.30,
                        help="extent 与 zone 包围盒允许的差值（默认 0.30）")
    args = parser.parse_args()

    if args.file:
        path = Path(args.file)
    else:
        path = ROOT / "games" / args.game / "tutorial" / "anim" / "_stage" / f"{args.game}.table.json"
    if not path.exists():
        print(f"stage 文件不存在: {path}", file=sys.stderr)
        return 2

    stage = load_json(path)
    errors, warnings = [], []

    def err(msg):
        errors.append(msg)

    def warn(msg):
        warnings.append(msg)

    zones = [z for z in stage.get("zones", []) if z.get("id")]
    onstage = [z for z in zones if (z.get("role") or "zone") != "offstage"]

    # 1) layout 合法性
    #
    # 重要语义：row 布局是**单行**，只按 capacity 和 x_step 排；cols 对 row 无效。
    # 一行的物理长度 = (capacity-1)*x_step，与 cols 无关。所以 row 写 cols 是误导，
    # 会让人以为它影响排布，进而推错纵向尺寸。
    for z in zones:
        layout = z.get("layout") or {}
        cols = int(layout.get("cols", 1))
        typ = layout.get("type", "pile")
        if cols < 1:
            err(f"zone {z['id']}: layout.cols 必须 >= 1（当前 {cols}）")
        if typ == "row":
            if "cols" in layout:
                err(f"zone {z['id']}: row 布局是单行，不该写 cols（它只影响 grid/pile，"
                    f"写在这里会让人误以为 row 会换行）")
            if float(layout.get("z_step", 0)) != 0:
                warn(f"zone {z['id']}: row 布局是单行，z_step 无意义")
        for key in ("x_step", "z_step"):
            if float(layout.get(key, 0)) < 0:
                err(f"zone {z['id']}: {key} 不能为负")
        if "center" not in z:
            err(f"zone {z['id']}: 缺少 center")

    # 2) 重叠
    boxes = [(z["id"], *zone_box(z)[0]) for z in onstage if "center" in z]
    for i in range(len(boxes)):
        for j in range(i + 1, len(boxes)):
            a_id, ax0, ax1, az0, az1 = boxes[i]
            b_id, bx0, bx1, bz0, bz1 = boxes[j]
            if ax0 < bx1 and bx0 < ax1 and az0 < bz1 and bz0 < az1:
                ox = min(ax1, bx1) - max(ax0, bx0)
                oz = min(az1, bz1) - max(az0, bz0)
                warn(f"zone {a_id} 与 {b_id} 的包围盒重叠 {ox:.2f}x{oz:.2f}（可能视觉打架）")

    # 3) extent 覆盖所有 onstage 内容
    board = stage.get("board") or {}
    extent = board.get("extent")
    if not extent:
        err("board.extent 缺失：相机取景的唯一依据，必须显式声明")
    else:
        ex0, ex1 = float(extent["min_x"]), float(extent["max_x"])
        ez0, ez1 = float(extent["min_z"]), float(extent["max_z"])
        if ex1 <= ex0 or ez1 <= ez0:
            err("board.extent 的 max 必须大于 min")
        else:
            need_x0 = min(b[1] for b in boxes)
            need_x1 = max(b[2] for b in boxes)
            need_z0 = min(b[3] for b in boxes)
            need_z1 = max(b[4] for b in boxes)
            for label, ext_lo, ext_hi, need_lo, need_hi in (
                ("x", ex0, ex1, need_x0, need_x1), ("z", ez0, ez1, need_z0, need_z1)
            ):
                if need_lo < ext_lo - args.tolerance:
                    err(f"内容 {label} 下界 {need_lo:.2f} 超出 extent {ext_lo:.2f}")
                if need_hi > ext_hi + args.tolerance:
                    err(f"内容 {label} 上界 {need_hi:.2f} 超出 extent {ext_hi:.2f}")
                slack = min(need_lo - ext_lo, ext_hi - need_hi)
                if slack > args.tolerance * 2:
                    warn(f"extent 在 {label} 方向留白偏大（{slack:.2f}），物件会显得偏小")

            # 4) extent 是否明显偏离实际内容（说明布局改过但 extent 没更新）
            if abs(need_x0 - ex0) > args.tolerance and abs(need_x1 - ex1) > args.tolerance:
                warn(f"extent x[{ex0:.2f},{ex1:.2f}] 与实际内容 x[{need_x0:.2f},{need_x1:.2f}] 偏差较大，可能忘了同步")

    if args.json:
        print(json.dumps({"ok": not errors, "file": str(path), "errors": errors, "warnings": warnings},
                         ensure_ascii=False, indent=2))
    else:
        print(f"{'OK ' if not errors else 'ERR'} {path.name}"
              + (f"（{len(warnings)} warning）" if warnings else ""))
        for e in errors:
            print(f"    error  {e}")
        for w in warnings:
            print(f"    warn   {w}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
