import json
import re
from pathlib import Path

ONTOLOGY_PATH = Path("D:/workspace/board/ontology/ontology.json")
GAME_FILES = [
    Path("D:/workspace/board/games/splendor/concepts.json"),
    Path("D:/workspace/board/games/splendor/flow.json"),
    Path("D:/workspace/board/games/civolution/concepts.json"),
    Path("D:/workspace/board/games/civolution/flow.json"),
]


def load_ontology_ids():
    data = json.loads(ONTOLOGY_PATH.read_text(encoding="utf-8"))
    return {c["id"] for c in data["concepts"]}


def replace_in_string(s: str, ontology_ids: set) -> str:
    """Replace <concept_id> with <ontology::concept_id> only for ontology ids."""
    if not isinstance(s, str):
        return s

    # Sort by length descending to avoid partial matches
    sorted_ids = sorted(ontology_ids, key=lambda x: len(x), reverse=True)

    result = s
    for cid in sorted_ids:
        pattern = re.escape(f"<{cid}>")
        replacement = f"<ontology::{cid}>"
        result = re.sub(pattern, replacement, result)
    return result


def transform_value(value, ontology_ids: set):
    """Recursively transform string values in JSON data."""
    if isinstance(value, str):
        return replace_in_string(value, ontology_ids)
    elif isinstance(value, list):
        return [transform_value(item, ontology_ids) for item in value]
    elif isinstance(value, dict):
        return {k: transform_value(v, ontology_ids) for k, v in value.items()}
    else:
        return value


def process_file(path: Path, ontology_ids: set):
    data = json.loads(path.read_text(encoding="utf-8"))
    transformed = transform_value(data, ontology_ids)
    path.write_text(json.dumps(transformed, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Updated {path}")


def main():
    ontology_ids = load_ontology_ids()
    print(f"Loaded {len(ontology_ids)} ontology ids")

    for p in GAME_FILES:
        process_file(p, ontology_ids)

    # Validate JSON
    for p in GAME_FILES:
        json.loads(p.read_text(encoding="utf-8"))
        print(f"JSON OK: {p}")


if __name__ == "__main__":
    main()
