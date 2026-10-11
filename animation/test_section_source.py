#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Regression tests for the section-first source model."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import section_source  # noqa: E402


def write(path: Path, doc) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


class SectionSourceTests(unittest.TestCase):
    def _fixture(self, tutorial: Path) -> None:
        sections = tutorial / "sections"
        write(tutorial / "shared" / "full.base.anim.json", {
            "schema": "tutorial-anim/v2",
            "kind": "animation_track",
            "game": "fake",
            "track": "full",
            "default_tree": "main",
            "worlds": [],
            "trees": [],
        })
        write(tutorial / "tracks" / "full.track.json", {
            "schema": "tutorial-track/v1",
            "game": "fake",
            "track": "full",
            "title": "Fake",
            "version": "0.1.0",
            "base_anim": "../shared/full.base.anim.json",
            "sections": ["a", "b"],
        })
        write(sections / "a" / "section.json", {
            "id": "a", "title": "A", "group_path": ["1 A"], "order": 0, "tree": "main",
        })
        write(sections / "a" / "full.anim.json", {
            "section": "a",
            "cues": [{
                "id": "c1", "entry": "initial", "tree": "main", "events": [],
            }],
            "time_anchors": [
                {"id": "a.c1.start", "cue": "c1", "edge": "cue_start"},
            ],
        })
        write(sections / "a" / "full.script.json", {
            "section": "a",
            "cues": [{
                "id": "c1", "tts_asset": "intro.old", "group": "1 A",
                "beats": [{"id": "c1.b1", "text": "hello"}],
            }],
        })
        write(sections / "b" / "section.json", {
            "id": "b", "title": "B", "group_path": ["2 B"], "order": 1,
            "tree": "main", "entry": "a/c1",
        })
        write(sections / "b" / "full.anim.json", {
            "section": "b",
            "cues": [
                {"id": "d1", "tree": "main", "events": []},
                {"id": "d2", "parent": "d1", "tree": "main", "events": []},
            ],
            "time_anchors": [
                {"id": "b.d1.start", "cue": "d1", "edge": "cue_start"},
            ],
        })
        write(sections / "b" / "full.script.json", {
            "section": "b",
            "cues": [
                {"id": "d1", "group": "2 B", "beats": [{"id": "d1.b1", "text": "bye"}]},
                {"id": "d2", "group": "2 B", "beats": [{"id": "d2.b1", "text": "again"}]},
            ],
        })
        write(sections / "b" / "qa.json", {
            "section": "b",
            "asks": [{"cue": "d1", "q": "is d1 legal?"}],
        })

    def test_load_resolves_local_ids_and_section_entry(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            with mock.patch.object(section_source, "ROOT", root):
                src = section_source.load_track_source("fake", "full")
            self.assertEqual([c["id"] for c in src.anim_doc["cues"]],
                             ["a/c1", "b/d1", "b/d2"])
            self.assertEqual([c["id"] for c in src.script_doc["cues"]],
                             ["a/c1", "b/d1", "b/d2"])
            # Section B's first cue inherits section.entry (qualified cross-section).
            self.assertEqual(src.anim_doc["cues"][1]["entry"], "a/c1")
            # Same-section local parent is qualified in the resolved track.
            self.assertEqual(src.anim_doc["cues"][2]["parent"], "b/d1")
            self.assertEqual(src.anim_doc["cues"][0]["local_id"], "c1")
            self.assertEqual(src.anim_doc["cues"][1]["section"], "b")
            self.assertEqual(src.anim_doc["cues"][2]["local_index"], 1)
            self.assertEqual(src.script_doc["cues"][0]["tts_asset"], "intro.old")
            self.assertEqual(src.qa_asks[0]["cue"], "b/d1")

    def test_section_ref_resolves_to_section_start(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            write(tutorial / "sections" / "b" / "full.anim.json", {
                "section": "b",
                "cues": [
                    {"id": "d1", "entry": "section:a",
                     "tree": "main", "events": []},
                ],
                "time_anchors": [],
            })
            write(tutorial / "sections" / "b" / "full.script.json", {
                "section": "b",
                "cues": [{"id": "d1", "group": "2 B",
                          "beats": [{"id": "d1.b1", "text": "bye"}]}],
            })
            with mock.patch.object(section_source, "ROOT", root):
                src = section_source.load_track_source("fake", "full")
            self.assertEqual(src.anim_doc["cues"][1]["entry"], "initial")

    def test_cross_section_reference_must_be_qualified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            write(tutorial / "sections" / "b" / "full.anim.json", {
                "section": "b",
                "cues": [{"id": "d1", "parent": "c1",
                          "tree": "main", "events": []}],
                "time_anchors": [],
            })
            write(tutorial / "sections" / "b" / "full.script.json", {
                "section": "b",
                "cues": [{"id": "d1", "group": "2 B",
                          "beats": [{"id": "d1.b1", "text": "bye"}]}],
            })
            with mock.patch.object(section_source, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "cross-section references"):
                    section_source.load_track_source("fake", "full")

    def test_duplicate_local_cue_id_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            write(tutorial / "sections" / "b" / "full.anim.json", {
                "section": "b",
                "cues": [
                    {"id": "d1", "tree": "main", "events": []},
                    {"id": "d1", "tree": "main", "events": []},
                ],
                "time_anchors": [],
            })
            write(tutorial / "sections" / "b" / "full.script.json", {
                "section": "b",
                "cues": [{"id": "d1", "group": "2 B",
                          "beats": [{"id": "d1.b1", "text": "bye"}]}],
            })
            with mock.patch.object(section_source, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "duplicate local cue id"):
                    section_source.load_track_source("fake", "full")

    def test_anchor_ids_must_be_unique_across_sections(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            write(tutorial / "sections" / "b" / "full.anim.json", {
                "section": "b",
                "cues": [{"id": "d1", "tree": "main", "events": []}],
                "time_anchors": [
                    {"id": "a.c1.start", "cue": "d1", "edge": "cue_start"},
                ],
            })
            write(tutorial / "sections" / "b" / "full.script.json", {
                "section": "b",
                "cues": [{"id": "d1", "group": "2 B",
                          "beats": [{"id": "d1.b1", "text": "bye"}]}],
            })
            with mock.patch.object(section_source, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "duplicate time anchor"):
                    section_source.load_track_source("fake", "full")

    def test_section_source_wins_over_legacy_monolith(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            self._fixture(tutorial)
            legacy = tutorial / "anim" / "v2" / "full.anim.json"
            write(legacy, {"game": "fake", "track": "full",
                           "cues": [{"id": "legacy.1"}]})
            with mock.patch.object(section_source, "ROOT", root):
                anim_doc, _, _, _, is_section = section_source.load_track_docs(
                    "fake", "full", allow_legacy=True, warn=False)
            self.assertTrue(is_section)
            self.assertEqual(anim_doc["cues"][0]["id"], "a/c1")

    def test_cue_id_to_slug_encodes_slash(self):
        self.assertEqual(section_source.cue_id_to_slug("take_gems/different.001"),
                         "take_gems__different.001")

    def test_legacy_warning_is_explicit_when_no_descriptor(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            write(tutorial / "anim" / "v2" / "full.anim.json", {
                "game": "fake", "track": "full",
                "cues": [{"id": "legacy.1"}],
            })
            with mock.patch.object(section_source, "ROOT", root):
                doc, _, _, _, is_section = section_source.load_track_docs(
                    "fake", "full", allow_legacy=True, warn=False)
            self.assertFalse(is_section)
            self.assertEqual(doc["cues"][0]["id"], "legacy.1")


if __name__ == "__main__":
    unittest.main()
