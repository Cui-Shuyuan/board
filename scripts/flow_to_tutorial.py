#!/usr/bin/env python3
"""
flow.json → tutorial.json 确定性编译器（第一版）。

设计目标：概念/流程层已经定义了 transfer / shuffle / random_draw / top_draw /
state_change 等语义，这些语义与动画原语直接对应。本脚本只做翻译，不创作：
从 flow.json 的 procedures 树中提取叶节点，映射为 tutorial.json 的时间轴事件。

产物是「结构正确的草稿」而不是最终教程：
- slot 坐标是自动网格占位，需要你或 LLM 在版图扫描图上校准；
- sprite 文件路径是 media/auto/{id}.png 占位，需要替换为实际扫描/拍摄图；
- 每章的音频是 media/audio/{chapter_id}.mp3 占位。

用法：
    python scripts/flow_to_tutorial.py --game splendor
    python scripts/flow_to_tutorial.py --game splendor --out games/splendor/tutorial.json
    python scripts/flow_to_tutorial.py --game splendor --stdout

输出后建议立即校验：
    python scripts/validate_tutorial.py --game splendor --skip-assets
"""

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VALID_ID = re.compile(r"^[A-Za-z0-9_./-]+$")
CONCEPT_REF = re.compile(r"^<([^>]+)>$")

# 动画原语可识别的 flow 语义
TRANSFER_SPECS = {"<ontology::transfer>"}
SHUFFLE_SPECS = {"<ontology::shuffle>"}
RANDOM_DRAW_SPECS = {"<ontology::random_draw>"}
TOP_DRAW_SPECS = {"<ontology::top_draw>"}
STATE_CHANGE_SPECS = {"<ontology::state_change>"}

# 事件时长启发值（秒）
DURATION = {
    "move": 1.2,
    "shuffle": 1.5,
    "highlight": 1.5,
    "wait": 0.8,
}
GAP = 0.2


class SlotRegistry:
    def __init__(self):
        self.slots = {}
        self.order = []

    def ensure(self, ref_value, node_id, field):
        sid = ref_to_id(ref_value, node_id, field)
        if sid not in self.slots:
            self.slots[sid] = {
                "id": sid,
                "x": 0.0,
                "y": 0.0,
                "z": 0.0,
                "label": {"zh": display_name(ref_value, node_id)},
                "comment": f"自动生成占位，请校准版图坐标。来源：{node_id}.{field}",
            }
            self.order.append(sid)
        return sid

    def assign_grid(self):
        # 确定性的自动网格，先按插入顺序铺开。真实坐标需要人工/视觉模型校准。
        n = len(self.order)
        if n == 0:
            return
        cols = 4
        for i, sid in enumerate(self.order):
            col = i % cols
            row = i // cols
            self.slots[sid]["x"] = round(0.08 + col * 0.21, 3)
            self.slots[sid]["y"] = round(0.12 + row * 0.18, 3)

    def items(self):
        return [self.slots[sid] for sid in self.order]


class SpriteRegistry:
    def __init__(self):
        self.sprites = {}
        self.order = []

    def ensure(self, obj_value, node_id, field="object"):
        sid = object_to_id(obj_value, node_id, field)
        if sid not in self.sprites:
            self.sprites[sid] = {
                "id": sid,
                "file": f"media/auto/{sid}.png",
                "width_mm": 60.0,
                "height_mm": 60.0,
                "pivot": "center",
                "z_offset": 1.0,
                "comment": f"自动生成占位，请替换为实际扫描/拍摄图并填写真实尺寸。来源：{node_id}.{field}",
            }
            self.order.append(sid)
        return sid

    def items(self):
        return [self.sprites[sid] for sid in self.order]


def display_name(ref_value, node_id):
    if isinstance(ref_value, str):
        m = CONCEPT_REF.match(ref_value)
        if m:
            return m.group(1).split("::")[-1]
        return ref_value.strip()[:40]
    return node_id


def ref_to_id(ref_value, node_id, field):
    """把 flow 里的概念引用/文本转为 tutorial 可用的 slot id。"""
    if isinstance(ref_value, str):
        m = CONCEPT_REF.match(ref_value)
        if m:
            s = m.group(1).split("::")[-1].strip()
            if VALID_ID.match(s):
                return s
        else:
            s = ref_value.strip()
            if VALID_ID.match(s):
                return s
    # 非概念引用（如中文目的地）或含特殊字符：用 node_id + field 生成稳定 id
    return f"{node_id}_{field}"


def object_to_id(obj_value, node_id, field="object"):
    """把 <ontology::object> 的值（字符串/对象/数组）转为 sprite id。"""
    if isinstance(obj_value, str):
        return ref_to_id(obj_value, node_id, field)
    if isinstance(obj_value, dict):
        for key in obj_value:
            return ref_to_id(key, node_id, field)
    if isinstance(obj_value, list):
        for item in obj_value:
            return object_to_id(item, node_id, field)
    return f"{node_id}_{field}"


def load_json(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def has_pipeline(node):
    if not isinstance(node, dict):
        return False
    p = node.get("<ontology::pipeline>")
    return bool(p and p.get("options"))


def pipeline_options(node):
    if not isinstance(node, dict):
        return []
    p = node.get("<ontology::pipeline>")
    return [o for o in (p.get("options", []) if p else []) if isinstance(o, dict)]


ACTION_KEYS = ["<ontology::transfer>", "<ontology::shuffle>", "<ontology::random_draw>", "<ontology::top_draw>", "<ontology::state_change>", "<ontology::play>"]


def get_inline_action(node):
    """返回节点内联的 ontology 动作键名（如 <ontology::transfer>），没有则 None。"""
    for key in ACTION_KEYS:
        if key in node:
            return key
    # 动作也可能藏在 <ontology::content>.<ontology::instant_content> 里（如 refill_market）
    content = node.get("<ontology::content>")
    if isinstance(content, dict):
        instant = content.get("<ontology::instant_content>")
        if isinstance(instant, dict):
            for key in ACTION_KEYS:
                if key in instant:
                    return key
    return None


def detect_action(node):
    spec = node.get("specifies")
    if spec in TRANSFER_SPECS:
        return "move"
    if spec in SHUFFLE_SPECS:
        return "shuffle"
    if spec in RANDOM_DRAW_SPECS or spec in TOP_DRAW_SPECS:
        return "move"  # 盲抽/顶抽在动画上仍是从 source 到 destination 的 move
    if spec in STATE_CHANGE_SPECS:
        return "highlight"

    inline = get_inline_action(node)
    if inline in ["<ontology::transfer>", "<ontology::random_draw>", "<ontology::top_draw>", "<ontology::play>"]:
        return "move"
    if inline == "<ontology::shuffle>":
        return "shuffle"
    if inline == "<ontology::state_change>":
        return "highlight"

    # 没有 specifies 但有 source+destination 的，按 transfer 处理（如部分 setup 步骤）
    if node.get("source") and node.get("destination"):
        return "move"

    return "wait"


def action_payload(node):
    """返回内联动作对象（若有），用于读取 source/destination/target/object。"""
    for key in ACTION_KEYS:
        if key in node and isinstance(node[key], dict):
            return node[key]
    content = node.get("<ontology::content>")
    if isinstance(content, dict):
        instant = content.get("<ontology::instant_content>")
        if isinstance(instant, dict):
            for key in ACTION_KEYS:
                if key in instant and isinstance(instant[key], dict):
                    return instant[key]
    return node


def make_event_id(node_id, used):
    eid = node_id if VALID_ID.match(node_id or "") else f"event_{len(used)}"
    if eid in used:
        i = 2
        while f"{eid}_{i}" in used:
            i += 1
        eid = f"{eid}_{i}"
    used.add(eid)
    return eid


def build_events(node, registries, used_event_ids):
    if not isinstance(node, dict):
        return [wait_event_for_ref(node, used_event_ids)]
    action = detect_action(node)
    payload = action_payload(node)
    desc = (node.get("description") or {}).get("zh", "") if isinstance(node.get("description"), dict) else (node.get("description") or "")
    if not desc:
        name = node.get("name") or {}
        desc = name.get("zh", node.get("id", "")) if isinstance(name, dict) else str(name)
    comment = desc[:120]

    events = []

    if action == "move":
        source = payload.get("source")
        destination = payload.get("destination")
        obj = payload.get("<ontology::object>") or node.get("<ontology::object>")
        sprite_id = registries["sprite"].ensure(obj, node.get("id", "event"), "object")

        from_slot = registries["slot"].ensure(source, node.get("id", "event"), "source") if source else None
        to_slot = registries["slot"].ensure(destination, node.get("id", "event"), "destination") if destination else None

        if from_slot and to_slot:
            events.append({
                "id": make_event_id(node.get("id", "event"), used_event_ids),
                "t": 0.0,
                "duration": DURATION["move"],
                "action": "move",
                "sprite": sprite_id,
                "from_slot": from_slot,
                "to_slot": to_slot,
                "easing": "easeOutCubic",
                "comment": comment,
            })
        else:
            events.append(wait_event(node, used_event_ids, comment))
    elif action == "shuffle":
        target = payload.get("target") or node.get("target")
        obj = payload.get("<ontology::object>") or node.get("<ontology::object>")
        if target:
            sprite_id = registries["sprite"].ensure(target, node.get("id", "event"), "target")
            # 也登记一个 slot，便于后续摆放牌堆
            registries["slot"].ensure(target, node.get("id", "event"), "target")
        else:
            sprite_id = registries["sprite"].ensure(obj, node.get("id", "event"), "object")
        events.append({
            "id": make_event_id(node.get("id", "event"), used_event_ids),
            "t": 0.0,
            "duration": DURATION["shuffle"],
            "action": "shuffle",
            "sprite": sprite_id,
            "easing": "easeOutCubic",
            "comment": comment,
        })
    elif action == "highlight":
        subject = payload.get("subject") or node.get("subject")
        if subject:
            slot_id = registries["slot"].ensure(subject, node.get("id", "event"), "subject")
            events.append({
                "id": make_event_id(node.get("id", "event"), used_event_ids),
                "t": 0.0,
                "duration": DURATION["highlight"],
                "action": "highlight",
                "slot": slot_id,
                "color": "#FFD54F",
                "loop": False,
                "comment": comment,
            })
        else:
            events.append(wait_event(node, used_event_ids, comment))
    else:
        events.append(wait_event(node, used_event_ids, comment))

    return events


def wait_event(node, used_event_ids, comment):
    return {
        "id": make_event_id(node.get("id", "event"), used_event_ids),
        "t": 0.0,
        "duration": DURATION["wait"],
        "action": "wait",
        "comment": comment,
    }


def assign_event_times(events):
    t = 0.0
    for ev in events:
        ev["t"] = round(t, 2)
        t += ev["duration"] + GAP
    return events


def collect_leaf_sections(node):
    """收集「叶子章节节点」：有 pipeline 且所有子节点都没有 pipeline 的节点。"""
    if not has_pipeline(node):
        return []
    children = pipeline_options(node)
    if not children:
        return []
    if not any(has_pipeline(c) for c in children):
        return [node]
    out = []
    for c in children:
        if has_pipeline(c):
            out.extend(collect_leaf_sections(c))
    return out


def chapter_id_for(node):
    sid = node.get("id") or "chapter"
    if VALID_ID.match(sid):
        return sid
    return f"chapter_{len(sid)}"


def content_action_options(node):
    """读取 <ontology::content> → <ontology::instant_content> → options 的动作子节点。"""
    content = node.get("<ontology::content>")
    if isinstance(content, dict):
        instant = content.get("<ontology::instant_content>")
        if isinstance(instant, dict):
            opts = instant.get("options")
            if isinstance(opts, list):
                return opts
        # 也可能是单条内联动作，如 refill_market 的 <ontology::top_draw>
        return None
    return None


def collect_action_leaves(node):
    """收集动作/触发器流程里的叶子节点（没有 options 的节点）。"""
    opts = content_action_options(node)
    if not opts:
        opts = node.get("options") if isinstance(node.get("options"), list) else None
    if opts:
        out = []
        for o in opts:
            out.extend(collect_action_leaves(o))
        return out
    return [node]


def build_chapter_from_leaves(chapter_node, leaf_nodes, registries, used_event_ids):
    events = []
    for child in leaf_nodes:
        events.extend(build_events(child, registries, used_event_ids))
    assign_event_times(events)
    return chapter_dict(chapter_node, events)


def chapter_dict(node, events):
    name = node.get("name") or {}
    title = {"zh": name.get("zh", node.get("id", ""))}
    if name.get("en"):
        title["en"] = name["en"]
    cid = chapter_id_for(node)
    return {
        "id": cid,
        "title": title,
        "audio": f"media/audio/{cid}.mp3",
        "interrupt_context": f"当前在讲：{title.get('zh', node.get('id', ''))}",
        "subtitles": [{"t": 0.0, "text": title.get("zh", "")}],
        "timeline": events,
    }


def build_chapter(node, registries, used_event_ids):
    children = pipeline_options(node)
    events = []
    for child in children:
        if isinstance(child, dict):
            events.extend(build_events(child, registries, used_event_ids))
        else:
            events.append(wait_event_for_ref(child, used_event_ids))
    assign_event_times(events)
    return chapter_dict(node, events)


def wait_event_for_ref(ref_value, used_event_ids):
    ref = str(ref_value)
    sid = ref.strip().strip("<").strip(">").split("::")[-1]
    if not VALID_ID.match(sid):
        sid = f"event_{len(used_event_ids)}"
    return {
        "id": make_event_id(sid, used_event_ids),
        "t": 0.0,
        "duration": DURATION["wait"],
        "action": "wait",
        "comment": f"引用流程节点：{ref}（待展开为动画）",
    }
    name = node.get("name") or {}
    title = {"zh": name.get("zh", node.get("id", ""))}
    if name.get("en"):
        title["en"] = name["en"]
    return {
        "id": chapter_id_for(node),
        "title": title,
        "audio": f"media/audio/{chapter_id_for(node)}.mp3",
        "interrupt_context": f"当前在讲：{title.get('zh', node.get('id', ''))}",
        "subtitles": [{"t": 0.0, "text": title.get("zh", "")}],
        "timeline": events,
    }


def build_chapters_from_flow(flow, registries, used_event_ids):
    chapters = []
    root_procedures = flow.get("procedures", [])
    for proc in root_procedures:
        if has_pipeline(proc):
            sections = collect_leaf_sections(proc)
            for sec in sections:
                chapters.append(build_chapter(sec, registries, used_event_ids))
        else:
            # 顶层独立 action / trigger 流程（如璀璨宝石的购买、保留、补牌）。
            # 它们没有 <ontology::pipeline>，但内容藏在 <ontology::content> 的 options 里。
            leaves = collect_action_leaves(proc)
            if len(leaves) > 1 or leaves[0] is not proc or detect_action(proc) != "wait" or proc.get("source") or proc.get("destination") or proc.get("subject"):
                chapters.append(build_chapter_from_leaves(proc, leaves, registries, used_event_ids))
    return chapters


def build_tutorial(game_id, flow):
    registries = {
        "slot": SlotRegistry(),
        "sprite": SpriteRegistry(),
    }
    used_event_ids = set()
    chapters = build_chapters_from_flow(flow, registries, used_event_ids)

    meta = flow.get("meta", {})
    name = meta.get("name", {})
    title = {"zh": name.get("zh", game_id)}
    if name.get("en"):
        title["en"] = name["en"]

    doc = {
        "meta": {
            "game_id": game_id,
            "version": "0.1.auto",
            "title": title,
            "locale": "zh",
            "default_easing": "easeOutCubic",
            "default_highlight_color": "#FFD54F",
            "generator": "flow_to_tutorial.py",
        },
        "board": {
            "image": "media/board/board.png",
            "width_mm": 360.0,
            "height_mm": 260.0,
            "origin": "top_left",
            "pixels_per_unit": 100.0,
            "comment": "自动生成占位：请替换为实际版图扫描图，并填写真实 width_mm/height_mm",
        },
        "slots": registries["slot"].items(),
        "sprites": registries["sprite"].items(),
        "chapters": chapters,
    }
    registries["slot"].assign_grid()
    return doc


def main():
    parser = argparse.ArgumentParser(description="Compile flow.json into a tutorial.json draft")
    parser.add_argument("--game", required=True)
    parser.add_argument("--out", help="output path (default: games/{game}/tutorial.json)")
    parser.add_argument("--stdout", action="store_true", help="print JSON to stdout instead of writing")
    args = parser.parse_args()

    game_dir = ROOT / "games" / args.game
    flow_path = game_dir / "flow.json"
    if not flow_path.exists():
        print(f"flow.json not found: {flow_path}", file=sys.stderr)
        sys.exit(2)

    flow = load_json(flow_path)
    doc = build_tutorial(args.game, flow)

    out_path = Path(args.out) if args.out else game_dir / "tutorial.json"

    if args.stdout:
        print(json.dumps(doc, ensure_ascii=False, indent=2))
        return

    out_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out_path}")

    # 自动跑一次结构校验（不检查素材，因为素材本来就是占位）。
    validate_cmd = [
        sys.executable,
        str(ROOT / "scripts" / "validate_tutorial.py"),
        "--tutorial",
        str(out_path),
        "--skip-assets",
        "--json",
    ]
    proc = subprocess.run(validate_cmd, capture_output=True, text=True)
    print(proc.stdout.strip())
    if proc.returncode != 0:
        print(proc.stderr.strip(), file=sys.stderr)
        sys.exit(proc.returncode)


if __name__ == "__main__":
    main()
