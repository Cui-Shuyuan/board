#!/usr/bin/env python3
"""
Validate a tutorial.json against the tutorial animation schema.

The schema lives in tutorial/schema/tutorial.schema.json.  The validator is
deliberately deterministic: it checks schema conformance, id uniqueness,
slot/sprite references, action-specific required fields, asset existence,
timeline ordering and overlapping-animation conflicts.  It is intended to be
run before the JSON is loaded in Unity, so that LLM-generated tutorials fail
here with machine-readable errors instead of showing small visual bugs.

Usage:
    python scripts/validate_tutorial.py --game splendor
    python scripts/validate_tutorial.py --tutorial games/splendor/tutorial.json
    python scripts/validate_tutorial.py --tutorial games/splendor/tutorial.json --skip-assets --json

Exit code is 1 when there are errors, 2 when the file cannot be read, and 0
when there are no errors (warnings are allowed).
"""

import argparse
import json
import sys
from pathlib import Path

import jsonschema

ROOT = Path(__file__).resolve().parent.parent
SCHEMA_PATH = ROOT / "tutorial" / "schema" / "tutorial.schema.json"

# Actions that move/transform a sprite and therefore occupy that sprite during
# their [t, t+duration] interval.  highlight and wait are skipped: highlight
# may intentionally loop, wait holds the state rather than animating it.
SPRITE_OCCUPYING_ACTIONS = {"move", "flip", "rotate", "scale", "fade", "shuffle"}


class Validator:
    def __init__(self, tutorial_path: Path, check_assets: bool = True):
        self.tutorial_path = tutorial_path
        self.check_assets = check_assets
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self.doc = None

    def error(self, msg: str):
        self.errors.append(msg)

    def warn(self, msg: str):
        self.warnings.append(msg)

    def run(self) -> bool:
        try:
            with open(self.tutorial_path, "r", encoding="utf-8") as f:
                self.doc = json.load(f)
        except json.JSONDecodeError as e:
            self.errors.append(f"JSON parse error: {e}")
            return False

        # 1. JSON Schema conformance.
        with open(SCHEMA_PATH, "r", encoding="utf-8") as f:
            schema = json.load(f)
        validator_cls = jsonschema.Draft202012Validator
        errors = sorted(validator_cls(schema).iter_errors(self.doc), key=lambda e: list(e.path))
        if errors:
            for e in errors[:50]:
                path = "/" + "/".join(str(p) for p in e.path) if e.path else "(root)"
                self.errors.append(f"{path}: {e.message}")
            if len(errors) > 50:
                self.errors.append(f"... {len(errors) - 50} more schema errors omitted")

        if not errors:
            self.semantic_checks()
        return not self.errors

    # ── semantic checks ──────────────────────────────────────────────────
    def semantic_checks(self):
        doc = self.doc
        game_id = doc["meta"]["game_id"]
        game_dir = ROOT / "games" / game_id
        media_dir = game_dir / "media"

        # The tutorial may live outside games/{game}/; make relative asset
        # paths resolve from the tutorial file's own game media dir when the
        # standard game dir is absent, so third-party/generic tutorials are
        # still checkable.
        if not game_dir.exists():
            alt = self.tutorial_path.resolve().parent.parent  # .../{game}/tutorial.json -> .../{game}
            if alt.name == game_id:
                game_dir = alt
                media_dir = alt / "media"
            else:
                self.warn(f"games/{game_id} directory not found; asset existence will be checked from {self.tutorial_path.parent}")

        slots = doc.get("slots", [])
        sprites = doc.get("sprites", [])
        chapters = doc.get("chapters", [])

        self.check_asset_file(doc.get("board", {}).get("image"), media_dir, "board.image")
        for si, sprite in enumerate(sprites):
            self.check_asset_file(sprite.get("file"), media_dir, f"sprites[{si}].file")

        slot_ids = [s["id"] for s in slots]
        sprite_ids = [s["id"] for s in sprites]
        chapter_ids = [c["id"] for c in chapters]

        self.check_duplicates(slot_ids, "slot id")
        self.check_duplicates(sprite_ids, "sprite id")
        self.check_duplicates(chapter_ids, "chapter id")

        slot_set = set(slot_ids)
        sprite_set = set(sprite_ids)

        # Per-chapter event ids (globally unique so logs/screenshots are unambiguous).
        all_event_ids: set[str] = set()
        global_event_ids_seen: dict[str, str] = {}

        for ci, ch in enumerate(chapters):
            self.check_asset_file(ch.get("audio"), media_dir, f"chapters[{ci}].audio")

            # Subtitles must be sorted by time.
            subs = ch.get("subtitles", [])
            if subs:
                prev = -1.0
                for si, sub in enumerate(subs):
                    if sub["t"] < prev:
                        self.error(f"chapters[{ci}].subtitles[{si}].t={sub['t']} is out of order (previous={prev})")
                    prev = sub["t"]

            timeline = ch.get("timeline", [])
            if not timeline:
                self.warn(f"chapters[{ci}] has an empty timeline (voice-only chapter)")
                continue

            # Timeline must be sorted by start time.
            prev_t = -1.0
            for ei, ev in enumerate(timeline):
                if ev["t"] < prev_t:
                    self.error(f"chapters[{ci}].timeline[{ei}] t={ev['t']} is out of order (previous={prev_t})")
                prev_t = ev["t"]

                self.check_event(ch, ci, ev, ei, slot_set, sprite_set, media_dir)

                eid = ev["id"]
                if eid in global_event_ids_seen:
                    self.error(f"duplicate event id '{eid}' in chapters[{ci}] (first seen in {global_event_ids_seen[eid]})")
                else:
                    global_event_ids_seen[eid] = f"chapters[{ci}]"

            # Sprite occupancy conflict detection: a sprite cannot be in two
            # occupying animations at the same time (small overlaps are warnings).
            self.check_sprite_conflicts(ch, ci, timeline)

        # Warn about unused definitions: often a sign that DeepSeek generated a
        # slot/sprite but forgot to use it in the timeline.
        used_slots: set[str] = set()
        used_sprites: set[str] = set()
        for ch in chapters:
            for ev in ch.get("timeline", []):
                if ev.get("from_slot"):
                    used_slots.add(ev["from_slot"])
                if ev.get("to_slot"):
                    used_slots.add(ev["to_slot"])
                if ev.get("slot"):
                    used_slots.add(ev["slot"])
                if ev.get("sprite"):
                    used_sprites.add(ev["sprite"])
                used_sprites.update(ev.get("sprites", []))
        unused_slots = sorted(slot_set - used_slots)
        unused_sprites = sorted(sprite_set - used_sprites)
        if unused_slots:
            self.warn(f"unused slots: {', '.join(unused_slots)}")
        if unused_sprites:
            self.warn(f"unused sprites: {', '.join(unused_sprites)}")

    def check_duplicates(self, ids, label):
        seen = {}
        for i, id_ in enumerate(ids):
            if id_ in seen:
                self.error(f"duplicate {label} '{id_}' (first at index {seen[id_]}, again at {i})")
            seen[id_] = i

    def check_asset_file(self, rel: str, media_dir: Path, where: str):
        if not rel:
            return
        if not self.check_assets:
            return
        p = media_dir / rel if media_dir.is_absolute() or media_dir.exists() or True else Path(rel)
        # media_dir may not exist yet; still report the path we checked.
        if not (media_dir / rel).exists():
            self.errors.append(f"{where}: asset not found: media/{rel} (looked in {media_dir})")

    def check_event(self, ch, ci, ev, ei, slot_set, sprite_set, media_dir):
        where = f"chapters[{ci}].timeline[{ei}] ({ev['id']})"
        action = ev["action"]

        if ev.get("sprite") and ev["sprite"] not in sprite_set:
            self.error(f"{where}: sprite '{ev['sprite']}' is not defined")
        if ev.get("from_slot") and ev["from_slot"] not in slot_set:
            self.error(f"{where}: from_slot '{ev['from_slot']}' is not defined")
        if ev.get("to_slot") and ev["to_slot"] not in slot_set:
            self.error(f"{where}: to_slot '{ev['to_slot']}' is not defined")
        if ev.get("slot") and ev["slot"] not in slot_set:
            self.error(f"{where}: slot '{ev['slot']}' is not defined")
        for sp in ev.get("sprites", []):
            if sp not in sprite_set:
                self.error(f"{where}: shuffle sprite '{sp}' is not defined")

        # Action-specific required fields.
        if action == "move":
            if not ev.get("sprite"):
                self.error(f"{where}: action 'move' requires sprite")
            has_from = bool(ev.get("from_slot") or ev.get("from_position"))
            has_to = bool(ev.get("to_slot") or ev.get("to_position"))
            if not has_from:
                self.error(f"{where}: action 'move' requires from_slot or from_position")
            if not has_to:
                self.error(f"{where}: action 'move' requires to_slot or to_position")
        elif action in ("flip", "rotate", "scale", "fade"):
            if not ev.get("sprite"):
                self.error(f"{where}: action '{action}' requires sprite")
            if action == "rotate" and ev.get("rotation") is None:
                self.error(f"{where}: action 'rotate' requires rotation")
            if action == "scale" and ev.get("scale") is None:
                self.error(f"{where}: action 'scale' requires scale")
            if action == "fade" and ev.get("opacity") is None:
                self.error(f"{where}: action 'fade' requires opacity")
        elif action == "highlight":
            if not (ev.get("slot") or ev.get("sprite")):
                self.error(f"{where}: action 'highlight' requires slot or sprite")
        elif action == "shuffle":
            if not ev.get("sprites"):
                self.error(f"{where}: action 'shuffle' requires sprites")
        elif action == "wait":
            pass
        else:
            self.error(f"{where}: unknown action '{action}'")

    def check_sprite_conflicts(self, ch, ci, timeline):
        intervals: dict[str, list[tuple[float, float, str]]] = {}
        for ev in timeline:
            if ev["action"] not in SPRITE_OCCUPYING_ACTIONS:
                continue
            targets = []
            if ev.get("sprite"):
                targets.append(ev["sprite"])
            targets.extend(ev.get("sprites", []))
            for sprite_id in targets:
                intervals.setdefault(sprite_id, []).append((ev["t"], ev["t"] + ev["duration"], ev["id"]))
        for sprite_id, ivs in intervals.items():
            ivs.sort()
            for i in range(len(ivs) - 1):
                a_start, a_end, a_id = ivs[i]
                b_start, b_end, b_id = ivs[i + 1]
                overlap = min(a_end, b_end) - max(a_start, b_start)
                if overlap > 0.05:
                    self.warn(
                        f"chapters[{ci}]: sprite '{sprite_id}' is busy in overlapping events "
                        f"'{a_id}' [{a_start:.2f}-{a_end:.2f}] and '{b_id}' [{b_start:.2f}-{b_end:.2f}] "
                        f"(overlap {overlap:.2f}s)"
                    )


def main():
    parser = argparse.ArgumentParser(description="Validate a board game tutorial.json")
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument("--game", help="game directory name, e.g. splendor")
    src.add_argument("--tutorial", help="path to tutorial.json")
    parser.add_argument("--skip-assets", action="store_true", help="do not check that referenced assets exist on disk")
    parser.add_argument("--json", action="store_true", help="print machine-readable JSON instead of text")
    args = parser.parse_args()

    if args.tutorial:
        tutorial_path = Path(args.tutorial)
    else:
        tutorial_path = ROOT / "games" / args.game / "tutorial.json"

    if not tutorial_path.exists():
        msg = f"tutorial file not found: {tutorial_path}"
        if args.json:
            print(json.dumps({"ok": False, "errors": [msg], "warnings": []}, ensure_ascii=False, indent=2))
        else:
            print(msg, file=sys.stderr)
        sys.exit(2)

    v = Validator(tutorial_path, check_assets=not args.skip_assets)
    ok = v.run()

    if args.json:
        print(json.dumps({"ok": ok, "errors": v.errors, "warnings": v.warnings}, ensure_ascii=False, indent=2))
    else:
        if v.errors:
            print(f"FAIL {tutorial_path}")
            for e in v.errors:
                print(f"  [ERROR] {e}")
        if v.warnings:
            print(f"WARNINGS ({len(v.warnings)})")
            for w in v.warnings:
                print(f"  [WARN] {w}")
        if ok and not v.errors:
            print(f"OK {tutorial_path}" + (f" ({len(v.warnings)} warnings)" if v.warnings else ""))

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
