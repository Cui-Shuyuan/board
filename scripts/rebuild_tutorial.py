#!/usr/bin/env python3
"""
一条命令跑完讲规口播制作链。

默认流程：
    tutorial_script_tool build            # source JSON -> estimated LRC
    tts_doubao --force --prune --write-lrc # LRC -> mp3 + subtitle + full.tts.lrc
    build_tutorial_runtime --force         # TTS 产物 -> full.runtime.json
    validate_timed_script                  # 校验 full.tts.lrc

用法：
    python scripts/rebuild_tutorial.py --game splendor --track full
    python scripts/rebuild_tutorial.py --game splendor --track full --skip-tts
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def run(cmd: list[str]) -> None:
    print("+ " + " ".join(cmd))
    subprocess.run(cmd, cwd=ROOT, check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Rebuild tutorial LRC/TTS/runtime pipeline.")
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--skip-tts", action="store_true", help="只从 source JSON 生成 estimated LRC，不重跑 TTS")
    args = parser.parse_args()

    game = args.game
    track = args.track
    lrc = ROOT / "games" / game / "tutorial" / f"{track}.lrc"
    tts_lrc = ROOT / "games" / game / "tutorial" / f"{track}.tts.lrc"
    out_dir = ROOT / "games" / game / "media" / "tts" / track

    run([sys.executable, "scripts/tutorial_script_tool.py", "build", "--game", game, "--track", track])
    run([sys.executable, "scripts/validate_timed_script.py", "--file", str(lrc)])

    if args.skip_tts:
        print("[skip] TTS/runtime rebuild skipped")
        return 0

    run([
        sys.executable,
        "scripts/tts_doubao.py",
        "--input", str(lrc),
        "--out-dir", str(out_dir),
        "--write-lrc",
        "--force",
        "--prune",
    ])
    run([sys.executable, "scripts/build_tutorial_runtime.py", "--game", game, "--track", track, "--force"])
    run([sys.executable, "scripts/validate_timed_script.py", "--file", str(tts_lrc)])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
