import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("analyze_protocol_variants", ROOT / "analyze_protocol_variants.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
analyze_runs = module.analyze_runs
percentile = module.percentile


def row(*, latency, outcome="answer", http_status=200, model_calls=1,
        prompt=10, completion=5, reasoning=0, protocol_errors=None,
        repair_count=0, repair_ms=0.0, category_model=10.0):
    return {
        "latency_ms": latency,
        "http_status": http_status,
        "attempts": 1,
        "orchestration": {
            "outcome": outcome,
            "protocolErrors": protocol_errors or [],
            "protocolRepairModelCallCount": repair_count,
            "protocolRepairMs": repair_ms,
            "modelCallCount": model_calls,
            "executedToolCallCount": 1,
            "deliveredEvidenceChars": 100,
            "candidateAutoReadCount": 1,
            "modelCalls": [{
                "promptTokens": prompt,
                "completionTokens": completion,
                "reasoningTokens": reasoning,
            }],
            "timelineCategories": [{"category": "model", "selfMs": category_model}],
            "timelineAccounting": {"isReconciled": True},
        },
    }


class PercentileTests(unittest.TestCase):
    def test_linear_interpolation(self):
        self.assertEqual(percentile([100, 200], 0.5), 150.0)
        self.assertEqual(percentile([100, 200], 0.95), 195.0)
        self.assertIsNone(percentile([], 0.5))


class AnalyzeRunsTests(unittest.TestCase):
    def test_separates_protocol_errors_from_repairs_and_quality(self):
        rows = [
            row(latency=100, outcome="answer", protocol_errors=["plain_final_submission"],
                repair_count=1, repair_ms=50.0, reasoning=7, category_model=80.0),
            row(latency=200, outcome="clarify", reasoning=0, category_model=120.0),
        ]
        result = analyze_runs(rows)
        self.assertEqual(result["runs"], 2)
        self.assertEqual(result["outcomes"], {"answer": 1, "clarify": 1})
        self.assertEqual(result["plain_final_runs"], 1)
        self.assertEqual(result["protocol_repair_runs"], 1)
        self.assertEqual(result["protocol_repair_rate"], 0.5)
        self.assertEqual(result["protocol_repair_ms_overall_mean"], 25.0)
        self.assertEqual(result["protocol_repair_ms_among_repaired_mean"], 50.0)
        self.assertEqual(result["latency_p50_ms"], 150.0)
        self.assertEqual(result["latency_p95_ms"], 195.0)
        self.assertEqual(result["reasoning_zero_runs"], 1)
        self.assertEqual(result["category_self_ms_mean"], {"model": 100.0})
        self.assertEqual(result["timeline_reconciled_rate"], 1.0)

    def test_empty_input_is_rejected(self):
        with self.assertRaises(ValueError):
            analyze_runs([])


if __name__ == "__main__":
    unittest.main()
