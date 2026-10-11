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

    def test_section_entry_controls_first_cue_and_section_reset(self):
        doc = {
            "default_tree": "main",
            "sections": [
                {"id": "take", "entry": "setup", "title": "拿宝石"},
            ],
            "cues": [
                {
                    "id": "setup",
                    "tree": "main",
                    "events": [],
                    "script": {"exit": {"zones": {"gem": {"count": 3}}}},
                },
                {"id": "branch_a", "tree": "main", "section": "take", "events": []},
                {"id": "branch_b", "tree": "main", "section": "take", "entry": "section:take", "events": []},
            ],
        }

        resolved = inherit.resolve_track(copy.deepcopy(doc))
        by_id = {cue["id"]: cue for cue in resolved["cues"]}

        # First cue in the section inherits section.entry.
        self.assertEqual(by_id["branch_a"]["entry"], "setup")
        self.assertEqual(by_id["branch_a"]["script"]["exit"]["zones"]["gem"]["count"], 3)
        # Explicit section reset resolves to the same state source.
        self.assertEqual(by_id["branch_b"]["entry"], "setup")
        self.assertEqual(by_id["branch_b"]["script"]["exit"]["zones"]["gem"]["count"], 3)

    def test_explicit_parent_still_overrides_section_entry(self):
        doc = {
            "default_tree": "main",
            "sections": [{"id": "take", "entry": "setup"}],
            "cues": [
                {
                    "id": "setup",
                    "tree": "main",
                    "events": [],
                    "script": {"exit": {"zones": {"gem": {"count": 3}}}},
                },
                {
                    "id": "other",
                    "tree": "main",
                    "events": [],
                    "script": {"exit": {"zones": {"gem": {"count": 1}}}},
                },
                {"id": "branch", "tree": "main", "section": "take", "parent": "other", "events": []},
            ],
        }

        resolved = inherit.resolve_track(copy.deepcopy(doc))
        by_id = {cue["id"]: cue for cue in resolved["cues"]}
        self.assertEqual(by_id["branch"]["script"]["exit"]["zones"]["gem"]["count"], 1)


if __name__ == "__main__":
    unittest.main()
