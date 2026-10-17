import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def load_module(name: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

fetch = load_module("fetch_rule_details")
judge = load_module("judge_answers")


class StructuredEvidenceTests(unittest.TestCase):
    def test_nested_cost_is_preserved_for_judge_material(self):
        detail = {
            "id": "play_bird",
            "name": {"zh": "打出一张鸟牌"},
            "description": {"zh": "选择一张鸟牌并支付费用。"},
            "<ontology::cost>(费用)": {
                "<ontology::instant_cost>(即时费用)": {
                    "options": [{
                        "id": "pay_egg_cost",
                        "<ontology::transfer>(转移)": {
                            "source": "该玩家鸟牌上的任意 <egg>(鸟蛋)",
                            "destination": "<ontology::supply>(供应堆)",
                        },
                    }]
                }
            },
        }
        structured = fetch.extract_structured_evidence(detail)
        self.assertIn("该玩家鸟牌上的任意", structured)
        self.assertIn("pay_egg_cost", structured)
        self.assertNotIn("选择一张鸟牌并支付费用", structured)

    def test_judge_prompt_includes_structured_details(self):
        run = {
            "game": "wingspan",
            "question": "鸟牌怎么打出来？",
            "question_type": "positive",
            "expected_behavior": "answer_from_rule",
            "expected_rule_ids": ["bird_card"],
            "reply": "蛋从自己鸟牌上拿。",
            "deterministic": {"matched_ids": ["play_bird"]},
        }
        truth = {"acceptable_rule_evidence": []}
        details = {
            "wingspan::play_bird": {
                "name": "打出一张鸟牌",
                "description": "支付蛋费和食物费。",
                "structured_evidence": '{"source":"该玩家鸟牌上的任意 <egg>"}',
            }
        }
        prompt = judge.build_prompt(run, truth, details)
        self.assertIn("结构化正式证据", prompt)
        self.assertIn("该玩家鸟牌上的任意", prompt)


if __name__ == "__main__":
    unittest.main()
