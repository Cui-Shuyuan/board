import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("compare_versions", ROOT / "compare_versions.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class CompareVersionIdentityTests(unittest.TestCase):
    def test_last_segment_is_not_a_hit(self):
        self.assertIsNone(module.matches_expected("feeding", ["actions/harvest/feeding"]))
        self.assertIs(module.matches_expected("actions/harvest/feeding", ["actions/harvest/feeding"]), True)

    def test_layered_contract_can_be_semantically_unevaluable(self):
        self.assertIsNone(module.matches_any({"feeding"}, ["actions/harvest/feeding"]))
        self.assertFalse(module.matches_any({"feeding"}, ["actions/harvest"]))


    def test_timeline_and_ledger_fields_are_extracted(self):
        response = {
            "evidence": {"queries": []},
            "orchestration": {
                "reusedToolCallCount": 1,
                "executedToolCallCount": 2,
                "deliveredEvidenceChars": 1234,
                "timeline": [
                    {"category": "model", "durationMs": 100},
                    {"category": "tool", "durationMs": 20},
                ],
                "timelineAccounting": {"isReconciled": True, "coveredMs": 120},
            },
        }
        result = module.analyze_evidence({}, response)
        self.assertEqual(result["reused_tool_call_count"], 1)
        self.assertEqual(result["executed_tool_call_count"], 2)
        self.assertEqual(result["delivered_evidence_chars"], 1234)
        self.assertEqual(result["model_span_ms"], 100)
        self.assertEqual(result["tool_span_ms"], 20)
        self.assertTrue(result["timeline_reconciled"])


if __name__ == "__main__":
    unittest.main()
