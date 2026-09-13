#!/usr/bin/env python3
"""
把 LRC-like 口播稿中过长的 cue 按标点拆成更短的 cue。

原则：
- 目标长度由 --max-chars 控制（默认 40 中文字，约 8 秒）。
- 优先在 。！？； 处拆；仍过长再在 ，、 处拆；最后才按字符硬切。
- 不改变文本内容，只增加 cue 数量。
- 被拆开的 cue 使用 {原id}.1 / {原id}.2 ... 作为新 id。
- 重新估算 timeline 方便试听；真正 TTS 后仍应以音频时长为准。

用法：
    python scripts/split_lrc_long_cues.py \
      --input games/splendor/tutorial/full.lrc \
      --output games/splendor/tutorial/full.short.lrc \
      --max-chars 40
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import Any

TIME_RE = re.compile(r"^\[(\d{2}):(\d{2}\.\d{2})\]")
TAG_RE = re.compile(r"^\[([A-Za-z_]+):([^\]]*)\]")
GROUP_RE = re.compile(r"^\[group:(.*)\]$")


def format_time(seconds: float) -> str:
    total_cs = int(round(max(0.0, seconds) * 100))
    return f"[{total_cs // 6000:02d}:{(total_cs % 6000) / 100:05.2f}]"


def count_chars(text: str) -> int:
    return len(re.findall(r"[\u4e00-\u9fffA-Za-z0-9]", text))


def split_sentences(text: str) -> list[str]:
    parts = re.findall(r"[^。！？；]+[。！？；]?", text)
    return [p for p in parts if p]


def split_commas(text: str) -> list[str]:
    parts = re.findall(r"[^，、]+[，、]?", text)
    return [p for p in parts if p]


def hard_split(text: str, max_chars: int) -> list[str]:
    return [text[i:i + max_chars] for i in range(0, len(text), max_chars)]


def split_one_text(text: str, max_chars: int) -> list[str]:
    if len(text) <= max_chars:
        return [text]

    pieces: list[str] = []
    for sentence in split_sentences(text):
        if len(sentence) <= max_chars:
            pieces.append(sentence)
            continue
        for clause in split_commas(sentence):
            if len(clause) <= max_chars:
                pieces.append(clause)
            else:
                pieces.extend(hard_split(clause, max_chars))

    # 贪心合并，避免产生一堆过短碎句。
    chunks: list[str] = []
    current = ""
    for piece in pieces:
        if current and len(current) + len(piece) > max_chars:
            chunks.append(current)
            current = piece
        else:
            current += piece
    if current:
        chunks.append(current)
    return chunks


def parse_entries(text: str) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    for line in text.splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        group = GROUP_RE.match(line)
        if group:
            entries.append({"kind": "group", "text": group.group(1)})
            continue
        time_match = TIME_RE.match(line)
        if not time_match:
            tag = TAG_RE.match(line)
            if tag:
                entries.append({"kind": "meta", "key": tag.group(1), "value": tag.group(2).strip()})
                continue
            entries.append({"kind": "ignored", "raw": line})
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
        refs = [t[4:] for t in tags if t.startswith("ref:")]
        entries.append({"kind": "cue", "id": cue_id, "refs": refs, "text": rest})
    return entries


def rebuild(entries: list[dict[str, Any]], max_chars: int, chars_per_sec: float) -> str:
    # 先处理 cue 拆分。
    processed: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    for entry in entries:
        if entry["kind"] != "cue":
            processed.append(entry)
            continue
        chunks = split_one_text(entry["text"], max_chars)
        for i, chunk in enumerate(chunks, 1):
            cue_id = entry["id"] if len(chunks) == 1 else f"{entry['id']}.{i}"
            if cue_id in seen_ids:
                raise SystemExit(f"duplicate cue id after split: {cue_id}")
            seen_ids.add(cue_id)
            processed.append({"kind": "cue", "id": cue_id, "refs": entry["refs"], "text": chunk})

    # 重建 header/body。保留原元数据顺序，并替换 timing/length。
    header_keys = {"ti", "game", "track", "version", "generator"}
    out_lines: list[str] = []
    body_entries: list[dict[str, Any]] = []
    for entry in processed:
        if entry["kind"] == "meta":
            key = entry["key"]
            if key == "timing":
                continue
            if key == "length":
                continue
            if key in header_keys or key not in {"timing", "length"}:
                out_lines.append(f"[{key}:{entry['value']}]")
            continue
        body_entries.append(entry)

    # header 必须在最前面；上面的循环按原顺序处理 meta，但可能把 body 也放进了 out_lines。
    # 重新组织：第一遍只收集 meta 到 header，body 单独按顺序保留 group/cue。
    header = [ln for ln in out_lines if TAG_RE.match(ln)]
    body: list[dict[str, Any]] = []
    for entry in processed:
        if entry["kind"] == "meta":
            continue
        body.append(entry)
    header.append("[timing:estimated]")

    lines = header[:]
    cursor = 0.0
    last_group = None

    for entry in body:
        if entry["kind"] == "group":
            if last_group is not None:
                cursor += 0.5
            lines.append(f"[group:{entry['text']}]")
            last_group = entry["text"]
            continue
        if entry["kind"] != "cue":
            continue
        refs = "".join(f"[ref:{r}]" for r in entry["refs"])
        lines.append(f"{format_time(cursor)}[id:{entry['id']}]{refs}{entry['text']}")
        cursor += max(1.0, count_chars(entry["text"]) / chars_per_sec) + 0.2

    lines.append(f"[length:{format_time(cursor)[1:-1]}]")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Split long LRC cues.")
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-chars", type=int, default=40)
    parser.add_argument("--chars-per-sec", type=float, default=5.0)
    args = parser.parse_args()

    if not args.input.exists():
        print(f"error: input not found: {args.input}", file=sys.stderr)
        return 2

    entries = parse_entries(args.input.read_text(encoding="utf-8"))
    output = rebuild(entries, args.max_chars, args.chars_per_sec)
    args.output.write_text(output, encoding="utf-8")
    print(f"wrote {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
