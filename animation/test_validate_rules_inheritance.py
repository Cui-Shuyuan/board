#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Source-scoped inheritance regressions for tools/content/validate_rules.py."""
from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_SPEC = importlib.util.spec_from_file_location(
    "validate_rules_inheritance", ROOT / "tools" / "content" / "validate_rules.py")
assert _SPEC and _SPEC.loader
validate_rules = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(validate_rules)


class SourceScopeTests(unittest.TestCase):
    def test_game_concepts_and_instances_share_one_scope(self):
        self.assertEqual("ontology", validate_rules.source_scope("content/ontology/concepts.json"))
        self.assertEqual("ontology", validate_rules.source_scope("content/ontology/flow.json"))
        self.assertEqual(
            validate_rules.source_scope("content/games/splendor/concepts.json"),
            validate_rules.source_scope("content/games/splendor/instances.json"),
        )
        self.assertNotEqual(
            validate_rules.source_scope("content/games/splendor/concepts.json"),
            validate_rules.source_scope("content/games/seasons/concepts.json"),
        )

    def test_local_parent_preferred_then_ontology_fallback(self):
        v = validate_rules.Validator(None, False)
        v.concept_fields = {
            ("game:demo", "local_parent"): set(),
            ("ontology", "ont_parent"): set(),
            ("game:demo", "child_local"): set(),
            ("game:demo", "child_ont"): set(),
        }
        v._raw_parent_ref = {
            ("game:demo", "child_local"): "<local_parent>",
            ("game:demo", "child_ont"): "<ont_parent>",
        }
        v._resolve_inheritance()
        self.assertEqual(("game:demo", "local_parent"), v.concept_parent[("game:demo", "child_local")])
        self.assertEqual(("ontology", "ont_parent"), v.concept_parent[("game:demo", "child_ont")])

    def test_same_id_in_different_games_are_independent_e11_branches(self):
        v = validate_rules.Validator(None, False)
        v.concept_fields = {
            ("ontology", "resource"): {"limited_supply"},
            ("game:A", "gold"): set(),
            ("game:B", "gold"): {"limited_supply"},
        }
        v.concept_parent = {
            ("game:A", "gold"): ("ontology", "resource"),
            ("game:B", "gold"): ("ontology", "resource"),
        }
        v.definition_nodes = set()
        v.check_e11_w05([{
            "id": "resource",
            "constraints": {"required": [{"id": "limited_supply", "type": "boolean"}]},
        }])
        errors = [issue for issue in v.issues if issue[0] == "ERROR"]
        self.assertEqual(1, len(errors))
        self.assertIn("gold", errors[0][2])


if __name__ == "__main__":
    unittest.main()
