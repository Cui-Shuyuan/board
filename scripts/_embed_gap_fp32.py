# -*- coding: utf-8 -*-
"""fp32 对照实验：官方 BAAI/bge-base-zh-v1.5 权重（torch fp32）在同样查询上的
余弦分布，与 int8 量化 ONNX 版（scripts/_embed_gap_experiment.py 的 B 条件）对比。

条件：纯中文名文档，无指令前缀，mean pooling + L2 归一化（与 C# 端完全一致）。
"""
import io
import json
import sys
from pathlib import Path

import numpy as np
import torch
from transformers import AutoModel, AutoTokenizer

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

MODEL_DIR = Path(r"D:\workspace\board\backend\BoardAI.Api\ml_models\_tmp_bge_fp32")
BOARD = Path(r"D:\workspace\board")

print("Loading fp32 model...")
tokenizer = AutoTokenizer.from_pretrained(str(MODEL_DIR))
model = AutoModel.from_pretrained(str(MODEL_DIR))  # fp32 默认
model.eval()
print(f"  params: {sum(p.numel() for p in model.parameters()) / 1e6:.0f}M, "
      f"dtype: {next(model.parameters()).dtype}")


@torch.no_grad()
def embed(text: str) -> np.ndarray:
    enc = tokenizer(text, padding=True, truncation=True, max_length=512, return_tensors="pt")
    out = model(**enc)
    hidden = out.last_hidden_state[0]                      # [seq, 768]
    mask = enc["attention_mask"][0].float()
    v = (hidden * mask[:, None]).sum(dim=0) / mask.sum()   # mean pooling
    v = v / v.norm()                                       # L2
    return v.numpy().astype(np.float32)


# ---- 文档（与 int8 实验相同的 216 概念、纯中文名）----
docs = []
for path in [BOARD / "ontology" / "concepts.json",
             BOARD / "games" / "agricola" / "concepts.json"]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    for c in data.get("concepts", []):
        docs.append((c.get("id", ""), c.get("name", {}).get("zh", ""), c.get("name", {}).get("en", "")))
    for arr in ["objects", "actions", "triggers", "conditions"]:
        for c in data.get(arr, []):
            docs.append((c.get("id", ""), c.get("name", {}).get("zh", ""), c.get("name", {}).get("en", "")))
zhonly = [d[1] or d[2] for d in docs]
print(f"docs: {len(docs)}")

print("embedding docs (fp32)...")
V = np.stack([embed(t) for t in zhonly])

QUERIES = {
    "烘焙": "bake_bread",
    "翻修": "renovation",
    "乞讨卡": "begging_marker",
    "宠物": "animal",
    "职业牌": "occupation_card",
    "计分": "final_scoring",
    "主要改良行动": "build_major_improvement",
    "播种谷物": "sow",
}

print("\n" + "=" * 100)
print("fp32 纯中文名（对照 int8 实验 B 条件）")
for q, target in QUERIES.items():
    scores = V @ embed(q)
    order = np.argsort(-scores)
    top = np.sort(scores)[::-1]
    gap = top[0] - top[1]
    hit_rank = np.where(np.array([d[0] for d in docs])[order] == target)[0]
    hit_rank = hit_rank[0] + 1 if len(hit_rank) else None
    top5 = [(docs[int(i)][1], f"{s:.3f}") for s, i in zip(scores[order[:5]], order[:5])]
    print(f"  「{q}」: top1={top[0]:.3f} gap={gap:.3f} 正确概念第{hit_rank}名 "
          f"top5={top[4]:.3f} top15={top[14]:.3f} 中位={np.median(scores):.3f} "
          f"最小={scores.min():.3f} std={scores.std():.3f}")
    for n, sc in top5:
        print(f"     {sc}  {n}")

print("\n== 各向异性检查（fp32）：随机 1000 对文档的互相似度分布 ==")
rng = np.random.default_rng(42)
idx = rng.integers(0, len(V), size=(1000, 2))
sims = np.array([V[i] @ V[j] for i, j in idx])
print(f"  均值={sims.mean():.3f} 中位={np.median(sims):.3f} "
      f"p95={np.percentile(sims, 95):.3f} 最大={sims.max():.3f}")
