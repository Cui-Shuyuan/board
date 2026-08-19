# -*- coding: utf-8 -*-
"""一次性数据补丁：给修道院瓷砖 1-15 注册「#N」别名（问题级直呼可确定性命中），
给 monastery_tile 父概念注册「瓷砖」别名（Q33 存储区剩余瓷砖 → 事实站），
并给 tile_15 的描述补上「15 号及以后」终局计分家族说明（Q45 及以后）。"""
import io
import json
import re
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

P = r"D:\workspace\board\games\castles-of-burgundy\concepts.json"
d = json.load(open(P, encoding="utf-8"))

changed = []


def set_alias(o: dict, al: dict):
    """在 abstract 字段之后插入 aliases（保持文件字段排版惯例）。"""
    out = {}
    inserted = False
    for k, v in list(o.items()):
        out[k] = v
        if k == "abstract" and not inserted:
            out["aliases"] = al
            inserted = True
    if not inserted:
        out["aliases"] = al
    o.clear()
    o.update(out)


def walk(o):
    if isinstance(o, dict):
        i = o.get("id")
        m = re.fullmatch(r"monastery_tile_(\d+)", str(i))
        if m and 1 <= int(m.group(1)) <= 15:
            n = m.group(1)
            set_alias(o, {"zh": ["#" + n], "en": ["#" + n]})
            changed.append(i)
        elif i == "monastery_tile":
            set_alias(o, {"zh": ["瓷砖"]})
            changed.append(i)
        elif i == "monastery_tile_15":
            o["description"]["zh"] += (
                "15 号之后同样全是**终局计分**瓷砖：16-23 号为 8 块建筑计分瓷砖"
                "（<monastery_building_score_market> 等，庄园中每放置 1 块对应建筑得 4 <vp>）、"
                "<monastery_tile_24> 每种牲畜 4 分、<monastery_tile_25> 每块已售货物 1 分、"
                "<monastery_tile_26> 每块奖励板块 3 分。"
            )
            o["description"]["en"] += (
                " Tiles after 15 are likewise all **final scoring** tiles: 16-23 are the 8 "
                "building-scoring tiles (<monastery_building_score_market> etc., 4 <vp> per "
                "matching building placed in your duchy), <monastery_tile_24> 4 VP per "
                "livestock type, <monastery_tile_25> 1 VP per sold goods tile, "
                "<monastery_tile_26> 3 VP per bonus tile."
            )
            changed.append(i)
        for v in o.values():
            walk(v)
    elif isinstance(o, list):
        for v in o:
            walk(v)


walk(d)

with open(P, "w", encoding="utf-8", newline="\n") as f:
    json.dump(d, f, ensure_ascii=False, indent=2)
    f.write("\n")

print("changed (%d): %s" % (len(changed), ", ".join(changed)))
