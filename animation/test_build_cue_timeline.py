#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for the deterministic Phase 4 plan-set builder.

All fixture cue/timing ids and times are synthetic.  The fixed audio-slices
document is the only place the fixture supplies timing-point seconds, source
ranges, cue-to-slice mappings and per-cue timing policy.
"""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
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
        self.audio_path.write_bytes(b"fake-mp3")

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

    # ── release/input consistency and source boundaries ─────────────────

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

    def test_full_track_plan_has_81_cues_and_is_deterministic(self) -> None:
        tutorial = ROOT / "content" / "games" / "splendor" / "tutorial"
        first = builder.build_plan_set(ROOT, "splendor", "full")
        second = builder.build_plan_set(ROOT, "splendor", "full")
        self.assertEqual(first, second)
        self.assertEqual(len(first["cues"]), 81)
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
