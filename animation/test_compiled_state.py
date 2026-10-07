#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for the shared compiled-state replay helpers."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import compiled_state as cs  # noqa: E402


def comp(cid, zone="showcase"):
    return {"Id": cid, "TemplateId": "tpl", "ZoneId": zone, "Order": 0}


class ApplyStateOpsTests(unittest.TestCase):
    def test_apply_put_remove_and_time_window(self):
        start = {"components": [comp("a"), comp("b")]}
        ops = [
            {"op": "put", "at": 0.0, "item": comp("c")},
            {"op": "remove", "at": 1.0, "item_id": "a"},
            {"op": "put", "at": 2.0, "item": comp("d")},
        ]

        self.assertEqual({"a", "b", "c"}, set(cs.apply_state_ops(start, ops, 0.5)))
        self.assertEqual({"b", "c"}, set(cs.apply_state_ops(start, ops, 1.0)))
        self.assertEqual({"b", "c", "d"}, set(cs.apply_state_ops(start, ops)))

    def test_unknown_ops_are_ignored(self):
        start = {"components": [comp("a")]}
        ops = [{"op": "camera", "at": 0.0}, {"op": "remove", "at": 0.5, "item_id": "a"}]
        self.assertEqual(set(), set(cs.apply_state_ops(start, ops)))

    def test_iter_renderable_states_yields_start_and_each_mutation(self):
        cue = {
            "id": "cue.1",
            "start_state": {"components": [comp("a")]},
            "state_ops": [
                {"op": "put", "at": 0.0, "item": comp("b")},
                {"op": "camera", "at": 0.5},
                {"op": "remove", "at": 1.0, "item_id": "a"},
            ],
        }

        states = list(cs.iter_renderable_states(cue))

        self.assertEqual(3, len(states))
        self.assertEqual((0.0, True), (states[0][0], states[0][2]))
        self.assertEqual({"a", "b"}, {c["Id"] for c in states[1][1]})
        self.assertEqual((1.0, False), (states[2][0], states[2][2]))
        self.assertEqual({"b"}, {c["Id"] for c in states[2][1]})


class ResolveEffectiveStateSourceTests(unittest.TestCase):
    BY_ID = {"a": {}, "b": {}}

    def test_entry_initial_wins(self):
        source = cs.resolve_effective_state_source(
            {"id": "c", "entry": "initial", "parent": "a"}, by_id=self.BY_ID)
        self.assertEqual(("initial", None), (source["kind"], source["id"]))

    def test_entry_cue_wins(self):
        source = cs.resolve_effective_state_source(
            {"id": "c", "entry": "b", "parent": "a"}, by_id=self.BY_ID)
        self.assertEqual(("cue", "b"), (source["kind"], source["id"]))

    def test_cut_resets_to_initial(self):
        source = cs.resolve_effective_state_source(
            {"id": "c", "transition": "cut", "parent": "a"}, by_id=self.BY_ID)
        self.assertEqual(("initial", None), (source["kind"], source["id"]))

    def test_parent_then_previous_then_implicit(self):
        parent = cs.resolve_effective_state_source(
            {"id": "c", "parent": "a"}, by_id=self.BY_ID, prev_id="b")
        prev = cs.resolve_effective_state_source(
            {"id": "c"}, by_id=self.BY_ID, prev_id="b")
        implicit = cs.resolve_effective_state_source(
            {"id": "c"}, by_id=self.BY_ID, implicit_initial=True)
        self.assertEqual("a", parent["id"])
        self.assertEqual("b", prev["id"])
        self.assertEqual("implicit:initial", implicit["label"])

    def test_strict_missing_entry_and_parent_raise(self):
        with self.assertRaises(ValueError):
            cs.resolve_effective_state_source(
                {"id": "c", "entry": "missing"}, by_id=self.BY_ID, strict=True)
        with self.assertRaises(ValueError):
            cs.resolve_effective_state_source(
                {"id": "c", "parent": "missing"}, by_id=self.BY_ID, strict=True)


if __name__ == "__main__":
    unittest.main()
