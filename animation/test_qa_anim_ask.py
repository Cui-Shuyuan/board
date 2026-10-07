#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for qa_anim_ask game/profile path resolution."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import qa_anim_ask as qa  # noqa: E402


class QaAnimAskPathTests(unittest.TestCase):
    def test_infers_game_from_input_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            games_root = Path(tmp) / "content/games"
            (games_root / "fake-game/tutorial/anim/v2").mkdir(parents=True)
            anim = games_root / "fake-game/tutorial/anim/v2/full.anim.json"
            anim.write_text("{}", encoding="utf-8")
            with mock.patch.object(qa, "GAMES_ROOT", games_root):
                game = qa.resolve_game(None, anim)
        self.assertEqual("fake-game", game)

    def test_qa_input_falls_back_to_track_specific_questions(self):
        with tempfile.TemporaryDirectory() as tmp:
            games_root = Path(tmp) / "content/games"
            qa_dir = games_root / "fake-game/tutorial/anim/_qa"
            qa_dir.mkdir(parents=True)
            per_track = qa_dir / "questions.quick.json"
            per_track.write_text('{"asks": []}', encoding="utf-8")
            with mock.patch.object(qa, "GAMES_ROOT", games_root):
                path = qa.qa_input_for("fake-game", "quick", None)
        self.assertEqual(per_track, path)

    def test_profile_owns_game_id_and_demo_note(self):
        with tempfile.TemporaryDirectory() as tmp:
            games_root = Path(tmp) / "content/games"
            profile_dir = games_root / "fake-game/tutorial/animation"
            profile_dir.mkdir(parents=True)
            (profile_dir / "qa-profile.json").write_text(
                json.dumps({
                    "game_id": "fake-id",
                    "demo_note": "假演示局说明",
                }, ensure_ascii=False),
                encoding="utf-8",
            )
            with mock.patch.object(qa, "GAMES_ROOT", games_root):
                profile = qa.load_qa_profile("fake-game")
        self.assertEqual("fake-id", profile["game_id"])
        self.assertEqual("假演示局说明", profile["demo_note"])

    def test_explicit_game_wins_over_input_path(self):
        with mock.patch.object(qa, "GAMES_ROOT", Path("/nonexistent")):
            game = qa.resolve_game("fake-game", Path("/tmp/whatever.json"))
        self.assertEqual("fake-game", game)


if __name__ == "__main__":
    unittest.main()
