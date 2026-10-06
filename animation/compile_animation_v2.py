#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic v2 animation compiler.

Source:  {track}.anim.json + its stage files
Output:  {track}.compiled.json

The compiler is the canonical state simulator and geometry consumer:
  * validates source schema;
  * resolves selectors into concrete component ids;
  * builds cue entry/exit snapshots (runtime jumps are pure lookups);
  * converts state changes + presentation events into concrete clips;
  * writes slot tables and camera frames from the single geometry module.

Usage:
    python3 animation/compile_animation_v2.py --game splendor --track _schema_example
    python3 animation/compile_animation_v2.py --source path/to/foo.anim.json --check
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))

import anim_geometry_v2 as geom  # noqa: E402
import anim_schema_v2 as schema  # noqa: E402


# Shuffle feel: all decks share these constants.  Kept in the compiler so the
# compiled clip carries deterministic per-item parameters and the Unity runtime
# does not invent motion on its own.
SHUFFLE_AMP_MIN = 0.040
SHUFFLE_AMP_MAX = 0.062
SHUFFLE_FREQ_MIN = 8.0
SHUFFLE_FREQ_MAX = 14.0
SHUFFLE_DEPTH_MIN = 0.25
SHUFFLE_DEPTH_MAX = 0.60
SHUFFLE_ENVELOPE_POWER = 0.45

# 多枚宝石的默认逐枚间隔：视觉上能分辨，但整体保持紧凑。
# 事件显式写 stagger 时仍以事件为准。
DEFAULT_GEM_STAGGER = 0.12


def is_gem_item(item: dict) -> bool:
    """Return True for gem/gold tokens, not development cards."""
    if not isinstance(item, dict):
        return False
    concept = norm(item.get("concept")).lower()
    template = norm(item.get("template")).lower()
    palette = norm(item.get("palette")).lower()
    if concept in ("gem", "gold") or template == "gem" or palette.startswith("gem_"):
        return True
    for part in item.get("parts") or []:
        if isinstance(part, dict) and norm(part.get("key")).lower() == "color":
            return True
    return False


# ── helpers ────────────────────────────────────────────────────────────────

def norm(s) -> str:
    return (str(s or "")).strip()


def norm_text(s) -> str:
    """Whitespace-insensitive text used only for beat -> subtitle matching."""
    return "".join((str(s or "")).split())


def key_text(s) -> str:
    """Punctuation/whitespace-insensitive key for beat -> TTS word matching."""
    return "".join(ch for ch in str(s or "") if ch.isalnum())


def face_int(v) -> int:
    if v in ("up", "face_up", 2, "2"):
        return 2
    if v in ("down", "face_down", 1, "1"):
        return 1
    if v in ("hidden", 0, "0"):
        return 0
    return 2


def face_name(v: int) -> str:
    return {2: "face_up", 1: "face_down", 0: "hidden"}.get(v, "face_up")


def parts_norm(parts):
    out = []
    for p in parts or []:
        if isinstance(p, dict) and p.get("key") is not None:
            out.append({"key": p.get("key"), "value": p.get("value")})
    return out


def selector_from_event(ev: dict) -> dict:
    return {
        "template": norm(ev.get("template")),
        "palette": norm(ev.get("palette")),
        "concept": norm(ev.get("concept")),
        "parts": parts_norm(ev.get("parts")),
    }


# Annotation anchor maps.  A "part" names a semantic corner of a card/token;
# the compiler writes it as a normalized (u, v) position in the target rect so
# Unity does not need to know card layout.  (0,0) is top-left in a GUI/screen
# rect and +z is "up" on the stage plane.
# Canonical development-card geometry.  Every development card template uses
# the same 0.63 x 0.88 card face, and its `part_anchors` define these positions
# in card-local units (dx, dy, r).  Normalized to the card rect:
#   u = 0.5 + dx / width, v = 0.5 - dy / height
#   part_w = 2*r / width, part_h = 2*r / height
CARD_PART_ANCHORS = {
    "whole": (0.5, 0.5),
    "": (0.5, 0.5),
    "prestige": (0.149733, 0.094258),
    # Cost is a vertical stack of one colored disc per gem type (1-4 types).
    # A single fixed anchor cannot cover the stack, so the four layouts get
    # distinct semantic part ids; new data should write cost_1..cost_4.
    # "cost" remains only a legacy compatibility alias.
    "cost": (0.126984, 0.840909),
    "cost_1": (0.1125, 0.920),
    "cost_2": (0.1125, 0.845),
    "cost_3": (0.1125, 0.770),
    "cost_4": (0.1125, 0.700),
    "bonus": (0.825397, 0.130682),
    "condition": (0.50, 0.84),
    # Noble-specific parts.  The noble face is square; its condition row
    # grows horizontally with the number of required gem kinds (2 or 3).
    "noble_prestige": (0.1417, 0.1417),
    "condition_2": (0.515, 0.840),
    "condition_3": (0.525, 0.840),
}
CARD_PART_SIZES = {
    "prestige": (0.238095, 0.170455),
    "cost": (0.5, 0.30),
    # Box extents measured from the true-card scans.  The column is always
    # left-aligned; only the vertical extent grows with the number of discs.
    # The left/top/bottom edges deliberately project a little past the card
    # face; only the right edge stays fixed at 0.27.
    "cost_1": (0.315, 0.22),
    "cost_2": (0.315, 0.37),
    "cost_3": (0.315, 0.52),
    "cost_4": (0.315, 0.66),
    "bonus": (0.269841, 0.193182),
    "noble_prestige": (0.44, 0.44),
    "condition_2": (0.53, 0.28),
    "condition_3": (0.81, 0.28),
}
SHAPE_KINDS = {"arrow", "circle", "cross", "forbid", "box"}


def annotation_space_of(ev: dict) -> str:
    """Return the annotation coordinate space: ``world`` or ``screen``.

    New source data may write an explicit top-level ``space`` (world/screen).
    Older data still derives it from ``target.space`` (entity/screen);
    ``_normalize_event`` stores that as ``annotation_space`` for us.
    """
    space = norm(ev.get("annotation_space")).lower()
    if space in ("world", "screen"):
        return space
    return "screen" if norm(ev.get("space")).lower() == "screen" else "world"


def part_uv(ev: dict) -> tuple[float, float]:
    pu = ev.get("part_u")
    pv = ev.get("part_v")
    if pu is not None or pv is not None:
        return (float(pu) if pu is not None else 0.5,
                float(pv) if pv is not None else 0.5)
    part = norm(ev.get("part")).lower()
    return CARD_PART_ANCHORS.get(part, (0.5, 0.5))


def nudge_xy(ev: dict) -> tuple[float, float]:
    """Screen-space nudge applied after the annotation anchor is projected.

    The event's historical scalar ``offset`` remains a *time* offset.  To keep
    the authoring surface simple, a mapping-valued ``offset``/``nudge`` is a
    screen-space nudge (x right / y down, in viewport fractions).
    """
    for key in ("nudge", "marker_offset", "offset"):
        raw = ev.get(key)
        if isinstance(raw, dict):
            return (float(raw.get("x", 0.0) or 0.0),
                    float(raw.get("y", 0.0) or 0.0))
    return (0.0, 0.0)


def find_overlay_rect(stage: dict | None, overlay_id: str) -> dict | None:
    for o in (stage or {}).get("overlays") or []:
        if isinstance(o, dict) and o.get("id") == overlay_id:
            return o.get("rect") if isinstance(o.get("rect"), dict) else None
    return None


def _annotation_style(ev: dict, defaults: dict | None = None) -> dict:
    """Resolve annotation visual parameters for one clip.

    Track-level ``annotation_style`` supplies defaults; event-level ``style``
    or flat keys override them.  Numeric values are pixels at a 1080p reference
    so scripts stay readable across screen sizes.
    """
    merged = dict(defaults or {})
    style = ev.get("style")
    if isinstance(style, dict):
        merged.update(style)
    for key in ("color", "stroke", "size", "gap"):
        if ev.get(key) is not None:
            merged[key] = ev.get(key)
    out = {}
    color = merged.get("color")
    if isinstance(color, str) and color.strip():
        out["annotation_color"] = color.strip()
    for key, field in (("stroke", "annotation_stroke"),
                       ("size", "annotation_size"),
                       ("gap", "annotation_gap")):
        value = merged.get(key)
        if value is None:
            continue
        try:
            number = float(value)
        except (TypeError, ValueError):
            continue
        if number > 0:
            out[field] = number
    return out


def event_annotation_fields(ev: dict, stage: dict | None = None,
                            style_defaults: dict | None = None) -> dict:
    """Compiled clip fields shared by every annotation primitive.

    ``annotation_space`` is the explicit authoring answer to "is this anchored
    to the table (world) or to the viewport/mask (screen)?".
    """
    u, v = part_uv(ev)
    nx, ny = nudge_xy(ev)
    out = {
        "annotation_space": annotation_space_of(ev),
        "part": norm(ev.get("part")),
        "part_u": u,
        "part_v": v,
        "has_part_uv": True,
        "nudge_x": nx,
        "nudge_y": ny,
    }
    out.update(_annotation_style(ev, style_defaults))
    # Semantic sub-rect around the part anchor.  Known development-card parts
    # get their fixed size from the canonical card geometry; an explicit
    # event value always wins.  For shape=box this is the rectangle; for
    # point/circle/arrow it is the fixed marker size around the anchor.
    part = out["part"].lower()
    default_size = CARD_PART_SIZES.get(part)
    for key, default in (("part_w", default_size[0] if default_size else None),
                         ("part_h", default_size[1] if default_size else None)):
        value = ev.get(key)
        if value is not None:
            out[key] = float(value)
        elif default is not None:
            out[key] = float(default)
    overlay_id = norm(ev.get("overlay"))
    if out["annotation_space"] == "screen" and overlay_id:
        rect = find_overlay_rect(stage, overlay_id)
        if rect is not None:
            out.update({
                "screen_x": float(rect.get("x", 0.0) or 0.0),
                "screen_y": float(rect.get("y", 0.0) or 0.0),
                "screen_w": float(rect.get("w", 0.0) or 0.0),
                "screen_h": float(rect.get("h", 0.0) or 0.0),
            })
    return out


class StateModel:
    """Small pure simulator used only at compile time.

    It is deliberately not a runtime dependency: the compiled cue carries full
    start/end snapshots, so the Unity runtime never resolves selectors.
    """

    def __init__(self, stack_zones=None, zone_order_policies=None):
        self.items = []
        self.next_seq = {}
        # Pile convention: order 0 is the bottom (first card laid down), the
        # largest occupied order is the top (next card drawn).  New cards go on
        # top; drawing removes the largest order and never renumbers the pile.
        self.stack_zones = set(stack_zones or [])
        # color_stack zones address items by (color, rank): different colors
        # occupy different base slots, same color stacks in its own slot.
        self.zone_order_policies = dict(zone_order_policies or {})

    def snapshot(self) -> dict:
        comps = []
        for it in sorted(self.items, key=lambda x: (x["order"], x["id"])):
            comps.append({
                "Id": it["id"],
                "TemplateId": it["template"],
                "Palette": it["palette"],
                "Concept": it["concept"],
                "parts": copy.deepcopy(it.get("parts") or []),
                "ZoneId": it["zone"],
                "Order": int(it["order"]),
                "Layer": int(it.get("layer", 0) or 0),
                "Face": int(it["face"]),
            })
        return {"components": comps,
                "nextSeq": [{"key": k, "value": v} for k, v in sorted(self.next_seq.items())]}

    def component_map(self) -> dict:
        """id -> full concrete component dict (matches ComponentState)."""
        out = {}
        for it in self.items:
            out[it["id"]] = {
                "Id": it["id"],
                "TemplateId": it["template"],
                "Palette": it["palette"],
                "Concept": it["concept"],
                "parts": copy.deepcopy(it.get("parts") or []),
                "ZoneId": it["zone"],
                "Order": int(it["order"]),
                "Layer": int(it.get("layer", 0) or 0),
                "Face": int(it["face"]),
            }
        return out

    def load_snapshot(self, snap: dict):
        """Initialize from a compiled StateSnapshot (cue entry state)."""
        self.items = []
        self.next_seq = {}
        for comp in (snap or {}).get("components") or []:
            self.items.append({
                "id": comp.get("Id"),
                "template": comp.get("TemplateId"),
                "palette": comp.get("Palette"),
                "concept": comp.get("Concept"),
                "parts": copy.deepcopy(comp.get("parts") or []),
                "zone": comp.get("ZoneId"),
                "order": int(comp.get("Order", 0) or 0),
                "layer": int(comp.get("Layer", 0) or 0),
                "face": int(comp.get("Face", 2) or 2),
            })
        for kv in (snap or {}).get("nextSeq") or []:
            self.next_seq[kv.get("key")] = int(kv.get("value", 0) or 0)
        return self

    def matching(self, zone: str, selector: dict) -> list:
        out = []
        for it in self.items:
            if zone and it["zone"] != zone:
                continue
            if selector:
                if selector.get("template") and it["template"] != selector["template"]:
                    continue
                if selector.get("palette") and it["palette"] != selector["palette"]:
                    continue
                if selector.get("concept") and it["concept"] != selector["concept"]:
                    continue
                if selector.get("parts"):
                    have = {(p.get("key"), p.get("value")) for p in (it.get("parts") or [])}
                    if not all((p.get("key"), p.get("value")) in have for p in selector["parts"]):
                        continue
            out.append(it)
        # Stable address order: scripts address concrete items by (order, id);
        # layer only controls cover/overlap order, not selection identity.
        out.sort(key=lambda x: (x["order"], x["id"]))
        return out

    def count(self, zone: str, selector: dict = None) -> int:
        return len(self.matching(zone, selector or {}))

    @staticmethod
    def _color_key(it: dict) -> str:
        # Gems use parts[color]; development cards use parts[bonus].
        for p in it.get("parts") or []:
            key = str(p.get("key", "")).strip().lower()
            if key in ("color", "bonus"):
                return str(p.get("value", "")).strip("<>")
        pal = str(it.get("palette") or "")
        if pal == "gem_gold":
            return "gold"
        if pal.startswith("gem_"):
            return pal[4:]
        return ""

    def _next_order(self, zone: str, item: dict = None) -> int:
        """Default append position, with optional per-zone color grouping.

        Removal leaves holes; only an explicit move_order event may close them.
        """
        policy = self.zone_order_policies.get(zone)
        if policy and item is not None:
            key = self._color_key(item)
            colors = list(policy.get("colors") or [])
            if key and key in colors:
                per = max(1, int(policy.get("per_color_capacity", 4) or 4))
                idx = colors.index(key)
                used = sum(1 for x in self.items
                           if x["zone"] == zone and self._color_key(x) == key)
                return idx * per + used
        orders = [i["order"] for i in self.items if i["zone"] == zone]
        return (max(orders) + 1) if orders else 0

    def _zone_layers(self, zone: str) -> list:
        return [int(i.get("layer", 0) or 0) for i in self.items if i["zone"] == zone]

    def _default_layer(self, zone: str) -> int:
        """Layer for a new item entering this zone.

        New items always go on top: max(existing layer) + 1.  Stack zones use
        the same direction for order (bottom-to-top), so drawing removes the
        largest order without renumbering the pile.
        """
        layers = self._zone_layers(zone)
        return max(layers) + 1 if layers else 0

    def _resolve_layer(self, zone: str, raw, ordinal: int = 0) -> int:
        if raw is None or str(raw).strip() == "":
            return self._default_layer(zone)
        if isinstance(raw, bool):
            raise ValueError(f"layer must be int/top/bottom, got {raw!r}")
        if isinstance(raw, (int, float)):
            return int(raw) + ordinal
        text = str(raw).strip().lower()
        if text in ("top",):
            layers = self._zone_layers(zone)
            return max(layers) + 1 if layers else 0
        if text in ("default",):
            return self._default_layer(zone)
        if text in ("bottom", "below"):
            layers = self._zone_layers(zone)
            return min(layers) - 1 if layers else 0
        try:
            return int(text) + ordinal
        except ValueError:
            raise ValueError(f"layer must be int/top/bottom, got {raw!r}") from None

    def spawn(self, template: str, palette: str, concept: str, zone: str, count: int,
              face: int = 2, parts=None, layer=None) -> list:
        if count <= 0:
            return []
        if not zone:
            raise ValueError("spawn zone required")
        added = []
        for idx in range(count):
            key = f"{template}|{palette}"
            seq = self.next_seq.get(key, 0) + 1
            self.next_seq[key] = seq
            it = {
                "id": f"{key}#{seq}",
                "template": template,
                "palette": palette,
                "concept": concept,
                "parts": copy.deepcopy(parts or []),
                "zone": zone,
                "order": 0,
                "layer": self._resolve_layer(zone, layer, idx),
                "face": int(face),
            }
            it["order"] = self._next_order(zone, it)
            self.items.append(it)
            added.append(it)
        return added

    def ensure_at_least(self, template: str, palette: str, concept: str, zone: str, count: int,
                        face: int = 2, parts=None, layer=None) -> list:
        sel = {"template": template, "palette": palette, "concept": concept, "parts": parts or []}
        have = self.count(zone, sel)
        return self.spawn(template, palette, concept, zone, max(0, count - have), face, parts, layer)

    def destroy(self, zone: str, selector: dict, count: int, from_back: bool = False) -> list:
        arr = self.matching(zone, selector)
        if count <= 0:
            count = len(arr)
        if len(arr) < count:
            raise ValueError(f"destroy needs {count} in {zone}, have {len(arr)}")
        victims = arr[-count:] if from_back else arr[:count]
        ids = {v["id"] for v in victims}
        self.items = [i for i in self.items if i["id"] not in ids]
        return victims

    def transfer(self, selector: dict, source: str, dest: str, quantity: int,
                 to_face=None, order: int = -1, layer=None,
                 from_top: bool = True, to_top: bool = True) -> list:
        """Move items between zones.

        ``from_top`` / ``to_top`` are optional; when omitted they default to
        ``True`` for visible pile movement:

        * from_top: take the ``quantity`` items with the largest ``order`` from
          the source pile (order is bottom-to-top, so the largest order is the
          top / next drawn card).
        * to_top: place the moved items at the top of the destination pile by
          assigning new orders/layers above the existing maximum.  Existing
          items are never renumbered, so holes left by earlier removals stay
          open.

        Grid / row zones (market, nobles, reserved cards...) have no inherent
        "top"; for those, the defaults preserve stable address order.  Pass an
        explicit ``False`` to disable the pile behavior for a transfer.

        ``order`` is only used for non-pile destinations: it is the explicit
        destination order/slot (for example refilling a market hole after a
        purchase).  It is not a source selector; deck draw picks the source
        stack top via ``from_top``.
        """
        # from_top/to_top are defaults for visible pile movement only.  Grid /
        # row zones (market, nobles, reserved...) have no inherent "top" and
        # must keep their stable address order.  color_stack zones already
        # append new gems into their own top slot via _next_order, so only pure
        # stack zones need to_top.
        source_is_pile = source in self.stack_zones or source in self.zone_order_policies
        dest_is_stack = dest in self.stack_zones
        use_from_top = bool(from_top) and source_is_pile
        use_to_top = bool(to_top) and dest_is_stack

        arr = self.matching(source, selector)
        if len(arr) < quantity:
            raise ValueError(f"transfer needs {quantity} from {source}, have {len(arr)}")
        if use_from_top:
            arr = sorted(arr, key=lambda x: (int(x["order"]), str(x["id"])))
            moved = list(reversed(arr[-quantity:]))
        else:
            moved = arr[:quantity]
        moved_ids = {m["id"] for m in moved}
        # Snapshot the destination before insertion, so to_top lands above the
        # original pile even when several records move together.
        dest_items_before = sorted(
            (it for it in self.items if it["zone"] == dest and it["id"] not in moved_ids),
            key=lambda x: (int(x.get("order", 0) or 0), str(x.get("id", ""))),
        )
        records = []
        for it in moved:
            rec = {"item": it, "from_zone": it["zone"], "from_order": it["order"],
                   "from_layer": int(it.get("layer", 0) or 0)}
            records.append(rec)
        self.items = [i for i in self.items if i["id"] not in moved_ids]
        for idx, rec in enumerate(records):
            it = rec["item"]
            it["zone"] = dest
            it["order"] = self._next_order(dest, it)
            it["layer"] = self._resolve_layer(dest, layer, idx)
            if to_face is not None:
                it["face"] = face_int(to_face)
            self.items.append(it)
            rec["to_zone"] = dest
            rec["to_order"] = it["order"]
            rec["to_layer"] = int(it["layer"])
        if use_to_top:
            # Keep records in source top-to-bottom order so records[0] (the
            # item taken from the source top) also ends up on top of the
            # destination; existing items are never renumbered.
            base_order = max((int(it.get("order", 0) or 0) for it in dest_items_before), default=-1) + 1
            base_layer = max((int(it.get("layer", 0) or 0) for it in dest_items_before), default=-1) + 1
            count = len(records)
            for idx, rec in enumerate(records):
                lift = count - 1 - idx
                it = rec["item"]
                it["order"] = base_order + lift
                it["layer"] = base_layer + lift
                rec["to_order"] = int(it["order"])
                rec["to_layer"] = int(it["layer"])
        if order >= 0 and moved and not use_to_top:
            # Explicit destination slot, e.g. refilling a market hole.
            self.move_order(moved[0], dest, order)
            for rec in records:
                rec["to_order"] = rec["item"]["order"]
        return records

    def move_order(self, item: dict, zone: str, order: int):
        """Renumber one item to an explicit slot inside a non-pile zone.

        Never use this to compact a deck: stack draw uses the largest order and
        deliberately leaves holes in the remaining cards.
        """
        arr = sorted([i for i in self.items if i["zone"] == zone], key=lambda x: (x["order"], x["id"]))
        arr = [i for i in arr if i["id"] != item["id"]]
        at = max(0, min(order, len(arr)))
        arr.insert(at, item)
        self.items = [i for i in self.items if i["zone"] != zone]
        self.items.extend(arr)
        for i, it in enumerate(arr):
            it["order"] = i

    def set_face(self, selector: dict, zone: str, face: int):
        for it in self.matching(zone, selector):
            it["face"] = int(face)


def fnv32(s: str, seed: int = 0) -> int:
    h = (2166136261 ^ (seed & 0xFFFFFFFF)) & 0xFFFFFFFF
    for ch in s:
        h ^= ord(ch)
        h = (h * 16777619) & 0xFFFFFFFF
    return h


def hash01(s: str, salt: int) -> float:
    """Deterministic [0,1) value; replay and compiled clips always agree."""
    return fnv32(s, salt) / 4294967296.0


# ── compiler ────────────────────────────────────────────────────────────────

class Compiler:
    def __init__(self, track_path: Path):
        self.track_path = track_path
        self.track_dir = track_path.parent
        self.doc = schema.resolve_track(json.loads(track_path.read_text(encoding="utf-8")))
        self.annotation_style = self.doc.get("annotation_style")
        if not isinstance(self.annotation_style, dict):
            self.annotation_style = {}
        self.rep = schema.validate_track(self.doc)
        self.stages = {}       # stage id -> source stage
        self.compiled_stages = {}
        self.stage_path_to_id = {}
        self.tree_stage_ids = {}
        self.trees = {}
        self.stack_zones = set()
        self.zone_order_policies = {}
        self.zone_bindings = {}   # physical zone id -> logical mapping
        self.cue_order = {
            str(c.get("id")): i
            for i, c in enumerate(self.doc.get("cues") or [])
            if isinstance(c, dict) and c.get("id")
        }
        self.prev_cue = {}
        ordered = [c for c in (self.doc.get("cues") or []) if isinstance(c, dict) and c.get("id")]
        for i in range(1, len(ordered)):
            self.prev_cue[str(ordered[i].get("id"))] = str(ordered[i - 1].get("id"))
        self.time_anchors = self._resolve_time_anchors()

    def _resolve_time_anchors(self) -> dict:
        """Resolve symbolic time anchors to cue-local seconds.

        Anchor definitions live at the top of the track script, next to worlds.
        Times are resolved from script beats + TTS subtitles, never from raw
        seconds written at the event site.
        """
        anchors = self.doc.get("time_anchors") or []
        if not anchors:
            return {}
        game = norm(self.doc.get("game"))
        track = norm(self.doc.get("track"))
        script_path = ROOT / "content" / "games" / game / "tutorial" / f"script.{track}.json"
        runtime_path = ROOT / "content" / "games" / game / "tutorial" / f"{track}.runtime.json"
        if not script_path.exists():
            raise FileNotFoundError(f"time_anchors: missing narration script {script_path}")
        if not runtime_path.exists():
            raise FileNotFoundError(f"time_anchors: missing runtime timing {runtime_path}")

        script_doc = json.loads(script_path.read_text(encoding="utf-8"))
        runtime_doc = json.loads(runtime_path.read_text(encoding="utf-8"))
        self.beat_times = self._build_beat_times(script_doc, runtime_doc)
        runtime_cues = {c.get("id"): c for c in (runtime_doc.get("cues") or []) if c.get("id")}

        out = {}
        for a in anchors:
            if not isinstance(a, dict) or not a.get("id"):
                raise ValueError(f"time_anchors: anchor needs id: {a!r}")
            aid = norm(a.get("id"))
            cue_id = norm(a.get("cue"))
            edge = norm(a.get("edge")) or "start"
            offset = float(a.get("offset", 0.0) or 0.0)
            runtime_cue = runtime_cues.get(cue_id)
            if runtime_cue is None:
                raise ValueError(f"time_anchor {aid!r}: unknown cue {cue_id!r}")
            if edge == "cue_start":
                base = 0.0
            elif edge == "cue_end":
                base = float(runtime_cue.get("duration", 0.0) or 0.0)
            else:
                beat_id = norm(a.get("beat"))
                beat_time = self.beat_times.get((cue_id, beat_id))
                if beat_time is None:
                    raise ValueError(f"time_anchor {aid!r}: unknown beat {cue_id}/{beat_id}")
                base = beat_time[0] if edge == "start" else beat_time[1]
            out[aid] = max(0.0, base + offset)
        return out

    @staticmethod
    def _build_beat_times(script_doc: dict, runtime_doc: dict) -> dict:
        """(cue_id, beat_id) -> (start_seconds, end_seconds).

        TTS subtitle events may split one written beat into several pieces, so we
        match against the flattened word stream instead of requiring an exact
        subtitle-event equality.
        """
        runtime_cues = {c.get("id"): c for c in (runtime_doc.get("cues") or []) if c.get("id")}
        out = {}
        for c in script_doc.get("cues") or []:
            cid = c.get("id")
            rt = runtime_cues.get(cid)
            if not cid or not rt:
                continue
            words = []
            for sub in rt.get("subtitles") or []:
                for w in sub.get("words") or []:
                    if w:
                        words.append(w)
            if not words:
                continue
            flat = "".join(key_text(w.get("word", "")) for w in words)
            char_word = []
            for wi, w in enumerate(words):
                for _ in key_text(w.get("word", "")):
                    char_word.append(wi)
            cursor = 0
            for b in c.get("beats") or []:
                bid = b.get("id")
                bkey = key_text(b.get("text"))
                if not bid or not bkey:
                    continue
                pos = flat.find(bkey, cursor)
                if pos < 0:
                    pos = flat.find(bkey)
                if pos < 0:
                    raise ValueError(f"beat text not found in TTS words: {cid}/{bid} {b.get('text')!r}")
                si = char_word[pos]
                ei = char_word[pos + len(bkey) - 1]
                out[(cid, bid)] = (
                    float(words[si].get("start", 0.0) or 0.0),
                    float(words[ei].get("end", words[ei].get("start", 0.0)) or 0.0),
                )
                cursor = pos + len(bkey)
        return out

    def event_at(self, ev: dict, cue_id=None) -> float:
        aid = norm(ev.get("anchor"))
        if not aid:
            return round(float(ev.get("at", 0.0) or 0.0), 6)
        if aid not in self.time_anchors:
            raise ValueError(f"cue {cue_id}: unknown time anchor {aid!r}")
        raw_offset = ev.get("offset", 0.0) or 0.0
        # Mapping-valued offset is a screen-space annotation nudge; the event
        # still starts exactly at its anchor.
        offset = 0.0 if isinstance(raw_offset, dict) else float(raw_offset)
        return round(max(0.0, self.time_anchors[aid] + offset), 6)

    def resolve_stage_path(self, rel: str) -> Path:
        rel = norm(rel)
        if not rel:
            raise ValueError("stage path is empty")
        candidates = [
            self.track_dir / rel,
            self.track_dir / (rel + ".json"),
            self.track_dir / ".." / rel,
            self.track_dir / ".." / (rel + ".json"),
        ]
        for p in candidates:
            if p.exists():
                return p.resolve()
        raise FileNotFoundError(f"stage not found: {rel}")

    def _validate_stage_shot_zones(self, sid: str) -> None:
        stage = self.stages[sid]
        zones = {
            z.get("id") for z in (stage.get("zones") or [])
            if isinstance(z, dict) and z.get("id")
        }
        special = {"*", "all", "all_zones", "board"}
        for shot in stage.get("shots") or []:
            if not isinstance(shot, dict):
                continue
            for zone in shot.get("zones") or []:
                zone = norm(zone)
                if not zone or zone in special:
                    continue
                if zone not in zones:
                    raise ValueError(
                        f"stage {sid}: shot {shot.get('id')!r} references missing zone {zone!r}"
                    )

    def _register_stage_zones(self, sid: str, qa_ignore_by_zone: dict) -> None:
        stage = self.stages[sid]
        for z in stage.get("zones") or []:
            if not isinstance(z, dict):
                continue
            disp = z.get("display") or {}
            mode = disp.get("mode")
            zid = z.get("id")
            if not zid:
                continue
            qa_ignore = z.get("qa_ignore") is True
            previous_qa_ignore = qa_ignore_by_zone.get(zid)
            if previous_qa_ignore is not None and previous_qa_ignore != qa_ignore:
                raise ValueError(
                    f"zone qa_ignore conflict for {zid!r} in stage {sid!r}: "
                    f"{previous_qa_ignore!r} vs {qa_ignore!r}"
                )
            qa_ignore_by_zone[zid] = qa_ignore
            binding = geom.zone_binding(z)
            if binding is not None:
                previous = self.zone_bindings.get(zid)
                if previous is not None and previous != binding:
                    raise ValueError(
                        f"zone binding conflict for {zid!r} in stage {sid!r}: "
                        f"{previous!r} vs {binding!r}"
                    )
                self.zone_bindings[zid] = binding
            if mode == "stack":
                self.stack_zones.add(zid)
            elif mode == "color_stack":
                self.zone_order_policies[zid] = {
                    "colors": list(disp.get("colors") or []),
                    "per_color_capacity": int(disp.get("per_color_capacity", 4) or 4),
                }

    def _load_stage(self, rel: str, qa_ignore_by_zone: dict | None = None) -> str:
        rel = norm(rel)
        if not rel:
            raise ValueError("stage path is empty")
        path = self.resolve_stage_path(rel)
        key = str(path)
        cached = self.stage_path_to_id.get(key)
        if cached is not None:
            return cached
        stage = json.loads(path.read_text(encoding="utf-8"))
        sr = schema.validate_stage(stage)
        if not sr.ok():
            raise ValueError(f"stage schema errors in {path}:\n" + "\n".join(sr.errors))
        for warning in sr.warnings:
            self.rep.warn(f"{path}: {warning}")
        sid = stage.get("id") or path.stem
        if sid in self.stages and self.stages[sid] is not stage:
            raise ValueError(f"stage id {sid!r} is declared by more than one file")
        self.stages[sid] = stage
        self.compiled_stages[sid] = geom.build_compiled_stage(stage)
        self.stage_path_to_id[key] = sid
        if qa_ignore_by_zone is not None:
            self._register_stage_zones(sid, qa_ignore_by_zone)
        self._validate_stage_shot_zones(sid)
        return sid

    def find_asset_meta(self, template_id: str, palette: str) -> dict:
        """Resolve a template's display asset without requiring it in the
        current cue stage.  Screen-space presentation overlays may show a real
        card while the current tree renders a different stage; the card asset
        still has to be found from any loaded stage definition."""
        tpl_id = norm(template_id)
        pal = norm(palette)
        for stage in self.stages.values():
            for t in stage.get("templates") or []:
                if t.get("id") != tpl_id:
                    continue
                face = t.get("face_image") or ""
                back = t.get("back_image") or ""
                for m in t.get("face_image_by_palette") or []:
                    if not isinstance(m, dict):
                        continue
                    if norm(m.get("palette")) == pal:
                        face = m.get("face_image") or face
                        back = m.get("back_image") or back
                if not face:
                    raise ValueError(f"template {tpl_id!r} has no face_image for overlay")
                return {
                    "face_image": face,
                    "back_image": back,
                    "shape": norm(t.get("shape")) or "card",
                    "width": float(t.get("width", 0.0) or 0.0),
                    "height": float(t.get("height", 0.0) or 0.0),
                }
        raise ValueError(f"template {tpl_id!r} not found in any stage; cannot build overlay")

    def stage_id_for_cue(self, cue: dict, by_id: dict, cache: dict) -> str:
        cid = cue.get("id")
        if cid in cache:
            return cache[cid]
        rel = norm(cue.get("stage"))
        if rel:
            sid = self._load_stage(rel)
        else:
            sid = self.tree_stage_ids.get(str(cue.get("tree") or ""))
        if not sid or sid not in self.stages:
            raise ValueError(f"cue {cid}: no resolved stage for tree {cue.get('tree')!r}")
        cache[cid] = sid
        return sid

    def load(self):
        if not self.rep.ok():
            raise ValueError("schema errors:\n" + "\n".join(self.rep.errors))
        qa_ignore_by_zone: dict = {}
        for tree in self.doc.get("trees") or []:
            sid = self._load_stage(tree.get("stage"), qa_ignore_by_zone)
            self.tree_stage_ids[tree["id"]] = sid
            self.trees[tree["id"]] = {
                "id": tree["id"],
                "world": tree.get("world") or tree["id"],
                "stage": sid,
                "purpose": tree.get("purpose", ""),
                "initial": tree.get("initial", ""),
                "extent_note": tree.get("extent_note", ""),
            }
        # Cue-level stage overrides may reference additional stage resources.
        for cue in self.doc.get("cues") or []:
            if isinstance(cue, dict) and norm(cue.get("stage")):
                self._load_stage(cue.get("stage"), qa_ignore_by_zone)
        self._warn_display_zone_state_ops()

    def _warn_display_zone_state_ops(self):
        """Warn when a state event targets a zone that has no logical mapping."""
        state_ops = {"ensure", "create", "destroy", "transfer", "stack", "shuffle", "move_order", "set_face"}
        seen = set()
        for cue in self.doc.get("cues") or []:
            if not isinstance(cue, dict):
                continue
            tree = self.trees.get(cue.get("tree") or self.doc.get("default_tree"))
            if tree is None:
                continue
            stage = self.stages.get(tree.get("stage"))
            if stage is None:
                continue
            zones = {
                z.get("id"): z
                for z in (stage.get("zones") or [])
                if isinstance(z, dict) and z.get("id")
            }
            for ev in cue.get("events") or []:
                if not isinstance(ev, dict) or ev.get("op") not in state_ops:
                    continue
                refs = []
                for key in ("zone", "destination"):
                    value = ev.get(key)
                    if isinstance(value, str) and value.strip():
                        refs.append(value.strip())
                source = ev.get("source")
                if isinstance(source, str) and source.strip():
                    refs.append(source.strip())
                elif isinstance(source, list):
                    refs.extend(str(x).strip() for x in source if str(x).strip())
                for zid in refs:
                    zone = zones.get(zid)
                    if (zone is None
                            or zone.get("qa_ignore") is True
                            or str(zone.get("concept") or "").strip()):
                        continue
                    key = (cue.get("id"), zid)
                    if key in seen:
                        continue
                    seen.add(key)
                    self.rep.warn(
                        f"cue {cue.get('id')}: state op {ev.get('op')} uses display-only "
                        f"zone {zid!r} without concept; QA summary falls back to the raw id"
                    )

    def shot_frame(self, stage_id: str, shot_id: str, visible_zones: set | None = None) -> dict:
        stage = self.stages[stage_id]
        for sh in stage.get("shots") or []:
            if sh.get("id") == shot_id:
                zones = sh.get("zones") or []
                # "*" 全景：动态收窄到当前真的有组件的 zone；
                # 只有没有可见 zone 时才退回全部 zone。这样空玩家区不会
                # 把设置完成前的桌面中景硬拉成整桌远景。
                if "*" in zones and visible_zones and not sh.get("static"):
                    stage_ids = {z.get("id") for z in (stage.get("zones") or [])}
                    live = [z for z in visible_zones if z in stage_ids]
                    if live:
                        zones = live
                return geom.build_camera_frame(stage, {
                    "zones": zones,
                    "fill": sh.get("fill", 0.8),
                    "view_offset_x": sh.get("view_offset_x", 0.0),
                    "view_offset_z": sh.get("view_offset_z", 0.0),
                    "at": 0.0,
                })
        raise ValueError(f"stage {stage_id}: unknown shot {shot_id!r}")

    @staticmethod
    def visible_zone_set(state: StateModel) -> set:
        return {it.get("zone") for it in state.items if it.get("zone")}

    def default_camera_frame(self, stage_id: str) -> dict:
        stage = self.stages[stage_id]
        shots = stage.get("shots") or []
        if shots:
            return self.shot_frame(stage_id, shots[0]["id"])
        return geom.build_camera_frame(stage, {})

    def state_source_ref(self, cue: dict, tree: dict, by_id: dict) -> tuple:
        """Resolve the effective state-inheritance edge for one cue.

        ``tree`` selects stage/visibility, not a state partition:

        * ``entry`` is authoritative and may cross tree/world.
        * otherwise ``parent`` is the state source regardless of tree;
        * otherwise the previous cue in track order is the source;
        * explicit ``cut`` / ``world_cut`` without ``entry`` resets to initial.
        """
        cid = cue.get("id")
        entry = cue.get("entry")
        if entry:
            if entry == "initial":
                return ("initial", None)
            if entry not in by_id:
                raise ValueError(f"cue {cid}: entry source {entry!r} does not exist")
            return ("cue", entry)
        transition = cue.get("transition", "continue")
        if transition in ("cut", "world_cut"):
            return ("initial", None)
        parent_id = cue.get("parent")
        if parent_id:
            if parent_id not in by_id:
                raise ValueError(f"cue {cid}: parent {parent_id!r} does not exist")
            return ("cue", parent_id)
        prev_id = self.prev_cue.get(str(cid))
        if prev_id and prev_id in by_id:
            return ("cue", prev_id)
        return ("initial", None)

    def compile(self) -> dict:
        self.load()
        cues = self.doc.get("cues") or []
        by_id = {c.get("id"): c for c in cues if c.get("id")}
        results = {}
        visiting = set()
        stage_cache: dict = {}

        def evaluate(cid: str) -> dict:
            if cid in results:
                return results[cid]
            if cid in visiting:
                raise ValueError(f"cue entry cycle at {cid!r}")
            cue = by_id.get(cid)
            if cue is None:
                raise ValueError(f"unknown cue in entry graph: {cid!r}")
            tree = self.trees.get(cue.get("tree"))
            if tree is None:
                raise ValueError(f"cue {cid}: unknown tree {cue.get('tree')!r}")
            visiting.add(cid)
            transition = cue.get("transition", "continue")
            stage_id = self.stage_id_for_cue(cue, by_id, stage_cache)
            mode, entry_id = self.state_source_ref(cue, tree, by_id)
            if mode == "cue":
                entry_res = evaluate(entry_id)
                parent_decl = by_id.get(entry_id) or {}
                # 反例 cue 的错误状态不能泄漏：子节点继承它的 start_state。
                entry_state = copy.deepcopy(
                    entry_res["start_state"] if parent_decl.get("negative") else entry_res["end_state"]
                )
                entry_stage = entry_res.get("_stage")
                entry_camera_out = copy.deepcopy(entry_res.get("_camera_out"))
            else:
                entry_state = {"components": [], "nextSeq": []}
                entry_stage = None
                entry_camera_out = None

            state = StateModel(self.stack_zones, self.zone_order_policies).load_snapshot(entry_state)
            start = state.snapshot()
            clips, state_ops, camera_ops, first_state, pointer_resolution = self.compile_events(
                cue, state, tree, 0, stage_id
            )
            if first_state is None:
                first_state = start
            enter_pic = ((cue.get("script") or {}).get("enter") or {}).get("picture")
            if enter_pic is not None and not any(
                    c.get("kind") in ("picture", "show") and float(c.get("at", 0.0)) <= 1e-6
                    for c in clips):
                clips.insert(0, self.clip("picture", 0.0, 0.0, 0.0, "easeOutCubic",
                                          picture=enter_pic, picture_on=True))
            end = state.snapshot()
            if (mode == "cue" and transition not in ("cut", "world_cut")
                    and entry_stage == stage_id):
                camera_in = copy.deepcopy(entry_camera_out) or self.default_camera_frame(stage_id)
            else:
                camera_in = self.default_camera_frame(stage_id)
            camera_out = camera_ops[-1]["frame"] if camera_ops else camera_in
            result = {
                "id": cue.get("id"),
                "tree": cue.get("tree"),
                "transition": transition,
                "stage": stage_id,
                "demo": bool(cue.get("demo")),
                "duration": round(self.duration(cue, clips), 6),
                "camera_in": camera_in,
                "camera_ops": camera_ops,
                "state_ops": state_ops,
                "start_state": start,
                "first_state": first_state,
                "end_state": end,
                "clips": clips,
                "pointer_resolution": pointer_resolution,
                "_stage": stage_id,
                "_camera_out": camera_out,
            }
            results[cid] = result
            visiting.discard(cid)
            return result

        for cue in cues:
            if cue.get("id"):
                evaluate(cue["id"])

        cues_out = []
        for idx, cue in enumerate(cues):
            cid = cue.get("id")
            if cid not in results:
                continue
            result = {k: v for k, v in results[cid].items() if not k.startswith("_")}
            # `parent` is the authoring hierarchy; state source may also fall
            # back to the previous cue in track order (see state_source_ref).
            result["parent"] = cue.get("parent")
            # Keep the schema's canonical field order stable.
            ordered = {
                "id": result.pop("id"),
                "parent": result.pop("parent"),
                "tree": result.pop("tree"),
                "transition": result.pop("transition"),
                "stage": result.pop("stage"),
                "demo": result.pop("demo"),
                "duration": result.pop("duration"),
                "camera_in": result.pop("camera_in"),
                "camera_ops": result.pop("camera_ops"),
                "state_ops": result.pop("state_ops"),
                "start_state": result.pop("start_state"),
                "first_state": result.pop("first_state"),
                "end_state": result.pop("end_state"),
                "clips": result.pop("clips"),
                "pointer_resolution": result.pop("pointer_resolution"),
            }
            cues_out.append(ordered)

        src_bytes = self.track_path.read_bytes()
        return {
            "schema": schema.COMPILED_TRACK_SCHEMA,
            "source_sha256": hashlib.sha256(src_bytes).hexdigest(),
            "game": self.doc.get("game"),
            "track": self.doc.get("track"),
            "trees": list(self.trees.values()),
            "stages": list(self.compiled_stages.values()),
            "zone_bindings": self.zone_bindings,
            "cues": cues_out,
        }

    def duration(self, cue: dict, clips: list) -> float:
        end = 0.0
        for ev in cue.get("events") or []:
            at = self.event_at(ev, cue.get("id"))
            lead = float(ev.get("lead", 0.0) or 0.0)
            dur = float(ev.get("dur", 0.0) or 0.0)
            end = max(end, at + lead + dur)
        # Staggered transfers move the per-record start into clips[].at;
        # the cue duration must cover the actual node timeline.
        for cl in clips or []:
            at = float(cl.get("at", 0.0) or 0.0)
            lead = max(0.0, float(cl.get("lead", 0.0) or 0.0))
            dur = max(0.0, float(cl.get("dur", 0.0) or 0.0))
            end = max(end, at + lead + dur)
        return end

    def infer_meta(self, stage: dict, template: str, palette: str, concept: str = "", parts=None):
        """Fill concept/parts/palette from stage template metadata when omitted."""
        if concept and parts:
            return concept, parts, palette
        for t in stage.get("templates") or []:
            if t.get("id") != template:
                continue
            if not concept and t.get("concept"):
                concept = t.get("concept")
            if not parts and t.get("parts"):
                parts = t.get("parts")
            if not palette and t.get("palette"):
                palette = t.get("palette")
            if not concept and t.get("concept_by_palette"):
                for b in t.get("concept_by_palette") or []:
                    if b.get("palette") == palette:
                        concept = b.get("concept", concept)
                        parts = b.get("parts", parts)
                        break
        return concept, parts or [], palette

    @staticmethod
    def select_items(state: StateModel, zone: str, selector: dict, order, limit=None):
        arr = state.matching(zone, selector)
        if order is not None:
            arr = [it for it in arr if int(it.get("order", -1)) == int(order)]
        if limit is not None:
            arr = arr[:limit]
        return arr

    def compile_events(self, cue: dict, state: StateModel, tree: dict, idx: int,
                       stage_id: str | None = None) -> tuple:
        """Returns (clips, state_ops, camera_ops, first_state, pointer_resolution).

        * state_ops are concrete item-id puts/removes: the logical truth the
          runtime applies before its visual clips.
        * camera_ops are concrete compiled camera frames at their switch times.
        * first_state is the logical state after every op whose effective time
          is <= 0.
        * pointer_resolution records every source point/highlight resolution so
          the audit can compare source events one-by-one.
        """
        clips: list = []
        state_ops: list = []
        camera_ops: list = []
        first_state = None
        pointer_resolution: list = []
        stage_id = stage_id or tree["stage"]
        stage = self.stages[stage_id]
        stage_slots = {z["zone"]: z["slots"] for z in self.compiled_stages[stage_id]["zones"]}
        cue_id = cue.get("id")
        for event_index, ev in enumerate(cue.get("events") or []):
            op = ev.get("op")
            at = self.event_at(ev, cue_id)
            dur = float(ev.get("dur", 0.0) or 0.0)
            lead = float(ev.get("lead", 0.0) or 0.0)
            easing = ev.get("easing") or "easeOutCubic"
            sel = selector_from_event(ev)
            zone = norm(ev.get("zone"))
            before = state.component_map()
            manual_state_ops = None
            manual_state_item_ids = set()
            affected_ids = []
            forbid_at = None
            if op == "camera":
                shot_id = norm(ev.get("shot"))
                if not shot_id:
                    raise ValueError(f"cue {cue_id}: camera needs shot")
                frame = self.shot_frame(stage_id, shot_id, visible_zones=self.visible_zone_set(state))
                camera_ops.append({
                    "at": at + max(0.0, lead),
                    "dur": dur,
                    "easing": easing,
                    "shot": shot_id,
                    "frame": frame,
                })
            elif op == "magnifier":
                rect = ev.get("rect") or {}
                rx = float(rect.get("x", 0.58) or 0.58)
                ry = float(rect.get("y", 0.18) or 0.18)
                rw = float(rect.get("w", 0.36) or 0.36)
                rh = float(rect.get("h", 0.36) or 0.36)
                matched = state.matching(zone, sel)
                if not matched:
                    raise ValueError(f"cue {cue_id}: magnifier needs at least one target item")
                overlay_id = norm(ev.get("id") or ev.get("overlay") or "magnifier")
                pointer_resolution.append({
                    "event_index": event_index,
                    "op": op,
                    "object_space": "screen",
                    "overlay": overlay_id,
                    "zone": zone,
                    "order": ev.get("order"),
                    "matched_count": len(matched),
                    "item_ids": [it["id"] for it in matched],
                })
                xs = []
                zs = []
                for it in matched:
                    x, z = self.position(stage_slots, it["zone"], it["order"])
                    r = self.template_radius(stage_id, it["template"])
                    xs.extend([x - r, x + r])
                    zs.extend([z - r, z + r])
                pad = float(ev.get("padding", 0.12) or 0.12)
                minx, maxx = min(xs) - pad, max(xs) + pad
                minz, maxz = min(zs) - pad, max(zs) + pad
                shape = norm(ev.get("shape") or "circle").lower()
                if shape not in ("circle", "box"):
                    raise ValueError(f"cue {cue_id}: unknown magnifier shape {shape!r}")
                mask_mode = norm(ev.get("mask") or "items").lower()
                if mask_mode not in ("items", "full"):
                    raise ValueError(f"cue {cue_id}: unknown magnifier mask {mask_mode!r}")
                if shape == "circle":
                    # Circle always resolves to a square window; this also
                    # lets full mask draw an opaque table-coloured disc.
                    lens_aspect = 1.0
                else:
                    lens_aspect = (rw * 16.0) / max(0.001, rh * 9.0)
                half_w = (maxx - minx) * 0.5
                half_h = (maxz - minz) * 0.5
                zoom = float(ev.get("zoom", 1.2) or 1.2)
                center_x = (minx + maxx) * 0.5
                center_z = (minz + maxz) * 0.5
                ortho = max(0.32, half_h, half_w / max(0.2, lens_aspect)) / max(0.05, zoom)
                # Optional explicit world framing.  Cues that must keep the
                # exact same lens while their target set changes use this to
                # pin the same center/ortho as a sibling cue.
                if ev.get("view_center_x") is not None:
                    center_x = float(ev["view_center_x"])
                if ev.get("view_center_z") is not None:
                    center_z = float(ev["view_center_z"])
                if ev.get("view_ortho_size") is not None:
                    ortho = float(ev["view_ortho_size"])
                    if ortho <= 0.0:
                        raise ValueError(f"cue {cue_id}: view_ortho_size must be > 0")
                c = self.base_clip("magnifier_show", at, dur, lead, easing)
                c.update({
                    "object_space": "screen",
                    "overlay": overlay_id,
                    "mag_x": round(rx, 6), "mag_y": round(ry, 6),
                    "mag_w": round(rw, 6), "mag_h": round(rh, 6),
                    "mag_center_x": round(center_x, 6),
                    "mag_center_z": round(center_z, 6),
                    "mag_ortho_size": round(ortho, 6),
                    "mag_shape": shape,
                    "mag_mask": mask_mode,
                    "mag_item_ids": [it["id"] for it in matched],
                    "layer": int(ev.get("layer", 10) or 10),
                })
                clips.append(c)
            elif op == "show":
                if ev.get("space") == "entity":
                    for it in self.select_items(state, zone, sel, ev.get("order")):
                        clips.append(self.presentation_clip("fade", it, at, dur, lead, easing,
                                                            to_alpha=1.0))
                else:
                    clips.append(self.clip("picture", at, dur, lead, easing,
                                           picture=ev.get("picture"), picture_on=ev.get("picture") is not None))
            elif op == "hide":
                if ev.get("space") != "entity":
                    raise ValueError(f"cue {cue_id}: hide without screen target must use entity target")
                for it in self.select_items(state, zone, sel, ev.get("order")):
                    clips.append(self.presentation_clip("fade", it, at, dur, lead, easing,
                                                        to_alpha=0.0))
            elif op in ("create",):
                tpl = norm(ev.get("template"))
                pal = norm(ev.get("palette"))
                if not tpl:
                    raise ValueError(f"cue {cue_id}: create needs template")
                count = int(ev.get("count", 1) or 1)
                face = face_int(ev.get("to") or "face_down")
                concept, parts, pal = self.infer_meta(stage, tpl, pal, norm(ev.get("concept")), parts_norm(ev.get("parts")))
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts, ev.get("layer"))
                affected_ids = [it["id"] for it in added]
                if ev.get("slot") is not None:
                    base = int(ev.get("slot") or 0)
                    for off, it in enumerate(added):
                        state.move_order(it, zone, base + off)
                for it in added:
                    clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "ensure":
                tpl = norm(ev.get("template"))
                pal = norm(ev.get("palette"))
                if not tpl:
                    raise ValueError(f"cue {cue_id}: ensure needs template")
                count = int(ev.get("count", 1) or 1)
                face = face_int(ev.get("to"))
                concept, parts, pal = self.infer_meta(stage, tpl, pal, norm(ev.get("concept")), parts_norm(ev.get("parts")))
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts, ev.get("layer"))
                affected_ids = [it["id"] for it in added]
                for it in added:
                    clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "destroy":
                count = int(ev.get("count", 0) or 0)
                victims = state.destroy(zone, sel, count, from_back=bool(ev.get("from_back")))
                affected_ids = [it["id"] for it in victims]
                for it in victims:
                    clips.append(self.destroy_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "transfer":
                quantity = int(ev.get("quantity", ev.get("count", 1)) or 1)
                raw_src = ev.get("source")
                sources = raw_src if isinstance(raw_src, list) else [raw_src]
                sources = [norm(x) for x in sources if norm(x)]
                dest = norm(ev.get("destination"))
                is_setup = bool(ev.get("setup"))

                # 先收齐所有转移记录，再决定时间：
                # 非 setup 的多枚宝石默认逐枚短间隔飞出，避免整把同时位移。
                records = []
                for source in sources:
                    records.extend(state.transfer(
                        sel, source, dest, quantity, ev.get("to"),
                        int(ev.get("order", -1)), ev.get("layer"),
                        from_top=bool(ev.get("from_top", True)),
                        to_top=bool(ev.get("to_top", True)),
                    ))

                explicit_stagger = ev.get("stagger")
                if is_setup:
                    stagger = float(explicit_stagger or 0.0)
                elif explicit_stagger is not None:
                    stagger = float(explicit_stagger or 0.0)
                elif len(records) > 1 and all(is_gem_item(rec["item"]) for rec in records):
                    stagger = DEFAULT_GEM_STAGGER
                else:
                    stagger = 0.0

                records_with_times = []
                for index, rec in enumerate(records):
                    # 一个 transfer record = 一个节点：逻辑转移与视觉飞行共用同一个 at。
                    # setup premise 只改状态，不生成动作动画；它只在 cue 起点静默成立。
                    record_at = at + max(0.0, lead) + index * stagger
                    if not is_setup:
                        clips.append(self.move_clip(rec, record_at, dur, 0.0, easing, stage_slots, ev.get("to")))
                    records_with_times.append((record_at, rec["item"]["id"]))
                    manual_state_item_ids.add(rec["item"]["id"])

                if records_with_times:
                    after_map = state.component_map()
                    manual_state_ops = [
                        {"op": "put", "at": record_at, "item": after_map[item_id]}
                        for record_at, item_id in records_with_times
                        if item_id in after_map
                    ]
                    affected_ids = [item_id for _, item_id in records_with_times]
                    if ev.get("forbid"):
                        last_start = at + max(0.0, lead) + max(0, len(records_with_times) - 1) * stagger
                        forbid_at = last_start + max(0.0, dur)
            elif op == "stack":
                dest = norm(ev.get("destination"))
                capacity = int(ev.get("capacity", 40) or 40)
                real = [x.strip() for x in norm(ev.get("real_templates")).split(",") if x.strip()]
                pad = norm(ev.get("pad_template"))
                face = face_int(ev.get("to") or "face_down")
                pad_count = max(0, capacity - len(real)) if pad else 0
                # Deck and gem-supply piles share one construction path: both
                # are just ordered spawns into a display.mode=stack zone.
                # Order is bottom-to-top.  Lay the padding first (deep/bottom
                # orders), then lay real templates from the END of the list
                # backwards, so real_templates[0] ends up at the highest order
                # (top / first drawn), matching the authored draw order.
                if pad and pad_count:
                    concept, parts, pal = self.infer_meta(stage, pad, "")
                    for _ in range(pad_count):
                        added = state.spawn(pad, pal, concept, dest, 1, face, parts)
                        for it in added:
                            clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
                # Reverse the authored top-first list so the first template
                # (first to be drawn) is spawned last and receives the highest
                # order = the visible/current top.
                for tpl in reversed(real):
                    pal = norm(ev.get("palette"))
                    concept, parts, pal = self.infer_meta(stage, tpl, pal)
                    added = state.spawn(tpl, pal, concept, dest, 1, face, parts)
                    for it in added:
                        clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "shuffle":
                # Visual-only jitter.  `shuffle` means the deck is being mixed,
                # but the tutorial's draw order is already authored in
                # `real_templates` and must stay stable across seek/replay.
                # Permuting state here would desync end_state and scripted
                # selectors, so emit deterministic jitter and leave state alone.
                strength = float(ev.get("amount", ev.get("strength", 1.0)) or 1.0)
                for it in state.matching(zone, {}):
                    bx, bz = self.position(stage_slots, zone, it["order"])
                    r1 = hash01(it["id"], 1)
                    r2 = hash01(it["id"], 2)
                    r3 = hash01(it["id"], 3)
                    r4 = hash01(it["id"], 4)
                    amp = (SHUFFLE_AMP_MIN + (SHUFFLE_AMP_MAX - SHUFFLE_AMP_MIN) * r1) * strength
                    c = self.base_clip("shuffle", at, dur, lead, easing)
                    c.update({
                        "item_id": it["id"], "template": it["template"], "palette": it["palette"],
                        "from_zone": zone, "from_order": it["order"],
                        "to_zone": zone, "to_order": it["order"],
                        "from_x": bx, "from_z": bz, "to_x": bx, "to_z": bz,
                        "sh_amp": amp,
                        "sh_freq": SHUFFLE_FREQ_MIN + (SHUFFLE_FREQ_MAX - SHUFFLE_FREQ_MIN) * r2,
                        "sh_phase": r3 * 2.0 * math.pi,
                        "sh_zamp": amp * (SHUFFLE_DEPTH_MIN + (SHUFFLE_DEPTH_MAX - SHUFFLE_DEPTH_MIN) * r4),
                        "sh_env": SHUFFLE_ENVELOPE_POWER,
                    })
                    clips.append(c)
            elif op == "set_face":
                state.set_face(sel, zone, face_int(ev.get("to")))
                affected = state.matching(zone, sel)
                affected_ids = [it["id"] for it in affected]
                for it in affected:
                    if ev.get("flip"):
                        clips.append(self.flip_clip(it, at, dur, lead, easing, ev.get("to")))
                    else:
                        clips.append(self.face_clip(it, at, dur, lead, easing, ev.get("to")))
            elif op == "move_order":
                arr = state.matching(zone, sel)
                if arr:
                    state.move_order(arr[0], zone, int(ev.get("index", ev.get("order", 0)) or 0))
            elif op == "highlight":
                if ev.get("space") == "screen":
                    overlay_id = norm(ev.get("overlay"))
                    if not overlay_id:
                        raise ValueError(f"cue {cue_id}: screen highlight needs overlay id")
                    grow = float(ev.get("grow", 1.16) or 1.16)
                    clips.append(self.screen_presentation_clip(
                        "highlight", overlay_id, at, dur, lead, easing, to_scale=grow))
                    pointer_resolution.append({
                        "event_index": event_index,
                        "op": op,
                        "object_space": "screen",
                        "overlay": overlay_id,
                        "matched_count": 1,
                        "item_ids": [overlay_id],
                    })
                else:
                    matched = self.select_items(state, zone, sel, ev.get("order"))
                    item_ids = [it["id"] for it in matched]
                    pointer_resolution.append({
                        "event_index": event_index,
                        "op": op,
                        "object_space": "entity",
                        "zone": zone,
                        "order": ev.get("order"),
                        "matched_count": len(matched),
                        "item_ids": item_ids,
                    })
                    if not item_ids:
                        self.rep.warn(
                            f"unresolved pointer: cue={cue_id} event_index={event_index} op={op} "
                            f"zone={zone!r} order={ev.get('order')!r} "
                            f"anchor={ev.get('anchor')!r} offset={ev.get('offset')!r}"
                        )
                    for it in matched:
                        clips.append(self.presentation_clip("highlight", it, at, dur, lead, easing,
                                                            to_scale=float(ev.get("grow", 1.16) or 1.16)))
            elif op in ("point", "shape"):
                ann_space = annotation_space_of(ev)
                kind = norm(ev.get("indicator")) if op == "point" else (
                    norm(ev.get("shape")) or norm(ev.get("indicator")) or "circle")
                if op == "point" and not kind:
                    kind = "circle"
                if op == "shape" and kind not in SHAPE_KINDS:
                    raise ValueError(
                        f"cue {cue_id}: unknown shape {kind!r}; expected one of {sorted(SHAPE_KINDS)}")
                if ann_space == "screen":
                    overlay_id = norm(ev.get("overlay"))
                    if not overlay_id:
                        raise ValueError(f"cue {cue_id}: screen {op} needs overlay id")
                    c = self.screen_presentation_clip(
                        "point" if op == "point" else "shape",
                        overlay_id, at, dur, lead, easing,
                        part=norm(ev.get("part")), indicator=kind)
                    c.update(event_annotation_fields(ev, stage, self.annotation_style))
                    clips.append(c)
                    pointer_resolution.append({
                        "event_index": event_index,
                        "op": op,
                        "object_space": "screen",
                        "annotation_space": "screen",
                        "overlay": overlay_id,
                        "matched_count": 1,
                        "item_ids": [overlay_id],
                    })
                else:
                    matched = self.select_items(state, zone, sel, ev.get("order"))
                    selected = matched[:1]
                    item_ids = [it["id"] for it in selected]
                    pointer_resolution.append({
                        "event_index": event_index,
                        "op": op,
                        "object_space": "entity",
                        "annotation_space": "world",
                        "zone": zone,
                        "order": ev.get("order"),
                        "matched_count": len(matched),
                        "item_ids": item_ids,
                    })
                    if not item_ids:
                        self.rep.warn(
                            f"unresolved pointer: cue={cue_id} event_index={event_index} op={op} "
                            f"zone={zone!r} order={ev.get('order')!r} "
                            f"anchor={ev.get('anchor')!r} offset={ev.get('offset')!r}"
                        )
                    if selected:
                        c = self.presentation_clip(
                            "point" if op == "point" else "shape", selected[0],
                            at, dur, lead, easing,
                            part=norm(ev.get("part")), indicator=kind)
                        c.update(event_annotation_fields(ev, stage, self.annotation_style))
                        clips.append(c)
            elif op == "overlay_show":
                overlay_id = norm(ev.get("overlay"))
                if not overlay_id:
                    raise ValueError(f"cue {cue_id}: overlay_show needs overlay")
                tpl = norm(ev.get("template"))
                pal = norm(ev.get("palette"))
                image = norm(ev.get("image"))
                face_image = image
                back_image = ""
                if not face_image and tpl:
                    meta = self.find_asset_meta(tpl, pal)
                    face_image = meta["face_image"]
                    back_image = meta["back_image"]
                rect = ev.get("rect") if isinstance(ev.get("rect"), dict) else {}
                c = self.base_clip("overlay_show", at, dur, lead, easing)
                c.update({
                    "object_space": "screen",
                    "overlay": overlay_id,
                    "template": tpl,
                    "palette": pal,
                    "face_image": face_image,
                    "back_image": back_image,
                    "mask": norm(ev.get("mask")),
                    "background": norm(ev.get("background")),
                    "source_item_id": norm(ev.get("source_item_id")),
                    "persist_on_source_missing": bool(ev.get("persist_on_source_missing", True)),
                    "layer": int(ev.get("layer", 0) or 0),
                    "label_x": float(rect["x"]) if rect.get("x") is not None else 0.03,
                    "label_y": float(rect["y"]) if rect.get("y") is not None else 0.10,
                    "label_w": float(rect["w"]) if rect.get("w") is not None else 0.28,
                    "label_h": float(rect["h"]) if rect.get("h") is not None else 0.62,
                    "screen_space": True,
                    "from_alpha": 1.0,
                    "to_alpha": 1.0,
                })
                clips.append(c)
            elif op == "overlay_hide":
                overlay_id = norm(ev.get("overlay"))
                if not overlay_id:
                    raise ValueError(f"cue {cue_id}: overlay_hide needs overlay")
                c = self.base_clip("overlay_hide", at, dur, lead, easing)
                c["object_space"] = "screen"
                c["overlay"] = overlay_id
                clips.append(c)
            elif op == "label":
                overlay_id = norm(ev.get("overlay"))
                ann_space = annotation_space_of(ev)
                overlays = {o.get("id"): o for o in (stage.get("overlays") or [])
                            if isinstance(o, dict) and o.get("id")}
                if ann_space == "world" and not overlay_id:
                    # World label anchored to a concrete entity.  This is the
                    # "text follows the table card" case; the runtime resolves
                    # the current item each frame and projects it through the
                    # live camera, so camera moves keep the text attached.
                    matched = self.select_items(state, zone, sel, ev.get("order"))
                    selected = matched[:1]
                    if not selected:
                        self.rep.warn(
                            f"unresolved label: cue={cue_id} event_index={event_index} "
                            f"zone={zone!r} order={ev.get('order')!r} "
                            f"anchor={ev.get('anchor')!r} offset={ev.get('offset')!r}"
                        )
                        continue
                    c = self.presentation_clip("label", selected[0], at, dur, lead, easing)
                    c.update({
                        "text": str(ev.get("text") or ""),
                        "screen_space": False,
                        "label_x": 0.0, "label_y": 0.0,
                        "label_w": 0.0, "label_h": 0.0,
                    })
                    c.update(event_annotation_fields(ev, stage, self.annotation_style))
                    clips.append(c)
                elif overlay_id:
                    overlay = overlays.get(overlay_id)
                    if overlay is None:
                        raise ValueError(f"cue {cue_id}: unknown overlay {overlay_id!r}")
                    overlay_space = norm(overlay.get("space") or "screen").lower()
                    c = self.base_clip("label", at, dur, lead, easing)
                    c.update({
                        "overlay": overlay_id,
                        "text": str(ev.get("text") or ""),
                    })
                    if overlay_space == "world" or ann_space == "world":
                        center = overlay.get("center") or {}
                        c.update({
                            "annotation_space": "world",
                            "screen_space": False,
                            "world_x": float(center.get("x", 0.0) or 0.0),
                            "world_z": float(center.get("z", 0.0) or 0.0),
                            "nudge_x": nudge_xy(ev)[0],
                            "nudge_y": nudge_xy(ev)[1],
                        })
                    else:
                        rect = overlay.get("rect") or {}
                        c.update({
                            "annotation_space": "screen",
                            "screen_space": True,
                            "label_x": float(rect.get("x", 0.0) or 0.0),
                            "label_y": float(rect.get("y", 0.0) or 0.0),
                            "label_w": float(rect.get("w", 0.3) or 0.3),
                            "label_h": float(rect.get("h", 0.1) or 0.1),
                        })
                        c.update(event_annotation_fields(ev, stage, self.annotation_style))
                    clips.append(c)
                else:
                    raise ValueError(f"cue {cue_id}: label needs an entity target or overlay id")
            elif op == "fade":
                if ev.get("space") == "screen":
                    overlay_id = norm(ev.get("overlay"))
                    if not overlay_id:
                        raise ValueError(f"cue {cue_id}: screen fade needs overlay id")
                    clips.append(self.screen_presentation_clip(
                        "fade", overlay_id, at, dur, lead, easing,
                        to_alpha=float(ev.get("to_alpha", ev.get("alpha", 0.0)) or 0.0)))
                else:
                    for it in self.select_items(state, zone, sel, ev.get("order")):
                        clips.append(self.presentation_clip("fade", it, at, dur, lead, easing,
                                                            to_alpha=float(ev.get("to_alpha", ev.get("alpha", 0.0)) or 0.0)))
            elif op == "scale":
                if ev.get("space") == "screen":
                    overlay_id = norm(ev.get("overlay"))
                    if not overlay_id:
                        raise ValueError(f"cue {cue_id}: screen scale needs overlay id")
                    clips.append(self.screen_presentation_clip(
                        "scale", overlay_id, at, dur, lead, easing,
                        to_scale=float(ev.get("scale", 1.0) or 1.0)))
                else:
                    for it in self.select_items(state, zone, sel, ev.get("order")):
                        clips.append(self.presentation_clip("scale", it, at, dur, lead, easing,
                                                            to_scale=float(ev.get("scale", 1.0) or 1.0)))
            elif op == "wait":
                pass
            else:
                raise ValueError(f"cue {cue_id}: unsupported op {op!r}")

            # Effective time of this event's state step.  Spawn/create/move/
            # destroy logical changes happen when the visual action starts
            # (destroy with dur>0 leaves the item until the end).
            op_time = at + max(0.0, lead)
            if op == "destroy" and dur > 0:
                op_time += dur
            if op != "camera":
                after = state.component_map()
                handled_ids = manual_state_item_ids if manual_state_ops is not None else set()
                for iid, comp in after.items():
                    if iid in handled_ids:
                        continue
                    if before.get(iid) != comp:
                        state_ops.append({"op": "put", "at": op_time, "item": comp})
                for iid in sorted(set(before) - set(after)):
                    if iid in handled_ids:
                        continue
                    state_ops.append({"op": "remove", "at": op_time, "item_id": iid})
                if manual_state_ops:
                    state_ops.extend(manual_state_ops)
            if ev.get("forbid"):
                if forbid_at is None:
                    forbid_at = at + max(0.0, lead) + max(0.0, dur)
                indicator = ev.get("forbid") if isinstance(ev.get("forbid"), str) else "forbid"
                overlay_id = norm(ev.get("overlay"))
                if overlay_id:
                    overlays = {o.get("id"): o for o in (stage.get("overlays") or [])
                                if isinstance(o, dict) and o.get("id")}
                    overlay = overlays.get(overlay_id)
                    if overlay is None:
                        raise ValueError(f"cue {cue_id}: unknown overlay {overlay_id!r}")
                    if norm(overlay.get("space") or "screen").lower() != "world":
                        raise ValueError(
                            f"cue {cue_id}: forbid overlay {overlay_id!r} must be space='world' "
                            f"(screen-space markers are not supported yet)"
                        )
                    center = overlay.get("center") or {}
                    size = overlay.get("size") or {}
                    mx = float(center.get("x", 0.0) or 0.0)
                    mz = float(center.get("z", 0.0) or 0.0)
                    mr = max(float(size.get("w", 0.0) or 0.0), float(size.get("h", 0.0) or 0.0)) * 0.5
                    if mr <= 0.0:
                        mr = 0.25
                else:
                    mx, mz, mr = self.marker_geometry(stage_id, stage_slots, state, affected_ids)
                clips.append(self.marker_clip(forbid_at, indicator or "forbid", mx, mz, mr))
            if op_time <= 1e-9:
                first_state = state.snapshot()

        # Stable chronological order; camera ops are already naturally ordered
        # but explicit sorting keeps the runtime/evaluator independent of source
        # event ordering edge cases.
        # Stable chronological order.  Python's sort is stable, so same-time
        # ops keep source event order (important for conflicting ops).
        state_ops.sort(key=lambda x: x["at"])
        camera_ops.sort(key=lambda x: x["at"])
        return clips, state_ops, camera_ops, first_state, pointer_resolution

    # ── clip builders ─────────────────────────────────────────────────────
    def base_clip(self, kind, at, dur, lead, easing):
        return {
            "kind": kind, "at": at, "dur": dur, "lead": lead, "easing": easing,
            "object_space": "entity",
            "item_id": "", "template": "", "palette": "",
            "from_zone": "", "from_order": -1, "to_zone": "", "to_order": -1,
            "from_x": 0.0, "from_z": 0.0, "to_x": 0.0, "to_z": 0.0,
            "from_scale": 1.0, "to_scale": 1.0,
            "from_alpha": 1.0, "to_alpha": 1.0,
            "to_face": "", "part": "", "indicator": "",
            "picture": "", "picture_on": False,
            "sh_amp": 0.0, "sh_freq": 0.0, "sh_phase": 0.0, "sh_zamp": 0.0, "sh_env": 0.0,
        }

    def position(self, slots: dict, zone: str, order: int):
        for s in slots.get(zone, []):
            if int(s.get("order", -1)) == int(order):
                return float(s.get("x", 0.0)), float(s.get("z", 0.0))
        return 0.0, 0.0

    def spawn_clip(self, it, at, dur, lead, easing, slots):
        x, z = self.position(slots, it["zone"], it["order"])
        c = self.base_clip("spawn", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "to_zone": it["zone"], "to_order": it["order"],
            "from_zone": it["zone"], "from_order": it["order"],
            "from_x": x, "from_z": z, "to_x": x, "to_z": z,
            "to_face": face_name(it["face"]),
        })
        return c

    def destroy_clip(self, it, at, dur, lead, easing, slots):
        x, z = self.position(slots, it["zone"], it["order"])
        c = self.base_clip("destroy", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "from_zone": it["zone"], "from_order": it["order"], "to_zone": it["zone"], "to_order": it["order"],
            "from_x": x, "from_z": z, "to_x": x, "to_z": z,
        })
        return c

    def move_clip(self, rec, at, dur, lead, easing, slots, to_face):
        it = rec["item"]
        fx, fz = self.position(slots, rec["from_zone"], rec["from_order"])
        tx, tz = self.position(slots, rec["to_zone"], rec["to_order"])
        c = self.base_clip("move", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "from_zone": rec["from_zone"], "from_order": rec["from_order"],
            "to_zone": rec["to_zone"], "to_order": rec["to_order"],
            "from_x": fx, "from_z": fz, "to_x": tx, "to_z": tz,
        })
        if to_face:
            c["to_face"] = face_name(face_int(to_face))
        return c

    def template_radius(self, stage_id: str, template: str) -> float:
        stage = self.compiled_stages.get(stage_id) or {}
        for tpl in stage.get("templates") or []:
            if tpl.get("id") != template:
                continue
            w = float(tpl.get("width", 0.0) or 0.0)
            h = float(tpl.get("height", 0.0) or 0.0)
            if w > 0.0 and h > 0.0:
                return min(w, h) * 0.5
            ws = float(tpl.get("world_size", 0.0) or 0.0)
            if ws > 0.0:
                return ws * 0.5
        return 0.12

    def marker_geometry(self, stage_id: str, slots: dict, state: StateModel, item_ids: list) -> tuple:
        """Average position / bounding radius of the components affected by an action."""
        unique_ids = list(dict.fromkeys(item_ids))
        if not unique_ids:
            raise ValueError("forbid marker needs at least one affected item")
        by_id = {it["id"]: it for it in state.items}
        points = []
        for iid in unique_ids:
            it = by_id.get(iid)
            if it is None:
                raise ValueError(f"forbid marker: affected item {iid!r} no longer exists after action")
            x, z = self.position(slots, it["zone"], int(it["order"]))
            points.append((x, z, self.template_radius(stage_id, it["template"])))
        ax = sum(p[0] for p in points) / len(points)
        az = sum(p[1] for p in points) / len(points)
        spread = max(math.hypot(p[0] - ax, p[1] - az) for p in points)
        item_r = max(p[2] for p in points)
        return (round(ax, 6), round(az, 6), round(max(0.12, spread + item_r), 6))

    def marker_clip(self, at: float, indicator: str, x: float, z: float, radius: float):
        c = self.base_clip("marker", at, 0.0, 0.0, "linear")
        c.update({
            "indicator": indicator or "forbid",
            "marker_x": x,
            "marker_z": z,
            "marker_radius": radius,
            "annotation_space": "world",
            "part_u": 0.5,
            "part_v": 0.5,
            "has_part_uv": False,
            "nudge_x": 0.0,
            "nudge_y": 0.0,
        })
        return c

    def face_clip(self, it, at, dur, lead, easing, to_face):
        c = self.base_clip("face", at, dur, lead, easing)
        c.update({"item_id": it["id"], "template": it["template"], "palette": it["palette"],
                  "to_face": face_name(face_int(to_face))})
        return c

    def flip_clip(self, it, at, dur, lead, easing, to_face):
        """卡牌绕竖轴翻转：中点 scale-x=0，正面/背面都不可见，随后换成 to_face。"""
        c = self.base_clip("flip", at, dur, lead, easing)
        c.update({"item_id": it["id"], "template": it["template"], "palette": it["palette"],
                  "to_face": face_name(face_int(to_face))})
        return c

    def presentation_clip(self, kind, it, at, dur, lead, easing, **kw):
        c = self.base_clip(kind, at, dur, lead, easing)
        c.update({"item_id": it["id"], "template": it["template"], "palette": it["palette"]})
        for k, v in kw.items():
            if k in c:
                c[k] = v
        return c

    def screen_presentation_clip(self, kind, overlay_id, at, dur, lead, easing, **kw):
        c = self.base_clip(kind, at, dur, lead, easing)
        c.update({"object_space": "screen", "overlay": overlay_id})
        for k, v in kw.items():
            c[k] = v
        return c

    def clip(self, kind, at, dur, lead, easing, **kw):
        c = self.base_clip(kind, at, dur, lead, easing)
        for k, v in kw.items():
            if k in c:
                c[k] = v
        return c


def source_path(game: str, track: str, explicit: str | None = None) -> Path:
    if explicit:
        return Path(explicit)
    return ROOT / "content" / "games" / game / "tutorial" / "anim" / "v2" / f"{track}.anim.json"


def output_path(src: Path) -> Path:
    return src.with_name(src.name.replace(".anim.json", ".compiled.json"))


def json_canonical(doc) -> str:
    # Compiled assets are machine-read by Unity; keep them compact to avoid
    # multi-megabyte pretty-printed snapshots.
    return json.dumps(doc, ensure_ascii=False, separators=(",", ":"), sort_keys=False) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="_schema_example")
    ap.add_argument("--source")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--stdout", action="store_true")
    a = ap.parse_args()

    src = source_path(a.game, a.track, a.source)
    if not src.exists():
        print(f"source not found: {src}", file=sys.stderr)
        return 2
    try:
        c = Compiler(src)
        compiled = c.compile()
    except Exception as e:
        print(f"COMPILE FAIL {src}: {e}", file=sys.stderr)
        return 1

    for warning in c.rep.warnings:
        print(f"WARN {src}: {warning}", file=sys.stderr)

    text = json_canonical(compiled)
    out = output_path(src)
    if a.stdout:
        print(text, end="")
        return 0
    if a.check:
        if not out.exists():
            print(f"CHECK FAIL missing compiled output: {out}", file=sys.stderr)
            return 1
        old = json.loads(out.read_text(encoding="utf-8"))
        if old != compiled:
            print(f"CHECK FAIL compiled output is stale: {out}", file=sys.stderr)
            return 1
        print(f"OK   {out} is up to date")
        return 0
    out.write_text(text, encoding="utf-8")
    print(f"OK   {src} -> {out} ({len(compiled['cues'])} cues, {len(compiled['stages'])} stages)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
