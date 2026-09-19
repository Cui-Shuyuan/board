#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给 stage 加两样东西：

① **部位锚点**（`part_anchors`）：每张卡/贵族板块上，"左上角声望 / 左下角价格 / 右上角折扣 /
   下方条件"各自在件平面里的相对坐标。这是动画独有的局部概念 —— 本体只说这张牌印着什么，
   动画要能把它圈出来。偏移是世界单位、相对件中心（+x 右、+y 上），件的朝向会带着它转。

② **玩家 B 的区**：原来的玩家区（player_holding / development / reserved / nobles）就是
   **玩家 A（学习者在的那一侧，桌面下方）**；现在补一套 B 的，摆在上方（对面）。
   多人对比的说法（轮到谁、不能从别人那里拿、终局谁还没走完）才有东西可指。

副作用：桌面变高 → extent.max_z 2.84 → 5.00，整桌取景里所有件小约 20%（要用整桌镜头时能看见对面玩家，
这笔账换得过来；嫌小可以再改 stage 一处）。
"""
import json
from pathlib import Path

ROOT = Path('/home/cui/workspace/board')
STAGE = ROOT / 'games/splendor/tutorial/anim/_stage/splendor.table.json'
stage = json.loads(STAGE.read_text(encoding='utf-8'))

# ── ① 部位锚点 ─────────────────────────────────────────────────────────────
# 发展卡 63x88mm → 世界 0.63x0.88。牌面：左上横幅=声望、右上圆=折扣、左下竖排圆盘=价格。
CARD_ANCHORS = [
    {"id": "prestige", "label": "左上角：声望值", "dx": -0.235, "dy": 0.315, "r": 0.075},
    {"id": "cost", "label": "左下角：价格（宝石种类与数量）", "dx": -0.235, "dy": -0.215, "r": 0.16},
    {"id": "bonus", "label": "右上角：折扣宝石", "dx": 0.205, "dy": 0.325, "r": 0.085},
]
# 贵族 60x60mm → 世界 0.6x0.6。左上=声望、下方=条件（宝石图标）。
NOBLE_ANCHORS = [
    {"id": "prestige", "label": "左上角：声望值", "dx": -0.215, "dy": 0.215, "r": 0.07},
    {"id": "condition", "label": "下方：结识条件（已购卡牌的种类与数量）",
     "dx": -0.04, "dy": -0.175, "r": 0.15},
]

CARD_CONCEPTS = {"development_card_level_1", "development_card_level_2", "development_card_level_3"}
added = 0
for t in stage['templates']:
    if t.get('part_anchors'):
        continue
    if t.get('shape') == 'card' and t.get('concept') in CARD_CONCEPTS:
        t['part_anchors'] = [dict(a) for a in CARD_ANCHORS]
        added += 1
    elif t.get('concept') == 'noble':
        t['part_anchors'] = [dict(a) for a in NOBLE_ANCHORS]
        added += 1

# ── ② 玩家 B 的区（玩家 A = 原来那几个，桌面下方）────────────────────────────
B_ZONES = [
    {"id": "player_b_development", "label": "玩家B的发展区",
     "center": {"x": -1.60, "z": 3.45},
     "layout": {"type": "grid", "cols": 3, "x_step": 0.66, "z_step": 0.46},
     "capacity": 6, "palette": "panel_player", "size": {"w": 0.63, "h": 0.88},
     "display": {"mode": "count"}, "concept": "<development_area>",
     "contains": ["development_card_level_1", "development_card_level_2", "development_card_level_3"],
     "note": "玩家B（对面）买下的发展卡。容量 6：教程里 B 只是『另一个玩家』，不需要摆满。"},
    {"id": "player_b_holding", "label": "玩家B的持有区",
     "center": {"x": 0.30, "z": 3.30},
     "layout": {"type": "row", "x_step": 0.3},
     "capacity": 6, "palette": "panel_player", "size": {"w": 0.43, "h": 0.43},
     "display": {"mode": "count"}, "concept": "<player_holding>",
     "contains": ["gem", "gold"],
     "note": "玩家B手里的宝石/黄金。"},
    {"id": "player_b_reserved", "label": "玩家B的保留区",
     "center": {"x": 0.30, "z": 4.15},
     "layout": {"type": "row", "x_step": 0.35},
     "capacity": 3, "palette": "panel_player", "size": {"w": 0.63, "h": 0.88},
     "display": {"mode": "count"}, "concept": "<reserve>",
     "contains": ["development_card_level_1", "development_card_level_2", "development_card_level_3"],
     "note": "玩家B保留的卡（朝下，别人看不到内容）。"},
    {"id": "player_b_nobles", "label": "玩家B结识的贵族",
     "center": {"x": -1.60, "z": 4.45},
     "layout": {"type": "row", "x_step": 0.66},
     "capacity": 3, "palette": "panel_player", "size": {"w": 0.6, "h": 0.6},
     "display": {"mode": "count"}, "concept": "<player_zone>",
     "contains": ["noble"],
     "note": "玩家B结识的贵族。3.2.2 那句『不可以从其他人那里拿贵族』就指着这里。"},
]
for z in B_ZONES:
    if not any(x['id'] == z['id'] for x in stage['zones']):
        stage['zones'].append(z)

if not any(a['id'] == 'board_player_b_area' for a in stage['anchors']):
    stage['anchors'].append({
        "id": "board_player_b_area", "template": "board_player",
        "x": -0.65, "z": 3.80,
        "zones": ["player_b_development", "player_b_holding", "player_b_reserved", "player_b_nobles"],
    })

stage['board']['extent']['max_z'] = 5.00
stage['note'] = (stage.get('note', '') +
                 "\n\n【玩家区 A/B（2026-09 用户指示）】不带前缀的那几个（player_holding / "
                 "player_development / player_reserved / player_nobles / player_marker）是**玩家A**"
                 "= 学习者这一侧，摆在桌面下方；player_b_* 是对面那位。教程里 A 的账是完整的，"
                 "B 只在『多人对比』的几句话里出现。")
STAGE.write_text(json.dumps(stage, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'部位锚点加到 {added} 个模板；玩家B 4 个 zone；extent.max_z=5.00')
