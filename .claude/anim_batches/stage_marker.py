#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给 stage 加「起始玩家标记」这**一件实物** + 它的落位 zone；并把实物尺寸记进 components.json。

为什么尺寸是估值：`components.json` 的铁律是「由实物尺寸换算，不靠扫描件反推」，
而标记的实测值用户还没给。所以这里写 50x62mm 并标 `confirmed_by: "estimated"`，
等用户量了改一个数即可（stage 的 width/height 由它换算）。
"""
import json
import shutil
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path('/home/cui/workspace/board')
WIN = Path('/mnt/d/workspace/board')
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'
COMP = ROOT / 'games/splendor/components.json'

W_MM, H_MM = 50.0, 62.0          # 估值（实物宽 x 高）
MM2U = 0.01

# ① 资产：把 clean.png 裁到不透明包围盒，存成 `_cutout.png`
#    （引擎约定：`<原名>_cutout.png` 优先，且**不再做运行时处理** —— alpha 已是成品）
src = WIN / 'games/splendor/media/marker/起始玩家标记_clean.png'
dst = WIN / 'games/splendor/media/marker/起始玩家标记_clean_cutout.png'
im = Image.open(src).convert('RGBA')
al = np.asarray(im)[..., 3]
ys, xs = np.where(al > 16)
box = (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
im.crop(box).save(dst)
print(f'① 资产 {dst.name}: {im.size} → 裁到件 {box} = {box[2]-box[0]}x{box[3]-box[1]}')

# ② components.json：记下这件实物
comp = json.loads(COMP.read_text(encoding='utf-8'))
if not any(c['id'] == 'starting_player_marker' for c in comp['components']):
    comp['components'].append({
        "id": "starting_player_marker", "zh": "起始玩家标记",
        "w": W_MM, "h": H_MM,
        "confirmed_by": "estimated",
        "note": "⚠ 尺寸是估值（按图片长宽比 0.806 定的 50x62mm），**等用户实测**。"
                "2024 新版是一张菱形纸板标记；照片见 media/marker/。",
    })
    COMP.write_text(json.dumps(comp, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'② components.json 加了 starting_player_marker {W_MM}x{H_MM}mm（estimated）')

# ③ stage：模板 + zone
stage = json.loads(STAGE.read_text(encoding='utf-8'))
if not any(t['id'] == 'starting_marker' for t in stage['templates']):
    stage['templates'].append({
        "id": "starting_marker",
        "shape": "card",
        "palette": "marker",
        "face_image": "media/marker/起始玩家标记_clean.png",
        "width": round(W_MM * MM2U, 4),
        "height": round(H_MM * MM2U, 4),
        "sorting_order": 3,
        "concept": "starting_player_marker",
        "note": "起始玩家标记（2024 新版：菱形纸板件，单面）。"
                "图片走 `_cutout.png` 约定 → media/marker/起始玩家标记_clean_cutout.png"
                "（裁到件、alpha 已成品，尺寸由 components.json 的 %gx%gmm 换算）。" % (W_MM, H_MM),
    })
    print('③ stage 加了模板 starting_marker')
if not any(z['id'] == 'player_marker' for z in stage['zones']):
    stage['zones'].append({
        "id": "player_marker",
        "label": "起始玩家标记位",
        "center": {"x": 0.90, "z": -3.20},
        "layout": {"type": "row", "x_step": 0.3},
        "capacity": 1,
        "palette": "panel_player",
        "size": {"w": round(W_MM * MM2U, 4), "h": round(H_MM * MM2U, 4)},
        "display": {"mode": "count"},
        "concept": None,
        "contains": ["starting_player_marker"],
        "note": "起始玩家标记放在玩家面前的地方。与 `player_holding` 同一排（z=-3.20）、位于其右侧 —— "
                "持有区那一排最多能铺到 x≈0.80（10 件时），所以两区只在持有区接近满时才轻微重叠"
                "（stage 布局校验会报一条 warning，属于已知取舍：标记物理上就该挨着玩家的东西）。",
    })
    print('③ stage 加了 zone player_marker')
STAGE.write_text(json.dumps(stage, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('完成')
