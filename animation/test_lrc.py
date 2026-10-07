#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for the unified LRC writer helpers."""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import lrc  # noqa: E402


class LrcWriterTests(unittest.TestCase):
    def test_estimated_lrc_has_expected_header_cues_and_refs(self):
        data = {
            "title": "T",
            "game_id": "fake",
            "track": "quick",
            "cues": [
                {"id": "c1", "group": "G", "beats": [{"text": "hello"}], "refs": ["r1"]},
                {"id": "c2", "group": "G", "beats": [{"text": "world"}], "refs": []},
            ],
        }

        text = lrc.write_estimated_lrc(data)

        self.assertIn("[game:fake]", text)
        self.assertIn("[timing:estimated]", text)
        self.assertIn("[id:c1][ref:r1]hello", text)
        self.assertIn("[id:c2]world", text)
        self.assertIn("[length:", text)

    def test_manifest_tts_lrc_uses_manifest_starts(self):
        script = {
            "title": "T", "game_id": "fake", "track": "quick",
            "cues": [
                {"id": "c1", "beats": [{"text": "hello"}], "refs": []},
                {"id": "c2", "beats": [{"text": "world"}], "refs": ["r2"]},
            ],
        }
        manifest = {
            "gap_seconds": 0.2,
            "cues": [
                {"id": "c1", "start": 0.0, "duration": 1.5},
                {"id": "c2", "start": 1.7, "duration": 2.0},
            ],
        }
        with tempfile.TemporaryDirectory() as tmp:
            game_dir = Path(tmp) / "fake"
            (game_dir / "tutorial").mkdir(parents=True)
            path = lrc.write_manifest_tts_lrc(game_dir, "quick", script, manifest)
            text = path.read_text(encoding="utf-8")

        self.assertIn("[timer]".replace("timer", "timing:tts"), text)
        self.assertIn("[00:00.00][id:c1]hello", text)
        self.assertIn("[00:01.70][id:c2][ref:r2]world", text)
        self.assertIn("[length:00:03.90]", text)

    def test_rewrite_tts_lrc_replaces_times_and_drops_missing_cues(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            src = base / "in.lrc"
            out = base / "out.lrc"
            src.write_text(
                "[ti:T]\n[game:fake]\n[track:quick]\n[timing:estimated]\n"
                "[group:G]\n"
                "[00:00.00][id:c1][ref:r1]hello\n"
                "[00:01.00][id:c2][ref:r2]world\n"
                "[length:00:02.00]\n",
                encoding="utf-8",
            )
            results = [{"id": "c1", "duration": 1.25}, {"id": "c3", "duration": 1.0}]

            lrc.rewrite_tts_lrc(src, out, [], results, 0.2)
            text = out.read_text(encoding="utf-8")

        self.assertIn("[timing:tts]", text)
        self.assertIn("[00:00.00][id:c1][ref:r1]hello", text)
        self.assertNotIn("world", text)
        self.assertIn("[length:00:01.45]", text)


if __name__ == "__main__":
    unittest.main()
