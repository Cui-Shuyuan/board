"""
重建向量索引 - 纯本地，不联网。

用法:
    python rebuild_index.py --all              # 增量同步所有游戏（默认）
    python rebuild_index.py --game civolution  # 增量同步指定游戏
    python rebuild_index.py --all --full       # 强制全量重建（删除并重建 collection）
    python rebuild_index.py --game civolution --full

依赖:
    pip install onnxruntime transformers requests
    Qdrant 在 localhost:6333 运行中

增量策略:
    - point ID 由 game/source/concept_id 确定性生成，upsert 天然覆盖；
    - payload 中存 content_hash + model_tag，diff 后只对新增/变化点重新 embedding；
    - collection 不存在、维度不匹配、或 --full 时回退为全量重建；
    - 删除点也会同步从 collection 移除（避免搜索返回已不存在的概念）。
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
BOARD_ROOT = Path(__file__).resolve().parent.parent
MODEL_DIR = BOARD_ROOT / "backend" / "BoardAI.Api" / "ml_models" / "bge-base-zh-v1.5-fp32"
MODEL_TAG = MODEL_DIR.name
QDRANT_URL = os.environ.get("QDRANT_URL", "http://localhost:6333")
BATCH_SIZE = 100

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
        if isinstance(value, dict) and "id" not in value:
            # 顶层引用对象本身没有 id 字段，但 key 就是它的概念 id。
            # 这里把 key 注入搜索文本，与 C# 端 BuildSearchText(summary.Id, detail) 对齐。
            search_concept = dict(value)
            search_concept["id"] = key
            results.append({
                "concept_id": key,
                "type": "top_level_ref",
                "name_zh": value.get("name", {}).get("zh", ""),
                "name_en": value.get("name", {}).get("en", ""),
                "search_text": build_search_text(search_concept),
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


def qdrant_put(path: str, body: dict | None = None):
    r = requests.put(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()


def qdrant_post(path: str, body: dict | None = None):
    r = requests.post(f"{QDRANT_URL}{path}", json=body, timeout=60)
    r.raise_for_status()


def qdrant_delete(path: str):
    r = requests.delete(f"{QDRANT_URL}{path}", timeout=30)
    r.raise_for_status()


def get_collection_info(collection: str) -> dict | None:
    """返回 collection 信息；不存在时返回 None。"""
    r = requests.get(f"{QDRANT_URL}/collections/{collection}", timeout=30)
    if r.status_code == 404:
        return None
    r.raise_for_status()
    return r.json().get("result")


def collection_dimension_ok(collection: str) -> bool:
    info = get_collection_info(collection)
    if not info:
        return False
    try:
        return info["config"]["params"]["vectors"]["size"] == dimension
    except (KeyError, TypeError):
        return False


def recreate_collection(collection: str):
    try:
        qdrant_delete(f"/collections/{collection}")
    except requests.HTTPError:
        pass
    qdrant_put(f"/collections/{collection}", {
        "vectors": {"size": dimension, "distance": "Cosine"}
    })


def scroll_points(collection: str) -> dict[str, dict]:
    """滚动读取 collection 中全部 point：id -> payload。"""
    existing: dict[str, dict] = {}
    offset: Any = None
    while True:
        body: dict[str, Any] = {"limit": 200, "with_payload": True, "with_vector": False}
        if offset is not None:
            body["offset"] = offset
        r = requests.post(f"{QDRANT_URL}/collections/{collection}/points/scroll",
                          json=body, timeout=60)
        r.raise_for_status()
        data = r.json().get("result", {})
        for p in data.get("points", []):
            pid = p.get("id")
            if pid is None:
                continue
            existing[str(pid)] = p.get("payload") or {}
        offset = data.get("next_page_offset")
        if not offset:
            break
    return existing


def upsert_points(collection: str, descriptors: list[dict[str, Any]]):
    if not descriptors:
        return
    for i in range(0, len(descriptors), BATCH_SIZE):
        batch = descriptors[i:i + BATCH_SIZE]
        points = [{
            "id": d["id"],
            "vector": d["vector"],
            "payload": d["payload"],
        } for d in batch]
        qdrant_put(f"/collections/{collection}/points", {"points": points})


def delete_points(collection: str, ids: list[str]):
    if not ids:
        return
    for i in range(0, len(ids), BATCH_SIZE):
        batch = ids[i:i + BATCH_SIZE]
        qdrant_post(f"/collections/{collection}/points/delete?wait=true", {"points": batch})


# ---- 索引项准备与 diff ----
def collect_index_items(game_id: str) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = []

    def add(extracted: list[dict], source: str):
        """按来源标记每个条目——同名概念（如 <ontology::flip>）跨文件重复出现，
        uuid 需含来源前缀避免 upsert 互相覆盖（2026-08-13 修复：曾导致 515 概念只写入 416）"""
        for c in extracted:
            c["source"] = source
        items.extend(extracted)

    ontology_path = BOARD_ROOT / "ontology" / "concepts.json"
    if ontology_path.exists():
        add(extract_concepts(ontology_path), "ontology")

    ontology_flow_path = BOARD_ROOT / "ontology" / "flow.json"
    if ontology_flow_path.exists():
        add(extract_flow(ontology_flow_path), "ontology_flow")

    concepts_path = BOARD_ROOT / "games" / game_id / "concepts.json"
    if concepts_path.exists():
        add(extract_concepts(concepts_path), "game")

    instances_path = BOARD_ROOT / "games" / game_id / "instances.json"
    if instances_path.exists():
        add(extract_instances(instances_path), "instances")

    flow_path = BOARD_ROOT / "games" / game_id / "flow.json"
    if flow_path.exists():
        add(extract_flow(flow_path), "game_flow")

    # 排除通用容器概念（game 等）——只作流程宿主，不参与语义检索
    items = [c for c in items if c["concept_id"] not in EXCLUDED_CONCEPT_IDS]

    # name-only 集合的文本：中文名优先，缺失时退回英文名；仍为空则不入 name 集合
    for c in items:
        c["name_text"] = (c["name_zh"] or c["name_en"] or "").strip()

    return items


def payload_hash(c: dict[str, Any], text: str) -> str:
    """payload 级内容指纹。覆盖 payload 字段 + 实际参与向量的文本，
    任一项变化都会触发重新 embedding + upsert。

    注意：分隔符拼接格式要与 C# 端 ContentHash 保持完全一致，
    保证 Python 与 C# 两条重建路径产出的 content_hash 可互相复用。"""
    blob = "\n".join([
        c.get("concept_id", ""),
        c.get("type", ""),
        c.get("name_zh", ""),
        c.get("name_en", ""),
        text,
    ])
    return hashlib.sha256(blob.encode()).hexdigest()


def build_full_descriptors(game_id: str, items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """full collection 的 point 描述符。vector 随后按需填充。"""
    descriptors: list[dict[str, Any]] = []
    for c in items:
        text = strip_refs(c["search_text"])
        descriptors.append({
            "id": make_uuid(f"{game_id}::{c['source']}::{c['concept_id']}"),
            "text": text,
            "payload": {
                "concept_id": c["concept_id"],
                "type": c["type"],
                "name_zh": c["name_zh"],
                "name_en": c["name_en"],
                "content_hash": payload_hash(c, text),
                "model_tag": MODEL_TAG,
            },
        })
    return dedupe_descriptors(descriptors)


def build_name_descriptors(game_id: str, items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """name-only collection 的 point 描述符。中文名为空的项目跳过。"""
    descriptors: list[dict[str, Any]] = []
    for c in items:
        text = c["name_text"]
        if not text:
            continue
        descriptors.append({
            "id": make_uuid(f"{game_id}::{c['source']}::{c['concept_id']}_name"),
            "text": text,
            "payload": {
                "concept_id": c["concept_id"],
                "type": c["type"],
                "name_zh": c["name_zh"],
                "name_en": c["name_en"],
                "content_hash": payload_hash(c, text),
                "model_tag": MODEL_TAG,
            },
        })
    return dedupe_descriptors(descriptors)


def dedupe_descriptors(descriptors: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[str] = set()
    result: list[dict[str, Any]] = []
    for d in descriptors:
        if d["id"] in seen:
            print(f"  WARN: duplicate point id {d['id']} -> keeping first occurrence")
            continue
        seen.add(d["id"])
        result.append(d)
    return result


def embed_descriptors(descriptors: list[dict[str, Any]]):
    for d in descriptors:
        d["vector"] = embed(d["text"]).tolist()


def diff_descriptors(existing: dict[str, dict],
                     descriptors: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], list[str]]:
    """对比旧 points 与新描述符。

    - 新 ID 或 content_hash/model_tag 变化 → upsert（重新 embedding）
    - 旧 ID 不在新集合 → delete
    - 其余 → 跳过
    """
    new_by_id = {d["id"]: d for d in descriptors}
    to_upsert: list[dict[str, Any]] = []
    for d in descriptors:
        old_payload = existing.get(d["id"])
        if (old_payload is None
                or old_payload.get("content_hash") != d["payload"]["content_hash"]
                or old_payload.get("model_tag") != MODEL_TAG):
            to_upsert.append(d)

    to_delete = [pid for pid in existing if pid not in new_by_id]
    return to_upsert, to_delete


# ---- 重建模式 ----
def full_rebuild(game_id: str, items: list[dict[str, Any]]):
    full_col = collection_name(game_id)
    name_col = full_col + "_name"

    print(f"  Full rebuild for '{game_id}' ({len(items)} concepts)...")
    recreate_collection(full_col)
    recreate_collection(name_col)

    full_descriptors = build_full_descriptors(game_id, items)
    name_descriptors = build_name_descriptors(game_id, items)

    print(f"  Embedding {len(full_descriptors)} full vectors...")
    embed_descriptors(full_descriptors)
    print(f"  Embedding {len(name_descriptors)} name vectors...")
    embed_descriptors(name_descriptors)

    upsert_points(full_col, full_descriptors)
    upsert_points(name_col, name_descriptors)

    print(f"  OK {game_id}: {len(full_descriptors)} full + {len(name_descriptors)} name "
          f"in '{full_col}' + '{name_col}'")


def incremental_rebuild(game_id: str, items: list[dict[str, Any]]):
    full_col = collection_name(game_id)
    name_col = full_col + "_name"

    # 目标 collection 不存在或维度不匹配时，增量无法安全进行，回退全量。
    if not collection_dimension_ok(full_col) or not collection_dimension_ok(name_col):
        print(f"  Collection missing or dimension mismatch for '{game_id}', falling back to full rebuild.")
        full_rebuild(game_id, items)
        return

    print(f"  Incremental sync for '{game_id}' ({len(items)} concepts)...")

    existing_full = scroll_points(full_col)
    existing_name = scroll_points(name_col)

    full_descriptors = build_full_descriptors(game_id, items)
    name_descriptors = build_name_descriptors(game_id, items)

    full_upsert, full_delete = diff_descriptors(existing_full, full_descriptors)
    name_upsert, name_delete = diff_descriptors(existing_name, name_descriptors)

    if full_upsert:
        print(f"  Embedding {len(full_upsert)} full vectors (new/changed)...")
        embed_descriptors(full_upsert)
        upsert_points(full_col, full_upsert)

    if name_upsert:
        print(f"  Embedding {len(name_upsert)} name vectors (new/changed)...")
        embed_descriptors(name_upsert)
        upsert_points(name_col, name_upsert)

    if full_delete:
        print(f"  Deleting {len(full_delete)} stale full points...")
        delete_points(full_col, full_delete)

    if name_delete:
        print(f"  Deleting {len(name_delete)} stale name points...")
        delete_points(name_col, name_delete)

    print(f"  OK {game_id}: full upserted {len(full_upsert)} / deleted {len(full_delete)} / "
          f"unchanged {len(full_descriptors) - len(full_upsert)}; "
          f"name upserted {len(name_upsert)} / deleted {len(name_delete)} / "
          f"unchanged {len(name_descriptors) - len(name_upsert)}")


def rebuild_game(game_id: str, full: bool = False):
    items = collect_index_items(game_id)
    if not items:
        print(f"  No concepts found for '{game_id}', skip.")
        return

    if full:
        full_rebuild(game_id, items)
    else:
        incremental_rebuild(game_id, items)


# ---- CLI ----
def main():
    parser = argparse.ArgumentParser(description="Rebuild vector index (offline)")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true", help="同步所有游戏（默认增量）")
    group.add_argument("--game", type=str, help="只同步指定游戏（默认增量）")
    parser.add_argument("--full", action="store_true",
                        help="强制全量重建（删除并重建 collection；模型/提取逻辑大改时用）")
    args = parser.parse_args()

    if args.all:
        games_dir = BOARD_ROOT / "games"
        games = sorted(d.name for d in games_dir.iterdir() if d.is_dir())
        print(f"Syncing {len(games)} game(s): {games}  (mode={'full' if args.full else 'incremental'})")
    else:
        games = [args.game]
        print(f"Syncing: {games[0]}  (mode={'full' if args.full else 'incremental'})")

    for game in games:
        rebuild_game(game, full=args.full)

    print("All done.")


if __name__ == "__main__":
    main()
