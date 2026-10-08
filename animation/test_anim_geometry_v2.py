#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic unit tests for v2 animation geometry and part anchors."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import anim_geometry_v2 as geom  # noqa: E402
import compile_animation_v2 as compile_anim  # noqa: E402


class SlotGeometryTests(unittest.TestCase):
    def test_grid_slots_are_canonical_and_overflow_drifts(self):
        zone = {
            "center": {"x": 0.0, "z": 0.0},
            "capacity": 4,
            "layout": {"type": "grid", "cols": 4, "x_step": 1.0, "z_step": 2.0},
        }
        self.assertEqual(geom.slot_at(zone, 0), (-1.5, 0.0))
        self.assertEqual(geom.slot_at(zone, 3), (1.5, 0.0))
        # Orders beyond capacity clamp to the last slot and then get an
        # explicit overflow drift; this is intentionally deterministic.
        self.assertEqual(geom.slot_at(zone, 4), (1.6, 0.2))

    def test_zero_capacity_uses_one_slot_at_zone_center(self):
        zone = {"center": {"x": 3.0, "z": -2.0}, "capacity": 0,
                "layout": {"type": "row", "x_step": 9.0}}
        self.assertEqual(geom.slot_at(zone, 0), (3.0, -2.0))

    def test_row_block_and_invalid_capacity_values(self):
        row = {"center": {"x": 10.0, "z": 5.0}, "capacity": 3,
               "layout": {"type": "row", "x_step": 2.0}}
        self.assertEqual(geom.slot_at(row, 1), (10.0, 5.0))

        block = {"center": {"x": 0.0, "z": 0.0}, "capacity": 5,
                 "layout": {"type": "block", "x_step": 2.0, "z_step": 4.0}}
        self.assertEqual(geom.slot_at(block, 0), (-2.0, -2.0))
        self.assertEqual(geom.slot_at(block, 4), (1.0, 2.0))

        self.assertEqual(geom.zone_capacity({"capacity": "bad"}), 0)
        self.assertEqual(geom.zone_capacity({"capacity": -3}), 0)

    def test_stack_caps_visible_offset_and_zero_size_stays_finite(self):
        stack = {
            "center": {"x": 10.0, "z": 20.0},
            "capacity": 5,
            "display": {"mode": "stack", "max_visible": 3, "dx": 0.1, "dz": -0.2},
        }
        self.assertEqual(geom.slot_at(stack, 2), (10.2, 19.6))
        self.assertEqual(geom.slot_at(stack, 4), (10.2, 19.6))
        self.assertEqual(geom.slot_at(stack, 99), (10.2, 19.6))
        self.assertEqual(geom.zone_size({"size": {"w": 0, "h": 0}}), (0.01, 0.01))

    def test_zone_box_uses_zero_size_floor_and_count_zero_capacity(self):
        zone = {"center": {"x": 0.0, "z": 0.0}, "capacity": 0, "size": {"w": 0, "h": 0}}
        self.assertEqual(geom.zone_box(zone, 0), (-0.005, 0.005, -0.005, 0.005))
        self.assertIsNone(geom.zone_box({}, 1))

    def test_union_and_stage_union_skip_empty_offstage_and_centerless_zones(self):
        self.assertIsNone(geom.union([]))
        self.assertIsNone(geom.union([None]))
        self.assertEqual(
            geom.union([(-1, 1, -2, 2), None, (0, 3, 1, 4)]),
            (-1, 3, -2, 4),
        )
        stage = {
            "zones": [
                {"id": "on", "center": {"x": 0, "z": 0}, "capacity": 1,
                 "size": {"w": 1, "h": 1}},
                {"id": "off", "role": "offstage", "center": {"x": 9, "z": 9},
                 "capacity": 1, "size": {"w": 1, "h": 1}},
                {"id": "no_center", "capacity": 1, "size": {"w": 1, "h": 1}},
            ]
        }
        self.assertEqual(geom.zone_union_box(stage), (-0.5, 0.5, -0.5, 0.5))


class CameraGeometryTests(unittest.TestCase):
    def test_board_extent_camera_is_deterministic(self):
        stage = {
            "board": {
                "camera_pitch": 90.0,
                "aspect": 2.0,
                "extent": {"min_x": -1.0, "max_x": 1.0,
                           "min_z": -1.0, "max_z": 1.0},
            },
            "zones": [],
        }
        frame = geom.build_camera_frame(stage, {"fill": 1.0})
        self.assertEqual(frame["center_x"], 0.0)
        self.assertEqual(frame["center_z"], 0.0)
        self.assertEqual(frame["ortho_size"], 1.18)
        self.assertEqual(frame["rect_min_x"], -2.36)
        self.assertEqual(frame["rect_max_x"], 2.36)
        self.assertEqual(frame["rect_min_z"], -1.18)
        self.assertEqual(frame["rect_max_z"], 1.18)

    def test_named_camera_zone_uses_union_and_fill_clamp(self):
        stage = {
            "board": {"camera_pitch": 90.0, "aspect": 2.0},
            "zones": [{
                "id": "table", "center": {"x": 0.0, "z": 0.0}, "capacity": 1,
                "size": {"w": 2.0, "h": 2.0},
            }],
        }
        frame = geom.build_camera_frame(stage, {"zones": ["table"], "fill": 0.1})
        # fill is clamped to 0.2 by the caller path before scale calculation,
        # then the extent is padded by that scale.
        self.assertEqual(frame["ortho_size"], 5.0)
        self.assertEqual(frame["rect_min_x"], -10.0)
        self.assertEqual(frame["rect_max_x"], 10.0)
        self.assertEqual(frame["rect_min_z"], -5.0)
        self.assertEqual(frame["rect_max_z"], 5.0)

    def test_missing_named_zone_falls_back_to_default_extent(self):
        stage = {"board": {"camera_pitch": 90.0, "aspect": 2.0}, "zones": []}
        frame = geom.build_camera_frame(stage, {"zones": ["missing"], "fill": 0.8})
        self.assertEqual(frame["center_x"], 0.0)
        self.assertEqual(frame["center_z"], 0.2)
        self.assertEqual(frame["ortho_size"], 1.5)
        self.assertEqual(frame["rect_min_x"], -3.0)
        self.assertEqual(frame["rect_max_x"], 3.0)
        self.assertEqual(frame["rect_min_z"], -1.3)
        self.assertEqual(frame["rect_max_z"], 1.7)


class PartAnchorFallbackTests(unittest.TestCase):
    def test_legacy_map_is_used_without_stage_or_template(self):
        self.assertEqual(compile_anim.part_uv({"part": "bonus"}), (0.825397, 0.130682))
        self.assertEqual(compile_anim.part_size({"part": "bonus"}), (0.269841, 0.193182))
        self.assertEqual(compile_anim.part_uv({"part": "unknown"}), (0.5, 0.5))
        self.assertIsNone(compile_anim.part_size({"part": "unknown"}))

    def test_stage_anchor_uv_and_size_override_legacy(self):
        stage = {
            "templates": [{
                "id": "card", "width": 2.0, "height": 4.0,
                "part_anchors": [{
                    "id": "bonus", "dx": 0.5, "dy": 1.0, "w": 0.4, "h": 0.6,
                }],
            }]
        }
        self.assertEqual(
            compile_anim.part_uv({"part": "bonus", "template": "card"}, stage),
            (0.75, 0.25),
        )
        self.assertEqual(
            compile_anim.part_size({"part": "bonus", "template": "card"}, stage),
            (0.4, 0.6),
        )

    def test_event_uv_wins_over_stage_anchor(self):
        stage = {"templates": [{"id": "card", "width": 2.0, "height": 2.0,
                                "part_anchors": [{"id": "bonus", "u": 0.1, "v": 0.2}]}]}
        self.assertEqual(
            compile_anim.part_uv({"part": "bonus", "template": "card",
                                  "part_u": 0.7, "part_v": 0.8}, stage),
            (0.7, 0.8),
        )

    def test_anchor_size_falls_back_to_legacy_when_stage_size_missing(self):
        stage = {"templates": [{"id": "card", "part_anchors": [{"id": "bonus", "u": 0.2, "v": 0.3}]}]}
        self.assertEqual(
            compile_anim.part_uv({"part": "bonus", "template": "card"}, stage),
            (0.2, 0.3),
        )
        self.assertEqual(
            compile_anim.part_size({"part": "bonus", "template": "card"}, stage),
            (0.269841, 0.193182),
        )

    def test_zero_size_template_anchor_falls_back_to_legacy(self):
        stage = {"templates": [{"id": "card", "width": 0.0, "height": 0.0,
                                "part_anchors": [{"id": "bonus", "dx": 1.0, "dy": 1.0}]}]}
        self.assertEqual(
            compile_anim.part_uv({"part": "bonus", "template": "card"}, stage),
            (0.825397, 0.130682),
        )
        self.assertEqual(
            compile_anim.part_size({"part": "bonus", "template": "card"}, stage),
            (0.269841, 0.193182),
        )


if __name__ == "__main__":
    unittest.main()
