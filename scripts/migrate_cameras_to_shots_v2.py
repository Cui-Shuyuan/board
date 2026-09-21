#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""One-off migration: per-cue script.camera -> stage named shots + camera events.

Destructive by design (v3 model): the camera becomes a timed op, and cues only
reference a named shot.  The geometry compiler resolves the shot to concrete
frame values later.
"""
from __future__ import annotations
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
V2 = ROOT / "games" / "splendor" / "tutorial" / "anim" / "v2"


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def save(p: Path, doc):
    p.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def slug(s: str) -> str:
    s = re.sub(r"[^0-9A-Za-z_]+", "_", s).strip("_")
    return s or "shot"


def shot_id_for(zones, fill):
    if not zones or zones == ["board"]:
        base = "shot_board"
    else:
        base = "shot_" + "_".join(slug(z) for z in zones)
    if abs(float(fill) - 0.8) > 1e-9:
        base += f"_f{str(fill).replace('.', '')}"
    return base[:80]


def migrate_track(track_path: Path):
    doc = load(track_path)
    trees = {t["id"]: t for t in doc.get("trees") or []}
    # stage doc cache
    stages = {}
    for t in doc.get("trees") or []:
        sp = t["stage"]
        p = V2 / sp if not Path(sp).is_absolute() else Path(sp)
        if p.exists():
            stages[t["id"]] = (p, load(p))
    # First pass: collect distinct camera specs per stage and create shots.
    specs = {}   # stage_id -> {(zones_tuple, fill): shot_id}
    stage_shots = {}  # stage_id -> [shot dict]
    for cue in doc.get("cues") or []:
        cam = (cue.get("script") or {}).get("camera")
        if not cam:
            continue
        tree = trees[cue["tree"]]
        z = tuple(cam.get("zones") or [])
        fill = round(float(cam.get("fill", 0.8)), 6)
        key = (z, fill)
        if tree["id"] not in specs:
            specs[tree["id"]] = {}
            stage_shots[tree["id"]] = []
        if key not in specs[tree["id"]]:
            sid = shot_id_for(list(z), fill)
            # avoid collision
            used = {s["id"] for s in stage_shots[tree["id"]]}
            base = sid; n = 2
            while sid in used:
                sid = f"{base}_{n}"; n += 1
            specs[tree["id"]][key] = sid
            stage_shots[tree["id"]].append({
                "id": sid,
                "zones": list(z),
                "fill": fill,
                "desc": ("整桌" if (not z or z == ("board",)) else "框 " + "、".join(z))
                        + f"，占画面 {fill:g}",
            })
    # Second pass: turn each cue's camera into an event.
    for cue in doc.get("cues") or []:
        script = cue.get("script") or {}
        cam = script.get("camera")
        if not cam:
            continue
        tree_id = cue["tree"]
        z = tuple(cam.get("zones") or [])
        fill = round(float(cam.get("fill", 0.8)), 6)
        sid = specs[tree_id][(z, fill)]
        at = float(cam.get("at", 0.0) or 0.0)
        script.pop("camera", None)
        cue.setdefault("events", []).insert(0, {
            "op": "camera", "at": at, "dur": 0.0, "shot": sid,
            "realizes": "<ontology::timing>",
        })
    # Install shots into stage docs.
    for tree_id, (p, sd) in stages.items():
        shots = stage_shots.get(tree_id) or []
        if shots:
            sd["shots"] = shots
            # keep a stable, readable position right after board/extent
            save(p, sd)
    save(track_path, doc)
    return sum(len(v) for v in stage_shots.values())


def main():
    n = migrate_track(V2 / "full.anim.json")
    n += migrate_track(V2 / "_schema_example.anim.json")
    print(f"OK   migrated cameras -> shots ({n} shot definitions)")


if __name__ == "__main__":
    sys.exit(main())
