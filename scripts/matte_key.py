#!/usr/bin/env python3
"""白底键控：给**生成式**产出的图（模型不输出透明通道）切出 alpha。

为什么不用"亮度阈值一把切"：白色的件（白宝石、卡背的白底图案）会被掏空。
所以从**图像边界泛洪**：只有和边界连通的"近白"像素才算背景。
件内部的白色区域与边界不连通 → 原样保留 ✓。

流程：边界泛洪出背景掩码 → alpha = 0/1 → 可选收边（最大连通域 + 1px 抗锯齿）
      → 写 PNG（与输入同尺寸）。

用法：
    python3 scripts/matte_key.py --dir /tmp/matte_gen --out /tmp/matte_key
    python3 scripts/matte_key.py --dir in --out out --white 235 --band 24
"""
from __future__ import annotations

import argparse
import sys
from collections import deque
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--white", type=int, default=228,
                    help="最小通道 ≥ 它 视为'可能是背景白'")
    ap.add_argument("--band", type=int, default=22,
                    help="边缘过渡带宽度（最小通道从 white-band 到 white 线性过渡）")
    args = ap.parse_args()

    from PIL import Image

    src_dir, out_dir = Path(args.dir), Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    files = sorted(p for p in src_dir.glob("*.png") if not p.name.endswith(".preview.png"))
    if not files:
        print(f"没找到 PNG：{src_dir}", file=sys.stderr)
        return 2

    print(f"白底键控 {len(files)} 个（白阈值 {args.white}，过渡带 {args.band}）")
    print("-" * 92)
    for f in files:
        im = Image.open(f).convert("RGBA")
        w, h = im.size
        px = list(im.getdata())
        minc = [min(p[0], p[1], p[2]) for p in px]

        def near_white(i: int) -> bool:
            return minc[i] >= args.white - args.band

        # 从边界泛洪：只有连到边界的近白像素才是背景
        is_bg = [False] * (w * h)
        q: deque[int] = deque()
        for x in range(w):
            for y in (0, h - 1):
                i = y * w + x
                if near_white(i) and not is_bg[i]:
                    is_bg[i] = True
                    q.append(i)
        for y in range(h):
            for x in (0, w - 1):
                i = y * w + x
                if near_white(i) and not is_bg[i]:
                    is_bg[i] = True
                    q.append(i)
        while q:
            i = q.popleft()
            x, y = i % w, i // w
            for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if 0 <= nx < w and 0 <= ny < h:
                    j = ny * w + nx
                    if not is_bg[j] and near_white(j):
                        is_bg[j] = True
                        q.append(j)

        n_bg = sum(is_bg)
        # alpha：背景 0；其余 1，边界 1px 过渡（用 3×3 平均做抗锯齿）
        solid = [not b for b in is_bg]
        out_px = []
        for y in range(h):
            for x in range(w):
                i = y * w + x
                if is_bg[i]:
                    a = 0.0
                else:
                    s = 0.0
                    for dy in (-1, 0, 1):
                        for dx in (-1, 0, 1):
                            nx, ny = x + dx, y + dy
                            if 0 <= nx < w and 0 <= ny < h and solid[ny * w + nx]:
                                s += 1.0
                    a = min(1.0, s / 9.0 * 1.6)
                p = px[i]
                out_px.append((p[0], p[1], p[2], int(round(255 * a))))

        out = Image.new("RGBA", (w, h))
        out.putdata(out_px)
        out.save(out_dir / f.name)
        print(f"OK   {f.name:26} {w}x{h} 背景像素 {n_bg}（{100 * n_bg / (w * h):.1f}%）→ {f.name}")

    print("-" * 92)
    print(f"输出目录 {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
