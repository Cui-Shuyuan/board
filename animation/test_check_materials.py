#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Boundary/state/fact/timing tests for material checking."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import check_materials as cm  # noqa: E402
import compile_animation_v2 as anim_compile  # noqa: E402
import compile_tutorial as ct  # noqa: E402
import material_library as ml  # noqa: E402
import section_source  # noqa: E402


def write(path: Path, doc) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def action(aid: str, *, steps=None, contract=None, timing=None, roles=None):
    doc = {
        "schema": "tutorial-material/v1",
        "id": aid,
        "kind": "action",
        "game": "casegame",
        "roles": roles or [{"name": "zone", "type": "zone", "required": False, "default": "a"}],
        "steps": steps if steps is not None else [{
            "id": "e",
            "events": [{
                "op": "create", "count": 1, "anchor": "@e",
                "target": {"space": "entity", "zone": "$zone", "template": "piece",
                           "palette": "plain", "concept": "piece"},
            }],
        }],
    }
    if contract:
        doc["contract"] = contract
    if timing:
        doc["timing"] = timing
    return doc


def create_event(zone="a", count=1, op="create", dest=None):
    event = {
        "op": op, "count": count, "anchor": "@e",
        "target": {"space": "entity", "zone": zone, "template": "piece",
                   "palette": "plain", "concept": "piece"},
    }
    if dest is not None:
        event["destination"] = {"space": "entity", "zone": dest}
    return event


class MaterialCheckerBoundaryTests(unittest.TestCase):
    def _fixture(self, root: Path, actions: dict, cue_uses: list, examples=None, *,
                 entry="initial"):
        game = "casegame"
        tut = root / "content/games" / game / "tutorial"
        stage = {
            "schema": "tutorial-stage/v2", "game": game, "id": "case_stage",
            "zones": [
                {"id": "a", "concept": "<zone>", "label": "A",
                 "layout": {"x": 0, "y": 0, "w": 1, "h": 1}},
                {"id": "b", "concept": "<zone>", "label": "B",
                 "layout": {"x": 2, "y": 0, "w": 1, "h": 1}},
            ],
            "templates": [{"id": "piece", "face_image": "media/piece_cutout.png",
                           "back_image": "media/piece_cutout.png"}],
            "shots": [{"id": "shot_all", "zones": ["a", "b"], "fill": 0.9}],
        }
        write(tut / "shared/stage/case.stage.json", stage)
        write(tut / "shared/full.base.anim.json", {
            "schema": "tutorial-anim/v2", "kind": "animation_track", "game": game,
            "track": "full", "default_tree": "main",
            "worlds": [{"id": "w", "why": "test"}],
            "trees": [{"id": "main", "world": "w",
                       "stage": "shared/stage/case.stage.json"}],
        })
        write(tut / "tracks/full.track.json", {
            "schema": "tutorial-track/v1", "game": game, "track": "full",
            "title": "case", "version": "0.1.0", "sections": ["s"],
            "base_anim": "../shared/full.base.anim.json",
        })
        write(tut / "sections/s/section.json", {
            "id": "s", "title": "S", "group_path": ["S"], "order": 0,
            "tree": "main", "entry": "initial",
        })
        write(tut / "sections/s/full.script.json", {
            "schema_version": 1, "game_id": game, "track": "full", "section": "s",
            "cues": [{"id": "c1", "beats": []}],
        })
        for aid, doc in actions.items():
            write(tut / "library/actions" / f"{aid}.action.json", doc)
        for eid, doc in (examples or {}).items():
            write(tut / "library/examples" / f"{eid}.example.json", doc)
        cue = {
            "id": "c1", "tree": "main",
            "script": {"story": "case", "enter": {"zones": {}}, "exit": {"zones": {}}},
            "events": [],
            "use": cue_uses,
        }
        if entry is not None:
            cue["entry"] = entry
        write(tut / "sections/s/full.anim.json", {"section": "s", "cues": [cue]})
        return tut

    def _run(self, root: Path, actions: dict, cue_uses: list, examples=None, *,
             entry="initial"):
        game = "casegame"
        tut = self._fixture(root, actions, cue_uses, examples, entry=entry)
        with mock.patch.object(section_source, "ROOT", root), \
                mock.patch.object(anim_compile, "ROOT", root):
            code = anim_compile.main(["--game", game, "--track", "full"])
            self.assertEqual(0, code)
            result = cm.check_track(game, "full")
        return result

    def test_create_then_move_is_checked_at_each_call_boundary(self):
        actions = {
            "case.create_one": action("case.create_one", contract={
                "postconditions": {"zones": {"$zone": {"count": 1}}}}),
            "case.move_a_b": action("case.move_a_b", steps=[{
                "id": "e",
                "events": [dict(create_event("a"), op="take", destination={"space": "entity", "zone": "b"})],
            }], contract={"postconditions": {"zones": {"b": {"count": 1}}}}),
        }
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            code, errors = self._run(root, actions, [
                {"ref": "case.create_one", "timing": {"e": {"at": 0.1}}},
                {"ref": "case.move_a_b", "timing": {"e": {"at": 0.4}}},
            ])
        self.assertEqual(0, code, errors)

    def test_previous_action_cannot_silently_invalidate_next_precondition(self):
        actions = {
            "case.create_one": action("case.create_one"),
            "case.destroy_one": action("case.destroy_one", steps=[{
                "id": "e",
                "events": [dict(create_event("a"), op="destroy", count=1)],
            }]),
            "case.need_a_one": action("case.need_a_one", steps=[{
                "id": "e", "events": [create_event("b")],
            }], contract={"preconditions": {"zones": {"a": {"count": 1}}},
                          "postconditions": {"zones": {"b": {"count": 1}}}}),
        }
        with tempfile.TemporaryDirectory() as tmp:
            code, errors = self._run(Path(tmp), actions, [
                {"ref": "case.create_one", "timing": {"e": {"at": 0.1}}},
                {"ref": "case.destroy_one", "timing": {"e": {"at": 0.3}}},
                {"ref": "case.need_a_one", "timing": {"e": {"at": 0.5}}},
            ])
        self.assertEqual(1, code)
        self.assertTrue(any("pre" in e and "count=0" in e for e in errors), errors)

    def test_later_repair_does_not_mask_earlier_bad_postcondition(self):
        actions = {
            "case.create_two": action("case.create_two", steps=[{
                "id": "e", "events": [create_event("a", count=2)],
            }], contract={"postconditions": {"zones": {"a": {"count": 1}}}}),
            "case.destroy_one": action("case.destroy_one", steps=[{
                "id": "e", "events": [dict(create_event("a"), op="destroy", count=1)],
            }]),
        }
        with tempfile.TemporaryDirectory() as tmp:
            code, errors = self._run(Path(tmp), actions, [
                {"ref": "case.create_two", "timing": {"e": {"at": 0.1}}},
                {"ref": "case.destroy_one", "timing": {"e": {"at": 0.4}}},
            ])
        self.assertEqual(1, code)
        self.assertTrue(any("post" in e and "count=2" in e for e in errors), errors)



    def test_stale_or_divergent_compiled_product_is_rejected(self):
        actions = {"case.create_one": action("case.create_one")}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._fixture(root, actions, [
                {"ref": "case.create_one", "timing": {"e": {"at": 0.1}}},
            ])
            game, track = "casegame", "full"
            with mock.patch.object(section_source, "ROOT", root), \
                    mock.patch.object(anim_compile, "ROOT", root):
                self.assertEqual(0, anim_compile.main(["--game", game, "--track", track]))
                path = section_source.compiled_path(game, track)
                doc = json.loads(path.read_text(encoding="utf-8"))
                doc["cues"][0]["duration"] = 999.0
                path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n",
                                encoding="utf-8")
                code, errors = cm.check_track(game, track)
        self.assertEqual(1, code)
        self.assertTrue(any("stale/divergent" in e for e in errors), errors)

    def test_build_gate_restores_previous_compiled_output_on_contract_failure(self):
        actions = {
            "case.create_one": action("case.create_one", contract={
                "postconditions": {"zones": {"$zone": {"count": 1}}}}),
        }
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._fixture(root, actions, [
                {"ref": "case.create_one", "timing": {"e": {"at": 0.1}}},
            ])
            game, track = "casegame", "full"
            with mock.patch.object(section_source, "ROOT", root), \
                    mock.patch.object(anim_compile, "ROOT", root):
                self.assertEqual(0, ct._compile_track_with_material_gate(game, track))
                compiled = section_source.compiled_path(game, track)
                previous = compiled.read_bytes()
                # Mutate the shared action contract while keeping compiler input valid.
                action_path = (root / "content/games/casegame/tutorial/library/actions"
                               / "case.create_one.action.json")
                doc = json.loads(action_path.read_text(encoding="utf-8"))
                doc["contract"]["postconditions"]["zones"]["$zone"]["count"] = 2
                action_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n",
                                       encoding="utf-8")
                self.assertNotEqual(0, ct._compile_track_with_material_gate(game, track))
                self.assertEqual(previous, compiled.read_bytes())


    def test_isolated_example_requires_explicit_entry_boundary(self):
        actions = {
            "case.create_one": action("case.create_one"),
            "case.example.isolated": {
                "schema": "tutorial-material/v1", "id": "case.example.isolated",
                "kind": "example", "game": "casegame",
                "steps": [{"id": "s", "use": [{"ref": "case.create_one",
                                                 "timing": {"e": {"at": 0.1}}}]}],
                "facts": {"zones": {"a": {"count": 1}}},
            },
        }
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._fixture(root, actions, [
                {"ref": "case.example.isolated", "mode": "isolated",
                 "timing": {"e": {"at": 0.1}}},
            ], entry=None)
            with mock.patch.object(section_source, "ROOT", root), \
                    mock.patch.object(anim_compile, "ROOT", root):
                self.assertEqual(0, anim_compile.main(["--game", "casegame", "--track", "full"]))
                code, errors = cm.check_track("casegame", "full")
        self.assertEqual(1, code)
        self.assertTrue(any("explicit cue entry boundary" in e for e in errors), errors)


    def test_isolated_example_can_be_reset_by_explicit_section_stage(self):
        actions = {
            "case.create_one": action("case.create_one"),
            "case.example.isolated": {
                "schema": "tutorial-material/v1", "id": "case.example.isolated",
                "kind": "example", "game": "casegame",
                "steps": [{"id": "s", "use": [{"ref": "case.create_one",
                                                 "timing": {"e": {"at": 0.1}}}]}],
                "facts": {"zones": {"a": {"count": 1}}},
            },
        }
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tut = self._fixture(root, actions, [
                {"ref": "case.example.isolated", "mode": "isolated",
                 "timing": {"e": {"at": 0.1}}},
            ])
            anim_path = tut / "sections/s/full.anim.json"
            script_path = tut / "sections/s/full.script.json"
            anim = json.loads(anim_path.read_text(encoding="utf-8"))
            script = json.loads(script_path.read_text(encoding="utf-8"))
            reset_cue = {
                "id": "c2", "entry": "initial", "tree": "main",
                "script": {"story": "reset", "enter": {"zones": {}}, "exit": {"zones": {}}},
                "events": [],
            }
            anim["cues"].append(reset_cue)
            script["cues"].append({"id": "c2", "beats": []})
            anim_path.write_text(json.dumps(anim, ensure_ascii=False, indent=2) + "\n",
                                 encoding="utf-8")
            script_path.write_text(json.dumps(script, ensure_ascii=False, indent=2) + "\n",
                                  encoding="utf-8")
            with mock.patch.object(section_source, "ROOT", root), \
                    mock.patch.object(anim_compile, "ROOT", root):
                src = section_source.load_track_source("casegame", "full")
                compiled = anim_compile.Compiler(
                    root / "dummy.anim.json", doc=src.anim_doc,
                    source_root=src.tutorial, source_hash=src.source_hash,
                    script_doc=src.script_doc).compile()
        self.assertTrue(compiled["cues"][0]["end_state"]["components"])
        self.assertEqual([], compiled["cues"][1]["start_state"]["components"])

    def test_nested_contract_and_boundary_are_checked(self):
        actions = {
            "case.create_one": action("case.create_one", contract={
                "postconditions": {"zones": {"$zone": {"count": 1}}}}),
            "case.bad_nested": action("case.bad_nested", steps=[{
                "id": "e", "events": [create_event("a", count=2)],
            }], contract={"postconditions": {"zones": {"a": {"count": 1}}}}),
        }
        examples = {
            "case.example.wrap": {
                "schema": "tutorial-material/v1", "id": "case.example.wrap",
                "kind": "example", "game": "casegame",
                "steps": [{"id": "wrapped", "use": [{
                    "ref": "case.bad_nested",
                    "timing": {"e": {"at": 0.2}},
                }]}],
                "facts": {"zones": {"a": {"count": 1}}},
            },
        }
        with tempfile.TemporaryDirectory() as tmp:
            code, errors = self._run(Path(tmp), actions, [
                {"ref": "case.example.wrap", "timing": {"e": {"at": 0.2}}},
            ], examples=examples)
        self.assertEqual(1, code)
        self.assertTrue(any("case.bad_nested" in e and "post" in e for e in errors), errors)

    def test_identical_calls_have_distinct_stable_ranges(self):
        actions = {
            "case.create_one": action("case.create_one", contract={
                "postconditions": {"zones": {"$zone": {"count": 1}}}}),
        }
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._fixture(root, actions, [
                {"ref": "case.create_one", "timing": {"e": {"at": 0.1}}},
                {"ref": "case.create_one", "timing": {"e": {"at": 0.4}}},
            ])
            with mock.patch.object(section_source, "ROOT", root), \
                    mock.patch.object(anim_compile, "ROOT", root):
                src = section_source.load_track_source("casegame", "full")
        refs = sorted(
            (u for u in src.material_usages if u.material == "case.create_one"),
            key=lambda u: u.event_start,
        )
        self.assertEqual(2, len(refs))
        self.assertEqual((0, 1), (refs[0].event_start, refs[0].event_count))
        self.assertEqual((1, 1), (refs[1].event_start, refs[1].event_count))

    def test_quick_nobles_003_example_facts_pass_at_setup_boundary(self):
        # Real repository integration: the setup example must be checked at its
        # own exit, not skipped because a purchase follows in the same cue.
        code, errors = cm.check_track("splendor", "quick")
        self.assertEqual(0, code, errors)


class SelectorAndTimingUnitTests(unittest.TestCase):
    def _snapshot(self, components):
        return {"components": components, "nextSeq": []}

    def _item(self, iid, zone, template="worker"):
        return {"Id": iid, "TemplateId": template, "Palette": "plain",
                "Concept": "piece", "parts": [], "ZoneId": zone,
                "Order": 0, "Layer": 0, "Face": 2}

    def test_contains_count_semantics(self):
        snap = self._snapshot([self._item("w1", "box")])
        report: list[str] = []
        checks = cm._check_zone_contract(
            snap, {"box": {"contains": [{"template": "worker", "count": 1}]}},
            "where", report)
        self.assertEqual([], report)
        self.assertGreaterEqual(checks, 1)

        report = []
        cm._check_zone_contract(
            snap, {"box": {"contains": [{"template": "worker", "count": 0}]}},
            "where", report)
        self.assertTrue(any("expected count 0" in e for e in report), report)

        report = []
        cm._check_zone_contract(
            snap, {"box": {"contains": [{"template": "worker", "count": 2}]}},
            "where", report)
        self.assertTrue(any("expected count 2" in e for e in report), report)

    def test_fact_objects_map_to_distinct_actual_ids(self):
        snap = self._snapshot([
            self._item("w1", "a"), self._item("w2", "b"),
        ])
        objects = [
            {"id": "logical.a", "template": "worker", "zone": "a"},
            {"id": "logical.b", "template": "worker", "zone": "b"},
        ]
        report: list[str] = []
        cm._check_fact_objects(snap, objects, "where", report)
        self.assertEqual([], report)

        report = []
        cm._check_fact_objects(snap, [
            {"id": "logical.a", "template": "worker"},
            {"id": "logical.b", "template": "worker"},
        ], "where", report)
        self.assertTrue(any("ambiguous" in e for e in report), report)


    def test_audio_window_diagnostics_use_section_audio_boundaries(self):
        usage = ml.MaterialUsage(
            track="t", section="s", cue="c1", use_index=0, step=None,
            material="m", kind="action", variant=None, mode="continue",
            path=Path("x"), where="c1 use[0]",
            events=[dict(create_event("a", count=1), dur=1.0)],
        )
        self.assertEqual(2.5, cm._material_end_time(usage, [1.5]))
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            rt = root / "content/games/casegame/tutorial/full.runtime.json"
            write(rt, {"cues": [
                {"id": "c1", "audio": "a.mp3", "audio_start": 0.0, "duration": 2.0},
                {"id": "c2", "audio": "a.mp3", "audio_start": 2.0, "duration": 3.0},
                {"id": "c3", "audio": "b.mp3", "audio_start": 0.0, "duration": 1.0},
            ]})
            with mock.patch.object(section_source, "ROOT", root):
                windows = cm._runtime_windows("casegame", "full")
        self.assertEqual(2.0, windows["c1"])
        self.assertEqual(3.0, windows["c2"])
        self.assertEqual(1.0, windows["c3"])

    def test_timing_causal_order_min_span_window_and_conflict(self):
        usage = ml.MaterialUsage(
            track="t", section="s", cue="c", use_index=0, step=None,
            material="m", kind="action", variant=None, mode="continue",
            path=Path("x"), where="c use[0]",
        )
        events = [create_event("a", count=1), create_event("a", count=1)]
        timeline = [self._snapshot([]), self._snapshot([self._item("i1", "a")]),
                    self._snapshot([self._item("i1", "a"), self._item("i2", "a")])]
        usage.events = events
        report: list[str] = []
        cm._check_timing(usage, SimpleNamespace(timing={}), [1.0, 0.5], None,
                         timeline, report)
        self.assertTrue(any("causal order" in e for e in report), report)

        report = []
        cm._check_timing(usage, SimpleNamespace(timing={"min_span": 1.0}),
                         [0.0, 0.2], None, timeline, report)
        self.assertTrue(any("min_span" in e for e in report), report)

        report = []
        one_event_usage = ml.MaterialUsage(
            track="t", section="s", cue="c", use_index=0, step=None,
            material="m", kind="action", variant=None, mode="continue",
            path=Path("x"), where="c use[0]",
            events=[dict(create_event("a", count=1), dur=0.5)],
        )
        cm._check_timing(one_event_usage, SimpleNamespace(timing={}), [0.0], 0.2,
                         timeline[:2], report)
        self.assertTrue(any("beyond cue duration" in e for e in report), report)

        conflict_usage = ml.MaterialUsage(
            track="t", section="s", cue="c", use_index=0, step=None,
            material="m", kind="action", variant=None, mode="continue",
            path=Path("x"), where="c use[0]",
            events=[dict(create_event("a", count=1), dur=0.5),
                    dict(create_event("a", count=1), dur=0.5)],
        )
        report = []
        cm._check_timing(conflict_usage, SimpleNamespace(timing={"conflict_check": True}),
                         [0.0, 0.2], None, timeline, report)
        self.assertTrue(any("conflicting state writes" in e for e in report), report)


if __name__ == "__main__":
    unittest.main()
