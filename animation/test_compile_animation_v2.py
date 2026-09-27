#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for animation/compile_animation_v2.py (standard library only)."""
from __future__ import annotations

import copy
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SCHEMA_DIR = ROOT / "content" / "games" / "splendor" / "tutorial" / "anim" / "v2"
SCHEMA_ANIM = SCHEMA_DIR / "_schema_example.anim.json"
SCHEMA_STAGE = SCHEMA_DIR / "_schema_example.stage.json"

sys.path.insert(0, str(HERE))

import compile_animation_v2 as compile_anim  # noqa: E402


def write_schema_track(tmp: Path, point_event: dict) -> tuple[Path, int]:
    """Copy the repo _schema_example into a temp dir and append one event.

    The repository fixture itself is never modified.
    """
    track_path = tmp / "_schema_example.anim.json"
    stage_path = tmp / "_schema_example.stage.json"
    shutil.copyfile(SCHEMA_STAGE, stage_path)

    doc = json.loads(SCHEMA_ANIM.read_text(encoding="utf-8"))
    for cue in doc.get("cues") or []:
        if cue.get("id") != "example.show.001":
            continue
        events = cue.setdefault("events", [])
        event_index = len(events)
        events.append(copy.deepcopy(point_event))
        break
    else:
        raise AssertionError("_schema_example missing example.show.001")

    track_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    return track_path, event_index


def _compiled_cue(compiled: dict, cue_id: str) -> dict:
    for cue in compiled.get("cues") or []:
        if cue.get("id") == cue_id:
            return cue
    raise AssertionError(f"compiled cue not found: {cue_id}")


class CompileAnimationV2Tests(unittest.TestCase):
    def test_resolvable_pointer_records_non_empty_item_ids(self):
        with tempfile.TemporaryDirectory() as tmp:
            point_event = {
                "op": "point",
                "at": 0.2,
                "dur": 0.0,
                "zone": "showcase",
                "order": 0,
                "indicator": "focus",
            }
            track_path, event_index = write_schema_track(Path(tmp), point_event)

            compiler = compile_anim.Compiler(track_path)
            compiled = compiler.compile()
            cue = _compiled_cue(compiled, "example.show.001")

        resolution = cue["pointer_resolution"]
        self.assertEqual(1, len(resolution), resolution)
        record = resolution[0]
        self.assertEqual(event_index, record["event_index"])
        self.assertEqual("point", record["op"])
        self.assertEqual("showcase", record["zone"])
        self.assertEqual(0, record["order"])
        self.assertEqual(1, record["matched_count"])
        self.assertEqual(1, len(record["item_ids"]))
        self.assertTrue(record["item_ids"][0])
        self.assertTrue(all("pointer_resolution" in c for c in compiled["cues"]))

    def test_unresolved_pointer_records_empty_item_ids_and_warns(self):
        with tempfile.TemporaryDirectory() as tmp:
            point_event = {
                "op": "point",
                "at": 0.2,
                "dur": 0.0,
                "zone": "showcase",
                "order": 99,
                "indicator": "focus",
            }
            track_path, event_index = write_schema_track(Path(tmp), point_event)

            compiler = compile_anim.Compiler(track_path)
            compiled = compiler.compile()
            cue = _compiled_cue(compiled, "example.show.001")

        resolution = cue["pointer_resolution"]
        self.assertEqual(1, len(resolution), resolution)
        record = resolution[0]
        self.assertEqual(event_index, record["event_index"])
        self.assertEqual("point", record["op"])
        self.assertEqual("showcase", record["zone"])
        self.assertEqual(99, record["order"])
        self.assertEqual(0, record["matched_count"])
        self.assertEqual([], record["item_ids"])
        self.assertFalse([c for c in cue["clips"] if c.get("kind") == "point"])
        self.assertTrue(any(
            "unresolved pointer" in warning
            and f"event_index={event_index}" in warning
            and "anchor=" in warning
            and "offset=" in warning
            for warning in compiler.rep.warnings
        ), compiler.rep.warnings)


if __name__ == "__main__":
    unittest.main()
