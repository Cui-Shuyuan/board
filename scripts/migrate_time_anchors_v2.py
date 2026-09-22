#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""One-off migration: raw `at` seconds -> symbolic top-level `time_anchors`.

Anchor conventions:
  <cue_id>.start                       cue start
  <cue_id>.end                         cue end
  <beat_id>.start / <beat_id>.end      written beat start/end via TTS words

Every migrated event stores `anchor`, and `offset` only when the original
timestamp was not exactly on an anchor.  The compiler still emits numeric `at`,
so Unity runtime stays unchanged.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
V2 = ROOT / "games" / "splendor" / "tutorial" / "anim" / "v2"
SCRIPT = ROOT / "games" / "splendor" / "tutorial" / "script.full.json"
RUNTIME = ROOT / "games" / "splendor" / "tutorial" / "full.runtime.json"
TRACK = V2 / "full.anim.json"


def key_text(s) -> str:
    return "".join(ch for ch in str(s or "") if ch.isalnum())


def beat_times(script_doc: dict, runtime_doc: dict) -> dict:
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
                raise ValueError(f"beat text not found: {cid}/{bid} {b.get('text')!r}")
            si = char_word[pos]
            ei = char_word[pos + len(bkey) - 1]
            out[(cid, bid)] = (
                float(words[si].get("start", 0.0) or 0.0),
                float(words[ei].get("end", words[ei].get("start", 0.0)) or 0.0),
            )
            cursor = pos + len(bkey)
    return out


LEGACY_ANCHORS = {
    "setup.cards.001.1.cue_start": "setup.cards.001.1.start",
    "setup.cards.001.1.card_intro": "setup.cards.001.1.b1.start",
    "setup.cards.001.1.card_intro_end": "setup.cards.001.1.b1.end",
}


def main() -> int:
    script_doc = json.loads(SCRIPT.read_text(encoding="utf-8"))
    runtime_doc = json.loads(RUNTIME.read_text(encoding="utf-8"))
    track = json.loads(TRACK.read_text(encoding="utf-8"))
    btimes = beat_times(script_doc, runtime_doc)

    anchors = []
    cue_anchor_times = {}
    for c in script_doc.get("cues") or []:
        cid = c.get("id")
        rt = next((x for x in runtime_doc.get("cues") or [] if x.get("id") == cid), None)
        if not cid or rt is None:
            continue
        start_id = f"{cid}.start"
        end_id = f"{cid}.end"
        anchors.append({"id": start_id, "cue": cid, "edge": "cue_start"})
        anchors.append({"id": end_id, "cue": cid, "edge": "cue_end"})
        candidates = [(0.0, start_id), (float(rt.get("duration", 0.0) or 0.0), end_id)]
        for b in c.get("beats") or []:
            bid = b.get("id")
            t = btimes.get((cid, bid))
            if not bid or t is None:
                continue
            sid = f"{bid}.start"
            eid = f"{bid}.end"
            anchors.append({"id": sid, "cue": cid, "beat": bid, "edge": "start"})
            anchors.append({"id": eid, "cue": cid, "beat": bid, "edge": "end"})
            candidates.append((t[0], sid))
            candidates.append((t[1], eid))
        candidates.sort(key=lambda x: (x[0], x[1]))
        cue_anchor_times[cid] = candidates

    migrated = 0
    for c in track.get("cues") or []:
        cid = c.get("id")
        events = c.get("events") or []
        candidates = cue_anchor_times.get(cid)
        if not events or not candidates:
            continue
        out_events = []
        for order, ev in enumerate(events):
            ev = dict(ev)
            if "anchor" in ev:
                ev["anchor"] = LEGACY_ANCHORS.get(ev["anchor"], ev["anchor"])
            if "at" not in ev:
                out_events.append((-1.0, order, ev))
                continue
            t = float(ev.pop("at") or 0.0)
            anchor_t, anchor_id = min(candidates, key=lambda x: (abs(x[0] - t), x[0], x[1]))
            offset = round(t - anchor_t, 3)
            ev["anchor"] = anchor_id
            if abs(offset) >= 0.0015:
                ev["offset"] = offset
            out_events.append((t, order, ev))
            migrated += 1
        out_events.sort(key=lambda x: (x[0], x[1]))
        c["events"] = [ev for _, _, ev in out_events]

    track["time_anchors"] = anchors
    TRACK.write_text(json.dumps(track, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"OK   migrated {migrated} events -> {len(anchors)} time anchors")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
