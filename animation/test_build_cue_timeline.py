#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for the deterministic Phase 4 plan-set builder.

All fixture cue/timing ids and times are synthetic.  The fixed audio-slices
document is the only place the fixture supplies timing-point seconds, source
ranges, cue-to-slice mappings and per-cue timing policy.
"""

from __future__ import annotations

import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import build_audio_slices as audio_slice_builder  # noqa: E402
import build_cue_timeline as builder  # noqa: E402
import cue_timeline as timeline  # noqa: E402


class BuildCueTimelineTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.game = "fixturegame"
        self.track = "quick"
        self.game_dir = self.root / "content" / "games" / self.game
        self.tutorial = self.game_dir / "tutorial"
        (self.tutorial / "anim" / "v2").mkdir(parents=True)
        (self.game_dir / "media").mkdir(parents=True)
        self.runtime_path = self.tutorial / f"{self.track}.runtime.json"
        self.compiled_path = self.tutorial / "anim" / "v2" / f"{self.track}.compiled.json"
        self.script_path = self.tutorial / "build" / f"{self.track}.script.json"
        self.script_path.parent.mkdir(parents=True)
        self.policy_path = self.tutorial / f"{self.track}.timing-policy.json"
        self.slices_path = self.tutorial / f"{self.track}.audio-slices.json"
        self.audio_path = self.game_dir / "media" / "fixture-audio.mp3"
        self.audio_path.write_bytes(self.wav_bytes(120.0))

        self.write_runtime([{
            "id": "fixture/one",
            "start": 0.0,
            "duration": 1.0,
            "text": "runtime text",
            "group_path": ["Fixture Chapter"],
            "refs": ["fixture-ref"],
            "entry": None,
        }])
        self.write_script([{
            "id": "fixture/one",
            "group_path": ["Fixture Chapter"],
            "refs": ["fixture-ref"],
            "beats": [{"id": "fixture/one.b1", "text": "script text"}],
        }])
        self.write_compiled([{"id": "fixture/one", "duration": 3.5}])
        self.write_policy()
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.25},
                "fixture__one.speech_end": {"time": 1.0},
                "fixture__one.subtitle.0.start": {"time": 0.1},
                "fixture__one.subtitle.0.end": {"time": 0.8},
            },
            cues={
                "fixture/one": [{
                    "id": "fixture__one",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "fixture__one.start",
                    "end_ref": "fixture__one.end",
                    "speech_end_ref": "fixture__one.speech_end",
                    "subtitles": [{
                        "start_ref": "fixture__one.subtitle.0.start",
                        "end_ref": "fixture__one.subtitle.0.end",
                        "text": "fixture subtitle",
                    }],
                }],
            },
        )

    # ── fixture helpers ──────────────────────────────────────────────────

    @staticmethod
    def wav_bytes(duration_seconds: float, sample_rate: int = 8000) -> bytes:
        frame_count = max(1, int(round(duration_seconds * sample_rate)))
        data = b"\x80" * frame_count
        fmt = struct.pack(
            "<HHIIHH", 1, 1, sample_rate, sample_rate, 1, 8)
        chunks = (
            b"fmt " + struct.pack("<I", len(fmt)) + fmt
            + b"data" + struct.pack("<I", len(data)) + data
        )
        return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks

    def write_runtime(self, cues: list[dict]) -> None:
        self.runtime_path.write_text(json.dumps({
            "schema_version": 3,
            "game_id": self.game,
            "track": self.track,
            "sample_rate": 24000,
            "cues": cues,
        }, ensure_ascii=False), encoding="utf-8")

    def write_script(self, cues: list[dict]) -> None:
        self.script_path.write_text(json.dumps({
            "cues": cues,
        }, ensure_ascii=False), encoding="utf-8")

    def write_compiled(self, cues: list[dict]) -> None:
        self.compiled_path.write_text(json.dumps({
            "game": self.game,
            "track": self.track,
            "cues": cues,
        }), encoding="utf-8")

    def write_policy(self, *, default_mode: str = "max",
                     extra: dict | None = None) -> None:
        document = {
            "schema": builder.TIMING_POLICY_SCHEMA,
            "schema_version": builder.TIMING_POLICY_VERSION,
            "game_id": self.game,
            "track": self.track,
            "audio_source": "audio_slice_manifest",
            "audio_slice_manifest": f"tutorial/{self.track}.audio-slices.json",
            "script_source": f"tutorial/build/{self.track}.script.json",
            "default_mode": default_mode,
        }
        if extra:
            document.update(extra)
        self.policy_path.write_text(json.dumps(document), encoding="utf-8")

    def write_slices(self, *, timing_points: dict, cues: dict,
                     cue_timing: dict | None = None,
                     audio_groups: dict | None = None,
                     cue_visuals: dict | None = None) -> None:
        document = {
            "schema": builder.AUDIO_SLICES_SCHEMA,
            "schema_version": builder.AUDIO_SLICES_VERSION,
            "game_id": self.game,
            "track": self.track,
            "sample_rate": 24000,
            "cue_timing": cue_timing or {},
            "audio_groups": audio_groups or {},
            "timing_points": timing_points,
            "cues": cues,
        }
        if cue_visuals is not None:
            document["cue_visuals"] = cue_visuals
        self.slices_path.write_text(
            json.dumps(document, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")

    def default_slice(self, *, start_ref: str = "fixture__one.start",
                      end_ref: str = "fixture__one.end",
                      speech_ref: str = "fixture__one.speech_end") -> dict:
        return {
            "id": "fixture__one",
            "asset": "media/fixture-audio.mp3",
            "start_ref": start_ref,
            "end_ref": end_ref,
            "speech_end_ref": speech_ref,
            "subtitles": [{
                "start_ref": "fixture__one.subtitle.0.start",
                "end_ref": "fixture__one.subtitle.0.end",
                "text": "fixture subtitle",
            }],
        }

    def audio_segments(self, plan: dict) -> list[dict]:
        track = next(
            item for item in plan["tracks"] if item["kind"] == "audio")
        return track["segments"]

    @staticmethod
    def _audio_track_content_end(track: dict) -> float:
        end = 0.0
        for segment in track["segments"]:
            cue_start = float(segment.get("cue_start", 0.0))
            if segment["type"] == "silence":
                content_end = cue_start + float(segment["duration"])
            elif segment["type"] == "audio_clip":
                content_end = (
                    cue_start
                    + float(segment["source_end"])
                    - float(segment["source_start"]))
            else:
                continue
            end = max(end, content_end)
        return end

    # ── core build / determinism ────────────────────────────────────────

    def test_build_is_complete_validated_and_byte_deterministic(self) -> None:
        first_path, first = builder.build_and_write(
            self.root, self.game, self.track)
        first_bytes = first_path.read_bytes()
        second_path, second = builder.build_and_write(
            self.root, self.game, self.track)

        self.assertEqual(first_path, second_path)
        self.assertEqual(first_bytes, second_path.read_bytes())
        self.assertEqual(first, second)
        parsed = json.loads(first_bytes)
        self.assertEqual(parsed["schema"], timeline.PLAN_SET_SCHEMA)
        self.assertEqual(parsed["schema_version"], timeline.PLAN_SET_VERSION)
        self.assertEqual(parsed["track"], self.track)
        self.assertEqual(parsed["cues"][0]["course"]["duration"], 3.5)
        self.assertIs(timeline.validate_plan_set(parsed), parsed)
        self.assertEqual(
            parsed["course"]["duration"],
            parsed["cues"][-1]["course"]["start"]
            + parsed["cues"][-1]["course"]["duration"],
        )

        visual_track = next(
            track for track in parsed["cues"][0]["tracks"]
            if track["kind"] == "visual")
        self.assertEqual(visual_track["segments"][0]["source_end"], 3.5)
        audio = self.audio_segments(parsed["cues"][0])
        self.assertEqual(audio[0]["audio_slice_id"], "fixture__one")
        self.assertEqual(audio[0]["source_start"], 0.0)
        self.assertEqual(audio[0]["source_end"], 1.25)
        self.assertEqual(parsed["cues"][0]["group_path"], ["Fixture Chapter"])
        self.assertEqual(parsed["cues"][0]["text"], "runtime text")
        self.assertEqual(parsed["cues"][0]["subtitles"], [{
            "id": "subtitle.0.0",
            "cue_start": 0.1,
            "duration": 0.7,
            "text": "fixture subtitle",
        }])

    def test_plan_cue_ids_match_runtime_order_and_course_end(self) -> None:
        self.write_runtime([
            {"id": "fixture/first", "start": 0.0, "duration": 1.0,
             "text": "first", "entry": None},
            {"id": "fixture/second", "start": 1.0, "duration": 1.0,
             "text": "second", "entry": None},
        ])
        self.write_script([
            {"id": "fixture/first", "beats": [{"text": "first"}]},
            {"id": "fixture/second", "beats": [{"text": "second"}]},
        ])
        self.write_compiled([
            {"id": "fixture/first", "duration": 1.2},
            {"id": "fixture/second", "duration": 0.8},
        ])
        self.write_slices(
            timing_points={
                "fixture__first.start": {"time": 0.0},
                "fixture__first.end": {"time": 1.2},
                "fixture__first.speech_end": {"time": 1.1},
                "fixture__second.start": {"time": 0.0},
                "fixture__second.end": {"time": 0.8},
                "fixture__second.speech_end": {"time": 0.75},
            },
            cues={
                "fixture/first": [{
                    "id": "fixture__first",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "fixture__first.start",
                    "end_ref": "fixture__first.end",
                    "speech_end_ref": "fixture__first.speech_end",
                }],
                "fixture/second": [{
                    "id": "fixture__second",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "fixture__second.start",
                    "end_ref": "fixture__second.end",
                    "speech_end_ref": "fixture__second.speech_end",
                }],
            },
        )

        document = builder.build_plan_set(self.root, self.game, self.track)
        self.assertEqual(
            [cue["cue_id"] for cue in document["cues"]],
            ["fixture/first", "fixture/second"],
        )
        last = document["cues"][-1]
        self.assertEqual(
            document["course"]["duration"],
            last["course"]["start"] + last["course"]["duration"],
        )

    # ── single-source timing refs ───────────────────────────────────────

    def test_changing_one_timing_point_changes_plan_without_code_changes(self) -> None:
        self.write_policy(default_mode="max")
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.25},
                "fixture__one.speech_end": {"time": 1.0},
                "fixture__one.subtitle.0.start": {"time": 0.1},
                "fixture__one.subtitle.0.end": {"time": 0.8},
            },
            cues={"fixture/one": [self.default_slice()]},
            cue_timing={"fixture/one": {"mode": "audio"}},
        )
        before = builder.build_plan_set(self.root, self.game, self.track)
        self.assertEqual(self.audio_segments(before["cues"][0])[0]["source_end"], 1.25)
        self.assertEqual(before["cues"][0]["course"]["duration"], 1.25)

        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["timing_points"]["fixture__one.end"]["time"] = 1.5
        self.slices_path.write_text(
            json.dumps(slices, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        after = builder.build_plan_set(self.root, self.game, self.track)
        self.assertEqual(self.audio_segments(after["cues"][0])[0]["source_end"], 1.5)
        self.assertEqual(after["cues"][0]["course"]["duration"], 1.5)

    def test_deleting_referenced_timing_point_fails_with_missing_id(self) -> None:
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        del slices["timing_points"]["fixture__one.end"]
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")

        with self.assertRaisesRegex(
                builder.BuildError, "fixture__one.end"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_unknown_ref_and_duplicate_slice_id_fail_explicitly(self) -> None:
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["cues"]["fixture/one"][0]["end_ref"] = "fixture__one.missing"
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "fixture__one.missing"):
            builder.build_plan_set(self.root, self.game, self.track)

        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.0},
            },
            cues={"fixture/one": [{
                "id": "fixture__one",
                "asset": "media/fixture-audio.mp3",
                "start_ref": "fixture__one.start",
                "end_ref": "fixture__one.end",
            }]},
        )
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["cues"]["fixture/one"].append(
            dict(slices["cues"]["fixture/one"][0]))
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "duplicate audio slice id"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_duplicate_json_object_key_fails_explicitly(self) -> None:
        text = self.slices_path.read_text(encoding="utf-8")
        text = text.replace(
            '"timing_points": {',
            '"timing_points": {\n'
            '    "fixture.duplicate": {"time": 0.0},\n'
            '    "fixture.duplicate": {"time": 1.0},',
            1,
        )
        self.slices_path.write_text(text, encoding="utf-8")
        with self.assertRaisesRegex(
                builder.BuildError, "duplicate JSON object key"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_adding_synthetic_cue_mapping_only_changes_fixed_file(self) -> None:
        self.write_runtime([
            {"id": "fixture/one", "start": 0.0, "duration": 1.0,
             "text": "one", "entry": None},
            {"id": "fixture/two", "start": 1.0, "duration": 1.0,
             "text": "two", "entry": None},
        ])
        self.write_script([
            {"id": "fixture/one", "beats": [{"text": "one"}]},
            {"id": "fixture/two", "beats": [{"text": "two"}]},
        ])
        self.write_compiled([
            {"id": "fixture/one", "duration": 1.0},
            {"id": "fixture/two", "duration": 1.0},
        ])
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.0},
            },
            cues={"fixture/one": [{
                "id": "fixture__one",
                "asset": "media/fixture-audio.mp3",
                "start_ref": "fixture__one.start",
                "end_ref": "fixture__one.end",
            }]},
        )
        with self.assertRaisesRegex(builder.BuildError, "fixture/two"):
            builder.build_plan_set(self.root, self.game, self.track)

        # Only the fixed timeline file changes: add the second cue's timing
        # points and cue-to-slice mapping.  No production code changes.
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["timing_points"].update({
            "fixture__two.start": {"time": 0.0},
            "fixture__two.end": {"time": 1.0},
        })
        slices["cues"]["fixture/two"] = [{
            "id": "fixture__two",
            "asset": "media/fixture-audio.mp3",
            "start_ref": "fixture__two.start",
            "end_ref": "fixture__two.end",
        }]
        self.slices_path.write_text(
            json.dumps(slices, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        document = builder.build_plan_set(self.root, self.game, self.track)
        self.assertEqual(
            [cue["cue_id"] for cue in document["cues"]],
            ["fixture/one", "fixture/two"],
        )
        self.assertEqual(
            self.audio_segments(document["cues"][1])[0]["audio_slice_id"],
            "fixture__two",
        )

    def test_audio_groups_are_resolved_from_fixed_timeline(self) -> None:
        self.write_runtime([
            {"id": "fixture/one", "start": 0.0, "duration": 1.0,
             "text": "one", "entry": None},
            {"id": "fixture/two", "start": 1.0, "duration": 1.0,
             "text": "two", "entry": None},
        ])
        self.write_script([
            {"id": "fixture/one", "beats": [{"text": "one"}]},
            {"id": "fixture/two", "beats": [{"text": "two"}]},
        ])
        self.write_compiled([
            {"id": "fixture/one", "duration": 1.0},
            {"id": "fixture/two", "duration": 1.0},
        ])
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 0.5},
                "fixture__two.start": {"time": 0.5},
                "fixture__two.end": {"time": 1.0},
            },
            cues={
                "fixture/one": [{
                    "id": "fixture__one",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "fixture__one.start",
                    "end_ref": "fixture__one.end",
                }],
                "fixture/two": [{
                    "id": "fixture__two",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "fixture__two.start",
                    "end_ref": "fixture__two.end",
                }],
            },
            audio_groups={
                "fixture_shared_group": {
                    "cue_ids": ["fixture/one", "fixture/two"],
                    "slice_ids": ["fixture__one", "fixture__two"],
                },
            },
        )
        document = builder.build_plan_set(self.root, self.game, self.track)
        first = self.audio_segments(document["cues"][0])[0]
        second = self.audio_segments(document["cues"][1])[0]
        self.assertNotIn("playback_group", first)
        self.assertNotIn("playback_group", second)
        self.assertEqual(first["source_start"], 0.0)
        self.assertEqual(first["source_end"], 0.5)
        self.assertEqual(second["source_start"], 0.5)
        self.assertEqual(second["source_end"], 1.0)

    # ── explicit trim policy ────────────────────────────────────────────

    def _configure_title(self) -> None:
        self.write_runtime([{
            "id": "fixture/title",
            "start": 0.0,
            "duration": 1.0,
            "text": "title",
            "entry": None,
        }])
        self.write_script([{
            "id": "fixture/title",
            "beats": [{"text": "title"}],
        }])
        self.write_compiled([{"id": "fixture/title", "duration": 3.0}])
        self.write_slices(
            timing_points={
                "fixture__title.start": {"time": 0.0},
                "fixture__title.end": {"time": 1.656},
                "fixture__title.speech_end": {"time": 1.245},
                "fixture__title.subtitle.0.start": {"time": 0.285},
                "fixture__title.subtitle.0.end": {"time": 1.245},
            },
            cues={"fixture/title": [{
                "id": "fixture__title",
                "asset": "media/fixture-audio.mp3",
                "start_ref": "fixture__title.start",
                "end_ref": "fixture__title.end",
                "speech_end_ref": "fixture__title.speech_end",
                "subtitles": [{
                    "start_ref": "fixture__title.subtitle.0.start",
                    "end_ref": "fixture__title.subtitle.0.end",
                    "text": "title subtitle",
                }],
            }]},
            cue_timing={
                "fixture/title": {
                    "mode": "audio",
                    "trim_to_speech_end": True,
                    "trailing_silence_seconds": 1.0,
                },
            },
        )

    def test_title_trim_appends_exact_silence_and_sets_cue_duration(self) -> None:
        self._configure_title()
        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        audio = self.audio_segments(plan)
        self.assertEqual(audio[0]["source_start"], 0.0)
        self.assertEqual(audio[0]["source_end"], 1.245)
        self.assertEqual(audio[1]["type"], "silence")
        self.assertEqual(audio[1]["cue_start"], 1.245)
        self.assertEqual(audio[1]["duration"], 1.0)
        self.assertAlmostEqual(plan["course"]["duration"], 1.245 + 1.0, places=9)
        visual = next(
            track for track in plan["tracks"] if track["kind"] == "visual")
        self.assertAlmostEqual(
            visual["segments"][0]["source_end"], 2.245, places=9)
        self.assertAlmostEqual(
            plan["course"]["duration"],
            (audio[0]["source_end"] - audio[0]["source_start"])
            + audio[1]["duration"],
            places=9,
        )

    def test_title_trim_tail_guard_extends_audio_before_silence(self) -> None:
        self._configure_title()
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["cue_timing"]["fixture/title"]["trim_tail_guard_seconds"] = 0.08
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        audio = self.audio_segments(plan)
        expected_end = 1.245 + 0.08
        self.assertAlmostEqual(audio[0]["source_end"], expected_end, places=9)
        self.assertAlmostEqual(audio[1]["cue_start"], expected_end, places=9)
        self.assertAlmostEqual(audio[1]["duration"], 1.0, places=9)
        self.assertAlmostEqual(
            plan["course"]["duration"], expected_end + 1.0, places=9)

    def test_trim_clips_subtitles_to_trimmed_cue_duration(self) -> None:
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.25},
                "fixture__one.speech_end": {"time": 0.5},
                "fixture__one.subtitle.0.start": {"time": 0.1},
                "fixture__one.subtitle.0.end": {"time": 0.9},
            },
            cues={"fixture/one": [self.default_slice()]},
            cue_timing={
                "fixture/one": {
                    "mode": "audio",
                    "trim_to_speech_end": True,
                    "trailing_silence_seconds": 0.1,
                },
            },
        )
        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        subtitle = next(
            track for track in plan["tracks"]
            if track["kind"] == "subtitle")["segments"][0]
        self.assertAlmostEqual(plan["course"]["duration"], 0.6, places=9)
        self.assertAlmostEqual(subtitle["cue_start"], 0.1, places=9)
        self.assertAlmostEqual(subtitle["duration"], 0.5, places=9)
        self.assertIs(timeline.validate_plan_set(document), document)

    def test_trim_without_speech_end_ref_fails_explicitly(self) -> None:
        self._configure_title()
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        del slices["cues"]["fixture/title"][0]["speech_end_ref"]
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "speech_end_ref"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_trim_speech_end_outside_slice_fails_explicitly(self) -> None:
        self._configure_title()
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["timing_points"]["fixture__title.speech_end"]["time"] = 1.8
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "within source range"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_trim_zero_or_negative_duration_fails_explicitly(self) -> None:
        self._configure_title()
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["timing_points"]["fixture__title.speech_end"]["time"] = 0.0
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "non-empty"):
            builder.build_plan_set(self.root, self.game, self.track)

        slices["timing_points"]["fixture__title.speech_end"]["time"] = -0.001
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError, "finite and >= 0"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_title_trim_keeps_source_context_lead(self) -> None:
        self.write_runtime([{
            "id": "fixture/title",
            "start": 0.0,
            "duration": 1.656,
            "text": "2.4 title",
            "entry": None,
            "audio": "media/fixture-audio.mp3",
            "audio_start": 10.0,
            "subtitles": [{
                "t": 0.285,
                "end": 1.656,
                "text": "2.4 title",
                "words": [
                    {"word": "2.4,", "start": 0.0, "end": 0.03},
                    {"word": "ti", "start": 0.31, "end": 0.54},
                    {"word": "tle", "start": 0.54, "end": 1.10},
                ],
            }],
        }])
        self.write_script([{
            "id": "fixture/title",
            "beats": [{"text": "2.4 title"}],
        }])
        self.write_compiled([{"id": "fixture/title", "duration": 2.0}])
        self.write_slices(
            timing_points={},
            cues={},
            cue_timing={
                "fixture/title": {
                    "mode": "audio",
                    "trim_to_speech_end": True,
                    "trailing_silence_seconds": 1.0,
                    "trim_tail_guard_seconds": 0.08,
                },
            },
        )

        slice_document = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        self.slices_path.write_text(
            json.dumps(slice_document, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")
        plan_document = builder.build_plan_set(
            self.root, self.game, self.track)
        plan = plan_document["cues"][0]
        audio = self.audio_segments(plan)
        self.assertAlmostEqual(audio[0]["source_start"], 9.85, places=9)
        self.assertAlmostEqual(audio[0]["source_end"], 11.18, places=9)
        self.assertAlmostEqual(audio[1]["cue_start"], 1.33, places=9)
        self.assertAlmostEqual(audio[1]["duration"], 1.0, places=9)
        self.assertAlmostEqual(plan["course"]["duration"], 2.33, places=9)
        subtitle = next(
            track for track in plan["tracks"]
            if track["kind"] == "subtitle"
        )["segments"][0]
        self.assertAlmostEqual(subtitle["cue_start"], 0.435, places=9)
        self.assertAlmostEqual(subtitle["duration"], 0.895, places=9)

    # ── release/input consistency and source boundaries ─────────────────

    def test_adjacent_cues_share_boundary_even_when_available_lead_differs(self) -> None:
        self.write_runtime([
            {"id": "fixture/body", "audio": "media/fixture-audio.mp3",
             "audio_start": 10.0, "start": 0.0, "duration": 2.0,
             "subtitles": [{"t": 0.0, "end": 2.0, "text": "body",
                            "words": [{"end": 1.95}]}]},
            {"id": "fixture/title", "audio": "media/fixture-audio.mp3",
             "audio_start": 12.0, "start": 2.0, "duration": 2.0,
             "subtitles": [{"t": 0.0, "end": 2.0, "text": "title",
                            "words": [{"end": 1.0}]}]},
        ])
        self.write_compiled([])
        self.write_slices(timing_points={}, cues={}, cue_timing={
            "fixture/title": {"mode": "audio", "trim_to_speech_end": True,
                              "trim_tail_guard_seconds": 0.08,
                              "trailing_silence_seconds": 1.0}})
        doc = audio_slice_builder.build_audio_slices(self.root, self.game, self.track)
        left = doc["cues"]["fixture/body"][0]
        right = doc["cues"]["fixture/title"][0]
        points = doc["timing_points"]
        self.assertEqual(points[left["end_ref"]]["time"], points[right["start_ref"]]["time"])
        self.assertGreaterEqual(points[left["end_ref"]]["time"], 11.95)
        self.assertEqual(doc["source_boundary_contracts"][0]["kind"], "continuous")
        builder._validate_source_boundary_contracts(doc)

    def test_trimmed_tail_clamps_next_context_lead_at_boundary(self) -> None:
        self.write_runtime([
            {"id": "fixture/left", "audio": "media/fixture-audio.mp3",
             "audio_start": 10.0, "start": 0.0, "duration": 2.0,
             "subtitles": [{"t": 0.0, "end": 2.0, "text": "left",
                            "words": [{"end": 1.9}]}]},
            {"id": "fixture/right", "audio": "media/fixture-audio.mp3",
             "audio_start": 12.0, "start": 2.0, "duration": 2.0,
             "subtitles": [{"t": 0.0, "end": 2.0, "text": "right",
                            "words": [{"end": 1.5}]}]},
        ])
        self.write_compiled([])
        self.write_slices(timing_points={}, cues={}, cue_timing={
            "fixture/left": {
                "mode": "audio",
                "trim_to_speech_end": True,
                "trim_tail_guard_seconds": 0.08,
                "trailing_silence_seconds": 0.5,
            },
            "fixture/right": {
                "mode": "audio",
                "trim_to_speech_end": True,
                "trim_tail_guard_seconds": 0.08,
                "trailing_silence_seconds": 0.5,
            },
        })
        doc = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        left = doc["cues"]["fixture/left"][0]
        right = doc["cues"]["fixture/right"][0]
        points = doc["timing_points"]
        left_end = points[left["end_ref"]]["time"]
        right_start = points[right["start_ref"]]["time"]
        self.assertAlmostEqual(left_end, 11.98, places=9)
        self.assertAlmostEqual(right_start, left_end, places=9)
        self.assertEqual(
            doc["source_boundary_contracts"][0]["kind"], "trimmed_tail")
        builder._validate_source_boundary_contracts(doc)

    def test_trimmed_tail_contract_rejects_source_overlap(self) -> None:
        doc = audio_slice_builder.build_audio_slices(ROOT, "splendor", "full")
        slices = {s["id"]: s for group in doc["cues"].values() for s in group}
        points = doc["timing_points"]
        pair = min(
            (c for c in doc["source_boundary_contracts"]
             if c["kind"] == "trimmed_tail"),
            key=lambda c: (
                points[slices[c["right_slice"]]["start_ref"]]["time"]
                - points[slices[c["left_slice"]]["end_ref"]]["time"]),
        )
        point = points[slices[pair["right_slice"]]["start_ref"]]
        original = point["time"]
        point["time"] = original - 0.15
        with self.assertRaisesRegex(builder.BuildError, "violates trimmed_tail"):
            builder._validate_source_boundary_contracts(doc)
        point["time"] = original
        self.assertIsNone(builder._validate_source_boundary_contracts(doc))
        # The compiler also checks resolved ranges after applying trim policy.
        resolved = {
            sid: {
                "source_start": doc["timing_points"][s["start_ref"]]["time"],
                "source_end": doc["timing_points"][s["end_ref"]]["time"],
            }
            for sid, s in slices.items()
        }
        resolved[pair["left_slice"]]["source_end"] += 0.15
        with self.assertRaisesRegex(builder.BuildError, "violates trimmed_tail"):
            builder._validate_source_boundary_contracts(doc, resolved)

    def test_full_numeric_title_boundaries_and_trimmed_tail_gaps(self) -> None:
        doc = audio_slice_builder.build_audio_slices(ROOT, "splendor", "full")
        builder._validate_source_boundary_contracts(doc)
        for cue_id in ("setup_players/title_2_4", "setup_players/title_2_5"):
            sid = doc["cues"][cue_id][0]["id"]
            contract = next(
                c for c in doc["source_boundary_contracts"]
                if c["right_slice"] == sid)
            self.assertEqual(contract["kind"], "trimmed_tail")
        self.assertTrue(any(
            c["kind"] == "trimmed_tail"
            for c in doc["source_boundary_contracts"]))

    def test_audio_source_range_must_fit_actual_media_duration(self) -> None:
        self.audio_path.write_bytes(self.wav_bytes(0.5))
        with self.assertRaisesRegex(builder.BuildError, "exceeds media duration"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_audio_slice_builder_rejects_runtime_range_beyond_media(self) -> None:
        self.audio_path.write_bytes(self.wav_bytes(0.5))
        self.write_runtime([{
            "id": "fixture/one",
            "section": "fixture",
            "start": 0.0,
            "duration": 1.0,
            "audio": "media/fixture-audio.mp3",
            "audio_start": 0.0,
            "text": "runtime text",
            "subtitles": [],
        }])
        with self.assertRaisesRegex(
                audio_slice_builder.BuildError, "beyond media duration"):
            audio_slice_builder.build_audio_slices(
                self.root, self.game, self.track)

    def test_missing_plan_audio_file_fails_build(self) -> None:
        self.audio_path.unlink()
        with self.assertRaisesRegex(
                builder.BuildError, "asset file not found|missing audio file"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_timing_policy_only_has_global_policy_and_builder_rejects_cues(self) -> None:
        actual_policy_path = (
            ROOT / "content" / "games" / "splendor" / "tutorial"
            / "quick.timing-policy.json"
        )
        actual_policy = json.loads(actual_policy_path.read_text(encoding="utf-8"))
        self.assertTrue(set(actual_policy).issubset(builder.TIMING_POLICY_GLOBAL_KEYS))
        self.assertNotIn("cues", actual_policy)
        self.assertNotIn("audio_groups", actual_policy)

        actual_slices = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "quick.audio-slices.json").read_text(encoding="utf-8")
        )
        self.assertIsInstance(actual_slices.get("cue_timing"), dict)
        self.assertIsInstance(actual_slices.get("audio_groups"), dict)

        self.write_policy(extra={"cues": {"fixture/one": {"mode": "audio"}}})
        with self.assertRaisesRegex(builder.BuildError, "timing-policy"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_production_builder_contains_no_concrete_splendor_timeline_ids(self) -> None:
        source = Path(builder.__file__).read_text(encoding="utf-8")
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        concrete_ids: set[str] = set()
        for track in ("quick", "full"):
            actual_slices = json.loads(
                (tutorial / f"{track}.audio-slices.json").read_text(
                    encoding="utf-8")
            )
            concrete_ids.update(actual_slices["cues"])
            concrete_ids.update(actual_slices["timing_points"])
            for slices in actual_slices["cues"].values():
                for raw_slice in slices:
                    concrete_ids.add(str(raw_slice["id"]))
            concrete_ids.update(actual_slices.get("cue_timing", {}))
            for visuals in actual_slices.get("cue_visuals", {}).values():
                for visual in visuals:
                    concrete_ids.add(str(visual["id"]))
        # Group ids such as "end" are generic section names, not concrete cue
        # or timing-point identifiers.  The assertion targets authoring IDs
        # that uniquely identify a cue/slice/timing point.
        concrete_ids = {
            value for value in concrete_ids
            if len(value) >= 6 and ("/" in value or "__" in value or "." in value)
        }
        for concrete_id in sorted(concrete_ids):
            self.assertNotIn(concrete_id, source)

    def test_splendor_catalog_enables_both_plan_tracks(self) -> None:
        catalog = json.loads(
            (ROOT / "content" / "catalog" / "splendor.json").read_text(
                encoding="utf-8")
        )
        self.assertEqual(catalog["cue_timeline_tracks"], ["quick", "full"])
        self.assertEqual(
            set(catalog["cue_timeline_tracks"]),
            set(catalog["tutorial_tracks"]),
        )

    def test_fixed_timeline_can_declare_multiple_visual_animation_segments(self) -> None:
        self.write_compiled([
            {"id": "fixture/opening", "duration": 2.0},
            {"id": "fixture/closing", "duration": 12.0},
        ])
        self.write_runtime([{
            "id": "fixture/one",
            "start": 0.0,
            "duration": 1.0,
            "text": "runtime text",
            "entry": None,
        }])
        self.write_script([{
            "id": "fixture/one",
            "beats": [{"text": "script text"}],
        }])
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.25},
                "fixture__one.visual.first.start": {"time": 0.0},
                "fixture__one.visual.first.source_start": {"time": 0.0},
                "fixture__one.visual.first.source_end": {"time": 2.0},
                "fixture__one.visual.second.start": {"time": 2.5},
                "fixture__one.visual.second.source_start": {"time": 10.0},
                "fixture__one.visual.second.source_end": {"time": 12.0},
                "fixture__one.speech_end": {"time": 1.0},
                "fixture__one.subtitle.0.start": {"time": 0.1},
                "fixture__one.subtitle.0.end": {"time": 0.8},
            },
            cues={"fixture/one": [self.default_slice()]},
            cue_visuals={"fixture/one": [
                {
                    "id": "v.first",
                    "animation": "fixture/opening",
                    "cue_start_ref": "fixture__one.visual.first.start",
                    "source_start_ref": "fixture__one.visual.first.source_start",
                    "source_end_ref": "fixture__one.visual.first.source_end",
                    "after_end": "hold_last_frame",
                },
                {
                    "id": "v.second",
                    "animation": "fixture/closing",
                    "cue_start_ref": "fixture__one.visual.second.start",
                    "source_start_ref": "fixture__one.visual.second.source_start",
                    "source_end_ref": "fixture__one.visual.second.source_end",
                    "after_end": "clear",
                },
            ]},
        )
        document = builder.build_plan_set(self.root, self.game, self.track)
        visual = next(
            track for track in document["cues"][0]["tracks"]
            if track["kind"] == "visual")
        self.assertEqual([segment["id"] for segment in visual["segments"]],
                         ["v.first", "v.second"])
        self.assertEqual([segment["animation"] for segment in visual["segments"]],
                         ["fixture/opening", "fixture/closing"])
        self.assertEqual(document["cues"][0]["course"]["duration"], 4.5)
        self.assertIs(timeline.validate_plan_set(document), document)

    def test_duplicate_visual_segment_or_unknown_animation_fails(self) -> None:
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 1.25},
                "fixture__one.visual.start": {"time": 0.0},
                "fixture__one.visual.source_start": {"time": 0.0},
                "fixture__one.visual.source_end": {"time": 2.0},
                "fixture__one.speech_end": {"time": 1.0},
                "fixture__one.subtitle.0.start": {"time": 0.1},
                "fixture__one.subtitle.0.end": {"time": 0.8},
            },
            cues={"fixture/one": [self.default_slice()]},
            cue_visuals={"fixture/one": [{
                "id": "v.duplicate",
                "animation": "fixture/one",
                "cue_start_ref": "fixture__one.visual.start",
                "source_start_ref": "fixture__one.visual.source_start",
                "source_end_ref": "fixture__one.visual.source_end",
                "after_end": "hold_last_frame",
            }]},
        )
        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["cues"]["fixture/one"][0]["id"] = "fixture__one"
        slices["cue_visuals"]["fixture/one"].append(
            dict(slices["cue_visuals"]["fixture/one"][0]))
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError,
                                    "duplicate visual segment id"):
            builder.build_plan_set(self.root, self.game, self.track)

        slices = json.loads(self.slices_path.read_text(encoding="utf-8"))
        slices["cue_visuals"]["fixture/one"][1]["id"] = "v.second"
        slices["cue_visuals"]["fixture/one"][1]["animation"] = "fixture/missing"
        self.slices_path.write_text(json.dumps(slices), encoding="utf-8")
        with self.assertRaisesRegex(builder.BuildError,
                                    "unknown compiled animation"):
            builder.build_plan_set(self.root, self.game, self.track)

    def test_full_track_plan_has_83_cues_and_is_deterministic(self) -> None:
        tutorial = ROOT / "content" / "games" / "splendor" / "tutorial"
        first = builder.build_plan_set(ROOT, "splendor", "full")
        second = builder.build_plan_set(ROOT, "splendor", "full")
        self.assertEqual(first, second)
        self.assertEqual(len(first["cues"]), 83)
        self.assertEqual(first["track"], "full")
        last = first["cues"][-1]
        self.assertAlmostEqual(
            first["course"]["duration"],
            last["course"]["start"] + last["course"]["duration"],
            places=9,
        )
        self.assertIs(timeline.validate_plan_set(first), first)
        self.assertTrue((tutorial / "full.audio-slices.json").is_file())
        self.assertTrue((tutorial / "full.timing-policy.json").is_file())
        self.assertTrue((tutorial / "full.cue-timeline.json").is_file())

    def test_zero_duration_compiled_cue_emits_state_visual_segment(self) -> None:
        self.write_compiled([{"id": "fixture/one", "duration": 0.0}])
        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        visual = next(
            track for track in plan["tracks"] if track["kind"] == "visual")
        self.assertEqual(visual["segments"][0]["type"], "state")
        self.assertEqual(
            visual["segments"][0]["animation"], "fixture/one")
        self.assertAlmostEqual(
            visual["segments"][0]["duration"],
            plan["course"]["duration"],
            places=9,
        )
        self.assertIs(timeline.validate_plan_set(document), document)

    def test_full_zero_duration_compiled_cues_keep_state_visuals(self) -> None:
        compiled = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "anim" / "v2" / "full.compiled.json").read_text("utf-8"))
        zero_ids = {
            str(cue["id"]) for cue in compiled["cues"]
            if float(cue.get("duration", 0.0) or 0.0) <= 0.0
        }
        document = builder.build_plan_set(ROOT, "splendor", "full")
        for cue in document["cues"]:
            visual_segments = [
                segment
                for track in cue["tracks"] if track["kind"] == "visual"
                for segment in track["segments"]
            ]
            self.assertTrue(
                visual_segments,
                f"{cue['cue_id']} has no visual track in the plan")
            if cue["cue_id"] in zero_ids:
                self.assertEqual(visual_segments[0]["type"], "state")
                self.assertAlmostEqual(
                    visual_segments[0]["duration"],
                    cue["course"]["duration"],
                    places=9,
                )

    def test_full_parent_chapter_titles_use_independent_audio_slices(self) -> None:
        document = builder.build_plan_set(ROOT, "splendor", "full")
        expectations = {
            "2，设置。": (
                ["2 设置"],
                "media/tts/full/setup.intro.mp3",
                1.345,
            ),
            "3.2，发展卡牌与贵族。": (
                ["3 行动", "3.2 发展卡牌与贵族"],
                "media/tts/full/action.cards.group.mp3",
                3.245,
            ),
        }
        for text, (group_path, asset, source_end) in expectations.items():
            cue = next(item for item in document["cues"]
                       if item["text"] == text)
            next_cue = document["cues"][document["cues"].index(cue) + 1]
            self.assertEqual(cue["group_path"], group_path)
            self.assertEqual(cue["tracks"][0]["kind"], "visual")
            audio_segment = next(
                segment
                for track in cue["tracks"] if track["kind"] == "audio"
                for segment in track["segments"]
                if segment["type"] == "audio_clip"
            )
            next_audio_segment = next(
                segment
                for track in next_cue["tracks"] if track["kind"] == "audio"
                for segment in track["segments"]
                if segment["type"] == "audio_clip"
            )
            self.assertEqual(audio_segment["asset"], asset)
            self.assertNotEqual(
                audio_segment["asset"], next_audio_segment["asset"])
            self.assertEqual(audio_segment["source_start"], 0.0)
            self.assertAlmostEqual(
                audio_segment["source_end"], source_end, places=9)
            audio_track = next(
                track for track in cue["tracks"] if track["kind"] == "audio")
            self.assertEqual(audio_track["segments"][-1]["type"], "silence")
            self.assertAlmostEqual(
                audio_track["segments"][-1]["duration"], 0.5, places=9)
            self.assertTrue(
                (ROOT / "content" / "games" / "splendor"
                 / audio_segment["asset"]).is_file())

    def test_every_declared_chapter_has_a_direct_cue(self) -> None:
        for track in ("quick", "full"):
            document = builder.build_plan_set(ROOT, "splendor", track)
            direct_paths = {
                tuple(cue["group_path"])
                for cue in document["cues"]
                if cue["group_path"]
            }
            all_paths: set[tuple[str, ...]] = set()
            for path in direct_paths:
                for end in range(1, len(path) + 1):
                    all_paths.add(path[:end])
            missing = sorted(all_paths - direct_paths)
            self.assertEqual(missing, [], f"{track} chapters without own cue")

    def test_full_market_deal_animation_spacing_is_uniform(self) -> None:
        compiled = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "anim" / "v2" / "full.compiled.json").read_text("utf-8"))
        cue = next(
            item for item in compiled["cues"]
            if item["id"] == "setup_cards/002.1")
        flips = [
            clip for clip in cue["clips"]
            if clip.get("kind") == "flip"
        ]
        self.assertEqual(len(flips), 12)
        spacings = [
            round(flips[index + 1]["at"] - flips[index]["at"], 9)
            for index in range(len(flips) - 1)
        ]
        self.assertEqual(set(spacings), {0.39})

    def test_full_noble_anatomy_uses_independent_markers_and_labels(self) -> None:
        compiled = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "anim" / "v2" / "full.compiled.json").read_text(
                 encoding="utf-8"))
        cue = next(
            item for item in compiled["cues"]
            if item["id"] == "nobles/intro.001")
        markers = {
            clip.get("marker_key")
            for clip in cue["clips"]
            if clip.get("kind") in {"point", "shape"}
        }
        self.assertIn("noble_score_circle", markers)
        self.assertIn("noble_condition_box", markers)
        texts = {
            clip.get("text")
            for clip in cue["clips"]
            if clip.get("kind") == "label"
        }
        self.assertIn("分数", texts)
        self.assertIn("条件\n（回合结束满足条件时拿取）", texts)

    def test_full_action_headers_persist_across_action_section_cues(self) -> None:
        compiled = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "anim" / "v2" / "full.compiled.json").read_text(
                 encoding="utf-8"))
        expected = {
            "take_gems/": "hint_action_first",
            "cards/": "hint_action_second",
            "reserve/": "hint_action_third",
        }
        checked = 0
        for cue in compiled["cues"]:
            cue_id = cue["id"]
            target = next(
                (value for prefix, value in expected.items()
                 if cue_id.startswith(prefix)), None)
            if target is None or cue["local_id"].startswith("title"):
                continue
            action_labels = [
                clip for clip in cue.get("clips", [])
                if clip.get("kind") == "label"
                and clip.get("overlay") == target
                and clip.get("label_key") == "action_hint"
                and clip.get("label_mode") == "replace"
            ]
            self.assertEqual(
                len(action_labels), 1,
                f"{cue_id} is missing its persistent action header")
            checked += 1
        self.assertGreaterEqual(checked, 20)

    def test_authoring_animation_sources_have_no_legacy_pause_hold_fields(self) -> None:
        legacy_keys = {"pause_after_speech_seconds", "hold_after_audio"}
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        paths = list((tutorial / "sections").rglob("*.anim.json"))
        paths += list((tutorial / "library").rglob("*.json"))
        self.assertTrue(paths)
        found = []
        def walk(value, path):
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in legacy_keys:
                        found.append(f"{path}:{key}")
                    walk(child, f"{path}/{key}")
            elif isinstance(value, list):
                for index, child in enumerate(value):
                    walk(child, f"{path}/{index}")
        for path in paths:
            document = json.loads(path.read_text(encoding="utf-8"))
            walk(document, str(path))
        self.assertEqual(found, [])

    def test_full_body_cues_keep_source_context_before_first_word(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        slices = json.loads(
            (tutorial / "full.audio-slices.json").read_text(encoding="utf-8"))
        runtime = json.loads(
            (tutorial / "full.runtime.json").read_text(encoding="utf-8"))
        runtime_by_id = {cue["id"]: cue for cue in runtime["cues"]}
        timing_points = slices["timing_points"]

        title = next(cue for cue in runtime["cues"]
                     if cue["id"] == "intro/title_1")
        title_slice = slices["cues"]["intro/title_1"][0]
        self.assertAlmostEqual(
            timing_points[title_slice["end_ref"]]["time"],
            timing_points[title_slice["speech_end_ref"]]["time"] + 0.08,
            places=9,
        )

        body_id = "intro/001"
        body_slice = slices["cues"][body_id][0]
        body_start = timing_points[body_slice["start_ref"]]["time"]
        body_end = timing_points[body_slice["end_ref"]]["time"]
        speech_end = timing_points[body_slice["speech_end_ref"]]["time"]
        self.assertLess(body_start, runtime_by_id[body_id]["audio_start"])
        self.assertAlmostEqual(body_end, speech_end + 0.08, places=9)
        self.assertLess(body_end - body_start, runtime_by_id[body_id]["duration"])
        self.assertGreaterEqual(speech_end, body_start + 0.1)

        plan = json.loads(
            (tutorial / "full.cue-timeline.json").read_text(encoding="utf-8"))
        body_plan = next(cue for cue in plan["cues"]
                         if cue["cue_id"] == body_id)
        subtitle_track = next(track for track in body_plan["tracks"]
                              if track["kind"] == "subtitle")
        self.assertGreaterEqual(
            subtitle_track["segments"][0]["cue_start"], 0.1)

    def test_full_audio_slice_ranges_do_not_overlap(self) -> None:
        slices_path = (
            ROOT / "content" / "games" / "splendor" / "tutorial"
            / "full.audio-slices.json")
        document = json.loads(slices_path.read_text(encoding="utf-8"))
        by_asset: dict[str, list[tuple[float, float, str]]] = {}
        for cue_id, cue_slices in document["cues"].items():
            for raw_slice in cue_slices:
                start = float(document["timing_points"][
                    raw_slice["start_ref"]]["time"])
                end = float(document["timing_points"][
                    raw_slice["end_ref"]]["time"])
                by_asset.setdefault(raw_slice["asset"], []).append(
                    (start, end, cue_id))
        for asset, intervals in by_asset.items():
            intervals.sort()
            for previous, current in zip(intervals, intervals[1:]):
                self.assertGreaterEqual(
                    current[0], previous[1] - 1e-9,
                    f"{asset}: {previous[2]} overlaps {current[2]}")

    def test_full_audio_slice_file_matches_runtime_input(self) -> None:
        expected = audio_slice_builder.build_audio_slices(
            ROOT, "splendor", "full")
        actual = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "full.audio-slices.json").read_text(encoding="utf-8"))
        self.assertEqual(actual, expected)

    def test_all_full_title_cues_trim_at_their_own_speech_end(self) -> None:
        slices = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "full.audio-slices.json").read_text("utf-8"))
        plan = builder.build_plan_set(ROOT, "splendor", "full")
        title_cues = [
            cue for cue in plan["cues"]
            if cue["cue_id"].rsplit("/", 1)[-1].startswith("title")
        ]
        self.assertTrue(title_cues)
        for cue in title_cues:
            policy = slices["cue_timing"][cue["cue_id"]]
            self.assertEqual(policy["mode"], "audio")
            self.assertIs(policy["trim_to_speech_end"], True)
            self.assertAlmostEqual(
                policy["trailing_silence_seconds"], 0.5, places=9)
            self.assertAlmostEqual(
                policy["trim_tail_guard_seconds"], 0.08, places=9)
            audio_track = next(
                track for track in cue["tracks"] if track["kind"] == "audio")
            self.assertEqual(audio_track["segments"][-1]["type"], "silence")
            self.assertAlmostEqual(
                audio_track["segments"][-1]["duration"], 0.5, places=9)
            self.assertAlmostEqual(
                audio_track["segments"][-1]["cue_start"],
                audio_track["segments"][0]["source_end"]
                - audio_track["segments"][0]["source_start"],
                places=9,
            )

    def test_all_splendor_cues_end_with_requested_reaction_pause(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        for track in ("quick", "full"):
            slices = json.loads(
                (tutorial / f"{track}.audio-slices.json").read_text(
                    encoding="utf-8"))
            plan = json.loads(
                (tutorial / f"{track}.cue-timeline.json").read_text(
                    encoding="utf-8"))
            cue_ids = [cue["cue_id"] for cue in plan["cues"]]
            self.assertTrue(cue_ids)
            for index, cue in enumerate(plan["cues"]):
                cue_id = cue["cue_id"]
                local_id = cue_id.rsplit("/", 1)[-1]
                is_chapter = local_id.startswith("title_")
                next_is_chapter = (
                    index + 1 < len(cue_ids)
                    and cue_ids[index + 1].rsplit("/", 1)[-1]
                    .startswith("title_")
                )
                expected_pause = (
                    0.5 if is_chapter or not next_is_chapter else 1.0)
                with self.subTest(track=track, cue=cue_id):
                    policy = slices["cue_timing"][cue_id]
                    self.assertIs(policy["trim_to_speech_end"], True)
                    post_speech_target = policy.get(
                        "post_speech_silence_seconds")
                    trailing_target = policy.get(
                        "trailing_silence_seconds")
                    target = (
                        post_speech_target
                        if post_speech_target is not None
                        else trailing_target)
                    self.assertIsNotNone(target)
                    self.assertAlmostEqual(
                        target, expected_pause, places=9)
                    audio_track = next(
                        item for item in cue["tracks"]
                        if item["kind"] == "audio")
                    last_segment = audio_track["segments"][-1]
                    self.assertEqual(last_segment["type"], "silence")
                    self.assertAlmostEqual(
                        last_segment["cue_start"]
                        + last_segment["duration"],
                        self._audio_track_content_end(audio_track),
                        places=9,
                    )
                    source_slice = slices["cues"][cue_id][-1]
                    clip = next(
                        segment for segment in audio_track["segments"]
                        if segment["type"] == "audio_clip"
                        and segment["audio_slice_id"] == source_slice["id"])
                    timing_points = slices["timing_points"]
                    speech_end = timing_points[
                        source_slice["speech_end_ref"]]["time"]
                    speech_end_cue_local = (
                        float(clip["cue_start"])
                        + speech_end
                        - timing_points[source_slice["start_ref"]]["time"])
                    audio_end = (
                        float(last_segment["cue_start"])
                        + float(last_segment["duration"]))
                    actual_pause = audio_end - speech_end_cue_local
                    if post_speech_target is not None:
                        self.assertAlmostEqual(
                            actual_pause, post_speech_target, places=6)
                        self.assertLessEqual(
                            last_segment["duration"],
                            post_speech_target + 1e-9,
                        )
                    else:
                        self.assertAlmostEqual(
                            last_segment["duration"],
                            trailing_target,
                            places=9,
                        )
                        self.assertGreaterEqual(
                            actual_pause, trailing_target - 1e-9)
                    trim_end = (
                        speech_end
                        + float(policy.get("trim_tail_guard_seconds", 0.0)))
                    expected_source_end = min(
                        timing_points[source_slice["end_ref"]]["time"],
                        trim_end,
                    )
                    self.assertAlmostEqual(
                        clip["source_end"], expected_source_end, places=9)

    def test_quick_speech_end_anchors_cover_measured_audio_tail(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        slices = json.loads(
            (tutorial / "quick.audio-slices.json").read_text(
                encoding="utf-8"))
        timing_points = slices["timing_points"]
        for cue_id, cue_slices in slices["cues"].items():
            with self.subTest(cue=cue_id):
                self.assertEqual(len(cue_slices), 1)
                raw_slice = cue_slices[0]
                source_end = timing_points[raw_slice["end_ref"]]["time"]
                speech_end = timing_points[
                    raw_slice["speech_end_ref"]]["time"]
                # quick cue-level ASR word ends are known to under-report the
                # last syllable.  The fixed timeline must use a reviewed
                # energy tail anchor close to the real end of speech.
                self.assertLessEqual(source_end - speech_end, 0.12)
                subtitle_ends = [
                    timing_points[subtitle["end_ref"]]["time"]
                    for subtitle in raw_slice.get("subtitles") or []
                ]
                if subtitle_ends:
                    self.assertGreaterEqual(
                        max(subtitle_ends), speech_end - 1e-9)

    def test_quick_third_level_word_is_not_trimmed(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        slices = json.loads(
            (tutorial / "quick.audio-slices.json").read_text(
                encoding="utf-8"))
        plan = json.loads(
            (tutorial / "quick.cue-timeline.json").read_text(
                encoding="utf-8"))
        cue_id = "quick_setup_cards/001.1"
        raw_slice = slices["cues"][cue_id][0]
        cue = next(item for item in plan["cues"]
                   if item["cue_id"] == cue_id)
        audio_track = next(
            track for track in cue["tracks"] if track["kind"] == "audio")
        clip = audio_track["segments"][0]
        source_end = slices["timing_points"][raw_slice["end_ref"]]["time"]
        self.assertAlmostEqual(clip["source_end"], source_end, places=9)
        self.assertGreater(cue["course"]["duration"], source_end)
        subtitle_track = next(
            track for track in cue["tracks"]
            if track["kind"] == "subtitle")
        subtitle_end = max(
            segment["cue_start"] + segment["duration"]
            for segment in subtitle_track["segments"])
        speech_end = (
            slices["timing_points"][raw_slice["speech_end_ref"]]["time"]
            - slices["timing_points"][raw_slice["start_ref"]]["time"])
        self.assertGreaterEqual(subtitle_end, speech_end - 1e-9)

    def test_full_timing_policy_is_global_only(self) -> None:
        policy_path = (
            ROOT / "content" / "games" / "splendor" / "tutorial"
            / "full.timing-policy.json"
        )
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
        self.assertTrue(set(policy).issubset(builder.TIMING_POLICY_GLOBAL_KEYS))
        self.assertNotIn("cues", policy)
        self.assertNotIn("cue_timing", policy)
        self.assertNotIn("timing_points", policy)

    # ── legacy runtime is an input document, never a runtime cue clock ───

    def test_missing_plan_policy_is_rejected_instead_of_legacy_fallback(self) -> None:
        self.policy_path.unlink()
        with self.assertRaisesRegex(builder.BuildError, "missing timing policy"):
            builder.build_plan_set(self.root, self.game, self.track)


if __name__ == "__main__":
    unittest.main()
