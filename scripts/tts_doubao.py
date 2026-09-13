#!/usr/bin/env python3
"""
LRC-like 口播稿 -> 豆包语音合成 2.0（WebSocket 双向流式接口）音频。

依赖：
    pip install websockets mutagen

用法：
    # 只解析和预览，不调用 API
    python scripts/tts_doubao.py --input games/splendor/tutorial/full.lrc --dry-run

    # 先合成前 3 条 cue 试听
    python scripts/tts_doubao.py \
      --input games/splendor/tutorial/full.lrc \
      --out-dir games/splendor/media/tts/full \
      --voice zh_female_vv_uranus_bigtts \
      --limit 3

    # 全量合成，并生成 full.tts.lrc + tts_manifest.json
    python scripts/tts_doubao.py \
      --input games/splendor/tutorial/full.lrc \
      --out-dir games/splendor/media/tts/full \
      --voice zh_female_vv_uranus_bigtts

环境变量（从仓库根目录 .env 读取，或设置到 shell）：
    VOLCENGINE_API_KEY
    DOUBAO_SPEAKER          # 可选，默认 zh_female_vv_uranus_bigtts
    DOUBAO_RESOURCE_ID      # 可选，默认 seed-tts-2.0
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import sys
import uuid
import wave
from pathlib import Path
from typing import Any

try:
    import websockets
except ImportError as exc:  # pragma: no cover - 环境未安装时给提示
    raise SystemExit("缺少 websockets，请先执行：pip install websockets") from exc

try:
    from mutagen.mp3 import MP3
except ImportError:  # pragma: no cover
    MP3 = None

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from validate_timed_script import parse_file  # noqa: E402
from volcengine_ws_protocols import (  # noqa: E402
    EventType,
    MsgType,
    finish_connection,
    finish_session,
    receive_message,
    start_connection,
    start_session,
    task_request,
    wait_for_event,
)

DEFAULT_ENDPOINT = "wss://openspeech.bytedance.com/api/v3/tts/bidirection"
DEFAULT_VOICE = "zh_female_vv_uranus_bigtts"
DEFAULT_RESOURCE_ID = "seed-tts-2.0"
DEFAULT_SAMPLE_RATE = 24000
DEFAULT_BIT_RATE = 160000
DEFAULT_SPEECH_RATE = 0
DEFAULT_LOUDNESS_RATE = 0
DEFAULT_GAP_SECONDS = 0.0


def load_dotenv(path: Path) -> None:
    """极简 .env 读取，不覆盖已有环境变量。"""
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


def format_time(seconds: float) -> str:
    total_cs = int(round(max(0.0, seconds) * 100))
    return f"[{total_cs // 6000:02d}:{(total_cs % 6000) / 100:05.2f}]"


def normalize_cue_refs(refs: list[str]) -> str:
    return "".join(f"[ref:{ref}]" for ref in refs)


def load_cues(lrc_path: Path, limit: int | None = None) -> list[dict[str, Any]]:
    doc = parse_file(lrc_path)
    errors = [i for i in doc["issues"] if i["level"] == "error"]
    if errors:
        raise SystemExit(f"LRC 校验失败：{errors}")
    cues = doc["cues"]
    if limit is not None:
        cues = cues[:limit]
    return cues


def build_base_request(voice: str, args: argparse.Namespace) -> dict[str, Any]:
    additions = {
        "explicit_language": "zh-cn",
        "disable_markdown_filter": True,
        # 官方文档：enable_subtitle 开启后返回字级别时间戳，仅中英文支持。
        "enable_subtitle": True,
    }
    return {
        "user": {"uid": "board-tutorial"},
        "namespace": "BidirectionalTTS",
        "req_params": {
            "speaker": voice,
            "audio_params": {
                "format": args.format,
                "sample_rate": args.sample_rate,
                "bit_rate": args.bit_rate,
                "speech_rate": args.speech_rate,
                "loudness_rate": args.loudness_rate,
                "enable_subtitle": True,
            },
            "additions": json.dumps(additions, ensure_ascii=False),
        },
    }


def build_start_session_payload(voice: str, args: argparse.Namespace) -> bytes:
    payload = build_base_request(voice, args)
    payload["event"] = int(EventType.StartSession)
    return json.dumps(payload, ensure_ascii=False).encode("utf-8")


def build_task_request_payload(cue_text: str, voice: str, args: argparse.Namespace) -> bytes:
    payload = build_base_request(voice, args)
    payload["event"] = int(EventType.TaskRequest)
    payload["req_params"]["text"] = cue_text
    return json.dumps(payload, ensure_ascii=False).encode("utf-8")


def audio_duration(path: Path, audio_format: str, sample_rate: int) -> float:
    if audio_format == "mp3":
        if MP3 is None:
            raise SystemExit("缺少 mutagen，请先执行：pip install mutagen")
        return float(MP3(str(path)).info.length)
    if audio_format == "wav":
        with wave.open(str(path), "rb") as wav:
            return wav.getnframes() / float(wav.getframerate())
    if audio_format == "pcm":
        # 双向流式接口的 pcm 默认是 16-bit 单声道。
        return path.stat().st_size / float(sample_rate * 2)
    raise SystemExit(f"暂不支持计算 {audio_format} 的时长，请使用 mp3/wav/pcm")


def write_subtitle_file(path: Path, subtitle_events: list[dict[str, Any]]) -> None:
    path.write_text(
        json.dumps(subtitle_events, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


async def connect_websocket(url: str, headers: dict[str, str]):
    """兼容 websockets 新旧版本的 additional_headers / extra_headers。"""
    kwargs = {"max_size": 10 * 1024 * 1024}
    try:
        return await websockets.connect(url, additional_headers=headers, **kwargs)
    except TypeError:
        return await websockets.connect(url, extra_headers=headers, **kwargs)


async def synthesize_cue(
    websocket,
    cue: dict[str, Any],
    audio_path: Path,
    subtitle_path: Path,
    voice: str,
    args: argparse.Namespace,
) -> float:
    session_id = str(uuid.uuid4())
    await start_session(websocket, build_start_session_payload(voice, args), session_id)
    await wait_for_event(websocket, MsgType.FullServerResponse, EventType.SessionStarted)

    await task_request(websocket, build_task_request_payload(cue["text"], voice, args), session_id)
    await finish_session(websocket, session_id)

    audio_data = bytearray()
    subtitle_events: list[dict[str, Any]] = []

    while True:
        msg = await receive_message(websocket)

        if msg.type == MsgType.AudioOnlyServer:
            audio_data.extend(msg.payload)
            continue

        if msg.type == MsgType.FullServerResponse:
            if msg.event == EventType.SessionFinished:
                break
            if msg.event in (EventType.SessionFailed, EventType.ConnectionFailed):
                raise RuntimeError(f"会话失败：{msg}")
            if msg.event == EventType.TTSSubtitle:
                try:
                    subtitle_events.append(json.loads(msg.payload.decode("utf-8")))
                except Exception:
                    subtitle_events.append({"raw": msg.payload.decode("utf-8", "ignore")})
            continue

        if msg.type == MsgType.Error:
            raise RuntimeError(f"合成错误：{msg}")

        # 其他事件忽略，例如 TTSSentenceStart / TTSSentenceEnd / UsageResponse。
        continue

    if not audio_data:
        raise RuntimeError(f"cue {cue['id']} 没有返回音频数据")

    audio_path.write_bytes(audio_data)
    if subtitle_events:
        write_subtitle_file(subtitle_path, subtitle_events)

    return audio_duration(audio_path, args.format, args.sample_rate)


async def synthesize_all(cues: list[dict[str, Any]], args: argparse.Namespace) -> list[dict[str, Any]]:
    api_key = os.environ.get("VOLCENGINE_API_KEY", "").strip()
    if not api_key:
        raise SystemExit("缺少 VOLCENGINE_API_KEY，请写入 .env 或设置为 shell 环境变量")

    voice = args.voice or os.environ.get("DOUBAO_SPEAKER", DEFAULT_VOICE)
    resource_id = args.resource_id or os.environ.get("DOUBAO_RESOURCE_ID", DEFAULT_RESOURCE_ID)

    headers = {
        "X-Api-Key": api_key,
        "X-Api-Resource-Id": resource_id,
        "X-Api-Connect-Id": str(uuid.uuid4()),
    }
    if args.usage:
        headers["X-Control-Require-Usage-Tokens-Return"] = "*"

    out_dir = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    results: list[dict[str, Any]] = []
    websocket = await connect_websocket(args.endpoint, headers)
    try:
        await start_connection(websocket)
        await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionStarted)

        for cue in cues:
            audio_path = out_dir / f"{cue['id']}.{args.format}"
            subtitle_path = out_dir / f"{cue['id']}.subtitle.json"

            if audio_path.exists() and not args.overwrite and not args.force:
                duration = audio_duration(audio_path, args.format, args.sample_rate)
                print(f"[skip] {cue['id']} 已存在")
            else:
                print(f"[tts] {cue['id']} ({len(cue['text'])} 字) -> {audio_path.name}")
                duration = await synthesize_cue(websocket, cue, audio_path, subtitle_path, voice, args)
                print(f"      duration={duration:.3f}s")

            results.append({
                "id": cue["id"],
                "text_length": len(cue["text"]),
                "file": str(audio_path.relative_to(ROOT)),
                "subtitle_file": str(subtitle_path.relative_to(ROOT)) if subtitle_path.exists() else None,
                "duration": duration,
            })

        await finish_connection(websocket)
        try:
            await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionFinished)
        except Exception:
            # 有些服务端在 close 时不等待 ConnectionFinished；连接关闭即可。
            pass
    finally:
        try:
            await websocket.close()
        except Exception:
            pass

    return results


def prune_stale(out_dir: Path, cues: list[dict[str, Any]], audio_format: str) -> None:
    expected = {cue["id"] for cue in cues}
    removed = 0
    for path in out_dir.iterdir():
        if not path.is_file():
            continue
        if path.name == "tts_manifest.json":
            continue
        cue_id = None
        if path.name.endswith(".subtitle.json"):
            cue_id = path.name[: -len(".subtitle.json")]
        elif path.name.endswith(f".{audio_format}"):
            cue_id = path.name[: -len(f".{audio_format}")]
        if cue_id and cue_id not in expected:
            path.unlink()
            removed += 1
    if removed:
        print(f"[prune] removed {removed} stale asset(s)")


def write_manifest(out_dir: Path, cues: list[dict[str, Any]], results: list[dict[str, Any]], args: argparse.Namespace) -> None:
    by_id = {r["id"]: r for r in results}
    cursor = 0.0
    manifest_cues = []
    for cue in cues:
        r = by_id.get(cue["id"])
        if not r:
            continue
        start = cursor
        cursor += float(r["duration"]) + args.gap
        manifest_cues.append({
            "id": cue["id"],
            "group": cue["group"],
            "start": round(start, 3),
            "duration": round(float(r["duration"]), 3),
            "file": r["file"],
            "subtitle_file": r["subtitle_file"],
            "refs": cue["refs"],
        })

    manifest = {
        "track": args.track,
        "voice": args.voice,
        "resource_id": args.resource_id,
        "format": args.format,
        "sample_rate": args.sample_rate,
        "gap_seconds": args.gap,
        "generator": "scripts/tts_doubao.py",
        "cues": manifest_cues,
    }
    (out_dir / "tts_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"[manifest] {out_dir / 'tts_manifest.json'}")


def write_tts_lrc(input_lrc: Path, output_lrc: Path, cues: list[dict[str, Any]], results: list[dict[str, Any]], args: argparse.Namespace) -> None:
    """基于原始 LRC 重写时间，保留原有 group 行和 ref 标签。"""
    time_re = re.compile(r"^\[(\d{2}):(\d{2}\.\d{2})\]")
    meta_re = re.compile(r"^\[([A-Za-z_]+):([^\]]*)\]")
    by_id = {r["id"]: r for r in results}
    cursor = 0.0
    out: list[str] = []
    timing_written = False

    for raw in input_lrc.read_text(encoding="utf-8").splitlines():
        line = raw.rstrip()
        if not line:
            continue
        if line.startswith("[group:"):
            out.append(line)
            continue

        meta = meta_re.match(line)
        if meta and not time_re.match(line):
            key = meta.group(1)
            if key == "timing":
                continue
            if key == "length":
                continue
            if key == "generator":
                continue
            out.append(line)
            if key == "track":
                out.append("[timing:tts]")
                out.append("[generator:scripts/tts_doubao.py]")
                timing_written = True
            continue

        time_match = time_re.match(line)
        if not time_match:
            out.append(line)
            continue

        rest = line[time_match.end():]
        tags: list[str] = []
        while rest.startswith("["):
            end = rest.find("]")
            if end < 0:
                break
            tag = rest[1:end]
            if tag.startswith("id:") or tag.startswith("ref:"):
                tags.append(tag)
                rest = rest[end + 1:]
                continue
            break

        cue_id = next((t[3:] for t in tags if t.startswith("id:")), "")
        result = by_id.get(cue_id)
        if result is None:
            # limit 模式下缺失的 cue 不写入输出。
            continue

        tag_text = "".join(f"[{tag}]" for tag in tags)
        out.append(f"{format_time(cursor)}{tag_text}{rest}")
        cursor += float(result["duration"]) + args.gap

    if not timing_written:
        out.insert(0, "[timing:tts]")
    out.append(f"[length:{format_time(cursor)[1:-1]}]")
    output_lrc.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"[lrc] {output_lrc}")



def print_dry_run(cues: list[dict[str, Any]], args: argparse.Namespace) -> None:
    total_chars = sum(len(c["text"]) for c in cues)
    print(f"cues: {len(cues)}")
    print(f"total chars: {total_chars}")
    print(f"voice: {args.voice}")
    print(f"resource_id: {args.resource_id}")
    print(f"out_dir: {args.out_dir}")
    print("---")
    for cue in cues:
        print(f"{cue['id']}  {len(cue['text']):>3} 字  {cue['text'][:40]}")


def parse_args() -> argparse.Namespace:
    load_dotenv(ROOT / ".env")
    parser = argparse.ArgumentParser(description="LRC-like 口播稿 -> 豆包语音合成 2.0 音频")
    parser.add_argument("--input", type=Path, default=ROOT / "games/splendor/tutorial/full.lrc")
    parser.add_argument("--out-dir", type=Path, default=ROOT / "games/splendor/media/tts/full")
    parser.add_argument("--voice", default=os.environ.get("DOUBAO_SPEAKER", DEFAULT_VOICE))
    parser.add_argument("--resource-id", default=os.environ.get("DOUBAO_RESOURCE_ID", DEFAULT_RESOURCE_ID))
    parser.add_argument("--track", default="full")
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT)
    parser.add_argument("--format", default="mp3", choices=["mp3", "wav", "pcm", "ogg_opus"])
    parser.add_argument("--sample-rate", type=int, default=DEFAULT_SAMPLE_RATE)
    parser.add_argument("--bit-rate", type=int, default=DEFAULT_BIT_RATE)
    parser.add_argument("--speech-rate", type=int, default=DEFAULT_SPEECH_RATE)
    parser.add_argument("--loudness-rate", type=int, default=DEFAULT_LOUDNESS_RATE)
    parser.add_argument("--gap", type=float, default=DEFAULT_GAP_SECONDS, help="cue 之间的额外停顿秒数")
    parser.add_argument("--limit", type=int, default=None, help="只处理前 N 条 cue")
    parser.add_argument("--overwrite", action="store_true", help="覆盖已存在的音频")
    parser.add_argument("--force", action="store_true", help="忽略已有音频，全部重新合成")
    parser.add_argument("--prune", action="store_true", help="删除 source LRC 中已不存在的旧音频和字幕")
    parser.add_argument("--usage", action="store_true", help="请求返回计费用量")
    parser.add_argument("--dry-run", action="store_true", help="只打印计划，不调用 API")
    parser.add_argument("--write-lrc", action="store_true", help="合成后生成 full.tts.lrc")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    args.input = args.input.resolve()
    args.out_dir = args.out_dir.resolve()

    if not args.input.exists():
        print(f"error: input not found: {args.input}", file=sys.stderr)
        return 2

    cues = load_cues(args.input, args.limit)
    if not cues:
        print("error: no cues", file=sys.stderr)
        return 2
    if args.prune and args.limit is not None:
        print("error: --prune cannot be used together with --limit", file=sys.stderr)
        return 2

    if args.dry_run:
        print_dry_run(cues, args)
        return 0

    if sys.version_info < (3, 9):
        print("error: Python 3.9+ required", file=sys.stderr)
        return 2

    results = asyncio.run(synthesize_all(cues, args))
    if args.prune:
        prune_stale(args.out_dir, cues, args.format)
    write_manifest(args.out_dir, cues, results, args)

    if args.write_lrc:
        output_lrc = args.input.with_name(args.input.stem + ".tts.lrc")
        write_tts_lrc(args.input, output_lrc, cues, results, args)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
