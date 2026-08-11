#!/usr/bin/env python3
"""
一次性清理脚本 (2026-08-11, 用户定稿: <> 只允许引用真实概念):

把全库 131 处"非概念引用"按语义修正:

1. 槽位名 → 路径引用 <轨道>.<槽位>: weather_gauge 的 hot/cold/hot_beyond/cold_beyond、
   final_scoring_area_hex 的 11 个计分槽位 (technology/prestige/.../point_bonus)
2. 字段名 → 裸写 (去 <>): target/options/type/do_after/components/result/level_effects/
   ownership_change/active_condition/level/parts/name
3. 枚举值 → 裸写: face_up/face_down/white

不处理 (另有计划): volcano (地点未录入, 保留报错提醒); starting_chip_zone (补概念)
"""
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = Path(__file__).resolve().parent.parent

FILES = [
    "ontology/concepts.json", "ontology/flow.json",
    "games/civolution/concepts.json", "games/civolution/flow.json",
    "games/civolution/instances.json",
    "games/splendor/concepts.json", "games/splendor/flow.json",
]

SLOT_PATHS = {
    "hot": "weather_gauge", "hot_beyond": "weather_gauge",
    "cold": "weather_gauge", "cold_beyond": "weather_gauge",
    "technology": "final_scoring_area_hex", "prestige": "final_scoring_area_hex",
    "knowledge": "final_scoring_area_hex", "construction": "final_scoring_area_hex",
    "culture": "final_scoring_area_hex", "evolution": "final_scoring_area_hex",
    "prosperity": "final_scoring_area_hex", "population": "final_scoring_area_hex",
    "expansion": "final_scoring_area_hex", "point_bonus": "final_scoring_area_hex",
}
FIELDS = ["target", "ownership_change", "result", "options", "type", "do_after",
          "level_effects", "components", "level", "active_condition", "parts", "name"]
ENUMS = ["face_up", "face_down", "white"]

total = 0
for rel in FILES:
    p = ROOT / rel
    if not p.exists():
        continue
    text = p.read_text(encoding="utf-8")
    orig = text
    for name, track in SLOT_PATHS.items():
        text, n = re.subn(rf"<{name}>", f"<{track}>.<{name}>", text)
        if n:
            print(f"{rel}: <{name}> → <{track}>.<{name}> ×{n}")
        total += n
    for name in FIELDS:
        text, n = re.subn(rf"<{name}>", name, text)
        if n:
            print(f"{rel}: <{name}> → {name} (裸写) ×{n}")
        total += n
    for name in ENUMS:
        text, n = re.subn(rf"<{name}>", name, text)
        if n:
            print(f"{rel}: <{name}> → {name} (枚举裸写) ×{n}")
        total += n
    if text != orig:
        p.write_text(text, encoding="utf-8")

print(f"\n共替换 {total} 处")
