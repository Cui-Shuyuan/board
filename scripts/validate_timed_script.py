#!/usr/bin/env python3
"""
校验 LRC-like 口播稿（时间 + 台词）。

格式摘要：
    [ti:标题]
    [game:splendor]
    [track:full]
    [timing:estimated]
    [length:11:27.17]
    [group:3.1 从宝石供应堆拿取宝石]
    [00:00.00][id:section.001][ref:<concept>|flow:node]台词内容

- 时间只表示该播放单元的开始时间；结束时间为下一条时间或 [length]。
- [id:...] 在口播稿内必须唯一，用于打断上下文、动画引用和编译产物。
- [ref:...] 可选，支持 `<concept>`、`flow:<node>`、`rulebook:*`，多个用 | 分隔。
- [group:...] 不参与播放，只用于导航层级。
- timing=estimated 表示时间轴是 TTS 前的估算，TTS 冻结后应改为 timing=tts 并重写时间。

用法：
    python scripts/validate_timed_script.py --file games/splendor/tutorial/full.lrc
    python scripts/validate_timed_script.py --file games/splendor/tutorial/full.lrc --json
"""

import argparse
import json
import re
import sys
from pathlib import Path

TIME_RE = re.compile(r"^\[(\d{2}):(\d{2}\.\d{2})\]")
TAG_RE = re.compile(r"^\[([A-Za-z_]+):([^\]]*)\]")
GROUP_RE = re.compile(r"^\[group:(.*)\]$")
META_KEYS = {"ti", "game", "track", "timing", "version", "generator", "length"}


class Issue:
    def __init__(self, level, line, message):
        self.level = level
        self.line = line
        self.message = message

    def as_dict(self):
        return {"level": self.level, "line": self.line, "message": self.message}


def parse_time(mm, ss):
    return int(mm) * 60 + float(ss)


def format_time(seconds):
    total_cs = int(round(seconds * 100))
    return f"{total_cs // 6000:02d}:{(total_cs % 6000) / 100:05.2f}"


def parse_file(path: Path):
    meta = {}
    groups = []
    cues = []
    issues = []
    current_group = None
    current_section_type = None
    seen_ids = set()

    for line_no, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        group_match = GROUP_RE.match(line)
        if group_match:
            group = group_match.group(1).strip()
            if not group:
                issues.append(Issue("error", line_no, "empty group title"))
            else:
                groups.append(group)
                current_group = group
            continue

        time_match = TIME_RE.match(line)
        if not time_match:
            meta_match = TAG_RE.match(line)
            if meta_match:
                key, value = meta_match.group(1), meta_match.group(2).strip()
                if key not in META_KEYS:
                    issues.append(Issue("warning", line_no, f"unknown metadata tag: {key}"))
                meta[key] = value
                if key == "track":
                    current_section_type = value
                continue
            issues.append(Issue("error", line_no, f"unrecognized line: {line[:80]}"))
            continue

        start = parse_time(*time_match.groups())
        rest = line[time_match.end():]
        tags = {}
        while True:
            tag_match = TAG_RE.match(rest)
            if not tag_match:
                break
            key, value = tag_match.group(1), tag_match.group(2).strip()
            if key in ("id", "ref"):
                if key in tags:
                    issues.append(Issue("error", line_no, f"duplicate [{key}:] tag"))
                tags[key] = value
            else:
                issues.append(Issue("warning", line_no, f"unknown cue tag: {key}"))
            rest = rest[tag_match.end():]

        text = rest.strip()
        cue_id = tags.get("id", "")
        refs = [r for r in tags.get("ref", "").split("|") if r]

        if not cue_id:
            issues.append(Issue("error", line_no, "cue missing [id:...]"))
        elif cue_id in seen_ids:
            issues.append(Issue("error", line_no, f"duplicate cue id: {cue_id}"))
        else:
            seen_ids.add(cue_id)

        if not text:
            issues.append(Issue("error", line_no, "cue has empty text"))

        cue = {
            "line": line_no,
            "start": round(start, 2),
            "id": cue_id,
            "group": current_group,
            "track": current_section_type or meta.get("track"),
            "refs": refs,
            "text": text,
        }
        if cues and start < cues[-1]["start"]:
            issues.append(Issue("error", line_no, "timestamp is before previous cue"))
        cues.append(cue)

    if not cues:
        issues.append(Issue("error", 0, "no cues found"))
    if "ti" not in meta and cues:
        issues.append(Issue("warning", 0, "missing [ti:...] title metadata"))
    if "length" in meta:
        try:
            length = format_time(parse_time(*meta["length"].split(":")))
        except Exception as exc:
            issues.append(Issue("error", 0, f"invalid [length:...]: {exc}"))
        else:
            if cues and cues[-1]["start"] > parse_time(*meta["length"].split(":")):
                issues.append(Issue("error", 0, "last cue starts after [length:...]"))

    return {
        "file": str(path),
        "meta": meta,
        "groups": groups,
        "cues": cues,
        "issues": [issue.as_dict() for issue in issues],
    }


def main():
    parser = argparse.ArgumentParser(description="Validate an LRC-like timed script.")
    parser.add_argument("--file", required=True, help="Path to the .lrc timed script")
    parser.add_argument("--json", action="store_true", help="Print parsed result as JSON")
    args = parser.parse_args()

    path = Path(args.file)
    if not path.exists():
        print(f"error: file not found: {path}", file=sys.stderr)
        return 2

    result = parse_file(path)
    issues = result["issues"]
    errors = [i for i in issues if i["level"] == "error"]
    warnings = [i for i in issues if i["level"] == "warning"]

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(f"file: {path}")
        print(f"meta: {result['meta']}")
        print(f"groups: {len(result['groups'])}  cues: {len(result['cues'])}")
        for issue in issues:
            print(f"[{issue['level']}] line {issue['line']}: {issue['message']}")
        if not issues:
            print("OK")

    if errors:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
