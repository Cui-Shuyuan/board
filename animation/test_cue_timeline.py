#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Protocol-level tests for animation/cue_timeline.py.

These tests use synthetic cues and the checked-in protocol fixtures only.  They
do not depend on any concrete tutorial track's cue ids or subtitle content.
"""
from __future__ import annotations

import copy
import json
import math
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
FIXTURES = ROOT / "tests" / "fixtures" / "cue_timeline"
sys.path.insert(0, str(HERE))

import cue_timeline as timeline  # noqa: E402


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def base_source(segments: list[dict], *, duration=None,
                cue_id: str = "fixture/synthetic") -> dict:
    source = {
        "schema": "tutorial-cue-source/v1",
        "cue_id": cue_id,
        "entry": "fixture/previous",
        "tracks": [{
            "id": "visual.main",
            "kind": "visual",
            "tail_policy": "hold_last_frame",
            "segments": segments,
        }],
    }
    if duration is not None:
        source["duration"] = duration
    return source


def audio_source(segments: list[dict], *, duration=None) -> dict:
    source = {
        "schema": "tutorial-cue-source/v1",
        "cue_id": "fixture/audio",
        "tracks": [{
            "id": "audio.main",
            "kind": "audio",
            "tail_policy": "silence",
            "segments": segments,
        }],
    }
    if duration is not None:
        source["duration"] = duration
    return source


def compile_synthetic(segments: list[dict], *, duration=None) -> dict:
    return timeline.compile_cue_source(
        base_source(segments, duration=duration),
        course_start=4.0,
        sample_rate=1000,
    )


class FixtureCompilationTests(unittest.TestCase):
    def test_source_fixture_compiles_to_checked_in_plan(self):
        source = load_json(FIXTURES / "source_example.json")
        expected = load_json(FIXTURES / "compiled_example.json")
        actual = timeline.compile_cue_source(
            source, course_start=12.0, sample_rate=24000)
        self.assertEqual(actual, expected)
        self.assertIs(timeline.validate_cue_plan(actual), actual)


class CompilerTests(unittest.TestCase):
    def test_absolute_and_all_supported_anchors(self):
        source = {
            "schema": "tutorial-cue-source/v1",
            "cue_id": "fixture/anchors",
            "tracks": [
                {
                    "id": "visual.main",
                    "kind": "visual",
                    "tail_policy": "hold_last_frame",
                    "segments": [
                        {
                            "id": "v.abs",
                            "type": "animation",
                            "animation": "fixture#abs",
                            "at": 1.0,
                            "source": {"start": 0.0, "end": 1.0},
                            "after_end": "hold_last_frame",
                        },
                        {
                            "id": "v.after",
                            "type": "hold",
                            "at": {
                                "anchor": {
                                    "type": "after",
                                    "segment": "v.abs",
                                    "delay": 0.5,
                                }
                            },
                            "duration": 0.25,
                            "pose": "last_frame",
                        },
                    ],
                },
                {
                    "id": "audio.main",
                    "kind": "audio",
                    "tail_policy": "silence",
                    "segments": [
                        {
                            "id": "a.cue",
                            "type": "silence",
                            "at": {
                                "type": "cue_start",
                                "offset": 0.25,
                            },
                            "duration": 0.5,
                        },
                        {
                            "id": "a.all",
                            "type": "audio_clip",
                            "asset": "fixture.mp3",
                            "at": {
                                "anchor": {
                                    "type": "after_all",
                                    "segments": ["v.after", "a.cue"],
                                    "delay": 0.1,
                                }
                            },
                            "source": {"start": 2.0, "end": 2.5},
                        },
                    ],
                },
            ],
        }
        plan = timeline.compile_cue_source(
            source, course_start=7.0, sample_rate=1000)
        visual = plan["tracks"][0]["segments"]
        audio = plan["tracks"][1]["segments"]
        self.assertEqual([s["cue_start"] for s in visual], [1.0, 2.5])
        self.assertEqual([s["cue_start"] for s in audio], [0.25, 2.85])
        self.assertAlmostEqual(plan["course"]["duration"], 3.35)
        self.assertEqual(plan["course"]["start"], 7.0)
        self.assertEqual(audio[1]["source_start_sample"], 2000)
        self.assertEqual(audio[1]["source_end_sample"], 2500)

    def test_auto_duration_and_explicit_longer_duration(self):
        segments = [{
            "id": "v1",
            "type": "hold",
            "at": {"anchor": {"type": "cue_start", "offset": 0.0}},
            "duration": 2.0,
            "pose": "last_frame",
        }]
        auto = compile_synthetic(segments)
        self.assertEqual(auto["course"]["duration"], 2.0)

        explicit = compile_synthetic(segments, duration=5.0)
        self.assertEqual(explicit["course"]["duration"], 5.0)

    def test_explicit_duration_shorter_than_content_is_rejected(self):
        segments = [{
            "id": "v1",
            "type": "hold",
            "at": 0.0,
            "duration": 2.0,
            "pose": "last_frame",
        }]
        with self.assertRaisesRegex(timeline.CueTimelineError, "shorter"):
            compile_synthetic(segments, duration=1.5)

    def test_anchor_cycle_is_rejected(self):
        source = base_source([
            {
                "id": "v1",
                "type": "hold",
                "at": {"anchor": {"type": "after", "segment": "v2", "delay": 0.0}},
                "duration": 1.0,
                "pose": "last_frame",
            },
            {
                "id": "v2",
                "type": "hold",
                "at": {"anchor": {"type": "after", "segment": "v1", "delay": 0.0}},
                "duration": 1.0,
                "pose": "last_frame",
            },
        ], duration=2.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "cycle"):
            timeline.compile_cue_source(source)

    def test_missing_anchor_reference_is_rejected(self):
        source = base_source([{
            "id": "v1",
            "type": "hold",
            "at": {"anchor": {"type": "after", "segment": "missing", "delay": 0.0}},
            "duration": 1.0,
            "pose": "last_frame",
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "unknown segment"):
            timeline.compile_cue_source(source)

    def test_same_track_overlap_is_rejected(self):
        source = audio_source([
            {
                "id": "a1",
                "type": "silence",
                "at": 0.0,
                "duration": 2.0,
            },
            {
                "id": "a2",
                "type": "silence",
                "at": 1.0,
                "duration": 1.0,
            },
        ], duration=3.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "overlaps"):
            timeline.compile_cue_source(source)

    def test_unknown_track_segment_and_tail_types_are_rejected(self):
        unknown_track = base_source([], duration=1.0)
        unknown_track["tracks"][0]["kind"] = "hologram"
        with self.assertRaisesRegex(timeline.CueTimelineError, "unknown track kind"):
            timeline.compile_cue_source(unknown_track)

        unknown_segment = base_source([{
            "id": "v1",
            "type": "burst",
            "at": 0.0,
            "duration": 1.0,
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "unknown visual segment"):
            timeline.compile_cue_source(unknown_segment)

        unknown_tail = base_source([], duration=1.0)
        unknown_tail["tracks"][0]["tail_policy"] = "loop_forever"
        with self.assertRaisesRegex(timeline.CueTimelineError, "tail policy"):
            timeline.compile_cue_source(unknown_tail)

        unknown_after = base_source([{
            "id": "v1",
            "type": "animation",
            "animation": "fixture#a",
            "at": 0.0,
            "source": {"start": 0.0, "end": 1.0},
            "after_end": "freeze",
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "after_end"):
            timeline.compile_cue_source(unknown_after)

    def test_illegal_times_are_rejected(self):
        negative_at = base_source([{
            "id": "v1",
            "type": "hold",
            "at": -0.1,
            "duration": 1.0,
            "pose": "last_frame",
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, ">= 0"):
            timeline.compile_cue_source(negative_at)

        bad_range = base_source([{
            "id": "v1",
            "type": "animation",
            "animation": "fixture#a",
            "at": 0.0,
            "source": {"start": 1.0, "end": 1.0},
            "after_end": "clear",
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "greater than"):
            timeline.compile_cue_source(bad_range)

        non_finite = base_source([{
            "id": "v1",
            "type": "hold",
            "at": float("nan"),
            "duration": 1.0,
            "pose": "last_frame",
        }], duration=1.0)
        with self.assertRaisesRegex(timeline.CueTimelineError, "finite"):
            timeline.compile_cue_source(non_finite)


class PlanValidatorTests(unittest.TestCase):
    def setUp(self):
        self.plan = compile_synthetic([{
            "id": "v1",
            "type": "animation",
            "animation": "fixture#a",
            "at": 0.0,
            "source": {"start": 0.0, "end": 2.0},
            "after_end": "hold_last_frame",
        }], duration=3.0)
        self.plan["tracks"].append({
            "id": "audio.main",
            "kind": "audio",
            "tail_policy": "silence",
            "segments": [{
                "id": "a1",
                "type": "silence",
                "cue_start": 0.0,
                "duration": 1.0,
            }],
        })

    def test_valid_plan_is_accepted(self):
        self.assertIs(timeline.validate_cue_plan(self.plan), self.plan)

    def test_duplicate_track_and_segment_ids_are_rejected(self):
        duplicate_track = copy.deepcopy(self.plan)
        duplicate_track["tracks"][1]["id"] = "visual.main"
        with self.assertRaisesRegex(timeline.CueTimelineError, "duplicate track id"):
            timeline.validate_cue_plan(duplicate_track)

        duplicate_segment = copy.deepcopy(self.plan)
        duplicate_segment["tracks"][1]["segments"][0]["id"] = "v1"
        with self.assertRaisesRegex(timeline.CueTimelineError, "duplicate segment id"):
            timeline.validate_cue_plan(duplicate_segment)

    def test_segment_overrun_and_track_overlap_are_rejected(self):
        overrun = copy.deepcopy(self.plan)
        overrun["tracks"][0]["segments"][0]["source_end"] = 4.0
        with self.assertRaisesRegex(timeline.CueTimelineError, "exceeds cue duration"):
            timeline.validate_cue_plan(overrun)

        overlap = copy.deepcopy(self.plan)
        overlap["tracks"][1]["segments"].append({
            "id": "a2",
            "type": "silence",
            "cue_start": 0.5,
            "duration": 1.0,
        })
        with self.assertRaisesRegex(timeline.CueTimelineError, "overlaps"):
            timeline.validate_cue_plan(overlap)

    def test_tail_after_end_and_audio_samples_are_validated(self):
        bad_visual_tail = copy.deepcopy(self.plan)
        bad_visual_tail["tracks"][0]["tail_policy"] = "silence"
        with self.assertRaisesRegex(timeline.CueTimelineError, "tail policy"):
            timeline.validate_cue_plan(bad_visual_tail)

        bad_audio_tail = copy.deepcopy(self.plan)
        bad_audio_tail["tracks"][1]["tail_policy"] = "hold_last_frame"
        with self.assertRaisesRegex(timeline.CueTimelineError, "tail policy"):
            timeline.validate_cue_plan(bad_audio_tail)

        bad_after_end = copy.deepcopy(self.plan)
        bad_after_end["tracks"][0]["segments"][0]["after_end"] = "continue"
        with self.assertRaisesRegex(timeline.CueTimelineError, "after_end"):
            timeline.validate_cue_plan(bad_after_end)

        bad_samples = copy.deepcopy(self.plan)
        clip = {
            "id": "a.clip",
            "type": "audio_clip",
            "asset": "fixture.mp3",
            "cue_start": 1.0,
            "source_start": 0.0,
            "source_end": 1.0,
            "source_start_sample": 100,
        }
        bad_samples["tracks"][1]["segments"].append(clip)
        with self.assertRaisesRegex(timeline.CueTimelineError, "must either both"):
            timeline.validate_cue_plan(bad_samples)


class LegacyAdapterTests(unittest.TestCase):
    def _runtime(self, cues: list[dict]) -> dict:
        return {
            "schema_version": 3,
            "game_id": "fixture",
            "track": "synthetic",
            "sample_rate": 24000,
            "cues": cues,
        }

    def _cue(self, cue_id: str, start: float, duration: float, **extra) -> dict:
        cue = {
            "id": cue_id,
            "local_id": cue_id.rsplit("/", 1)[-1],
            "start": start,
            "duration": duration,
            "text": f"text {cue_id}",
            "audio": f"media/{cue_id.rsplit('/', 1)[-1]}.mp3",
            "audio_start": 0.0,
            "subtitles": [{
                "t": 0.0,
                "end": duration,
                "text": f"text {cue_id}",
                "words": [{"word": "tail", "start": 0.0, "end": 0.05}],
            }],
            "entry": "fixture/previous",
        }
        cue.update(extra)
        return cue

    def test_audio_plan_section_audio_and_single_audio_paths(self):
        runtime = self._runtime([
            self._cue(
                "c/audio_plan", 0.0, 3.0,
                audio_plan=[
                    {"type": "clip", "file": "a.mp3", "start": 1.0, "end": 2.5},
                    {"type": "silence", "duration": 1.0},
                ],
            ),
            self._cue(
                "c/section_audio", 3.0, 2.0,
                audio_start=0.75,
                section_audio=True,
            ),
            self._cue(
                "c/single_audio", 5.0, 1.5,
                audio_start=4.0,
            ),
        ])
        original = copy.deepcopy(runtime)
        document = timeline.adapt_legacy_runtime(runtime)
        self.assertEqual(runtime, original)
        self.assertIs(timeline.validate_plan_set(document), document)

        plan_a, plan_b, plan_c = document["cues"]
        segments_a = plan_a["tracks"][0]["segments"]
        self.assertEqual([s["type"] for s in segments_a],
                         ["audio_clip", "silence"])
        self.assertEqual(segments_a[0]["asset"], "a.mp3")
        self.assertEqual(segments_a[0]["source_start"], 1.0)
        self.assertEqual(segments_a[0]["source_end"], 2.5)
        self.assertEqual(segments_a[1]["cue_start"], 1.5)
        self.assertEqual(segments_a[1]["duration"], 1.0)

        audio_b = plan_b["tracks"][0]["segments"][0]
        self.assertEqual(audio_b["source_start"], 0.75)
        self.assertEqual(audio_b["source_end"], 2.75)
        # Subtitle word timing is not used as an audio boundary.
        self.assertNotEqual(audio_b["source_end"], 0.05)

        audio_c = plan_c["tracks"][0]["segments"][0]
        self.assertEqual(audio_c["source_start"], 4.0)
        self.assertEqual(audio_c["source_end"], 5.5)
        self.assertEqual([p["course"]["start"] for p in document["cues"]],
                         [0.0, 3.0, 5.0])
        self.assertEqual(plan_a["subtitles"], runtime["cues"][0]["subtitles"])
        self.assertEqual(plan_a["entry"], "fixture/previous")

    def test_compiled_animation_extends_duration_and_shifts_later_cue(self):
        runtime = self._runtime([
            self._cue("c/a", 0.0, 2.0, section_audio=True),
            self._cue("c/b", 2.0, 1.0, section_audio=True),
        ])
        compiled = {"cues": [
            {"id": "c/a", "duration": 3.5},
            {"id": "c/b", "duration": 1.0},
        ]}
        document = timeline.adapt_legacy_runtime(runtime, compiled)
        plan_a, plan_b = document["cues"]
        self.assertEqual(plan_a["course"]["duration"], 3.5)
        self.assertEqual(plan_b["course"]["start"], 3.5)
        self.assertEqual(plan_a["tracks"][0]["segments"][0]["source_end"],
                         plan_a["course"]["duration"])

    def test_extending_a_does_not_change_b_material_or_in_cue_arrangement(self):
        runtime = self._runtime([
            self._cue("c/a", 0.0, 2.0, section_audio=True),
            self._cue(
                "c/b", 2.0, 2.0, section_audio=True,
                audio_plan=[
                    {"type": "clip", "file": "b.mp3", "start": 3.0, "end": 4.0},
                    {"type": "silence", "duration": 0.5},
                ],
            ),
        ])
        short = timeline.adapt_legacy_runtime(runtime, {"cues": [
            {"id": "c/a", "duration": 2.0},
            {"id": "c/b", "duration": 1.5},
        ]})
        longer = timeline.adapt_legacy_runtime(runtime, {"cues": [
            {"id": "c/a", "duration": 5.0},
            {"id": "c/b", "duration": 1.5},
        ]})
        plan_b_short = short["cues"][1]
        plan_b_long = longer["cues"][1]
        self.assertEqual(plan_b_long["course"]["start"] - plan_b_short["course"]["start"],
                         3.0)
        self.assertEqual(plan_b_long["tracks"], plan_b_short["tracks"])
        self.assertEqual(plan_b_long["subtitles"], plan_b_short["subtitles"])

    def test_explicit_legacy_gap_is_preserved(self):
        runtime = self._runtime([
            self._cue("c/a", 0.0, 1.0, section_audio=True),
            self._cue("c/b", 2.5, 1.0, section_audio=True),
        ])
        without_animation = timeline.adapt_legacy_runtime(runtime)
        self.assertEqual(without_animation["cues"][1]["course"]["start"], 2.5)

        with_long_a = timeline.adapt_legacy_runtime(runtime, {"cues": [
            {"id": "c/a", "duration": 3.0},
            {"id": "c/b", "duration": 1.0},
        ]})
        self.assertEqual(with_long_a["cues"][1]["course"]["start"], 4.5)

    def test_missing_compiled_animation_is_recorded_without_visual_track(self):
        runtime = self._runtime([self._cue("c/a", 0.0, 1.0, section_audio=True)])
        document = timeline.adapt_legacy_runtime(runtime)
        plan = document["cues"][0]
        self.assertEqual(plan["tracks"][0]["kind"], "audio")
        codes = [item["code"] for item in document["diagnostics"]]
        self.assertIn("missing_compiled_animation", codes)
        self.assertIn("missing_compiled_animation",
                      [item["code"] for item in plan["diagnostics"]])

    def test_zero_duration_compiled_animation_is_recorded_not_invented(self):
        runtime = self._runtime([self._cue("c/a", 0.0, 1.0, section_audio=True)])
        document = timeline.adapt_legacy_runtime(
            runtime, {"cues": [{"id": "c/a", "duration": 0.0}]})
        plan = document["cues"][0]
        self.assertEqual([track["kind"] for track in plan["tracks"]], ["audio"])
        self.assertEqual(plan["course"]["duration"], 1.0)
        self.assertIn("empty_compiled_animation",
                      [item["code"] for item in plan["diagnostics"]])

    def test_adapter_does_not_mutate_compiled_input(self):
        runtime = self._runtime([self._cue("c/a", 0.0, 1.0, section_audio=True)])
        compiled = {"cues": [{"id": "c/a", "duration": 1.5}]}
        runtime_before = copy.deepcopy(runtime)
        compiled_before = copy.deepcopy(compiled)
        timeline.adapt_legacy_runtime(runtime, compiled)
        self.assertEqual(runtime, runtime_before)
        self.assertEqual(compiled, compiled_before)


if __name__ == "__main__":
    unittest.main()
