#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Fetch rule descriptions for matched/related ids seen in comparison runs.

Output is keyed as ``<game>::<concept_id>`` to avoid id collisions such as
``setup`` across different games.
"""
from __future__ import annotations

import argparse
import json
import urllib.parse
import urllib.request
from pathlib import Path


def get_json(url: str):
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def post_json(url: str, body: dict):
    req = urllib.request.Request(url, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def extract_text(detail: dict) -> tuple[str, str]:
    name = detail.get("name") or ""
    if isinstance(name, dict):
        name = name.get("zh") or name.get("en") or ""
    desc = detail.get("description") or ""
    if isinstance(desc, dict):
        desc = desc.get("zh") or desc.get("en") or ""
    return str(name), str(desc)


def extract_first_detail(payload) -> dict:
    if isinstance(payload, list) and payload:
        first = payload[0]
        return first if isinstance(first, dict) else {}
    if isinstance(payload, dict):
        if isinstance(payload.get("matched"), list) and payload["matched"]:
            return payload["matched"][0]
        return payload
    return {}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--api", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    pairs: set[tuple[str, str]] = set()
    for line in args.runs.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        run = json.loads(line)
        game = run["game"]
        evidence = run.get("evidence") or {}
        for query in evidence.get("queries") or []:
            for key in ("matched", "references"):
                for item in query.get(key) or []:
                    cid = item.get("id") or item.get("Id")
                    if cid:
                        pairs.add((game, cid))

    results = {}
    failures = []
    for index, (game, cid) in enumerate(sorted(pairs), 1):
        encoded = urllib.parse.quote(cid, safe="")
        detail = {}
        try:
            payload = get_json(f"{args.api}/api/rules/games/{game}/concepts/{encoded}")
            detail = extract_first_detail(payload)
        except Exception:  # noqa: BLE001
            pass
        if not detail:
            try:
                payload = post_json(f"{args.api}/api/rules/games/{game}/execute-plan", {
                    "question": "",
                    "plan": {"queries": [{"relation": "explain", "entity": cid}]}
                })
                item = (payload.get("results") or [{}])[0]
                if item.get("status") == "ok" and item.get("matched"):
                    detail = item["matched"][0]
            except Exception:  # noqa: BLE001
                pass
        name, desc = extract_text(detail) if detail else ("", "")
        if not desc:
            failures.append({"game": game, "id": cid})
        results[f"{game}::{cid}"] = {"id": cid, "name": name, "description": desc}
        if index % 50 == 0:
            print(f"progress {index}/{len(pairs)}", flush=True)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"fetching {len(pairs)} ids; wrote {args.out}; missing descriptions={len(failures)}")
    for item in failures[:20]:
        print("MISSING", item)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
