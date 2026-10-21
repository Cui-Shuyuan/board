"""
重建向量索引 - 本地基础设施入口。

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
    纯抽取/身份/版本逻辑见 index_extraction.py，不需要第三方依赖。
    本 CLI 的 Qdrant 写入路径需要:
        pip install requests
    embedding 路径需要:
        pip install numpy onnxruntime transformers
    Qdrant 必须在 QDRANT_URL（默认 localhost:6333）运行中。
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path

from index_extraction import (
    BOARD_ROOT,
    EXCLUDED_CONCEPT_IDS,
    INDEX_SCHEMA,
    REF_PATTERN,
    build_name_text,
    build_search_text,
    compute_index_version as _compute_index_version_pure,
    embedding_model_id,
    extract_concepts,
    extract_flow,
    extract_instances,
    identity_of,
    load_json,
    make_uuid,
    strip_refs,
)

# ---- 基础设施配置 ----
MODEL_DIR = BOARD_ROOT / "backend" / "BoardAI.Api" / "ml_models" / "bge-base-zh-v1.5-fp32"
QDRANT_URL = os.environ.get("QDRANT_URL", "http://localhost:6333").rstrip("/")
BATCH_SIZE = 100
POOLING = "mean"  # import-only callers keep the legacy version contract
SWITCH_ALIASES = True

# 纯逻辑模块不假设模型路径；CLI 在配置/加载模型后提供这两个值。
dimension = 768


def compute_index_version(game_id: str, items: list[dict]) -> str:
    """Compatibility wrapper for callers of the old rebuild_index module."""
    return _compute_index_version_pure(
        game_id,
        items,
        embedding_model_id(MODEL_DIR.name, POOLING),
        dimension,
    )


# ---- 加载本地 ONNX 模型（延迟到首次 embedding，便于抽取/契约测试导入） ----
tokenizer = None
session = None


def _load_model():
    global tokenizer, session, dimension
    if session is not None:
        return
    import numpy as np  # noqa: F401  (局部导入，避免纯提取测试被 optional deps 阻塞)
    import onnxruntime as ort
    from transformers import AutoTokenizer

    print(f"Loading ONNX model from {MODEL_DIR}...")
    tokenizer = AutoTokenizer.from_pretrained(str(MODEL_DIR))
    session = ort.InferenceSession(str(MODEL_DIR / "model.onnx"))
    dimension = session.get_outputs()[0].shape[2]  # 512
    print(f"  Model loaded, dimension={dimension}")


def embed(text: str):
    """文本 → L2 归一化向量；pooling 与 C# 配置一致并进入索引版本。"""
    import numpy as np

    _load_model()
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
    emb = hidden[0, 0].copy() if POOLING == "cls" else (hidden[0] * mask[:, None]).sum(axis=0) / mask.sum()
    # L2 normalize
    norm = np.linalg.norm(emb)
    if norm > 1e-8:
        emb = emb / norm
    return emb.astype(np.float32)


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
    import requests

    r = requests.put(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()


def qdrant_post(path: str, body: dict | None = None):
    import requests

    r = requests.post(f"{QDRANT_URL}{path}", json=body, timeout=30)
    r.raise_for_status()
    return r


def qdrant_delete(path: str):
    import requests

    r = requests.delete(f"{QDRANT_URL}{path}", timeout=30)
    r.raise_for_status()


def collection_exists(name: str) -> bool:
    import requests

    r = requests.get(f"{QDRANT_URL}/collections/{name}", timeout=30)
    if r.status_code == 404:
        return False
    r.raise_for_status()
    return True


def collection_count(name: str) -> int:
    import requests

    r = requests.post(
        f"{QDRANT_URL}/collections/{name}/points/count",
        json={"exact": True},
        timeout=30,
    )
    r.raise_for_status()
    return int(r.json()["result"]["count"])


def rebuild_game(game_id: str):
    items = []

    def add(extracted: list[dict], source: str):
        """按来源标记每个条目——同名概念（如 <ontology::flip>）跨文件重复出现，
        uuid 需含来源前缀避免 upsert 互相覆盖（2026-08-13 修复：曾导致 515 概念只写入 416）"""
        for c in extracted:
            c["source"] = source
            c.setdefault("path", c.get("concept_id", ""))
            c.setdefault("owner_path", "")
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

    # fail closed：同一 source + 身份路径必须唯一，否则 Qdrant 会静默覆盖。
    seen_identities: set[tuple[str, str]] = set()
    for c in items:
        key = (c.get("source", ""), identity_of(c))
        if key in seen_identities:
            raise RuntimeError(
                f"duplicate index identity in '{game_id}': source={key[0]!r}, "
                f"path={key[1]!r}; 槽位提取或 source 标记有冲突，拒绝写入不完整索引"
            )
        seen_identities.add(key)

    if not items:
        print(f"  No concepts found for '{game_id}'")
        return

    print(f"  {len(items)} concepts found, generating embeddings...")

    # 版本行里的模型维度来自实际 ONNX 输出；纯提取/契约测试导入本模块时不会触发加载。
    _load_model()

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
                    identity = identity_of(c)
                    full_points.append({
                        "id": make_uuid(
                            f"{game_id}::{c.get('source', '')}::{identity}"
                        ),
                        "vector": full_vectors[j],
                        "payload": {
                            "concept_id": c["concept_id"],
                            "path": identity,
                            "owner_path": c.get("owner_path") or "",
                            "type": c["type"],
                            "name_zh": c["name_zh"],
                            "name_en": c["name_en"],
                            "embedding_model": embedding_model_id(MODEL_DIR.name, POOLING),
                        },
                    })

            name_points = []
            if need_name:
                name_batch = [c for c in batch if c["name_text"]]
                name_vectors = [embed(c["name_text"]).tolist() for c in name_batch]
                for c, vec in zip(name_batch, name_vectors):
                    identity = identity_of(c)
                    name_points.append({
                        "id": make_uuid(
                            f"{game_id}::{c.get('source', '')}::{identity}_name"
                        ),
                        "vector": vec,
                        "payload": {
                            "concept_id": c["concept_id"],
                            "path": identity,
                            "owner_path": c.get("owner_path") or "",
                            "type": c["type"],
                            "name_zh": c["name_zh"],
                            "name_en": c["name_en"],
                            "embedding_model": embedding_model_id(MODEL_DIR.name, POOLING),
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

        if not SWITCH_ALIASES:
            print(f"  VERIFIED (not activated) {game_id}: {version} full={actual_full}, name={actual_name}")
            return

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
    global POOLING, MODEL_DIR, SWITCH_ALIASES
    parser = argparse.ArgumentParser(description="Rebuild vector index (offline)")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true")
    group.add_argument("--game", type=str)
    parser.add_argument("--pooling", choices=("mean", "cls"), help="Default: Embedding:Pooling in appsettings, overridden by Embedding__Pooling")
    parser.add_argument("--no-switch", action="store_true", help="Build and verify new collections without changing active aliases")
    args = parser.parse_args()
    config = load_json(BOARD_ROOT / "backend/BoardAI.Api/appsettings.json")
    POOLING = (args.pooling or os.environ.get("Embedding__Pooling") or config.get("Embedding", {}).get("Pooling", "mean")).strip().lower()
    configured_model = os.environ.get("Embedding__ModelDir") or config.get("Embedding", {}).get("ModelDir")
    if configured_model:
        MODEL_DIR = Path(configured_model)
        if not MODEL_DIR.is_absolute():
            MODEL_DIR = BOARD_ROOT / MODEL_DIR
    SWITCH_ALIASES = not args.no_switch
    embedding_model_id(MODEL_DIR.name, POOLING)  # reject invalid values before writing anything

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
