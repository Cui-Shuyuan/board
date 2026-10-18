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
        visual_track = next(
            track for track in parsed["cues"][0]["tracks"]
            if track["kind"] == "visual"
        )
        self.assertEqual(visual_track["segments"][0]["source_end"], 3.5)
        self.assertEqual(parsed["cues"][0]["group_path"], ["chapter"])
        self.assertEqual(parsed["cues"][0]["text"], "hello")
        self.assertEqual(parsed["cues"][0]["subtitles"], [{
            "id": "subtitle.0",
            "cue_start": 0.0,
            "duration": 3.5,
            "text": "hello subtitle",
        }])
        self.assertIs(timeline.validate_plan_set(parsed), parsed)

    def test_per_cue_audio_policy_trims_visual_and_keeps_default_max_elsewhere(self) -> None:
        policy = self.root / "content" / "games" / self.game / "tutorial" / "quick.timing-policy.json"
        policy.write_text(json.dumps({
            "schema": builder.TIMING_POLICY_SCHEMA,
            "schema_version": builder.TIMING_POLICY_VERSION,
            "game_id": self.game,
            "track": self.track,
            "default_mode": "max",
            "cues": {"quick/one": "audio"},
        }), encoding="utf-8")

        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        self.assertEqual(plan["course"]["duration"], 2.0)
        visual_track = next(
            track for track in plan["tracks"] if track["kind"] == "visual")
        self.assertEqual(visual_track["segments"][0]["source_end"], 2.0)

        policy_doc = json.loads(policy.read_text(encoding="utf-8"))
        policy_doc["cues"]["missing/cue"] = "audio"
        policy.write_text(json.dumps(policy_doc), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "unknown cue ids"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_cue_tts_manifest_builds_whole_file_audio_tracks(self) -> None:
        game_dir = self.root / "content" / "games" / self.game
        media = game_dir / "media" / "tts" / "test"
        media.mkdir(parents=True)
        for name in ("a.mp3", "b.mp3"):
            (media / name).write_bytes(b"fake-mp3")
        subtitle = media / "a.subtitle.json"
        subtitle.write_text(json.dumps([
            {
                "text": "first beat",
                "words": [
                    {"word": "first", "startTime": 0.1, "endTime": 0.4},
                    {"word": "beat", "startTime": 0.4, "endTime": 0.8},
                ],
            },
        ]), encoding="utf-8")

        script_path = game_dir / "tutorial" / "build" / "quick.script.json"
        script_path.parent.mkdir(parents=True)
        script_path.write_text(json.dumps({
            "cues": [{
                "id": "quick/one",
                "group_path": ["chapter"],
                "refs": ["concept"],
                "beats": [
                    {"id": "quick/one.b1", "text": "first beat"},
                    {"id": "quick/one.b2", "text": "second beat"},
                ],
            }],
        }), encoding="utf-8")

        tts_manifest = media / "tts_manifest.json"
        tts_manifest.write_text(json.dumps({
            "sample_rate": 24000,
            "cues": [
                {
                    "id": "quick/one.b1",
                    "duration": 1.0,
                    "file": "media/tts/test/a.mp3",
                    "subtitle_file": "media/tts/test/a.subtitle.json",
                },
                {
                    "id": "quick/one.b2",
                    "duration": 1.5,
                    "file": "media/tts/test/b.mp3",
                },
            ],
        }), encoding="utf-8")

        policy = game_dir / "tutorial" / "quick.timing-policy.json"
        policy.write_text(json.dumps({
            "schema": builder.TIMING_POLICY_SCHEMA,
            "schema_version": builder.TIMING_POLICY_VERSION,
            "game_id": self.game,
            "track": self.track,
            "audio_source": "cue_tts_manifest",
            "tts_manifest": "media/tts/test/tts_manifest.json",
            "script_source": "tutorial/build/quick.script.json",
            "default_mode": "max",
            "cues": {
                "quick/one": {
                    "mode": "audio",
                    "trailing_silence_seconds": 1.0,
                },
            },
        }), encoding="utf-8")

        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        audio_track = next(
            track for track in plan["tracks"] if track["kind"] == "audio")
        segments = audio_track["segments"]
        self.assertEqual([segment["type"] for segment in segments],
                         ["audio_clip", "audio_clip", "silence"])
        self.assertEqual(segments[0]["source_start"], 0.0)
        self.assertEqual(segments[1]["source_start"], 0.0)
        self.assertEqual(segments[1]["cue_start"], 1.0)
        self.assertEqual(segments[2]["cue_start"], 2.5)
        self.assertEqual(segments[2]["duration"], 1.0)
        self.assertEqual(plan["course"]["duration"], 3.5)
        self.assertEqual(plan["subtitles"][0]["cue_start"], 0.1)
        self.assertEqual(plan["subtitles"][0]["duration"], 0.7)

    def test_shared_audio_group_assigns_contiguous_ranges_to_serial_cues(self) -> None:
        game_dir = self.root / "content" / "games" / self.game
        media = game_dir / "media" / "tts" / "test"
        media.mkdir(parents=True)
        (media / "shared.mp3").write_bytes(b"fake-mp3")
        (media / "shared.subtitle.json").write_text(json.dumps([
            {
                "text": "第一句，第二句。",
                "words": [
                    {"word": "第一句，", "startTime": 0.1, "endTime": 0.8},
                    {"word": "第二句。", "startTime": 1.0, "endTime": 1.8},
                ],
            },
        ]), encoding="utf-8")

        self.runtime_path.write_text(json.dumps({
            "schema_version": 3,
            "game_id": self.game,
            "track": self.track,
            "sample_rate": 24000,
            "cues": [
                {"id": "quick/one", "start": 0.0, "duration": 2.0,
                 "audio": "media/tts/test/shared.mp3", "audio_start": 0.0,
                 "text": "第一句，", "entry": None},
                {"id": "quick/two", "start": 2.0, "duration": 2.0,
                 "audio": "media/tts/test/shared.mp3", "audio_start": 0.0,
                 "text": "第二句。", "entry": None},
            ],
        }), encoding="utf-8")
        self.compiled_path.write_text(json.dumps({
            "game": self.game,
            "track": self.track,
            "cues": [
                {"id": "quick/one", "duration": 1.0},
                {"id": "quick/two", "duration": 1.0},
            ],
        }), encoding="utf-8")

        script_path = game_dir / "tutorial" / "build" / "quick.script.json"
        script_path.parent.mkdir(parents=True)
        script_path.write_text(json.dumps({
            "cues": [
                {"id": "quick/one", "group_path": ["chapter"],
                 "beats": [{"id": "quick/one.b1", "text": "第一句，"}]},
                {"id": "quick/two", "group_path": ["chapter"],
                 "beats": [{"id": "quick/two.b1", "text": "第二句。"}]},
            ],
        }), encoding="utf-8")
        (media / "tts_manifest.json").write_text(json.dumps({
            "sample_rate": 24000,
            "cues": [{
                "id": "quick/one_two.b1",
                "duration": 2.0,
                "file": "media/tts/test/shared.mp3",
                "subtitle_file": "media/tts/test/shared.subtitle.json",
            }],
        }), encoding="utf-8")
        (game_dir / "tutorial" / "quick.timing-policy.json").write_text(json.dumps({
            "schema": builder.TIMING_POLICY_SCHEMA,
            "schema_version": builder.TIMING_POLICY_VERSION,
            "game_id": self.game,
            "track": self.track,
            "audio_source": "cue_tts_manifest",
            "tts_manifest": "media/tts/test/tts_manifest.json",
            "script_source": "tutorial/build/quick.script.json",
            "default_mode": "max",
            "audio_groups": {
                "shared": {
                    "cue_ids": ["quick/one", "quick/two"],
                    "beat_id": "quick/one_two.b1",
                },
            },
            "cues": {},
        }), encoding="utf-8")

        document = builder.build_plan_set(self.root, self.game, self.track)
        first, second = document["cues"]
        first_audio = next(
            track for track in first["tracks"] if track["kind"] == "audio")["segments"][0]
        second_audio = next(
            track for track in second["tracks"] if track["kind"] == "audio")["segments"][0]
        self.assertEqual(first_audio["asset"], second_audio["asset"])
        self.assertEqual(first_audio["playback_group"], "shared")
        self.assertEqual(second_audio["playback_group"], "shared")
        self.assertEqual(first_audio["source_start"], 0.0)
        self.assertEqual(first_audio["source_end"], 0.9)
        self.assertEqual(second_audio["source_start"], 0.9)
        self.assertEqual(second_audio["source_end"], 2.0)
        self.assertEqual(second["course"]["start"], 1.0)

        split_dir = media / "split"
        split_dir.mkdir()
        for name in ("one.wav", "two.wav"):
            (split_dir / name).write_bytes(b"fake-wav")
        (split_dir / "slices_manifest.json").write_text(json.dumps({
            "schema": "tutorial-cue-audio-slices/v1",
            "schema_version": 1,
            "cues": [
                {"cue_id": "quick/one", "file": "media/tts/test/split/one.wav",
                 "duration": 0.9},
                {"cue_id": "quick/two", "file": "media/tts/test/split/two.wav",
                 "duration": 1.1},
            ],
        }), encoding="utf-8")
        policy_doc = json.loads(
            (game_dir / "tutorial" / "quick.timing-policy.json").read_text(encoding="utf-8"))
        policy_doc["slice_manifest"] = "media/tts/test/split/slices_manifest.json"
        (game_dir / "tutorial" / "quick.timing-policy.json").write_text(
            json.dumps(policy_doc), encoding="utf-8")

        sliced = builder.build_plan_set(self.root, self.game, self.track)
        sliced_first = next(
            track for track in sliced["cues"][0]["tracks"]
            if track["kind"] == "audio")["segments"][0]
        sliced_second = next(
            track for track in sliced["cues"][1]["tracks"]
            if track["kind"] == "audio")["segments"][0]
        self.assertIn("/split/one.wav", sliced_first["asset"])
        self.assertIn("/split/two.wav", sliced_second["asset"])
        self.assertEqual(sliced_first["source_start"], 0.0)
        self.assertEqual(sliced_second["source_start"], 0.0)
        self.assertNotIn("playback_group", sliced_first)
        self.assertNotIn("playback_group", sliced_second)

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
