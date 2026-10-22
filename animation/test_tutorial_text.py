#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))

import tutorial_text  # noqa: E402


class TutorialTextTests(unittest.TestCase):
    def test_tts_text_converts_section_numbers_without_date_reading(self) -> None:
        self.assertEqual(
            tutorial_text.to_tts_text("3.5，回合结束与循环。"),
            "三点五，回合结束与循环。",
        )
        self.assertEqual(
            tutorial_text.to_tts_text("3.2.1，认识发展卡牌。"),
            "三点二点一，认识发展卡牌。",
        )

    def test_tts_text_converts_integer_quantities_and_plus_symbol(self) -> None:
        self.assertEqual(
            tutorial_text.to_tts_text("2人游戏时拿4枚，玩家数量+1块。"),
            "二人游戏时拿四枚，玩家数量加一块。",
        )

    def test_subtitle_text_removes_punctuation_and_keeps_word_spaces(self) -> None:
        self.assertEqual(
            tutorial_text.to_subtitle_text("3.5，回合结束与循环。"),
            "三点五 回合结束与循环",
        )
        self.assertEqual(
            tutorial_text.to_subtitle_text(
                "玩家A有17分，玩家B有14分，A获胜。"),
            "玩家A有十七分 玩家B有十四分 A获胜",
        )

    def test_subtitle_text_has_no_digits_or_punctuation(self) -> None:
        text = tutorial_text.to_subtitle_text(
            "2.4，起始玩家；每位拿3枚（共4种）。")
        self.assertFalse(any(ch.isdigit() for ch in text))
        self.assertFalse(any(ch in "，。；：、（）." for ch in text))
        self.assertEqual(text, tutorial_text.to_subtitle_text(text))


if __name__ == "__main__":
    unittest.main()
