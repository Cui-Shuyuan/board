#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic unit tests for check_anim_v2 intermediate results."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import check_anim_v2 as checker  # noqa: E402


def comp(item_id, zone="z1", order=0, face=2):
    return {"Id": item_id, "ZoneId": zone, "Order": order, "Face": face,
            "TemplateId": "card", "Palette": ""}


class CameraTimelineTests(unittest.TestCase):
    def test_camera_at_uses_latest_op_at_or_before_time(self):
        cue = {
            "camera_in": {"center_x": 1.0},
            "camera_ops": [
                {"at": 1.0, "frame": {"center_x": 2.0}},
                {"at": 2.0, "frame": {"center_x": 3.0}},
            ],
        }
        self.assertEqual(checker.camera_at(cue, 0.5), {"center_x": 1.0})
        self.assertEqual(checker.camera_at(cue, 1.0), {"center_x": 2.0})
        self.assertEqual(checker.camera_at(cue, 1.5), {"center_x": 2.0})
        self.assertEqual(checker.camera_at(cue, 2.0), {"center_x": 3.0})

    def test_picture_at_uses_latest_picture_show_and_honors_picture_on(self):
        cue = {
            "clips": [
                {"kind": "show", "at": 0.0, "picture": "Z", "picture_on": True},
                {"kind": "picture", "at": 0.5, "picture": "A", "picture_on": True},
                {"kind": "picture", "at": 1.0, "picture": "B", "picture_on": True},
                {"kind": "picture", "at": 1.5, "picture": "C", "picture_on": False},
            ]
        }
        self.assertEqual(checker.picture_at(cue, 0.0), "Z")
        self.assertEqual(checker.picture_at(cue, 0.75), "A")
        self.assertEqual(checker.picture_at(cue, 1.2), "B")
        self.assertIsNone(checker.picture_at(cue, 1.5))
        self.assertIsNone(checker.picture_at(cue, 2.0))

    def test_check_camera_ops_reports_missing_frame_order_and_negative_at(self):
        compiled = {"cues": [
            {"id": "missing_camera"},
            {"id": "negative", "camera_in": {"center_x": 0.0},
             "camera_ops": [{"at": -0.1, "frame": {"center_x": 0.0}}]},
            {"id": "unsorted", "camera_in": {"center_x": 0.0},
             "camera_ops": [{"at": 1.0, "frame": {"center_x": 0.0}},
                            {"at": 0.0, "frame": {"center_x": 0.0}}]},
            {"id": "no_frame", "camera_in": {"center_x": 0.0},
             "camera_ops": [{"at": 0.0}]},
        ]}
        rep = []
        checker.check_camera_ops(rep, compiled)
        joined = "\n".join(rep)
        self.assertIn("missing camera_in", joined)
        self.assertIn("camera_ops not sorted by at", joined)
        self.assertIn("camera op missing frame", joined)
        self.assertIn("camera op at must be >= 0", joined)

    def test_check_camera_ops_accepts_valid_stream(self):
        compiled = {"cues": [{
            "id": "cue.ok",
            "camera_in": {"center_x": 0.0},
            "camera_ops": [{"at": 0.0, "frame": {"center_x": 0.0}},
                           {"at": 1.0, "frame": {"center_x": 1.0}}],
        }]}
        rep = []
        checker.check_camera_ops(rep, compiled)
        self.assertEqual(rep, [])


class StateOpsTests(unittest.TestCase):
    def test_check_state_ops_accepts_put_then_remove(self):
        item = comp("i1", zone="hand", order=0)
        compiled = {"cues": [{
            "id": "cue.ok",
            "start_state": {"components": []},
            "state_ops": [
                {"at": 0.0, "op": "put", "item": item},
                {"at": 1.0, "op": "remove", "item_id": "i1"},
            ],
            "first_state": {"components": [item]},
            "end_state": {"components": []},
        }]}
        rep = []
        checker.check_state_ops(rep, compiled)
        self.assertEqual(rep, [])

    def test_check_state_ops_reports_unsorted_and_state_drift(self):
        item = comp("i1", zone="hand", order=0)
        compiled = {"cues": [{
            "id": "cue.bad",
            "start_state": {"components": []},
            "state_ops": [
                {"at": 1.0, "op": "put", "item": item},
                {"at": 0.5, "op": "put", "item": comp("i2")},
            ],
            "first_state": {"components": [item, comp("i2")]},
            "end_state": {"components": [item, comp("i2")]},
        }]}
        rep = []
        checker.check_state_ops(rep, compiled)
        joined = "\n".join(rep)
        self.assertIn("state_ops not sorted by at", joined)
        self.assertIn("state_ops <=0 应用后与 first_state 不一致", joined)


class DirtyBoundaryTests(unittest.TestCase):
    CAM_A = {"center_x": 0.0, "center_z": 0.0, "ortho_size": 1.0, "pitch": 90.0}
    CAM_B = {"center_x": 1.0, "center_z": 0.0, "ortho_size": 1.0, "pitch": 90.0}

    def test_dirty_carried_item_under_new_camera_is_flagged(self):
        item = comp("i1")
        compiled = {"cues": [
            {"id": "prev", "duration": 1.0, "camera_in": dict(self.CAM_A),
             "end_state": {"components": [item]}, "clips": []},
            {"id": "cur", "camera_in": dict(self.CAM_B),
             "first_state": {"components": [item]},
             "end_state": {"components": []},
             "clips": [{"item_id": "i1", "kind": "fade", "at": 0.5}]},
        ]}
        rep = []
        checker.check_dirty_boundaries(rep, compiled)
        self.assertEqual(len(rep), 1)
        self.assertIn("脏帧风险", rep[0])
        self.assertIn("i1", rep[0])
        self.assertIn("t=0.5", rep[0])

    def test_same_camera_or_surviving_item_is_not_flagged(self):
        item = comp("i1")
        compiled = {"cues": [
            {"id": "prev", "duration": 1.0, "camera_in": dict(self.CAM_A),
             "end_state": {"components": [item]}, "clips": []},
            {"id": "cur_same_cam", "camera_in": dict(self.CAM_A),
             "first_state": {"components": [item]},
             "end_state": {"components": []},
             "clips": [{"item_id": "i1", "kind": "fade", "at": 0.5}]},
            {"id": "prev2", "duration": 1.0, "camera_in": dict(self.CAM_A),
             "end_state": {"components": [item]}, "clips": []},
            {"id": "cur_survives", "camera_in": dict(self.CAM_B),
             "first_state": {"components": [item]},
             "end_state": {"components": [item]}, "clips": []},
        ]}
        rep = []
        checker.check_dirty_boundaries(rep, compiled)
        self.assertEqual(rep, [])

    def test_camera_eq_compares_compiled_fields(self):
        self.assertTrue(checker.camera_eq(dict(self.CAM_A), dict(self.CAM_A)))
        self.assertFalse(checker.camera_eq(dict(self.CAM_A), dict(self.CAM_B)))
        self.assertFalse(checker.camera_eq(None, dict(self.CAM_A)))
        self.assertTrue(checker.camera_eq(None, None))


if __name__ == "__main__":
    unittest.main()
