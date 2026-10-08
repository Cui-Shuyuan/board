"""
重建向量索引 - 纯本地，不联网。

用法:
    python rebuild_index.py --all
    python rebuild_index.py --game civolution
    QDRANT_URL=http://host:6333 python rebuild_index.py --game civolution

契约:
    与 backend/BoardAI.Api 的 RebuildIndexAsync 共用 IndexContract：
    version = schema + game + model_dir_name + dimension + 全部索引条目哈希；
    具体 collection 写入 board_{game}__v{version}[__name] 并校验点数后，
    一次性原子切换 board_{game}__active[_name] 别名；失败保留旧别名。

依赖:
    pip install onnxruntime transformers requests
    Qdrant 在 localhost:6333 运行中
"""

import argparse
import hashlib
import json
import os
import re
import uuid
from pathlib import Path
from typing import Any

import numpy as np
import onnxruntime as ort
import requests
from transformers import AutoTokenizer

# ---- 配置 ----
BOARD_ROOT = Path(__file__).resolve().parent.parent.parent
MODEL_DIR = BOARD_ROOT / "backend" / "BoardAI.Api" / "ml_models" / "bge-base-zh-v1.5-fp32"
QDRANT_URL = os.environ.get("QDRANT_URL", "http://localhost:6333").rstrip("/")
BATCH_SIZE = 100
INDEX_SCHEMA = "board-index/v1"

# 概念引用正则——与后端 AnnotateReferences 同一模式
REF_PATTERN = re.compile(r"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>")

# 不参与向量相似度计算的概念（顶层聚合容器，各游戏通用，只作流程宿主）
EXCLUDED_CONCEPT_IDS = {"game"}


def strip_refs(text: str) -> str:
    """<> 包裹的概念引用不参与相似度计算：引用由精确查询（get_concept 注解/扩展）导航，
    其字面（含英文 id）不应污染概念自身语义的向量（2026-08-13 实验规则）。"""
    return REF_PATTERN.sub("", text)


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
    feed = {"input_ids": encoded["input_ids"], "attention_mask": encoded["attention_mask"]}
    if "token_type_ids" in tokenizer.model_input_names:
        feed["token_type_ids"] = np.zeros_like(encoded["input_ids"])
    outputs = session.run(None, feed)
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
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def build_search_text(concept: dict) -> str:
    parts = [concept.get("id", "")]
    name = concept.get("name", {})
    if isinstance(name, dict):
        parts.append(name.get("zh", ""))
        parts.append(name.get("en", ""))
    description = concept.get("description", {})
    if isinstance(description, dict):
        parts.append(description.get("zh", ""))
        parts.append(description.get("en", ""))
    return " ".join(p for p in parts if p)

def build_name_text(concept: dict) -> str:
    """只用 id + name，不加 description——用于 LLM 做精确 action/概念查找"""
    parts = [concept.get("id", "")]
    name = concept.get("name", {})
    if isinstance(name, dict):
        parts.append(name.get("zh", ""))
        parts.append(name.get("en", ""))
    return " ".join(p for p in parts if p)
    return " ".join(p for p in parts if p)


def _collect_text(node, parts):
    """递归收集 id/name/description 文本 (slots 深层效果描述)"""
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "id":
                parts.append(str(v))
            elif k == "name" and isinstance(v, dict):
                parts.extend([v.get("zh", ""), v.get("en", "")])
            elif k == "description" and isinstance(v, dict):
                parts.extend([v.get("zh", ""), v.get("en", "")])
            else:
                _collect_text(v, parts)
    elif isinstance(node, list):
        for v in node:
            _collect_text(v, parts)


def _extract_slots(node, results):
    """递归提取 slots 元素 (裸键槽名如 population/expansion 作为概念)"""
    if isinstance(node, dict):
        slots = node.get("slots")
        if isinstance(slots, list):
            for slot in slots:
                if not isinstance(slot, dict):
                    continue
                for sk, sv in slot.items():
                    # 裸键 = 槽位名 (可索引概念); <概念> 键 = 已有定义的概念引用, 跳过
                    if not sk.startswith("<") and isinstance(sv, dict):
                        parts = []
                        _collect_text(sv, parts)
                        results.append({
                            "concept_id": sk,
                            "type": "slot",
                            "name_zh": "",
                            "name_en": "",
                            "search_text": " ".join(p for p in parts if p),
                        })
        for v in node.values():
            _extract_slots(v, results)
    elif isinstance(node, list):
        for v in node:
            _extract_slots(v, results)


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
            _extract_slots(c, results)

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
            _extract_slots(c, results)

    for key, value in data.items():
        if key in array_types or key == "concepts":
            continue
        if isinstance(value, dict):
            # 顶层 dict 与 C# RulesIndexService 的 top_level_refs 契约保持一致：
            # 即使元素自带 id 也按 key 身份索引（真实数据中多为无 id 的聚合引用）。
            results.append({
                "concept_id": key,
                "type": "top_level_ref",
                "name_zh": value.get("name", {}).get("zh", ""),
                "name_en": value.get("name", {}).get("en", ""),
                "search_text": build_search_text(value),
            })
            _extract_slots(value, results)

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
        # 只索引独立概念节点：有 id 且有 specifies/extends/instance_of。
        # 局部步骤（仅 id+name，do_after 引用用）不入索引——短名+短描述是向量噪音，
        # 且同 id 跨位置重复（roll_die ×2、_skip ×10 等）会互相覆盖；步骤信息
        # 随父概念的 get_concept 完整返回（2026-08-13 判据收紧）
        if node_id and any(k in node for k in ("specifies", "extends", "instance_of")):
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

        # 递归无条件下钻——匿名容器（pipeline/options 包装）也要深入
        for evt in node.get("events", []):
            walk(evt)
        for opt in node.get("options", []):
            if isinstance(opt, dict):
                walk(opt)
        # recurse into container fields that can hold nested pipelines
        for container_key in ("<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
                              "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
                              "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
                              "<ontology::instant_content>", "<ontology::instant_cost>",
                              "<ontology::continuous_effect>", "<ontology::effect>"):
            container = node.get(container_key)
            if isinstance(container, dict):
                walk(container)

    for proc in data.get("procedures", []):
        walk(proc)

    for trigger in data.get("triggers", []):
        walk(trigger)

    return results


# ---- Qdrant REST API ----
def collection_name(game_id: str) -> str:
    return f"board_{game_id}"


def active_alias(game_id: str, name_only: bool = False) -> str:
    base = f"{collection_name(game_id)}__active"
    return f"{base}_name" if name_only else base


def versioned_collection(game_id: str, version: str, name_only: bool = False) -> str:
    base = f"{collection_name(game_id)}__v{version}"
    return f"{base}__name" if name_only else base


def qdrant_put(path: str, body: dict | None = None):
    r = requests.put(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()


def qdrant_post(path: str, body: dict | None = None):
    r = requests.post(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()
    return r


def qdrant_delete(path: str):
    r = requests.delete(f"{QDRANT_URL}{path}", timeout=30)
    r.raise_for_status()


def collection_exists(name: str) -> bool:
    r = requests.get(f"{QDRANT_URL}/collections/{name}", timeout=30)
    if r.status_code == 404:
        return False
    r.raise_for_status()
    return True


def collection_count(name: str) -> int:
    r = requests.post(
        f"{QDRANT_URL}/collections/{name}/points/count",
        json={"exact": True},
        timeout=30,
    )
    r.raise_for_status()
    return int(r.json()["result"]["count"])


def compute_index_version(game_id: str, items: list[dict]) -> str:
    """Canonical version line shared with IndexContract.ComputeIndexVersion in C#."""
    sorted_items = sorted(
        items,
        key=lambda c: (
            c.get("source", ""),
            c["concept_id"],
            c["type"],
            c.get("name_zh") or "",
            c.get("name_en") or "",
            c.get("name_text") or "",
            strip_refs(c["search_text"]),
        ),
    )
    lines = [INDEX_SCHEMA, game_id, MODEL_DIR.name, str(dimension)]
    for c in sorted_items:
        lines.append(
            "\t".join(
                [
                    c.get("source", ""),
                    c["concept_id"],
                    c["type"],
                    c.get("name_zh") or "",
                    c.get("name_en") or "",
                    c.get("name_text") or "",
                    strip_refs(c["search_text"]),
                ]
            )
        )
    payload = "\n".join(lines) + "\n"
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:16]


def rebuild_game(game_id: str):
    items = []

    def add(extracted: list[dict], source: str):
        """按来源标记每个条目——同名概念（如 <ontology::flip>）跨文件重复出现，
        uuid 需含来源前缀避免 upsert 互相覆盖（2026-08-13 修复：曾导致 515 概念只写入 416）"""
        for c in extracted:
            c["source"] = source
        items.extend(extracted)

    ontology_path = BOARD_ROOT / "content" / "ontology" / "concepts.json"
    if ontology_path.exists():
        add(extract_concepts(ontology_path), "ontology")

    ontology_flow_path = BOARD_ROOT / "content" / "ontology" / "flow.json"
    if ontology_flow_path.exists():
        add(extract_flow(ontology_flow_path), "ontology_flow")

    concepts_path = BOARD_ROOT / "content" / "games" / game_id / "concepts.json"
    if concepts_path.exists():
        add(extract_concepts(concepts_path), "game")

    instances_path = BOARD_ROOT / "content" / "games" / game_id / "instances.json"
    if instances_path.exists():
        add(extract_instances(instances_path), "instances")

    flow_path = BOARD_ROOT / "content" / "games" / game_id / "flow.json"
    if flow_path.exists():
        add(extract_flow(flow_path), "game_flow")

    # 排除通用容器概念（game 等）——只作流程宿主，不参与语义检索
    items = [c for c in items if c["concept_id"] not in EXCLUDED_CONCEPT_IDS]

    if not items:
        print(f"  No concepts found for '{game_id}'")
        return

    print(f"  {len(items)} concepts found, generating embeddings...")

    # build name-only search text for each item
    # 纯中文名：英文 id（下划线串被 tokenizer 拆碎）与英文名会稀释中文查询的
    # 语义相似度（2026-08-16 实测「乞讨卡」在混合文本上排不进向量 top-15）。
    # 中文名缺失时退回英文名；仍为空则不入 name 集合（零向量无意义）。
    for c in items:
        c["name_text"] = (c["name_zh"] or c["name_en"] or "").strip()

    # ---- versioned collections + stable query aliases ----
    version = compute_index_version(game_id, items)
    full_collection = versioned_collection(game_id, version)
    name_collection = versioned_collection(game_id, version, name_only=True)
    full_alias = active_alias(game_id)
    name_alias = active_alias(game_id, name_only=True)
    expected_full = len(items)
    expected_name = sum(1 for c in items if c["name_text"])
    created: list[str] = []

    def prepare(collection: str, expected: int) -> bool:
        if collection_exists(collection) and collection_count(collection) == expected:
            print(f"  reusing {collection} ({expected} points)")
            return False
        if collection_exists(collection):
            qdrant_delete(f"/collections/{collection}")
        qdrant_put(
            f"/collections/{collection}",
            {"vectors": {"size": dimension, "distance": "Cosine"}},
        )
        created.append(collection)
        return True

    try:
        need_full = prepare(full_collection, expected_full)
        need_name = prepare(name_collection, expected_name)

        total_batches = (len(items) + BATCH_SIZE - 1) // BATCH_SIZE
        for i in range(0, len(items), BATCH_SIZE):
            batch = items[i:i + BATCH_SIZE]
            # 嵌入前剥离 <> 引用——概念引用不参与相似度计算
            full_points = []
            if need_full:
                full_vectors = [embed(strip_refs(c["search_text"])).tolist() for c in batch]
                for j, c in enumerate(batch):
                    full_points.append({
                        "id": make_uuid(
                            f"{game_id}::{c.get('source', '')}::{c['concept_id']}"
                        ),
                        "vector": full_vectors[j],
                        "payload": {
                            "concept_id": c["concept_id"],
                            "type": c["type"],
                            "name_zh": c["name_zh"],
                            "name_en": c["name_en"],
                        },
                    })

            name_points = []
            if need_name:
                name_batch = [c for c in batch if c["name_text"]]
                name_vectors = [embed(c["name_text"]).tolist() for c in name_batch]
                for c, vec in zip(name_batch, name_vectors):
                    name_points.append({
                        "id": make_uuid(
                            f"{game_id}::{c.get('source', '')}::{c['concept_id']}_name"
                        ),
                        "vector": vec,
                        "payload": {
                            "concept_id": c["concept_id"],
                            "type": c["type"],
                            "name_zh": c["name_zh"],
                            "name_en": c["name_en"],
                        },
                    })

            if full_points:
                qdrant_put(f"/collections/{full_collection}/points?wait=true", {"points": full_points})
            if name_points:
                qdrant_put(f"/collections/{name_collection}/points?wait=true", {"points": name_points})
            batch_num = i // BATCH_SIZE + 1
            print(f"  [{batch_num}/{total_batches}] {len(batch)} concepts")

        # 切换前严格校验：不完整的新索引绝不允许顶替旧索引。
        actual_full = collection_count(full_collection)
        actual_name = collection_count(name_collection)
        if actual_full != expected_full or actual_name != expected_name:
            raise RuntimeError(
                f"index verification failed: full={actual_full}/{expected_full}, "
                f"name={actual_name}/{expected_name}"
            )

        # Qdrant 在一个 update_aliases 请求内原子执行以下操作。
        actions = []
        for alias, collection in ((full_alias, full_collection), (name_alias, name_collection)):
            actions.append({"delete_alias": {"alias_name": alias}})
            actions.append({"create_alias": {"collection_name": collection, "alias_name": alias}})
        qdrant_post("/collections/aliases", {"actions": actions})

        print(
            f"  OK {game_id}: version={version} full='{full_collection}' "
            f"({actual_full}) + name='{name_collection}' ({actual_name}); "
            f"aliases '{full_alias}' / '{name_alias}' switched atomically"
        )
    except Exception:
        # 切换前失败：删除本次新建的临时 collection，旧别名继续服务。
        for collection in created:
            try:
                if collection_exists(collection):
                    qdrant_delete(f"/collections/{collection}")
            except Exception as cleanup_error:
                print(f"  warning: cleanup of '{collection}' failed: {cleanup_error}")
        raise


# ---- CLI ----
def main():
    parser = argparse.ArgumentParser(description="Rebuild vector index (offline)")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true")
    group.add_argument("--game", type=str)
    args = parser.parse_args()

    if args.all:
        games_dir = BOARD_ROOT / "content" / "games"
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
