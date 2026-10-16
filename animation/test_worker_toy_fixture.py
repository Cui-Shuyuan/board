#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Non-card fixture: same example used by detailed/compact orchestration."""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
FIXTURE = HERE.parent / "tests/fixtures/worker_toy/tutorial"
sys.path.insert(0, str(HERE))

import compile_animation_v2  # noqa: E402
import section_source  # noqa: E402


class WorkerToyFixtureTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        target = self.root / "content/games/worker_toy/tutorial"
        shutil.copytree(FIXTURE, target)
        self.patches = [
            mock.patch.object(section_source, "ROOT", self.root),
            mock.patch.object(compile_animation_v2, "ROOT", self.root),
        ]
        for p in self.patches:
            p.start()
        self.addCleanup(self._tmp.cleanup)
        self.addCleanup(lambda: [p.stop() for p in self.patches])

    def _load(self, track: str):
        return section_source.load_track_source("worker_toy", track)

    def _compile(self, src):
        compiler = compile_animation_v2.Compiler(
            self.root / "dummy.anim.json",
            doc=src.anim_doc,
            source_root=src.tutorial,
            source_hash=src.source_hash,
            script_doc=src.script_doc,
        )
        return compiler.compile()

    def test_same_example_different_orchestration(self):
        detailed = self._load("detailed")
        compact = self._load("compact")
        dc = self._compile(detailed)["cues"][0]
        cc = self._compile(compact)["cues"][0]

        # Different timing and camera shot, same logical material.
        self.assertNotEqual(dc["duration"], cc["duration"])
        self.assertEqual("shot_workshop", detailed.anim_doc["cues"][0]["events"][0]["shot"])
        self.assertEqual("shot_result", compact.anim_doc["cues"][0]["events"][0]["shot"])

        # Same example was instantiated: two workers of the same kind land in
        # different bound action slots with stable distinct identities.
        def zone_items(cue, zone):
            return [x for x in cue["end_state"]["components"] if x["ZoneId"] == zone]

        d_workers = [x for x in zone_items(dc, "action_slot_a") + zone_items(dc, "action_slot_b")
                     if x["TemplateId"] == "worker"]
        c_workers = [x for x in zone_items(cc, "action_slot_a") + zone_items(cc, "action_slot_b")
                     if x["TemplateId"] == "worker"]
        self.assertEqual(2, len(d_workers))
        self.assertEqual(2, len(c_workers))
        self.assertEqual(
            {"action_slot_a", "action_slot_b"},
            {x["ZoneId"] for x in d_workers},
        )
        self.assertEqual(2, len({x["Id"] for x in d_workers}))
        # Compact swaps the slot bindings; the example itself is unchanged.
        self.assertIn(c_workers[0]["ZoneId"], {"action_slot_a", "action_slot_b"})
        self.assertNotEqual(dc["state_ops"], cc["state_ops"])

        # Resources: amount is a bound parameter (2 vs 3).
        self.assertEqual(2, len(zone_items(dc, "resource_pool")))
        self.assertEqual(3, len(zone_items(cc, "resource_pool")))

    def test_material_change_invalidates_both_tracks(self):
        before = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        path = self.root / "content/games/worker_toy/tutorial/library/actions/worker_toy.action.gain_resource.action.json"
        doc = json.loads(path.read_text(encoding="utf-8"))
        doc["title"] = doc["title"] + "（变化）"
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        after = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        self.assertNotEqual(before, after)

    def test_stage_change_invalidates_both_tracks(self):
        before = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        path = self.root / "content/games/worker_toy/tutorial/shared/stage/worker_toy.stage.json"
        doc = json.loads(path.read_text(encoding="utf-8"))
        doc["shots"][0]["fill"] = 0.7
        path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        after = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        self.assertNotEqual(before, after)

    def test_unrelated_unreferenced_material_does_not_affect_hash(self):
        before = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        lib = self.root / "content/games/worker_toy/tutorial/library/actions"
        (lib / "worker_toy.action.unused.action.json").write_text(json.dumps({
            "schema": "tutorial-material/v1",
            "id": "worker_toy.action.unused",
            "kind": "action",
            "game": "worker_toy",
            "steps": [{"id": "x", "events": [{
                "op": "create", "count": 1, "anchor": "@add",
                "target": {"space": "entity", "zone": "resource_pool", "template": "other"},
            }]}],
        }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        after = {t: self._load(t).source_hash for t in ("detailed", "compact")}
        self.assertEqual(before, after)


if __name__ == "__main__":
    unittest.main()
