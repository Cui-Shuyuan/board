#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""REV-04 regression tests: QA merge/change gate and validate_rules exit code.

All Board API calls are mocked.  Tests build temporary game roots or pass
already-loaded cue documents, so they never touch real content/.
"""
from __future__ import annotations

import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))

import compile_tutorial as ct  # noqa: E402
import qa_anim_ask as qa  # noqa: E402

_VALIDATE_RULES_PATH = ROOT / "tools" / "content" / "validate_rules.py"
_SPEC = importlib.util.spec_from_file_location("rev04_validate_rules", _VALIDATE_RULES_PATH)
assert _SPEC and _SPEC.loader
validate_rules = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(validate_rules)


class _FakeValidator:
    issues: list[tuple[str, str, str]] = []

    def __init__(self, *args, **kwargs):
        pass

    def run(self):
        return 0


class QaChangeDetectionTests(unittest.TestCase):
    def _cue(self, cue_id: str = "cue.1", **extra):
        cue = {
            "id": cue_id,
            "parent": "cue.0",
            "tree": "main",
            "script": {"story": "same", "beats": [{"text": "same"}]},
            "events": [],
        }
        cue.update(extra)
        return cue

    def _select(self, before: dict, after: dict, tts_ids=None):
        return ct.select_qa_changed_ids(
            {"cues": [after]}, {"cues": [before]}, tts_ids or [])

    def test_state_event_change_triggers_without_tts_change(self):
        for op in ("create", "ensure", "destroy", "transfer", "stack",
                   "shuffle", "move_order", "set_order", "set_face", "take"):
            with self.subTest(op=op):
                before = self._cue(events=[{"op": op, "quantity": 1}])
                after = self._cue(events=[{"op": op, "quantity": 2}])
                self.assertEqual(["cue.1"], self._select(before, after))

    def test_presentation_only_change_does_not_trigger(self):
        for op in ("camera", "wait", "label", "point", "shape", "fade",
                   "scale", "highlight", "overlay_show", "overlay_hide",
                   "magnifier", "show", "hide"):
            with self.subTest(op=op):
                before = self._cue(events=[{"op": op, "dur": 1}])
                after = self._cue(events=[{"op": op, "dur": 2}])
                self.assertEqual([], self._select(before, after))

    def test_contract_and_inheritance_changes_trigger(self):
        cases = [
            ("entry", "initial", "other"),
            ("parent", "cue.0", "cue.other"),
            ("transition", "continue", "cut"),
            ("script_enter", {"gems": 4}, {"gems": 5}),
            ("script_exit", {"gems": 4}, {"gems": 5}),
        ]
        for field, old, new in cases:
            with self.subTest(field=field):
                before = self._cue()
                after = self._cue()
                if field == "script_enter":
                    before["script"]["enter"] = old
                    after["script"]["enter"] = new
                elif field == "script_exit":
                    before["script"]["exit"] = old
                    after["script"]["exit"] = new
                else:
                    before[field] = old
                    after[field] = new
                self.assertEqual(["cue.1"], self._select(before, after))

    def test_inherited_contract_change_selects_child_cue(self):
        before = {"cues": [
            {"id": "p", "tree": "main", "script": {"exit": {"x": 1}}, "events": []},
            {"id": "c", "parent": "p", "tree": "main", "events": []},
        ]}
        after = {"cues": [
            {"id": "p", "tree": "main", "script": {"exit": {"x": 2}}, "events": []},
            {"id": "c", "parent": "p", "tree": "main", "events": []},
        ]}
        self.assertEqual(["p", "c"], ct.select_qa_changed_ids(after, before, []))

    def test_tts_changed_ids_are_kept_even_without_state_change(self):
        cue = self._cue()
        self.assertEqual(["cue.1"], ct.select_qa_changed_ids(
            {"cues": [cue]}, {"cues": [cue]}, ["cue.1"]))

    def test_missing_baseline_treats_all_cues_as_changed(self):
        doc = {"cues": [self._cue("cue.1"), self._cue("cue.2")]}
        self.assertEqual(
            ["cue.1", "cue.2"],
            ct.select_qa_changed_ids(doc, None, []),
        )


class QaMergeTests(unittest.TestCase):
    def test_cue_qa_accepts_string_object_and_list(self):
        doc = {"cues": [
            {"id": "cue.1", "qa": "single question"},
            {"id": "cue.2", "qa": {"q": "object question", "expect": "合法"}},
            {"id": "cue.3", "qa": ["list one", {"q": "list two", "expect": "不允许"}]},
        ]}
        asks = ct.cue_qa_asks(doc, "anim.json")
        self.assertEqual(4, len(asks))
        self.assertEqual("single question", asks[0]["q"])
        self.assertEqual("合法", asks[1]["expect"])
        self.assertEqual("list two", asks[3]["q"])
        self.assertEqual("cue.3", asks[3]["cue"])

    def test_external_primary_cue_and_original_cue_are_preserved(self):
        spec = {"asks": [{"cue": "cue.1#2", "q": "external question",
                          "expect": "允许"}]}
        asks = ct.external_qa_asks(spec, "_qa/questions.json")
        self.assertEqual(1, len(asks))
        self.assertEqual("cue.1", asks[0]["cue"])
        self.assertEqual("cue.1#2", asks[0]["original_cue"])
        self.assertEqual("允许", asks[0]["expect"])

    def test_merge_dedupes_and_keeps_expect_and_sources(self):
        builtin = ct.cue_qa_asks({"cues": [
            {"id": "cue.1", "qa": ["same question", "builtin only"]},
        ]}, "anim.json")
        external = ct.external_qa_asks({"asks": [
            {"cue": "cue.1", "q": "same question", "expect": "允许"},
            {"cue": "cue.2#1", "q": "external only"},
        ]}, "_qa/questions.json")
        merged = ct.merge_qa_asks(builtin, external)
        by_q = {item["q"]: item for item in merged}
        self.assertEqual(3, len(merged))
        self.assertEqual("允许", by_q["same question"]["expect"])
        self.assertEqual(2, len(by_q["same question"]["sources"]))
        self.assertEqual("cue.2", by_q["external only"]["cue"])
        self.assertEqual("cue.2#1", by_q["external only"]["original_cue"])


class QaGateTests(unittest.TestCase):
    def _write_external(self, root: Path, game: str, spec: dict) -> Path:
        path = root / "content" / "games" / game / "tutorial" / "anim" / "_qa" / "questions.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(spec, ensure_ascii=False), encoding="utf-8")
        return path

    def _capture_api_asks(self):
        captured = {}

        def fake_main(argv):
            inp = Path(argv[argv.index("--in") + 1])
            captured["asks"] = json.loads(inp.read_text(encoding="utf-8"))["asks"]
            return 0

        return captured, fake_main

    def test_missing_required_question_fails_without_api(self):
        doc = {"cues": [{"id": "cue.1", "parent": "initial", "events": []}]}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main") as api:
                rc = ct.run_qa_gate("fake", "full", ["cue.1"], track_doc=doc)
        self.assertNotEqual(0, rc)
        api.assert_not_called()

    def test_qa_exempt_true_cue_without_question_skips_gate(self):
        doc = {"cues": [{
            "id": "intro.1", "parent": "initial", "tree": "main",
            "qa_exempt": True, "events": [],
        }]}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main") as api:
                rc = ct.run_qa_gate("fake", "full", ["intro.1"], track_doc=doc)
        self.assertEqual(0, rc)
        api.assert_not_called()

    def test_qa_exempt_false_still_requires_question(self):
        doc = {"cues": [{
            "id": "intro.1", "parent": "initial", "tree": "main",
            "qa_exempt": False, "events": [],
        }]}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main") as api:
                rc = ct.run_qa_gate("fake", "full", ["intro.1"], track_doc=doc)
        self.assertNotEqual(0, rc)
        api.assert_not_called()

    def test_qa_exempt_is_local_and_not_inherited(self):
        doc = {"cues": [
            {"id": "intro.1", "parent": "initial", "tree": "main",
             "qa_exempt": True, "events": []},
            {"id": "next.1", "parent": "intro.1", "tree": "main", "events": []},
        ]}
        resolved = ct.schema.resolve_track(doc)
        by_id = {cue["id"]: cue for cue in resolved["cues"]}
        self.assertTrue(by_id["intro.1"].get("qa_exempt"))
        self.assertIsNone(by_id["next.1"].get("qa_exempt"))

    def test_stale_external_cue_fails_without_api(self):
        doc = {"cues": [{"id": "cue.1", "parent": "initial", "events": []}]}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = self._write_external(
                root, "fake", {"asks": [{"cue": "stale.001", "q": "?"}]})
            self.assertTrue(path.exists())
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main") as api:
                rc = ct.run_qa_gate("fake", "full", [], track_doc=doc)
        self.assertNotEqual(0, rc)
        api.assert_not_called()

    def test_missing_external_file_uses_cue_qa(self):
        doc = {"cues": [{
            "id": "cue.1", "parent": "initial", "events": [],
            "qa": [{"q": "builtin question", "expect": "合法"}],
        }]}
        captured, fake_main = self._capture_api_asks()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main", side_effect=fake_main):
                rc = ct.run_qa_gate("fake", "full", ["cue.1"], track_doc=doc)
        self.assertEqual(0, rc)
        self.assertEqual("合法", captured["asks"][0]["expect"])
        self.assertEqual("cue.1", captured["asks"][0]["cue"])

    def test_gate_merges_builtin_and_external_and_preserves_expect(self):
        doc = {"cues": [
            {"id": "cue.1", "parent": "initial", "events": [],
             "qa": ["same question"]},
            {"id": "cue.2", "parent": "initial", "events": []},
        ]}
        captured, fake_main = self._capture_api_asks()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_external(root, "fake", {"asks": [
                {"cue": "cue.1", "q": "same question", "expect": "合法"},
                {"cue": "cue.2#1", "q": "external only", "expect": "有问题"},
            ]})
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(ct.qa_anim_ask, "main", side_effect=fake_main):
                rc = ct.run_qa_gate("fake", "full", None, track_doc=doc)
        self.assertEqual(0, rc)
        by_q = {item["q"]: item for item in captured["asks"]}
        self.assertEqual(2, len(captured["asks"]))
        self.assertEqual("合法", by_q["same question"]["expect"])
        self.assertEqual("cue.2", by_q["external only"]["cue"])

    def test_gate_closes_temp_fd_and_removes_temp_file(self):
        doc = {"cues": [{
            "id": "cue.1", "parent": "initial", "events": [],
            "qa": [{"q": "builtin question", "expect": "合法"}],
        }]}
        captured, fake_main = self._capture_api_asks()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fake_tmp = root / "compile_qa_fake.json"
            with mock.patch.object(ct, "ROOT", root), \
                    mock.patch.object(tempfile, "mkstemp",
                                      return_value=(123, str(fake_tmp))), \
                    mock.patch.object(ct.os, "close") as close_mock, \
                    mock.patch.object(ct.qa_anim_ask, "main", side_effect=fake_main):
                rc = ct.run_qa_gate("fake", "full", ["cue.1"], track_doc=doc)
            self.assertEqual(0, rc)
            close_mock.assert_called_once_with(123)
            self.assertFalse(fake_tmp.exists())
            self.assertEqual(1, len(captured["asks"]))


class ValidateRulesExitCodeTests(unittest.TestCase):
    def _run(self, issues):
        _FakeValidator.issues = issues
        with mock.patch.object(validate_rules, "Validator", _FakeValidator), \
                mock.patch.object(sys, "argv", ["validate_rules.py", "--errors-only"]), \
                mock.patch("sys.stdout", new_callable=io.StringIO) as out:
            rc = validate_rules.main()
        return rc, out.getvalue()

    def test_errors_only_returns_nonzero_on_error(self):
        rc, output = self._run([("ERROR", "content/x.json", "bad")])
        self.assertEqual(1, rc)
        self.assertIn("ERROR", output)
        self.assertIn("bad", output)

    def test_errors_only_returns_zero_on_warning_only(self):
        rc, output = self._run([("WARN", "content/x.json", "warn")])
        self.assertEqual(0, rc)
        self.assertIn("0 个错误, 1 个警告", output)

    def test_errors_only_returns_zero_without_issues(self):
        rc, output = self._run([])
        self.assertEqual(0, rc)
        self.assertIn("全部通过", output)


class QaVerdictTests(unittest.TestCase):
    def test_expected_vocabulary_ignores_later_explanatory_synonym(self):
        self.assertEqual("允许", qa.verdict_of(
            "允许。拿取宝石动作本身不检查上限，所以动作合法。", "允许"))
        self.assertEqual("有问题", qa.verdict_of(
            "有问题。被玩家B合法拿走后就归B所有。", "有问题"))

    def test_expected_vocabulary_prefers_last_standalone_self_correction(self):
        self.assertEqual("允许", qa.verdict_of(
            "不允许，我一开始看错了；按规则这是允许。", "允许"))

    def test_expected_vocabulary_normalizes_negated_problem_phrase(self):
        self.assertEqual("合法", qa.verdict_of("没有问题，这样合法。", "合法"))


class QaAnimAskLoaderTests(unittest.TestCase):
    def test_load_asks_accepts_single_qa_field(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "anim.json"
            path.write_text(json.dumps({"cues": [
                {"id": "cue.1", "qa": "single"},
                {"id": "cue.2", "qa": {"q": "object", "expect": "允许"}},
            ]}, ensure_ascii=False), encoding="utf-8")
            spec = qa.load_asks(path)
        self.assertEqual(2, len(spec["asks"]))
        self.assertEqual("single", spec["asks"][0]["q"])
        self.assertEqual("允许", spec["asks"][1]["expect"])


if __name__ == "__main__":
    unittest.main()
