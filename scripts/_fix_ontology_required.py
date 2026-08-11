#!/usr/bin/env python3
"""
一次性迁移脚本 (2026-08-11, 用户拍板方案):

按 E11 校验结果调整 ontology 的 required/optional 归属——required 必须有实现,
无法实现的字段降级到 optional 或通过中间概念收敛:

1. zone: required [id, contains, <ownership>, <information_visibility>] → [id]
   - contains / <information_visibility> → optional (有默认语义: 空数组=不限 / public)
   - <ownership> → 完全移除 (zone 回归最底层"区域"; 归属由 player_supply/public_supply 表达)
2. piece/board/aid/marker: appearance required → optional (外观是描述性信息, 从不强制)
3. state: subject required → optional (ownership 用 owner 表达同一语义)
4. property: domain required → optional (domain 语义已被 parts/绑定替代)
5. 新增 <player_supply> / <public_supply> (extends <supply>):
   各声明 <ownership> 一次, 具体供应堆 specifies 它们 (ownership 实现收敛到 2 处)
6. score_track: 补 constraints.required slots (实现 track.required)
7. top_draw / random_draw: 补 constraints.required source/destination/<object>
   (extends <draw> 的可执行形态, 自身声明字段)

用法:
    D:/Python/Python312/python.exe scripts/_fix_ontology_required.py
"""
import json
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
P = Path(__file__).resolve().parent.parent / "ontology" / "concepts.json"
data = json.loads(P.read_text(encoding="utf-8"))
concepts = data["concepts"]
by_id = {c["id"]: c for c in concepts}

def move_to_optional(cid: str, field: str):
    """把 required 中的条目 (带 description) 移到 optional"""
    c = by_id[cid]
    req = c["constraints"]["required"]
    opt = c["constraints"]["optional"]
    item = next((i for i in req if (i.get("id") if isinstance(i, dict) else i) == field), None)
    if item is None:
        print(f"  ! {cid}: required 无 {field}, 跳过")
        return
    req.remove(item)
    if field not in {(i.get("id") if isinstance(i, dict) else i) for i in opt}:
        opt.append(item)
    print(f"  ✓ {cid}: required 移除 {field} → optional")

def drop_required(cid: str, field: str):
    """从 required 完全移除 (不进 optional)"""
    c = by_id[cid]
    req = c["constraints"]["required"]
    item = next((i for i in req if (i.get("id") if isinstance(i, dict) else i) == field), None)
    if item is None:
        print(f"  ! {cid}: required 无 {field}, 跳过")
        return
    req.remove(item)
    print(f"  ✓ {cid}: required 移除 {field} (完全删除)")

def add_required(cid: str, fields: list[tuple[str, str, str]]):
    """补 constraints.required 条目 (id, zh, en)"""
    c = by_id[cid]
    req = c["constraints"]["required"]
    exist = {(i.get("id") if isinstance(i, dict) else i) for i in req}
    for fid, zh, en in fields:
        if fid in exist:
            print(f"  ! {cid}: {fid} 已存在, 跳过")
            continue
        req.append({"id": fid, "description": {"zh": zh, "en": en}})
        print(f"  ✓ {cid}: required 补 {fid}")

print("== 1. 降级 (required → optional) ==")
for cid, field in [("zone", "contains"), ("zone", "<information_visibility>"),
                   ("piece", "appearance"), ("board", "appearance"), ("aid", "appearance"),
                   ("marker", "appearance"), ("state", "subject"), ("property", "domain")]:
    move_to_optional(cid, field)
print("== 2. zone 完全移除 <ownership> ==")
drop_required("zone", "<ownership>")
print("== 3. 新增 player_supply / public_supply ==")
NEW_CONCEPTS = [
    {
        "id": "player_supply",
        "extends": "<supply>",
        "name": {"zh": "玩家供应堆", "en": "Player Supply"},
        "abstract": False,
        "description": {
            "zh": "归属于某位玩家的供应堆——其中存放的 <object> 属于该玩家，其他玩家不可取用。公共供应堆的 <ownership> 为 null；玩家专属供应堆固定归属其玩家。",
            "en": "A supply belonging to a specific player — the <object>s in it belong to that player, no other player may take them. Public supplies have <ownership> null; player supplies are owned by their player."
        },
        "<ownership>": {
            "type": "<ontology::player>",
            "description": {"zh": "该供应堆的归属玩家。", "en": "The player who owns this supply."}
        }
    },
    {
        "id": "public_supply",
        "extends": "<supply>",
        "name": {"zh": "公共供应堆", "en": "Public Supply"},
        "abstract": False,
        "description": {
            "zh": "不归属任何玩家的供应堆——所有玩家均可取用其中的 <object>。<ownership> 固定为 null。",
            "en": "A supply owned by no one — every player may take <object>s from it. <ownership> is fixed to null."
        },
        "<ownership>": None
    },
]
for nc in NEW_CONCEPTS:
    if nc["id"] in by_id:
        print(f"  ! {nc['id']} 已存在, 跳过")
    else:
        concepts.append(nc)
        by_id[nc["id"]] = nc
        print(f"  ✓ 新增 {nc['id']}")

print("== 4. score_track 补 slots ==")
add_required("score_track", [(
    "slots",
    "轨道槽位定义——取值范围字符串（如「0–12」）或槽位对象数组（每个含 name + <ontology::effect>）。终局计分轨、进程轨等使用槽位数组，纯数值轨使用范围字符串。",
    "Track slot definition — a value range string (e.g. '0–12') or an array of slot objects (each with name + <ontology::effect>). Scoring and progress tracks use slot arrays; pure numeric tracks use a range string."
)])
print("== 5. top_draw / random_draw 补字段 ==")
add_required("top_draw", [
    ("source", "抽牌的来源 <deck>。", "The source <deck> to draw from."),
    ("destination", "抽出的 <object> 进入的目标区。", "Where drawn <object>s go."),
    ("<object>", "被抽取的对象类型。", "The type of object being drawn."),
])
add_required("random_draw", [
    ("source", "抽牌的来源 <pool>（袋子等）。", "The source <pool> (bag etc.) to draw from."),
    ("destination", "抽出的 <object> 进入的目标区。", "Where drawn <object>s go."),
    ("<object>", "被抽取的对象类型。", "The type of object being drawn."),
])

P.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"\n已保存 ontology/concepts.json (共 {len(concepts)} 个概念)")
