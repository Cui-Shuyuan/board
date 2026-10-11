#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Split quick section 2's single combined card/setup cue into two cues.

Result:
  quick_setup_cards/001.1  screen card display (face + three backs)
  quick_setup_cards/001.2  table camera + deck setup + market deal

The original audio is replaced by two regenerated TTS assets named
``setup.quick.001.1`` / ``setup.quick.001.2``.
"""
from __future__ import annotations

import copy
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SECTION = ROOT / "content/games/splendor/tutorial/sections/quick_setup_cards"
ANIM = SECTION / "quick.anim.json"
SCRIPT = SECTION / "quick.script.json"
GEMS_ANIM = ROOT / "content/games/splendor/tutorial/sections/quick_setup_gems/quick.anim.json"
GEMS_SECTION = ROOT / "content/games/splendor/tutorial/sections/quick_setup_gems/section.json"


def read(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def write(p: Path, doc) -> None:
    p.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def anchor_id(cue: str, name: str) -> str:
    return f"quick_setup_cards/{cue}.{name}"


def beat_id(cue: str, name: str) -> str:
    return f"quick_setup_cards/{cue}.{name}"


def anchors_for(cue_id: str, beats: list[str]) -> list[dict]:
    out = [
        {"id": anchor_id(cue_id, "start"), "cue": cue_id, "edge": "cue_start"},
        {"id": anchor_id(cue_id, "end"), "cue": cue_id, "edge": "cue_end"},
    ]
    for name in beats:
        bid = beat_id(cue_id, name)
        out.append({"id": f"{bid}.start", "cue": cue_id, "beat": bid, "edge": "start"})
        out.append({"id": f"{bid}.end", "cue": cue_id, "beat": bid, "edge": "end"})
    return out


def main() -> int:
    old_anim = read(ANIM)
    old_script = read(SCRIPT)
    old_cue = copy.deepcopy(old_anim["cues"][0])
    old_events = old_cue["events"]

    # --- cue A: screen-only card display -------------------------------
    cue_a = copy.deepcopy(old_cue)
    cue_a["id"] = "001.1"
    cue_a["events"] = [
        copy.deepcopy(e) for e in (
            old_events[0], old_events[1], old_events[2],
        )
    ]
    for event in cue_a["events"]:
        event["anchor"] = anchor_id("001.1", "start")
        event.pop("offset", None)
    cue_a["script"] = {
        "story": "发展卡共90张，按背面分成三级：一级、二级、三级。",
        "note": "只展示卡牌：先放正面，再并排展示三级卡背并依次高亮；全部走 screen 对象，不切桌面视角。",
        "enter": {"picture": None, "zones": {}},
        "exit": {"picture": None, "zones": {}},
    }
    a_b2 = anchor_id("001.1", "b2.start")
    for event in (old_events[6], old_events[7], old_events[8], old_events[9]):
        clone = copy.deepcopy(event)
        clone["anchor"] = a_b2
        clone.pop("offset", None)
        cue_a["events"].append(clone)
    for index, offset, old_event in (
        (1.15, 0.0, old_events[10]),
        (1.85, 0.0, old_events[11]),
        (2.55, 0.0, old_events[12]),
    ):
        clone = copy.deepcopy(old_event)
        clone["anchor"] = a_b2
        clone["offset"] = offset
        cue_a["events"].append(clone)

    # --- cue B: table camera + setup/deal ------------------------------
    cue_b = copy.deepcopy(old_cue)
    cue_b["id"] = "001.2"
    cue_b["parent"] = "001.1"
    cue_b["script"] = {
        "story": "每级洗混后，翻开四张，摆成三行，组成市场。",
        "note": "从 screen 卡牌展示切回桌面镜头；建立三级牌堆、洗混，再逐行翻开四张组成 3×4 市场。",
        "enter": {"picture": None, "zones": {}},
        "exit": copy.deepcopy(old_cue["script"].get("exit") or {"picture": None, "zones": {}}),
    }
    b_start = anchor_id("001.2", "start")
    b_b1 = anchor_id("001.2", "b1.start")
    b_b2 = anchor_id("001.2", "b2.start")
    b_b3 = anchor_id("001.2", "b3.start")
    b_b4 = anchor_id("001.2", "b4.start")

    cue_b["events"] = []
    for old_index in (13, 14, 15, 16):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_start
        clone.pop("offset", None)
        cue_b["events"].append(clone)
    cam = copy.deepcopy(old_events[17])
    cam["anchor"] = b_start
    cam.pop("offset", None)
    cue_b["events"].append(cam)
    for old_index in (18, 19, 20):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_start
        clone.pop("offset", None)
        cue_b["events"].append(clone)
    for old_index in (21, 22, 23):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_b1
        clone.pop("offset", None)
        cue_b["events"].append(clone)
    for old_index in (24, 25, 26, 27):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_b2
        clone.pop("offset", None)
        cue_b["events"].append(clone)
    for old_index in (28, 29, 30, 31):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_b3
        clone.pop("offset", None)
        cue_b["events"].append(clone)
    for old_index in (32, 33, 34, 35):
        clone = copy.deepcopy(old_events[old_index])
        clone["anchor"] = b_b4
        clone.pop("offset", None)
        cue_b["events"].append(clone)

    # --- scripts -------------------------------------------------------
    new_script = {
        "schema_version": 1,
        "game_id": "splendor",
        "track": "quick",
        "section": "quick_setup_cards",
        "cues": [
            {
                "id": "001.1",
                "tts_asset": "setup.quick.001.1",
                "group": "2 设置",
                "group_path": ["2 设置"],
                "refs": [],
                "pause_after": 0.0,
                "beats": [
                    {"id": beat_id("001.1", "b1"), "text": "发展卡共90张，"},
                    {"id": beat_id("001.1", "b2"), "text": "按背面分成三级：一级、二级、三级。"},
                ],
            },
            {
                "id": "001.2",
                "tts_asset": "setup.quick.001.2",
                "group": "2 设置",
                "group_path": ["2 设置"],
                "refs": [],
                "pause_after": 0.0,
                "beats": [
                    {"id": beat_id("001.2", "b1"), "text": "每级洗混后，"},
                    {"id": beat_id("001.2", "b2"), "text": "翻开四张，"},
                    {"id": beat_id("001.2", "b3"), "text": "摆成三行，"},
                    {"id": beat_id("001.2", "b4"), "text": "组成市场。"},
                ],
            },
        ],
    }

    new_anim = {
        "schema": "tutorial-section-anim/v1",
        "game": "splendor",
        "track": "quick",
        "section": "quick_setup_cards",
        "cues": [cue_a, cue_b],
        "time_anchors": (
            anchors_for("001.1", ["b1", "b2"])
            + anchors_for("001.2", ["b1", "b2", "b3", "b4"])
        ),
    }
    write(SCRIPT, new_script)
    write(ANIM, new_anim)

    # --- later sections reference the last cue of this section ---------
    gems_anim = read(GEMS_ANIM)
    first = gems_anim["cues"][0]
    if first.get("parent") == "quick_setup_cards/001":
        first["parent"] = "quick_setup_cards/001.2"
    write(GEMS_ANIM, gems_anim)

    gems_section = read(GEMS_SECTION)
    if gems_section.get("entry") == "quick_setup_cards/001":
        gems_section["entry"] = "quick_setup_cards/001.2"
    write(GEMS_SECTION, gems_section)

    print("split quick_setup_cards/001 -> 001.1 + 001.2")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
