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
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import anim_geometry_v2 as geom  # noqa: E402
import anim_schema_v2 as schema  # noqa: E402


# Shuffle feel: all decks share these constants.  Kept in the compiler so the
# compiled clip carries deterministic per-item parameters and the Unity runtime
# does not invent motion on its own.
SHUFFLE_AMP_MIN = 0.040
SHUFFLE_AMP_MAX = 0.062
SHUFFLE_FREQ_MIN = 8.0
SHUFFLE_FREQ_MAX = 14.0
SHUFFLE_DEPTH_MIN = 0.25
SHUFFLE_DEPTH_MAX = 0.60
SHUFFLE_ENVELOPE_POWER = 0.45


# ── helpers ────────────────────────────────────────────────────────────────

def norm(s) -> str:
    return (str(s or "")).strip()


def norm_text(s) -> str:
    """Whitespace-insensitive text used only for beat -> subtitle matching."""
    return "".join((str(s or "")).split())


def key_text(s) -> str:
    """Punctuation/whitespace-insensitive key for beat -> TTS word matching."""
    return "".join(ch for ch in str(s or "") if ch.isalnum())


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

    def __init__(self, stack_zones=None):
        self.items = []
        self.next_seq = {}
        # Stack-style zones keep the old pile convention: append goes underneath
        # (order 0 = top).  Other zones default new items on top.
        self.stack_zones = set(stack_zones or [])

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
                "Layer": int(it.get("layer", 0) or 0),
                "Face": int(it["face"]),
            })
        return {"components": comps,
                "nextSeq": [{"key": k, "value": v} for k, v in sorted(self.next_seq.items())]}

    def component_map(self) -> dict:
        """id -> full concrete component dict (matches ComponentState)."""
        out = {}
        for it in self.items:
            out[it["id"]] = {
                "Id": it["id"],
                "TemplateId": it["template"],
                "Palette": it["palette"],
                "Concept": it["concept"],
                "parts": copy.deepcopy(it.get("parts") or []),
                "ZoneId": it["zone"],
                "Order": int(it["order"]),
                "Layer": int(it.get("layer", 0) or 0),
                "Face": int(it["face"]),
            }
        return out

    def load_snapshot(self, snap: dict):
        """Initialize from a compiled StateSnapshot (cue entry state)."""
        self.items = []
        self.next_seq = {}
        for comp in (snap or {}).get("components") or []:
            self.items.append({
                "id": comp.get("Id"),
                "template": comp.get("TemplateId"),
                "palette": comp.get("Palette"),
                "concept": comp.get("Concept"),
                "parts": copy.deepcopy(comp.get("parts") or []),
                "zone": comp.get("ZoneId"),
                "order": int(comp.get("Order", 0) or 0),
                "layer": int(comp.get("Layer", 0) or 0),
                "face": int(comp.get("Face", 2) or 2),
            })
        for kv in (snap or {}).get("nextSeq") or []:
            self.next_seq[kv.get("key")] = int(kv.get("value", 0) or 0)
        return self

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
        # Keep legacy selection order (stable address) so scripts continue to
        # pick the same concrete items; layer only controls cover/overlap order.
        out.sort(key=lambda x: (x["order"], x["id"]))
        return out

    def count(self, zone: str, selector: dict = None) -> int:
        return len(self.matching(zone, selector or {}))

    def _next_order(self, zone: str) -> int:
        """Default append position: max(order)+1, never based on count.

        Removal leaves holes; only an explicit move_order event may close them.
        """
        orders = [i["order"] for i in self.items if i["zone"] == zone]
        return (max(orders) + 1) if orders else 0

    def _zone_layers(self, zone: str) -> list:
        return [int(i.get("layer", 0) or 0) for i in self.items if i["zone"] == zone]

    def _default_layer(self, zone: str) -> int:
        """Layer for a new item entering this zone.

        General zones: later item goes on top (max + 1).
        Stack zones: append goes underneath, matching the existing order-0-top
        pile convention used by decks and supply heaps.
        """
        layers = self._zone_layers(zone)
        if not layers:
            return 0
        if zone in self.stack_zones:
            return min(layers) - 1
        return max(layers) + 1

    def _resolve_layer(self, zone: str, raw, ordinal: int = 0) -> int:
        if raw is None or str(raw).strip() == "":
            return self._default_layer(zone)
        if isinstance(raw, bool):
            raise ValueError(f"layer must be int/top/bottom, got {raw!r}")
        if isinstance(raw, (int, float)):
            return int(raw) + ordinal
        text = str(raw).strip().lower()
        if text in ("top",):
            layers = self._zone_layers(zone)
            return max(layers) + 1 if layers else 0
        if text in ("default",):
            return self._default_layer(zone)
        if text in ("bottom", "below"):
            layers = self._zone_layers(zone)
            return min(layers) - 1 if layers else 0
        try:
            return int(text) + ordinal
        except ValueError:
            raise ValueError(f"layer must be int/top/bottom, got {raw!r}") from None

    def spawn(self, template: str, palette: str, concept: str, zone: str, count: int,
              face: int = 2, parts=None, layer=None) -> list:
        if count <= 0:
            return []
        if not zone:
            raise ValueError("spawn zone required")
        added = []
        for idx in range(count):
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
                "order": self._next_order(zone),
                "layer": self._resolve_layer(zone, layer, idx),
                "face": int(face),
            }
            self.items.append(it)
            added.append(it)
        return added

    def ensure_at_least(self, template: str, palette: str, concept: str, zone: str, count: int,
                        face: int = 2, parts=None, layer=None) -> list:
        sel = {"template": template, "palette": palette, "concept": concept, "parts": parts or []}
        have = self.count(zone, sel)
        return self.spawn(template, palette, concept, zone, max(0, count - have), face, parts, layer)

    def destroy(self, zone: str, selector: dict, count: int, from_back: bool = False) -> list:
        arr = self.matching(zone, selector)
        if count <= 0:
            count = len(arr)
        if len(arr) < count:
            raise ValueError(f"destroy needs {count} in {zone}, have {len(arr)}")
        victims = arr[-count:] if from_back else arr[:count]
        ids = {v["id"] for v in victims}
        self.items = [i for i in self.items if i["id"] not in ids]
        return victims

    def transfer(self, selector: dict, source: str, dest: str, quantity: int,
                 to_face=None, order: int = -1, layer=None) -> list:
        arr = self.matching(source, selector)
        if len(arr) < quantity:
            raise ValueError(f"transfer needs {quantity} from {source}, have {len(arr)}")
        moved = arr[:quantity]
        moved_ids = {m["id"] for m in moved}
        records = []
        for it in moved:
            rec = {"item": it, "from_zone": it["zone"], "from_order": it["order"],
                   "from_layer": int(it.get("layer", 0) or 0)}
            records.append(rec)
        self.items = [i for i in self.items if i["id"] not in moved_ids]
        for idx, rec in enumerate(records):
            it = rec["item"]
            it["zone"] = dest
            it["order"] = self._next_order(dest)
            it["layer"] = self._resolve_layer(dest, layer, idx)
            if to_face is not None:
                it["face"] = face_int(to_face)
            self.items.append(it)
            rec["to_zone"] = dest
            rec["to_order"] = it["order"]
            rec["to_layer"] = int(it["layer"])
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


def hash01(s: str, salt: int) -> float:
    """Deterministic [0,1) value; replay and compiled clips always agree."""
    return fnv32(s, salt) / 4294967296.0


# ── compiler ────────────────────────────────────────────────────────────────

class Compiler:
    def __init__(self, track_path: Path):
        self.track_path = track_path
        self.track_dir = track_path.parent
        self.doc = schema.resolve_track(json.loads(track_path.read_text(encoding="utf-8")))
        self.rep = schema.validate_track(self.doc)
        self.stages = {}       # stage id -> source stage
        self.compiled_stages = {}
        self.trees = {}
        self.world_modes = {w.get("id"): w.get("mode", "isolated") for w in (self.doc.get("worlds") or [])}
        self.stack_zones = set()
        self.time_anchors = self._resolve_time_anchors()

    def _resolve_time_anchors(self) -> dict:
        """Resolve symbolic time anchors to cue-local seconds.

        Anchor definitions live at the top of the track script, next to worlds.
        Times are resolved from script beats + TTS subtitles, never from raw
        seconds written at the event site.
        """
        anchors = self.doc.get("time_anchors") or []
        if not anchors:
            return {}
        game = norm(self.doc.get("game"))
        track = norm(self.doc.get("track"))
        script_path = ROOT / "games" / game / "tutorial" / f"script.{track}.json"
        runtime_path = ROOT / "games" / game / "tutorial" / f"{track}.runtime.json"
        if not script_path.exists():
            raise FileNotFoundError(f"time_anchors: missing narration script {script_path}")
        if not runtime_path.exists():
            raise FileNotFoundError(f"time_anchors: missing runtime timing {runtime_path}")

        script_doc = json.loads(script_path.read_text(encoding="utf-8"))
        runtime_doc = json.loads(runtime_path.read_text(encoding="utf-8"))
        self.beat_times = self._build_beat_times(script_doc, runtime_doc)
        runtime_cues = {c.get("id"): c for c in (runtime_doc.get("cues") or []) if c.get("id")}

        out = {}
        for a in anchors:
            if not isinstance(a, dict) or not a.get("id"):
                raise ValueError(f"time_anchors: anchor needs id: {a!r}")
            aid = norm(a.get("id"))
            cue_id = norm(a.get("cue"))
            edge = norm(a.get("edge")) or "start"
            offset = float(a.get("offset", 0.0) or 0.0)
            runtime_cue = runtime_cues.get(cue_id)
            if runtime_cue is None:
                raise ValueError(f"time_anchor {aid!r}: unknown cue {cue_id!r}")
            if edge == "cue_start":
                base = 0.0
            elif edge == "cue_end":
                base = float(runtime_cue.get("duration", 0.0) or 0.0)
            else:
                beat_id = norm(a.get("beat"))
                beat_time = self.beat_times.get((cue_id, beat_id))
                if beat_time is None:
                    raise ValueError(f"time_anchor {aid!r}: unknown beat {cue_id}/{beat_id}")
                base = beat_time[0] if edge == "start" else beat_time[1]
            out[aid] = max(0.0, base + offset)
        return out

    @staticmethod
    def _build_beat_times(script_doc: dict, runtime_doc: dict) -> dict:
        """(cue_id, beat_id) -> (start_seconds, end_seconds).

        TTS subtitle events may split one written beat into several pieces, so we
        match against the flattened word stream instead of requiring an exact
        subtitle-event equality.
        """
        runtime_cues = {c.get("id"): c for c in (runtime_doc.get("cues") or []) if c.get("id")}
        out = {}
        for c in script_doc.get("cues") or []:
            cid = c.get("id")
            rt = runtime_cues.get(cid)
            if not cid or not rt:
                continue
            words = []
            for sub in rt.get("subtitles") or []:
                for w in sub.get("words") or []:
                    if w:
                        words.append(w)
            if not words:
                continue
            flat = "".join(key_text(w.get("word", "")) for w in words)
            char_word = []
            for wi, w in enumerate(words):
                for _ in key_text(w.get("word", "")):
                    char_word.append(wi)
            cursor = 0
            for b in c.get("beats") or []:
                bid = b.get("id")
                bkey = key_text(b.get("text"))
                if not bid or not bkey:
                    continue
                pos = flat.find(bkey, cursor)
                if pos < 0:
                    pos = flat.find(bkey)
                if pos < 0:
                    raise ValueError(f"beat text not found in TTS words: {cid}/{bid} {b.get('text')!r}")
                si = char_word[pos]
                ei = char_word[pos + len(bkey) - 1]
                out[(cid, bid)] = (
                    float(words[si].get("start", 0.0) or 0.0),
                    float(words[ei].get("end", words[ei].get("start", 0.0)) or 0.0),
                )
                cursor = pos + len(bkey)
        return out

    def event_at(self, ev: dict, cue_id=None) -> float:
        aid = norm(ev.get("anchor"))
        if not aid:
            return round(float(ev.get("at", 0.0) or 0.0), 6)
        if aid not in self.time_anchors:
            raise ValueError(f"cue {cue_id}: unknown time anchor {aid!r}")
        offset = float(ev.get("offset", 0.0) or 0.0)
        return round(max(0.0, self.time_anchors[aid] + offset), 6)

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
            for z in stage.get("zones") or []:
                if ((z.get("display") or {}).get("mode") == "stack") and z.get("id"):
                    self.stack_zones.add(z["id"])
            self.trees[tree["id"]] = {
                "id": tree["id"],
                "world": tree.get("world") or tree["id"],
                "stage": sid,
                "purpose": tree.get("purpose", ""),
                "initial": tree.get("initial", ""),
                "extent_note": tree.get("extent_note", ""),
            }

    def shot_frame(self, stage_id: str, shot_id: str, visible_zones: set | None = None) -> dict:
        stage = self.stages[stage_id]
        for sh in stage.get("shots") or []:
            if sh.get("id") == shot_id:
                zones = sh.get("zones") or []
                # "*" 全景：动态收窄到当前真的有组件的 zone；
                # 只有没有可见 zone 时才退回全部 zone。这样空玩家区不会
                # 把设置完成前的桌面中景硬拉成整桌远景。
                if "*" in zones and visible_zones and not sh.get("static"):
                    stage_ids = {z.get("id") for z in (stage.get("zones") or [])}
                    live = [z for z in visible_zones if z in stage_ids]
                    if live:
                        zones = live
                return geom.build_camera_frame(stage, {
                    "zones": zones,
                    "fill": sh.get("fill", 0.8),
                    "at": 0.0,
                })
        raise ValueError(f"stage {stage_id}: unknown shot {shot_id!r}")

    @staticmethod
    def visible_zone_set(state: StateModel) -> set:
        return {it.get("zone") for it in state.items if it.get("zone")}

    def default_camera_frame(self, stage_id: str) -> dict:
        stage = self.stages[stage_id]
        shots = stage.get("shots") or []
        if shots:
            return self.shot_frame(stage_id, shots[0]["id"])
        return geom.build_camera_frame(stage, {})

    def entry_ref(self, cue: dict, tree: dict, by_id: dict) -> tuple:
        """Return ("initial", None) or ("cue", id) for this cue's entry state."""
        entry = cue.get("entry")
        if entry:
            return ("initial", None) if entry == "initial" else ("cue", entry)
        transition = cue.get("transition", "continue")
        parent_id = cue.get("parent")
        if parent_id and parent_id in by_id and transition not in ("cut", "world_cut"):
            parent = by_id[parent_id]
            parent_tree = self.trees.get(parent.get("tree"))
            if parent_tree and parent_tree.get("world") == tree.get("world"):
                return ("cue", parent_id)
        return ("initial", None)

    def compile(self) -> dict:
        self.load()
        cues = self.doc.get("cues") or []
        by_id = {c.get("id"): c for c in cues if c.get("id")}
        results = {}
        visiting = set()

        def evaluate(cid: str) -> dict:
            if cid in results:
                return results[cid]
            if cid in visiting:
                raise ValueError(f"cue entry cycle at {cid!r}")
            cue = by_id.get(cid)
            if cue is None:
                raise ValueError(f"unknown cue in entry graph: {cid!r}")
            tree = self.trees.get(cue.get("tree"))
            if tree is None:
                raise ValueError(f"cue {cid}: unknown tree {cue.get('tree')!r}")
            visiting.add(cid)
            transition = cue.get("transition", "continue")
            mode, entry_id = self.entry_ref(cue, tree, by_id)
            if mode == "cue":
                entry_res = evaluate(entry_id)
                parent_decl = by_id.get(entry_id) or {}
                # 反例 cue 的错误状态不能泄漏：子节点继承它的 start_state。
                entry_state = copy.deepcopy(
                    entry_res["start_state"] if parent_decl.get("negative") else entry_res["end_state"]
                )
                entry_stage = entry_res.get("_stage")
                entry_camera_out = copy.deepcopy(entry_res.get("_camera_out"))
            else:
                entry_state = {"components": [], "nextSeq": []}
                entry_stage = None
                entry_camera_out = None

            state = StateModel(self.stack_zones).load_snapshot(entry_state)
            start = state.snapshot()
            clips, state_ops, camera_ops, first_state = self.compile_events(cue, state, tree, 0)
            if first_state is None:
                first_state = start
            enter_pic = ((cue.get("script") or {}).get("enter") or {}).get("picture")
            if enter_pic is not None and not any(
                    c.get("kind") in ("picture", "show") and float(c.get("at", 0.0)) <= 1e-6
                    for c in clips):
                clips.insert(0, self.clip("picture", 0.0, 0.0, 0.0, "easeOutCubic",
                                          picture=enter_pic, picture_on=True))
            end = state.snapshot()
            stage_id = tree["stage"]
            if (mode == "cue" and transition not in ("cut", "world_cut")
                    and entry_stage == stage_id):
                camera_in = copy.deepcopy(entry_camera_out) or self.default_camera_frame(stage_id)
            else:
                camera_in = self.default_camera_frame(stage_id)
            camera_out = camera_ops[-1]["frame"] if camera_ops else camera_in
            result = {
                "id": cue.get("id"),
                "tree": cue.get("tree"),
                "transition": transition,
                "duration": round(self.duration(cue, clips), 6),
                "camera_in": camera_in,
                "camera_ops": camera_ops,
                "state_ops": state_ops,
                "start_state": start,
                "first_state": first_state,
                "end_state": end,
                "clips": clips,
                "_stage": stage_id,
                "_camera_out": camera_out,
            }
            results[cid] = result
            visiting.discard(cid)
            return result

        for cue in cues:
            if cue.get("id"):
                evaluate(cue["id"])

        cues_out = []
        for idx, cue in enumerate(cues):
            cid = cue.get("id")
            if cid not in results:
                continue
            result = {k: v for k, v in results[cid].items() if not k.startswith("_")}
            parent = cue.get("parent")
            if not parent and idx > 0 and cue.get("transition") != "world_cut":
                parent = cues[idx - 1].get("id")
            result["parent"] = parent
            # Keep the schema's canonical field order close to the old output.
            ordered = {
                "id": result.pop("id"),
                "parent": result.pop("parent"),
                "tree": result.pop("tree"),
                "transition": result.pop("transition"),
                "duration": result.pop("duration"),
                "camera_in": result.pop("camera_in"),
                "camera_ops": result.pop("camera_ops"),
                "state_ops": result.pop("state_ops"),
                "start_state": result.pop("start_state"),
                "first_state": result.pop("first_state"),
                "end_state": result.pop("end_state"),
                "clips": result.pop("clips"),
            }
            cues_out.append(ordered)

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
            at = self.event_at(ev, cue.get("id"))
            lead = float(ev.get("lead", 0.0) or 0.0)
            dur = float(ev.get("dur", 0.0) or 0.0)
            end = max(end, at + lead + dur)
        # Staggered transfers move the per-record start into clips[].at;
        # the cue duration must cover the actual node timeline.
        for cl in clips or []:
            at = float(cl.get("at", 0.0) or 0.0)
            lead = max(0.0, float(cl.get("lead", 0.0) or 0.0))
            dur = max(0.0, float(cl.get("dur", 0.0) or 0.0))
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

    @staticmethod
    def select_items(state: StateModel, zone: str, selector: dict, order, limit=None):
        arr = state.matching(zone, selector)
        if order is not None:
            arr = [it for it in arr if int(it.get("order", -1)) == int(order)]
        if limit is not None:
            arr = arr[:limit]
        return arr

    def compile_events(self, cue: dict, state: StateModel, tree: dict, idx: int) -> tuple:
        """Returns (clips, state_ops, camera_ops, first_state).

        * state_ops are concrete item-id puts/removes: the logical truth the
          runtime applies before its visual clips.
        * camera_ops are concrete compiled camera frames at their switch times.
        * first_state is the logical state after every op whose effective time
          is <= 0.
        """
        clips: list = []
        state_ops: list = []
        camera_ops: list = []
        first_state = None
        stage_id = tree["stage"]
        stage = self.stages[stage_id]
        stage_slots = {z["zone"]: z["slots"] for z in self.compiled_stages[stage_id]["zones"]}
        cue_id = cue.get("id")
        for ev in cue.get("events") or []:
            op = ev.get("op")
            at = self.event_at(ev, cue_id)
            dur = float(ev.get("dur", 0.0) or 0.0)
            lead = float(ev.get("lead", 0.0) or 0.0)
            easing = ev.get("easing") or "easeOutCubic"
            sel = selector_from_event(ev)
            zone = norm(ev.get("zone"))
            before = state.component_map()
            manual_state_ops = None
            manual_state_item_ids = set()
            affected_ids = []
            forbid_at = None
            if op == "camera":
                shot_id = norm(ev.get("shot"))
                if not shot_id:
                    raise ValueError(f"cue {cue_id}: camera needs shot")
                frame = self.shot_frame(stage_id, shot_id, visible_zones=self.visible_zone_set(state))
                camera_ops.append({
                    "at": at + max(0.0, lead),
                    "dur": dur,
                    "easing": easing,
                    "shot": shot_id,
                    "frame": frame,
                })
            elif op == "show":
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
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts, ev.get("layer"))
                affected_ids = [it["id"] for it in added]
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
                added = state.ensure_at_least(tpl, pal, concept, zone, count, face, parts, ev.get("layer"))
                affected_ids = [it["id"] for it in added]
                for it in added:
                    clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "destroy":
                count = int(ev.get("count", 0) or 0)
                victims = state.destroy(zone, sel, count, from_back=bool(ev.get("from_back")))
                affected_ids = [it["id"] for it in victims]
                for it in victims:
                    clips.append(self.destroy_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "transfer":
                quantity = int(ev.get("quantity", ev.get("count", 1)) or 1)
                raw_src = ev.get("source")
                sources = raw_src if isinstance(raw_src, list) else [raw_src]
                sources = [norm(x) for x in sources if norm(x)]
                dest = norm(ev.get("destination"))
                stagger = float(ev.get("stagger", 0.0) or 0.0)
                index = 0
                records_with_times = []
                for source in sources:
                    records = state.transfer(sel, source, dest, quantity, ev.get("to"), int(ev.get("order", -1)), ev.get("layer"))
                    for rec in records:
                        # 一个 transfer record = 一个节点：逻辑转移与视觉飞行共用同一个 at。
                        record_at = at + max(0.0, lead) + index * stagger
                        clips.append(self.move_clip(rec, record_at, dur, 0.0, easing, stage_slots, ev.get("to")))
                        records_with_times.append((record_at, rec["item"]["id"]))
                        manual_state_item_ids.add(rec["item"]["id"])
                        index += 1
                if records_with_times:
                    after_map = state.component_map()
                    manual_state_ops = [
                        {"op": "put", "at": record_at, "item": after_map[item_id]}
                        for record_at, item_id in records_with_times
                        if item_id in after_map
                    ]
                    affected_ids = [item_id for _, item_id in records_with_times]
                    if ev.get("forbid"):
                        last_start = at + max(0.0, lead) + max(0, len(records_with_times) - 1) * stagger
                        forbid_at = last_start + max(0.0, dur)
            elif op == "stack":
                dest = norm(ev.get("destination"))
                capacity = int(ev.get("capacity", 40) or 40)
                real = [x.strip() for x in norm(ev.get("real_templates")).split(",") if x.strip()]
                pad = norm(ev.get("pad_template"))
                face = face_int(ev.get("to") or "face_down")
                pad_count = max(0, capacity - len(real)) if pad else 0
                total = len(real) + pad_count
                # Keep current pile semantics: first real card remains the top card.
                # Layer is cover order, so assign it in reverse build order.
                layer_cursor = total - 1
                for tpl in real:
                    pal = norm(ev.get("palette"))
                    concept, parts, pal = self.infer_meta(stage, tpl, pal)
                    added = state.spawn(tpl, pal, concept, dest, 1, face, parts, layer_cursor)
                    layer_cursor -= 1
                    for it in added:
                        clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
                if pad and pad_count:
                    concept, parts, pal = self.infer_meta(stage, pad, "")
                    for _ in range(pad_count):
                        added = state.spawn(pad, pal, concept, dest, 1, face, parts, layer_cursor)
                        layer_cursor -= 1
                        for it in added:
                            clips.append(self.spawn_clip(it, at, dur, lead, easing, stage_slots))
            elif op == "shuffle":
                # In-place jitter, exactly like v1 — and **visual only**.
                # The decks are built with their real cards already on top
                # (order 0..N), then padded, and the scripted market deal picks
                # those cards by template/parts.  Permuting the logical order
                # here would make a scripted card start its flight from the
                # middle/bottom of the pile, which is exactly the v1 behaviour
                # this animation was built around.  So the state model is left
                # untouched; only the pile edge gets the deterministic shake.
                strength = float(ev.get("amount", ev.get("strength", 1.0)) or 1.0)
                for it in state.matching(zone, {}):
                    bx, bz = self.position(stage_slots, zone, it["order"])
                    r1 = hash01(it["id"], 1)
                    r2 = hash01(it["id"], 2)
                    r3 = hash01(it["id"], 3)
                    r4 = hash01(it["id"], 4)
                    amp = (SHUFFLE_AMP_MIN + (SHUFFLE_AMP_MAX - SHUFFLE_AMP_MIN) * r1) * strength
                    c = self.base_clip("shuffle", at, dur, lead, easing)
                    c.update({
                        "item_id": it["id"], "template": it["template"], "palette": it["palette"],
                        "from_zone": zone, "from_order": it["order"],
                        "to_zone": zone, "to_order": it["order"],
                        "from_x": bx, "from_z": bz, "to_x": bx, "to_z": bz,
                        "sh_amp": amp,
                        "sh_freq": SHUFFLE_FREQ_MIN + (SHUFFLE_FREQ_MAX - SHUFFLE_FREQ_MIN) * r2,
                        "sh_phase": r3 * 2.0 * math.pi,
                        "sh_zamp": amp * (SHUFFLE_DEPTH_MIN + (SHUFFLE_DEPTH_MAX - SHUFFLE_DEPTH_MIN) * r4),
                        "sh_env": SHUFFLE_ENVELOPE_POWER,
                    })
                    clips.append(c)
            elif op == "set_face":
                state.set_face(sel, zone, face_int(ev.get("to")))
                affected = state.matching(zone, sel)
                affected_ids = [it["id"] for it in affected]
                for it in affected:
                    clips.append(self.face_clip(it, at, dur, lead, easing, ev.get("to")))
            elif op == "move_order":
                arr = state.matching(zone, sel)
                if arr:
                    state.move_order(arr[0], zone, int(ev.get("index", ev.get("order", 0)) or 0))
            elif op == "highlight":
                arr = self.select_items(state, zone, sel, ev.get("order"))
                for it in arr:
                    clips.append(self.presentation_clip("highlight", it, at, dur, lead, easing,
                                                        to_scale=float(ev.get("grow", 1.16) or 1.16)))
            elif op == "point":
                arr = self.select_items(state, zone, sel, ev.get("order"), limit=1)
                if arr:
                    clips.append(self.presentation_clip("point", arr[0], at, dur, lead, easing,
                                                        part=norm(ev.get("part")), indicator=norm(ev.get("indicator"))))
            elif op == "label":
                overlay_id = norm(ev.get("overlay"))
                if not overlay_id:
                    raise ValueError(f"cue {cue_id}: label needs overlay")
                overlays = {o.get("id"): o for o in (stage.get("overlays") or [])
                            if isinstance(o, dict) and o.get("id")}
                overlay = overlays.get(overlay_id)
                if overlay is None:
                    raise ValueError(f"cue {cue_id}: unknown overlay {overlay_id!r}")
                space = norm(overlay.get("space") or "screen").lower()
                c = self.base_clip("label", at, dur, lead, easing)
                c.update({
                    "overlay": overlay_id,
                    "text": str(ev.get("text") or ""),
                })
                if space == "world":
                    center = overlay.get("center") or {}
                    c.update({
                        "screen_space": False,
                        "label_x": float(center.get("x", 0.0) or 0.0),
                        "label_y": 0.0,
                        "label_w": 0.0,
                        "label_h": 0.0,
                    })
                else:
                    rect = overlay.get("rect") or {}
                    c.update({
                        "screen_space": True,
                        "label_x": float(rect.get("x", 0.0) or 0.0),
                        "label_y": float(rect.get("y", 0.0) or 0.0),
                        "label_w": float(rect.get("w", 0.3) or 0.3),
                        "label_h": float(rect.get("h", 0.1) or 0.1),
                    })
                clips.append(c)
            elif op == "fade":
                for it in self.select_items(state, zone, sel, ev.get("order")):
                    clips.append(self.presentation_clip("fade", it, at, dur, lead, easing,
                                                        to_alpha=float(ev.get("to_alpha", ev.get("alpha", 0.0)) or 0.0)))
            elif op == "scale":
                for it in self.select_items(state, zone, sel, ev.get("order")):
                    clips.append(self.presentation_clip("scale", it, at, dur, lead, easing,
                                                        to_scale=float(ev.get("scale", 1.0) or 1.0)))
            elif op == "wait":
                pass
            else:
                raise ValueError(f"cue {cue_id}: unsupported op {op!r}")

            # Effective time of this event's state step.  Spawn/create/move/
            # destroy logical changes happen when the visual action starts
            # (destroy with dur>0 leaves the item until the end).
            op_time = at + max(0.0, lead)
            if op == "destroy" and dur > 0:
                op_time += dur
            if op != "camera":
                after = state.component_map()
                handled_ids = manual_state_item_ids if manual_state_ops is not None else set()
                for iid, comp in after.items():
                    if iid in handled_ids:
                        continue
                    if before.get(iid) != comp:
                        state_ops.append({"op": "put", "at": op_time, "item": comp})
                for iid in sorted(set(before) - set(after)):
                    if iid in handled_ids:
                        continue
                    state_ops.append({"op": "remove", "at": op_time, "item_id": iid})
                if manual_state_ops:
                    state_ops.extend(manual_state_ops)
            if ev.get("forbid"):
                if forbid_at is None:
                    forbid_at = at + max(0.0, lead) + max(0.0, dur)
                indicator = ev.get("forbid") if isinstance(ev.get("forbid"), str) else "forbid"
                mx, mz, mr = self.marker_geometry(stage_id, stage_slots, state, affected_ids)
                clips.append(self.marker_clip(forbid_at, indicator or "forbid", mx, mz, mr))
            if op_time <= 1e-9:
                first_state = state.snapshot()

        # Stable chronological order; camera ops are already naturally ordered
        # but explicit sorting keeps the runtime/evaluator independent of source
        # event ordering edge cases.
        # Stable chronological order.  Python's sort is stable, so same-time
        # ops keep source event order (important for conflicting ops).
        state_ops.sort(key=lambda x: x["at"])
        camera_ops.sort(key=lambda x: x["at"])
        return clips, state_ops, camera_ops, first_state

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
            "sh_amp": 0.0, "sh_freq": 0.0, "sh_phase": 0.0, "sh_zamp": 0.0, "sh_env": 0.0,
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

    def template_radius(self, stage_id: str, template: str) -> float:
        stage = self.compiled_stages.get(stage_id) or {}
        for tpl in stage.get("templates") or []:
            if tpl.get("id") != template:
                continue
            w = float(tpl.get("width", 0.0) or 0.0)
            h = float(tpl.get("height", 0.0) or 0.0)
            if w > 0.0 and h > 0.0:
                return min(w, h) * 0.5
            ws = float(tpl.get("world_size", 0.0) or 0.0)
            if ws > 0.0:
                return ws * 0.5
        return 0.12

    def marker_geometry(self, stage_id: str, slots: dict, state: StateModel, item_ids: list) -> tuple:
        """Average position / bounding radius of the components affected by an action."""
        unique_ids = list(dict.fromkeys(item_ids))
        if not unique_ids:
            raise ValueError("forbid marker needs at least one affected item")
        by_id = {it["id"]: it for it in state.items}
        points = []
        for iid in unique_ids:
            it = by_id.get(iid)
            if it is None:
                raise ValueError(f"forbid marker: affected item {iid!r} no longer exists after action")
            x, z = self.position(slots, it["zone"], int(it["order"]))
            points.append((x, z, self.template_radius(stage_id, it["template"])))
        ax = sum(p[0] for p in points) / len(points)
        az = sum(p[1] for p in points) / len(points)
        spread = max(math.hypot(p[0] - ax, p[1] - az) for p in points)
        item_r = max(p[2] for p in points)
        return (round(ax, 6), round(az, 6), round(max(0.12, spread + item_r), 6))

    def marker_clip(self, at: float, indicator: str, x: float, z: float, radius: float):
        c = self.base_clip("marker", at, 0.0, 0.0, "linear")
        c.update({
            "indicator": indicator or "forbid",
            "marker_x": x,
            "marker_z": z,
            "marker_radius": radius,
        })
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
    # Compiled assets are machine-read by Unity; keep them compact to avoid
    # multi-megabyte pretty-printed snapshots.
    return json.dumps(doc, ensure_ascii=False, separators=(",", ":"), sort_keys=False) + "\n"


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
