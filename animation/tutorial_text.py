#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Narration text normalization shared by TTS and subtitle rendering.

Rules:
- TTS input keeps punctuation so the provider can phrase naturally, but digits
  and symbol-like numeric tokens are converted to Chinese words first.
- Subtitle display reuses the TTS-normalized text and replaces punctuation with
  spaces; trailing punctuation is simply removed by the final strip.
- Animation labels are not touched by this module.
"""
from __future__ import annotations

import re

_DIGITS = "零一二三四五六七八九"
_SMALL_UNITS = ("", "十", "百", "千")
_GROUP_UNITS = ("", "万", "亿", "万亿")

# Symbols that should be spoken as words while punctuation is kept for TTS.
_SYMBOL_WORDS = {
    "+": "加",
    "＋": "加",
    "×": "乘",
    "✕": "乘",
    "·": "点",
    "•": "点",
    "・": "点",
    "%": "百分之",
    "％": "百分之",
    "~": "到",
    "～": "到",
}

_PUNCTUATION_RE = re.compile(
    r"[\u3000-\u303f\u2010-\u2027\u2030-\u205e"
    r"\uff01-\uff0f\uff1a-\uff20\uff3b-\uff40\uff5b-\uff65"
    r"!-/:-@\[-`{-~]"
)
_SPACE_RE = re.compile(r"\s+")
_NUMBER_RE = re.compile(r"\d+(?:\.\d+)*")
_DECIMAL_NUMBER_RE = re.compile(r"\d+(?:\.\d)+")


def _four_digit_to_chinese(value: int, *, leading_zero: bool = False) -> str:
    if value == 0:
        return "零"
    parts: list[str] = []
    zero_pending = False
    for position in range(3, -1, -1):
        divisor = 10 ** position
        digit = value // divisor
        value %= divisor
        if digit == 0:
            if parts and value > 0:
                zero_pending = True
            continue
        if zero_pending:
            parts.append("零")
            zero_pending = False
        parts.append(_DIGITS[digit])
        parts.append(_SMALL_UNITS[position])
    text = "".join(parts)
    if leading_zero and text.startswith("一十"):
        text = text[1:]
    return text


def integer_to_chinese(value: int) -> str:
    """Convert a non-negative integer to natural Chinese numerals."""
    if value < 0:
        return "负" + integer_to_chinese(-value)
    if value == 0:
        return "零"
    groups: list[int] = []
    while value > 0:
        groups.append(value % 10000)
        value //= 10000
    parts: list[str] = []
    zero_between = False
    for index in range(len(groups) - 1, -1, -1):
        group = groups[index]
        if group == 0:
            if parts:
                zero_between = True
            continue
        if parts and (zero_between or group < 1000):
            if parts[-1] != "零":
                parts.append("零")
        group_text = _four_digit_to_chinese(group)
        if parts and group_text.startswith("一十"):
            group_text = group_text[1:]
        parts.append(group_text)
        parts.append(_GROUP_UNITS[index])
        zero_between = False
    text = "".join(parts)
    # "一十" is normally spoken/written as "十" for standalone 10-19.
    if text.startswith("一十"):
        text = text[1:]
    return text


def _number_token_to_chinese(token: str) -> str:
    if "." not in token:
        return integer_to_chinese(int(token))
    parts = token.split(".")
    if len(parts) == 2:
        whole_text = integer_to_chinese(int(parts[0])) if parts[0] else "零"
        fraction = "".join(_DIGITS[int(ch)] for ch in parts[1])
        return whole_text + "点" + fraction
    # Version-like section numbers such as 3.2.1 read dot by dot.
    return "点".join(
        integer_to_chinese(int(part)) if part else "零"
        for part in parts
    )


def to_tts_text(text: str) -> str:
    """Normalize digits and symbol words while preserving punctuation."""
    value = str(text or "")
    value = _NUMBER_RE.sub(lambda match: _number_token_to_chinese(match.group(0)), value)
    for symbol, spoken in _SYMBOL_WORDS.items():
        value = value.replace(symbol, spoken)
    return value


def to_subtitle_text(text: str) -> str:
    """Return the bottom-subtitle form: normalized words without punctuation."""
    value = to_tts_text(text)
    value = _PUNCTUATION_RE.sub(" ", value)
    return _SPACE_RE.sub(" ", value).strip()
