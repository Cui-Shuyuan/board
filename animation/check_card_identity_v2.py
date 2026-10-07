#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check that every face-up development card in each compiled state has a unique physical identity.

Each real card gets its own template (`content/games/splendor/card_registry.json`)
and the same physical card never appears twice in one state; template reuse for
different physical cards would make every inventory check ambiguous.

The checker walks every cue's start state and every `put` state op (i.e. every
intermediate state the runtime can render) and reports duplicate registry card
keys.  It intentionally ignores face-down / back-side components and the
non-registry `blank_card_*` / `sample_back_*` helpers.

Usage:
    python3 animation/check_card_identity_v2.py --game splendor --track full
Exit code 0 = no duplicate face-up real card in any state.
"""
from __future__ import annotations
import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "animation"))
from compiled_state import iter_renderable_states  # noqa: E402


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def physical_key(comp: dict, registry_by_template: dict[str, dict]):
    tid = comp.get("TemplateId") or ""
    c = registry_by_template.get(tid)
    if c:
        return c["_physical_key"]
    # Any other card-looking template that is not a documented card/back is a bug:
    # if it is face-up, we do not know which physical card it impersonates.
    return None


def face_up_dev_components(components: list[dict]):
    for comp in components or []:
        if str(comp.get("Concept") or "").startswith("development_card_level_") and int(comp.get("Face", 2) or 2) == 2:
            yield comp


def check_state(rep: list[str], where: str, components: list[dict], registry_by_template: dict[str, dict]):
    buckets: dict[str, list[dict]] = defaultdict(list)
    for comp in face_up_dev_components(components):
        tid = comp.get("TemplateId") or ""
        if not tid or tid.startswith("blank_card_") or tid.startswith("sample_back_"):
            continue
        key = physical_key(comp, registry_by_template)
        if key is None:
            rep.append(f"{where}: 未登记的 face-up 发展卡模板 {tid!r}（组件 {comp.get('Id')}）")
            continue
        buckets[key].append(comp)
    for key, comps in sorted(buckets.items()):
        if len(comps) > 1:
            ids = ", ".join(str(c.get("Id")) for c in comps)
            rep.append(f"{where}: 真卡 {key} 同一状态出现 {len(comps)} 次（{ids}）")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--track", default="full")
    ap.add_argument("--compiled", default=None)
    a = ap.parse_args()

    reg_path = ROOT / "content" / "games" / a.game / "card_registry.json"
    comp_path = Path(a.compiled) if a.compiled else (
        ROOT / "content" / "games" / a.game / "tutorial" / "anim" / "v2" / f"{a.track}.compiled.json")
    if not reg_path.exists():
        print(f"missing {reg_path}", file=sys.stderr)
        return 2
    if not comp_path.exists():
        print(f"missing {comp_path}", file=sys.stderr)
        return 2

    registry = load(reg_path)
    by_template = {}
    for c in registry.get("cards") or []:
        item = dict(c)
        item["_physical_key"] = item["template_id"]
        by_template[item["template_id"]] = item

    compiled = load(comp_path)
    rep: list[str] = []
    checked = 0
    for cue in compiled.get("cues") or []:
        cid = cue.get("id", "?")
        for at, components, is_start in iter_renderable_states(cue):
            where = f"{cid} start" if is_start else f"{cid} t={at:g}"
            check_state(rep, where, [dict(c) for c in components], by_template)
            checked += 1

    if rep:
        print(f"FAIL card identity: {len(rep)} problem(s) across {checked} states")
        for line in rep:
            print("ERR ", line)
        return 1
    print(f"OK   card identity: no duplicate face-up real development card across {checked} states")
    return 0


if __name__ == "__main__":
    sys.exit(main())
