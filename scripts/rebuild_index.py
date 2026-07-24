"""
重建向量索引 - 纯本地，不联网。

用法:
    python rebuild_index.py --all
    python rebuild_index.py --game civolution

依赖:
    pip install onnxruntime transformers requests
    Qdrant 在 localhost:6333 运行中
"""

import argparse
import hashlib
import json
import os
import uuid
from pathlib import Path
from typing import Any

import numpy as np
import onnxruntime as ort
import requests
from transformers import AutoTokenizer

# ---- 配置 ----
BOARD_ROOT = Path(__file__).resolve().parent.parent
MODEL_DIR = BOARD_ROOT / "backend" / "BoardAI.Api" / "ml_models" / "bge-small-zh"
QDRANT_URL = "http://localhost:6333"
BATCH_SIZE = 100


# ---- UUID ----
def make_uuid(s: str) -> str:
    h = hashlib.sha256(s.encode()).digest()[:16]
    b = bytearray(h)
    b[7] = (b[7] & 0x0F) | 0x50
    b[8] = (b[8] & 0x3F) | 0x80
    return str(uuid.UUID(bytes=bytes(b)))


# ---- 加载本地 ONNX 模型 ----
print(f"Loading ONNX model from {MODEL_DIR}...")
tokenizer = AutoTokenizer.from_pretrained(str(MODEL_DIR))
session = ort.InferenceSession(str(MODEL_DIR / "model.onnx"))
dimension = session.get_outputs()[0].shape[2]  # 512
print(f"  Model loaded, dimension={dimension}")


def embed(text: str) -> np.ndarray:
    """文本 → L2 归一化向量（mean pooling，跟 C# 端完全一致）"""
    if not text or not text.strip():
        return np.zeros(dimension, dtype=np.float32)

    encoded = tokenizer(text, padding=True, truncation=True,
                        max_length=512, return_tensors="np")
    outputs = session.run(None, {
        "input_ids": encoded["input_ids"],
        "attention_mask": encoded["attention_mask"],
    })
    hidden = outputs[0]  # [1, seq_len, dim]
    mask = encoded["attention_mask"][0]  # [seq_len]

    # Mean pooling over real tokens
    emb = (hidden[0] * mask[:, None]).sum(axis=0) / mask.sum()
    # L2 normalize
    norm = np.linalg.norm(emb)
    if norm > 1e-8:
        emb = emb / norm
    return emb.astype(np.float32)


# ---- 从 JSON 提取概念 ----
def load_json(path: Path) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def build_search_text(concept: dict) -> str:
    parts = [concept.get("id", "")]
    name = concept.get("name", {})
    if isinstance(name, dict):
        parts.append(name.get("zh", ""))
        parts.append(name.get("en", ""))
    definition = concept.get("definition", {})
    if isinstance(definition, dict):
        parts.append(definition.get("zh", ""))
        parts.append(definition.get("en", ""))
    return " ".join(p for p in parts if p)


def extract_concepts(file_path: Path) -> list[dict[str, Any]]:
    data = load_json(file_path)
    results = []

    if "concepts" in data:
        for c in data["concepts"]:
            results.append({
                "concept_id": c.get("id", ""),
                "type": "ontology",
                "name_zh": c.get("name", {}).get("zh", ""),
                "name_en": c.get("name", {}).get("en", ""),
                "search_text": build_search_text(c),
            })

    array_types = ["objects", "actions", "triggers", "conditions"]
    for arr_type in array_types:
        for c in data.get(arr_type, []):
            results.append({
                "concept_id": c.get("id", ""),
                "type": arr_type,
                "name_zh": c.get("name", {}).get("zh", ""),
                "name_en": c.get("name", {}).get("en", ""),
                "search_text": build_search_text(c),
            })

    for key, value in data.items():
        if key in array_types or key == "concepts":
            continue
        if isinstance(value, dict) and "id" not in value:
            results.append({
                "concept_id": key,
                "type": "top_level_ref",
                "name_zh": value.get("name", {}).get("zh", ""),
                "name_en": value.get("name", {}).get("en", ""),
                "search_text": build_search_text(value),
            })

    return results


def extract_instances(file_path: Path) -> list[dict[str, Any]]:
    """从 instances.json 提取所有实例"""
    data = load_json(file_path)
    results = []

    instance_array_types = ["effects", "modules", "cards", "continent_tiles", "sites", "chips"]
    for arr_type in instance_array_types:
        for c in data.get(arr_type, []):
            results.append({
                "concept_id": c.get("id", ""),
                "type": arr_type,
                "name_zh": c.get("name", {}).get("zh", ""),
                "name_en": c.get("name", {}).get("en", ""),
                "search_text": build_search_text(c),
            })

    return results


def extract_flow(file_path: Path) -> list[dict[str, Any]]:
    """从 flow.json 递归提取所有流程节点"""
    data = load_json(file_path)
    results = []

    def walk(node: dict):
        node_id = node.get("id", "")
        if not node_id:
            return

        parts = [node_id]
        node_type = node.get("type", "")
        if node_type:
            parts.append(node_type)
        name = node.get("name", {})
        if isinstance(name, dict):
            parts.append(name.get("zh", ""))
        desc = node.get("description", {})
        if isinstance(desc, dict):
            parts.append(desc.get("zh", ""))

        results.append({
            "concept_id": node_id,
            "type": "flow",
            "name_zh": name.get("zh", "") if isinstance(name, dict) else node_id,
            "name_en": name.get("en", "") if isinstance(name, dict) else "",
            "search_text": " ".join(p for p in parts if p),
        })

        for child in node.get("children", []):
            walk(child)
        for evt in node.get("events", []):
            walk(evt)

    for proc in data.get("procedures", []):
        walk(proc)

    return results


# ---- Qdrant REST API ----
def collection_name(game_id: str) -> str:
    return f"board_{game_id}"


def qdrant_put(path: str, body: dict | None = None):
    r = requests.put(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()


def qdrant_delete(path: str):
    r = requests.delete(f"{QDRANT_URL}{path}", timeout=30)
    r.raise_for_status()


def rebuild_game(game_id: str):
    items = []

    ontology_path = BOARD_ROOT / "ontology" / "ontology.json"
    if ontology_path.exists():
        items.extend(extract_concepts(ontology_path))

    concepts_path = BOARD_ROOT / "games" / game_id / "concepts.json"
    if concepts_path.exists():
        items.extend(extract_concepts(concepts_path))

    instances_path = BOARD_ROOT / "games" / game_id / "instances.json"
    if instances_path.exists():
        items.extend(extract_instances(instances_path))

    flow_path = BOARD_ROOT / "games" / game_id / "flow.json"
    if flow_path.exists():
        items.extend(extract_flow(flow_path))

    if not items:
        print(f"  No concepts found for '{game_id}'")
        return

    print(f"  {len(items)} concepts found, generating embeddings...")

    name = collection_name(game_id)
    try:
        qdrant_delete(f"/collections/{name}")
    except requests.HTTPError:
        pass

    qdrant_put(f"/collections/{name}", {
        "vectors": {"size": dimension, "distance": "Cosine"}
    })

    for i in range(0, len(items), BATCH_SIZE):
        batch = items[i:i + BATCH_SIZE]
        texts = [c["search_text"] for c in batch]
        vectors = [embed(t) for t in texts]

        points = []
        for j, c in enumerate(batch):
            points.append({
                "id": make_uuid(f"{game_id}::{c['concept_id']}"),
                "vector": vectors[j].tolist(),
                "payload": {
                    "concept_id": c["concept_id"],
                    "type": c["type"],
                    "name_zh": c["name_zh"],
                    "name_en": c["name_en"],
                },
            })

        qdrant_put(f"/collections/{name}/points", {"points": points})
        batch_num = i // BATCH_SIZE + 1
        total_batches = (len(items) + BATCH_SIZE - 1) // BATCH_SIZE
        print(f"  [{batch_num}/{total_batches}] {len(batch)} concepts")

    print(f"  OK {game_id}: {len(items)} concepts in '{name}'")


# ---- CLI ----
def main():
    parser = argparse.ArgumentParser(description="Rebuild vector index (offline)")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true")
    group.add_argument("--game", type=str)
    args = parser.parse_args()

    if args.all:
        games_dir = BOARD_ROOT / "games"
        games = sorted(d.name for d in games_dir.iterdir() if d.is_dir())
        print(f"Rebuilding {len(games)} game(s): {games}")
    else:
        games = [args.game]
        print(f"Rebuilding: {games[0]}")

    for game in games:
        rebuild_game(game)

    print("All done.")


if __name__ == "__main__":
    main()
