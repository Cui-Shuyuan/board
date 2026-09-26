#!/usr/bin/env python3
"""Volcengine/Doubao **standard small-model** TTS v1 bridge.

This module implements the classic v1 WebSocket binary protocol used by the
small-model speech synthesis service:

    wss://openspeech.bytedance.com/api/v1/tts/ws_binary

Official docs:
    * WebSocket interface: https://www.volcengine.com/docs/6561/79821
    * Parameter reference: https://www.volcengine.com/docs/6561/79823
    * Voice list:          https://www.volcengine.com/docs/6561/97465
    * API Key usage:       https://www.volcengine.com/docs/6561/2119699

Authentication
--------------
The new console can use a project-scoped API key:

    X-Api-Key: <VOLCENGINE_API_KEY>

The legacy console uses a bearer token in the connect header:

    Authorization: Bearer; <VOLCENGINE_TTS_ACCESS_TOKEN>

plus ``app.appid`` / ``app.token`` / ``app.cluster`` inside the request JSON.
The standard small-model service ignores ``app.token`` and historically used
``cluster=volcano_tts``.

Output
------
mp3 (default) or another encoding supported by the v1 service.  The v1
WebSocket streaming response does not provide the v3 subtitle events the old
seed-tts-2.0 implementation consumed, so this module intentionally does not
write ``*.subtitle.json``.  Callers must treat subtitle timing as unavailable
and must not pretend the missing data exists.
"""

from __future__ import annotations

import argparse
import asyncio
import gzip
import json
import os
import re
import sys
import uuid
import wave
from pathlib import Path
from types import SimpleNamespace
from typing import Any

try:
    import websockets
    from websockets.exceptions import ConnectionClosed, InvalidStatusCode
except ImportError as exc:  # pragma: no cover - dependency check
    raise SystemExit("缺少 websockets，请先执行：pip install websockets") from exc

try:
    from mutagen.mp3 import MP3
except ImportError:  # pragma: no cover - optional dependency
    MP3 = None


DEFAULT_ENDPOINT = "wss://openspeech.bytedance.com/api/v1/tts/ws_binary"
DEFAULT_VOICE = "BV700_streaming"  # 灿灿, 中文女声, 通用/讲故事
DEFAULT_CLUSTER = "volcano_tts"
# Billing/usage-query resource id for 语音合成(小模型版).  The v1 request
# itself uses app.cluster, not X-Api-Resource-Id.
DEFAULT_RESOURCE_ID = "volc.tts.default"
DEFAULT_SAMPLE_RATE = 24000
DEFAULT_ENCODING = "mp3"
DEFAULT_SPEED_RATIO = 1.0
DEFAULT_VOLUME_RATIO = 1.0
DEFAULT_PITCH_RATIO = 1.0

MSG_AUDIO_ONLY_SERVER = 0xB
MSG_FULL_SERVER = 0x9
MSG_FRONTEND_SERVER = 0xC
MSG_ERROR = 0xF

_STANDARD_VOICE_RE = re.compile(r"^(?:BV|BR)\d+.*_streaming$", re.IGNORECASE)


def is_standard_voice(voice: str) -> bool:
    """Return True for documented small-model voice_type values."""
    return bool(_STANDARD_VOICE_RE.match((voice or "").strip()))


def _env_first(*names: str) -> str:
    for name in names:
        value = os.environ.get(name, "").strip()
        if value:
            return value
    return ""


def resolve_voice(requested: str | None) -> str:
    """Resolve a standard small-model voice and avoid stale v3 voice ids.

    ``compile_tutorial.py`` can pass a ``voice`` value from an older
    seed-tts-2.0 manifest.  Rather than failing a delta TTS run, a mismatched
    voice is reported and the current standard default is used.
    """
    requested = (requested or "").strip()
    if is_standard_voice(requested):
        return requested

    env_voice = _env_first("DOUBAO_TTS_VOICE")
    if env_voice and is_standard_voice(env_voice):
        fallback = env_voice
    else:
        fallback = DEFAULT_VOICE

    if requested:
        print(
            f"[warn] 标准语音合成不支持音色 {requested!r}；"
            f"已回退到 {fallback!r}",
            file=sys.stderr,
        )
    return fallback


def resolve_cluster(requested: str | None = None) -> str:
    return (
        (requested or "").strip()
        or _env_first("DOUBAO_TTS_CLUSTER", "DOUBAO_CLUSTER")
        or DEFAULT_CLUSTER
    )


def resolve_resource_id(requested: str | None = None) -> str:
    return (
        (requested or "").strip()
        or _env_first("DOUBAO_TTS_RESOURCE_ID", "DOUBAO_RESOURCE_ID")
        or DEFAULT_RESOURCE_ID
    )


def resolve_endpoint(requested: str | None = None) -> str:
    return (
        (requested or "").strip()
        or _env_first("DOUBAO_TTS_ENDPOINT")
        or DEFAULT_ENDPOINT
    )


def resolve_sample_rate(value: Any | None = None) -> int:
    if value is not None:
        try:
            rate = int(value)
        except (TypeError, ValueError):
            rate = 0
        if rate > 0:
            return rate
    env_rate = _env_first("DOUBAO_TTS_SAMPLE_RATE")
    if env_rate:
        try:
            return int(env_rate)
        except ValueError:
            pass
    return DEFAULT_SAMPLE_RATE


def resolve_encoding(value: str | None = None) -> str:
    encoding = (value or "").strip().lower()
    if not encoding:
        encoding = _env_first("DOUBAO_TTS_FORMAT") or DEFAULT_ENCODING
    if encoding not in {"mp3", "pcm", "ogg_opus", "wav"}:
        raise RuntimeError(f"标准语音合成不支持的音频格式：{encoding}")
    if encoding == "wav":
        raise RuntimeError("标准语音合成 WebSocket 流式接口不支持 wav，请使用 mp3/pcm/ogg_opus")
    return encoding


def resolve_speed_ratio(value: Any | None = None) -> float:
    if value is None:
        return DEFAULT_SPEED_RATIO
    return float(value)


def _app_and_headers(cluster: str) -> tuple[dict[str, dict[str, str]], dict[str, str], str]:
    """Build the ``app`` request object and connection headers.

    The new-console shared API key is preferred when ``VOLCENGINE_API_KEY`` is
    present.  The API-key docs say appid is not needed; the legacy JSON field
    is therefore omitted.  ``app.token`` is documented as an arbitrary
    non-empty value for the standard TTS service.
    """
    api_key = _env_first("VOLCENGINE_API_KEY")
    app_id = _env_first("VOLCENGINE_TTS_APP_ID", "VOLCENGINE_APP_ID")
    access_token = _env_first("VOLCENGINE_TTS_ACCESS_TOKEN", "VOLCENGINE_ACCESS_TOKEN")
    preferred_mode = _env_first("DOUBAO_TTS_AUTH_MODE").lower()

    if preferred_mode == "legacy":
        api_key = ""
    elif preferred_mode in {"apikey", "api_key", "api-key"}:
        app_id = ""

    if api_key:
        headers = {"X-Api-Key": api_key}
        app = {"token": "api-key", "cluster": cluster}
        return app, headers, "api_key"

    if app_id and access_token:
        headers = {"Authorization": f"Bearer; {access_token}"}
        app = {"appid": app_id, "token": access_token, "cluster": cluster}
        return app, headers, "legacy_token"

    raise RuntimeError(
        "缺少语音合成凭证：请设置 VOLCENGINE_API_KEY，"
        "或旧版控制台 VOLCENGINE_TTS_APP_ID + VOLCENGINE_TTS_ACCESS_TOKEN"
    )


def build_request(text: str, voice: str, args: Any) -> tuple[bytes, str]:
    cluster = resolve_cluster(getattr(args, "cluster", None))
    app, _headers, auth_mode = _app_and_headers(cluster)
    speech_rate = getattr(args, "speed_ratio", None)
    if speech_rate is None:
        speech_rate = getattr(args, "speed", None)
    if speech_rate is None:
        # Backward compatibility with the seed2 CLI's integer speech_rate.
        raw_old = getattr(args, "speech_rate", None)
        if raw_old is not None:
            speech_rate = 1.0 + (float(raw_old) / 100.0)
    if speech_rate is None:
        speech_rate = DEFAULT_SPEED_RATIO

    volume_ratio = getattr(args, "volume_ratio", None)
    if volume_ratio is None:
        volume_ratio = DEFAULT_VOLUME_RATIO
    pitch_ratio = getattr(args, "pitch_ratio", None)
    if pitch_ratio is None:
        pitch_ratio = DEFAULT_PITCH_RATIO

    encoding = resolve_encoding(getattr(args, "format", None))
    sample_rate = resolve_sample_rate(getattr(args, "sample_rate", None))

    request = {
        "app": app,
        "user": {"uid": getattr(args, "uid", None) or "board-tutorial"},
        "audio": {
            "voice_type": voice,
            "encoding": encoding,
            "rate": sample_rate,
            "speed_ratio": float(speech_rate),
            "volume_ratio": float(volume_ratio),
            "pitch_ratio": float(pitch_ratio),
        },
        "request": {
            "reqid": str(uuid.uuid4()),
            "text": text,
            "text_type": "plain",
            "operation": "submit",
            "silence_duration": 125,
        },
    }
    payload = gzip.compress(json.dumps(request, ensure_ascii=False).encode("utf-8"))
    return (
        b"\x11\x10\x11\x00"
        + len(payload).to_bytes(4, "big")
        + payload,
        auth_mode,
    )


async def _connect(endpoint: str, headers: dict[str, str]):
    kwargs = {
        "max_size": 64 * 1024 * 1024,
        "open_timeout": 20,
        "close_timeout": 5,
        "ping_interval": None,
    }
    try:
        return await websockets.connect(endpoint, additional_headers=headers, **kwargs)
    except TypeError:
        return await websockets.connect(endpoint, extra_headers=headers, **kwargs)


def _audio_duration(path: Path, encoding: str, sample_rate: int) -> float:
    if encoding == "mp3":
        if MP3 is None:
            raise RuntimeError("缺少 mutagen，请先执行：pip install mutagen")
        return float(MP3(str(path)).info.length)
    if encoding == "wav":
        with wave.open(str(path), "rb") as wav:
            return wav.getnframes() / float(wav.getframerate())
    if encoding == "pcm":
        # v1 pcm is signed 16-bit mono by this bridge/project convention.
        return path.stat().st_size / float(max(1, sample_rate * 2))
    if encoding == "ogg_opus":
        # mutagen cannot reliably report opus stream duration without more
        # parsing; do not invent a value.
        raise RuntimeError("ogg_opus 时长暂不支持；请使用 mp3/pcm")
    raise RuntimeError(f"暂不支持计算 {encoding} 时长")


def _decode_error_payload(payload: bytes, compression: int) -> tuple[int, str]:
    if len(payload) < 8:
        return -1, "格式错误的错误响应"
    code = int.from_bytes(payload[:4], "big", signed=False)
    msg_size = int.from_bytes(payload[4:8], "big", signed=False)
    message = payload[8 : 8 + msg_size]
    if compression == 1:
        try:
            message = gzip.decompress(message)
        except OSError:
            pass
    return code, message.decode("utf-8", "replace")


async def synthesize_text(
    text: str,
    out_file: Path,
    voice: str | None = None,
    args: Any | None = None,
) -> float:
    """Synthesize one text string and return mp3/pcm duration in seconds."""
    args = args or SimpleNamespace()
    text = (text or "").strip()
    if not text:
        raise RuntimeError("TTS text is empty")
    encoded = text.encode("utf-8")
    if len(encoded) > 1024:
        raise RuntimeError(f"标准语音合成单次文本超过 1024 UTF-8 字节：{len(encoded)}")

    resolved_voice = resolve_voice(voice or getattr(args, "voice", None))
    endpoint = resolve_endpoint(getattr(args, "endpoint", None))
    encoding = resolve_encoding(getattr(args, "format", None))
    sample_rate = resolve_sample_rate(getattr(args, "sample_rate", None))

    frame, auth_mode = build_request(text, resolved_voice, args)
    cluster = resolve_cluster(getattr(args, "cluster", None))
    _app, headers, _auth_mode = _app_and_headers(cluster)
    # Keep auth_mode visible for diagnostics without printing secrets.
    _ = auth_mode

    out_file = out_file.resolve()
    out_file.parent.mkdir(parents=True, exist_ok=True)
    tmp_file = out_file.with_name(out_file.name + ".part")
    audio_bytes = bytearray()

    try:
        websocket = await _connect(endpoint, headers)
    except InvalidStatusCode as exc:
        raise RuntimeError(
            f"标准语音合成 WebSocket 建连被拒绝（HTTP {getattr(exc, 'status_code', '?')}）；"
            "请检查 VOLCENGINE_API_KEY 与标准语音合成是否已开通"
        ) from exc

    try:
        await websocket.send(frame)
        while True:
            try:
                data = await asyncio.wait_for(websocket.recv(), timeout=75)
            except asyncio.TimeoutError as exc:
                raise RuntimeError("标准语音合成等待服务端响应超时") from exc

            if not isinstance(data, (bytes, bytearray)):
                raise RuntimeError("标准语音合成收到非二进制响应")
            if len(data) < 4:
                raise RuntimeError(f"标准语音合成响应过短：{len(data)} bytes")

            header_size = (data[0] & 0x0F) * 4
            if header_size < 4 or header_size > len(data):
                raise RuntimeError("标准语音合成响应 header 无效")
            message_type = data[1] >> 4
            message_flags = data[1] & 0x0F
            compression = data[2] & 0x0F
            payload = bytes(data[header_size:])

            if message_type == MSG_AUDIO_ONLY_SERVER:
                if message_flags == 0:
                    # ACK with no audio.
                    continue
                if len(payload) < 8:
                    raise RuntimeError("标准语音合成音频包过短")
                sequence = int.from_bytes(payload[:4], "big", signed=True)
                payload_size = int.from_bytes(payload[4:8], "big", signed=False)
                chunk = payload[8 : 8 + payload_size]
                if len(chunk) != payload_size:
                    raise RuntimeError("标准语音合成音频包长度不完整")
                if compression == 1:
                    try:
                        chunk = gzip.decompress(chunk)
                    except OSError as exc:
                        raise RuntimeError(f"标准语音合成音频解压失败：{exc}") from exc
                elif compression != 0:
                    raise RuntimeError(f"标准语音合成不支持的压缩方式：{compression}")
                audio_bytes.extend(chunk)
                if sequence < 0:
                    break
                continue

            if message_type == MSG_ERROR:
                code, message = _decode_error_payload(payload, compression)
                raise RuntimeError(f"标准语音合成服务返回错误 {code}: {message}")

            if message_type == MSG_FRONTEND_SERVER:
                # Optional frontend info (for example timestamps).  The
                # current small-model bridge has no subtitle consumer, so
                # ignore it rather than inventing timing data.
                continue

            if message_type == MSG_FULL_SERVER:
                # Deprecated full server response in v1; ignore safely.
                continue

            raise RuntimeError(
                f"标准语音合成未知响应消息类型：{message_type} (flags={message_flags})"
            )
    except ConnectionClosed as exc:
        raise RuntimeError(
            "标准语音合成 WebSocket 被服务端关闭（常见原因：VOLCENGINE_API_KEY 无效、" 
            "项目未开通标准语音合成，或音色/参数与标准小模型不匹配）。"
            "旧版控制台可设置 VOLCENGINE_TTS_APP_ID + "
            "VOLCENGINE_TTS_ACCESS_TOKEN。"
        ) from exc
    finally:
        try:
            await websocket.close()
        except Exception:
            pass

    if not audio_bytes:
        raise RuntimeError("标准语音合成没有返回音频数据")

    tmp_file.write_bytes(bytes(audio_bytes))
    tmp_file.replace(out_file)

    duration = _audio_duration(out_file, encoding, sample_rate)
    return float(duration)


def _get_arg(args: Any, name: str, default: Any = None) -> Any:
    return getattr(args, name, default)


async def synthesize_all(cues: list[dict[str, Any]], args: argparse.Namespace) -> list[dict[str, Any]]:
    """Generate one audio file per cue and return the standard CLI result list."""
    out_dir = Path(_get_arg(args, "out_dir"))
    out_dir.mkdir(parents=True, exist_ok=True)
    encoding = resolve_encoding(_get_arg(args, "format"))
    sample_rate = resolve_sample_rate(_get_arg(args, "sample_rate"))
    voice = resolve_voice(_get_arg(args, "voice"))
    args.format = encoding
    args.sample_rate = sample_rate
    args.voice = voice

    results: list[dict[str, Any]] = []
    for cue in cues:
        audio_path = out_dir / f"{cue['id']}.{encoding}"
        subtitle_path = out_dir / f"{cue['id']}.subtitle.json"

        if audio_path.exists() and not _get_arg(args, "force", False) and not _get_arg(args, "overwrite", False):
            duration = _audio_duration(audio_path, encoding, sample_rate)
            print(f"[skip] {cue['id']} 已存在")
        else:
            print(f"[tts:standard] {cue['id']} ({len(cue['text'])} 字) -> {audio_path.name}")
            duration = await synthesize_text(cue["text"], audio_path, voice, args)
            # v1 standard streaming TTS has no subtitle events.  Do not leave
            # stale seed-tts-2.0 subtitle JSON next to a newly generated wav.
            try:
                if subtitle_path.exists():
                    subtitle_path.unlink()
            except OSError:
                pass
            print(f"      duration={duration:.3f}s")

        root = Path(_get_arg(args, "root", Path.cwd()))
        try:
            rel_file = str(audio_path.relative_to(root))
        except ValueError:
            rel_file = str(audio_path)
        results.append(
            {
                "id": cue["id"],
                "text_length": len(cue["text"]),
                "file": rel_file,
                "subtitle_file": None,
                "duration": duration,
            }
        )
    return results
