#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Run a game's rule ledger over v2 animation source events.

Each cue is replayed from its compiled v2 ``start_state``; the adapter builds
ledger events independently from the v2 compiler so the rule checks do not
reuse compiler logic.

The game-specific ledger lives at::

    content/games/{game}/tutorial/checks/ledger.py

Nothing from this adapter knows the game's materials or limits.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))
import anim_schema_v2 as schema  # noqa: E402


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _ledger_paths() -> list[tuple[str, Path]]:
    out = []
    for path in sorted((ROOT / "content" / "games").glob("*/tutorial/checks/ledger.py")):
        try:
            game = path.relative_to(ROOT / "content" / "games").parts[0]
        except (OSError, ValueError, IndexError):
            continue
        out.append((game, path))
    return out


def resolve_game(game: str | None) -> str:
    if game:
        return game
    candidates = _ledger_paths()
    if len(candidates) == 1:
        return candidates[0][0]
    if not candidates:
        raise FileNotFoundError("找不到任何游戏 ledger；请用 --game 指定游戏")
    raise ValueError(
        "检测到多个游戏 ledger；请用 --game 指定其中之一的游戏: "
        + ", ".join(name for name, _ in candidates)
    )


def load_game_ledger(game: str, explicit: str | None = None):
    path = Path(explicit) if explicit else ROOT / "content" / "games" / game / "tutorial" / "checks" / "ledger.py"
    if not path.exists():
        raise FileNotFoundError(f"missing ledger: {path}")
    spec = importlib.util.spec_from_file_location(f"_tutorial_ledger_{game}", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load ledger: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for required in ("Report", "State", "run"):
        if not hasattr(module, required):
            raise ImportError(f"ledger {path} is missing {required!r}")
    return module


def _component_ident(comp):
    """Map a compiled ComponentState to the ledger's simplified identity."""
    tpl = comp.get("TemplateId") or ""
    pal = comp.get("Palette") or ""
    concept = comp.get("Concept") or ""
    parts = comp.get("parts") or []
    color = None
    for part in parts:
        if isinstance(part, dict) and part.get("key") == "color":
            color = str(part.get("value", "")).strip("<>")
            break
    if concept == "gold" or pal == "gem_gold":
        return f"gold@{tpl}@{pal}"
    if color is None and pal.startswith("gem_"):
        color = pal[4:]
    if color:
        return f"gem:{color}@{tpl}@{pal}"
    return f"card:{tpl}"


def _start_states(compiled, ledger):
    out = {}
    for cue in compiled.get("cues") or []:
        cid = cue.get("id")
        if not cid:
            continue
        st = ledger.State()
        for comp in (cue.get("start_state") or {}).get("components") or []:
            zid = comp.get("ZoneId")
            if zid:
                st.add(zid, _component_ident(comp), 1)
        out[cid] = st
    return out


def to_ledger_event(ev):
    op = ev.get("op")
    if op in ("camera", "label", "shape", "overlay_show", "overlay_hide"):
        return None
    out = {"action": op, "at": ev.get("at", 0), "dur": ev.get("dur", 0)}
    if ev.get("lead") is not None:
        out["lead"] = ev["lead"]
    if ev.get("easing"):
        out["easing"] = ev["easing"]
    what = {}
    if ev.get("concept"):
        what["concept"] = ev["concept"]
    if ev.get("parts"):
        what["parts"] = ev["parts"]
    if what:
        out["what"] = what
    for key in ("template", "palette", "zone", "source", "destination", "quantity",
                "count", "to", "order", "slot", "stagger", "from_back", "setup",
                "over_limit_demo"):
        if ev.get(key) is not None:
            out[key] = ev[key]
    if op in ("create", "ensure"):
        out["destination"] = ev.get("zone") or ev.get("destination")
    if op == "ensure":
        out["action"] = "create"
    if op == "show":
        out["action"] = "showbox"
        out["on"] = 1 if ev.get("picture") else 0
        out["picture"] = ev.get("picture")
    if op == "stack":
        out.update({
            "destination": ev.get("destination"),
            "capacity": ev.get("capacity"),
            "real_templates": ev.get("real_templates"),
            "pad_template": ev.get("pad_template"),
            "to": ev.get("to"),
        })
    if op in ("highlight", "point", "fade", "scale", "wait"):
        out["action"] = op
    if op == "transfer" and isinstance(ev.get("source"), str):
        out["source"] = [ev["source"]]
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default=None)
    ap.add_argument("--track", default="full")
    ap.add_argument("--ledger", default=None, help="override ledger.py path")
    a = ap.parse_args(argv)

    try:
        game = resolve_game(a.game)
    except (FileNotFoundError, ValueError) as exc:
        print(f"ERR  ledger: {exc}", file=sys.stderr)
        return 2
    try:
        ledger = load_game_ledger(game, a.ledger)
    except (FileNotFoundError, ImportError) as exc:
        print(f"ERR  ledger: {exc}", file=sys.stderr)
        return 2

    base = ROOT / "content" / "games" / game / "tutorial" / "anim" / "v2"
    try:
        track = schema.resolve_track(load(base / f"{a.track}.anim.json"))
        compiled = load(base / f"{a.track}.compiled.json")
    except (OSError, json.JSONDecodeError) as exc:
        print(f"ERR  input: {base / (a.track + '.anim.json')}: {exc}", file=sys.stderr)
        return 2

    stages = {}
    default_stage = None
    for t in track.get("trees") or []:
        p = base / (t["stage"] if t["stage"].endswith(".json") else t["stage"] + ".json")
        if p.exists():
            stages[t["id"]] = load(p)
            if t["id"] == track.get("default_tree") or default_stage is None:
                default_stage = load(p)
    try:
        facts = load(ROOT / "content" / "games" / game / "card_facts.json")
    except FileNotFoundError:
        facts = {}
    except json.JSONDecodeError as exc:
        print(f"ERR  card_facts: {exc}", file=sys.stderr)
        return 2

    anim = {
        "trees": [{"id": t["id"], "world": t.get("world") or t["id"]}
                  for t in (track.get("trees") or [])],
        "cues": [],
    }
    for c in track.get("cues") or []:
        anim["cues"].append({
            "cue": c["id"],
            "tree": c.get("tree") or "main",
            "demo": bool(c.get("demo")),
            "events": [
                x for x in (to_ledger_event(e) for e in (c.get("events") or [])) if x
            ],
        })

    rep = ledger.Report()
    ledger.run(anim, default_stage, stages, facts, rep,
               cue_start_states=_start_states(compiled, ledger))
    for w in rep.warnings:
        print("WARN", *w)
    for e in rep.errors:
        print("ERR ", *e)
    if rep.errors:
        print(
            f"FAIL v2 rule ledger: {len(rep.errors)} errors, {len(rep.warnings)} warnings",
            file=sys.stderr,
        )
        return 1
    print(f'OK   v2 rule ledger: {len(anim["cues"])} cues passed, {len(rep.warnings)} warnings')
    return 0


if __name__ == "__main__":
    sys.exit(main())
