#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Regression tests for section-level TTS runtime expansion.

The expanded runtime is cue-order/media/subtitle data only.  Playback timing
must not sneak back in as audio-plan, section-audio or title pause fields.
"""
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

import build_section_runtime as bsr  # noqa: E402
import build_tutorial_runtime as btr  # noqa: E402
import compile_tutorial as ct  # noqa: E402
import section_source  # noqa: E402


class SectionRuntimeHelperTests(unittest.TestCase):
    def test_match_cue_starts_uses_word_boundaries(self):
        words = [
            {"word": "发展", "start": 0.3, "end": 0.6},
            {"word": "卡共", "start": 0.6, "end": 0.9},
            {"word": "90张，", "start": 0.9, "end": 1.4},
            {"word": "每级", "start": 2.0, "end": 2.3},
            {"word": "洗混后，", "start": 2.3, "end": 2.9},
        ]
        starts = bsr._match_cue_starts(
            ["发展卡共90张，", "每级洗混后，"], words, 3.5)
        self.assertEqual(len(starts), 2)
        self.assertEqual(starts[0], 0.0)
        self.assertAlmostEqual(starts[1], 2.0, places=3)

    def test_match_cue_starts_falls_back_to_proportional_split(self):
        starts = bsr._match_cue_starts(
            ["短句。", "这是一个更长的句子。"], [], 3.0)
        self.assertEqual(starts[0], 0.0)
        self.assertGreater(starts[1], 0.0)
        self.assertLess(starts[1], 3.0)

    def test_slice_subtitles_rebases_words(self):
        events = [{
            "t": 2.0, "end": 4.0, "text": "第二段",
            "words": [
                {"word": "第", "start": 2.1, "end": 2.3, "confidence": 0.9},
                {"word": "二", "start": 2.3, "end": 2.5, "confidence": 0.9},
                {"word": "段", "start": 2.5, "end": 2.7, "confidence": 0.9},
            ],
        }]
        sliced = bsr._slice_subtitles(events, 2.0, 4.0)
        self.assertEqual(len(sliced), 1)
        self.assertAlmostEqual(sliced[0]["t"], 0.0, places=3)
        self.assertAlmostEqual(sliced[0]["words"][0]["start"], 0.1, places=3)


class SectionRuntimePipelineTests(unittest.TestCase):
    def _fixture(self, root: Path) -> dict:
        tutorial = root / "content" / "games" / "fake" / "tutorial"
        media = root / "content" / "games" / "fake" / "media" / "tts" / "full"
        (media / "sec.a.mp3").parent.mkdir(parents=True, exist_ok=True)
        (media / "sec.a.mp3").write_bytes(b"")
        (media / "sec.a.subtitle.json").write_text(json.dumps([
            {
                "t": 0.0, "end": 1.5, "text": "第一句",
                "words": [
                    {"word": "第", "startTime": 0.1, "endTime": 0.4, "confidence": 0.9},
                    {"word": "一", "startTime": 0.4, "endTime": 0.7, "confidence": 0.9},
                    {"word": "句。", "startTime": 0.7, "endTime": 1.0, "confidence": 0.9},
                ],
            },
            {
                "t": 2.0, "end": 3.5, "text": "第二句",
                "words": [
                    {"word": "第", "startTime": 2.1, "endTime": 2.4, "confidence": 0.9},
                    {"word": "二", "startTime": 2.4, "endTime": 2.7, "confidence": 0.9},
                    {"word": "句。", "startTime": 2.7, "endTime": 3.0, "confidence": 0.9},
                ],
            },
        ], ensure_ascii=False), encoding="utf-8")

        def write(path: Path, document: dict) -> None:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(
                json.dumps(document, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8")

        write(tutorial / "shared" / "full.base.anim.json", {
            "schema": "tutorial-anim/v2", "game": "fake", "track": "full",
            "default_tree": "main", "worlds": [], "trees": [],
        })
        write(tutorial / "tracks" / "full.track.json", {
            "schema": "tutorial-track/v1", "game": "fake", "track": "full",
            "sections": ["a"], "base_anim": "../shared/full.base.anim.json",
        })
        write(tutorial / "sections" / "a" / "section.json", {
            "id": "a", "title": "A", "tts_asset": "sec.a", "tree": "main",
        })
        write(tutorial / "sections" / "a" / "full.anim.json", {
            "section": "a",
            "cues": [
                {"id": "title_a", "entry": "initial", "tree": "main", "events": []},
                {"id": "001", "tree": "main", "events": []},
            ],
            "time_anchors": [],
        })
        write(tutorial / "sections" / "a" / "full.script.json", {
            "section": "a",
            "cues": [
                {"id": "title_a", "group": "1 A", "refs": [],
                 "beats": [{"id": "a.title_a.b1", "text": "第一句"}]},
                {"id": "001", "group": "1 A", "refs": [],
                 "beats": [{"id": "a.001.b1", "text": "第二句"}]},
            ],
        })
        return {
            "units": "section",
            "voice": "v", "format": "mp3", "sample_rate": 24000,
            "gap_seconds": 0.0,
            "cues": [{
                "id": "a", "asset": "sec.a", "text": "第一句第二句",
                "duration": 4.0,
                "file": "content/games/fake/media/tts/full/sec.a.mp3",
                "subtitle_file": "content/games/fake/media/tts/full/sec.a.subtitle.json",
                "cue_ids": ["a/title_a", "a/001"],
            }],
        }

    def test_build_section_tts_entries_concatenates_cues(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._fixture(root)
            with mock.patch.object(section_source, "ROOT", root):
                src = section_source.load_track_source("fake", "full")
            entries = ct.build_section_tts_entries(src)
            self.assertEqual(len(entries), 1)
            self.assertEqual(entries[0]["id"], "a")
            self.assertEqual(entries[0]["asset"], "sec.a")
            self.assertEqual(entries[0]["text"], "第一句第二句")
            self.assertEqual(entries[0]["cue_ids"], ["a/title_a", "a/001"])

    def test_build_section_runtime_is_media_metadata_only(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = self._fixture(root)
            with mock.patch.object(bsr, "ROOT", root), \
                 mock.patch.object(btr, "ROOT", root), \
                 mock.patch.object(section_source, "ROOT", root):
                out = bsr.build_section_runtime(
                    "fake", "full", force=True, manifest=manifest)
            runtime = json.loads(out.read_text(encoding="utf-8"))
            self.assertEqual(runtime["schema_version"], 3)
            self.assertEqual(runtime["units"], "section")
            self.assertEqual(len(runtime["sections"]), 1)
            self.assertEqual(len(runtime["cues"]), 2)
            first, second = runtime["cues"]
            self.assertEqual(first["audio"], second["audio"])
            self.assertEqual(first["text"], "第一句")
            self.assertEqual(second["text"], "第二句")
            for cue in runtime["cues"]:
                self.assertNotIn("audio_plan", cue)
                self.assertNotIn("section_audio", cue)
                self.assertNotIn("pause_after_speech_seconds", cue)
                self.assertNotIn("hold_after_audio", cue)
                self.assertAlmostEqual(
                    cue["subtitles"][-1]["end"], cue["duration"], places=3)
            self.assertGreaterEqual(
                second["audio_start"], first["audio_start"])

    def test_cue_audio_override_supports_source_window(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = self._fixture(root)
            tutorial = root / "content" / "games" / "fake" / "tutorial"
            overrides = {
                "schema": "tutorial-cue-audio-overrides/v1",
                "track": "full",
                "cues": {
                    "a/001": {
                        "audio": "media/tts/full/sec.a.mp3",
                        "audio_start": 1.0,
                        "duration": 2.0,
                    },
                },
            }
            (tutorial / "full.cue-audio-overrides.json").write_text(
                json.dumps(overrides, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8")
            with mock.patch.object(bsr, "ROOT", root), \
                 mock.patch.object(btr, "ROOT", root), \
                 mock.patch.object(section_source, "ROOT", root), \
                 mock.patch.object(
                     bsr.audio_probe, "media_duration_seconds",
                     return_value=4.0):
                out = bsr.build_section_runtime(
                    "fake", "full", force=True, manifest=manifest)
            runtime = json.loads(out.read_text(encoding="utf-8"))
            second = runtime["cues"][1]
            self.assertEqual(second["audio_start"], 1.0)
            self.assertEqual(second["duration"], 2.0)
            self.assertEqual(second["animation"], None)


if __name__ == "__main__":
    unittest.main()
