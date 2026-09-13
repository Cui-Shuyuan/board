#!/usr/bin/env python3
"""
讲规口播稿的分层编辑工具。

源文件：
    games/{game}/tutorial/script.{track}.json

结构：
    group_path -> cue -> beat

- cue 是 TTS 单元：一条 cue 生成一个音频。
- beat 是文本最小单元：拆分/合并按 beat 分组进行。
- cue.pause_after 是播完这个 cue 后，给动画留白的秒数。

命令：
    import  从现有 LRC 导入 source JSON
    build   从 source JSON 生成 estimated LRC
    validate 校验 source JSON
    split   拆分一个 cue
    merge   合并相邻 cue
    set-pause 设置 cue 尾部留白
"""

from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from pathlib import Path
from typing import Any

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from validate_timed_script import parse_file  # noqa: E402

CPS = 5.0
CUE_PAUSE = 0.2


def format_time(seconds: float) -> str:
    total_cs = int(round(max(0.0, seconds) * 100))
    return f"[{total_cs // 6000:02d}:{(total_cs % 6000) / 100:05.2f}]"


def count_chars(text: str) -> int:
    return len(re.findall(r"[\u4e00-\u9fffA-Za-z0-9]", text))


def split_beats(text: str) -> list[str]:
    parts = re.findall(r"[^。！？；]+[。！？；]?", text)
    return [p.strip() for p in parts if p.strip()]


def make_beats(cue: dict[str, Any], text: str) -> list[dict[str, Any]]:
    beats = []
    for i, part in enumerate(split_beats(text), 1):
        beats.append({"id": f"{cue['id']}.b{i}", "text": part})
    return beats or [{"id": f"{cue['id']}.b1", "text": text}]


def build_group_paths(groups: list[str]) -> dict[str, list[str]]:
    code_to_path: dict[str, list[str]] = {}
    title_to_path: dict[str, list[str]] = {}
    for title in groups:
        m = re.match(r"^([0-9]+(?:\.[0-9]+)*)\s+", title)
        code = m.group(1) if m else None
        if code and "." in code:
            parent = code.rsplit(".", 1)[0]
            path = code_to_path.get(parent, []) + [title]
        else:
            path = [title]
        if code:
            code_to_path[code] = path
        title_to_path[title] = path
    return title_to_path


def source_path(game: str, track: str) -> Path:
    return ROOT / "games" / game / "tutorial" / f"script.{track}.json"


def lrc_path(game: str, track: str) -> Path:
    return ROOT / "games" / game / "tutorial" / f"{track}.lrc"


def load_source(path: Path) -> dict[str, Any]:
    if not path.exists():
        raise SystemExit(f"source not found: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def save_source(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"[source] {path}")


def validate_source(data: dict[str, Any]) -> list[str]:
    issues: list[str] = []
    ids: set[str] = set()
    for cue in data.get("cues", []):
        cue_id = cue.get("id", "")
        if not cue_id:
            issues.append("cue missing id")
        elif cue_id in ids:
            issues.append(f"duplicate cue id: {cue_id}")
        ids.add(cue_id)

        beats = cue.get("beats") or []
        if not beats:
            issues.append(f"{cue_id}: no beats")
        beat_ids: set[str] = set()
        for beat in beats:
            bid = beat.get("id", "")
            if not bid:
                issues.append(f"{cue_id}: beat missing id")
            elif bid in beat_ids:
                issues.append(f"{cue_id}: duplicate beat id {bid}")
            beat_ids.add(bid)
            if not (beat.get("text") or "").strip():
                issues.append(f"{cue_id}: empty beat {bid}")
    return issues


def rebuild_lrc(data: dict[str, Any], out_path: Path | None = None) -> str:
    lines: list[str] = []
    lines.append(f"[ti:{data.get('title', '')}]")
    lines.append(f"[game:{data.get('game_id', '')}]")
    lines.append(f"[track:{data.get('track', '')}]")
    lines.append("[timing:estimated]")
    if data.get("version"):
        lines.append(f"[version:{data['version']}]")
    lines.append("[generator:tutorial_script_tool.py]")

    cursor = 0.0
    last_path: list[str] = []
    for cue in data.get("cues", []):
        path = cue.get("group_path") or ([cue["group"]] if cue.get("group") else [])
        common = 0
        while common < len(path) and common < len(last_path) and path[common] == last_path[common]:
            common += 1
        for title in path[common:]:
            lines.append(f"[group:{title}]")
        last_path = path

        text = "".join((beat.get("text") or "") for beat in cue.get("beats", []))
        if not text:
            continue
        refs = cue.get("refs", [])
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"{format_time(cursor)}[id:{cue['id']}]{ref_tag}{text}")
        cursor += max(1.0, count_chars(text) / CPS) + CUE_PAUSE + float(cue.get("pause_after", 0) or 0)

    lines.append(f"[length:{format_time(cursor)[1:-1]}]")
    output = "\n".join(lines) + "\n"
    if out_path is not None:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(output, encoding="utf-8")
        print(f"[lrc] {out_path}")
    return output


def cmd_import(args: argparse.Namespace) -> int:
    src_lrc = args.lrc or lrc_path(args.game, args.track)
    doc = parse_file(src_lrc)
    errors = [i for i in doc["issues"] if i["level"] == "error"]
    if errors:
        raise SystemExit(f"LRC invalid: {errors}")

    group_paths = build_group_paths(doc["groups"])
    cues: list[dict[str, Any]] = []
    for cue in doc["cues"]:
        text = cue.get("text", "")
        if not text:
            continue
        entry = {
            "id": cue["id"],
            "group": cue.get("group"),
            "group_path": group_paths.get(cue.get("group") or "", [cue["group"]] if cue.get("group") else []),
            "refs": cue.get("refs", []),
            "pause_after": 0.0,
            "beats": [],
        }
        entry["beats"] = make_beats(entry, text)
        cues.append(entry)

    data = {
        "schema_version": 1,
        "game_id": args.game,
        "track": args.track,
        "title": doc["meta"].get("ti", ""),
        "version": doc["meta"].get("version", "0.1.0"),
        "cues": cues,
    }
    save_source(args.source, data)
    rebuild_lrc(data, args.lrc or lrc_path(args.game, args.track))
    return 0


def cmd_build(args: argparse.Namespace) -> int:
    data = load_source(args.source)
    rebuild_lrc(data, args.lrc or lrc_path(args.game, args.track))
    return 0


def cmd_validate(args: argparse.Namespace) -> int:
    data = load_source(args.source)
    issues = validate_source(data)
    if issues:
        for issue in issues:
            print(f"[error] {issue}")
        return 1
    print("OK")
    return 0


def unique_cue_id(data: dict[str, Any], base: str) -> str:
    ids = {c["id"] for c in data.get("cues", [])}
    if base not in ids:
        return base
    i = 2
    while f"{base}.s{i}" in ids:
        i += 1
    return f"{base}.s{i}"


def cmd_split(args: argparse.Namespace) -> int:
    data = load_source(args.source)
    cues = data.get("cues", [])
    idx = next((i for i, c in enumerate(cues) if c["id"] == args.cue), None)
    if idx is None:
        raise SystemExit(f"cue not found: {args.cue}")

    cue = cues[idx]
    text = "".join((b.get("text") or "") for b in cue.get("beats", []))
    pos = text.find(args.at)
    if pos < 0:
        raise SystemExit(f"split text not found in {cue['id']}: {args.at!r}")
    if pos == 0:
        raise SystemExit("split at beginning not supported")

    left_text = text[:pos].strip()
    right_text = text[pos:].strip()
    if left_text and left_text[-1] not in "。！？；":
        left_text += "。"
    if right_text and right_text[0] in "，、":
        right_text = right_text.lstrip("，、").strip()

    left = copy.deepcopy(cue)
    left["beats"] = make_beats(left, left_text)
    left["pause_after"] = 0.0

    right = copy.deepcopy(cue)
    right["id"] = unique_cue_id(data, f"{cue['id']}.s2")
    right["beats"] = make_beats(right, right_text)
    right["pause_after"] = cue.get("pause_after", 0.0)

    if args.dry_run:
        preview = copy.deepcopy(data)
        preview["cues"] = cues[:idx] + [left, right] + cues[idx + 1:]
        print(f"[dry-run] split {cue['id']} -> {left['id']} + {right['id']}")
        print(rebuild_lrc(preview))
        return 0

    cues[idx:idx + 1] = [left, right]
    save_source(args.source, data)
    rebuild_lrc(data, args.lrc or lrc_path(args.game, args.track))
    print(f"[split] {cue['id']} -> {left['id']} + {right['id']}")
    return 0


def cmd_merge(args: argparse.Namespace) -> int:
    data = load_source(args.source)
    cues = data.get("cues", [])
    ids = [x.strip() for x in args.cues.split(",") if x.strip()]
    if len(ids) < 2:
        raise SystemExit("merge requires at least two cue ids")
    indexes = []
    for cue_id in ids:
        idx = next((i for i, c in enumerate(cues) if c["id"] == cue_id), None)
        if idx is None:
            raise SystemExit(f"cue not found: {cue_id}")
        indexes.append(idx)
    if indexes != list(range(indexes[0], indexes[0] + len(indexes))):
        raise SystemExit("cues must be adjacent")
    first = cues[indexes[0]]
    last = cues[indexes[-1]]
    if first.get("group_path") != last.get("group_path"):
        raise SystemExit("cues must be in the same group_path")

    merged = copy.deepcopy(first)
    merged_text = "".join((b.get("text") or "") for b in first.get("beats", []))
    merged_beats = copy.deepcopy(first.get("beats", []))
    for cue in cues[indexes[1]:indexes[-1] + 1]:
        next_text = "".join((b.get("text") or "") for b in cue.get("beats", []))
        if merged_text and not merged_text.endswith(("。", "！", "？", "；", "：")):
            merged_text += "。"
        merged_text += next_text
        merged_beats.extend(copy.deepcopy(cue.get("beats", [])))
    merged["beats"] = merged_beats
    merged["pause_after"] = last.get("pause_after", 0.0)

    if args.dry_run:
        preview = copy.deepcopy(data)
        preview["cues"] = cues[:indexes[0]] + [merged] + cues[indexes[-1] + 1:]
        print(f"[dry-run] merge {ids} -> {merged['id']}")
        print(rebuild_lrc(preview))
        return 0

    cues[indexes[0]:indexes[-1] + 1] = [merged]
    save_source(args.source, data)
    rebuild_lrc(data, args.lrc or lrc_path(args.game, args.track))
    print(f"[merge] {ids} -> {merged['id']}")
    return 0


def cmd_set_pause(args: argparse.Namespace) -> int:
    data = load_source(args.source)
    cue = next((c for c in data.get("cues", []) if c["id"] == args.cue), None)
    if cue is None:
        raise SystemExit(f"cue not found: {args.cue}")
    cue["pause_after"] = float(args.seconds)
    save_source(args.source, data)
    rebuild_lrc(data, args.lrc or lrc_path(args.game, args.track))
    print(f"[pause] {cue['id']} pause_after={cue['pause_after']}s")
    return 0


def add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--game", default="splendor")
    parser.add_argument("--track", default="full")
    parser.add_argument("--source", type=Path, default=None)
    parser.add_argument("--lrc", type=Path, default=None)


def main() -> int:
    parser = argparse.ArgumentParser(description="Layered tutorial script editor.")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("import", help="import existing LRC into source JSON")
    add_common(p)
    p.set_defaults(func=cmd_import)

    p = sub.add_parser("build", help="build estimated LRC from source JSON")
    add_common(p)
    p.set_defaults(func=cmd_build)

    p = sub.add_parser("validate", help="validate source JSON")
    add_common(p)
    p.set_defaults(func=cmd_validate)

    p = sub.add_parser("split", help="split a cue at a text boundary")
    add_common(p)
    p.add_argument("--cue", required=True)
    p.add_argument("--at", required=True, help="substring where the new cue starts")
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(func=cmd_split)

    p = sub.add_parser("merge", help="merge adjacent cues")
    add_common(p)
    p.add_argument("--cues", required=True, help="comma-separated cue ids")
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(func=cmd_merge)

    p = sub.add_parser("set-pause", help="set pause_after for a cue")
    add_common(p)
    p.add_argument("--cue", required=True)
    p.add_argument("--seconds", type=float, required=True)
    p.set_defaults(func=cmd_set_pause)

    args = parser.parse_args()
    if args.source is None:
        args.source = source_path(args.game, args.track)
    if args.lrc is None:
        args.lrc = lrc_path(args.game, args.track)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
