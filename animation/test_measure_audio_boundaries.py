#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import math
import sys
import tempfile
import unittest
from array import array
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import measure_audio_boundaries as tail  # noqa: E402


class MeasureAudioTailTests(unittest.TestCase):
    SAMPLE_RATE = 24_000

    def _tone_samples(
        self,
        *,
        duration: float,
        start: float,
        end: float,
        amplitude: int = 2000,
    ) -> array:
        samples = array("h", [0] * int(round(duration * self.SAMPLE_RATE)))
        begin = int(round(start * self.SAMPLE_RATE))
        finish = min(len(samples), int(round(end * self.SAMPLE_RATE)))
        for index in range(begin, finish):
            samples[index] = int(
                amplitude * math.sin(2.0 * math.pi * 440.0 * index / self.SAMPLE_RATE))
        return samples

    def test_returns_end_of_last_energy_active_window(self) -> None:
        samples = self._tone_samples(duration=1.5, start=0.5, end=1.0)
        measured = tail.last_voiced_end_seconds(samples, self.SAMPLE_RATE, 0.0, 1.4)
        self.assertGreaterEqual(measured, 0.98)
        self.assertLessEqual(measured, 1.02)

    def test_first_voiced_start_skips_leading_silence_and_clicks(self) -> None:
        samples = self._tone_samples(duration=1.5, start=0.7, end=1.2)
        click = int(round(0.2 * self.SAMPLE_RATE))
        samples[click] = 2000
        measured = tail.first_voiced_start_seconds(
            samples, self.SAMPLE_RATE, 0.0, 1.4)
        self.assertGreaterEqual(measured, 0.68)
        self.assertLessEqual(measured, 0.74)

    def test_silence_returns_source_start(self) -> None:
        samples = array("h", [0] * (self.SAMPLE_RATE * 2))
        measured = tail.last_voiced_end_seconds(samples, self.SAMPLE_RATE, 0.25, 1.5)
        self.assertEqual(measured, 0.25)

    def test_runtime_window_mode_writes_measured_timestamp_and_marker(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            tutorial = root / "tutorial"
            tutorial.mkdir(parents=True)
            slices = {
                "sample_rate": self.SAMPLE_RATE,
                "cues": {
                    "fixture/one": [{
                        "id": "fixture__one",
                        "asset": "media/one.mp3",
                        "start_ref": "fixture__one.start",
                        "end_ref": "fixture__one.end",
                        "speech_end_ref": "fixture__one.speech_end",
                        "subtitles": [],
                    }],
                },
                "timing_points": {
                    "fixture__one.start": {"time": 0.0},
                    "fixture__one.end": {"time": 2.0},
                    "fixture__one.speech_end": {"time": 0.9},
                },
                "cue_timing": {
                    "fixture/one": {"trim_to_speech_end": True},
                },
            }
            (tutorial / "full.audio-slices.json").write_text(
                json.dumps(slices), encoding="utf-8")
            runtime = {
                "cues": [{
                    "id": "fixture/one",
                    "audio": "media/one.mp3",
                    "audio_start": 0.5,
                    "duration": 1.0,
                }],
            }
            (tutorial / "full.runtime.json").write_text(
                json.dumps(runtime), encoding="utf-8")
            samples = self._tone_samples(duration=2.0, start=0.8, end=1.4)
            with mock.patch.object(tail, "_decode_pcm", return_value=samples):
                document, changes = tail.measure_timeline(
                    tutorial, root, "full", "ffmpeg",
                    guard_seconds=0.1, runtime_windows=True)
            cue = document["cues"]["fixture/one"][0]
            measured_end = document["timing_points"][
                cue["speech_end_ref"]]["time"]
            measured_start = document["timing_points"][
                cue["speech_start_ref"]]["time"]
            self.assertGreaterEqual(measured_end, 1.38)
            self.assertLessEqual(measured_end, 1.42)
            self.assertGreaterEqual(measured_start, 0.78)
            self.assertLessEqual(measured_start, 0.82)
            policy = document["cue_timing"]["fixture/one"]
            self.assertIs(policy["use_measured_speech_end"], True)
            self.assertIs(policy["use_measured_speech_start"], True)
            self.assertIs(policy["trim_to_speech_start"], True)
            self.assertAlmostEqual(policy["trim_tail_guard_seconds"], 0.1)
            self.assertAlmostEqual(policy["trim_start_guard_seconds"], 0.1)
            self.assertEqual(len(changes), 1)

    def test_analysis_is_clamped_to_authored_source_end(self) -> None:
        samples = self._tone_samples(duration=1.5, start=0.5, end=1.4)
        measured = tail.last_voiced_end_seconds(samples, self.SAMPLE_RATE, 0.0, 1.0)
        self.assertLessEqual(measured, 1.0)
        self.assertGreater(measured, 0.9)


if __name__ == "__main__":
    unittest.main()
