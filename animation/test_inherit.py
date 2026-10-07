#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Regression tests for the shared cue-inheritance resolver."""
from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import inherit  # noqa: E402
import schema_defs  # noqa: E402


class InheritResolverTests(unittest.TestCase):
    def test_local_cue_keys_uses_schema_defs_single_source(self):
        self.assertIs(inherit._LOCAL_CUE_KEYS, schema_defs._LOCAL_CUE_KEYS)

    def test_resolve_track_uses_effective_state_source(self):
        doc = {
            "default_tree": "main",
            "cues": [
                {
                    "id": "a",
                    "tree": "main",
                    "events": [],
                    "script": {"exit": {"zones": {"x": {"count": 1}}}},
                },
                {"id": "b", "tree": "main", "parent": "a", "events": []},
                {"id": "c", "tree": "main", "events": []},
                {"id": "d", "tree": "main", "events": [], "entry": "initial"},
            ],
        }

        resolved = inherit.resolve_track(copy.deepcopy(doc))
        by_id = {cue["id"]: cue for cue in resolved["cues"]}

        self.assertEqual(by_id["b"]["script"]["exit"]["zones"]["x"]["count"], 1)
        self.assertEqual(by_id["c"]["script"]["exit"]["zones"]["x"]["count"], 1)
        self.assertNotIn("x", (by_id["d"].get("script") or {}).get("exit", {}).get("zones", {}))


if __name__ == "__main__":
    unittest.main()
