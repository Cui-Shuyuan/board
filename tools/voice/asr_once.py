#!/usr/bin/env python3
"""
One-shot Volcengine ASR bridge.

Protocol: Volcengine API v2 one-sentence WebSocket ASR.
Docs: https://www.volcengine.com/docs/6561/80816
Auth: https://www.volcengine.com/docs/6561/107789 (token auth)

Required environment variables (normally loaded from repo-root .env):
    VOLCENGINE_ASR_APP_ID
    VOLCENGINE_ASR_ACCESS_TOKEN
    VOLCENGINE_ASR_CLUSTER
Optional:
    VOLCENGINE_ASR_ENDPOINT  default wss://openspeech.bytedance.com/api/v2/asr
    VOLCENGINE_ASR_UID       default boardai-android
    VOLCENGINE_ASR_WORKFLOW  default audio_in,...,itn,nlu_punctuate

Usage:
    python tools/voice/asr_once.py --input /absolute/path/rec.wav

Stdout on success:
    {"text":"...","request_id":"..."}

On failure this script prints a clear message to stderr and exits non-zero.
"""

from __future__ import annotations

import argparse
import asyncio
import gzip
import json
import os
import sys
import uuid
import wave
from pathlib import Path
from typing import Any, AsyncIterator

try:
    import websockets
except ImportError as exc:  # pragma: no cover - dependency check
    print("错误：缺少 websockets，请执行 pip install -r animation/requirements-tts.txt", file=sys.stderr)
    raise SystemExit(2) from exc


SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent

PROTOCOL_VERSION = 0b0001
DEFAULT_HEADER_SIZE = 0b0001

CLIENT_FULL_REQUEST = 0b0001
CLIENT_AUDIO_ONLY_REQUEST = 0b0010
SERVER_FULL_RESPONSE = 0b1001
SERVER_ACK = 0b1011
SERVER_ERROR_RESPONSE = 0b1111

NO_SEQUENCE = 0b0000
NEG_SEQUENCE = 0b0010

NO_SERIALIZATION = 0b0000
JSON_SERIALIZATION = 0b0001

NO_COMPRESSION = 0b0000
GZIP_COMPRESSION = 0b0001

DEFAULT_ENDPOINT = "wss://openspeech.bytedance.com/api/v2/asr"
DEFAULT_WORKFLOW = "audio_in,resample,partition,vad,fe,decode,itn,nlu_punctuate"
SUCCESS_CODE = 1000


def load_dotenv(path: Path) -> None:
    """Minimal .env reader that does not overwrite real environment variables."""
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key, value = key.strip(), value.strip().strip('"').strip("'")
        if key:
            os.environ.setdefault(key, value)


def generate_header(
    message_type: int,
    message_type_specific_flags: int = NO_SEQUENCE,
    serial_method: int = JSON_SERIALIZATION,
    compression_type: int = GZIP_COMPRESSION,
) -> bytes:
    header_size = DEFAULT_HEADER_SIZE
    return bytes(
        [
            (PROTOCOL_VERSION << 4) | header_size,
            (message_type << 4) | message_type_specific_flags,
            (serial_method << 4) | compression_type,
            0x00,  # reserved
        ]
    )


def parse_response(data: bytes) -> dict[str, Any]:
    if len(data) < 4:
        raise RuntimeError(f"ASR 响应过短：{len(data)} bytes")

    header_size = (data[0] & 0x0F) * 4
    message_type = data[1] >> 4
    message_compression = data[2] & 0x0F
    payload = data[header_size:]

    result: dict[str, Any] = {"message_type": message_type}
    payload_msg: bytes | None = None

    if message_type == SERVER_FULL_RESPONSE:
        if len(payload) < 4:
            raise RuntimeError("ASR full response 缺少 payload size")
        payload_msg = payload[4:]
    elif message_type == SERVER_ACK:
        if len(payload) < 8:
            raise RuntimeError("ASR ACK 缺少 payload size")
        payload_msg = payload[8:]
    elif message_type == SERVER_ERROR_RESPONSE:
        if len(payload) < 8:
            raise RuntimeError("ASR error response 缺少 payload size")
        result["error_code"] = int.from_bytes(payload[:4], "big", signed=False)
        payload_msg = payload[8:]
    else:
        raise RuntimeError(f"未知 ASR 消息类型：{message_type}")

    if payload_msg is None or not payload_msg:
        return result

    if message_compression == GZIP_COMPRESSION:
        try:
            payload_msg = gzip.decompress(payload_msg)
        except OSError as exc:
            raise RuntimeError(f"ASR 响应 gzip 解压失败：{exc}") from exc

    try:
        result["payload_msg"] = json.loads(payload_msg.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"ASR 响应不是合法 JSON：{exc}") from exc

    return result


def read_wav_info(path: Path) -> tuple[int, int, int, int, float]:
    try:
        with wave.open(str(path), "rb") as wav:
            channels = wav.getnchannels()
            sample_width = wav.getsampwidth()
            sample_rate = wav.getframerate()
            frames = wav.getnframes()
    except wave.Error as exc:
        raise RuntimeError(f"WAV 文件无效：{exc}") from exc

    if channels <= 0 or sample_rate <= 0 or sample_width <= 0 or frames <= 0:
        raise RuntimeError("WAV 文件参数无效")

    duration = frames / float(sample_rate)
    return channels, sample_width * 8, sample_rate, frames, duration


def slice_data(data: bytes, chunk_size: int) -> AsyncIterator[tuple[bytes, bool]]:
    """Yield chunks; the last chunk is flagged with last=True."""
    async def _iter():
        offset = 0
        while offset + chunk_size < len(data):
            yield data[offset : offset + chunk_size], False
            offset += chunk_size
        yield data[offset:], True

    return _iter()


async def connect_websocket(url: str, headers: dict[str, str]):
    kwargs = {
        "max_size": 64 * 1024 * 1024,
        "open_timeout": 15,
        "close_timeout": 5,
    }
    try:
        return await websockets.connect(url, additional_headers=headers, **kwargs)
    except TypeError:
        return await websockets.connect(url, extra_headers=headers, **kwargs)


def ensure_success(result: dict[str, Any]) -> dict[str, Any] | None:
    payload = result.get("payload_msg")
    if not isinstance(payload, dict):
        return None

    code = int(payload.get("code", SUCCESS_CODE))
    if code != SUCCESS_CODE:
        message = payload.get("message") or "unknown error"
        raise RuntimeError(f"ASR 服务返回错误 {code}: {message}")
    return payload


def collect_text(payload: dict[str, Any] | None) -> str:
    if not payload:
        return ""
    result_items = payload.get("result") or []
    texts = [
        str(item.get("text") or "").strip()
        for item in result_items
        if isinstance(item, dict)
    ]
    return texts[-1] if texts else ""


async def recognize(input_path: Path) -> dict[str, str]:
    load_dotenv(REPO_ROOT / ".env")

    app_id = os.environ.get("VOLCENGINE_ASR_APP_ID", "").strip()
    access_token = os.environ.get("VOLCENGINE_ASR_ACCESS_TOKEN", "").strip()
    cluster = os.environ.get("VOLCENGINE_ASR_CLUSTER", "").strip()
    missing = [
        name
        for name, value in (
            ("VOLCENGINE_ASR_APP_ID", app_id),
            ("VOLCENGINE_ASR_ACCESS_TOKEN", access_token),
            ("VOLCENGINE_ASR_CLUSTER", cluster),
        )
        if not value
    ]
    if missing:
        raise RuntimeError("缺少 ASR 凭证：" + ", ".join(missing))

    endpoint = os.environ.get("VOLCENGINE_ASR_ENDPOINT", DEFAULT_ENDPOINT).strip() or DEFAULT_ENDPOINT
    uid = os.environ.get("VOLCENGINE_ASR_UID", "boardai-android").strip() or "boardai-android"
    workflow = os.environ.get("VOLCENGINE_ASR_WORKFLOW", DEFAULT_WORKFLOW).strip() or DEFAULT_WORKFLOW

    channels, bits, sample_rate, frames, duration = read_wav_info(input_path)
    if duration > 60.0:
        raise RuntimeError(f"音频时长 {duration:.1f}s 超过 60 秒限制")

    audio_data = input_path.read_bytes()
    if not audio_data:
        raise RuntimeError("WAV 文件为空")

    bytes_per_second = max(1, channels * (bits // 8) * sample_rate)
    segment_size = max(3200, min(int(bytes_per_second * 10), 1_000_000))

    reqid = str(uuid.uuid4())
    request_params = {
        "app": {
            "appid": app_id,
            "token": access_token,
            "cluster": cluster,
        },
        "user": {"uid": uid},
        "audio": {
            "format": "wav",
            "codec": "raw",
            "rate": sample_rate,
            "bits": bits,
            "channel": channels,
            "language": "zh-CN",
        },
        "request": {
            "reqid": reqid,
            "nbest": 1,
            "workflow": workflow,
            "show_utterances": True,
            "result_type": "full",
            "sequence": 1,
        },
    }

    full_payload = gzip.compress(json.dumps(request_params, ensure_ascii=False).encode("utf-8"))
    full_request = bytearray(generate_header(CLIENT_FULL_REQUEST))
    full_request.extend(len(full_payload).to_bytes(4, "big"))
    full_request.extend(full_payload)

    headers = {"Authorization": f"Bearer; {access_token}"}
    text = ""
    request_id = reqid
    log_id = ""

    websocket = await connect_websocket(endpoint, headers)
    try:
        await websocket.send(bytes(full_request))
        response = parse_response(await asyncio.wait_for(websocket.recv(), timeout=30))
        payload = ensure_success(response)
        text = collect_text(payload) or text
        if payload:
            request_id = str(payload.get("reqid") or request_id)
            addition = payload.get("addition") or {}
            if isinstance(addition, dict) and addition.get("logid"):
                log_id = str(addition["logid"])

        packet_index = 0
        async for chunk, is_last in slice_data(audio_data, segment_size):
            packet_index += 1
            compressed = gzip.compress(chunk)
            flags = NEG_SEQUENCE if is_last else NO_SEQUENCE
            audio_request = bytearray(
                generate_header(
                    CLIENT_AUDIO_ONLY_REQUEST,
                    message_type_specific_flags=flags,
                    serial_method=NO_SERIALIZATION,
                    compression_type=GZIP_COMPRESSION,
                )
            )
            audio_request.extend(len(compressed).to_bytes(4, "big"))
            audio_request.extend(compressed)

            await websocket.send(bytes(audio_request))
            response = parse_response(await asyncio.wait_for(websocket.recv(), timeout=60))
            payload = ensure_success(response)
            chunk_text = collect_text(payload)
            if chunk_text:
                text = chunk_text
            if payload:
                request_id = str(payload.get("reqid") or request_id)
                addition = payload.get("addition") or {}
                if isinstance(addition, dict) and addition.get("logid"):
                    log_id = str(addition["logid"])

            # The final audio packet normally carries the complete text.  Keep
            # going until the server has acknowledged every chunk.
            if not is_last:
                continue
    finally:
        try:
            await websocket.close()
        except Exception:
            pass

    final_text = text.strip()
    if not final_text:
        raise RuntimeError("ASR 没有识别出文本")

    return {
        "text": final_text,
        "request_id": request_id,
        "log_id": log_id,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Volcengine one-shot ASR bridge")
    parser.add_argument("--input", type=Path, required=True, help="16k mono WAV file")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    input_path = args.input.resolve()
    if not input_path.exists():
        print(f"错误：输入文件不存在：{input_path}", file=sys.stderr)
        return 2

    try:
        result = asyncio.run(recognize(input_path))
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1

    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
