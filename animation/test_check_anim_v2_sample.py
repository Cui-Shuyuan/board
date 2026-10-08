#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for check_anim_v2_sample contract reconciliation."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import check_anim_v2_sample as sample_check  # noqa: E402


def item(item_id="i1", zone="market", order=0, face=2):
    return {"Id": item_id, "ZoneId": zone, "Order": order, "Face": face}


class SampleReconcileTests(unittest.TestCase):
    def test_valid_sample_matches_picture_count_state_and_items(self):
        track = {"cues": [{
            "id": "cue.1",
            "script": {"exit": {
                "picture": "picA",
                "zones": {"market": {"count": 1}},
            }},
        }]}
        compiled = {"cues": [{
            "id": "cue.1",
            "end_state": {"components": [item()]},
        }]}
        sample_doc = {"cues": [{
            "cue": "cue.1",
            "picture": "picA",
            "items": [{"id": "i1", "zone": "market", "order": 0,
                       "face": "up", "visible": True}],
        }]}
        self.assertEqual(
            sample_check.reconcile_sample(track, compiled, sample_doc), []
        )

    def test_missing_sample_cue_is_fail_closed(self):
        track = {"cues": [{"id": "cue.1", "script": {"exit": {}}}]}
        errors = sample_check.reconcile_sample(track, {"cues": []}, {"cues": []})
        self.assertEqual(errors, ["cue.1: missing sample"])

    def test_extra_sample_cue_is_fail_closed(self):
        errors = sample_check.reconcile_sample(
            {"cues": []}, {"cues": []},
            {"cues": [{"cue": "cue.extra", "items": []}]},
        )
        self.assertEqual(errors, ["cue.extra: extra sample"])

    def test_picture_and_visible_state_mismatches_are_reported(self):
        track = {"cues": [{
            "id": "cue.1",
            "script": {"exit": {"picture": "want_pic", "zones": {}}},
        }]}
        compiled = {"cues": [{
            "id": "cue.1",
            "end_state": {"components": [item(order=2)]},
        }]}
        sample_doc = {"cues": [{
            "cue": "cue.1",
            "picture": "got_pic",
            "items": [{"id": "i1", "zone": "market", "order": 0,
                       "face": "up", "visible": True}],
        }]}
        joined = "\n".join(sample_check.reconcile_sample(track, compiled, sample_doc))
        self.assertIn("picture: want 'want_pic' got 'got_pic'", joined)
        self.assertIn("state-sync i1", joined)

    def test_count_only_and_item_contract_forms(self):
        track = {"cues": [{
            "id": "cue.1",
            "script": {"exit": {
                "picture": None,
                "zones": [
                    {"zone": "market", "count": 1},
                    {"zone": "hand", "items": [
                        {"template": "card", "palette": "ruby", "count": 1,
                         "face": "up"},
                    ]},
                ],
            }},
        }]}
        sample_doc = {"cues": [{
            "cue": "cue.1",
            "picture": None,
            "items": [
                {"id": "i1", "zone": "market", "order": 0,
                 "face": "up", "visible": True},
                {"id": "i2", "zone": "hand", "order": 0,
                 "face": "up", "visible": True, "kind": "card|ruby"},
            ],
        }]}
        self.assertEqual(
            sample_check.reconcile_sample(track, {"cues": []}, sample_doc), []
        )

    def test_contract_item_count_face_and_visibility_mismatches(self):
        track = {"cues": [{
            "id": "cue.1",
            "script": {"exit": {"zones": {"hand": {"items": [
                {"template": "card", "palette": "ruby", "count": 2,
                 "face": "down"},
            ]}}}},
        }]}
        sample_doc = {"cues": [{
            "cue": "cue.1",
            "items": [
                {"id": "i2", "zone": "hand", "order": 0,
                 "face": "up", "visible": True, "kind": "card|ruby"},
                {"id": "hidden", "zone": "hand", "order": 1,
                 "face": "up", "visible": False, "kind": "card|ruby"},
            ],
        }]}
        compiled = {"cues": [{
            "id": "cue.1",
            "end_state": {"components": [
                {"Id": "i2", "ZoneId": "hand", "Order": 0, "Face": 2},
            ]},
        }]}
        joined = "\n".join(sample_check.reconcile_sample(track, compiled, sample_doc))
        self.assertIn("hand.items[0].face", joined)
        # Hidden sample items are not part of the state-sync comparison.
        self.assertNotIn("state-sync i2", joined)
        self.assertNotIn("state-sync hidden", joined)


if __name__ == "__main__":
    unittest.main()
