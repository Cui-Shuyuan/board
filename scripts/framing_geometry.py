#!/usr/bin/env python3
"""取景几何：**引擎 `TutorialCueAnimPlayer.FitCamera` 的 Python 镜像**。

为什么要单独一个模块：这份几何有两个用户 —— 对账（`check_cue_script.py` 判"取景里
有没有契约没提到的组件"）和静态校验（`validate_cue_anim.py` 判"切取景之后才清场"）。
抄成三份必然漂移，而**一旦漂移，这些检查就会开始撒谎**（比没有检查更糟）。
所以只留这一份，两个工具都从这里 import；改引擎的取景数学时只改这里。

镜像了这四件事（与 FitCamera 的分支一一对应）：
  1. 取景目标框：多 zone / `supply` / `cards` / 单个 zone（各自不同的留白倍率）
  2. `orthoSize = max(halfH*sin(pitch), halfW/aspect, 0.6)` 与横向兜底
  3. 可见**地面矩形**：半宽 = `orthoSize*aspect`，半高 = `orthoSize/sin(pitch)`
  4. 格位坐标 `_slot_at`（row / block / grid / stack 台阶）—— 与 `ZoneStore.ComputeZonePosition` 同源

**不知道的就返回 None**（例如整桌取景 `board` = 不限制），绝不猜一个框出来。
"""

import math


CAMERA_TOKENS = {"board", "cards", "supply"}
SUPPLY_PALETTE = "panel_supply"
CARDS_ZONES = ("showcase_1", "showcase_2", "showcase_3")
CARD_HALF = (0.315, 0.44)


def resolve_zone_ref(stage, ref):
    """zone 引用 → 具体 zone id。

    **与引擎 `ZoneStore.ResolveZoneRef` 互为镜像**（同一套规则，两个用户：几何与校验）：
      "gem_supply_diamond"                ← 直接写 id（老写法，仍支持）
      "<gem_supply|color=<diamond>>"      ← 写"哪个概念的哪一份"（推荐，可复用）
      "<card_market>"                     ← 只有概念、没有属性

    0 个匹配（写错了）或多个匹配（说不清，例如只写 `<gem_supply>`）→ **原样返回**，
    让下游的"未知 zone"/"说不清"检查去报 —— 这里不猜。
    """
    if not ref:
        return ref
    s = str(ref).strip()
    if len(s) < 3 or not s.startswith("<") or not s.endswith(">"):
        return s
    body = s[1:-1]
    segs = body.split("|")
    concept = _norm_concept(segs[0])
    want = []
    for seg in segs[1:]:
        if "=" in seg:
            k, v = seg.split("=", 1)
            want.append((k.strip(), v.strip()))
    hits = _zone_ref_hits(stage, concept, want)
    return hits[0] if len(hits) == 1 else s


def _zone_ref_hits(stage, concept, want):
    hits = []
    for z in (stage.get("zones") or []):
        if not z or not z.get("id"):
            continue
        if _norm_concept(z.get("concept")) != concept:
            continue
        have = {(p.get("key"), p.get("value")) for p in (z.get("parts") or []) if isinstance(p, dict)}
        if all(w in have for w in want):
            hits.append(z["id"])
    return hits


def zone_ref_hint(stage, ref):
    """引用没解析出来时，给一句"到底怎么了"（没有匹配 / 说不清是哪一份）。

    为什么值得单独一句：`<gem_supply>` 这种写法本身没错，**只是不够具体**（五个颜色堆都匹配）。
    只说"未知 zone"会让人以为名字拼错了，而实际要说的是"把属性写全"。
    """
    s = str(ref or "").strip()
    if len(s) < 3 or not s.startswith("<") or not s.endswith(">"):
        return ""
    segs = s[1:-1].split("|")
    concept = _norm_concept(segs[0])
    want = []
    for seg in segs[1:]:
        if "=" in seg:
            k, v = seg.split("=", 1)
            want.append((k.strip(), v.strip()))
    hits = _zone_ref_hits(stage, concept, want)
    if not hits:
        return "（没有 zone 的 concept/属性 匹配它 —— 概念名或属性值写错了？）"
    if len(hits) > 1:
        return f"（匹配到 {len(hits)} 个区域 {hits} —— 说不清是哪一份，请把属性写全）"
    return ""


def _norm_concept(c):
    return (c or "").strip().strip("<>") if c else ""


def zone_by_ref(stage, ref):
    """引用 → stage 里的 zone 字典（找不到 → None）。"""
    zid = resolve_zone_ref(stage, ref)
    for z in (stage.get("zones") or []):
        if z.get("id") == zid:
            return z
    return None


def _slot_at(stage, zone, order):
    """格位坐标。镜像 ZoneStore.ComputeZonePosition（row/block/grid + stack 台阶）。"""
    center = zone.get("center") or {}
    x = float(center.get("x") or 0.0)
    z = float(center.get("z") or 0.0)
    layout = zone.get("layout") or {}
    display = zone.get("display") or {}
    capacity = int(zone.get("capacity") or 12)
    slot, overflow = order, 0
    if slot >= capacity:
        slot, overflow = capacity - 1, order - capacity + 1

    if display.get("mode") == "stack":
        max_visible = int(display.get("max_visible") or 8)
        lift = min(max(0, capacity - 1 - slot), max_visible - 1)
        return (x + lift * float(display.get("dx") or 0.0),
                z + lift * float(display.get("dz") or 0.0))

    x_step = float(layout.get("x_step") or 0.0)
    z_step = float(layout.get("z_step") or 0.0)
    kind = layout.get("type")
    if kind == "row":
        x += (slot - (capacity - 1) * 0.5) * x_step
    elif kind == "block":
        cols = max(1, -(-capacity // 2))
        row, col = slot // cols, slot % cols
        in_row = min(cols, capacity - row * cols)
        x += (col - (in_row - 1) * 0.5) * x_step
        z += (row - 0.5) * z_step
    else:
        cols = max(1, int(layout.get("cols") or 1))
        row, col = slot // cols, slot % cols
        x += (col - (cols - 1) * 0.5) * x_step
        z += row * z_step
    if overflow > 0:
        x += overflow * x_step * 0.10
        z += overflow * z_step * 0.10
    return (x, z)


def _zone_box(stage, zone, count=None):
    """一个 zone 的（外接）范围：按它自己占的格位算，不按容量 —— 空的格子不该撑大画面。"""
    n = count if count is not None else max(1, int(zone.get("capacity") or 1))
    size = zone.get("size") or {}
    hw = float(size.get("w") or 0.2) / 2
    hh = float(size.get("h") or 0.2) / 2
    box = None
    for i in range(max(1, n)):
        x, z = _slot_at(stage, zone, i)
        b = (x - hw, x + hw, z - hh, z + hh)
        box = b if box is None else (min(box[0], b[0]), max(box[1], b[1]),
                                     min(box[2], b[2]), max(box[3], b[3]))
    return box


def _union(boxes):
    boxes = [b for b in boxes if b]
    if not boxes:
        return None
    return (min(b[0] for b in boxes), max(b[1] for b in boxes),
            min(b[2] for b in boxes), max(b[3] for b in boxes))


def _fill_scale(fill, fallback):
    """camera_fill → orthoScale，与引擎 FitCamera 的 `1/Clamp(fill,0.2,1)` 一致。"""
    if fill and fill > 0:
        return 1.0 / max(0.2, min(1.0, float(fill)))
    return fallback


def frame_bounds(stage, camera, padding=0.0, fill=0.0):
    """camera → (取景目标框, orthoScale)。None = 不限制（整桌取景）。

    与 FitCamera 的分支一一对应：多 zone / supply / cards / 单个 zone。
    """
    if not camera or camera in ("board",):
        return None
    zones = {z.get("id"): z for z in stage.get("zones", [])}
    parts = [p.strip() for p in str(camera).split(",") if p.strip()]
    scale = 2.2

    zid = {p: resolve_zone_ref(stage, p) for p in parts}
    if len(parts) > 1:
        scale = _fill_scale(fill, 1.25)
        box = _union([_zone_box(stage, zones[zid[p]]) for p in parts if zid[p] in zones])
    elif parts[0] == "cards":
        scale = 1.5
        box = _union([_zone_box(stage, zones[z]) for z in CARDS_ZONES if z in zones])
        # FitCamera 对 cards 用的是写死的半个卡宽/卡高
        box = (box[0] - CARD_HALF[0], box[1] + CARD_HALF[0],
               box[2] - CARD_HALF[1], box[3] + CARD_HALF[1]) if box else None
    elif parts[0] == "supply":
        scale = 1.25
        box = _union([_zone_box(stage, z) for z in zones.values()
                      if z.get("palette") == SUPPLY_PALETTE and z.get("role") != "offstage"])
    elif zid[parts[0]] in zones:
        box = _zone_box(stage, zones[zid[parts[0]]])
        # 单 zone 特写：优先 camera_fill，其次 camera_padding，最后默认 2.2
        scale = _fill_scale(fill, padding if padding > 0 else 2.2)
    else:
        return None
    if not box:
        return None
    if parts[0] in ("supply", "cards"):
        # supply/cards 引擎仍用 camera_padding，不用 fill
        scale = padding if padding > 0 else scale
    return (box, scale)


def visible_rect(stage, camera, padding=0.0, fill=0.0):
    """取景 → 画面覆盖的地面矩形 (minX, maxX, minZ, maxZ)。镜像 FitCamera 后半段。"""
    got = frame_bounds(stage, camera, padding, fill)
    if got is None:
        return None
    (min_x, max_x, min_z, max_z), scale = got
    board = stage.get("board") or {}
    pitch = float(board.get("camera_pitch") or 90.0)
    aspect = float(board.get("aspect") or 1.7778)
    cx, cz = (min_x + max_x) * 0.5, (min_z + max_z) * 0.5
    half_w = max(0.5, (max_x - min_x) * 0.5 * scale)
    half_h = max(0.5, (max_z - min_z) * 0.5 * scale)
    sin_p = max(0.15, math.sin(math.radians(pitch)))
    ortho = max(half_h * sin_p, half_w / aspect, 0.6)
    if ortho * aspect < half_w:
        ortho = half_w / aspect
    return (cx - ortho * aspect, cx + ortho * aspect, cz - ortho / sin_p, cz + ortho / sin_p)


def overlaps(a, b):
    """两个矩形（minX, maxX, minZ, maxZ）相交吗。任一个为 None → False。"""
    if not a or not b:
        return False
    return a[0] <= b[1] and b[0] <= a[1] and a[2] <= b[3] and b[2] <= a[3]


def _in_rect(rect, x, z, hw=0.02, hh=0.02):
    """中心在框内**或**组件本体压到框边 —— 判据是"这块像素会不会露在画面里"。

    只看中心会把"卡片下沿露在画面顶部"判成没入镜（用户看到的恰恰是那一条边）。
    """
    return rect and (rect[0] - hw <= x <= rect[1] + hw and rect[2] - hh <= z <= rect[3] + hh)
