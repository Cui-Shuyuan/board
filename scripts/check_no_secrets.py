#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""防再犯：扫**已跟踪文件**里有没有明文密钥（API key / token / 密码）。

为什么单独一条：`backend/BoardAI.Api/appsettings.json` 里那个 DeepSeek key
从 2026-07-23 起就在 git 里、还推到了 GitHub（用户发现后才知道）。
密钥一旦进过公开仓库，唯一正确的处置是**轮换**；所以这里的目标是**别再进第二次**。

用法：
    python3 scripts/check_no_secrets.py            # 0 = 干净；1 = 有疑似明文密钥
    python3 scripts/check_no_secrets.py --all      # 连未跟踪文件一起看（默认只看 git 跟踪的）
接法：跟 validate_* 一起跑；也可以挂成 .git/hooks/pre-commit（见 README）。
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 明文密钥的形状：sk-xxx / 长 token 赋值 / 常见密码键
PATTERNS = [
    (re.compile(r"\bsk-[A-Za-z0-9_-]{16,}"), "疑似 API key（sk-…）"),
    (re.compile(r"(?i)\b(api[_-]?key|secret|token|password|passwd)\b\s*[:=]\s*\"[A-Za-z0-9_\-\.]{16,}\""),
     "疑似明文密钥/口令赋值"),
]
# 允许出现的地方（示例、模板、占位）
ALLOW_LINE = re.compile(r"(?i)(your[-_]?api|example|placeholder|xxxx|<[^>]+>|\$\{|getenv|environment\.)")
SKIP_DIRS = (".git/", ".tools/", "node_modules/", ".claude/worktrees/")
SKIP_EXT = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp3", ".mp4", ".onnx", ".bin", ".index"}


def tracked_files(all_files: bool):
    if all_files:
        for p in ROOT.rglob("*"):
            if p.is_file() and p.suffix.lower() not in SKIP_EXT:
                rel = p.relative_to(ROOT).as_posix()
                if not any(s in rel + "/" for s in SKIP_DIRS):
                    yield rel
        return
    out = subprocess.run(["git", "-C", str(ROOT), "ls-files"], capture_output=True, text=True).stdout
    for rel in out.splitlines():
        if rel and Path(rel).suffix.lower() not in SKIP_EXT:
            yield rel


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true", help="连未跟踪文件一起扫")
    a = ap.parse_args()
    hits = []
    for rel in tracked_files(a.all):
        p = ROOT / rel
        try:
            text = p.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if ALLOW_LINE.search(line):
                continue
            for rx, why in PATTERNS:
                if rx.search(line):
                    hits.append((rel, i, why, line.strip()[:80]))
    if hits:
        print(f"✗ 发现 {len(hits)} 处疑似明文密钥（已跟踪文件里）：")
        for rel, i, why, snippet in hits:
            masked = re.sub(r"(sk-[A-Za-z0-9_-]{6})[A-Za-z0-9_-]+", r"\1……", snippet)
            print(f"    {rel}:{i}  {why}\n        {masked}")
        print("\n处置：把值挪到环境变量/user-secrets，仓库里留空或占位；**并轮换那个密钥**"
              "（进过公开仓库就等于已泄露）。")
        return 1
    print(f"✓ 没发现明文密钥（扫了 {'全部' if a.all else 'git 已跟踪'} 文件）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
