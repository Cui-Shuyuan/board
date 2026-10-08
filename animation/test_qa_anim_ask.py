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


class QaAnimAskVerdictTests(unittest.TestCase):
    def test_empty_or_unexpected_reply_is_unknown(self):
        self.assertEqual("?", qa.verdict_of(""))
        self.assertEqual("?", qa.verdict_of("请继续观察，这里没有结论。"))

    def test_expected_pattern_selects_hand_written_vocabulary_family(self):
        self.assertEqual(r"(不允许|允许)", qa._verdict_pattern("允许"))
        self.assertEqual(r"(不允许|允许)", qa._verdict_pattern("不允许"))
        self.assertEqual(r"(不合法|合法|有问题)", qa._verdict_pattern("合法"))
        self.assertEqual(r"(不合法|合法|有问题)", qa._verdict_pattern("有问题"))
        self.assertIn("合法", qa._verdict_pattern(None))

    def test_multiple_candidates_uses_last_standalone_verdict(self):
        self.assertEqual("允许", qa.verdict_of("不允许。\n允许。"))
        self.assertEqual("合法", qa.verdict_of("有问题。\n合法。"))

    def test_negated_problem_phrases_are_positive(self):
        self.assertEqual("合法", qa.verdict_of("没有问题。"))
        self.assertEqual("合法", qa.verdict_of("没有不合法。"))
        self.assertEqual("不合法", qa.verdict_of("这个操作不合法。"))

    def test_self_correction_prefers_last_standalone_verdict(self):
        self.assertEqual("合法", qa.verdict_of("我一开始以为有问题，但按规则是合法。"))
        self.assertEqual("允许", qa.verdict_of("不允许。更正：允许。"))

    def test_expected_family_ignores_explanatory_synonym(self):
        self.assertEqual("允许", qa.verdict_of("允许。这个动作合法。", "允许"))
        self.assertEqual("有问题", qa.verdict_of("有问题。对方合法取得。", "有问题"))


class QaAnimAskAsksLoaderTests(unittest.TestCase):
    def test_load_asks_extracts_cue_qa_string_dict_and_list(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "anim.json"
            path.write_text(json.dumps({"cues": [
                {"id": "cue.1", "qa": "single"},
                {"id": "cue.2", "qa": [{"q": "object", "expect": "允许"}, "list-string"]},
                {"id": "cue.3", "qa": {"q": "   ", "expect": "合法"}},
                {"id": "cue.4", "qa": ["", "  valid  "]},
            ]}, ensure_ascii=False), encoding="utf-8")
            spec = qa.load_asks(path)
        self.assertEqual([x["q"] for x in spec["asks"]],
                         ["single", "object", "list-string", "valid"])
        self.assertEqual("允许", spec["asks"][1]["expect"])
        self.assertEqual("cue.1", spec["asks"][0]["cue"])
        self.assertEqual("cue.4", spec["asks"][3]["cue"])

    def test_load_asks_leaves_plain_questions_spec_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "questions.json"
            doc = {"asks": [{"cue": "cue.1", "q": "question"}]}
            path.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
            self.assertEqual(qa.load_asks(path), doc)


if __name__ == "__main__":
    unittest.main()
