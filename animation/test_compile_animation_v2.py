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
import anim_schema_v2 as schema  # noqa: E402


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

    def test_screen_target_highlight_compiles_common_screen_clip(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "highlight",
                "at": 0.2,
                "dur": 0.5,
                "grow": 1.25,
                "target": {"space": "screen", "id": "sample_red"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"]
                 if c.get("object_space") == "screen" and c.get("overlay") == "sample_red"]
        self.assertEqual(1, len(clips), clips)
        self.assertEqual("highlight", clips[0]["kind"])
        self.assertAlmostEqual(1.25, clips[0]["to_scale"])

    def test_screen_target_point_records_overlay_resolution(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "point",
                "at": 0.2,
                "dur": 0.0,
                "part": "cost",
                "indicator": "arrow",
                "target": {"space": "screen", "id": "purchase_card"},
            }
            track_path, event_index = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        record = cue["pointer_resolution"][0]
        self.assertEqual(event_index, record["event_index"])
        self.assertEqual("screen", record["object_space"])
        self.assertEqual("purchase_card", record["overlay"])
        self.assertEqual(["purchase_card"], record["item_ids"])
        clips = [c for c in cue["clips"] if c.get("kind") == "point"]
        self.assertEqual(1, len(clips), clips)
        self.assertEqual("purchase_card", clips[0]["overlay"])
        self.assertEqual("cost", clips[0]["part"])

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


def _stage_doc(stage_id: str, zone_ids: list[str]) -> dict:
    zones = []
    for idx, zid in enumerate(zone_ids):
        zones.append({
            "id": zid,
            "label": zid,
            "role": "zone",
            "center": {"x": float(idx) * 0.5, "z": 0.0},
            "size": {"w": 0.2, "h": 0.2},
            "layout": {"type": "row", "capacity": 4, "x_step": 0.25, "z_step": 0.0},
        })
    return {
        "schema": "tutorial-stage/v2",
        "kind": "stage",
        "game": "splendor",
        "id": stage_id,
        "extent": {"min_x": -1.0, "max_x": 1.0, "min_z": -1.0, "max_z": 1.0},
        "zones": zones,
        "templates": [],
        "shots": [{"id": "shot", "zones": [zone_ids[0]] if zone_ids else [], "fill": 0.8}],
    }


def _contract(zones: dict | None = None) -> dict:
    return {"picture": None, "zones": zones or {}}


def _cue_doc(cid: str, tree: str, *, entry=None, parent=None, events=None,
             stage=None, transition="continue", demo=None) -> dict:
    cue = {
        "id": cid,
        "tree": tree,
        "script": {"story": cid, "enter": _contract(), "exit": _contract()},
        "events": events or [],
    }
    if entry is not None:
        cue["entry"] = entry
    if parent is not None:
        cue["parent"] = parent
    if stage is not None:
        cue["stage"] = stage
    if transition != "continue":
        cue["transition"] = transition
    if demo is not None:
        cue["demo"] = demo
    return cue


def _write_test_track(tmp: Path, track: dict, stages: dict[str, dict]) -> Path:
    for filename, stage in stages.items():
        (tmp / filename).write_text(json.dumps(stage, ensure_ascii=False), encoding="utf-8")
    path = tmp / "track.anim.json"
    path.write_text(json.dumps(track, ensure_ascii=False), encoding="utf-8")
    return path


class StateGraphCompileTests(unittest.TestCase):
    def test_sibling_branches_share_common_entry_without_crossing(self):
        stage = _stage_doc("s1", ["deck", "market"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "main",
            "time_anchors": [],
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w", "stage": "s1.stage.json",
                       "purpose": "p", "initial": "i", "extent_note": "e"}],
            "cues": [
                _cue_doc("common", "main", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "deck", "count": 2}]),
                _cue_doc("refill.001", "main", entry="common", parent="common",
                         events=[{"op": "transfer", "at": 0.0, "source": "deck",
                                  "destination": "market", "quantity": 1}]),
                _cue_doc("no_refill.001", "main", entry="common", parent="common",
                         events=[{"op": "destroy", "at": 0.0, "zone": "deck", "count": 2}]),
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
            compiled = compile_anim.Compiler(path).compile()
        common = _compiled_cue(compiled, "common")
        refill = _compiled_cue(compiled, "refill.001")
        no_refill = _compiled_cue(compiled, "no_refill.001")
        self.assertEqual(common["end_state"], refill["start_state"])
        self.assertEqual(common["end_state"], no_refill["start_state"])
        self.assertNotEqual(refill["end_state"], no_refill["end_state"])
        self.assertEqual(2, len(refill["end_state"]["components"]))
        self.assertEqual(0, len(no_refill["end_state"]["components"]))

    def test_cross_tree_entry_copies_snapshot_and_does_not_leak(self):
        stage_a = _stage_doc("sa", ["a"])
        stage_b = _stage_doc("sb", ["a", "b"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "A",
            "time_anchors": [],
            "worlds": [{"id": "wa", "why": "a"}, {"id": "wb", "why": "b"}],
            "trees": [
                {"id": "A", "world": "wa", "stage": "sa.stage.json",
                 "purpose": "p", "initial": "i", "extent_note": "e"},
                {"id": "B", "world": "wb", "stage": "sb.stage.json",
                 "purpose": "p", "initial": "i", "extent_note": "e"},
            ],
            "cues": [
                _cue_doc("A1", "A", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "a", "count": 1}]),
                _cue_doc("A2", "A", parent="A1"),
                _cue_doc("B1", "B", entry="A1", parent="A1",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "b", "count": 1}]),
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {
                "sa.stage.json": stage_a,
                "sb.stage.json": stage_b,
            })
            compiled = compile_anim.Compiler(path).compile()
        a1 = _compiled_cue(compiled, "A1")
        a2 = _compiled_cue(compiled, "A2")
        b1 = _compiled_cue(compiled, "B1")
        self.assertEqual(a1["end_state"], b1["start_state"])
        self.assertEqual(a1["end_state"], a2["start_state"])
        self.assertEqual(2, len(b1["end_state"]["components"]))
        self.assertEqual(1, len(a2["end_state"]["components"]))
        self.assertNotEqual(b1["end_state"], a1["end_state"])

    def test_cross_tree_default_parent_copies_state(self):
        stage = _stage_doc("s1", ["a"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "A",
            "time_anchors": [],
            "worlds": [{"id": "wa", "why": "a"}, {"id": "wb", "why": "b"}],
            "trees": [
                {"id": "A", "world": "wa", "stage": "s1.stage.json",
                 "purpose": "p", "initial": "i", "extent_note": "e"},
                {"id": "B", "world": "wb", "stage": "s1.stage.json",
                 "purpose": "p", "initial": "i", "extent_note": "e"},
            ],
            "cues": [
                _cue_doc("A1", "A", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "a", "count": 1}]),
                _cue_doc("B1", "B", parent="A1"),
            ],
        }
        rep = schema.validate_track(track)
        self.assertFalse(rep.errors, rep.errors)
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
            compiled = compile_anim.Compiler(path).compile()
        self.assertEqual(_compiled_cue(compiled, "A1")["end_state"],
                         _compiled_cue(compiled, "B1")["start_state"])

    def test_track_order_supplies_state_by_default(self):
        stage = _stage_doc("s1", ["a"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "main",
            "time_anchors": [],
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w", "stage": "s1.stage.json",
                       "purpose": "p", "initial": "i", "extent_note": "e"}],
            "cues": [
                _cue_doc("c1", "main", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "a", "count": 1}]),
                _cue_doc("c2", "main"),
            ],
        }
        rep = schema.validate_track(track)
        self.assertFalse(rep.errors, rep.errors)
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
            compiled = compile_anim.Compiler(path).compile()
        self.assertEqual(_compiled_cue(compiled, "c1")["end_state"],
                         _compiled_cue(compiled, "c2")["start_state"])

    def test_cue_stage_inherits_parent_then_tree_and_overrides(self):
        s1 = _stage_doc("s1", ["a"])
        s2 = _stage_doc("s2", ["a"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "main",
            "time_anchors": [],
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w", "stage": "s1.stage.json",
                       "purpose": "p", "initial": "i", "extent_note": "e"}],
            "cues": [
                _cue_doc("c1", "main", entry="initial", transition="world_cut"),
                _cue_doc("c2", "main", parent="c1"),
                _cue_doc("c3", "main", parent="c2", stage="s2.stage.json"),
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {
                "s1.stage.json": s1,
                "s2.stage.json": s2,
            })
            compiled = compile_anim.Compiler(path).compile()
        self.assertEqual("s1", _compiled_cue(compiled, "c1")["stage"])
        self.assertEqual("s1", _compiled_cue(compiled, "c2")["stage"])
        self.assertEqual("s2", _compiled_cue(compiled, "c3")["stage"])

    def test_state_zone_may_be_hidden_from_current_stage(self):
        """Tree/stage decides visibility; logical state may carry hidden zones."""
        stage = _stage_doc("s1", ["a"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "main",
            "time_anchors": [],
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w", "stage": "s1.stage.json",
                       "purpose": "p", "initial": "i", "extent_note": "e"}],
            "cues": [
                _cue_doc("c1", "main", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "missing", "count": 1}]),
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
            compiled = compile_anim.Compiler(path).compile()
        end = _compiled_cue(compiled, "c1")["end_state"]["components"]
        self.assertEqual(1, len(end))
        self.assertEqual("missing", end[0]["ZoneId"])

    def test_demo_branch_can_modify_assumptions_without_leaking_back(self):
        stage = _stage_doc("s1", ["deck"])
        track = {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "splendor",
            "track": "test",
            "default_tree": "main",
            "time_anchors": [],
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w", "stage": "s1.stage.json",
                       "purpose": "p", "initial": "i", "extent_note": "e"}],
            "cues": [
                _cue_doc("canon.1", "main", entry="initial", transition="world_cut",
                         events=[{"op": "create", "at": 0.0, "template": "token",
                                  "palette": "p", "zone": "deck", "count": 1}]),
                _cue_doc("demo.1", "main", parent="canon.1", demo=True,
                         events=[{"op": "destroy", "at": 0.0, "zone": "deck", "count": 1}]),
                _cue_doc("canon.2", "main", parent="canon.1", entry="canon.1"),
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
            compiled = compile_anim.Compiler(path).compile()
        canon1 = _compiled_cue(compiled, "canon.1")
        demo1 = _compiled_cue(compiled, "demo.1")
        canon2 = _compiled_cue(compiled, "canon.2")
        self.assertTrue(demo1["demo"])
        self.assertFalse(canon2["demo"])
        self.assertEqual(canon1["end_state"], demo1["start_state"])
        self.assertEqual(0, len(demo1["end_state"]["components"]))
        self.assertEqual(canon1["end_state"], canon2["start_state"])
        self.assertEqual(1, len(canon2["start_state"]["components"]))


if __name__ == "__main__":
    unittest.main()
