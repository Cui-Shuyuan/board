import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("rules_capability_report", ROOT / "rules_capability_report.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RulesCapabilityReportTests(unittest.TestCase):
    def test_synthetic_heterogeneous_content_reports_structure_and_gaps(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "ontology").mkdir(parents=True)
            (root / "games" / "synthetic").mkdir(parents=True)
            (root / "ontology" / "concepts.json").write_text(
                json.dumps({"concepts": [{"id": "base_type"}]}), encoding="utf-8")
            (root / "games" / "synthetic" / "concepts.json").write_text(
                json.dumps({
                    "objects": [
                        {"id": "owner", "members": [{"id": "a"}, {"id": "b"}]},
                        {"id": "child", "specifies": "<base_type>"},
                    ],
                    "actions": [
                        {
                            "id": "act",
                            "description": {"zh": "使用 <missing_ref>"},
                            "<ontology::condition>": "<child>",
                            "<ontology::cost>": "<child>",
                            "target": "<base_type>",
                        },
                        {
                            "id": "description_only",
                            "description": {"zh": "只有文字"},
                        },
                    ],
                }), encoding="utf-8")
            (root / "games" / "synthetic" / "flow.json").write_text(
                json.dumps({
                    "procedures": [{
                        "id": "game",
                        "options": [
                            {"id": "step", "loop": {"count": 1}},
                            {"id": "step", "loop": {"until": "end"}},
                        ],
                        "score_table": {"by_count": []},
                    }]
                }), encoding="utf-8")

            report = module.build_report(root)
            game = report["games"][0]

            self.assertEqual(game["flow"]["branches"], 1)
            self.assertEqual(game["flow"]["loops"], 2)
            self.assertIn("step", game["flow"]["duplicate_local_ids"])
            self.assertGreaterEqual(game["members"]["explicit_collection_owners"], 1)
            self.assertGreaterEqual(game["rules"]["condition_fields"], 1)
            self.assertGreaterEqual(game["rules"]["cost_fields"], 1)
            self.assertGreaterEqual(game["rules"]["target_fields"], 1)
            self.assertTrue(game["rules"]["score_table_present"])
            self.assertGreaterEqual(game["rules"]["natural_language_only_nodes"], 1)
            self.assertIn("unresolved_concept_references", game["actionable_gaps"])
            self.assertIn("duplicate_flow_local_ids_require_canonical_paths", game["actionable_gaps"])


if __name__ == "__main__":
    unittest.main()
