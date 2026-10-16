#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for reusable tutorial materials (tutorial-material/v1)."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import material_library as ml  # noqa: E402
import section_source  # noqa: E402


def write(path: Path, doc) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def action_doc(aid="fake.seed", **over):
    doc = {
        "schema": "tutorial-material/v1",
        "id": aid,
        "kind": "action",
        "game": "fake",
        "title": "seed objects",
        "roles": [{"name": "zone", "type": "zone", "required": False, "default": "box"}],
        "params": [{"name": "count", "type": "integer", "required": False, "default": 1}],
        "steps": [{"id": "seed", "events": [{
            "op": "create",
            "count": "$count",
            "to": "face_up",
            "anchor": "@add",
            "target": {"space": "entity", "zone": "$zone", "template": "piece",
                       "palette": "plain", "concept": "piece"},
        }]}],
        "contract": {"postconditions": {"zones": {"$zone": {"count": "$count"}}}},
    }
    doc.update(over)
    return doc


def presentation_doc(aid="fake.show"):
    return {
        "schema": "tutorial-material/v1",
        "id": aid,
        "kind": "presentation",
        "game": "fake",
        "steps": [{"id": "show", "events": [{
            "op": "show", "dur": 0.0, "anchor": "@show",
            "target": {"space": "screen", "id": "@pin"},
            "rect": {"x": 0, "y": 0, "w": 0.1, "h": 0.1},
        }]}],
    }


def example_doc(aid="fake.round"):
    return {
        "schema": "tutorial-material/v1",
        "id": aid,
        "kind": "example",
        "game": "fake",
        "steps": [{"id": "do", "use": [{
            "ref": "fake.seed",
            "bind": {"zone": "${zone}", "count": "${count}"},
        }]}],
        "roles": [{"name": "zone", "type": "zone", "required": False, "default": "box"}],
        "params": [{"name": "count", "type": "integer", "required": False, "default": 1}],
        "facts": {"zones": {"box": {"count": 1}}},
    }


class MaterialLibraryTests(unittest.TestCase):
    def _tutorial(self, root: Path) -> Path:
        tut = root / "content/games/fake/tutorial"
        library = tut / "library"
        write(library / "actions/fake.seed.action.json", action_doc())
        return tut

    def test_expands_bindings_timing_and_generated_anchor(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            lib = ml.load_materials("fake", tut)
            anim = {
                "cues": [{
                    "id": "c1",
                    "entry": "initial",
                    "events": [{"op": "camera", "shot": "s", "at": 0.0}],
                    "use": [{
                        "ref": "fake.seed",
                        "bind": {"zone": "board", "count": 3},
                        "timing": {"add": {"beat": "b1", "edge": "start"}},
                        "insert_at": 1,
                    }],
                }],
            }
            out, usages, files = lib.expand_section("sec", "tr", anim)
            cue = out["cues"][0]
            self.assertEqual([e["op"] for e in cue["events"]], ["camera", "create"])
            ev = cue["events"][1]
            self.assertEqual(3, ev["count"])
            self.assertEqual("board", ev["target"]["zone"])
            self.assertEqual("sec/c1.b1.start", ev["anchor"])
            defs = {a["id"]: a for a in out["time_anchors"]}
            self.assertEqual("sec/c1.b1", defs["sec/c1.b1.start"]["beat"])
            self.assertEqual(1, len(usages))
            self.assertEqual("fake.seed", usages[0].material)
            self.assertIn("fake.seed", usages[0].source_chain())
            self.assertIn("fake.seed.action.json", files[0].name)

    def test_numeric_local_time_binds_explicit_at(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            lib = ml.load_materials("fake", tut)
            anim = {"cues": [{
                "id": "c1", "entry": "initial",
                "use": [{"ref": "fake.seed", "timing": {"add": {"at": 0.75, "offset": 0.25}}}],
            }]}
            out, _, _ = lib.expand_section("sec", "tr", anim)
            ev = out["cues"][0]["events"][0]
            self.assertEqual(1.0, ev["at"])
            self.assertNotIn("anchor", ev)
            self.assertEqual([], out.get("time_anchors") or [])

    def test_missing_timing_unknown_ref_and_cycle_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            write(tut / "library/actions/fake.loop.action.json", action_doc(
                aid="fake.loop",
                steps=[{"id": "loop", "use": [{"ref": "fake.loop"}]}],
            ))
            with self.assertRaisesRegex(ml.MaterialError, "cycle"):
                ml.load_materials("fake", tut)

            # unknown ref is rejected at load time
            write(tut / "library/actions/fake.loop.action.json", action_doc(
                aid="fake.loop",
                steps=[{"id": "loop", "use": [{"ref": "fake.missing"}]}],
            ))
            with self.assertRaisesRegex(ml.MaterialError, "unknown material"):
                ml.load_materials("fake", tut)

    def test_missing_timing_raises_with_source_chain(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            lib = ml.load_materials("fake", tut)
            anim = {"cues": [{"id": "c1", "entry": "initial",
                              "use": [{"ref": "fake.seed"}]}]}
            with self.assertRaisesRegex(ml.MaterialError, "timing marker '@add'"):
                lib.expand_section("sec", "tr", anim)

    def test_action_and_presentation_separation(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            bad_action = action_doc(aid="fake.bad_action")
            bad_action["steps"][0]["events"][0]["op"] = "show"
            (tut / "library/actions/fake.bad_action.action.json").write_text(
                json.dumps(bad_action, ensure_ascii=False), encoding="utf-8")
            with self.assertRaisesRegex(ml.MaterialError, "non-state op"):
                ml.load_materials("fake", tut)

            write(tut / "library/actions/fake.bad_action.action.json",
                  action_doc(aid="fake.bad_action"))
            bad_pres = presentation_doc(aid="fake.bad_pres")
            bad_pres["steps"][0]["events"][0]["op"] = "create"
            write(tut / "library/presentations/fake.bad_pres.presentation.json", bad_pres)
            with self.assertRaisesRegex(ml.MaterialError, "non-presentation"):
                ml.load_materials("fake", tut)

    def test_repeated_instance_namespaces_local_screen_ids(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            write(tut / "library/presentations/fake.show.presentation.json", presentation_doc())
            lib = ml.load_materials("fake", tut)
            anim = {"cues": [{
                "id": "c1", "entry": "initial",
                "use": [
                    {"ref": "fake.show", "timing": {"show": {"at": 0.2}}},
                    {"ref": "fake.show", "timing": {"show": {"at": 0.4}}, "as": "second"},
                ],
            }]}
            out, _, _ = lib.expand_section("sec", "tr", anim)
            ids = [e["target"]["id"] for e in out["cues"][0]["events"]]
            self.assertEqual(["u1__pin", "second__pin"], ids)

    def test_example_variant_selects_variant_steps(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            example = example_doc(aid="fake.variant")
            example["steps"] = []
            example["variants"] = [
                {"id": "left", "steps": [{"id": "seed", "events": action_doc()["steps"][0]["events"]}]},
                {"id": "right", "steps": [{"id": "seed", "events": [dict(action_doc()["steps"][0]["events"][0], target={"space": "entity", "zone": "right", "template": "piece"})]}]},
            ]
            write(tut / "library/examples/fake.variant.example.json", example)
            lib = ml.load_materials("fake", tut)
            base = {"cues": [{"id": "c1", "entry": "initial",
                               "use": [{"ref": "fake.variant", "variant": "right",
                                        "timing": {"add": {"at": 1.0}}}]}]}
            out, _, _ = lib.expand_section("sec", "tr", base)
            self.assertEqual("right", out["cues"][0]["events"][0]["target"]["zone"])
            bad = {"cues": [{"id": "c1", "entry": "initial",
                             "use": [{"ref": "fake.variant", "variant": "nope"}]}]}
            with self.assertRaisesRegex(ml.MaterialError, "no variant"):
                lib.expand_section("sec", "tr", bad)

    def test_contract_placeholders_are_resolved(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            lib = ml.load_materials("fake", tut)
            anim = {"cues": [{
                "id": "c1", "entry": "initial",
                "use": [{"ref": "fake.seed", "bind": {"zone": "board", "count": 2},
                         "timing": {"add": {"at": 0.1}}}],
            }]}
            out, usages, _ = lib.expand_section("sec", "tr", anim)
            contract = usages[0].contract
            self.assertEqual({"count": 2}, contract["postconditions"]["zones"]["board"])
            self.assertEqual("board", usages[0].bindings["zone"])


    def test_unknown_use_field_and_illegal_binding(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            action = action_doc(aid="fake.cap")
            action["roles"] = [{"name": "zone", "type": "zone", "required": True,
                                "accepts": ["allowed"]}]
            action["steps"] = [{"id": "seed", "events": [{
                "op": "create", "count": 1, "anchor": "@add",
                "target": {"space": "entity", "zone": "$zone", "template": "piece"},
            }]}]
            write(tut / "library/actions/fake.cap.action.json", action)
            lib = ml.load_materials("fake", tut)
            bad_bind = {"cues": [{"id": "c1", "entry": "initial",
                                  "use": [{"ref": "fake.cap", "bind": {"zone": "other"},
                                           "timing": {"add": {"at": 0.1}}}]}]}
            with self.assertRaisesRegex(ml.MaterialError, "accepts"):
                lib.expand_section("sec", "tr", bad_bind)
            bad_field = {"cues": [{"id": "c1", "entry": "initial",
                                   "use": [{"ref": "fake.cap", "bogus": 1}]}]}
            with self.assertRaisesRegex(ml.MaterialError, "unknown use field"):
                lib.expand_section("sec", "tr", bad_field)

    def test_optional_steps_can_be_omitted_but_required_steps_cannot(self):
        with tempfile.TemporaryDirectory() as tmp:
            tut = self._tutorial(Path(tmp))
            action = action_doc(aid="fake.steps")
            action["steps"] = [
                {"id": "required", "events": [{
                    "op": "create", "count": 1, "anchor": "@add",
                    "target": {"space": "entity", "zone": "box", "template": "piece"}}]},
                {"id": "optional", "status": "optional", "events": [{
                    "op": "create", "count": 1, "anchor": "@add",
                    "target": {"space": "entity", "zone": "box", "template": "piece"}}]},
            ]
            write(tut / "library/actions/fake.steps.action.json", action)
            lib = ml.load_materials("fake", tut)
            good = {"cues": [{"id": "c1", "entry": "initial",
                              "use": [{"ref": "fake.steps", "omit": ["optional"],
                                       "timing": {"add": {"at": 0.1}}}]}]}
            out, _, _ = lib.expand_section("sec", "tr", good)
            self.assertEqual(1, len(out["cues"][0]["events"]))
            bad = {"cues": [{"id": "c1", "entry": "initial",
                             "use": [{"ref": "fake.steps", "omit": ["required"]}]}]}
            with self.assertRaisesRegex(ml.MaterialError, "required and cannot be omitted"):
                lib.expand_section("sec", "tr", bad)

    def test_section_source_hash_tracks_only_referenced_materials(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            tut = root / "content/games/fake/tutorial"
            self._tutorial(root)
            # second action, and two tracks: one uses fake.seed, one uses fake.other
            write(tut / "library/actions/fake.other.action.json", action_doc(aid="fake.other"))
            for track, ref in (("a", "fake.example.materials"), ("b", "fake.other")):
                write(tut / f"shared/{track}.base.anim.json", {
                    "schema": "tutorial-anim/v2", "kind": "animation_track",
                    "game": "fake", "track": track, "default_tree": "main",
                    "worlds": [], "trees": [],
                })
                write(tut / f"tracks/{track}.track.json", {
                    "schema": "tutorial-track/v1", "game": "fake", "track": track,
                    "title": track, "version": "0.1.0",
                    "base_anim": f"../shared/{track}.base.anim.json",
                    "sections": ["s"],
                })
                write(tut / "sections/s/section.json", {
                    "id": "s", "title": "s", "group_path": ["s"], "order": 0, "tree": "main",
                })
                write(tut / "sections/s/a.script.json", {
                    "schema_version": 1, "game_id": "fake", "track": "a", "section": "s",
                    "cues": [{"id": "c1", "beats": []}],
                })
                write(tut / "sections/s/b.script.json", {
                    "schema_version": 1, "game_id": "fake", "track": "b", "section": "s",
                    "cues": [{"id": "c1", "beats": []}],
                })
                use = ({"ref": "fake.example.materials", "timing": {"add": {"at": 0.2}}}
                       if track == "a" else {"ref": "fake.other", "timing": {"add": {"at": 0.2}}})
                cue = {"id": "c1", "entry": "initial", "events": [], "use": [use]}
                if track == "a":
                    cue["example"] = {"ref": "fake.example.materials"}
                write(tut / f"sections/s/{track}.anim.json", {"section": "s", "cues": [cue]})
                # Minimal example wrapper on track a (created lazily below).
                if track == "a":
                    write(tut / "library/examples/fake.example.materials.example.json", example_doc("fake.example.materials"))
            # fix example wrapper: it seeds fake.seed
            example_path = tut / "library/examples/fake.example.materials.example.json"
            ex = json.loads(example_path.read_text(encoding="utf-8"))
            ex["steps"] = [{"id": "do", "use": [{"ref": "fake.seed", "bind": {"zone": "${zone}", "count": "${count}"}}]}]
            write(example_path, ex)

            with mock.patch.object(section_source, "ROOT", root):
                sa = section_source.load_track_source("fake", "a")
                sb = section_source.load_track_source("fake", "b")
                ha, hb = sa.source_hash, sb.source_hash
                # Change a shared action reachable from track a's example.
                path = tut / "library/actions/fake.seed.action.json"
                doc = json.loads(path.read_text(encoding="utf-8"))
                doc["title"] = "changed title"
                write(path, doc)
                sa2 = section_source.load_track_source("fake", "a")
                sb2 = section_source.load_track_source("fake", "b")
            self.assertNotEqual(ha, sa2.source_hash)
            self.assertEqual(hb, sb2.source_hash)


if __name__ == "__main__":
    unittest.main()
