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

    def test_subtitle_track_is_validated_as_first_class_track(self):
        plan = copy.deepcopy(self.plan)
        plan["tracks"].append({
            "id": "subtitle.main",
            "kind": "subtitle",
            "tail_policy": "clear",
            "segments": [{
                "id": "subtitle.0",
                "type": "subtitle",
                "cue_start": 0.5,
                "duration": 1.5,
                "text": "caption",
            }],
        })
        self.assertIs(timeline.validate_cue_plan(plan), plan)
        invalid = copy.deepcopy(plan)
        invalid["tracks"][-1]["segments"][0]["type"] = "audio_clip"
        with self.assertRaisesRegex(timeline.CueTimelineError, "unknown subtitle segment"):
            timeline.validate_cue_plan(invalid)

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




if __name__ == "__main__":
    unittest.main()
