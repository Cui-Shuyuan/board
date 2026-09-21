#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Single geometry source for the v2 animation pipeline.

Everything spatial is derived here and written into `*.compiled.json`:
  * zone -> slot table (order -> x,z)
  * camera spec -> center / ortho_size / pitch / visible rect

The Unity runtime must not duplicate these formulas.  It only applies the
compiled values.
"""
from __future__ import annotations

import math


def num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def zone_capacity(zone: dict) -> int:
    layout = zone.get("layout") or {}
    cap = layout.get("capacity", zone.get("capacity", 0))
    try:
        return max(0, int(cap))
    except (TypeError, ValueError):
        return 0


def zone_center(zone: dict) -> tuple[float, float]:
    c = zone.get("center") or {}
    return num(c.get("x")), num(c.get("z"))


def slot_at(zone: dict, order: int) -> tuple[float, float]:
    """Canonical slot formula for both compiler and validator."""
    cap = max(1, zone_capacity(zone))
    layout = zone.get("layout") or {}
    display = zone.get("display") or {}
    cx, cz = zone_center(zone)
    slot = int(order)
    overflow = 0
    if slot >= cap:
        slot, overflow = cap - 1, slot - cap + 1

    if display.get("mode") == "stack":
        max_visible = max(1, int(display.get("max_visible", 8) or 8))
        lift = min(max(0, cap - 1 - slot), max_visible - 1)
        return (cx + lift * num(display.get("dx"), 0.03),
                cz + lift * num(display.get("dz"), 0.03))

    x_step = num(layout.get("x_step"), 0.0)
    z_step = num(layout.get("z_step"), 0.0)
    kind = layout.get("type") or "grid"
    if kind == "row":
        x = cx + (slot - (cap - 1) * 0.5) * x_step
        z = cz
    elif kind == "block":
        cols = max(1, (cap + 1) // 2)
        row, col = divmod(slot, cols)
        in_row = min(cols, cap - row * cols)
        x = cx + (col - (in_row - 1) * 0.5) * x_step
        z = cz + (row - 0.5) * z_step
    else:
        cols = max(1, int(layout.get("cols", 4) or 4))
        row, col = divmod(slot, cols)
        x = cx + (col - (cols - 1) * 0.5) * x_step
        z = cz + row * z_step

    if overflow > 0:
        x += overflow * x_step * 0.10
        z += overflow * z_step * 0.10
    return x, z


def build_slots(stage: dict) -> dict:
    out = {}
    for zone in stage.get("zones") or []:
        if not isinstance(zone, dict) or not zone.get("id"):
            continue
        cap = zone_capacity(zone)
        slots = []
        for order in range(cap):
            x, z = slot_at(zone, order)
            slots.append({"order": order, "x": round(x, 6), "z": round(z, 6)})
        out[zone["id"]] = slots
    return out


def zone_size(zone: dict) -> tuple[float, float]:
    size = zone.get("size") or {}
    return max(0.01, num(size.get("w"), 0.2)), max(0.01, num(size.get("h"), 0.2))


def zone_box(zone: dict, count: int) -> tuple[float, float, float, float] | None:
    if not zone:
        return None
    cap = zone_capacity(zone)
    n = max(1, count if count > 0 else cap)
    hw, hh = zone_size(zone)
    hw *= 0.5
    hh *= 0.5
    boxes = []
    for order in range(n):
        x, z = slot_at(zone, order)
        boxes.append((x - hw, x + hw, z - hh, z + hh))
    if not boxes:
        x, z = zone_center(zone)
        boxes.append((x - hw, x + hw, z - hh, z + hh))
    return (min(b[0] for b in boxes), max(b[1] for b in boxes),
            min(b[2] for b in boxes), max(b[3] for b in boxes))


def union(boxes):
    boxes = [b for b in boxes if b]
    if not boxes:
        return None
    return (min(b[0] for b in boxes), max(b[1] for b in boxes),
            min(b[2] for b in boxes), max(b[3] for b in boxes))


def build_camera_frame(stage: dict, camera: dict) -> dict:
    """Compile a camera declaration into runtime values.

    `camera = {"zones": [...], "fill": 0.8, "at": 0.0}`.
    Tokens `board` / empty mean the whole stage extent.
    """
    board = stage.get("board") or stage or {}
    pitch = num(board.get("camera_pitch", board.get("pitch")), 90.0) or 90.0
    aspect = num(board.get("aspect"), 1.7778) or 1.7778
    extent = board.get("extent") or stage.get("extent") or {}
    fill = num((camera or {}).get("fill"), 0.8) or 0.8
    zones = [z for z in ((camera or {}).get("zones") or []) if z]
    zones_by_id = {z.get("id"): z for z in (stage.get("zones") or []) if isinstance(z, dict) and z.get("id")}

    if not zones or zones == ["board"]:
        if extent:
            box = (num(extent.get("min_x")), num(extent.get("max_x")),
                   num(extent.get("min_z")), num(extent.get("max_z")))
        else:
            box = (-1.5, 1.5, -1.0, 1.4)
        scale = 1.18
    else:
        boxes = []
        for zid in zones:
            z = zones_by_id.get(zid)
            if z is None:
                continue
            boxes.append(zone_box(z, zone_capacity(z)))
        box = union(boxes)
        if box is None:
            box = (-1.5, 1.5, -1.0, 1.4)
        scale = 1.0 / clamp(fill, 0.2, 1.0)

    min_x, max_x, min_z, max_z = box
    cx = (min_x + max_x) * 0.5
    cz = (min_z + max_z) * 0.5
    half_w = max(0.5, (max_x - min_x) * 0.5 * scale)
    half_h = max(0.5, (max_z - min_z) * 0.5 * scale)
    sin_p = max(0.15, math.sin(math.radians(pitch)))
    ortho = max(half_h * sin_p, half_w / aspect, 0.6)
    if ortho * aspect < half_w:
        ortho = half_w / aspect
    return {
        "at": num((camera or {}).get("at"), 0.0),
        "center_x": round(cx, 6),
        "center_z": round(cz, 6),
        "ortho_size": round(ortho, 6),
        "pitch": round(pitch, 6),
        "rect_min_x": round(cx - ortho * aspect, 6),
        "rect_max_x": round(cx + ortho * aspect, 6),
        "rect_min_z": round(cz - ortho / sin_p, 6),
        "rect_max_z": round(cz + ortho / sin_p, 6),
    }


def build_compiled_stage(stage: dict) -> dict:
    templates = []
    for t in stage.get("templates") or []:
        if not isinstance(t, dict) or not t.get("id"):
            continue
        ws = t.get("world_size")
        if isinstance(ws, dict):
            width = num(ws.get("w"), num(t.get("width"), 0.2))
            height = num(ws.get("h"), num(t.get("height"), 0.2))
            world_size = max(width, height)
        else:
            world_size = num(ws, num(t.get("world_size"), 0.2))
            width = num(t.get("width"), world_size)
            height = num(t.get("height"), world_size)
        templates.append({
            "id": t["id"],
            "shape": t.get("shape", "card"),
            "palette": t.get("palette", ""),
            "face_image": t.get("face_image", ""),
            "back_image": t.get("back_image", ""),
            "width": round(width, 6),
            "height": round(height, 6),
            "world_size": round(world_size, 6),
            "alpha": num(t.get("alpha"), 1.0),
            "rotation": num(t.get("rotation"), 0.0),
            "sorting_order": int(t.get("sorting_order", 0) or 0),
        })
    board = stage.get("board") or stage or {}
    zone_display = {}
    for z in stage.get("zones") or []:
        if not isinstance(z, dict) or not z.get("id"):
            continue
        zone_display[z["id"]] = ((z.get("display") or {}).get("mode") or "")
    return {
        "schema": "tutorial-stage-compiled/v2",
        "game": stage.get("game", ""),
        "stage": stage.get("id") or stage.get("stage") or "",
        "pitch": round(num(board.get("camera_pitch", board.get("pitch")), 90.0) or 90.0, 6),
        "aspect": round(num(board.get("aspect"), 1.7778) or 1.7778, 6),
        "background": board.get("background", "#1E2126"),
        "zones": [{"zone": zid, "display": zone_display.get(zid, ""), "slots": slots}
                  for zid, slots in build_slots(stage).items()],
        "templates": templates,
    }
