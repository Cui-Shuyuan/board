#!/usr/bin/env python3
"""Read-only retrieval experiments: pooling, source fragments and a fixed-pool reranker.

No Qdrant writes, active-alias changes, rule edits or model downloads. Dependencies:
numpy/onnxruntime/transformers; --reranker additionally needs torch and local weights.
Labels refer to checked-in rules, not authoritative FAQ answers or answer accuracy.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sqlite3
from collections import defaultdict
from pathlib import Path

import rebuild_index as index

ROOT = index.BOARD_ROOT
INSTRUCTION = "为这个句子生成表示以用于检索相关文章："


def walk(node, pointer=""):
    yield node, pointer
    if isinstance(node, dict):
        for key, value in node.items():
            yield from walk(value, pointer + "/" + key.replace("~", "~0").replace("/", "~1"))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from walk(value, pointer + "/" + str(i))


def source_documents(game):
    result = []
    for rel, extractor, source in [
        ("content/ontology/concepts.json", index.extract_concepts, "ontology"),
        ("content/ontology/flow.json", index.extract_flow, "ontology_flow"),
        (f"content/games/{game}/concepts.json", index.extract_concepts, "game"),
        (f"content/games/{game}/instances.json", index.extract_instances, "instances"),
        (f"content/games/{game}/flow.json", index.extract_flow, "game_flow"),
    ]:
        path = ROOT / rel
        if not path.exists():
            continue
        data = index.load_json(path)
        nodes = list(walk(data))
        for item in extractor(path):
            if item["concept_id"] in index.EXCLUDED_CONCEPT_IDS:
                continue
            owner, *slots = item["path"].split(".")
            found = next(((n, p) for n, p in nodes if isinstance(n, dict) and n.get("id") == owner), None)
            if found is None and isinstance(data.get(owner), dict):
                found = (data[owner], "/" + owner)
            for slot in slots:
                found = next(((n[slot], p + "/" + slot) for n, p in walk(*found)
                              if isinstance(n, dict) and isinstance(n.get(slot), dict)), None) if found else None
            result.append({**item, "source": source, "file": rel, "node": found[0] if found else {},
                           "pointer": found[1] if found else ""})
    return result


def project(game):
    records = source_documents(game)
    names = {r["concept_id"]: r["name_zh"] or r["name_en"] for r in records}

    def expand(text):
        return re.sub(r"<([^<>]+)>", lambda m: names.get(m[1].split("::")[-1], m[1].split("::")[-1]), text)

    whole, facts, named = [], [], []
    for r in records:
        identity = {k: r[k] for k in ("path", "file", "pointer", "source")}
        title = " ".join(x for x in [r["name_zh"], r["name_en"]] if x)
        raw = index.strip_refs(r["search_text"])
        whole.append({**identity, "text": raw})
        named.append({**identity, "text": " ".join([r["concept_id"], title])})
        snippets = []
        for node, p in walk(r["node"], r["pointer"]):
            if not isinstance(node, dict):
                continue
            for key in ("description", "definition", "material", "quantity", "formula"):
                value = node.get(key)
                if isinstance(value, dict) and isinstance(value.get("zh"), str):
                    text = expand(value["zh"]).strip()
                    if text:
                        snippets.append((text, p + "/" + key + "/zh"))
        seen = set()
        for snippet, pointer in snippets:
            # Split long descriptions without inventing facts or adding question wording.
            chunks = re.split(r"(?<=[。！？；])", snippet)
            buf = ""
            for chunk in chunks + [""]:
                if buf and (len(buf) + len(chunk) > 350 or not chunk):
                    if buf not in seen:
                        seen.add(buf)
                        facts.append({**identity, "pointer": pointer, "text": title + "。" + buf})
                    buf = ""
                buf += chunk
        if not seen:
            facts.append({**identity, "text": title + "。" + expand(r["search_text"])})
    return whole, facts, named


def validate_labels(rows):
    for row in rows:
        for relevant in row["relevance"]:
            for ref in relevant["evidence"]:
                node = index.load_json(ROOT / ref["file"])
                for part in ref["pointer"].strip("/").split("/"):
                    key = part.replace("~1", "/").replace("~0", "~")
                    node = node[int(key)] if isinstance(node, list) else node[key]
                if not isinstance(node, dict):
                    raise ValueError(f"Label is not a rule node: {row['id']} {ref}")
        if set(row["required"]) - {r["id"] for r in row["relevance"]}:
            raise ValueError(f"Missing required evidence: {row['id']}")
    groups = defaultdict(set)
    for row in rows:
        groups[(row["game"], row["topic"])].add(row["split"])
    if any(len(splits) != 1 for splits in groups.values()):
        raise ValueError("Paraphrases of the same topic leak across dev/holdout")


class Encoder:
    def __init__(self, model, cache):
        import numpy as np
        import onnxruntime as ort
        from transformers import AutoTokenizer
        self.np = np
        self.tokenizer = AutoTokenizer.from_pretrained(str(model), local_files_only=True)
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        self.session = ort.InferenceSession(str(model / "model.onnx"), sess_options=options)
        self.inputs = {i.name for i in self.session.get_inputs()}
        self.model_hash = hashlib.sha256((model / "model.onnx").read_bytes()).hexdigest()
        self.tokenizer_hash = hashlib.sha256((model / "tokenizer.json").read_bytes()).hexdigest()
        self.cache = sqlite3.connect(cache)
        self.cache.execute("CREATE TABLE IF NOT EXISTS vectors (key TEXT PRIMARY KEY, mean BLOB, cls BLOB)")
        self.cache.execute("CREATE TABLE IF NOT EXISTS reranks (key TEXT PRIMARY KEY, score REAL)")

    def encode(self, texts):
        np = self.np
        results = {}
        unique = sorted(set(texts), key=len)
        missing = []
        for text in unique:
            key = hashlib.sha256((self.model_hash + self.tokenizer_hash + text).encode()).hexdigest()
            row = self.cache.execute("SELECT mean,cls FROM vectors WHERE key=?", (key,)).fetchone()
            if row:
                results[text] = tuple(np.frombuffer(v, dtype=np.float32).copy() for v in row)
            else:
                missing.append((text, key))
        for start in range(0, len(missing), 8):
            batch = missing[start:start + 8]
            encoded = self.tokenizer([t for t, _ in batch], padding=True, truncation=True, max_length=512, return_tensors="np")
            hidden = self.session.run(["last_hidden_state"], {k: encoded[k].astype(np.int64) for k in self.inputs})[0]
            mask = encoded["attention_mask"][:, :, None]
            mean = (hidden * mask).sum(axis=1) / mask.sum(axis=1)
            cls = hidden[:, 0, :].copy()
            mean /= np.maximum(np.linalg.norm(mean, axis=1, keepdims=True), 1e-8)
            cls /= np.maximum(np.linalg.norm(cls, axis=1, keepdims=True), 1e-8)
            for (text, key), m, c in zip(batch, mean, cls):
                m, c = m.astype(np.float32), c.astype(np.float32)
                results[text] = (m, c)
                self.cache.execute("INSERT OR REPLACE INTO vectors VALUES (?,?,?)", (key, m.tobytes(), c.tobytes()))
            self.cache.commit()
            if start % 128 == 0:
                print(f"encode {start + len(batch)}/{len(missing)}", flush=True)
        return results


def ranked(documents, scores, limit=60):
    seen, result = set(), []
    for i in sorted(range(len(documents)), key=lambda i: (-float(scores[i]), documents[i]["path"])):
        doc = documents[i]
        if doc["path"] in seen:
            continue
        seen.add(doc["path"])
        result.append({**doc, "score": round(float(scores[i]), 6)})
        if len(result) >= limit:
            break
    return result


def metrics(row, result):
    required = set(row["required"])
    ids = [d["path"] for d in result]
    grades = {r["id"]: r["grade"] for r in row["relevance"]}
    ranks = [ids.index(cid) + 1 for cid in required if cid in ids]
    dcg = sum((2 ** grades.get(cid, 0) - 1) / math.log2(i + 2) for i, cid in enumerate(ids[:10]))
    ideal = sum((2 ** grade - 1) / math.log2(i + 2) for i, grade in enumerate(sorted(grades.values(), reverse=True)[:10]))
    return dict(hit1=bool(required & set(ids[:1])), hit3=bool(required & set(ids[:3])),
                hit10=bool(required & set(ids[:10])), coverage3=bool(required) and required <= set(ids[:3]),
                coverage10=bool(required) and required <= set(ids[:10]),
                mrr=1 / min(ranks) if ranks else 0, ndcg10=dcg / ideal if ideal else 0)


class Reranker:
    def __init__(self, directory, cache):
        import torch
        from transformers import AutoModelForSequenceClassification, AutoTokenizer
        torch.set_num_threads(4)
        self.torch, self.cache = torch, cache
        self.tokenizer = AutoTokenizer.from_pretrained(str(directory), local_files_only=True)
        self.model = AutoModelForSequenceClassification.from_pretrained(str(directory), local_files_only=True, trust_remote_code=False).eval()
        self.model_hash = hashlib.sha256((directory / "model.safetensors").read_bytes()).hexdigest()

    def rank(self, query, documents):
        scores, missing = {}, []
        for i, doc in enumerate(documents):
            key = hashlib.sha256((self.model_hash + query + "\0" + doc["text"]).encode()).hexdigest()
            cached = self.cache.execute("SELECT score FROM reranks WHERE key=?", (key,)).fetchone()
            if cached:
                scores[i] = cached[0]
            else:
                missing.append((i, key))
        for start in range(0, len(missing), 4):
            batch = missing[start:start + 4]
            encoded = self.tokenizer([(query, documents[i]["text"]) for i, _ in batch], padding=True, truncation=True, max_length=512, return_tensors="pt")
            with self.torch.inference_mode():
                logits = self.model(**encoded).logits.view(-1).float().tolist()
            for (i, key), score in zip(batch, logits):
                scores[i] = score
                self.cache.execute("INSERT OR REPLACE INTO reranks VALUES (?,?)", (key, score))
            self.cache.commit()
        return ranked(documents, [scores[i] for i in range(len(documents))])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--questions", type=Path, default=ROOT / "tools/qa/retrieval_questions.jsonl")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--model", type=Path, default=index.MODEL_DIR)
    parser.add_argument("--reranker", type=Path)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.questions.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    validate_labels(rows)
    if args.validate_only:
        print(f"Validated {len(rows)} source-anchored questions")
        return
    args.out.mkdir(parents=True, exist_ok=True)
    encoder = Encoder(args.model, args.out / "embeddings.sqlite")
    corpora = {game: project(game) for game in sorted({r["game"] for r in rows})}
    all_texts = [doc["text"] for corpus in corpora.values() for documents in corpus for doc in documents]
    all_texts += [p + r["query"] for r in rows for p in ("", INSTRUCTION)]
    vectors = encoder.encode(all_texts)
    reranker = Reranker(args.reranker, encoder.cache) if args.reranker else None
    traces = []
    for row in rows:
        whole, facts, names = corpora[row["game"]]
        strategies = {}
        for label, docs, pooling, instruction in [
            ("mean_whole", whole, 0, False), ("cls_whole", whole, 1, False),
            ("cls_instruction", whole, 1, True), ("mean_facts", facts, 0, False),
            ("cls_facts", facts, 1, False), ("cls_facts_instruction", facts, 1, True),
            ("cls_names", names, 1, False),
        ]:
            q = vectors[(INSTRUCTION if instruction else "") + row["query"]][pooling]
            matrix = encoder.np.stack([vectors[d["text"]][pooling] for d in docs])
            strategies[label] = ranked(docs, matrix @ q)
        # Identical candidates for RRF and reranker; no oracle insertion of expected IDs.
        union, rrf = {}, defaultdict(float)
        for label, limit in [("mean_whole", 30), ("cls_whole", 30), ("cls_facts", 30), ("cls_names", 15)]:
            for rank, doc in enumerate(strategies[label][:limit], 1):
                union.setdefault(doc["path"], doc)
                rrf[doc["path"]] += 1 / (60 + rank)
        fixed = sorted(union.values(), key=lambda d: (-rrf[d["path"]], d["path"]))[:60]
        strategies["rrf_union"] = [{**d, "score": rrf[d["path"]]} for d in fixed]
        # A concept's strongest fact is its context for reranking, even if recalled via another channel.
        fact_by_id = {d["path"]: d for d in strategies["cls_facts"]}
        fixed_context = [fact_by_id.get(d["path"], d) for d in fixed]
        if reranker:
            strategies["cross_encoder"] = reranker.rank(row["query"], fixed_context)
        traces.append({**row, "fixedPoolContainsAllRequired": set(row["required"]) <= {d["path"] for d in fixed},
                       "strategies": {k: {"metrics": metrics(row, v), "top": v[:30]} for k, v in strategies.items()}})
        (args.out / "traces.json").write_text(json.dumps(traces, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"question {row['id']} ({row['split']}) complete", flush=True)
    summary = {}
    for split in ("dev", "holdout", "all"):
        answerable = [t for t in traces if t["kind"] == "answerable" and (split == "all" or t["split"] == split)]
        summary[split] = {name: {"n": len(answerable), **{k: round(sum(t["strategies"][name]["metrics"][k] for t in answerable) / len(answerable), 4)
                            for k in ("hit1", "hit3", "hit10", "coverage3", "coverage10", "mrr", "ndcg10")}}
                          for name in traces[0]["strategies"]}
    summary["metadata"] = dict(modelHash=encoder.model_hash, tokenizerHash=encoder.tokenizer_hash,
                               queryCount=len(rows), corpusCounts={g: [len(x) for x in c] for g, c in corpora.items()},
                               rerankerHash=reranker.model_hash if reranker else None,
                               note="排名实验，不是问答准确率；未修改生产索引或自动确认阈值。")
    (args.out / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.out / "corpus.json").write_text(json.dumps(corpora, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
