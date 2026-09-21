#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Linked-list style editor for v2 animation cue graphs.

This operates on the *structure* of a track: list order, parent and entry
pointers.  It deliberately does not rewrite scripts or TTS assets; those are
separate layers.  The operations are designed so a cue's state/camera remains
a pure function of (entry, own events), independent of unrelated neighbours.

Commands:
    python scripts/cue_graph_v2.py insert  --source full.anim.json \
        --after A --new B --cue-file new_cue.json
    python scripts/cue_graph_v2.py delete  --source full.anim.json --cue B
    python scripts/cue_graph_v2.py split   --source full.anim.json \
        --cue A --at 1.5 --new A.s2
    python scripts/cue_graph_v2.py merge   --source full.anim.json \
        --first A --second A.s2
"""
from __future__ import annotations

import argparse
import copy
import json
import sys
from pathlib import Path


class CueGraphError(Exception):
    pass


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, doc):
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def event_start(ev: dict) -> float:
    return float(ev.get("at", 0.0) or 0.0)


def event_end(ev: dict) -> float:
    return float(ev.get("at", 0.0) or 0.0) + float(ev.get("dur", 0.0) or 0.0)


def cue_end(cue: dict) -> float:
    return max([0.0] + [event_end(e) for e in (cue.get("events") or [])])


class CueGraph:
    def __init__(self, doc: dict):
        if not isinstance(doc.get("cues"), list):
            raise CueGraphError("track has no cues[]")
        self.doc = doc

    @classmethod
    def from_file(cls, path: Path):
        return cls(load_json(path))

    def save(self, path: Path):
        save_json(path, self.doc)

    @property
    def cues(self) -> list:
        return self.doc["cues"]

    def index(self, cue_id: str) -> int:
        for i, c in enumerate(self.cues):
            if c.get("id") == cue_id:
                return i
        raise CueGraphError(f"cue not found: {cue_id!r}")

    def by_id(self, cue_id: str) -> dict:
        return self.cues[self.index(cue_id)]

    def _assert_unique(self, cue_id: str):
        if any(c.get("id") == cue_id for c in self.cues):
            raise CueGraphError(f"duplicate cue id: {cue_id!r}")

    # ── operations ─────────────────────────────────────────────────────────
    def insert_after(self, after_id: str, cue: dict, entry: str | None = None) -> dict:
        if not isinstance(cue, dict) or not cue.get("id"):
            raise CueGraphError("new cue needs an id")
        self._assert_unique(cue["id"])
        self.index(after_id)  # existence check
        new = copy.deepcopy(cue)
        if not new.get("parent"):
            new["parent"] = after_id
        if entry is not None:
            new["entry"] = entry
        at = self.index(after_id) + 1
        self.cues.insert(at, new)
        return new

    def delete(self, cue_id: str, force: bool = False) -> dict:
        idx = self.index(cue_id)
        target = self.cues[idx]
        # A deleted node with state-changing events cannot be removed while
        # preserving its children's old state.  The caller must opt in.
        state_ops = {"create", "ensure", "destroy", "transfer", "stack",
                     "shuffle", "set_face", "move_order"}
        changed = any(e.get("op") in state_ops for e in (target.get("events") or []))
        if changed and not force:
            raise CueGraphError(
                f"{cue_id}: cue has state-changing events; deleting it changes "
                f"children.  Pass --force to reparent children anyway."
            )
        del self.cues[idx]
        for c in self.cues:
            if c.get("parent") == cue_id:
                c["parent"] = target.get("parent")
            if c.get("entry") == cue_id:
                c["entry"] = target.get("entry") or target.get("parent") or "initial"
        return target

    def split(self, cue_id: str, at: float, new_id: str) -> tuple[dict, dict]:
        idx = self.index(cue_id)
        cue = self.cues[idx]
        events = cue.get("events") or []
        left, right = [], []
        for ev in events:
            s, e = event_start(ev), event_end(ev)
            if e <= at + 1e-9:
                left.append(ev)
            elif s >= at - 1e-9:
                right.append(ev)
            else:
                raise CueGraphError(
                    f"{cue_id}: event {ev.get('op')} spans split time {at}; "
                    f"split only at clean boundaries"
                )
        if not left or not right:
            raise CueGraphError(f"{cue_id}: split time would leave one side empty")
        first = copy.deepcopy(cue)
        first["events"] = left
        second = copy.deepcopy(cue)
        second["id"] = new_id
        self._assert_unique(new_id)
        second["parent"] = cue_id
        second["entry"] = cue_id
        second["transition"] = "continue"
        shifted = []
        for ev in right:
            ev2 = copy.deepcopy(ev)
            ev2["at"] = round(max(0.0, event_start(ev) - at), 6)
            shifted.append(ev2)
        second["events"] = shifted
        self.cues[idx] = first
        self.cues.insert(idx + 1, second)
        # Move direct children from the old cue to the second half, because
        # the old cue's terminal state is now the second half's terminal state.
        for c in self.cues:
            if c is second:
                continue
            if c.get("parent") == cue_id:
                c["parent"] = new_id
            if c.get("entry") == cue_id and c.get("id") != new_id:
                c["entry"] = new_id
        return first, second

    def merge(self, first_id: str, second_id: str) -> dict:
        i = self.index(first_id)
        j = self.index(second_id)
        if j != i + 1:
            raise CueGraphError(f"{first_id} and {second_id} are not adjacent")
        first, second = self.cues[i], self.cues[j]
        offset = cue_end(first)
        merged = copy.deepcopy(first)
        merged_events = list(first.get("events") or [])
        for ev in second.get("events") or []:
            ev2 = copy.deepcopy(ev)
            ev2["at"] = round(event_start(ev) + offset, 6)
            merged_events.append(ev2)
        merged["events"] = merged_events
        self.cues[i] = merged
        del self.cues[j]
        for c in self.cues:
            if c.get("parent") == second_id:
                c["parent"] = first_id
            if c.get("entry") == second_id:
                c["entry"] = first_id
        return merged


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("insert")
    p.add_argument("--source", required=True)
    p.add_argument("--after", required=True)
    p.add_argument("--new", required=True, help="new cue id (also written into cue-file)")
    p.add_argument("--cue-file", required=True)
    p.add_argument("--entry")

    p = sub.add_parser("delete")
    p.add_argument("--source", required=True)
    p.add_argument("--cue", required=True)
    p.add_argument("--force", action="store_true")

    p = sub.add_parser("split")
    p.add_argument("--source", required=True)
    p.add_argument("--cue", required=True)
    p.add_argument("--at", type=float, required=True)
    p.add_argument("--new", required=True)

    p = sub.add_parser("merge")
    p.add_argument("--source", required=True)
    p.add_argument("--first", required=True)
    p.add_argument("--second", required=True)
    return ap.parse_args(argv)


def main(argv=None) -> int:
    a = parse_args(argv)
    path = Path(a.source)
    if not path.exists():
        print(f"source not found: {path}", file=sys.stderr)
        return 2
    try:
        g = CueGraph.from_file(path)
        if a.cmd == "insert":
            cue = load_json(Path(a.cue_file))
            cue["id"] = a.new
            g.insert_after(a.after, cue, entry=a.entry)
        elif a.cmd == "delete":
            g.delete(a.cue, force=a.force)
        elif a.cmd == "split":
            g.split(a.cue, a.at, a.new)
        elif a.cmd == "merge":
            g.merge(a.first, a.second)
        g.save(path)
        print(f"OK   {a.cmd}: {path}")
    except (CueGraphError, OSError, json.JSONDecodeError) as exc:
        print(f"FAIL {a.cmd}: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
