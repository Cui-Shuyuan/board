#!/usr/bin/env python3
"""
LRC-like 口播稿 -> 豆包语音合成音频。

默认使用 **标准语音合成（小模型 WebSocket v1）**：
    wss://openspeech.bytedance.com/api/v1/tts/ws_binary

可通过 ``--provider seed2`` 或 ``DOUBAO_TTS_PROVIDER=seed2`` 切回旧的
豆包语音合成 2.0 双向流式实现。旧实现保留，便于以后对照或复用。

依赖：
    pip install websockets mutagen

用法：
    # 只解析和预览，不调用 API
    python animation/tts_doubao.py --input content/games/splendor/tutorial/full.lrc --dry-run

    # 标准语音合成前 3 条 cue 试听（默认 provider=standard）
    python animation/tts_doubao.py \
      --input content/games/splendor/tutorial/full.lrc \
      --out-dir content/games/splendor/media/tts/full \
      --limit 3

    # 显式切回旧语音合成 2.0
    python animation/tts_doubao.py \
      --provider seed2 \
      --input content/games/splendor/tutorial/full.lrc \
      --out-dir content/games/splendor/media/tts/full

环境变量（从仓库根目录 .env 读取，或设置到 shell）：
    VOLCENGINE_API_KEY              # 新版控制台共享 API Key（推荐）
    DOUBAO_TTS_PROVIDER             # standard（默认）/ seed2
    DOUBAO_TTS_VOICE                # 标准音色，默认 BV700_streaming
    DOUBAO_TTS_CLUSTER              # 标准 v1 默认 volcano_tts
    DOUBAO_TTS_ENDPOINT             # 可选，默认 v1 ws_binary
    DOUBAO_TTS_RESOURCE_ID          # 用量查询/清单用，默认 volc.tts.default
    DOUBAO_SPEAKER                  # seed2 provider 的旧默认音色
    DOUBAO_RESOURCE_ID              # seed2 provider 的旧 resource id
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
from types import SimpleNamespace
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
from tts_volcengine_standard import (  # noqa: E402
    DEFAULT_ENDPOINT as STANDARD_DEFAULT_ENDPOINT,
    DEFAULT_RESOURCE_ID as STANDARD_DEFAULT_RESOURCE_ID,
    DEFAULT_VOICE as STANDARD_DEFAULT_VOICE,
    is_standard_voice,
    resolve_voice,
    synthesize_all as standard_synthesize_all,
    synthesize_text as standard_synthesize_text,
)

PROVIDER_STANDARD = "standard"
PROVIDER_SEED2 = "seed2"
DEFAULT_PROVIDER = PROVIDER_STANDARD

# seed2 / v3 compatibility constants.  The names DEFAULT_* are kept for
# callers that still import them, but tts_once.py now uses the provider-aware
# high-level helper instead of hard-coded protocol defaults.
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


def resolve_provider(value: str | None = None) -> str:
    provider = (value or os.environ.get("DOUBAO_TTS_PROVIDER", "") or DEFAULT_PROVIDER).strip().lower()
    if provider in {PROVIDER_STANDARD, PROVIDER_SEED2}:
        return provider
    raise SystemExit(f"不支持的 DOUBAO_TTS_PROVIDER/--provider：{provider!r}（可选 standard/seed2）")


def apply_provider_defaults(args: argparse.Namespace) -> argparse.Namespace:
    """Fill provider-specific defaults after dotenv/CLI parsing."""
    provider = resolve_provider(getattr(args, "provider", None))
    args.provider = provider

    if provider == PROVIDER_STANDARD:
        args.voice = (getattr(args, "voice", None) or os.environ.get("DOUBAO_TTS_VOICE", "").strip() or STANDARD_DEFAULT_VOICE)
        args.resource_id = (
            getattr(args, "resource_id", None)
            or os.environ.get("DOUBAO_TTS_RESOURCE_ID", "").strip()
            or STANDARD_DEFAULT_RESOURCE_ID
        )
        args.endpoint = (getattr(args, "endpoint", None) or os.environ.get("DOUBAO_TTS_ENDPOINT", "").strip() or STANDARD_DEFAULT_ENDPOINT)
    else:
        args.voice = (getattr(args, "voice", None) or os.environ.get("DOUBAO_SPEAKER", "").strip() or DEFAULT_VOICE)
        args.resource_id = (getattr(args, "resource_id", None) or os.environ.get("DOUBAO_RESOURCE_ID", "").strip() or DEFAULT_RESOURCE_ID)
        args.endpoint = (getattr(args, "endpoint", None) or DEFAULT_ENDPOINT)

    if getattr(args, "root", None) is None:
        args.root = ROOT
    return args


def provider_subtitle_mode(provider: str) -> str:
    return "seed2-v3-events" if provider == PROVIDER_SEED2 else "unavailable-standard-v1"


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


async def _synthesize_all_seed2(cues: list[dict[str, Any]], args: argparse.Namespace) -> list[dict[str, Any]]:
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



async def synthesize_all(cues: list[dict[str, Any]], args: argparse.Namespace) -> list[dict[str, Any]]:
    """Provider-aware compile/CLI entry point."""
    provider = resolve_provider(getattr(args, "provider", None))
    args.provider = provider
    if provider == PROVIDER_STANDARD:
        return await standard_synthesize_all(cues, args)
    return await _synthesize_all_seed2(cues, args)


def speed_to_speech_rate(speed: float) -> int:
    """Map the friendly 1.0 = normal multiplier to seed2 speech_rate."""
    return max(-50, min(100, int(round((speed - 1.0) * 100))))


async def synthesize_once(
    text: str,
    out_file: Path,
    voice: str | None = None,
    resource_id: str | None = None,
    speed: float = 1.0,
    provider: str | None = None,
    endpoint: str | None = None,
    format: str = "mp3",
    sample_rate: int = DEFAULT_SAMPLE_RATE,
    bit_rate: int = DEFAULT_BIT_RATE,
    cluster: str | None = None,
) -> float:
    """One-text TTS used by ``tools/voice/tts_once.py``.

    Keeps stdout/JSON and mp3 output stable while switching the protocol
    implementation behind the provider flag.
    """
    provider = resolve_provider(provider)
    if not (0.5 <= float(speed) <= 2.0):
        raise RuntimeError("speed must be between 0.5 and 2.0")

    if provider == PROVIDER_STANDARD:
        args = SimpleNamespace(
            voice=voice or os.environ.get("DOUBAO_TTS_VOICE", "").strip(),
            endpoint=endpoint or os.environ.get("DOUBAO_TTS_ENDPOINT", "").strip(),
            format=format,
            sample_rate=sample_rate,
            speed_ratio=float(speed),
            volume_ratio=1.0,
            pitch_ratio=1.0,
            cluster=cluster,
            uid="board-qa",
            resource_id=resource_id,
        )
        return await standard_synthesize_text(text, out_file, args.voice, args)

    # ---- seed2 / v3 one-shot path (old behaviour kept) ----
    api_key = os.environ.get("VOLCENGINE_API_KEY", "").strip()
    if not api_key:
        raise RuntimeError("缺少 VOLCENGINE_API_KEY，请写入 .env 或设置为环境变量")
    resolved_endpoint = endpoint or DEFAULT_ENDPOINT
    resolved_resource_id = resource_id or os.environ.get("DOUBAO_RESOURCE_ID", DEFAULT_RESOURCE_ID)

    out_file = out_file.resolve()
    out_file.parent.mkdir(parents=True, exist_ok=True)
    subtitle_file = out_file.with_suffix(".subtitle.json")

    args = SimpleNamespace(
        format="mp3",
        sample_rate=DEFAULT_SAMPLE_RATE,
        bit_rate=bit_rate,
        speech_rate=speed_to_speech_rate(float(speed)),
        loudness_rate=0,
        voice=voice or os.environ.get("DOUBAO_SPEAKER", DEFAULT_VOICE),
    )

    headers = {
        "X-Api-Key": api_key,
        "X-Api-Resource-Id": resolved_resource_id,
        "X-Api-Connect-Id": str(uuid.uuid4()),
    }

    websocket = await connect_websocket(resolved_endpoint, headers)
    try:
        await start_connection(websocket)
        await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionStarted)

        cue = {"id": "answer", "text": text}
        duration = await synthesize_cue(
            websocket,
            cue,
            out_file,
            subtitle_file,
            args.voice,
            args,
        )

        await finish_connection(websocket)
        try:
            await wait_for_event(websocket, MsgType.FullServerResponse, EventType.ConnectionFinished)
        except Exception:
            # Some server versions close without sending ConnectionFinished.
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

    provider = resolve_provider(getattr(args, "provider", None))
    manifest = {
        "track": args.track,
        "provider": provider,
        "voice": args.voice,
        "resource_id": args.resource_id,
        "format": args.format,
        "sample_rate": args.sample_rate,
        "gap_seconds": args.gap,
        "subtitle_timing": provider_subtitle_mode(provider),
        "generator": "animation/tts_doubao.py",
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
                out.append("[generator:animation/tts_doubao.py]")
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
    provider = resolve_provider(getattr(args, "provider", None))
    display_voice = resolve_voice(args.voice) if provider == PROVIDER_STANDARD else args.voice
    print(f"provider: {provider}")
    print(f"cues: {len(cues)}")
    print(f"total chars: {total_chars}")
    print(f"voice: {display_voice}")
    print(f"resource_id: {args.resource_id}")
    print(f"endpoint: {args.endpoint}")
    print(f"out_dir: {args.out_dir}")
    print("---")
    for cue in cues:
        print(f"{cue['id']}  {len(cue['text']):>3} 字  {cue['text'][:40]}")


def parse_args() -> argparse.Namespace:
    load_dotenv(ROOT / ".env")
    parser = argparse.ArgumentParser(description="LRC-like 口播稿 -> 豆包语音合成音频（默认标准小模型）")
    parser.add_argument("--input", type=Path, default=ROOT / "content/games/splendor/tutorial/full.lrc")
    parser.add_argument("--out-dir", type=Path, default=ROOT / "content/games/splendor/media/tts/full")
    parser.add_argument("--provider", default=None, choices=[PROVIDER_STANDARD, PROVIDER_SEED2], help="standard（默认）/ seed2（旧语音合成 2.0）")
    parser.add_argument("--voice", default=None, help="音色；标准默认 BV700_streaming")
    parser.add_argument("--resource-id", default=None, help="resource id；标准 v1 仅记录清单，不发送该 header")
    parser.add_argument("--cluster", default=None, help="标准 v1 app.cluster，默认 volcano_tts")
    parser.add_argument("--track", default="full")
    parser.add_argument("--endpoint", default=None)
    parser.add_argument("--format", default="mp3", choices=["mp3", "wav", "pcm", "ogg_opus"])
    parser.add_argument("--sample-rate", type=int, default=DEFAULT_SAMPLE_RATE)
    parser.add_argument("--bit-rate", type=int, default=DEFAULT_BIT_RATE)
    parser.add_argument("--speech-rate", type=int, default=DEFAULT_SPEECH_RATE, help="seed2 兼容；standard 下建议改用 --speed-ratio")
    parser.add_argument("--speed-ratio", type=float, default=None, help="标准语音合成语速倍数，0.2..3.0")
    parser.add_argument("--volume-ratio", type=float, default=None, help="标准语音合成音量倍数，0.1..3.0")
    parser.add_argument("--pitch-ratio", type=float, default=None, help="标准语音合成音高倍数，0.1..3.0")
    parser.add_argument("--loudness-rate", type=int, default=DEFAULT_LOUDNESS_RATE)
    parser.add_argument("--gap", type=float, default=DEFAULT_GAP_SECONDS, help="cue 之间的额外停顿秒数")
    parser.add_argument("--limit", type=int, default=None, help="只处理前 N 条 cue")
    parser.add_argument("--overwrite", action="store_true", help="覆盖已存在的音频")
    parser.add_argument("--force", action="store_true", help="忽略已有音频，全部重新合成")
    parser.add_argument("--prune", action="store_true", help="删除 source LRC 中已不存在的旧音频和字幕")
    parser.add_argument("--usage", action="store_true", help="seed2 请求返回计费用量")
    parser.add_argument("--dry-run", action="store_true", help="只打印计划，不调用 API")
    parser.add_argument("--write-lrc", action="store_true", help="合成后生成 full.tts.lrc")
    return parser.parse_args()


def main() -> int:
    args = apply_provider_defaults(parse_args())
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

    try:
        results = asyncio.run(synthesize_all(cues, args))
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    if args.prune:
        prune_stale(args.out_dir, cues, args.format)
    write_manifest(args.out_dir, cues, results, args)

    if args.write_lrc:
        output_lrc = args.input.with_name(args.input.stem + ".tts.lrc")
        write_tts_lrc(args.input, output_lrc, cues, results, args)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
