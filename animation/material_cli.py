#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Material library CLI: validate, inspect, find impact, rebuild affected tracks."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import material_library as ml  # noqa: E402
import section_source  # noqa: E402


def _load_lib(game: str):
    tutorial = section_source.tutorial_dir(game)
    lib = ml.load_materials(game, tutorial)
    return lib


def _dependency_closure(lib: ml.MaterialLibrary, root: str) -> set[str]:
    seen: set[str] = set()
    stack = [root]
    while stack:
        mid = stack.pop()
        if mid in seen:
            continue
        seen.add(mid)
        asset = lib.assets.get(mid)
        if asset:
            stack.extend(sorted(asset.dependencies))
    return seen


def _affected(game: str, material: str, *, include_self: bool = True):
    lib = _load_lib(game)
    if material not in lib.assets:
        raise KeyError(f"{game}: unknown material {material!r}")
    affected = []
    seen = set()
    for track in section_source.discover_tracks(game):
        try:
            src = section_source.load_track_source(game, track)
        except Exception as exc:  # noqa: BLE001
            print(f"WARN {game}/{track}: cannot load source: {exc}", file=sys.stderr)
            continue
        for usage in src.material_usages:
            closure = _dependency_closure(lib, usage.material)
            if material in closure or (include_self and usage.material == material):
                key = (track, usage.cue, usage.material)
                if key in seen:
                    continue
                seen.add(key)
                affected.append({
                    "game": game,
                    "track": track,
                    "cue": usage.cue,
                    "use": usage.where,
                    "material": usage.material,
                    "via": [] if usage.material == material else sorted(closure),
                })
    return lib, affected


def cmd_list(args) -> int:
    games = args.game or [section_source.discover_tracks(g) and g or g for g in []]
    # discover games with a tutorial dir containing library/
    if not games:
        games = sorted({
            p.parent.parent.name
            for p in (ROOT / "content/games").glob("*/tutorial/library")
        })
    rc = 0
    for game in games:
        try:
            lib = _load_lib(game)
        except Exception as exc:  # noqa: BLE001
            print(f"ERR  {game}: {exc}", file=sys.stderr)
            rc = 1
            continue
        if args.kind:
            assets = [a for a in lib.assets.values() if a.kind == args.kind]
        else:
            assets = list(lib.assets.values())
        for asset in sorted(assets, key=lambda a: a.id):
            deps = f" deps=[{', '.join(sorted(asset.dependencies))}]" if asset.dependencies else ""
            print(f"{asset.kind:12s} {asset.id}  {asset.title}{deps}")
    return rc


def cmd_validate(args) -> int:
    games = args.game or sorted({
        p.parent.parent.name for p in (ROOT / "content/games").glob("*/tutorial/library")
    })
    rc = 0
    for game in games:
        try:
            lib = _load_lib(game)
        except Exception as exc:  # noqa: BLE001
            print(f"ERR  {game}: {exc}", file=sys.stderr)
            rc = 1
            continue
        for warning in lib.warnings:
            print(f"WARN {game}: {warning}", file=sys.stderr)
        print(f"OK   {game}: {len(lib.assets)} materials loaded")
        for asset in lib.assets.values():
            for ref in sorted(asset.dependencies):
                if ref not in lib.assets:
                    print(f"ERR  {asset.id}: missing dependency {ref}", file=sys.stderr)
                    rc = 1
    return rc


def cmd_expand_cue(args) -> int:
    game, track, cue_id = args.game, args.track, args.cue
    if not game or not track or not cue_id:
        print("--game --track --cue are required", file=sys.stderr)
        return 2
    try:
        src = section_source.load_track_source(game, track)
    except Exception as exc:  # noqa: BLE001
        print(f"ERR  source load: {exc}", file=sys.stderr)
        return 1
    cue = next((c for c in src.anim_doc.get("cues") or [] if c.get("id") == cue_id), None)
    if cue is None:
        print(f"ERR  unknown cue {cue_id!r}", file=sys.stderr)
        return 1
    report = {
        "game": game,
        "track": track,
        "cue": cue_id,
        "events": cue.get("events") or [],
        "source_map": [u.to_dict() for u in src.material_usages if u.cue == cue_id],
    }
    if args.anchors:
        report["time_anchors"] = [
            a for a in src.anim_doc.get("time_anchors") or []
            if str(a.get("id", "")).startswith(cue_id + ".")
        ]
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


def cmd_impact(args) -> int:
    games = args.game or sorted({
        p.parent.parent.name for p in (ROOT / "content/games").glob("*/tutorial/library")
    })
    rc = 0
    all_affected = []
    for game in games:
        try:
            _, affected = _affected(game, args.material)
        except Exception as exc:  # noqa: BLE001
            print(f"ERR  {game}: {exc}", file=sys.stderr)
            rc = 1
            continue
        all_affected.extend(affected)
        for item in affected:
            via = f" via {', '.join(item['via'])}" if item["via"] else ""
            print(f"{item['game']}/{item['track']} {item['cue']} <- {item['material']}{via}")
    if not all_affected:
        print(f"no callers for {args.material}")
    if args.rebuild:
        # Recompile each affected track through the same source -> compiled path.
        pairs = sorted({(i["game"], i["track"]) for i in all_affected})
        import compile_animation_v2  # noqa: PLC0415
        for game, track in pairs:
            print(f"[rebuild] {game}/{track}")
            code = compile_animation_v2.main(["--game", game, "--track", track])
            if code:
                rc = code
    elif args.check:
        for item in all_affected:
            compiled = section_source.compiled_path(item["game"], item["track"])
            try:
                src = section_source.load_track_source(item["game"], item["track"])
                current = json.loads(compiled.read_text(encoding="utf-8")).get("source_sha256")
            except Exception as exc:  # noqa: BLE001
                print(f"ERR  {item['game']}/{item['track']}: {exc}", file=sys.stderr)
                rc = 1
                continue
            if current != src.source_hash:
                print(
                    f"STALE {item['game']}/{item['track']}: {compiled}",
                    file=sys.stderr,
                )
                rc = 1
    return rc


def cmd_stale(args) -> int:
    games = args.game or sorted({
        p.parent.parent.name for p in (ROOT / "content/games").glob("*/tutorial/library")
    })
    rc = 0
    for game in games:
        for track in section_source.discover_tracks(game):
            compiled = section_source.compiled_path(game, track)
            if not compiled.is_file():
                print(f"STALE {game}/{track}: missing {compiled}", file=sys.stderr)
                rc = 1
                continue
            try:
                src = section_source.load_track_source(game, track)
                current = json.loads(compiled.read_text(encoding="utf-8")).get("source_sha256")
            except Exception as exc:  # noqa: BLE001
                print(f"ERR  {game}/{track}: {exc}", file=sys.stderr)
                rc = 1
                continue
            if current != src.source_hash:
                print(f"STALE {game}/{track}: {compiled}", file=sys.stderr)
                rc = 1
            else:
                print(f"OK   {game}/{track}: compiled source hash current")
    return rc


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("list", help="list materials")
    p.add_argument("--game", action="append")
    p.add_argument("--kind", choices=sorted(ml.MATERIAL_KINDS))
    p.set_defaults(func=cmd_list)

    p = sub.add_parser("validate", help="load/validate library")
    p.add_argument("--game", action="append")
    p.set_defaults(func=cmd_validate)

    p = sub.add_parser("expand-cue", help="print a cue after material expansion")
    p.add_argument("--game", required=True)
    p.add_argument("--track", required=True)
    p.add_argument("--cue", required=True)
    p.add_argument("--anchors", action="store_true")
    p.set_defaults(func=cmd_expand_cue)

    p = sub.add_parser("impact", help="find all tracks/cues affected by a material")
    p.add_argument("--material", required=True)
    p.add_argument("--game", action="append")
    p.add_argument("--check", action="store_true", help="check affected compiled outputs are current")
    p.add_argument("--rebuild", action="store_true", help="rebuild affected tracks")
    p.set_defaults(func=cmd_impact)

    p = sub.add_parser("stale", help="check all track compiled outputs for stale source hash")
    p.add_argument("--game", action="append")
    p.set_defaults(func=cmd_stale)

    a = ap.parse_args(argv)
    return a.func(a)


if __name__ == "__main__":
    raise SystemExit(main())
