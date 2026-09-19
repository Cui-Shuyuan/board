#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 15 张发展卡面上的**事实**读出来：折扣宝石（右上）、声望值（左上横幅）、价格（左下圆盘）。

这些是**卡牌事实**，购买演示（3.4）与结算（第 4 节）都要用，所以一次性抽成数据文件：
  games/splendor/media/card_facts.json

做法（确定性，不靠肉眼）：
  1. 圆盘检测：左下区域里"高饱和 + 近似圆形"的连通域 → 每个圆盘一个；
  2. 圆盘颜色 → 用色相分类到 白/蓝/绿/红/黑（白宝石是低饱和的银白，单独判）；
  3. 圆盘里的数字 → **裁出来放大给人读**（出 montage 图，数字太小机器读不可靠）；
  4. 声望横幅 → 同样裁出来给人读。
数字这一维由人（我）读图后写回 json —— 这一步不猜。
"""
import json
from pathlib import Path
import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path('/home/cui/workspace/board')
WIN = Path('/mnt/d/workspace/board')
CARD = WIN / 'games/splendor/media/card'
LV = {1: '一', 2: '二', 3: '三'}
COLORS = ['白', '蓝', '绿', '红', '黑']
HUES = {  # 色相区间（度）→ 宝石色
    '红': (345, 20), '绿': (80, 170), '蓝': (185, 260),
}


def hue_class(rgb):
    r, g, b = [v / 255.0 for v in rgb]
    mx, mn = max(r, g, b), min(r, g, b)
    sat = 0.0 if mx <= 1e-6 else (mx - mn) / mx
    if sat < 0.13 and mx > 0.55:
        return '白'          # 银白钻石：低饱和但很亮
    if sat < 0.18:
        return '黑'          # 黑玛瑙：低饱和且暗
    import colorsys
    h = colorsys.rgb_to_hsv(r, g, b)[0] * 360
    for name, (lo, hi) in HUES.items():
        if lo <= h <= hi or (lo > hi and (h >= lo or h <= hi)):
            return name
    return '?'


def comp(im):
    bg = Image.new('RGB', im.size, (30, 33, 38))
    bg.paste(im, mask=im.split()[3])
    return bg


def find_discs(arr):
    """在左下的价格区域里找圆盘：高饱和块 + 圆形度过滤。返回 [(cx,cy,r,color)]。"""
    h, w, _ = arr.shape
    sub = arr[int(h * 0.30):, :int(w * 0.55)]
    f = sub.astype(np.float32)
    mx = f.max(axis=2); mn = f.min(axis=2)
    sat = np.where(mx <= 1, 0, (mx - mn) / np.maximum(mx, 1))
    mask = sat > 0.30
    mask = ndimage.binary_opening(mask, np.ones((5, 5)))
    mask = ndimage.binary_fill_holes(mask)
    lab, n = ndimage.label(mask)
    out = []
    for i in range(1, n + 1):
        ys, xs = np.where(lab == i)
        if len(xs) < 900:
            continue
        cx, cy = xs.mean(), ys.mean()
        bw, bh = xs.max() - xs.min() + 1, ys.max() - ys.min() + 1
        fill = len(xs) / (bw * bh)
        if not (0.6 < bw / max(bh, 1) < 1.7) or fill < 0.6:
            continue
        rgb = sub[ys, xs].mean(axis=0)
        out.append((float(cx), float(cy + int(h * 0.30)), float((bw + bh) / 4),
                    hue_class(rgb), rgb.round(0).tolist()))
    out.sort(key=lambda d: d[1])
    return out


def main():
    facts = {}
    tiles = []
    for lv in (1, 2, 3):
        for cn in COLORS:
            f = CARD / f'{LV[lv]}级发展卡_{cn}_cutout.png'
            im = Image.open(f).convert('RGBA')
            arr = np.asarray(comp(im))
            h, w, _ = arr.shape
            discs = find_discs(arr)
            # 折扣宝石：右上那一块里的圆盘
            top = arr[:int(h * 0.30), int(w * 0.60):]
            f2 = top.astype(np.float32)
            mx = f2.max(axis=2); mn = f2.min(axis=2)
            sat = np.where(mx <= 1, 0, (mx - mn) / np.maximum(mx, 1))
            m2 = ndimage.binary_opening(sat > 0.25, np.ones((7, 7)))
            lab2, n2 = ndimage.label(ndimage.binary_fill_holes(m2))
            bonus = '?'
            best = 0
            for i in range(1, n2 + 1):
                ys, xs = np.where(lab2 == i)
                if len(xs) > best:
                    best = len(xs)
                    bonus = hue_class(f2[ys, xs].mean(axis=0))
            facts[f'{lv}-{cn}'] = {
                'level': lv, 'bonus': bonus, 'prestige': None,
                'cost': [{'color': d[3], 'count': None, 'rgb': d[4]} for d in discs],
                'file': f.name,
            }
            # 出图：横幅（声望）+ 折扣圆 + 每个价格圆盘
            tiles.append((f'{lv}{cn} 折扣={bonus}',
                          comp(im).crop((0, 0, int(w * 0.30), int(h * 0.30)))))
            for j, d in enumerate(discs):
                r = d[2] * 1.45
                box = (max(0, int(d[0] - r)), max(0, int(d[1] - r)),
                       min(w, int(d[0] + r)), min(h, int(d[1] + r)))
                tiles.append((f'{lv}{cn} 价{j+1}={d[3]}', comp(im).crop(box)))
    (WIN / 'games/splendor/media/card_facts_raw.json').write_text(
        json.dumps(facts, ensure_ascii=False, indent=1), encoding='utf-8')

    # 每 5 张卡一行，把「横幅 + 折扣 + 价格盘」拼成一张图给人读数字
    TH = 150
    rows = []
    for lv in (1, 2, 3):
        for cn in COLORS:
            key = f'{lv}{cn}'
            row = [t for t in tiles if t[0].startswith(key + ' ')]
            ims = []
            for lbl, t in row:
                s = TH / max(t.height, 1)
                ims.append(t.resize((max(1, int(t.width * s)), TH), Image.LANCZOS))
            rows.append((key, ims))
    CW = max(sum(i.width for i in ims) + 8 * len(ims) for _, ims in rows)
    canvas = Image.new('RGB', (CW, len(rows) * (TH + 8)), (255, 0, 255))
    y = 0
    for key, ims in rows:
        x = 0
        for i in ims:
            canvas.paste(i, (x, y)); x += i.width + 8
        y += TH + 8
    canvas.save('/tmp/card_facts_read.png')
    print('读图用 montage → /tmp/card_facts_read.png', canvas.size)
    print('原始事实（待填数字）→ games/splendor/media/card_facts_raw.json')


if __name__ == '__main__':
    main()
