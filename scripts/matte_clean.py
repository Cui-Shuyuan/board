#!/usr/bin/env python3
"""抠图收边：把**任何来源**的 alpha（模型/API/几何）压成"实体件的干净剪影"。

为什么需要这一步（2026-09 实测出来的）：
  BiRefNet 在白宝石上很准（实心度 0.792 ≈ π/4），但
    · 红宝石把**阴影**一起抠进来了（bbox 508px，真值≈266）→ 软边 9px；
    · 贵族板块留下了台面（四角 α=0.79）；
    · 铺满画面的发展卡直接切出空 alpha。
  通用抠图模型学的是"自然照片里的显著物体"，平扫件（尤其是铺满画面的卡牌、带影子的圆片）
  不在它的舒适区，而且**它没有"实物多大/什么形状"的常识**。

所以分工是：**模型负责"物体大概在哪"，这一步负责把它变成实物剪影**：
  1. α ≥ 阈值 二值化（阴影是 0<α<0.5 的软环，**一刀就掉**）
  2. 取最大连通域（灰尘、边框残留、模型漏出的碎块）
  3. 填掉小孔洞；**大孔洞保留**（异形件可能真有镂空），并报出来
  4. 1px 抗锯齿（3×3 平均）——实体件不该有糊边
  5. 输出与原图同尺寸的 PNG（位置不变，便于和原始扫描件对照）

用法：
    python3 scripts/matte_clean.py --dir /tmp/matte_out --out /tmp/matte_clean
    python3 scripts/matte_clean.py --dir in --out out --threshold 0.5 --min-hole 0.005
"""
from __future__ import annotations

import argparse
import sys
from collections import deque
from pathlib import Path


def largest_component(mask: list[bool], w: int, h: int) -> list[bool]:
    seen = [False] * (w * h)
    best: list[int] = []
    for start in range(w * h):
        if not mask[start] or seen[start]:
            continue
        comp: list[int] = []
        q = deque([start])
        seen[start] = True
        while q:
            i = q.popleft()
            comp.append(i)
            x, y = i % w, i // w
            for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if 0 <= nx < w and 0 <= ny < h:
                    j = ny * w + nx
                    if mask[j] and not seen[j]:
                        seen[j] = True
                        q.append(j)
        if len(comp) > len(best):
            best = comp
    out = [False] * (w * h)
    for i in best:
        out[i] = True
    return out


def fill_small_holes(mask: list[bool], w: int, h: int, min_hole_ratio: float) -> tuple[list[bool], int, int]:
    """填掉小于 min_hole_ratio×实体面积 的孔洞；返回 (mask, 填了几个, 保留了几个大孔)。"""
    holes: list[list[int]] = []
    seen = [False] * (w * h)
    for start in range(w * h):
        if mask[start] or seen[start]:
            continue
        comp: list[int] = []
        q = deque([start])
        seen[start] = True
        touches_border = False
        while q:
            i = q.popleft()
            comp.append(i)
            x, y = i % w, i // w
            if x == 0 or y == 0 or x == w - 1 or y == h - 1:
                touches_border = True
            for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if 0 <= nx < w and 0 <= ny < h:
                    j = ny * w + nx
                    if not mask[j] and not seen[j]:
                        seen[j] = True
                        q.append(j)
        if not touches_border:
            holes.append(comp)

    area = sum(mask)
    limit = min_hole_ratio * max(1, area)
    filled = kept = 0
    for comp in holes:
        if len(comp) < limit:
            for i in comp:
                mask[i] = True
            filled += 1
        else:
            kept += 1
    return mask, filled, kept


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--threshold", type=float, default=0.5)
    ap.add_argument("--min-hole", type=float, default=0.005, help="小于实体面积这个比例的孔洞会填掉")
    args = ap.parse_args()

    from PIL import Image

    src_dir, out_dir = Path(args.dir), Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    files = sorted(p for p in src_dir.glob("*.png") if not p.name.endswith(".preview.png"))
    if not files:
        print(f"没找到 PNG：{src_dir}", file=sys.stderr)
        return 2

    print(f"收边 {len(files)} 个（阈值 α≥{args.threshold}，孔洞阈值 {args.min_hole:.1%}）")
    print("-" * 96)
    for f in files:
        im = Image.open(f).convert("RGBA")
        w, h = im.size
        px = list(im.getdata())
        alpha = [p[3] / 255.0 for p in px]

        mask = [a >= args.threshold for a in alpha]
        before = sum(mask)
        if before == 0:
            print(f"FAIL {f.name:28} 阈值化后没有任何不透明像素（模型没找到主体？）")
            continue
        mask = largest_component(mask, w, h)
        after_cc = sum(mask)
        mask, filled, kept = fill_small_holes(mask, w, h, args.min_hole)

        # 1px 抗锯齿：3×3 平均（实体件要硬边，但留 1px 过渡）
        soft = [0.0] * (w * h)
        for y in range(h):
            for x in range(w):
                s = 0.0
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        nx, ny = x + dx, y + dy
                        if 0 <= nx < w and 0 <= ny < h and mask[ny * w + nx]:
                            s += 1.0
                soft[y * w + x] = s / 9.0

        out = Image.new("RGBA", (w, h))
        out.putdata([(px[i][0], px[i][1], px[i][2], int(round(255 * min(1.0, soft[i] * 1.6)))) for i in range(w * h)])
        dst = out_dir / f.name
        out.save(dst)

        print(f"OK   {f.name:28} 不透明 {before}→{after_cc}"
              f"（丢碎块 {before - after_cc}）；空洞填 {filled} 留 {kept} → {dst.name}")

    print("-" * 96)
    print(f"输出目录 {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
