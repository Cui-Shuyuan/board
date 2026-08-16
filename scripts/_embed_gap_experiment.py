# -*- coding: utf-8 -*-
"""离线实验：为什么正确概念和干扰项的向量分数拉不开差距。
三条件对比：
  A. 无指令前缀 × 旧混合文本（id + 中文名 + 英文名）
  B. 无指令前缀 × 纯中文名
  C. 查询加 BGE 官方指令前缀 × 纯中文名
输出每条件 top-10 排序与 gap，以及全部文档余弦的整体分布。
"""
import io
import json
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

MODEL_DIR = Path(r"D:\workspace\board\backend\BoardAI.Api\ml_models\bge-base-zh-v1.5")
BOARD = Path(r"D:\workspace\board")
INSTRUCTION = "为这个句子生成表示以用于检索相关文章："

print("Loading model...")
tokenizer = AutoTokenizer.from_pretrained(str(MODEL_DIR))
session = ort.InferenceSession(str(MODEL_DIR / "model.onnx"))
dim = session.get_outputs()[0].shape[2]
print(f"  dim={dim}")


def embed(text: str) -> np.ndarray:
    enc = tokenizer(text, padding=True, truncation=True, max_length=512, return_tensors="np")
    feed = {"input_ids": enc["input_ids"], "attention_mask": enc["attention_mask"]}
    if "token_type_ids" in tokenizer.model_input_names:
        feed["token_type_ids"] = np.zeros_like(enc["input_ids"])
    out = session.run(None, feed)
    hidden = out[0][0]
    mask = enc["attention_mask"][0]
    v = (hidden * mask[:, None]).sum(axis=0) / mask.sum()
    n = np.linalg.norm(v)
    return (v / n).astype(np.float32) if n > 1e-8 else v


# ---- 收集文档（name_zh / name_en / id）----
docs = []  # (id, zh, en)
for path in [BOARD / "ontology" / "concepts.json",
             BOARD / "games" / "agricola" / "concepts.json"]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    for c in data.get("concepts", []):
        docs.append((c.get("id", ""), c.get("name", {}).get("zh", ""), c.get("name", {}).get("en", "")))
    for arr in ["objects", "actions", "triggers", "conditions"]:
        for c in data.get(arr, []):
            docs.append((c.get("id", ""), c.get("name", {}).get("zh", ""), c.get("name", {}).get("en", "")))
print(f"docs: {len(docs)}")

mixed = [f"{d[0]} {d[1]} {d[2]}".strip() for d in docs]
zhonly = [d[1] or d[2] for d in docs]

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

# ---- 预嵌文档（三种条件共用 B/C 的 zhonly）----
print("embedding docs...")
V_mixed = np.stack([embed(t) for t in mixed])
V_zh = np.stack([embed(t) for t in zhonly])


def rank(qvec, V):
    scores = V @ qvec
    order = np.argsort(-scores)
    return scores, order


def show(q, qvec, V, ids):
    scores, order = rank(qvec, V)
    return scores[order[:10]], [ids[i] for i in order[:10]]


print("\n" + "=" * 100)
for q, target in QUERIES.items():
    print(f"\n### 查询「{q}」（正确概念 {target}）")
    vA = embed(q)
    vC = embed(INSTRUCTION + q)
    for label, qv, V, ids in [
        ("A 无指令×混合文本", vA, V_mixed, [d[0] for d in docs]),
        ("B 无指令×纯中文名", vA, V_zh, [d[0] for d in docs]),
        ("C 指令前缀×纯中文名", vC, V_zh, [d[0] for d in docs]),
    ]:
        scores, order = rank(qv, V)
        top = list(zip(scores[order[:5]], [ids[i] for i in order[:5]]))
        gap = scores[order[0]] - scores[order[1]]
        hit_rank = np.where(np.array(ids)[order] == target)[0]
        hit_rank = hit_rank[0] + 1 if len(hit_rank) else None
        hit_score = scores[order[hit_rank - 1]] if hit_rank else 0
        names = [(docs[int(i)][1], f"{s:.3f}") for s, i in zip(scores[order[:5]], order[:5])]
        print(f"  {label}: gap={gap:.3f} 正确概念第{hit_rank}名({hit_score:.3f})")
        for (n, sc), (i, _) in zip(names, top):
            mark = " <<" if i == target else ""
            print(f"     {sc}  {n}{mark}")

# ---- 整体分布：余弦值域有多窄 ----
print("\n" + "=" * 100)
print("== 全部文档 × 查询的余弦分布（B 条件）==")
qs = list(QUERIES.keys())
for q in qs:
    scores, _ = rank(embed(q), V_zh)
    top = np.sort(scores)[::-1]
    print(f"  「{q}」: top1={top[0]:.3f} top2={top[1]:.3f} gap={top[0]-top[1]:.3f} "
          f"top5={top[4]:.3f} top15={top[14]:.3f} 中位={np.median(scores):.3f} "
          f"最小={scores.min():.3f} std={scores.std():.3f}")

print("\n== 各向异性检查：随机 1000 对文档的互相似度分布 ==")
rng = np.random.default_rng(42)
idx = rng.integers(0, len(V_zh), size=(1000, 2))
sims = np.array([V_zh[i] @ V_zh[j] for i, j in idx])
print(f"  互相似度 均值={sims.mean():.3f} 中位={np.median(sims):.3f} "
      f"p95={np.percentile(sims, 95):.3f} 最大={sims.max():.3f}")
