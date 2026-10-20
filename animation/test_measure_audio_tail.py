#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import math
import sys
import unittest
from array import array
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import measure_audio_tail as tail  # noqa: E402


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

    def test_silence_returns_source_start(self) -> None:
        samples = array("h", [0] * (self.SAMPLE_RATE * 2))
        measured = tail.last_voiced_end_seconds(samples, self.SAMPLE_RATE, 0.25, 1.5)
        self.assertEqual(measured, 0.25)

    def test_analysis_is_clamped_to_authored_source_end(self) -> None:
        samples = self._tone_samples(duration=1.5, start=0.5, end=1.4)
        measured = tail.last_voiced_end_seconds(samples, self.SAMPLE_RATE, 0.0, 1.0)
        self.assertLessEqual(measured, 1.0)
        self.assertGreater(measured, 0.9)


if __name__ == "__main__":
    unittest.main()
