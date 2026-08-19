#!/usr/bin/env python3
"""检查 Qdrant 中各游戏 collection 点数是否与规则文件应索引的唯一概念数一致。

用法:
    PYTHONUSERBASE=.tools/pyuser python3 scripts/check_index_counts.py

它会用 rebuild_index.py 的提取逻辑算出每个游戏应有的 full/name 点数，
再和 Qdrant 实际 points_count 对比。主要用于发现 C#/Python 索引逻辑漂移。
"""
import sys
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rebuild_index as ri  # noqa: E402

QDRANT_URL = ri.QDRANT_URL
BOARD_ROOT = ri.BOARD_ROOT
GAMES = sorted(d.name for d in BOARD_ROOT.joinpath("games").iterdir() if d.is_dir())


def expected_items(game_id: str):
    items = []

    def add(lst, source):
        for c in lst:
            c["source"] = source
            items.append(c)

    ont = BOARD_ROOT / "ontology" / "concepts.json"
    if ont.exists():
        add(ri.extract_concepts(ont), "ontology")

    ontf = BOARD_ROOT / "ontology" / "flow.json"
    if ontf.exists():
        add(ri.extract_flow(ontf), "ontology_flow")

    cp = BOARD_ROOT / "games" / game_id / "concepts.json"
    if cp.exists():
        add(ri.extract_concepts(cp), "game")

    ip = BOARD_ROOT / "games" / game_id / "instances.json"
    if ip.exists():
        add(ri.extract_instances(ip), "instances")

    fp = BOARD_ROOT / "games" / game_id / "flow.json"
    if fp.exists():
        add(ri.extract_flow(fp), "game_flow")

    items = [c for c in items if c["concept_id"] not in ri.EXCLUDED_CONCEPT_IDS]

    # Qdrant 中 full/name 的 point id 都基于 (source, concept_id)，同源同 id 只会保留一个。
    full = {(c["source"], c["concept_id"]) for c in items}
    name = {
        (c["source"], c["concept_id"])
        for c in items
        if (c.get("name_zh") or c.get("name_en") or "").strip()
    }
    return full, name


def points_count(collection: str) -> int:
    r = requests.get(f"{QDRANT_URL}/collections/{collection}", timeout=10)
    r.raise_for_status()
    return r.json()["result"].get("points_count", 0)


def main():
    failed = False
    for game in GAMES:
        full, name = expected_items(game)
        full_col = ri.collection_name(game)
        name_col = f"{full_col}_name"
        full_actual = points_count(full_col)
        name_actual = points_count(name_col)
        ok_full = full_actual == len(full)
        ok_name = name_actual == len(name)
        status = "OK" if ok_full and ok_name else "MISMATCH"
        print(f"{status:8s} {game:20s} full={full_actual}/{len(full)} name={name_actual}/{len(name)}")
        if not ok_full or not ok_name:
            failed = True
            if not ok_full:
                print(f"  expected full points: {len(full)}, actual: {full_actual}")
            if not ok_name:
                print(f"  expected name points: {len(name)}, actual: {name_actual}")

    if failed:
        print("\n有 collection 数量不一致，请检查索引重建逻辑或重新 rebuild_index。")
        sys.exit(1)

    print("\n所有 collection 点数与规则文件一致。")


if __name__ == "__main__":
    main()
