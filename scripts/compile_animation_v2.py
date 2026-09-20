#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deterministic v2 animation compiler.

Source:  {track}.anim.json + its stage files
Output:  {track}.compiled.json

The compiler is the canonical state simulator and geometry consumer:
  * validates source schema;
  * resolves selectors into concrete component ids;
  * builds cue entry/exit snapshots (runtime jumps are pure lookups);
  * converts state changes + presentation events into concrete clips;
  * writes slot tables and camera frames from the single geometry module.

Usage:
    python3 scripts/compile_animation_v2.py --game splendor --track _schema_example
    python3 scripts/compile_animation_v2.py --source path/to/foo.anim.json --check
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import anim_geometry_v2 as geom  # noqa: E402
import anim_schema_v2 as schema  # noqa: E402


# ── helpers ────────────────────────────────────────────────────────────────

def norm(s) -> str:
    return (str(s or "")).strip()


def face_int(v) -> int:
    if v in ("up", "face_up", 2, "2"):
        return 2
    if v in ("down", "face_down", 1, "1"):
        return 1
    if v in ("hidden", 0, "0"):
        return 0
    return 2


def face_name(v: int) -> str:
    return {2: "face_up", 1: "face_down", 0: "hidden"}.get(v, "face_up")


def parts_norm(parts):
    out = []
    for p in parts or []:
        if isinstance(p, dict) and p.get("key") is not None:
            out.append({"key": p.get("key"), "value": p.get("value")})
    return out


def selector_from_event(ev: dict) -> dict:
    return {
        "template": norm(ev.get("template")),
        "palette": norm(ev.get("palette")),
        "concept": norm(ev.get("concept")),
        "parts": parts_norm(ev.get("parts")),
    }


class StateModel:
    """Small pure simulator used only at compile time.

    It is deliberately not a runtime dependency: the compiled cue carries full
    start/end snapshots, so the Unity runtime never resolves selectors.
    """

    def __init__(self):
        self.items = []
        self.next_seq = {}

    def snapshot(self) -> dict:
        comps = []
        for it in sorted(self.items, key=lambda x: (x["order"], x["id"])):
            comps.append({
                "Id": it["id"],
                "TemplateId": it["template"],
                "Palette": it["palette"],
                "Concept": it["concept"],
                "parts": copy.deepcopy(it.get("parts") or []),
                "ZoneId": it["zone"],
                "Order": int(it["order"]),
                "Face": int(it["face"]),
            })
        return {"components": comps,
                "nextSeq": [{"key": k, "value": v} for k, v in sorted(self.next_seq.items())]}

    def matching(self, zone: str, selector: dict) -> list:
        out = []
        for it in self.items:
            if zone and it["zone"] != zone:
                continue
            if selector:
                if selector.get("template") and it["template"] != selector["template"]:
                    continue
                if selector.get("palette") and it["palette"] != selector["palette"]:
                    continue
                if selector.get("concept") and it["concept"] != selector["concept"]:
                    continue
                if selector.get("parts"):
                    have = {(p.get("key"), p.get("value")) for p in (it.get("parts") or [])}
                    if not all((p.get("key"), p.get("value")) in have for p in selector["parts"]):
                        continue
            out.append(it)
        out.sort(key=lambda x: (x["order"], x["id"]))
        return out

    def count(self, zone: str, selector: dict = None) -> int:
        return len(self.matching(zone, selector or {}))

    def _normalize(self, zone: str):
        arr = sorted([i for i in self.items if i["zone"] == zone], key=lambda x: (x["order"], x["id"]))
        for i, it in enumerate(arr):
            it["order"] = i

    def spawn(self, template: str, palette: str, concept: str, zone: str, count: int,
              face: int = 2, parts=None) -> list:
        if count <= 0:
            return []
        if not zone:
            raise ValueError("spawn zone required")
        added = []
        for _ in range(count):
            key = f"{template}|{palette}"
            seq = self.next_seq.get(key, 0) + 1
            self.next_seq[key] = seq
            it = {
                "id": f"{key}#{seq}",
                "template": template,
                "palette": palette,
                "concept": concept,
                "parts": copy.deepcopy(parts or []),
                "zone": zone,
                "order": self.count(zone),
                "face": int(face),
            }
            self.items.append(it)
            added.append(it)
        self._normalize(zone)
        return added

    def ensure_at_least(self, template: str, palette: str, concept: str, zone: str, count: int,
                        face: int = 2, parts=None) -> list:
        sel = {"template": template, "palette": palette, "concept": concept, "parts": parts or []}
        have = self.count(zone, sel)
        return self.spawn(template, palette, concept, zone, max(0, count - have), face, parts)

    def destroy(self, zone: str, selector: dict, count: int, from_back: bool = False) -> list:
        arr = self.matching(zone, selector)
        if len(arr) < count:
            raise ValueError(f"destroy needs {count} in {zone}, have {len(arr)}")
        victims = arr[-count:] if from_back else arr[:count]
        ids = {v["id"] for v in victims}
        self.items = [i for i in self.items if i["id"] not in ids]
        self._normalize(zone)
        return victims

    def transfer(self, selector: dict, source: str, dest: str, quantity: int,
                 to_face=None, order: int = -1) -> list:
        arr = self.matching(source, selector)
        if len(arr) < quantity:
            raise ValueError(f"transfer needs {quantity} from {source}, have {len(arr)}")
        moved = arr[:quantity]
        moved_ids = {m["id"] for m in moved}
        records = []
        for it in moved:
            rec = {"item": it, "from_zone": it["zone"], "from_order": it["order"]}
            records.append(rec)
        self.items = [i for i in self.items if i["id"] not in moved_ids]
        self._normalize(source)
        for rec in records:
            it = rec["item"]
            it["zone"] = dest
            it["order"] = self.count(dest)
            if to_face is not None:
                it["face"] = face_int(to_face)
            self.items.append(it)
            rec["to_zone"] = dest
            rec["to_order"] = it["order"]
        self._normalize(dest)
        if order >= 0 and moved:
            self.move_order(moved[0], dest, order)
            for rec in records:
                rec["to_order"] = rec["item"]["order"]
        return records

    def move_order(self, item: dict, zone: str, order: int):
        arr = sorted([i for i in self.items if i["zone"] == zone], key=lambda x: (x["order"], x["id"]))
        arr = [i for i in arr if i["id"] != item["id"]]
        at = max(0, min(order, len(arr)))
        arr.insert(at, item)
        self.items = [i for i in self.items if i["zone"] != zone]
        self.items.extend(arr)
        for i, it in enumerate(arr):
            it["order"] = i

    def shuffle(self, zone: str, seed: int):
        arr = sorted([i for i in self.items if i["zone"] == zone], key=lambda x: (x["order"], x["id"]))
        arr.sort(key=lambda it: (fnv32(it["id"], seed), it["id"]))
        for i, it in enumerate(arr):
            it["order"] = i

    def set_face(self, selector: dict, zone: str, face: int):
        for it in self.matching(zone, selector):
            it["face"] = int(face)


def fnv32(s: str, seed: int = 0) -> int:
    h = (2166136261 ^ (seed & 0xFFFFFFFF)) & 0xFFFFFFFF
    for ch in s:
        h ^= ord(ch)
        h = (h * 16777619) & 0xFFFFFFFF
    return h


# ── compiler ────────────────────────────────────────────────────────────────

class Compiler:
    def __init__(self, track_path: Path):
        self.track_path = track_path
        self.track_dir = track_path.parent
        self.doc = json.loads(track_path.read_text(encoding="utf-8"))
        self.rep = schema.validate_track(self.doc)
        self.stages = {}       # stage id -> source stage
        self.compiled_stages = {}
        self.trees = {}
        self.world_modes = {w.get("id"): w.get("mode", "isolated") for w in (self.doc.get("worlds") or [])}

    def resolve_stage_path(self, rel: str) -> Path:
        rel = norm(rel)
        if not rel:
            raise ValueError("tree.stage is empty")
        candidates = [
            self.track_dir / rel,
            self.track_dir / (rel + ".json"),
            self.track_dir / ".." / rel,
            self.track_dir / ".." / (rel + ".json"),
        ]
        for p in candidates:
            if p.exists():
                return p.resolve()
        raise FileNotFoundError(f"stage not found: {rel}")

    def load(self):
        if not self.rep.ok():
            raise ValueError("schema errors:\n" + "\n".join(self.rep.errors))
        for tree in self.doc.get("trees") or []:
            rel = tree.get("stage")
            path = self.resolve_stage_path(rel)
            stage = json.loads(path.read_text(encoding="utf-8"))
            sr = schema.validate_stage(stage)
            if not sr.ok():
                raise ValueError(f"stage schema errors in {path}:\n" + "\n".join(sr.errors))
            sid = stage.get("id") or path.stem
            self.stages[sid] = stage
            self.compiled_stages[sid] = geom.build_compiled_stage(stage)
            self.trees[tree["id"]] = {
                "id": tree["id"],
                "world": tree.get("world") or tree["id"],
                "stage": sid,
                "purpose": tree.get("purpose", ""),
                "initial": tree.get("initial", ""),
                "extent_note": tree.get("extent_note", ""),
            }

    def compile(self) -> dict:
        self.load()
        stores = {}
        prev_world = None
        cues_out = []
        for idx, cue in enumerate(self.doc.get("cues") or []):
            tree = self.trees.get(cue.get("tree"))
            if tree is None:
                raise ValueError(f"cue {cue.get('id')}: unknown tree {cue.get('tree')!r}")
            world = tree["world"]
            if world not in stores:
                stores[world] = StateModel()
            # Independent worlds reset when re-entered; shared worlds preserve state.
            if prev_world is not None and prev_world != world and self.world_modes.get(world, "isolated") == "isolated":
                stores[world] = StateModel()
            if prev_world is None and self.world_modes.get(world, "isolated") == "isolated":
                stores[world] = StateModel()
            state = stores[world]
            start = state.snapshot()
            clips = self.compile_events(cue, state, tree, idx)
            enter_pic = ((cue.get("script") or {}).get("enter") or {}).get("picture")
            if enter_pic is not None and not any(
                    c.get("kind") in ("picture", "show") and float(c.get("at", 0.0)) <= 1e-6
                    for c in clips):
                clips.insert(0, self.clip("picture", 0.0, 0.0, 0.0, "easeOutCubic",
                                          picture=enter_pic, picture_on=True))
            end = state.snapshot()
            camera = geom.build_camera_frame(self.stages[tree["stage"]], (cue.get("script") or {}).get("camera") or {})
            parent = cue.get("parent")
            if not parent and cues_out:
                parent = cues_out[-1]["id"] if (cue.get("transition") != "world_cut") else None
            cues_out.append({
                "id": cue.get("id"),
                "parent": parent,
                "tree": cue.get("tree"),
                "transition": cue.get("transition", "continue"),
                "duration": round(self.duration(cue, clips), 6),
                "camera": camera,
                "start_state": start,
                "end_state": end,
                "clips": clips,
            })
            prev_world = world

        src_bytes = self.track_path.read_bytes()
        return {
            "schema": schema.COMPILED_TRACK_SCHEMA,
            "source_sha256": hashlib.sha256(src_bytes).hexdigest(),
            "game": self.doc.get("game"),
            "track": self.doc.get("track"),
            "trees": list(self.trees.values()),
            "stages": list(self.compiled_stages.values()),
            "cues": cues_out,
        }

    def duration(self, cue: dict, clips: list) -> float:
        end = 0.0
        for ev in cue.get("events") or []:
            at = float(ev.get("at", 0.0) or 0.0)
            lead = float(ev.get("lead", 0.0) or 0.0)
            dur = float(ev.get("dur", 0.0) or 0.0)
            end = max(end, at + lead + dur)
        return end

    def infer_meta(self, stage: dict, template: str, palette: str, concept: str = "", parts=None):
        """Fill concept/parts/palette from stage template metadata when omitted."""
        if concept and parts:
            return concept, parts, palette
        for t in stage.get("templates") or []:
            if t.get("id") != template:
                continue
            if not concept and t.get("concept"):
                concept = t.get("concept")
            if not parts and t.get("parts"):
                parts = t.get("parts")
            if not palette and t.get("palette"):
                palette = t.get("palette")
            if not concept and t.get("concept_by_palette"):
                for b in t.get("concept_by_palette") or []:
                    if b.get("palette") == palette:
                        concept = b.get("concept", concept)
                        parts = b.get("parts", parts)
                        break
        return concept, parts or [], palette

    def compile_events(self, cue: dict, state: StateModel, tree: dict, idx: int) -> list:
        clips = []
        stage = self.stages[tree["stage"]]
        stage_slots = {z["zone"]: z["slots"] for z in self.compiled_stages[tree["stage"]]["zones"]}
        cue_id = cue.get("id")
        for ev in cue.get("events") or []:
            op = ev.get("op")
            at = float(ev.get("at", 0.0) or 0.0)
            dur = float(ev.get("dur", 0.0) or 0.0)
            lead = float(ev.get("lead", 0.0) or 0.0)
            easing = ev.get("easing") or "easeOutCubic"
            sel = selector_from_event(ev)
            zone = norm(ev.get("zone"))
            if op == "show":
                clips.append(self.clip("picture", at, dur, lead, easing,
                                       picture=ev.get("picture"), picture_on=ev.get("picture") is not None))
            elif op in ("create",):
                tpl = norm(ev.get("template"))
                pal = norm(ev.get("palette"))
                if not tpl:
                    raise ValueError(f"cue {cue_id}: create needs template")
                count = int(ev.get("count", 1) or 1)
                face = face_int(ev.get("to") or "face_down")
                concept, parts, pal = self.infer_meta(stage, tpl, pal, norm(ev.get("concept")), parts_norm(ev.get("parts")))
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts)
                if ev.get("slot") is not None:
                    base = int(ev.get("slot") or 0)
                    for off, it in enumerate(added):
                        state.move_order(it, zone, base + off)
                for it in added:
                    clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "ensure":
                tpl = norm(ev.get("template"))
                pal = norm(ev.get("palette"))
                if not tpl:
                    raise ValueError(f"cue {cue_id}: ensure needs template")
                count = int(ev.get("count", 1) or 1)
                face = face_int(ev.get("to"))
                concept, parts, pal = self.infer_meta(stage, tpl, pal, norm(ev.get("concept")), parts_norm(ev.get("parts")))
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts)
                for it in added:
                    clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "destroy":
                count = int(ev.get("count", 1) or 1)
                victims = state.destroy(zone, sel, count)
                for it in victims:
                    clips.append(self.destroy_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "transfer":
                quantity = int(ev.get("quantity", ev.get("count", 1)) or 1)
                source = norm(ev.get("source"))
                dest = norm(ev.get("destination"))
                records = state.transfer(sel, source, dest, quantity, ev.get("to"), int(ev.get("order", -1)))
                for rec in records:
                    clips.append(self.move_clip(rec, at, dur, lead, easing, stage_slots, ev.get("to")))
            elif op == "stack":
                dest = norm(ev.get("destination"))
                capacity = int(ev.get("capacity", 40) or 40)
                real = [x.strip() for x in norm(ev.get("real_templates")).split(",") if x.strip()]
                pad = norm(ev.get("pad_template"))
                face = face_int(ev.get("to") or "face_down")
                for tpl in real:
                    pal = norm(ev.get("palette"))
                    concept, parts, pal = self.infer_meta(stage, tpl, pal)
                    added = state.spawn(tpl, pal, concept, dest, 1, face, parts)
                    for it in added:
                        clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
                pad_count = max(0, capacity - len(real))
                if pad and pad_count:
                    concept, parts, pal = self.infer_meta(stage, pad, "")
                    added = state.spawn(pad, pal, concept, dest, pad_count, face, parts)
                    for it in added:
                        clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "shuffle":
                state.shuffle(zone, int(ev.get("seed", 1) or 1))
            elif op == "set_face":
                state.set_face(sel, zone, face_int(ev.get("to")))
                for it in state.matching(zone, sel):
                    clips.append(self.face_clip(it, at, dur, lead, easing, ev.get("to")))
            elif op == "move_order":
                arr = state.matching(zone, sel)
                if arr:
                    state.move_order(arr[0], zone, int(ev.get("index", ev.get("order", 0)) or 0))
            elif op == "highlight":
                for it in state.matching(zone, sel):
                    clips.append(self.presentation_clip("highlight", it, at, dur, lead, easing,
                                                        to_scale=float(ev.get("grow", 1.16) or 1.16)))
            elif op == "point":
                arr = state.matching(zone, sel)
                if arr:
                    clips.append(self.presentation_clip("point", arr[0], at, dur, lead, easing,
                                                        part=norm(ev.get("part")), indicator=norm(ev.get("indicator"))))
            elif op == "fade":
                for it in state.matching(zone, sel):
                    clips.append(self.presentation_clip("fade", it, at, dur, lead, easing,
                                                        to_alpha=float(ev.get("to_alpha", ev.get("alpha", 0.0)) or 0.0)))
            elif op == "scale":
                for it in state.matching(zone, sel):
                    clips.append(self.presentation_clip("scale", it, at, dur, lead, easing,
                                                        to_scale=float(ev.get("scale", 1.0) or 1.0)))
            elif op == "wait":
                pass
            else:
                raise ValueError(f"cue {cue_id}: unsupported op {op!r}")
        return clips

    # ── clip builders ─────────────────────────────────────────────────────
    def base_clip(self, kind, at, dur, lead, easing):
        return {
            "kind": kind, "at": at, "dur": dur, "lead": lead, "easing": easing,
            "item_id": "", "template": "", "palette": "",
            "from_zone": "", "from_order": -1, "to_zone": "", "to_order": -1,
            "from_x": 0.0, "from_z": 0.0, "to_x": 0.0, "to_z": 0.0,
            "from_scale": 1.0, "to_scale": 1.0,
            "from_alpha": 1.0, "to_alpha": 1.0,
            "to_face": "", "part": "", "indicator": "",
            "picture": "", "picture_on": False,
        }

    def position(self, slots: dict, zone: str, order: int):
        for s in slots.get(zone, []):
            if int(s.get("order", -1)) == int(order):
                return float(s.get("x", 0.0)), float(s.get("z", 0.0))
        return 0.0, 0.0

    def spawn_clip(self, it, at, dur, lead, easing, slots):
        x, z = self.position(slots, it["zone"], it["order"])
        c = self.base_clip("spawn", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "to_zone": it["zone"], "to_order": it["order"],
            "from_zone": it["zone"], "from_order": it["order"],
            "from_x": x, "from_z": z, "to_x": x, "to_z": z,
            "to_face": face_name(it["face"]),
        })
        return c

    def destroy_clip(self, it, at, dur, lead, easing, slots):
        x, z = self.position(slots, it["zone"], it["order"])
        c = self.base_clip("destroy", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "from_zone": it["zone"], "from_order": it["order"], "to_zone": it["zone"], "to_order": it["order"],
            "from_x": x, "from_z": z, "to_x": x, "to_z": z,
        })
        return c

    def move_clip(self, rec, at, dur, lead, easing, slots, to_face):
        it = rec["item"]
        fx, fz = self.position(slots, rec["from_zone"], rec["from_order"])
        tx, tz = self.position(slots, rec["to_zone"], rec["to_order"])
        c = self.base_clip("move", at, dur, lead, easing)
        c.update({
            "item_id": it["id"], "template": it["template"], "palette": it["palette"],
            "from_zone": rec["from_zone"], "from_order": rec["from_order"],
            "to_zone": rec["to_zone"], "to_order": rec["to_order"],
            "from_x": fx, "from_z": fz, "to_x": tx, "to_z": tz,
        })
        if to_face:
            c["to_face"] = face_name(face_int(to_face))
        return c

    def face_clip(self, it, at, dur, lead, easing, to_face):
        c = self.base_clip("face", at, dur, lead, easing)
        c.update({"item_id": it["id"], "template": it["template"], "palette": it["palette"],
                  "to_face": face_name(face_int(to_face))})
        return c

    def presentation_clip(self, kind, it, at, dur, lead, easing, **kw):
        c = self.base_clip(kind, at, dur, lead, easing)
        c.update({"item_id": it["id"], "template": it["template"], "palette": it["palette"]})
        for k, v in kw.items():
            if k in c:
                c[k] = v
        return c

    def clip(self, kind, at, dur, lead, easing, **kw):
        c = self.base_clip(kind, at, dur, lead, easing)
        for k, v in kw.items():
            if k in c:
                c[k] = v
        return c


def source_path(game: str, track: str, explicit: str | None = None) -> Path:
    if explicit:
        return Path(explicit)
    return ROOT / "games" / game / "tutorial" / "anim" / "v2" / f"{track}.anim.json"


def output_path(src: Path) -> Path:
    return src.with_name(src.name.replace(".anim.json", ".compiled.json"))


def json_canonical(doc) -> str:
    return json.dumps(doc, ensure_ascii=False, indent=2, sort_keys=False) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="_schema_example")
    ap.add_argument("--source")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--stdout", action="store_true")
    a = ap.parse_args()

    src = source_path(a.game, a.track, a.source)
    if not src.exists():
        print(f"source not found: {src}", file=sys.stderr)
        return 2
    try:
        c = Compiler(src)
        compiled = c.compile()
    except Exception as e:
        print(f"COMPILE FAIL {src}: {e}", file=sys.stderr)
        return 1

    text = json_canonical(compiled)
    out = output_path(src)
    if a.stdout:
        print(text, end="")
        return 0
    if a.check:
        if not out.exists():
            print(f"CHECK FAIL missing compiled output: {out}", file=sys.stderr)
            return 1
        old = json.loads(out.read_text(encoding="utf-8"))
        if old != compiled:
            print(f"CHECK FAIL compiled output is stale: {out}", file=sys.stderr)
            return 1
        print(f"OK   {out} is up to date")
        return 0
    out.write_text(text, encoding="utf-8")
    print(f"OK   {src} -> {out} ({len(compiled['cues'])} cues, {len(compiled['stages'])} stages)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
