import requests, numpy as np
from transformers import AutoTokenizer
import onnxruntime as ort
session = ort.InferenceSession("backend/BoardAI.Api/ml_models/bge-small-zh/model.onnx")
tokenizer = AutoTokenizer.from_pretrained("backend/BoardAI.Api/ml_models/bge-small-zh")

def embed(text):
    inputs = tokenizer(text, padding=True, truncation=True, max_length=512, return_tensors="np")
    outputs = session.run(None, {"input_ids": inputs["input_ids"], "attention_mask": inputs["attention_mask"]})
    hidden, mask = outputs[0], inputs["attention_mask"][0]
    vec = hidden[0, :int(mask.sum()), :].mean(axis=0)
    return (vec / np.linalg.norm(vec)).tolist()

targets = ["build_boat", "place_boat", "board_tribe", "build_farm", "place_farm", "build_statue", "place_statue", "build_settlement"]
vec = embed("造船")
r = requests.post("http://localhost:6333/collections/board_civolution/points/search",
                  json={"vector": vec, "limit": 50, "with_payload": True})
print("Query: 造船 -> build_boat rank")
for i, res in enumerate(r.json()["result"]):
    pid = res["payload"].get("concept_id", "?")
    if pid in targets:
        name = res["payload"].get("name_zh", "")
        print(f"  #{i+1:2d} {pid:35s} {name:20s} score={res['score']:.4f}")
