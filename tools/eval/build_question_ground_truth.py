#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build per-question acceptable rule snippets from an execute-plan API.

Positive questions list expected_rule_ids. For each id this script asks the
current rules API for the formal concept description and stores it as an
acceptable evidence snippet. Negative questions intentionally have no snippets.
"""
from __future__ import annotations

import argparse
import json
import urllib.request
from pathlib import Path


def post_json(url: str, body: dict):
    req = urllib.request.Request(url, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def find_localized_text(node, keys: set[str]) -> str:
    if isinstance(node, dict):
        for key, value in node.items():
            if key in keys:
                if isinstance(value, str) and value.strip():
                    return value.strip()
                if isinstance(value, dict):
                    text = value.get("zh") or value.get("en")
                    if isinstance(text, str) and text.strip():
                        return text.strip()
            found = find_localized_text(value, keys)
            if found:
                return found
    elif isinstance(node, list):
        for value in node:
            found = find_localized_text(value, keys)
            if found:
                return found
    return ""


def extract_matched(payload: dict, fallback_rule_id: str) -> list[dict]:
    item = (payload.get("results") or [{}])[0]
    if item.get("status") != "ok":
        return []
    out = []
    for matched in item.get("matched") or []:
        desc = matched.get("description")
        if isinstance(desc, dict):
            desc = desc.get("zh") or desc.get("en") or ""
        if not desc:
            desc = find_localized_text(matched, {"description"})
        name = matched.get("name")
        if isinstance(name, dict):
            name = name.get("zh") or name.get("en") or ""
        if not name:
            name = find_localized_text(matched, {"name"})
        out.append({
            # Local slots may not have a top-level id; the requested canonical
            # path is still the stable identity for evidence.
            "rule_id": matched.get("id") or fallback_rule_id,
            "name": name or "",
            "description": str(desc or ""),
        })
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--questions", type=Path, default=Path("tools/qa/comprehensive_eval_questions.jsonl"))
    parser.add_argument("--api", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    questions = [json.loads(line) for line in args.questions.read_text(encoding="utf-8").splitlines() if line.strip()]
    result = {}
    failures = []
    for index, question in enumerate(questions, 1):
        snippets = []
        for rule_id in question.get("expected_rule_ids") or []:
            relation = question.get("relation_hint") or "explain"
            matched = []
            for candidate_relation in (relation, "explain", "condition", "ordering", "boundary", "flow"):
                try:
                    payload = post_json(
                        f"{args.api}/api/rules/games/{question['game']}/execute-plan",
                        {"question": "", "plan": {"queries": [{"relation": candidate_relation, "entity": rule_id}]}},
                    )
                    matched = extract_matched(payload, rule_id)
                except Exception:  # noqa: BLE001
                    matched = []
                if matched:
                    break
            if not matched:
                failures.append({"question_id": question["id"], "game": question["game"], "rule_id": rule_id})
                continue
            for item in matched:
                if item["rule_id"] not in {x["rule_id"] for x in snippets}:
                    snippets.append(item)
        result[question["id"]] = {**question, "acceptable_rule_evidence": snippets}
        if index % 25 == 0:
            print(f"progress {index}/{len(questions)}", flush=True)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {args.out}; questions={len(result)}; unresolved_expected_ids={len(failures)}")
    for failure in failures[:30]:
        print("MISSING", failure)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
