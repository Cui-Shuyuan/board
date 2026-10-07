#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for compile_tutorial path handling and game/track parameters."""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import compile_tutorial as ct  # noqa: E402


class CompileTutorialPathTests(unittest.TestCase):
    def test_resolve_game_uses_explicit_game(self):
        with mock.patch.object(ct, "ROOT", Path("/nonexistent")):
            self.assertEqual("chosen", ct.resolve_game("chosen", "full"))

    def test_resolve_game_discovers_unique_track_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game = root / "content" / "games" / "fake-game"
            (game / "tutorial" / "anim" / "v2").mkdir(parents=True)
            (game / "media" / "tts" / "full").mkdir(parents=True)
            (game / "tutorial" / "script.full.json").write_text("{}", encoding="utf-8")
            (game / "tutorial" / "anim" / "v2" / "full.anim.json").write_text("{}", encoding="utf-8")
            (game / "media" / "tts" / "full" / "tts_manifest.json").write_text("{}", encoding="utf-8")
            with mock.patch.object(ct, "ROOT", root):
                self.assertEqual("fake-game", ct.resolve_game(None, "full"))

    def test_normalize_manifest_paths_converts_windows_separators(self):
        manifest = {
            "cues": [
                {
                    "id": "cue.1",
                    "file": "content\\games\\fake\\media\\tts\\full\\cue.1.mp3",
                    "subtitle_file": "content\\games\\fake\\media\\tts\\full\\cue.1.subtitle.json",
                }
            ]
        }

        ct.normalize_manifest_paths(manifest)

        cue = manifest["cues"][0]
        self.assertEqual("content/games/fake/media/tts/full/cue.1.mp3", cue["file"])
        self.assertEqual(
            "content/games/fake/media/tts/full/cue.1.subtitle.json",
            cue["subtitle_file"],
        )

    def test_windows_manifest_path_is_not_treated_as_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            audio = root / "content/games/fake/media/tts/full/cue.1.mp3"
            audio.parent.mkdir(parents=True)
            audio.write_bytes(b"")
            cue = {
                "id": "cue.1",
                "file": "content\\games\\fake\\media\\tts\\full\\cue.1.mp3",
            }

            self.assertTrue(ct.manifest_audio_ok(cue, root=root))
            self.assertFalse(ct.manifest_audio_ok({"file": "content\\missing.mp3"}, root=root))
            self.assertFalse(ct.manifest_audio_ok({"file": ""}, root=root))

    def test_delta_lrc_uses_explicit_game_and_track(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "delta.lrc"
            cues = [{"id": "cue.1", "beats": [{"text": "hello"}]}]

            ct.write_delta_lrc(out, cues, {}, game="fake-game", track="quick")

            text = out.read_text(encoding="utf-8")
        self.assertIn("[game:fake-game]", text)
        self.assertIn("[track:quick]", text)
        self.assertNotIn("splendor", text)

    def test_tts_asset_path_uses_game_and_track(self):
        rel = ct.tts_asset_rel("fake-game", "quick", "cue.1", ".mp3")
        self.assertEqual("content/games/fake-game/media/tts/quick/cue.1.mp3", rel)

    def test_qa_questions_path_uses_game(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root):
                path = ct.qa_questions_path("fake-game")
        self.assertEqual(
            "content/games/fake-game/tutorial/anim/_qa/questions.json",
            path.as_posix()[len(str(root)) + 1:],
        )


if __name__ == "__main__":
    unittest.main()
