import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("run_protocol_variant", ROOT / "run_protocol_variant.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ProtocolVariantTests(unittest.TestCase):
    def test_module_exposes_main(self):
        self.assertTrue(callable(module.main))


if __name__ == "__main__":
    unittest.main()
