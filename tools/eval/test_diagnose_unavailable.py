import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DiagnoseUnavailableTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.diag = load("diagnose_unavailable")
        cls.layers = load("analyze_evidence_layers")

    def test_identity_does_not_match_by_last_segment(self):
        self.assertIsNone(self.diag.match_identity("feeding", ["actions/harvest/feeding"]))
        self.assertIs(self.diag.match_identity("actions/feeding", ["actions/feeding"]), True)
        self.assertIsNone(self.layers.match_identity("feeding", ["actions/feeding"]))
        self.assertIs(self.layers.match_identity("actions/feeding", ["actions/feeding"]), True)

    def test_delivered_evidence_wins_over_retrieval_miss(self):
        run = {
            "run_id": "r1",
            "question_id": "q1",
            "game": "g",
            "question": "q",
            "version": "new",
            "expected_rule_ids": ["actions/feeding"],
            "orchestration": {
                "outcome": "unavailable",
                "questions": [
                    {
                        "retrievalCandidatePaths": [],
                        "evidenceCanonicalPaths": ["actions/feeding"],
                    }
                ],
            },
        }
        result = self.diag.classify(run)
        self.assertEqual(result["pack_expected"], True)
        self.assertEqual(result["bucket"], "expected_target_delivered_but_unavailable")
        self.assertEqual(result["earliest_failure_hint"], "respond_or_sufficiency")

    def test_retrieval_hit_without_delivery_is_navigation_failure(self):
        run = {
            "run_id": "r2",
            "question_id": "q2",
            "game": "g",
            "question": "q",
            "version": "new",
            "expected_rule_ids": ["actions/feeding"],
            "orchestration": {
                "outcome": "unavailable",
                "questions": [
                    {
                        "retrievalCandidatePaths": ["actions/feeding"],
                        "evidenceCanonicalPaths": [],
                    }
                ],
            },
        }
        result = self.diag.classify(run)
        self.assertEqual(result["bucket"], "retrieval_hit_not_formally_delivered")
        self.assertEqual(result["earliest_failure_hint"], "navigation_or_formal_read")

    def test_missing_observation_is_unknown(self):
        run = {
            "run_id": "r3",
            "question_id": "q3",
            "game": "g",
            "question": "q",
            "version": "new",
            "expected_rule_ids": ["actions/feeding"],
            "orchestration": {"outcome": "unavailable"},
        }
        result = self.diag.classify(run)
        self.assertEqual(result["bucket"], "unknown_observability")
        self.assertIsNone(result["retrieval_expected"])
        self.assertIsNone(result["pack_expected"])


if __name__ == "__main__":
    unittest.main()
