#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Small, dependency-free audio duration probe used by build-time validators.

The tutorial pipeline only needs to verify that a declared plan/source range
fits inside the packaged media.  It must fail at build time, not after the
Android/Unity plan-mode split has already been chosen at runtime.
"""
from __future__ import annotations

import math
import struct
from pathlib import Path


class AudioProbeError(ValueError):
    """Raised when an audio container cannot be measured."""


_MP3_BITRATES_V1_LAYER3 = (
    0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0,
)
_MP3_BITRATES_V2_LAYER3 = (
    0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0,
)
_MP3_SAMPLE_RATES = {
    1: (44100, 48000, 32000, 0),  # MPEG-1
    2: (22050, 24000, 16000, 0),  # MPEG-2
    0: (11025, 12000, 8000, 0),   # MPEG-2.5
}


def _wav_duration_seconds(data: bytes) -> float:
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise AudioProbeError("not a RIFF/WAVE file")
    offset = 12
    sample_rate = channels = bits_per_sample = 0
    data_size: int | None = None
    while offset + 8 <= len(data):
        chunk_id = data[offset:offset + 4]
        chunk_size = struct.unpack_from("<I", data, offset + 4)[0]
        payload = offset + 8
        end = payload + chunk_size
        if end > len(data):
            raise AudioProbeError("truncated WAV chunk")
        if chunk_id == b"fmt " and chunk_size >= 16:
            _, channels, sample_rate, _, _, bits_per_sample = struct.unpack_from(
                "<HHIIHH", data, payload)
        elif chunk_id == b"data":
            data_size = chunk_size
        offset = end + (chunk_size & 1)
    if sample_rate <= 0 or channels <= 0 or bits_per_sample <= 0:
        raise AudioProbeError("WAV fmt chunk is missing or invalid")
    if data_size is None:
        raise AudioProbeError("WAV data chunk is missing")
    bytes_per_second = sample_rate * channels * max(1, bits_per_sample // 8)
    return data_size / float(bytes_per_second)


def _mp3_duration_seconds(data: bytes) -> float:
    offset = 0
    if len(data) >= 10 and data[:3] == b"ID3":
        size = (
            ((data[6] & 0x7F) << 21)
            | ((data[7] & 0x7F) << 14)
            | ((data[8] & 0x7F) << 7)
            | (data[9] & 0x7F)
        )
        offset = 10 + size + (10 if data[5] & 0x10 else 0)

    total = 0.0
    frames = 0
    length = len(data)
    while offset + 4 <= length:
        if data[offset:offset + 3] == b"TAG":
            break
        header = int.from_bytes(data[offset:offset + 4], "big")
        if (header >> 21) & 0x7FF != 0x7FF:
            offset += 1
            continue
        version_bits = (header >> 19) & 0x3
        layer_bits = (header >> 17) & 0x3
        if version_bits == 1 or layer_bits != 1:  # Layer III only
            offset += 1
            continue
        bitrate_index = (header >> 12) & 0xF
        sample_rate_index = (header >> 10) & 0x3
        padding = (header >> 9) & 0x1
        if bitrate_index in (0, 15) or sample_rate_index == 3:
            offset += 1
            continue
        version = 3 if version_bits == 3 else (2 if version_bits == 2 else 0)
        bitrate_table = (
            _MP3_BITRATES_V1_LAYER3
            if version == 1 else _MP3_BITRATES_V2_LAYER3
        )
        bitrate = bitrate_table[bitrate_index] * 1000
        sample_rate = _MP3_SAMPLE_RATES[version][sample_rate_index]
        if version == 1:
            samples_per_frame = 1152
            frame_length = (144 * bitrate) // sample_rate + padding
        else:
            samples_per_frame = 576
            frame_length = (72 * bitrate) // sample_rate + padding
        if frame_length <= 4 or offset + frame_length > length:
            offset += 1
            continue
        total += samples_per_frame / float(sample_rate)
        frames += 1
        offset += frame_length
    if frames == 0:
        raise AudioProbeError("no MPEG Layer III frames found")
    return total


def media_duration_seconds(path: Path) -> float:
    """Return container duration in seconds for WAV or MPEG Layer III."""
    data = Path(path).read_bytes()
    if len(data) < 4:
        raise AudioProbeError(f"audio file is too small: {path}")
    if data[:4] == b"RIFF":
        duration = _wav_duration_seconds(data)
    else:
        duration = _mp3_duration_seconds(data)
    if not math.isfinite(duration) or duration <= 0.0:
        raise AudioProbeError(f"audio duration is invalid: {path}")
    return duration
