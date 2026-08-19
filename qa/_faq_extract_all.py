# -*- coding: utf-8 -*-
"""把所有游戏的 faq.md 中文问题统一抽取到一个 JSONL 分析文件。
输出：scripts/_faq_all.jsonl，每行 {game, category, question}。
"""
import io
import json
import re
import sys
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

BOARD = Path(__file__).resolve().parent.parent
OUT = BOARD / "scripts" / "_faq_all.jsonl"

# 问题行：**中文**：... / **中文翻译**：... / **中文**: ...
Q_RE = re.compile(r"^\s*\*\*中文(?:翻译)?\*\*\s*[:：]\s*(.+?)\s*$")
CAT_RE = re.compile(r"^###\s+(.+)$")
# agricola 长尾简式：`43. 问题？（答案）`
TAIL_RE = re.compile(r"^(\d+)\. (.*?)（(.*)）\s*$")


def extract(path: Path):
    out = []
    cat = ""
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = CAT_RE.match(line)
        if m:
            cat = m.group(1).strip()
            continue
        m = Q_RE.match(line)
        if m:
            q = m.group(1).strip()
            if q and not q.startswith("原文"):
                out.append((cat, q))
        else:
            # agricola 的 TAIL 式（43-55 题）
            if "agricola" in str(path):
                m = TAIL_RE.match(line)
                if m:
                    out.append((cat, m.group(2).strip()))
    return out


rows = []
for faq in sorted((BOARD / "doc").glob("*/faq.md")):
    game = faq.parent.name
    for cat, q in extract(faq):
        rows.append({"game": game, "category": cat, "question": q})
    print(f"{game}: {sum(1 for r in rows if r['game'] == game)} 题")

with OUT.open("w", encoding="utf-8") as f:
    for r in rows:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
print(f"\n共 {len(rows)} 题 → {OUT}")
