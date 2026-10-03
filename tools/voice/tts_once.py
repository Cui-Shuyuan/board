#!/usr/bin/env python3
"""
One-shot Volcengine/Doubao TTS bridge.

The protocol/provider choice lives in ``animation/tts_doubao.py``.  This
wrapper only owns the API-facing CLI contract:

* ``--text`` / ``--text-file``
* ``--out-file`` (mp3)
* ``--voice`` / ``--resource-id`` / ``--speed``
* stdout ``{"file": "...", "duration": 3.21}``

Default provider is ``standard`` (Doubao standard small-model TTS v1).
Set ``DOUBAO_TTS_PROVIDER=seed2`` or pass ``--provider seed2`` to use the
speech synthesis 2.0 path.

Environment variables (normally loaded from repo-root .env):
    VOLCENGINE_API_KEY      shared new-console API key (preferred)
    DOUBAO_TTS_PROVIDER     standard (default) / seed2
    DOUBAO_TTS_VOICE        standard voice, default BV700_streaming
    DOUBAO_TTS_CLUSTER      standard v1 app.cluster, default volcano_tts
    DOUBAO_TTS_ENDPOINT     optional standard endpoint override
    DOUBAO_SPEAKER          seed2 voice
    DOUBAO_RESOURCE_ID      seed2 resource id
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
ANIMATION_DIR = REPO_ROOT / "animation"
if str(ANIMATION_DIR) not in sys.path:
    sys.path.insert(0, str(ANIMATION_DIR))

try:
    from tts_doubao import (  # noqa: E402
        DEFAULT_PROVIDER,
        PROVIDER_SEED2,
        PROVIDER_STANDARD,
        load_dotenv,
        resolve_provider,
        synthesize_once,
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
    parser.add_argument("--voice", default=None, help="Doubao speaker/voice_type")
    parser.add_argument("--resource-id", default=None, help="seed2 resource id; standard records only")
    parser.add_argument("--speed", type=float, default=1.0, help="0.5..2.0, friendly speed multiplier")
    parser.add_argument(
        "--provider",
        default=os.environ.get("DOUBAO_TTS_PROVIDER", DEFAULT_PROVIDER),
        choices=[PROVIDER_STANDARD, PROVIDER_SEED2],
        help="standard (default) or seed2 (speech synthesis 2.0)",
    )
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


def main() -> int:
    args = parse_args()
    try:
        provider = resolve_provider(args.provider)
        text = load_text(args)
        out_file = args.out_file.resolve()
        duration = asyncio.run(
            synthesize_once(
                text=text,
                out_file=out_file,
                voice=args.voice,
                resource_id=args.resource_id,
                speed=args.speed,
                provider=provider,
            )
        )
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 1

    print(json.dumps({"file": str(out_file), "duration": round(float(duration), 3)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
