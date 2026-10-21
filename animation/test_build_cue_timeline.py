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

    def test_continuous_shared_source_suppresses_cue_gap(self) -> None:
        self.write_runtime([
            {
                "id": "fixture/left", "section": "fixture",
                "audio": "media/fixture-audio.mp3", "audio_start": 0.0,
                "duration": 2.0, "start": 0.0, "text": "left",
                "subtitles": [{
                    "t": 0.0, "end": 1.9, "text": "left",
                    "words": [{"end": 1.9}],
                }],
            },
            {
                "id": "fixture/right", "section": "fixture",
                "audio": "media/fixture-audio.mp3", "audio_start": 2.0,
                "duration": 2.0, "start": 2.0, "text": "right",
                "subtitles": [{
                    "t": 0.1, "end": 1.5, "text": "right",
                    "words": [{"start": 0.1, "end": 1.5}],
                }],
            },
        ])
        self.write_script([
            {"id": "fixture/left", "beats": [{"text": "left"}]},
            {"id": "fixture/right", "beats": [{"text": "right"}]},
        ])
        self.write_compiled([])
        self.write_policy(extra={
            "inter_cue_gap_seconds": 0.5,
            "section_end_gap_seconds": 1.0,
        })
        self.write_slices(timing_points={}, cues={})
        slices = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        left_slice = slices["cues"]["fixture/left"][0]
        right_slice = slices["cues"]["fixture/right"][0]
        points = slices["timing_points"]
        self.assertEqual(
            points[left_slice["end_ref"]]["time"],
            points[right_slice["start_ref"]]["time"])
        self.assertIn({
            "left_slice": left_slice["id"],
            "right_slice": right_slice["id"],
            "kind": "continuous",
        }, slices["source_boundary_contracts"])
        self.slices_path.write_text(
            json.dumps(slices, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")

        document = builder.build_plan_set(self.root, self.game, self.track)
        left_plan, right_plan = document["cues"]
        left_audio = self.audio_segments(left_plan)
        right_audio = self.audio_segments(right_plan)
        self.assertEqual(left_audio[-1]["type"], "audio_clip")
        self.assertEqual(right_audio[-1]["type"], "silence")
        self.assertEqual(
            left_plan["course"]["start"] + left_plan["course"]["duration"],
            right_plan["course"]["start"])
        self.assertEqual(left_audio[0]["source_end"], 2.0)
        self.assertEqual(right_audio[0]["source_start"], 2.0)

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
        doc = {
            "sample_rate": 24000,
            "cues": {
                "left": [{
                    "id": "left",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "left.start",
                    "end_ref": "left.end",
                }],
                "right": [{
                    "id": "right",
                    "asset": "media/fixture-audio.mp3",
                    "start_ref": "right.start",
                    "end_ref": "right.end",
                }],
            },
            "timing_points": {
                "left.start": {"time": 0.0},
                "left.end": {"time": 1.0},
                "right.start": {"time": 1.0},
                "right.end": {"time": 2.0},
            },
            "source_boundary_contracts": [{
                "left_slice": "left",
                "right_slice": "right",
                "kind": "trimmed_tail",
            }],
        }
        self.assertIsNone(builder._validate_source_boundary_contracts(doc))
        doc["timing_points"]["right.start"]["time"] = 0.85
        with self.assertRaisesRegex(builder.BuildError, "violates trimmed_tail"):
            builder._validate_source_boundary_contracts(doc)
        doc["timing_points"]["right.start"]["time"] = 1.0
        resolved = {
            "left": {"source_start": 0.0, "source_end": 1.1},
            "right": {"source_start": 1.0, "source_end": 2.0},
        }
        with self.assertRaisesRegex(builder.BuildError, "violates trimmed_tail"):
            builder._validate_source_boundary_contracts(doc, resolved)

    def test_full_numeric_title_cues_use_independent_audio_assets(self) -> None:
        doc = audio_slice_builder.build_audio_slices(ROOT, "splendor", "full")
        builder._validate_source_boundary_contracts(doc)
        users: dict[str, set[str]] = {}
        for cue_id, slices in doc["cues"].items():
            for raw_slice in slices:
                users.setdefault(raw_slice["asset"], set()).add(cue_id)
        title_slice_ids = {
            doc["cues"][cue_id][0]["id"]
            for cue_id in ("setup_players/title_2_4", "setup_players/title_2_5")
        }
        for cue_id in ("setup_players/title_2_4", "setup_players/title_2_5"):
            raw_slice = doc["cues"][cue_id][0]
            self.assertEqual(users[raw_slice["asset"]], {cue_id})
        self.assertFalse(any(
            contract["left_slice"] in title_slice_ids
            or contract["right_slice"] in title_slice_ids
            for contract in doc["source_boundary_contracts"]))

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
                1.632,
            ),
            "3.2，发展卡牌与贵族。": (
                ["3 行动", "3.2 发展卡牌与贵族"],
                "media/tts/full/action.cards.group.mp3",
                3.456,
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
            self.assertGreaterEqual(audio_segment["source_start"], 0.0)
            self.assertLess(
                audio_segment["source_start"], audio_segment["source_end"])
            self.assertAlmostEqual(
                audio_segment["source_end"], source_end, places=9)
            audio_track = next(
                track for track in cue["tracks"] if track["kind"] == "audio")
            self.assertEqual(audio_track["segments"][-1]["type"], "silence")
            self.assertGreater(
                audio_track["segments"][-1]["duration"], 0.0)
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
        title_speech_end = timing_points[
            title_slice["speech_end_ref"]]["time"]
        title_runtime_end = (
            title["audio_start"] + title["duration"])
        title_end = timing_points[title_slice["end_ref"]]["time"]
        self.assertGreaterEqual(title_end, title_speech_end - 1e-9)
        self.assertLessEqual(title_end, title_runtime_end + 1e-9)

        body_id = "intro/001"
        body_slice = slices["cues"][body_id][0]
        body_start = timing_points[body_slice["start_ref"]]["time"]
        body_end = timing_points[body_slice["end_ref"]]["time"]
        speech_end = timing_points[body_slice["speech_end_ref"]]["time"]
        runtime_cue = runtime_by_id[body_id]
        runtime_end = runtime_cue["audio_start"] + runtime_cue["duration"]
        self.assertLessEqual(body_start, runtime_cue["audio_start"] + 1e-9)
        self.assertGreaterEqual(body_start, 0.0)
        self.assertLess(body_start, body_end)
        self.assertAlmostEqual(
            body_end, min(runtime_end, speech_end + 0.1), places=9)
        self.assertGreaterEqual(speech_end, body_start + 0.1)

        plan = json.loads(
            (tutorial / "full.cue-timeline.json").read_text(encoding="utf-8"))
        body_plan = next(cue for cue in plan["cues"]
                         if cue["cue_id"] == body_id)
        subtitle_track = next(track for track in body_plan["tracks"]
                              if track["kind"] == "subtitle")
        subtitle_start = subtitle_track["segments"][0]["cue_start"]
        self.assertGreaterEqual(subtitle_start, 0.0)
        self.assertLessEqual(subtitle_start, 0.5)

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

    def test_audio_slice_builder_honors_measured_speech_end_timestamp(self) -> None:
        self.write_runtime([{
            "id": "fixture/one",
            "section": "fixture",
            "audio": "media/fixture-audio.mp3",
            "audio_start": 0.0,
            "duration": 2.0,
            "text": "runtime text",
            "subtitles": [],
        }])
        self.write_compiled([])
        self.write_slices(
            timing_points={
                "fixture__one.speech_end": {"time": 1.8},
            },
            cues={},
            cue_timing={
                "fixture/one": {
                    "mode": "max",
                    "trim_to_speech_end": True,
                    "use_measured_speech_end": True,
                    "trim_tail_guard_seconds": 0.1,
                    "trailing_silence_seconds": 0.5,
                },
            },
        )
        document = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        raw_slice = document["cues"]["fixture/one"][0]
        timing_points = document["timing_points"]
        self.assertAlmostEqual(
            timing_points[raw_slice["speech_end_ref"]]["time"],
            1.8,
            places=9,
        )
        self.assertAlmostEqual(
            timing_points[raw_slice["end_ref"]]["time"],
            1.9,
            places=9,
        )
        self.assertIs(
            document["cue_timing"]["fixture/one"]["use_measured_speech_end"],
            True,
        )

    def test_audio_slice_builder_honors_measured_speech_start_timestamp(self) -> None:
        self.write_runtime([{
            "id": "fixture/one",
            "section": "fixture",
            "audio": "media/fixture-audio.mp3",
            "audio_start": 0.0,
            "duration": 2.0,
            "text": "runtime text",
            "subtitles": [],
        }])
        self.write_compiled([])
        self.write_slices(
            timing_points={
                "fixture__one.speech_start": {"time": 0.8},
            },
            cues={},
            cue_timing={
                "fixture/one": {
                    "mode": "max",
                    "trim_to_speech_start": True,
                    "use_measured_speech_start": True,
                    "trim_start_guard_seconds": 0.1,
                },
            },
        )
        document = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        raw_slice = document["cues"]["fixture/one"][0]
        self.assertIn("speech_start_ref", raw_slice)
        self.assertAlmostEqual(
            document["timing_points"][raw_slice["speech_start_ref"]]["time"],
            0.8,
            places=9,
        )
        self.assertIs(
            document["cue_timing"]["fixture/one"][
                "use_measured_speech_start"],
            True,
        )

    def test_audio_slice_builder_preserves_measured_source_window(self) -> None:
        self.write_runtime([{
            "id": "fixture/one",
            "section": "fixture",
            "audio": "media/fixture-audio.mp3",
            "audio_start": 0.0,
            "duration": 2.0,
            "text": "runtime text",
            "subtitles": [],
        }])
        self.write_compiled([])
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.2},
                "fixture__one.end": {"time": 1.8},
                "fixture__one.speech_start": {"time": 0.4},
                "fixture__one.speech_end": {"time": 1.5},
            },
            cues={},
            cue_timing={
                "fixture/one": {
                    "mode": "max",
                    "trim_to_speech_start": True,
                    "trim_to_speech_end": True,
                    "trim_start_guard_seconds": 0.1,
                    "trim_tail_guard_seconds": 0.1,
                    "use_measured_speech_start": True,
                    "use_measured_speech_end": True,
                    "use_measured_source_window": True,
                },
            },
        )
        document = audio_slice_builder.build_audio_slices(
            self.root, self.game, self.track)
        raw_slice = document["cues"]["fixture/one"][0]
        timing_points = document["timing_points"]
        self.assertAlmostEqual(
            timing_points[raw_slice["start_ref"]]["time"], 0.2, places=9)
        self.assertAlmostEqual(
            timing_points[raw_slice["end_ref"]]["time"], 1.8, places=9)
        self.assertAlmostEqual(
            timing_points[raw_slice["speech_start_ref"]]["time"],
            0.4,
            places=9,
        )
        self.assertAlmostEqual(
            timing_points[raw_slice["speech_end_ref"]]["time"],
            1.5,
            places=9,
        )
        self.assertIs(
            document["cue_timing"]["fixture/one"][
                "use_measured_source_window"],
            True,
        )

    def test_plan_trims_start_at_measured_timestamp(self) -> None:
        raw_slice = {
            **self.default_slice(),
            "speech_start_ref": "fixture__one.speech_start",
        }
        self.write_slices(
            timing_points={
                "fixture__one.start": {"time": 0.0},
                "fixture__one.end": {"time": 2.0},
                "fixture__one.speech_start": {"time": 0.8},
                "fixture__one.speech_end": {"time": 1.5},
                "fixture__one.subtitle.0.start": {"time": 0.2},
                "fixture__one.subtitle.0.end": {"time": 1.4},
            },
            cues={"fixture/one": [raw_slice]},
            cue_timing={
                "fixture/one": {
                    "mode": "audio",
                    "trim_to_speech_start": True,
                    "trim_start_guard_seconds": 0.1,
                    "trim_to_speech_end": True,
                    "trim_tail_guard_seconds": 0.1,
                    "trailing_silence_seconds": 0.5,
                },
            },
        )
        document = builder.build_plan_set(self.root, self.game, self.track)
        plan = document["cues"][0]
        audio = self.audio_segments(plan)
        self.assertAlmostEqual(audio[0]["source_start"], 0.7, places=9)
        self.assertAlmostEqual(audio[0]["source_end"], 1.6, places=9)
        subtitle = next(
            track for track in plan["tracks"]
            if track["kind"] == "subtitle")["segments"][0]
        self.assertAlmostEqual(subtitle["cue_start"], 0.0, places=9)
        self.assertAlmostEqual(subtitle["duration"], 0.7, places=9)
        self.assertAlmostEqual(plan["course"]["duration"], 1.4, places=9)
        self.assertIs(timeline.validate_plan_set(document), document)

    def test_full_audio_slice_file_matches_runtime_input(self) -> None:
        expected = audio_slice_builder.build_audio_slices(
            ROOT, "splendor", "full")
        actual = json.loads(
            (ROOT / "content" / "games" / "splendor" / "tutorial"
             / "full.audio-slices.json").read_text(encoding="utf-8"))
        self.assertEqual(actual, expected)

    def test_all_full_title_cues_trim_at_their_own_speech_end(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        slices = json.loads(
            (tutorial / "full.audio-slices.json").read_text("utf-8"))
        runtime = json.loads(
            (tutorial / "full.runtime.json").read_text("utf-8"))
        policy_document = json.loads(
            (tutorial / "full.timing-policy.json").read_text("utf-8"))
        plan = builder.build_plan_set(ROOT, "splendor", "full")
        section_by_id = {
            cue["id"]: cue.get("section") for cue in runtime["cues"]
        }
        title_cues = [
            cue for cue in plan["cues"]
            if cue["cue_id"].rsplit("/", 1)[-1].startswith("title")
        ]
        self.assertTrue(title_cues)
        for cue in title_cues:
            cue_id = cue["cue_id"]
            policy = slices["cue_timing"][cue_id]
            self.assertEqual(policy["mode"], "audio")
            self.assertIs(policy["trim_to_speech_end"], True)
            self.assertAlmostEqual(
                policy["trim_tail_guard_seconds"], 0.1, places=9)
            audio_track = next(
                track for track in cue["tracks"] if track["kind"] == "audio")
            self.assertEqual(audio_track["segments"][-1]["type"], "silence")
            self.assertAlmostEqual(
                audio_track["segments"][-1]["cue_start"],
                audio_track["segments"][0]["source_end"]
                - audio_track["segments"][0]["source_start"],
                places=9,
            )
            next_cue = (
                plan["cues"][plan["cues"].index(cue) + 1]
                if plan["cues"].index(cue) + 1 < len(plan["cues"]) else None)
            target = float(
                policy_document["section_end_gap_seconds"]
                if next_cue is None
                or section_by_id[cue_id] != section_by_id[
                    next_cue["cue_id"]]
                else policy_document["inter_cue_gap_seconds"])
            source_slice = slices["cues"][cue_id][-1]
            clip = next(
                segment for segment in audio_track["segments"]
                if segment["type"] == "audio_clip")
            speech_end = slices["timing_points"][
                source_slice["speech_end_ref"]]["time"]
            speech_end_cue_local = (
                float(clip["cue_start"])
                + speech_end
                - float(clip["source_start"]))
            audio_end = self._audio_track_content_end(audio_track)
            self.assertAlmostEqual(
                audio_end - speech_end_cue_local, target, places=6)

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
            runtime = json.loads(
                (tutorial / f"{track}.runtime.json").read_text(
                    encoding="utf-8"))
            policy_document = json.loads(
                (tutorial / f"{track}.timing-policy.json").read_text(
                    encoding="utf-8"))
            inter_gap = float(policy_document["inter_cue_gap_seconds"])
            section_end_gap = float(
                policy_document["section_end_gap_seconds"])
            section_by_id = {
                cue["id"]: cue.get("section") for cue in runtime["cues"]
            }
            timing_points = slices["timing_points"]
            for index, cue in enumerate(plan["cues"]):
                cue_id = cue["cue_id"]
                next_cue = (
                    plan["cues"][index + 1]
                    if index + 1 < len(plan["cues"]) else None)
                source_slice = slices["cues"][cue_id][-1]
                next_slices = (
                    slices["cues"][next_cue["cue_id"]]
                    if next_cue is not None else [])
                continuous_to_next = bool(
                    next_slices
                    and any(
                        contract.get("kind") == "continuous"
                        and contract.get("left_slice") == source_slice["id"]
                        and contract.get("right_slice") == next_slices[0]["id"]
                        for contract in slices.get(
                            "source_boundary_contracts", [])))
                default_target = (
                    section_end_gap
                    if next_cue is None
                    or section_by_id[cue_id] != section_by_id[
                        next_cue["cue_id"]]
                    else inter_gap)
                with self.subTest(track=track, cue=cue_id):
                    policy = slices["cue_timing"][cue_id]
                    explicit_post = policy.get(
                        "post_speech_silence_seconds")
                    if continuous_to_next:
                        self.assertFalse(
                            policy.get("trim_to_speech_end", False))
                        self.assertEqual(explicit_post, 0.0)
                        target = None
                    elif explicit_post is not None:
                        self.assertFalse(
                            policy.get("trim_to_speech_end", False))
                        target = float(explicit_post)
                    else:
                        self.assertIs(policy["trim_to_speech_end"], True)
                        self.assertNotIn("trailing_silence_seconds", policy)
                        target = default_target
                    audio_track = next(
                        item for item in cue["tracks"]
                        if item["kind"] == "audio")
                    last_segment = audio_track["segments"][-1]
                    if target is None:
                        self.assertEqual(last_segment["type"], "audio_clip")
                    else:
                        self.assertEqual(last_segment["type"], "silence")
                        self.assertAlmostEqual(
                            last_segment["cue_start"]
                            + last_segment["duration"],
                            self._audio_track_content_end(audio_track),
                            places=9,
                        )
                        clip = next(
                            segment for segment in audio_track["segments"]
                            if segment["type"] == "audio_clip"
                            and segment["audio_slice_id"] == source_slice["id"])
                        speech_end = timing_points[
                            source_slice["speech_end_ref"]]["time"]
                        speech_end_cue_local = (
                            float(clip["cue_start"])
                            + speech_end
                            - float(clip["source_start"]))
                        audio_end = self._audio_track_content_end(audio_track)
                        self.assertAlmostEqual(
                            audio_end - speech_end_cue_local,
                            target, places=6)

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
                policy = slices["cue_timing"][cue_id]
                self.assertAlmostEqual(
                    policy.get("trim_tail_guard_seconds"), 0.1, places=9)
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
            - float(clip["source_start"]))
        self.assertGreaterEqual(subtitle_end, speech_end - 1e-9)

    def test_full_setup_cards_keeps_measured_blue_level_word_tail(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        runtime = json.loads(
            (tutorial / "full.runtime.json").read_text(encoding="utf-8"))
        plan = json.loads(
            (tutorial / "full.cue-timeline.json").read_text(encoding="utf-8"))
        cue_id = "setup_cards/001.2"
        runtime_cue = next(cue for cue in runtime["cues"]
                           if cue["id"] == cue_id)
        raw_end = runtime_cue["audio_start"] + runtime_cue["duration"]
        cue = next(item for item in plan["cues"]
                   if item["cue_id"] == cue_id)
        slices = json.loads(
            (tutorial / "full.audio-slices.json").read_text(
                encoding="utf-8"))
        raw_slice = slices["cues"][cue_id][0]
        timing_points = slices["timing_points"]
        audio_track = next(
            track for track in cue["tracks"] if track["kind"] == "audio")
        clip = audio_track["segments"][0]
        self.assertEqual(clip["asset"], raw_slice["asset"])
        self.assertIn("/cues/setup_cards__001.2", clip["asset"])
        speech_start = timing_points[
            raw_slice["speech_start_ref"]]["time"]
        speech_end = timing_points[raw_slice["speech_end_ref"]]["time"]
        self.assertLessEqual(clip["source_start"], speech_start)
        self.assertGreaterEqual(clip["source_end"], speech_end)
        speech_end_cue_local = (
            float(clip["cue_start"])
            + speech_end
            - float(clip["source_start"]))
        audio_end = self._audio_track_content_end(audio_track)
        self.assertAlmostEqual(
            audio_end - speech_end_cue_local, 0.5, places=6)

    def test_full_intro_title_and_body_use_separate_audio_assets(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        runtime = json.loads(
            (tutorial / "full.runtime.json").read_text(encoding="utf-8"))
        plan = json.loads(
            (tutorial / "full.cue-timeline.json").read_text(encoding="utf-8"))
        runtime_by_id = {cue["id"]: cue for cue in runtime["cues"]}
        self.assertEqual(
            runtime_by_id["intro/title_1"]["audio"],
            "media/tts/full/intro.title.mp3",
        )
        self.assertEqual(
            runtime_by_id["intro/001"]["audio"],
            "media/tts/full/intro.body.mp3",
        )
        for cue_id, expected_asset in (
                ("intro/title_1", "media/tts/full/intro.title.mp3"),
                ("intro/001", "media/tts/full/intro.body.mp3")):
            cue = next(item for item in plan["cues"]
                       if item["cue_id"] == cue_id)
            clip = next(
                segment
                for track in cue["tracks"] if track["kind"] == "audio"
                for segment in track["segments"]
                if segment["type"] == "audio_clip")
            self.assertEqual(clip["asset"], expected_asset)
        title = next(item for item in plan["cues"]
                     if item["cue_id"] == "intro/title_1")
        body = next(item for item in plan["cues"]
                    if item["cue_id"] == "intro/001")
        self.assertAlmostEqual(
            title["course"]["start"] + title["course"]["duration"],
            body["course"]["start"],
            places=9,
        )

    def test_splendor_shared_audio_is_continuous_across_adjacent_cues(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        for track in ("quick", "full"):
            slices = json.loads(
                (tutorial / f"{track}.audio-slices.json").read_text(
                    encoding="utf-8"))
            cue_order = list(slices["cues"])
            cue_position = {cue_id: index for index, cue_id in enumerate(cue_order)}
            continuous = {
                (contract["left_slice"], contract["right_slice"])
                for contract in slices.get("source_boundary_contracts", [])
                if contract.get("kind") == "continuous"
            }
            users: dict[str, set[str]] = {}
            for cue_id, cue_slices in slices["cues"].items():
                for raw_slice in cue_slices:
                    users.setdefault(raw_slice["asset"], set()).add(cue_id)
            for asset, cue_ids in users.items():
                if len(cue_ids) == 1:
                    continue
                ordered = sorted(cue_ids, key=cue_position.__getitem__)
                self.assertGreater(len(ordered), 1)
                for left_id, right_id in zip(ordered, ordered[1:]):
                    with self.subTest(track=track, asset=asset,
                                      left=left_id, right=right_id):
                        self.assertEqual(
                            cue_position[right_id],
                            cue_position[left_id] + 1,
                            "shared audio must join adjacent cues")
                        left_slice = slices["cues"][left_id][-1]
                        right_slice = slices["cues"][right_id][0]
                        self.assertEqual(left_slice["asset"], asset)
                        self.assertEqual(right_slice["asset"], asset)
                        self.assertIn(
                            (left_slice["id"], right_slice["id"]),
                            continuous,
                            "shared audio must declare a continuous boundary")

    def test_all_splendor_chapter_cues_use_independent_audio_assets(self) -> None:
        tutorial = (
            ROOT / "content" / "games" / "splendor" / "tutorial")
        for track in ("quick", "full"):
            slices = json.loads(
                (tutorial / f"{track}.audio-slices.json").read_text(
                    encoding="utf-8"))
            users: dict[str, set[str]] = {}
            for cue_id, cue_slices in slices["cues"].items():
                for raw_slice in cue_slices:
                    users.setdefault(raw_slice["asset"], set()).add(cue_id)
            title_ids = [
                cue_id for cue_id in slices["cues"]
                if cue_id.rsplit("/", 1)[-1].startswith("title_")
            ]
            self.assertTrue(title_ids)
            for cue_id in title_ids:
                with self.subTest(track=track, cue=cue_id):
                    assets = {
                        raw_slice["asset"]
                        for raw_slice in slices["cues"][cue_id]
                    }
                    self.assertEqual(len(assets), 1)
                    asset = next(iter(assets))
                    self.assertEqual(users[asset], {cue_id})

    def test_global_gap_policy_applies_cue_and_section_defaults(self) -> None:
        cue_defs = [
            ("fixture/one", "s1", 0.0),
            ("fixture/two", "s1", 1.0),
            ("fixture/three", "s2", 2.0),
        ]
        self.write_runtime([
            {
                "id": cue_id,
                "section": section,
                "audio": "media/fixture-audio.mp3",
                "audio_start": start,
                "duration": 1.0,
                "start": start,
                "text": cue_id,
                "subtitles": [],
            }
            for cue_id, section, start in cue_defs
        ])
        self.write_script([
            {
                "id": cue_id,
                "section": section,
                "beats": [{"text": cue_id}],
            }
            for cue_id, section, _ in cue_defs
        ])
        self.write_compiled([])
        self.write_policy(extra={
            "inter_cue_gap_seconds": 0.5,
            "section_end_gap_seconds": 1.0,
        })
        timing_points = {}
        cue_slices = {}
        for cue_id, _, start in cue_defs:
            safe = cue_id.replace("/", "__")
            timing_points[f"{safe}.start"] = {"time": start}
            timing_points[f"{safe}.end"] = {"time": start + 1.0}
            timing_points[f"{safe}.speech_end"] = {"time": start + 0.8}
            cue_slices[cue_id] = [{
                "id": safe,
                "asset": "media/fixture-audio.mp3",
                "start_ref": f"{safe}.start",
                "end_ref": f"{safe}.end",
                "speech_end_ref": f"{safe}.speech_end",
                "subtitles": [],
            }]
        self.write_slices(timing_points=timing_points, cues=cue_slices)
        document = builder.build_plan_set(self.root, self.game, self.track)
        pauses = []
        for cue in document["cues"]:
            audio = next(
                track for track in cue["tracks"] if track["kind"] == "audio")
            self.assertEqual(audio["segments"][-1]["type"], "silence")
            clip = audio["segments"][0]
            # The fixture speech ends 0.2s before the authored source end.
            speech_end_cue_local = (
                float(clip["cue_start"])
                + (1.0 - 0.2))
            audio_end = (
                float(audio["segments"][-1]["cue_start"])
                + float(audio["segments"][-1]["duration"]))
            pauses.append(round(audio_end - speech_end_cue_local, 9))
        self.assertEqual(pauses, [0.5, 1.0, 1.0])

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
