#!/usr/bin/env python3
"""
把 slot_layouts.json 展开成 tutorial.json 里的 slots。

设计动机：规整的卡位/板块位不应靠人一个个点。它们本质是 grid / row / ring
这类规律布局，只需要在 slot_layouts.json 里写「原点 + 间距 + 数量」，
脚本保证生成出的坐标绝对整齐；要调整也只改一个参数后重新生成。

slot_layouts.json 放在 games/{game}/slot_layouts.json：

    {
      "game_id": "splendor",
      "layouts": [
        {
          "type": "grid",
          "id_prefix": "market",
          "origin": {"x": 0.62, "y": 0.25},
          "step_x": 0.07,
          "step_y": 0.10,
          "rows": 3,
          "cols": 4,
          "start_index": 0,
          "order": "row_major",
          "label": {"zh": "市场卡位"}
        },
        {
          "type": "row",
          "id_prefix": "noble",
          "origin": {"x": 0.70, "y": 0.10},
          "step_x": 0.08,
          "count": 5,
          "label": {"zh": "贵族板块位"}
        }
      ],
      "slots": [
        {"id": "game_box", "x": -0.15, "y": 0.5, "label": {"zh": "游戏盒"}}
      ]
    }

用法：
    python scripts/expand_slot_layouts.py --game splendor
    python scripts/expand_slot_layouts.py --game splendor --tutorial games/splendor/tutorial.json
"""

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def expand_grid(layout):
    prefix = layout["id_prefix"]
    origin = layout["origin"]
    step_x = layout.get("step_x", 0.0)
    step_y = layout.get("step_y", 0.0)
    rows = int(layout.get("rows", 1))
    cols = int(layout.get("cols", 1))
    start_index = int(layout.get("start_index", 0))
    order = layout.get("order", "row_major")
    z = float(layout.get("z", 0.0))
    label = layout.get("label")

    slots = []
    for r in range(rows):
        for c in range(cols):
            if order == "column_major":
                idx = start_index + c * rows + r
            else:
                idx = start_index + r * cols + c
            sid = f"{prefix}_{idx}"
            slots.append({
                "id": sid,
                "x": round(origin["x"] + c * step_x, 4),
                "y": round(origin["y"] + r * step_y, 4),
                "z": z,
                **({"label": label} if label else {}),
            })
    return slots


def expand_row(layout):
    prefix = layout["id_prefix"]
    origin = layout["origin"]
    step_x = layout.get("step_x", 0.0)
    step_y = layout.get("step_y", 0.0)
    count = int(layout.get("count", 0))
    start_index = int(layout.get("start_index", 0))
    z = float(layout.get("z", 0.0))
    label = layout.get("label")

    slots = []
    for i in range(count):
        sid = f"{prefix}_{i + start_index}"
        slots.append({
            "id": sid,
            "x": round(origin["x"] + i * step_x, 4),
            "y": round(origin["y"] + i * step_y, 4),
            "z": z,
            **({"label": label} if label else {}),
        })
    return slots


def expand_ring(layout):
    prefix = layout["id_prefix"]
    center = layout["center"]
    radius = float(layout.get("radius", 0.2))
    count = int(layout.get("count", 0))
    start_angle_deg = float(layout.get("start_angle_deg", -90.0))
    clockwise = bool(layout.get("clockwise", True))
    z = float(layout.get("z", 0.0))
    label = layout.get("label")

    slots = []
    sign = 1.0 if clockwise else -1.0
    for i in range(count):
        ang = math.radians(start_angle_deg + sign * i * 360.0 / count)
        slots.append({
            "id": f"{prefix}_{i}",
            "x": round(center["x"] + radius * math.cos(ang), 4),
            "y": round(center["y"] + radius * math.sin(ang), 4),
            "z": z,
            **({"label": label} if label else {}),
        })
    return slots


def expand_layout(layout):
    ltype = layout.get("type")
    if ltype == "grid":
        return expand_grid(layout)
    if ltype == "row":
        return expand_row(layout)
    if ltype == "ring":
        return expand_ring(layout)
    raise ValueError(f"unknown layout type: {ltype}")


def main():
    parser = argparse.ArgumentParser(description="Expand slot_layouts.json into tutorial.json slots")
    parser.add_argument("--game", required=True)
    parser.add_argument("--layout", help="slot_layouts.json path (default: games/{game}/slot_layouts.json)")
    parser.add_argument("--tutorial", help="tutorial.json path (default: games/{game}/tutorial.json)")
    args = parser.parse_args()

    game_dir = ROOT / "games" / args.game
    layout_path = Path(args.layout) if args.layout else game_dir / "slot_layouts.json"
    tutorial_path = Path(args.tutorial) if args.tutorial else game_dir / "tutorial.json"

    if not layout_path.exists():
        print(f"slot_layouts.json not found: {layout_path}", file=sys.stderr)
        sys.exit(2)
    if not tutorial_path.exists():
        print(f"tutorial.json not found: {tutorial_path}", file=sys.stderr)
        sys.exit(2)

    with open(layout_path, "r", encoding="utf-8") as f:
        layout_doc = json.load(f)
    with open(tutorial_path, "r", encoding="utf-8") as f:
        tutorial_doc = json.load(f)

    slots = []
    seen = set()

    for layout in layout_doc.get("layouts", []):
        for slot in expand_layout(layout):
            if slot["id"] in seen:
                print(f"warning: duplicate slot id '{slot['id']}', ignored", file=sys.stderr)
                continue
            seen.add(slot["id"])
            slots.append(slot)

    for slot in layout_doc.get("slots", []):
        if slot.get("id") in seen:
            print(f"warning: duplicate slot id '{slot['id']}' in manual slots, ignored", file=sys.stderr)
            continue
        seen.add(slot.get("id"))
        slots.append(slot)

    tutorial_doc["slots"] = slots
    out_text = json.dumps(tutorial_doc, ensure_ascii=False, indent=2) + "\n"
    tutorial_path.write_text(out_text, encoding="utf-8")
    print(f"expanded {len(slots)} slots into {tutorial_path}")

    # 自动校验（跳过素材检查）。
    validate_cmd = [
        sys.executable,
        str(ROOT / "scripts" / "validate_tutorial.py"),
        "--tutorial",
        str(tutorial_path),
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
