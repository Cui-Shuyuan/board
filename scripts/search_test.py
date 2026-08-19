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

targets = ["build_boat", "place_boat", "board_tribe", "build_farm", "build_statue", "build_settlement"]

for mode, collection in [("name", "board_civolution_name"), ("full", "board_civolution")]:
    vec = embed("造船")
    r = requests.post(f"http://localhost:6333/collections/{collection}/points/search",
                      json={"vector": vec, "limit": 20, "with_payload": True})
    print(f"=== search_mode={mode}, query=造船 ===")
    print(f"{'#':>3} {'score':>7} {'id':40s} name")
    print("-" * 80)
    for i, res in enumerate(r.json()["result"]):
        pid = res["payload"].get("concept_id", "?")
        name = res["payload"].get("name_zh", "")
        score = res["score"]
        marker = " <<<" if pid in targets else ""
        print(f"{i+1:3d} {score:.4f}   {pid:40s} {name}{marker}")
    print()
