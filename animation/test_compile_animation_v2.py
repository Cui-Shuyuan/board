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

    def test_entity_show_hide_compile_to_fade_clips(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "show",
                "at": 0.2,
                "dur": 0.25,
                "target": {
                    "space": "entity",
                    "zone": "showcase",
                    "template": "sample_card_1",
                },
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"]
                 if c.get("object_space") == "entity" and c.get("kind") == "fade"]
        self.assertEqual(1, len(clips), clips)
        self.assertAlmostEqual(1.0, clips[0]["to_alpha"])

    def test_screen_show_explicit_zero_rect_keeps_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "show",
                "at": 0.2,
                "dur": 0.0,
                "rect": {"x": 0, "y": 0, "w": 1, "h": 1},
                "background": "#1E2126",
                "layer": 0,
                "target": {"space": "screen", "id": "scene_backdrop"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"]
                 if c.get("kind") == "overlay_show" and c.get("overlay") == "scene_backdrop"]
        self.assertEqual(1, len(clips), clips)
        self.assertAlmostEqual(0.0, clips[0]["label_x"], places=6)
        self.assertAlmostEqual(0.0, clips[0]["label_y"], places=6)
        self.assertAlmostEqual(1.0, clips[0]["label_w"], places=6)
        self.assertAlmostEqual(1.0, clips[0]["label_h"], places=6)

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

    def test_magnifier_compiles_to_live_lens_clip(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "magnifier",
                "at": 0.9,
                "dur": 0.2,
                "id": "lens",
                "shape": "circle",
                "mask": "items",
                "zoom": 1.3,
                "padding": 0.1,
                "rect": {"x": 0.6, "y": 0.1, "w": 0.3, "h": 0.3},
                "target": {"space": "entity", "zone": "showcase"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"] if c.get("kind") == "magnifier_show"]
        self.assertEqual(1, len(clips), clips)
        clip = clips[0]
        self.assertEqual("screen", clip["object_space"])
        self.assertEqual("lens", clip["overlay"])
        self.assertEqual("circle", clip["mag_shape"])
        self.assertEqual("items", clip["mag_mask"])
        self.assertEqual(["sample_card_1|card_level_1#1"], clip["mag_item_ids"])
        self.assertAlmostEqual(0.6, clip["mag_x"])
        self.assertAlmostEqual(0.3, clip["mag_w"])
        self.assertGreater(clip["mag_ortho_size"], 0.0)

        resolution = [r for r in cue["pointer_resolution"] if r.get("op") == "magnifier"]
        self.assertEqual(1, len(resolution), resolution)
        self.assertEqual(["sample_card_1|card_level_1#1"], resolution[0]["item_ids"])

    def test_magnifier_box_shape_compiles(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "magnifier",
                "at": 0.9,
                "id": "lens_box",
                "shape": "box",
                "zoom": 1.2,
                "rect": {"x": 0.1, "y": 0.2, "w": 0.4, "h": 0.2},
                "target": {"space": "entity", "zone": "showcase"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"] if c.get("kind") == "magnifier_show"]
        self.assertEqual(1, len(clips), clips)
        self.assertEqual("box", clips[0]["mag_shape"])
        self.assertAlmostEqual(0.4, clips[0]["mag_w"])
        self.assertAlmostEqual(0.2, clips[0]["mag_h"])

    def test_magnifier_zoom_reduces_ortho_size(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "magnifier",
                "at": 0.9,
                "id": "lens_zoom",
                "shape": "circle",
                "zoom": 2.0,
                "rect": {"x": 0.3, "y": 0.2, "w": 0.3, "h": 0.3},
                "target": {"space": "entity", "zone": "showcase"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clip = [c for c in cue["clips"] if c.get("kind") == "magnifier_show"][0]
        self.assertGreater(clip["mag_ortho_size"], 0.0)
        # Base fit for the schema card is roughly 0.55; zoom 2 halves it.
        self.assertLess(clip["mag_ortho_size"], 0.35)

    def test_magnifier_full_mask_compiles(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "magnifier",
                "at": 0.9,
                "id": "lens_full",
                "mask": "full",
                "shape": "box",
                "zoom": 1.2,
                "rect": {"x": 0.1, "y": 0.1, "w": 0.3, "h": 0.3},
                "target": {"space": "entity", "zone": "showcase"},
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clip = [c for c in cue["clips"] if c.get("kind") == "magnifier_show"][0]
        self.assertEqual("full", clip["mag_mask"])
        self.assertEqual("box", clip["mag_shape"])

    def test_magnifier_full_circle_uses_square_window_aspect(self):
        events = [
            {
                "op": "magnifier",
                "at": 0.9,
                "id": "lens_full_wide",
                "mask": "full",
                "shape": "circle",
                "zoom": 1.2,
                "rect": {"x": 0.1, "y": 0.1, "w": 0.09, "h": 0.32},
                "target": {"space": "entity", "zone": "showcase"},
            },
            {
                "op": "magnifier",
                "at": 0.9,
                "id": "lens_full_tall",
                "mask": "full",
                "shape": "circle",
                "zoom": 1.2,
                "rect": {"x": 0.1, "y": 0.1, "w": 0.32, "h": 0.09},
                "target": {"space": "entity", "zone": "showcase"},
            },
        ]
        clips = []
        for event in events:
            with tempfile.TemporaryDirectory() as tmp:
                track_path, _ = write_schema_track(Path(tmp), event)
                compiled = compile_anim.Compiler(track_path).compile()
                cue = _compiled_cue(compiled, "example.show.001")
                clip = [c for c in cue["clips"] if c.get("kind") == "magnifier_show"][0]
                clips.append(clip)

        self.assertEqual("full", clips[0]["mag_mask"])
        self.assertEqual("circle", clips[0]["mag_shape"])
        # A circle lens resolves to a square world window; its ortho size must
        # not depend on the authored rect's w/h aspect.
        self.assertAlmostEqual(
            clips[0]["mag_ortho_size"], clips[1]["mag_ortho_size"], places=6)

    def test_camera_shot_view_offset_shifts_frame_center(self):
        stage = {
            "id": "offset_test",
            "game": "splendor",
            "board": {
                "aspect": 1.7778,
                "extent": {"min_x": -1.0, "max_x": 1.0, "min_z": -1.0, "max_z": 1.0},
            },
            "zones": [{
                "id": "zone",
                "center": {"x": 0.0, "z": 0.0},
                "layout": {"type": "row", "x_step": 1.0},
                "capacity": 1,
                "size": {"w": 0.5, "h": 0.5},
            }],
        }
        base = compile_anim.geom.build_camera_frame(stage, {"zones": ["zone"], "fill": 0.8})
        shifted = compile_anim.geom.build_camera_frame(
            stage,
            {"zones": ["zone"], "fill": 0.8, "view_offset_x": -2.0, "view_offset_z": -0.5},
        )
        self.assertAlmostEqual(base["center_x"] - 2.0, shifted["center_x"], places=6)
        self.assertAlmostEqual(base["center_z"] - 0.5, shifted["center_z"], places=6)

    def test_card_part_variants_use_measured_box_geometry(self):
        expected = {
            "cost_1": (0.1125, 0.920, 0.315, 0.22),
            "cost_2": (0.1125, 0.845, 0.315, 0.37),
            "cost_3": (0.1125, 0.770, 0.315, 0.52),
            "cost_4": (0.1125, 0.700, 0.315, 0.66),
            "noble_prestige": (0.1417, 0.1417, 0.44, 0.44),
            "condition_2": (0.515, 0.840, 0.53, 0.28),
            "condition_3": (0.525, 0.840, 0.81, 0.28),
        }
        for part, (u, v, w, h) in expected.items():
            with self.subTest(part=part), tempfile.TemporaryDirectory() as tmp:
                event = {
                    "op": "shape",
                    "at": 0.2,
                    "dur": 0.0,
                    "shape": "box",
                    "part": part,
                    "target": {"space": "screen", "id": "purchase_card"},
                }
                track_path, _ = write_schema_track(Path(tmp), event)
                compiled = compile_anim.Compiler(track_path).compile()
                cue = _compiled_cue(compiled, "example.show.001")

            clips = [c for c in cue["clips"]
                     if c.get("kind") == "shape" and c.get("part") == part]
            self.assertEqual(1, len(clips), clips)
            clip = clips[0]
            self.assertAlmostEqual(u, clip["part_u"], places=6)
            self.assertAlmostEqual(v, clip["part_v"], places=6)
            self.assertAlmostEqual(w, clip["part_w"], places=6)
            self.assertAlmostEqual(h, clip["part_h"], places=6)

    def test_world_shape_box_compiles_to_annotation_clip(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "shape",
                "at": 0.3,
                "dur": 0.0,
                "shape": "box",
                "space": "world",
                "part": "whole",
                "target": {
                    "space": "entity",
                    "zone": "showcase",
                    "template": "sample_card_1",
                },
            }
            track_path, event_index = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"] if c.get("kind") == "shape"]
        self.assertEqual(1, len(clips), clips)
        clip = clips[0]
        self.assertEqual("world", clip["annotation_space"])
        self.assertTrue(clip["item_id"])
        self.assertEqual("box", clip["indicator"])
        self.assertTrue(clip["has_part_uv"])
        self.assertAlmostEqual(0.5, clip["part_u"])
        self.assertAlmostEqual(0.5, clip["part_v"])
        resolution = [r for r in cue["pointer_resolution"] if r["event_index"] == event_index]
        self.assertEqual(1, len(resolution), resolution)
        self.assertEqual("world", resolution[0]["annotation_space"])
        self.assertEqual(1, resolution[0]["matched_count"])

    def test_world_label_targets_entity_and_keeps_part_anchor(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "label",
                "at": 0.3,
                "dur": 0.0,
                "text": "跟随卡牌的世界文字",
                "space": "world",
                "part": "bonus",
                "target": {
                    "space": "entity",
                    "zone": "showcase",
                    "template": "sample_card_1",
                },
            }
            track_path, _ = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"] if c.get("kind") == "label" and c.get("text")]
        self.assertEqual(1, len(clips), clips)
        clip = clips[0]
        self.assertEqual("world", clip["annotation_space"])
        self.assertTrue(clip["item_id"])
        self.assertEqual("bonus", clip["part"])
        # Canonical development-card geometry: bonus is at the fixed upper-right
        # badge position, with a fixed badge size.
        self.assertAlmostEqual(0.825397, clip["part_u"], places=6)
        self.assertAlmostEqual(0.130682, clip["part_v"], places=6)
        self.assertAlmostEqual(0.269841, clip["part_w"], places=6)
        self.assertAlmostEqual(0.193182, clip["part_h"], places=6)

    def test_screen_shape_uses_mapping_offset_as_spatial_nudge(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = {
                "op": "shape",
                "at": 0.3,
                "dur": 0.0,
                "shape": "arrow",
                "space": "screen",
                "part": "prestige",
                "offset": {"x": 0.02, "y": -0.03},
                "target": {"space": "screen", "id": "sample_red"},
            }
            track_path, event_index = write_schema_track(Path(tmp), event)
            compiled = compile_anim.Compiler(track_path).compile()
            cue = _compiled_cue(compiled, "example.show.001")

        clips = [c for c in cue["clips"] if c.get("kind") == "shape"]
        self.assertEqual(1, len(clips), clips)
        clip = clips[0]
        self.assertEqual("screen", clip["annotation_space"])
        self.assertEqual("sample_red", clip["overlay"])
        self.assertEqual("arrow", clip["indicator"])
        self.assertAlmostEqual(0.02, clip["nudge_x"], places=6)
        self.assertAlmostEqual(-0.03, clip["nudge_y"], places=6)
        self.assertAlmostEqual(0.3, clip["at"], places=6)
        resolution = [r for r in cue["pointer_resolution"] if r["event_index"] == event_index]
        self.assertEqual(1, len(resolution), resolution)
        self.assertEqual("screen", resolution[0]["annotation_space"])

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

    def test_transfer_from_top_uses_largest_order_not_layer(self):
        model = compile_anim.StateModel(stack_zones={"src", "dest"})

        def add(zone: str, layer: int):
            return model.spawn(
                "gem", "gem_diamond", "gem", zone, count=1,
                parts=[{"key": "color", "value": "<diamond>"}], layer=layer,
            )[0]

        # Layers deliberately disagree with orders: order is the canonical
        # pile sequence, so order 2 must win as the top card.
        add("src", 9)   # order 0
        add("src", 0)   # order 1
        add("src", 3)   # order 2 = pile top
        existing = add("dest", -3)
        existing["order"] = 3  # supply/deck holes stay open after removals

        # Omit both options: defaults pick the source top by largest order and
        # put it on top of the existing destination without renumbering it.
        records = model.transfer(
            {"template": "gem", "palette": "gem_diamond"},
            "src", "dest", 2,
        )
        self.assertEqual(
            {"gem|gem_diamond#2", "gem|gem_diamond#3"},
            {rec["item"]["id"] for rec in records},
        )
        dest_items = sorted(
            (item for item in model.items if item["zone"] == "dest"),
            key=lambda item: item["order"],
        )
        self.assertEqual(
            ["gem|gem_diamond#4", "gem|gem_diamond#2", "gem|gem_diamond#3"],
            [item["id"] for item in dest_items],
        )
        # Existing gem keeps its order/layer; the higher source card stays on
        # top with the higher order/layer.
        self.assertEqual("gem|gem_diamond#3", dest_items[-1]["id"])
        self.assertEqual([3, 4, 5], [item["order"] for item in dest_items])
        self.assertEqual([-3, -2, -1], [item["layer"] for item in dest_items])


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

    def test_shuffle_event_is_visual_only_and_keeps_logical_order(self):
        stage = _stage_doc("s1", ["deck"])
        stage["zones"][0]["display"] = {"mode": "stack", "max_visible": 4}

        base_events = [
            {"op": "create", "at": 0.0, "template": "token", "palette": "p",
             "zone": "deck", "count": 3},
        ]
        shuffle_event = {"op": "shuffle", "at": 0.5, "dur": 0.5, "zone": "deck"}

        def compile_cue(events):
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
                             events=copy.deepcopy(events)),
                ],
            }
            with tempfile.TemporaryDirectory() as tmp:
                path = _write_test_track(Path(tmp), track, {"s1.stage.json": stage})
                return _compiled_cue(compile_anim.Compiler(path).compile(), "c1")

        plain = compile_cue(base_events)
        shuffled = compile_cue(base_events + [shuffle_event])
        self.assertEqual(plain["end_state"], shuffled["end_state"])
        self.assertTrue(any(c.get("kind") == "shuffle" for c in shuffled["clips"]))
        self.assertFalse(any(c.get("kind") == "shuffle" for c in plain["clips"]))

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
