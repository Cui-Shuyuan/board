import copy
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import retrieval_lab as lab


class RetrievalLabTests(unittest.TestCase):
    def test_actual_labels_have_valid_evidence_and_topic_split(self):
        rows = [json.loads(line) for line in (lab.ROOT / "tools/qa/retrieval_questions.jsonl").read_text(encoding="utf-8-sig").splitlines()]
        lab.validate_labels(rows)
        self.assertEqual(9, len({r["game"] for r in rows}))
        self.assertEqual(3, sum(r["kind"] == "unsupported" for r in rows))
        self.assertEqual(2, sum(r["kind"] == "ambiguous" for r in rows))

    def test_question_leakage_is_rejected(self):
        row = dict(id="x", game="test", topic="same", split="dev", required=[], relevance=[])
        other = {**row, "id": "y", "split": "holdout"}
        with self.assertRaisesRegex(ValueError, "leak"):
            lab.validate_labels([row, other])

    def test_multi_object_question_requires_both_objects_for_coverage(self):
        row = dict(required=["white", "pink"], relevance=[dict(id="white", grade=3), dict(id="pink", grade=3)])
        result = [dict(path=p) for p in ["white", "generic", "irrelevant", "pink"]]
        scores = lab.metrics(row, result)
        self.assertTrue(scores["hit1"])
        self.assertFalse(scores["coverage3"])
        self.assertTrue(scores["coverage10"])
        self.assertLess(scores["ndcg10"], 1)

    def test_empty_or_unknown_results_are_not_hits(self):
        row = dict(required=["target"], relevance=[dict(id="target", grade=3)])
        scores = lab.metrics(row, [dict(path="irrelevant")])
        self.assertEqual(0, scores["mrr"])
        self.assertEqual(0, scores["ndcg10"])
        self.assertFalse(scores["hit3"])

    def test_projections_keep_real_slot_identity_and_resolvable_fact_pointers(self):
        whole, facts, names = lab.project("civolution")
        self.assertEqual(434, len(whole))
        slot = [d for d in facts if d["path"] == "final_scoring_area_hex.point_bonus"]
        self.assertTrue(slot)
        for document in slot:
            node = json.loads((lab.ROOT / document["file"]).read_text(encoding="utf-8-sig"))
            for part in document["pointer"].strip("/").split("/"):
                key = part.replace("~1", "/").replace("~0", "~")
                node = node[int(key)] if isinstance(node, list) else node[key]
            self.assertIsInstance(node, str)
            self.assertTrue(node)
        self.assertEqual(412, len(names))
        self.assertTrue({d["path"] for d in names} <= {d["path"] for d in whole})


if __name__ == "__main__":
    unittest.main()
