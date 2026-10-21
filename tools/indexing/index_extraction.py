"""
纯索引抽取/身份/版本契约。

This module is intentionally dependency-light: importing it must not load
requests, numpy, onnxruntime or transformers, must not load a model, and must
not access the network or run the CLI.  rebuild_index.py owns the Qdrant and
embedding infrastructure and imports these functions.

纯逻辑测试入口（无第三方依赖）:
    python -m unittest discover -s animation -p "test_index_extraction.py"

完整索引重建:
    pip install requests numpy onnxruntime transformers
    # Qdrant must be running
    python tools/indexing/rebuild_index.py --all
"""

from __future__ import annotations

import hashlib
import json
import re
import uuid
from pathlib import Path
from typing import Any

BOARD_ROOT = Path(__file__).resolve().parent.parent.parent
INDEX_SCHEMA = "board-index/v1"

def embedding_model_id(directory_name: str, pooling: str) -> str:
    if pooling not in ("mean", "cls"):
        raise ValueError("Embedding pooling must be mean or cls")
    return directory_name if pooling == "mean" else directory_name + "/cls-v1"

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


def _slot_path(owner_path: str, slot_id: str) -> str:
    """局部槽位的稳定寻址路径：<owner>.<slot_id>。全局概念恒为 <concept_id>。"""
    return f"{owner_path}.{slot_id}" if owner_path else slot_id


def _collect_text(node, parts):
    """递归收集 id/name/description/material 文本 (slots 深层效果描述)"""
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "id":
                parts.append(str(v))
            elif k in ("name", "description", "material") and isinstance(v, dict):
                parts.extend([v.get("zh", ""), v.get("en", "")])
            else:
                _collect_text(v, parts)
    elif isinstance(node, list):
        for v in node:
            _collect_text(v, parts)


def _add_slot(results, slot_id: str, slot_path: str, owner_path: str, value: dict,
              name_zh: str = "", name_en: str = "") -> None:
    parts = [slot_id]
    _collect_text(value, parts)
    results.append({
        "concept_id": slot_id,
        "path": slot_path,
        "owner_path": owner_path,
        "type": "slot",
        "name_zh": name_zh,
        "name_en": name_en,
        # 显式 id/name 与带 name 的裸键槽位都视为正式槽位名称；空名槽位不进 name 集合。
        "name_text": name_zh or name_en,
        "search_text": " ".join(p for p in parts if p),
    })


def _extract_slots(node, results, owner_path: str = "") -> None:
    """递归提取真实 slots。

    身份规则与 C# RulesIndexService 保持一致：
    - 显式 slot_spec（含字符串 id）整体是一个槽位，id/name/material 是元数据；
    - 裸键槽位以 key 为局部 slot_id；
    - <concept> 键是已有全局概念的引用，不作为新槽位重复入库；
    - 局部槽位的 path = <owner>.<slot_id>，与同名全局 concept_id 不互相覆盖。
    """
    if isinstance(node, dict):
        slots = node.get("slots")
        if isinstance(slots, list):
            for slot in slots:
                if not isinstance(slot, dict):
                    continue
                explicit_id = slot.get("id")
                if isinstance(explicit_id, str) and explicit_id:
                    slot_path = _slot_path(owner_path, explicit_id)
                    name = slot.get("name", {}) if isinstance(slot.get("name"), dict) else {}
                    _add_slot(results, explicit_id, slot_path, owner_path, slot,
                              name.get("zh", ""), name.get("en", ""))
                    # 嵌套 slots 归属显式槽位路径。
                    _extract_slots(slot, results, slot_path)
                    continue

                for sk, sv in slot.items():
                    # <概念> 键 = 已有定义的概念引用, 跳过；非 dict 值不是槽位定义。
                    if sk.startswith("<") or not isinstance(sv, dict):
                        continue
                    slot_path = _slot_path(owner_path, sk)
                    slot_name = sv.get("name", {}) if isinstance(sv.get("name"), dict) else {}
                    _add_slot(results, sk, slot_path, owner_path, sv,
                              slot_name.get("zh", ""), slot_name.get("en", ""))
                    # 嵌套 slots 归属裸键槽位路径。
                    _extract_slots(sv, results, slot_path)

        # 继续下钻，发现嵌套概念/槽位内的 slots；slots 本身已按 owner 处理，避免重复。
        for key, value in node.items():
            if key == "slots":
                continue
            _extract_slots(value, results, owner_path)
    elif isinstance(node, list):
        for v in node:
            _extract_slots(v, results, owner_path)


def _concept_item(concept_id: str, type_name: str, source_obj: dict) -> dict:
    name = source_obj.get("name", {}) if isinstance(source_obj.get("name"), dict) else {}
    return {
        "concept_id": concept_id,
        "path": concept_id,
        "owner_path": "",
        "type": type_name,
        "name_zh": name.get("zh", ""),
        "name_en": name.get("en", ""),
        "search_text": build_search_text(source_obj),
    }


def extract_concepts(file_path: Path) -> list[dict[str, Any]]:
    data = load_json(file_path)
    results = []

    if "concepts" in data:
        for c in data["concepts"]:
            concept_id = c.get("id", "")
            results.append(_concept_item(concept_id, "ontology", c))
            _extract_slots(c, results, concept_id)

    array_types = ["objects", "actions", "triggers", "conditions"]
    for arr_type in array_types:
        for c in data.get(arr_type, []):
            concept_id = c.get("id", "")
            results.append(_concept_item(concept_id, arr_type, c))
            _extract_slots(c, results, concept_id)

    for key, value in data.items():
        if key in array_types or key == "concepts":
            continue
        if isinstance(value, dict):
            # 顶层 dict 与 C# RulesIndexService 的 top_level_refs 契约保持一致：
            # 即使元素自带 id 也按 key 身份索引（真实数据中多为无 id 的聚合引用）。
            results.append(_concept_item(key, "top_level_ref", value))
            _extract_slots(value, results, key)

    return results


def extract_instances(file_path: Path) -> list[dict[str, Any]]:
    """从 instances.json 提取所有实例"""
    data = load_json(file_path)
    results = []

    instance_array_types = ["effects", "modules", "cards", "continent_tiles", "sites", "chips"]
    for arr_type in instance_array_types:
        for c in data.get(arr_type, []):
            concept_id = c.get("id", "")
            results.append(_concept_item(concept_id, arr_type, c))

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
                "path": node_id,
                "owner_path": "",
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


def identity_of(item: dict) -> str:
    """索引身份：局部槽位用 owner.slot 路径，普通概念用 concept_id。"""
    return item.get("path") or item["concept_id"]


def compute_index_version(
    game_id: str,
    items: list[dict],
    model_id: str,
    dimension: int,
) -> str:
    """Canonical version line shared with IndexContract.ComputeIndexVersion in C#.

    ``model_id`` and ``dimension`` must be supplied by the infrastructure layer
    after loading/configuring the embedding model.  The pure module does not
    assume a model path or load one.
    """
    sorted_items = sorted(
        items,
        key=lambda c: (
            c.get("source", ""),
            identity_of(c),
            c["concept_id"],
            c["type"],
            c.get("name_zh") or "",
            c.get("name_en") or "",
            c.get("name_text") or "",
            strip_refs(c["search_text"]),
        ),
    )
    lines = [INDEX_SCHEMA, game_id, model_id, str(dimension)]
    for c in sorted_items:
        lines.append(
            "\t".join(
                [
                    c.get("source", ""),
                    identity_of(c),
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
