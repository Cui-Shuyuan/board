"""REV-03a: Python/C# index extraction and identity contract regression.

This suite intentionally imports tools/indexing/rebuild_index.py without loading
the ONNX model; the extraction/version/UUID functions are pure and must stay in
sync with backend/BoardAI.Api.Services.IndexContract.
"""

from __future__ import annotations

import importlib.util
import sys
import unittest
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def _load_rebuild_module():
    path = ROOT / "tools" / "indexing" / "rebuild_index.py"
    spec = importlib.util.spec_from_file_location("board_rebuild_index", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


ri = _load_rebuild_module()


def _extract_game(game: str) -> list[dict]:
    items: list[dict] = []

    def add(source: str, extracted: list[dict]) -> None:
        for item in extracted:
            item["source"] = source
            item.setdefault("path", item.get("concept_id", ""))
            item.setdefault("owner_path", "")
        items.extend(extracted)

    ontology = ROOT / "content" / "ontology" / "concepts.json"
    if ontology.exists():
        add("ontology", ri.extract_concepts(ontology))
    ontology_flow = ROOT / "content" / "ontology" / "flow.json"
    if ontology_flow.exists():
        add("ontology_flow", ri.extract_flow(ontology_flow))

    game_dir = ROOT / "content" / "games" / game
    concepts = game_dir / "concepts.json"
    if concepts.exists():
        add("game", ri.extract_concepts(concepts))
    instances = game_dir / "instances.json"
    if instances.exists():
        add("instances", ri.extract_instances(instances))
    flow = game_dir / "flow.json"
    if flow.exists():
        add("game_flow", ri.extract_flow(flow))

    return [item for item in items if item["concept_id"] != "game"]


class IndexExtractionContractTests(unittest.TestCase):
    def test_uuid_contract_matches_csharp(self):
        self.assertEqual(
            "075604e4-754d-925c-ab1a-67e18dce79fa",
            ri.make_uuid("testgame::game::widget"),
        )
        self.assertEqual(
            "d10710a1-1b07-e054-9317-a678d9971639",
            ri.make_uuid("testgame::game::widget_name"),
        )

    def test_version_line_matches_csharp(self):
        ri.dimension = 768
        item = {
            "source": "game",
            "concept_id": "widget",
            "path": "widget",
            "owner_path": "",
            "type": "objects",
            "name_zh": "小装置",
            "name_en": "",
            "name_text": "小装置",
            "search_text": "widget 小装置 使用资源",
        }
        self.assertEqual("a84d97eb85b6cfc8", ri.compute_index_version("testgame", [item]))

    def test_all_games_have_unique_source_and_identity_paths(self):
        games = sorted(p.name for p in (ROOT / "content" / "games").iterdir() if p.is_dir())
        self.assertEqual(
            [
                "agricola",
                "ark-nova",
                "brass-birmingham",
                "castles-of-burgundy",
                "civolution",
                "puerto-rico",
                "seasons",
                "splendor",
                "wingspan",
            ],
            games,
        )

        for game in games:
            with self.subTest(game=game):
                items = _extract_game(game)
                identities = [(i["source"], i.get("path") or i["concept_id"]) for i in items]
                duplicates = [key for key, count in Counter(identities).items() if count > 1]
                self.assertEqual([], duplicates)

    def test_real_content_version_matches_csharp_api_contract(self):
        ri.dimension = 768
        expected = {
            "agricola": "3796433e0d63a235",
            "ark-nova": "a25f3dc1e20c5590",
            "brass-birmingham": "6aec6eaf6ac0cbaa",
            "castles-of-burgundy": "f763f42b734c7a26",
            "civolution": "75a5bd36f360c382",
            "puerto-rico": "21f842ace2b37e6b",
            "seasons": "2df6ff09b492b711",
            "splendor": "03283886a77eb0de",
            "wingspan": "527374f5bc466f7d",
        }
        for game, version in expected.items():
            with self.subTest(game=game):
                items = _extract_game(game)
                for item in items:
                    item["name_text"] = (item.get("name_zh") or item.get("name_en") or "").strip()
                self.assertEqual(version, ri.compute_index_version(game, items))

    def test_civolution_slot_extraction_does_not_collapse_local_slot_with_global_object(self):
        items = _extract_game("civolution")

        # 修复前：438 条记录只有 431 个唯一点，stage_partition 全局对象被局部槽位覆盖。
        self.assertEqual(434, len(items))
        self.assertEqual(412, sum(1 for item in items if (item.get("name_zh") or item.get("name_en"))))

        global_stage_partition = [
            item for item in items
            if item["type"] == "objects" and item["concept_id"] == "stage_partition"
        ]
        self.assertEqual(1, len(global_stage_partition))
        self.assertEqual("stage_partition", global_stage_partition[0]["path"])

        local_stage_partition = [
            item for item in items
            if item["type"] == "slot" and item["path"] == "final_scoring_area_hex.stage_partition"
        ]
        self.assertEqual(1, len(local_stage_partition))
        self.assertEqual("final_scoring_area_hex", local_stage_partition[0]["owner_path"])

        # 显式槽位的 id/name/material 是元数据，不能成为三个虚假槽位键。
        self.assertFalse(
            any(item["type"] == "slot" and item["concept_id"] in {"id", "name", "material"}
                for item in items)
        )
        explicit = [
            item for item in items
            if item["type"] == "slot" and item["concept_id"].startswith("settlement_slot_")
        ]
        self.assertEqual(4, len(explicit))
        self.assertTrue(all(item["path"].startswith("settlement_zone.") for item in explicit))
        self.assertTrue(all(item["name_zh"].startswith("聚落格") for item in explicit))


if __name__ == "__main__":
    unittest.main()
