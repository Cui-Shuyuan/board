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


if __name__ == "__main__":
    unittest.main()
