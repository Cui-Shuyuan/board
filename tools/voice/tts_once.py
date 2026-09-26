#!/usr/bin/env python3
"""
One-shot Volcengine/Doubao TTS bridge.

This deliberately reuses the existing tutorial TTS implementation in
animation/tts_doubao.py (same WebSocket v3 protocol, same X-Api-Key auth and
same request payload).  It does not touch the tutorial TTS compile pipeline.

Environment variables (normally loaded from repo-root .env):
    VOLCENGINE_API_KEY      required
    DOUBAO_SPEAKER          optional default
    DOUBAO_RESOURCE_ID      optional default

Usage:
    python tools/voice/tts_once.py \
      --text "这是一次语音测试" \
      --out-file /tmp/test.mp3 \
      --voice zh_female_vv_uranus_bigtts \
      --speed 1.0

Stdout on success:
    {"file":"/tmp/test.mp3","duration":3.21}
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import uuid
from pathlib import Path
from types import SimpleNamespace

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
ANIMATION_DIR = REPO_ROOT / "animation"
if str(ANIMATION_DIR) not in sys.path:
    sys.path.insert(0, str(ANIMATION_DIR))

try:
    from tts_doubao import (  # noqa: E402
        DEFAULT_BIT_RATE,
        DEFAULT_ENDPOINT,
        DEFAULT_RESOURCE_ID,
        DEFAULT_SAMPLE_RATE,
        DEFAULT_VOICE,
        connect_websocket,
        load_dotenv,
        synthesize_cue,
    )
    from volcengine_ws_protocols import (  # noqa: E402
        EventType,
        MsgType,
        finish_connection,
        start_connection,
        wait_for_event,
    )
except (ImportError, SystemExit) as exc:  # pragma: no cover - dependency check
    print(f"错误：无法加载 animation/tts_doubao.py：{exc}", file=sys.stderr)
    raise SystemExit(2) from exc


def parse_args() -> argparse.Namespace:
    load_dotenv(REPO_ROOT / ".env")

    parser = argparse.ArgumentParser(description="Volcengine one-shot TTS bridge")
    text_group = parser.add_mutually_exclusive_group(required=True)
    text_group.add_argument("--text", help="synthesis text")
    text_group.add_argument("--text-file", type=Path, help="UTF-8 file containing synthesis text")
    parser.add_argument("--out-file", type=Path, required=True, help="output mp3 path")
    parser.add_argument(
        "--voice",
        default=os.environ.get("DOUBAO_SPEAKER", DEFAULT_VOICE),
        help="Doubao speaker id",
    )
    parser.add_argument("--resource-id", default=os.environ.get("DOUBAO_RESOURCE_ID", DEFAULT_RESOURCE_ID))
    parser.add_argument("--speed", type=float, default=1.0, help="0.5..2.0, mapped to TTS speech_rate")
    return parser.parse_args()


def load_text(args: argparse.Namespace) -> str:
    if args.text is not None:
        text = args.text
    else:
        text_path = args.text_file.resolve()
        if not text_path.exists():
            raise RuntimeError(f"text file not found: {text_path}")
        text = text_path.read_text(encoding="utf-8")

    text = text.strip()
    if not text:
        raise RuntimeError("TTS text is empty")
    if len(text) > 500:
        raise RuntimeError(f"TTS text exceeds 500 chars: {len(text)}")
    return text


def speed_to_speech_rate(speed: float) -> int:
    # Doubao's v3 request uses speech_rate (int).  The public API in this task
    # exposes a friendlier 1.0 = normal speed.  1.0 -> 0, 2.0 -> 100,
    # 0.5 -> -50; clamp to the documented safe range.
    return max(-50, min(100, int(round((speed - 1.0) * 100))))


async def synthesize(text: str, out_file: Path, voice: str, resource_id: str, speed: float) -> float:
    api_key = os.environ.get("VOLCENGINE_API_KEY", "").strip()
    if not api_key:
        raise RuntimeError("缺少 VOLCENGINE_API_KEY，请写入 .env 或设置为环境变量")

    if not (0.5 <= speed <= 2.0):
        raise RuntimeError("speed must be between 0.5 and 2.0")

    out_file = out_file.resolve()
    out_file.parent.mkdir(parents=True, exist_ok=True)
    subtitle_file = out_file.with_suffix(".subtitle.json")

    args = SimpleNamespace(
        format="mp3",
        sample_rate=DEFAULT_SAMPLE_RATE,
        bit_rate=DEFAULT_BIT_RATE,
        speech_rate=speed_to_speech_rate(speed),
        loudness_rate=0,
        voice=voice,
    )

    headers = {
        "X-Api-Key": api_key,
        "X-Api-Resource-Id": resource_id,
        "X-Api-Connect-Id": str(uuid.uuid4()),
    }

    websocket = await connect_websocket(DEFAULT_ENDPOINT, headers)
    try:
        await start_connection(websocket)
        await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionStarted)

        cue = {"id": "answer", "text": text}
        duration = await synthesize_cue(
            websocket,
            cue,
            out_file,
            subtitle_file,
            voice,
            args,
        )

        await finish_connection(websocket)
        try:
            await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionFinished)
        except Exception:
            # Some server versions close without waiting for ConnectionFinished.
            pass
        return float(duration)
    finally:
        try:
            await websocket.close()
        except Exception:
            pass
        try:
            if subtitle_file.exists():
                subtitle_file.unlink()
        except Exception:
            pass


def main() -> int:
    args = parse_args()
    try:
        text = load_text(args)
        out_file = args.out_file.resolve()
        duration = asyncio.run(
            synthesize(
                text=text,
                out_file=out_file,
                voice=args.voice,
                resource_id=args.resource_id,
                speed=args.speed,
            )
        )
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1

    print(json.dumps({"file": str(out_file), "duration": round(duration, 3)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
