#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unified LRC generation/rewrite helpers for the tutorial pipeline.

Callers keep their own CLI wrappers, but the formatting rules live here so the
estimated timeline, manifest TTS timeline, delta input and TTS rewrite cannot
drift apart.
"""
from __future__ import annotations

import re
from pathlib import Path

CPS = 5.0
CUE_PAUSE = 0.2


def format_time(seconds: float) -> str:
    """``[mm:ss.cc]`` timestamp used by LRC cue lines."""
    total_cs = int(round(max(0.0, seconds) * 100))
    return f"[{total_cs // 6000:02d}:{(total_cs % 6000) / 100:05.2f}]"


def format_time_text(seconds: float) -> str:
    """``mm:ss.cc`` timestamp (no brackets), e.g. for ``[length:...]``."""
    return format_time(seconds)[1:-1]


def count_chars(text: str) -> int:
    return len(re.findall(r"[\u4e00-\u9fffA-Za-z0-9]", text))


def cue_text(cue: dict) -> str:
    return "".join((beat.get("text") or "") for beat in cue.get("beats") or [])


def write_estimated_lrc(data: dict, out_path: Path | None = None) -> str:
    """Source script -> estimated narration timeline (``{track}.lrc``)."""
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

        text = cue_text(cue)
        if not text:
            continue
        refs = cue.get("refs", [])
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"{format_time(cursor)}[id:{cue['id']}]{ref_tag}{text}")
        cursor += max(1.0, count_chars(text) / CPS) + CUE_PAUSE + float(cue.get("pause_after", 0) or 0)

    lines.append(f"[length:{format_time_text(cursor)}]")
    output = "\n".join(lines) + "\n"
    if out_path is not None:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(output, encoding="utf-8")
        print(f"[lrc] {out_path}")
    return output


def write_manifest_tts_lrc(game_dir: Path, track: str, script: dict, manifest: dict) -> Path:
    """Write ``{track}.tts.lrc`` from script order + manifest durations."""
    lrc_path = game_dir / "tutorial" / f"{track}.tts.lrc"
    manifest_by = {cue["id"]: cue for cue in manifest.get("cues") or []}
    lines = [
        f"[ti:{script.get('title','')}]",
        f"[game:{script.get('game_id','')}]",
        f"[track:{script.get('track','')}]",
        "[timing:tts]",
        "[generator:animation/compile_tutorial.py]",
    ]
    if script.get("version"):
        lines.append(f"[version:{script['version']}]")

    cursor = 0.0
    last_path: list[str] = []
    gap = float(manifest.get("gap_seconds", 0.0) or 0.0)
    for cue in script.get("cues") or []:
        cid = cue.get("id")
        if not cid:
            continue
        manifest_cue = manifest_by.get(cid)
        if manifest_cue is None:
            raise SystemExit(f"manifest missing cue: {cid}")
        path = cue.get("group_path") or ([cue.get("group")] if cue.get("group") else [])
        common = 0
        while common < len(path) and common < len(last_path) and path[common] == last_path[common]:
            common += 1
        for title in path[common:]:
            lines.append(f"[group:{title}]")
        last_path = path
        start = float(manifest_cue.get("start", cursor) or 0.0)
        refs = cue.get("refs") or []
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"{format_time(start)}[id:{cid}]{ref_tag}{cue_text(cue)}")
        cursor = start + float(manifest_cue.get("duration", 0.0) or 0.0) + gap

    lines.append(f"[length:{format_time_text(cursor)}]")
    lrc_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return lrc_path


def write_delta_lrc(path: Path, cues: list[dict], refs_by_id: dict, game: str, track: str) -> None:
    """Write the one-shot TTS input for changed cues."""
    lines = ["[ti:delta]", f"[game:{game}]", f"[track:{track}]", "[timing:estimated]"]
    last_group = None
    for cue in cues:
        group = cue.get("group") or ""
        if group != last_group:
            lines.append(f"[group:{group}]")
            last_group = group
        refs = refs_by_id.get(cue["id"]) or []
        ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
        lines.append(f"[00:00.00][id:{cue['id']}]{ref_tag}{cue_text(cue)}")
    lines.append("[length:23:59.99]")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def update_lrc_refs(lrc_path: Path, script: dict) -> None:
    """Refresh ``[ref:...]`` tags in an existing LRC from the source script."""
    script_by = {cue["id"]: cue for cue in script.get("cues") or []}
    lines = lrc_path.read_text(encoding="utf-8").splitlines()
    out = []
    for line in lines:
        match = re.match(r'^(\[[0-9:.]+\]\[id:([^\]]+)\])(?:\[ref:[^\]]*\])?(.*)$', line)
        if match and match.group(2) in script_by:
            refs = script_by[match.group(2)].get("refs") or []
            ref_tag = f"[ref:{'|'.join(refs)}]" if refs else ""
            out.append(match.group(1) + ref_tag + match.group(3))
        else:
            out.append(line)
    lrc_path.write_text("\n".join(out) + "\n", encoding="utf-8")


def rewrite_tts_lrc(
    input_lrc: Path,
    output_lrc: Path,
    cues: list[dict],
    results: list[dict],
    gap: float,
) -> None:
    """Rewrite an estimated LRC with actual TTS durations, preserving tags."""
    time_re = re.compile(r"^\[(\d{2}):(\d{2}\.\d{2})\]")
    meta_re = re.compile(r"^\[([A-Za-z_]+):([^\]]*)\]")
    by_id = {result["id"]: result for result in results}
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
            if key in ("timing", "length", "generator"):
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

        cue_id = next((tag[3:] for tag in tags if tag.startswith("id:")), "")
        result = by_id.get(cue_id)
        if result is None:
            # limit mode: cues absent from this synthesis run are not emitted.
            continue

        tag_text = "".join(f"[{tag}]" for tag in tags)
        out.append(f"{format_time(cursor)}{tag_text}{rest}")
        cursor += float(result["duration"]) + float(gap or 0.0)

    if not timing_written:
        out.insert(0, "[timing:tts]")
    out.append(f"[length:{format_time_text(cursor)}]")
    output_lrc.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"[lrc] {output_lrc}")
