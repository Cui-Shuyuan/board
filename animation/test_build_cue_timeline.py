#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for the deterministic Phase 4 plan-set builder."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import build_cue_timeline as builder  # noqa: E402
import cue_timeline as timeline  # noqa: E402


class BuildCueTimelineTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.game = "fixturegame"
        self.track = "quick"
        tutorial = self.root / "content" / "games" / self.game / "tutorial"
        (tutorial / "anim" / "v2").mkdir(parents=True)
        self.runtime_path = tutorial / f"{self.track}.runtime.json"
        self.compiled_path = tutorial / "anim" / "v2" / f"{self.track}.compiled.json"
        self.runtime_path.write_text(json.dumps({
            "schema_version": 3,
            "game_id": self.game,
            "track": self.track,
            "sample_rate": 24000,
            "cues": [{
                "id": "quick/one",
                "start": 0.0,
                "duration": 2.0,
                "audio": "media/one.mp3",
                "audio_start": 0.75,
                "text": "hello",
                "group_path": ["chapter"],
                "refs": ["concept"],
                "entry": None,
                "subtitles": [{
                    "t": 0.0,
                    "end": 1.25,
                    "text": "hello subtitle",
                    "words": [{"word": "hello", "start": 0.0, "end": 0.5}],
                }],
            }],
        }, ensure_ascii=False), encoding="utf-8")
        self.compiled_path.write_text(json.dumps({
            "game": self.game,
            "track": self.track,
            "cues": [{"id": "quick/one", "duration": 3.5}],
        }), encoding="utf-8")

    def test_build_is_complete_validated_and_deterministic(self) -> None:
        runtime_before = self.runtime_path.read_bytes()
        compiled_before = self.compiled_path.read_bytes()

        first_path, first = builder.build_and_write(
            self.root, self.game, self.track)
        first_bytes = first_path.read_bytes()
        second_path, second = builder.build_and_write(
            self.root, self.game, self.track)

        self.assertEqual(first_path, second_path)
        self.assertEqual(first_bytes, second_path.read_bytes())
        self.assertEqual(first, second)
        self.assertEqual(runtime_before, self.runtime_path.read_bytes())
        self.assertEqual(compiled_before, self.compiled_path.read_bytes())

        parsed = json.loads(first_bytes)
        self.assertEqual(parsed["schema"], timeline.PLAN_SET_SCHEMA)
        self.assertEqual(parsed["schema_version"], timeline.PLAN_SET_VERSION)
        self.assertEqual(parsed["track"], self.track)
        self.assertEqual(parsed["cues"][0]["course"]["duration"], 3.5)
        self.assertEqual(parsed["cues"][0]["group_path"], ["chapter"])
        self.assertEqual(parsed["cues"][0]["text"], "hello")
        self.assertEqual(parsed["cues"][0]["subtitles"], [{
            "id": "subtitle.0",
            "cue_start": 0.0,
            "duration": 1.25,
            "text": "hello subtitle",
        }])
        self.assertIs(timeline.validate_plan_set(parsed), parsed)

    def test_content_audio_boundary_override_is_applied_and_validated(self) -> None:
        boundaries = self.root / "content" / "games" / self.game / "tutorial" / "quick.audio-boundaries.json"
        boundaries.write_text(json.dumps({
            "schema": builder.AUDIO_BOUNDARIES_SCHEMA,
            "schema_version": builder.AUDIO_BOUNDARIES_VERSION,
            "game_id": self.game,
            "track": self.track,
            "boundaries": [{
                "cue_id": "quick/one",
                "segment_id": "audio.single_audio",
                "source_start": 0.5,
                "reason": "synthetic waveform onset",
            }],
        }), encoding="utf-8")

        document = builder.build_plan_set(self.root, self.game, self.track)
        audio_track = next(
            track for track in document["cues"][0]["tracks"]
            if track["kind"] == "audio"
        )
        audio = audio_track["segments"][0]
        self.assertEqual(audio["source_start"], 0.5)
        self.assertEqual(audio["source_end"], 2.75)
        self.assertIn("audio_start_onset_guard",
                      [item["code"] for item in document["diagnostics"]])

        bad = json.loads(boundaries.read_text(encoding="utf-8"))
        bad["boundaries"][0]["cue_id"] = "missing/cue"
        boundaries.write_text(json.dumps(bad), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "unknown cue_id"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_missing_plan_input_is_an_explicit_build_error(self) -> None:
        self.compiled_path.unlink()
        with self.assertRaisesRegex(builder.BuildError, "missing input"):
            builder.build_plan_set(self.root, self.game, self.track)


if __name__ == "__main__":
    unittest.main()
