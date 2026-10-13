#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Validate the animation change log.

The log is append-only JSONL at docs/animation-change-log/entries.jsonl.
This checker keeps the schema from drifting as the file grows.

Usage:
    python3 tools/ops/check_animation_change_log.py
    python3 tools/ops/check_animation_change_log.py --summary
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
LOG_PATH = ROOT / "docs" / "animation-change-log" / "entries.jsonl"

REQUIRED = (
    "id", "date", "game", "track", "scope", "section", "cue",
    "before", "desired", "reason", "status",
)
SCOPES = {"cue", "section", "track"}
STATUSES = {"implemented", "pending", "rejected"}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--summary", action="store_true", help="also print a small summary")
    args = ap.parse_args(argv)

    if not LOG_PATH.is_file():
        print(f"ERR  missing log: {LOG_PATH}", file=sys.stderr)
        return 2

    errors: list[str] = []
    ids: set[str] = set()
    games = Counter()
    tracks = Counter()
    tags = Counter()
    statuses = Counter()
    rows: list[dict] = []

    for lineno, raw in enumerate(LOG_PATH.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line:
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as exc:
            errors.append(f"line {lineno}: invalid JSON: {exc}")
            continue
        if not isinstance(row, dict):
            errors.append(f"line {lineno}: entry must be an object")
            continue
        rows.append(row)

        for field in REQUIRED:
            if field not in row:
                errors.append(f"line {lineno}: missing required field {field!r}")
        if errors and any(e.startswith(f"line {lineno}: missing") for e in errors):
            continue

        rid = row.get("id")
        if not isinstance(rid, str) or not rid.strip():
            errors.append(f"line {lineno}: id must be a non-empty string")
        elif rid in ids:
            errors.append(f"line {lineno}: duplicate id {rid!r}")
        else:
            ids.add(rid)

        date = row.get("date")
        if not isinstance(date, str):
            errors.append(f"line {lineno}: date must be a string")
        else:
            try:
                dt.date.fromisoformat(date)
            except ValueError:
                errors.append(f"line {lineno}: date must be ISO YYYY-MM-DD, got {date!r}")

        for field in ("game", "track", "section", "before", "desired", "reason"):
            if not isinstance(row.get(field), str) or not str(row.get(field)).strip():
                errors.append(f"line {lineno}: {field} must be a non-empty string")

        scope = row.get("scope")
        if scope not in SCOPES:
            errors.append(f"line {lineno}: scope must be one of {sorted(SCOPES)}, got {scope!r}")
        cue = row.get("cue")
        if scope == "cue" and (not isinstance(cue, str) or not cue.strip()):
            errors.append(f"line {lineno}: cue scope needs a non-empty cue id")
        if cue is not None and (not isinstance(cue, str) or not cue.strip()):
            errors.append(f"line {lineno}: cue must be null or a non-empty string")

        status = row.get("status")
        if status not in STATUSES:
            errors.append(f"line {lineno}: status must be one of {sorted(STATUSES)}, got {status!r}")
        if status == "implemented" and not str(row.get("actual") or "").strip():
            errors.append(f"line {lineno}: implemented entries need actual")

        tags_value = row.get("tags")
        if tags_value is not None:
            if not isinstance(tags_value, list) or any(not isinstance(t, str) for t in tags_value):
                errors.append(f"line {lineno}: tags must be a list of strings")
            else:
                tags.update(tags_value)

        if "backfill" in row and not isinstance(row["backfill"], bool):
            errors.append(f"line {lineno}: backfill must be boolean")
        kind = row.get("kind", "change")
        if kind not in ("change", "resolution"):
            errors.append(f"line {lineno}: kind must be 'change' or 'resolution'")
        if kind == "resolution" and not str(row.get("resolves") or "").strip():
            errors.append(f"line {lineno}: resolution entries need resolves")

        games[str(row.get("game"))] += 1
        tracks[str(row.get("track"))] += 1
        statuses[str(status)] += 1

    if errors:
        for e in errors:
            print("ERR  " + e, file=sys.stderr)
        print(f"FAIL animation change log: {len(errors)} errors, {len(rows)} entries", file=sys.stderr)
        return 1

    print(f"OK   animation change log: {len(rows)} entries")
    if args.summary:
        print("games:", dict(games))
        print("tracks:", dict(tracks))
        print("statuses:", dict(statuses))
        if tags:
            print("top tags:", ", ".join(f"{k}({v})" for k, v in tags.most_common(12)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
