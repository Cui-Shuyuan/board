#!/usr/bin/env python3
"""本体概念解析：把 `ontology/concepts.json` 和 `games/{game}/concepts.json` 合起来读。

动画侧要用它回答三类问题：
  1. 这个模板 / 区域实例化的是哪个概念？            —— stage 里的 `concept` 字段
  2. 这个概念**继承**到了什么？                      —— 沿 extends 链合并字段
     （`<card>.face`、`<piece>.parts`、`<resource>.limited_supply` …）
  3. 这个概念允许待在哪些区域？                      —— `<zone>.contains`

为什么要它：本体里的概念分两个文件、概念之间靠 `extends` 连成链，而 `extends` 的写法有
`<ontology::card>`（跨文件）和 `<piece>`（文件内）两种。手写一处映射就要手抄一次父类字段，
迟早漂移；让工具合一次，两边说的是同一件事。

用法：
    python3 scripts/concept_ref.py --game splendor --concept development_card_level_1
    python3 scripts/concept_ref.py --game splendor --concept <ontology::card>
    python3 scripts/concept_ref.py --game splendor --list
    python3 scripts/concept_ref.py --game splendor --json --concept gem
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ONTOLOGY_FILE = ROOT / "ontology" / "concepts.json"

REF_RE = re.compile(r"^<(?:(?P<ns>[a-z_]+)::)?(?P<id>[a-z_0-9]+)>$")


def _concepts_of(doc) -> list[dict]:
    """把一份概念文件里的**概念**捞出来。

    两个文件的形状不同，但都很规整：
      - ontology：`{"meta":…, "concepts": [ {id,…}, … ]}`
      - 游戏：`{"objects":[…], "actions":[…], "triggers":[…], "conditions":[…],
                "<player_holding>": {id,…}, …}`
    所以规则就两条：**顶层数组的值是概念**；**以 `<` 开头的键，值是概念**。

    千万别递归下去"看见有 id 和 description 就当概念"——`constraints.required/optional`
    里的字段槽位（`id`、`source`、`face`、`contains`…）长得一模一样，
    第一版就是这么做，结果 `face`、`level`、`<condition>[]` 全成了"概念"，
    于是校验器会把字段名当概念放行（假阴性）。宁可规则笨一点。
    """
    found: list[dict] = []
    for key, val in doc.items():
        if key == "meta":
            continue
        if isinstance(val, list):
            found += [x for x in val if isinstance(x, dict) and "id" in x]
        elif isinstance(val, dict) and key.startswith("<") and "id" in val:
            found.append(val)
    return found


class World:
    """本体 + 某个游戏的概念集合，已解好引用。"""

    def __init__(self, game: str):
        self.game = game
        self.concepts: dict[str, dict] = {}
        self.source: dict[str, str] = {}     # id → "ontology" | game 名
        self._load(ONTOLOGY_FILE, "ontology")
        gpath = ROOT / "games" / game / "concepts.json"
        if gpath.exists():
            self._load(gpath, game)
        else:
            print(f"（没有游戏概念文件 {gpath}）", file=sys.stderr)

    def _load(self, path: Path, source: str):
        doc = json.loads(path.read_text(encoding="utf-8"))
        for c in _concepts_of(doc):
            self.concepts[c["id"]] = c
            self.source[c["id"]] = source

    # ── 引用解析 ────────────────────────────────────────────────────────
    def resolve(self, ref) -> str | None:
        """`<ontology::card>` / `<card>` / `card` → 概念 id（找不到返回 None）。

        规则：`<ontology::x>` 只查本体；不带命名空间的引用先查本体再查游戏
        ——「游戏的 N 级发展卡 extends 本体 card」和「动画里说 <gem> 指的是游戏那个 gem」
        两种写法都要能落地。
        """
        if not ref or not isinstance(ref, str):
            return None
        ref = ref.strip()
        m = REF_RE.match(ref)
        if m:
            ns, cid = m.group("ns"), m.group("id")
            if ns == "ontology":
                return cid if self.source.get(cid) == "ontology" else None
            return cid if cid in self.concepts else None
        return ref if ref in self.concepts else None

    def parent_of(self, cid: str) -> str | None:
        """父概念：`extends`（结构扩展）或 `specifies`（只填父类槽位）。

        两个都要跟：按项目约定，`extends` = 子概念声明了父概念没有的新字段，
        `specifies` = 子概念只填父概念已有的槽位。**两者都是 IS-A**，都继承父类的字段词汇
        ——「一级发展卡 specifies 发展卡」一样要继承到 `<card>.face`。
        只跟 `extends` 会把一整批"参数化子类"当成孤儿（我第一版就犯了这个错）。
        """
        c = self.concepts.get(cid) or {}
        return self.resolve(c.get("extends")) or self.resolve(c.get("specifies"))

    def chain(self, ref) -> list[str]:
        """从根到该概念的继承链（含自身）。带环保护。"""
        cid = self.resolve(ref)
        out: list[str] = []
        seen: set[str] = set()
        while cid and cid in self.concepts and cid not in seen:
            seen.add(cid)
            out.append(cid)
            cid = self.parent_of(cid)
        out.reverse()
        return out

    def merged_fields(self, ref) -> dict[str, dict]:
        """沿继承链合并 constraints（子类覆盖父类同名槽位）。"""
        fields: dict[str, dict] = {}
        for cid in self.chain(ref):
            const = self.concepts[cid].get("constraints") or {}
            for grp in ("required", "optional"):
                for spec in const.get(grp) or []:
                    if not isinstance(spec, dict) or "id" not in spec:
                        continue
                    fields[spec["id"]] = {**spec, "from": cid, "group": grp}
        return fields

    def has_field(self, ref, field: str) -> bool:
        return field in self.merged_fields(ref)

    def name(self, ref) -> str:
        cid = self.resolve(ref)
        if not cid:
            return ref or ""
        n = self.concepts[cid].get("name")
        if isinstance(n, dict):
            return n.get("zh") or n.get("en") or cid
        return n or cid

    def describe(self, ref) -> str:
        cid = self.resolve(ref)
        if not cid:
            return f"{ref!r} —— 本体和游戏概念里都找不到"
        lines = [f"<{cid}>（{self.name(cid)}，来自 {self.source[cid]}）"]
        lines.append("  继承链: " + " → ".join(f"<{c}>" for c in self.chain(cid)))
        fields = self.merged_fields(cid)
        if fields:
            lines.append("  合并后的字段（★ = 这个概念自己声明的）:")
            for fid, spec in fields.items():
                mark = "★" if spec["from"] == cid else " "
                desc = (spec.get("description") or {})
                desc = desc.get("zh", "") if isinstance(desc, dict) else ""
                lines.append(f"    {mark} [{spec['group'][:3]}] {fid:22s} ← <{spec['from']}>  {desc[:60]}")
        return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--concept", help="概念引用，如 development_card_level_1 / <ontology::card>")
    ap.add_argument("--list", action="store_true", help="列出所有概念及其继承链")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    w = World(args.game)
    if args.list:
        rows = []
        for cid in sorted(w.concepts):
            rows.append({"id": cid, "source": w.source[cid],
                         "chain": w.chain(cid), "name": w.name(cid)})
        if args.json:
            print(json.dumps(rows, ensure_ascii=False, indent=2))
        else:
            for r in rows:
                chain = " → ".join(r["chain"])
                print(f"  {r['id']:26s} [{r['source']:8s}] {r['name']:8s} {chain}")
        print(f"\n共 {len(rows)} 个概念（本体 {sum(1 for r in rows if r['source']=='ontology')} + "
              f"{args.game} {sum(1 for r in rows if r['source']!= 'ontology')}）")
        return 0

    if not args.concept:
        ap.error("要么给 --concept，要么用 --list")
    if args.json:
        cid = w.resolve(args.concept)
        print(json.dumps({"id": cid, "chain": w.chain(cid),
                          "fields": w.merged_fields(cid)}, ensure_ascii=False, indent=2))
    else:
        print(w.describe(args.concept))
    return 0


if __name__ == "__main__":
    sys.exit(main())
