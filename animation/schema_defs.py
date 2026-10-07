#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Shared constants, report type and generic selectors for v2 schema modules."""
from __future__ import annotations

import json
import re
from pathlib import Path

TRACK_SCHEMA = "tutorial-anim/v2"
STAGE_SCHEMA = "tutorial-stage/v2"
COMPILED_TRACK_SCHEMA = "tutorial-anim-compiled/v2"
COMPILED_STAGE_SCHEMA = "tutorial-stage-compiled/v2"

TRANSITIONS = {"continue", "overlay", "cut", "world_cut"}
STATE_OPS = {"ensure", "create", "destroy", "transfer", "stack", "shuffle", "move_order", "set_order", "set_face"}
# Semantic action macros.  They are source-level sugar and are lowered to the
# primitive STATE_OPS above by resolve_track(), so the compiler, validators and
# audit tools only ever see the primitive event stream.
SEMANTIC_OPS = {"take", "move", "pay", "flip", "draw"}
SEMANTIC_SOURCE_OPS = ("transfer", "take", "move", "pay", "flip", "draw")
PRESENTATION_OPS = {"show", "hide", "highlight", "point", "shape", "fade", "scale", "wait", "camera", "label", "magnifier",
                     "overlay_show", "overlay_hide"}
SHAPE_KINDS = {"arrow", "circle", "cross", "forbid", "box"}
# Magnifier 专用形状（circle=圆形透镜；box=矩形透镜）
MAGNIFIER_SHAPES = {"circle", "box"}
MAGNIFIER_MASKS = {"items", "full"}
# 对象接口：世界/屏幕对象的原语统一指向一个 target。
#   {"space": "entity", "zone": ..., "template": ..., "palette": ..., "concept": ..., "parts": [...], "order": n}
#   {"space": "screen", "id": "overlay_slot"}
TARGET_SPACES = {"entity", "screen"}
# `order` is a normal selector field for most entity ops.  For transfer it is
# destination placement and must stay on the event; `_check_object_target`
# warns when it is written inside target instead.
ENTITY_TARGET_FIELDS = ("zone", "template", "palette", "concept", "parts", "order")
# 一个机位至少要保持这么久，否则属于「1 帧镜头」书写事故。
MIN_CAMERA_SHOT_SECONDS = 0.4
OPS = STATE_OPS | PRESENTATION_OPS | SEMANTIC_OPS
# Seconds.  flip/draw are the semantic verbs whose visual is an edge flip, so
# they need a sensible duration when the author does not write one explicitly.
DEFAULT_FLIP_DURATION = 0.6
DEFAULT_DRAW_DURATION = DEFAULT_FLIP_DURATION
FACES = {"up", "down", "hidden", None, ""}
CONCEPT_ID_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.:\-]*$")
SPECIAL_CONCEPT_RE = re.compile(r"^<[A-Za-z_][A-Za-z0-9_.:\-]*>$")

def _valid_stage_concept(value: str) -> bool:
    text = (value or "").strip()
    return bool(CONCEPT_ID_RE.match(text) or SPECIAL_CONCEPT_RE.match(text))



class Report:
    def __init__(self):
        self.errors: list[str] = []
        self.warnings: list[str] = []

    def error(self, msg: str):
        self.errors.append(msg)

    def warn(self, msg: str):
        self.warnings.append(msg)

    def ok(self) -> bool:
        return not self.errors



def load_json(path: str | Path):
    return json.loads(Path(path).read_text(encoding="utf-8"))



def _has_selector(ev: dict) -> bool:
    return any(ev.get(k) for k in ("template", "palette", "concept", "parts"))



_LOCAL_CUE_KEYS = {"id", "parent", "entry", "negative", "qa", "events", "stage", "demo"}
