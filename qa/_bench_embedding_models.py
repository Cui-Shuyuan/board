# -*- coding: utf-8 -*-
"""嵌入模型区分度基准：bge-small-zh（现网） vs bge-base-zh-v1.5（候选）。
复刻 rebuild_index 的 name 集合构造（id + name_zh + name_en，剥离 <> 引用），
用同一组探测词对比两个模型的 top-5 排序与分数分布。
"""
import json
import re
from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

ROOT = Path(r"D:\workspace\board")
SMALL = ROOT / "backend" / "BoardAI.Api" / "ml_models" / "bge-small-zh"
BASE = Path(r"D:\Temp\models\bge-base-zh-v1.5")

REF_PATTERN = re.compile(r"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>")
strip_refs = lambda t: REF_PATTERN.sub("", t)

# 探测词：精确名 / 经典别名 / 无意义词 / 自然语言转述 / 歧义对
PROBES = [
    "钱币", "招募官", "采石场", "冒险家", "水果", "港口", "码头",
    "杜布隆", "市长", "殖民者", "探矿者", "靛蓝", "圣胡安",
    "小精灵", "魔法卡",
    "领工人的角色", "采石头的地方", "卖货换钱的地方", "装货得分",
]


def collect_names():
    """按 rebuild_index 相同方式收集 name_text（含 ontology + 游戏概念 + flow 节点）。"""
    items = []

    def add_from(path, section, source):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if "concepts" in data:
            for c in data["concepts"]:
                items.append((
                    c.get("id", ""), c.get("name", {}).get("zh", ""),
                    c.get("name", {}).get("en", ""), source))
        for arr in ("objects", "actions", "triggers", "conditions"):
            for c in data.get(arr, []):
                items.append((
                    c.get("id", ""), c.get("name", {}).get("zh", ""),
                    c.get("name", {}).get("en", ""), source))

    def walk_flow(node, source):
        nid = node.get("id", "")
        if nid and any(k in node for k in ("specifies", "extends", "instance_of")):
            name = node.get("name", {})
            items.append((nid, name.get("zh", ""), name.get("en", ""), source))
        for evt in node.get("events", []):
            walk_flow(evt, source)
        for opt in node.get("options", []):
            if isinstance(opt, dict):
                walk_flow(opt, source)
        for key in ("<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
                    "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
                    "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
                    "<ontology::instant_content>", "<ontology::instant_cost>",
                    "<ontology::continuous_effect>", "<ontology::effect>"):
            if isinstance(node.get(key), dict):
                walk_flow(node[key], source)

    add_from(ROOT / "ontology" / "concepts.json", "concepts", "ontology")
    add_from(ROOT / "games" / "puerto-rico" / "concepts.json", "concepts", "game")
    flow = json.loads((ROOT / "games" / "puerto-rico" / "flow.json").read_text(encoding="utf-8-sig"))
    for proc in flow.get("procedures", []):
        walk_flow(proc, "game_flow")
    onto_flow = ROOT / "ontology" / "flow.json"
    if onto_flow.exists():
        f = json.loads(onto_flow.read_text(encoding="utf-8-sig"))
        walk_flow(f, "ontology_flow")
    return items


class Model:
    def __init__(self, path):
        self.tok = AutoTokenizer.from_pretrained(str(path))
        onnx_file = path / "model.onnx"
        if not onnx_file.exists():
            onnx_file = path / "model_quantized.onnx"
        self.sess = ort.InferenceSession(str(onnx_file))

    def embed(self, texts):
        out = []
        for t in texts:
            enc = self.tok(t, padding=True, truncation=True, max_length=512, return_tensors="np")
            feed = {"input_ids": enc["input_ids"], "attention_mask": enc["attention_mask"]}
            if "token_type_ids" in self.tok.model_input_names:
                feed["token_type_ids"] = np.zeros_like(enc["input_ids"])
            hidden = self.sess.run(None, feed)[0]
            mask = enc["attention_mask"][0]
            emb = (hidden[0] * mask[:, None]).sum(axis=0) / mask.sum()
            n = np.linalg.norm(emb)
            out.append((emb / n).astype(np.float32) if n > 1e-8 else np.zeros_like(emb))
        return np.stack(out)


def main():
    items = collect_names()
    names = [strip_refs(f"{i} {z} {e}") for i, z, e, _ in items]
    print(f"collection items: {len(items)}")

    print("loading bge-small-zh ...")
    small = Model(SMALL)
    print("loading bge-base-zh-v1.5 ...")
    base = Model(BASE)

    for label, model in (("small", small), ("base", base)):
        docs = model.embed(names)
        qs = model.embed(PROBES)
        sim = qs @ docs.T
        print(f"\n===== {label} =====")
        for qi, q in enumerate(PROBES):
            top = np.argsort(-sim[qi])[:5]
            row = [(round(float(sim[qi][j]), 3), items[j][0], items[j][1]) for j in top]
            print(f"{q:　<10}", row)


if __name__ == "__main__":
    main()
