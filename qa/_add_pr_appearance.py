# -*- coding: utf-8 -*-
"""一次性脚本：为 games/puerto-rico/concepts.json 的实体概念补 appearance。
父类概念（good/estate_tile/building 等）补字段声明；具体实体补外观数据。
只修改这一个新文件，不触碰其他文件。
"""
import json
import sys
from pathlib import Path

PATH = Path(r"D:\workspace\board\games\puerto-rico\concepts.json")

# 父类概念 → 字段声明 (type + description)
FIELD_DECLS = {
    "good": (
        "货物桶的外观描述——小木桶，颜色对应货物（黄=玉米、绿=水果、白=糖、棕=烟草、黑=咖啡）。AI 向玩家解释「哪个是咖啡桶」时的关键视觉信息。",
        "Appearance of a goods barrel — a small wooden barrel colored by good (yellow=corn, green=fruit, white=sugar, brown=tobacco, black=coffee). Key visual info for the AI to identify barrels."
    ),
    "estate_tile": (
        "庄园板块的外观描述——方形小板块，正面画作物图案与 1 个工人半圆槽，背面统一图案。",
        "Appearance of an estate tile — a small square tile with the crop art and 1 worker semicircle on the front, uniform back."
    ),
    "building": (
        "建筑板块的外观描述——正面印建筑名、右上角 VP 数字、半圆槽（首个半圆内印费用）、功能图标（商业建筑），背面统一图案。",
        "Appearance of a building tile — front shows the name, the VP number top-right, semicircles (the first one printed with the cost), and the function icon (commercial buildings); uniform back."
    ),
    "production_building": (
        "生产建筑板块的外观——板块颜色对应产物：绿=水果、白=糖、棕=烟草、黑=咖啡。",
        "Appearance of a production building tile — color-coded by product: green=fruit, white=sugar, brown=tobacco, black=coffee."
    ),
    "commercial_building": (
        "商业建筑板块的外观——印建筑名、费用、VP 与功能图标（板块名称下方的图标标明生效阶段）。",
        "Appearance of a commercial building tile — shows the name, cost, VP, and a function icon (the icon under the name marks the phase it applies in)."
    ),
    "large_commercial_building": (
        "大商业建筑板块的外观——两格高的宽板块，紫色调，印建筑名、费用 10、VP 4 与终局加成说明。",
        "Appearance of a large commercial building tile — a double-height wide purple tile showing the name, cost 10, VP 4, and the endgame bonus."
    ),
    "role_card": (
        "角色卡的外观——卡牌正面印角色名、插画与行动/特权图标。",
        "Appearance of a role card — the front shows the role name, artwork, and action/privilege icons."
    ),
    "cargo_ship": (
        "货船的外观——船形板块，印货舱格数（4–8）。",
        "Appearance of a cargo ship — a ship-shaped tile printed with its hold spaces (4–8)."
    ),
}

# 具体实体 → 外观数据 (zh, en)
APPEAR_DATA = {
    "corn_estate": ("黄色玉米田图案的庄园小板块，带 1 个半圆槽。", "Small estate tile with yellow cornfield art and 1 semicircle."),
    "fruit_estate": ("绿色果园图案的庄园小板块，带 1 个半圆槽。", "Small estate tile with green orchard art and 1 semicircle."),
    "sugar_estate": ("白色甘蔗田图案的庄园小板块，带 1 个半圆槽。", "Small estate tile with white sugarcane art and 1 semicircle."),
    "tobacco_estate": ("棕色烟叶田图案的庄园小板块，带 1 个半圆槽。", "Small estate tile with brown tobacco-field art and 1 semicircle."),
    "coffee_estate": ("黑色咖啡园图案的庄园小板块，带 1 个半圆槽。", "Small estate tile with black coffee-plantation art and 1 semicircle."),
    "quarry_tile": ("灰色采石场图案的小板块，带 1 个半圆槽。", "Small tile with gray quarry art and 1 semicircle."),
    "small_fruit_warehouse": ("绿色图案的生产建筑小板块，1 个半圆槽，费用 1、1 VP。", "Small green production building tile, 1 semicircle, cost 1, 1 VP."),
    "small_sugar_mill": ("白色图案的生产建筑小板块，1 个半圆槽，费用 2、1 VP。", "Small white production building tile, 1 semicircle, cost 2, 1 VP."),
    "large_fruit_warehouse": ("绿色图案的生产建筑板块，2 个半圆槽，费用 3、2 VP。", "Green production building tile, 2 semicircles, cost 3, 2 VP."),
    "large_sugar_mill": ("白色图案的生产建筑板块，2 个半圆槽，费用 4、2 VP。", "White production building tile, 2 semicircles, cost 4, 2 VP."),
    "tobacco_storage": ("棕色图案的生产建筑板块，3 个半圆槽，费用 5、3 VP。", "Brown production building tile, 3 semicircles, cost 5, 3 VP."),
    "coffee_roaster": ("黑色图案的生产建筑板块，3 个半圆槽，费用 6、3 VP。", "Black production building tile, 3 semicircles, cost 6, 3 VP."),
    "small_market": ("紫色调商业建筑小板块，费用 1、1 VP，标贸易阶段图标。", "Small purple commercial building tile, cost 1, 1 VP, marked with the trade-phase icon."),
    "hacienda": ("紫色调商业建筑小板块，费用 2、1 VP，标种植阶段图标。", "Small purple commercial building tile, cost 2, 1 VP, marked with the planting-phase icon."),
    "builders_yard": ("紫色调商业建筑小板块，费用 2、1 VP，标种植阶段图标。", "Small purple commercial building tile, cost 2, 1 VP, marked with the planting-phase icon."),
    "small_warehouse": ("紫色调商业建筑小板块，费用 3、1 VP，标装运阶段图标。", "Small purple commercial building tile, cost 3, 1 VP, marked with the shipment-phase icon."),
    "hospital": ("紫色调商业建筑板块，费用 4、2 VP，标种植阶段图标。", "Purple commercial building tile, cost 4, 2 VP, marked with the planting-phase icon."),
    "office": ("紫色调商业建筑板块，费用 5、2 VP，标贸易阶段图标。", "Purple commercial building tile, cost 5, 2 VP, marked with the trade-phase icon."),
    "large_market": ("紫色调商业建筑板块，费用 5、2 VP，标贸易阶段图标。", "Purple commercial building tile, cost 5, 2 VP, marked with the trade-phase icon."),
    "large_warehouse": ("紫色调商业建筑板块，费用 6、2 VP，标装运阶段图标。", "Purple commercial building tile, cost 6, 2 VP, marked with the shipment-phase icon."),
    "factory": ("紫色调商业建筑板块，费用 7、3 VP，标生产阶段图标。", "Purple commercial building tile, cost 7, 3 VP, marked with the production-phase icon."),
    "school": ("紫色调商业建筑板块，费用 8、3 VP，标建造阶段图标。", "Purple commercial building tile, cost 8, 3 VP, marked with the build-phase icon."),
    "harbor": ("紫色调商业建筑板块，费用 8、3 VP，标装运阶段图标。", "Purple commercial building tile, cost 8, 3 VP, marked with the shipment-phase icon."),
    "wharf": ("紫色调商业建筑板块，费用 9、3 VP，标装运阶段图标。", "Purple commercial building tile, cost 9, 3 VP, marked with the shipment-phase icon."),
    "fire_station": ("紫色两格高大建筑板块，费用 10、4 VP，印生产建筑加分图标。", "Purple double-height large building tile, cost 10, 4 VP, with production-building bonus icon."),
    "residence": ("紫色两格高大建筑板块，费用 10、4 VP，印乡村格数计分表。", "Purple double-height large building tile, cost 10, 4 VP, with countryside-space scoring table."),
    "fortress": ("紫色两格高大建筑板块，费用 10、4 VP，印工人计分图标。", "Purple double-height large building tile, cost 10, 4 VP, with worker-scoring icon."),
    "customs_house": ("紫色两格高大建筑板块，费用 10、4 VP，印 VP 筹码计分图标。", "Purple double-height large building tile, cost 10, 4 VP, with VP-chip scoring icon."),
    "city_hall": ("紫色两格高大建筑板块，费用 10、4 VP，印商业建筑计分图标。", "Purple double-height large building tile, cost 10, 4 VP, with commercial-building scoring icon."),
    "planter_card": ("角色卡——正面印种植园主插画、行动（取庄园）与特权（可采石）图标。", "Role card — front shows planter artwork and the action (take estate) / privilege (quarry) icons."),
    "builder_card": ("角色卡——正面印建筑师插画、行动（建 1 座建筑）与特权（减 1 元）图标。", "Role card — front shows builder artwork and the action (build 1) / privilege (−1 coin) icons."),
    "recruiter_card": ("角色卡——正面印招募官插画、行动（分发工人）与特权（多 1 名工人）图标。", "Role card — front shows recruiter artwork and the action (distribute workers) / privilege (+1 worker) icons."),
    "craftsman_card": ("角色卡——正面印工匠插画、行动（生产货物）与特权（多 1 桶）图标。", "Role card — front shows craftsman artwork and the action (produce) / privilege (+1 barrel) icons."),
    "trader_card": ("角色卡——正面印商人插画、行动（卖 1 桶）与特权（多 1 元）图标。", "Role card — front shows trader artwork and the action (sell 1) / privilege (+1 coin) icons."),
    "captain_card": ("角色卡——正面印船长插画、行动（装船）与特权（多 1 VP）图标。", "Role card — front shows captain artwork and the action (load ships) / privilege (+1 VP) icons."),
    "adventurer_card": ("角色卡——正面印冒险家插画与特权（拿 1 元）图标，无行动。", "Role card — front shows adventurer artwork and the privilege (take 1 coin) icon; no action."),
    "cargo_ship_4": ("4 舱货船——印 4 个货舱格的船板块。", "A ship tile with 4 cargo hold spaces."),
    "cargo_ship_5": ("5 舱货船——印 5 个货舱格的船板块。", "A ship tile with 5 cargo hold spaces."),
    "cargo_ship_6": ("6 舱货船——印 6 个货舱格的船板块。", "A ship tile with 6 cargo hold spaces."),
    "cargo_ship_7": ("7 舱货船——印 7 个货舱格的船板块。", "A ship tile with 7 cargo hold spaces."),
    "cargo_ship_8": ("8 舱货船——印 8 个货舱格的船板块。", "A ship tile with 8 cargo hold spaces."),
    "trading_house": ("交易所小板块——4 个货物位置，印五种货物售价（玉米 0、水果 1、糖 2、烟草 3、咖啡 4）。", "Small trading house tile — 4 goods spaces, printed with the five prices (corn 0, fruit 1, sugar 2, tobacco 3, coffee 4)."),
}


def insert_appearance(obj: dict) -> dict:
    oid = obj.get("id")
    if "appearance" in obj:
        return obj
    if oid in FIELD_DECLS:
        zh, en = FIELD_DECLS[oid]
        decl = {"type": "string", "description": {"zh": zh, "en": en}}
    elif oid in APPEAR_DATA:
        zh, en = APPEAR_DATA[oid]
        decl = {"zh": zh, "en": en}
    else:
        return obj
    out = {}
    for k, v in obj.items():
        out[k] = v
        if k == "abstract":
            out["appearance"] = decl
    return out


def main():
    data = json.loads(PATH.read_text(encoding="utf-8"))
    data["objects"] = [insert_appearance(o) for o in data["objects"]]
    PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    got = {o["id"] for o in data["objects"] if "appearance" in o}
    expected = set(FIELD_DECLS) | set(APPEAR_DATA)
    missing = expected - got
    print(f"appearance 已写入: {len(got & expected)} 个")
    print(f"缺失目标: {sorted(missing)}" if missing else "全部命中")


if __name__ == "__main__":
    main()
