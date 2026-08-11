#!/usr/bin/env python3
"""
Board AI 规则文件语法校验脚本。

按 .claude/memory/json-writing-conventions.md 的检查清单逐条校验，
防止新增/修改概念、流程时引入悬空引用或格式违规。

用法:
    D:/Python/Python312/python.exe scripts/validate_rules.py            # 校验全部游戏 + ontology
    D:/Python/Python312/python.exe scripts/validate_rules.py --game civolution
    D:/Python/Python312/python.exe scripts/validate_rules.py --game splendor --no-ontology

检查项（ERROR = 必须修，WARN = 需人工确认）:
  E01  JSON 语法
  E02  悬空引用: 文本中的 <concept_id> / <ontology::xxx> 无定义
  E03  extends/specifies/instance_of 引用无定义 或 非 <...> 格式
  E04  有 id 的节点缺 name (flow/game concepts 层), 或 name 缺 zh/en
  E05  type 不是完整引用 <ontology::multiple_choice_enum.XXX>
  E06  do_after 引用不存在的步骤 id
  E07  cost 写 null (应省略)
  E08  type 值含 " | null" (旧写法)
  E09  游戏层用 definition 键 (应 description)
  E10  _skip 缺 id 或缺 description
  W01  target 是对象 (约定纯字符串)
  W02  do_after 引用了 <概念> (应引用步骤 id)
  W03  condition 形态未知 (应为 字符串引用 | {zh,en} | {options,type})
  W04  constraints.required/optional 字段 id 无顶层声明
"""

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ONTOLOGY_DIR = ROOT / "ontology"
GAMES_DIR = ROOT / "games"

# ── 引用正则: <xxx> / <xxx.yyy> / <ontology::xxx> / <ontology::xxx.YYY> / <a>.<b>.<c> ──
REF_RE = re.compile(r"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?(?:\.[A-Za-z0-9_]+)*)>")

# multiple_choice_enum 合法枚举值
MCE_VALUES = {
    "EXECUTE_ALL", "CHOOSE_ONE", "CHOOSE_AT_LEAST_ONE", "CHOOSE_ANY", "MATCH",
}

# 允许出现在 "type" 值中的完整引用
TYPE_RE = re.compile(r"^<ontology::multiple_choice_enum\.([A-Z_]+)>$")


def find_line(text: str, needle: str) -> int | None:
    """粗定位: needle 首次出现的行号 (1-based), 找不到返回 None"""
    idx = text.find(needle)
    if idx < 0:
        return None
    return text.count("\n", 0, idx) + 1


def in_constraints(path: str) -> bool:
    """是否位于 concepts 的 constraints.required/optional 条目内"""
    return ".constraints.required[" in path or ".constraints.optional[" in path


class Validator:
    def __init__(self, game: str | None, include_ontology: bool):
        self.game = game
        self.include_ontology = include_ontology
        self.issues: list[tuple[str, str, str]] = []  # (level, location, message)
        # 定义集合 (两阶段: 先全量收集, 再检查)
        self.ontology_ids: set[str] = set()
        self.game_ids: set[str] = set()
        self.node_ids: set[str] = set()   # 所有有 id 的节点 (步骤命名空间, 全局可寻址)
        self.field_ids: set[str] = set()  # 所有 JSON key (字段名, description 中的 <field> 引用可能指向它)
        # description 中的纯文档占位符 (非概念非字段, 忽略)
        self.placeholder_refs = {"concept", "field", "subfield", "event_id", "zone_id",
                                 "part_id", "slot_id", "null", "a", "b", "x", "y"}
        self.enum_ids: set[str] = set()  # 枚举值 (如 upright/lying/face_up, <枚举值> 引用不报悬空)
        # W04/E11 继承链分析: 概念 id → 字段集 / 父概念引用 (setdefault 防 game 层重名覆盖)
        self.concept_fields: dict[str, set[str]] = {}
        self.concept_parent: dict[str, str] = {}
        self.definition_ids: set[str] = set()  # 概念定义型节点 (W05 孤立检查范围)
        self.referenced: set[str] = set()      # 被引用过的概念 (W05)

    # ── 工具 ──────────────────────────────────────────────
    def err(self, loc: str, msg: str): self.issues.append(("ERROR", loc, msg))
    def warn(self, loc: str, msg: str): self.issues.append(("WARN", loc, msg))

    @staticmethod
    def norm_field(k: str) -> str:
        """字段 key 归一化: 去 [] 后缀 + 去 namespace (<ontology::ownership> → <ownership>)"""
        k = k.strip("[]")
        if "::" in k:
            k = "<" + k.split("::")[-1]
        return k

    def is_defined(self, ref: str) -> bool:
        return ref in self.game_ids or ref in self.ontology_ids

    def check_ref(self, ref: str, loc: str):
        """检查一个引用是否可解析; 同时记录被引用概念 (W05)"""
        # <ontology::multiple_choice_enum.XXX> 特判
        if ref.startswith("ontology::multiple_choice_enum."):
            val = ref.split(".")[-1]
            if val not in MCE_VALUES:
                self.err(loc, f"E05 枚举值 {val} 不是 multiple_choice_enum 合法值 {sorted(MCE_VALUES)}")
            return
        if "::" in ref:
            head = ref.split("::", 1)[1].split(".")[0]  # <ontology::a.b> 取 a
            if head in self.ontology_ids:
                self.referenced.add(head)
            elif head not in self.placeholder_refs:
                self.err(loc, f"E02 悬空引用 <{ref}> — ontology 无此概念")
        else:
            head = ref.split(".")[0]  # 路径引用取第一段 (如 <phase_sequence>.<reset_end_space>)
            if head in ("this", "self", "_skip"):
                return
            # 概念优先: 既是概念又是字段名时按概念处理
            if self.is_defined(head):
                self.referenced.add(head)
                return
            # 字段名引用 (<parts> 指向 piece 的 parts 字段)、枚举值、文档占位符不算悬空
            if (head in self.placeholder_refs or head in self.field_ids
                    or f"<{head}>" in self.field_ids or head in self.enum_ids):
                return
            self.err(loc, f"E02 悬空引用 <{ref}> — 游戏层与 ontology 均无定义")

    # ── 阶段一: 全量收集定义 (不检查, 保证顺序无关) ──────
    def collect_ids(self, data: object, source: str, is_ontology: bool):
        def walk(obj, path, depth):
            if isinstance(obj, dict):
                if in_constraints(path):
                    # constraints 条目: 字段 ID 进 field_ids (description 可能引用 <字段>), 不收集为节点
                    cid = obj.get("id")
                    if isinstance(cid, str):
                        self.field_ids.add(cid.strip("[]"))
                    # 但枚举值仍要收集 (如 face: enum [face_up, face_down])
                    if isinstance(obj.get("enum"), list):
                        for e in obj["enum"]:
                            if isinstance(e, str):
                                self.enum_ids.add(e)
                    return
                oid = obj.get("id")
                if isinstance(oid, str) and oid:
                    self.node_ids.add(oid)
                    (self.ontology_ids if is_ontology else self.game_ids).add(oid)
                    # 概念字段与继承链: ontology 先收集, game 层重名不覆盖 (setdefault)
                    top = {self.norm_field(k) for k in obj.keys()
                           if k not in ("id", "name", "abstract", "description", "definition",
                                        "constraints", "extends", "specifies", "instance_of",
                                        "level", "meta")}
                    # 递归合并: key-as-type 值对象 + options 元素的字段也算实现
                    #   (如 {"<ontology::transfer>": {"source": ...}}、
                    #    options 内联的 {"<ontology::state_change>": {"subject": ..., "to": ...}})
                    def merge_impl(d: dict, out: set[str]):
                        for k, v in d.items():
                            if k.startswith("<") and isinstance(v, dict):
                                out.update(self.norm_field(sk) for sk in v.keys())
                                merge_impl(v, out)
                            elif k == "options" and isinstance(v, list):
                                for item in v:
                                    if isinstance(item, dict):
                                        out.update(self.norm_field(sk) for sk in item.keys())
                                        merge_impl(item, out)
                    merge_impl(obj, top)
                    for slot in ("required", "optional"):
                        for item in obj.get("constraints", {}).get(slot, []):
                            fid = item if isinstance(item, str) else item.get("id")
                            if isinstance(fid, str):
                                top.add(fid.strip("[]"))
                    self.concept_fields.setdefault(oid, top)
                    for rel in ("extends", "specifies", "instance_of"):
                        pv = obj.get(rel)
                        if isinstance(pv, str) and pv.startswith("<"):
                            self.concept_parent.setdefault(
                                oid, pv.strip("<>").split("::")[-1].split(".")[0])
                    # W05 定义型节点: ontology 全部 + 游戏顶层概念 (depth==2, 排除流程/enum/_skip)
                    #   concepts/instances 组数组元素 (objects[i] 等) + flow triggers 组元素
                    is_top_concept = (
                        (is_ontology and depth == 2 and "concepts.json" in source)
                        or (depth == 2 and "concepts.json" in source and ".procedures[" not in path)
                        or (depth == 2 and "instances.json" in source)
                        or (".triggers[" in path and depth == 2)
                    )
                    if (is_top_concept and oid != "_skip" and ".enum[" not in path
                            and ".parts[" not in path):
                        self.definition_ids.add(oid)
                    # parts 条目: value dict 的 id 是实例引用 (如 {"<ontology::effect>": {"id": "activity_01"}})
                    if ".parts[" in path and isinstance(oid, str):
                        self.referenced.add(oid)
                # 枚举值收集 (如 posture: enum [upright, lying])
                for v in obj.values():
                    if isinstance(v, dict) and isinstance(v.get("enum"), list):
                        for e in v["enum"]:
                            if isinstance(e, str):
                                self.enum_ids.add(e)
                for k, v in obj.items():
                    self.field_ids.add(k.strip("[]"))
                    walk(v, f"{path}.{k}", depth + 1)
            elif isinstance(obj, list):
                for i, v in enumerate(obj):
                    walk(v, f"{path}[{i}]", depth + 1)

        walk(data, "$", 0)

    # ── 阶段二: 逐文件检查 ────────────────────────────────
    def check_file(self, file_path: Path, source: str, is_ontology: bool):
        text = file_path.read_text(encoding="utf-8")
        try:
            data = json.loads(text)
        except json.JSONDecodeError as e:
            self.err(source, f"E01 JSON 语法错误: {e}")
            return

        def walk(obj, path):
            if isinstance(obj, dict):
                if path == "$.meta":
                    return  # meta 是文档区, 其中的 <...> 是占位符示例
                in_cons = in_constraints(path)

                if not in_cons:
                    oid = obj.get("id")
                    # parts 条目 (纯标签带 position) 与 _skip 哨兵不是完整节点, 豁免 E04
                    in_parts = ".parts[" in path
                    if isinstance(oid, str) and oid and oid != "_skip" and not in_parts:
                        # E04: 有 id 的节点必须有 name {zh, en} (definition 写法豁免)
                        name = obj.get("name")
                        if name is None and "definition" not in obj:
                            self.err(f"{source} › {path} › {oid}",
                                     "E04 缺 name 字段")
                        elif isinstance(name, dict):
                            for k in ("zh", "en"):
                                if not isinstance(name.get(k), str):
                                    self.err(f"{source} › {path} › {oid}",
                                             f"E04 name 缺 {k} 字段")
                    # E10: _skip 哨兵
                    if oid == "_skip" and "description" not in obj:
                        self.err(f"{source} › {path} › _skip",
                                 "E10 _skip 必须带 description")
                else:
                    # constraints 条目: 字段 ID 若是 <...> 引用格式则验证定义
                    cid = obj.get("id")
                    if isinstance(cid, str) and cid.startswith("<"):
                        for ref in REF_RE.findall(cid):
                            self.check_ref(ref, f"{source} › {path} › id")

                # E03: 层级关系引用
                for rel in ("extends", "specifies", "instance_of"):
                    val = obj.get(rel)
                    if isinstance(val, str):
                        if not (val.startswith("<") and val.endswith(">")):
                            self.err(f"{source} › {path} › {rel}",
                                     f"E03 {rel} 不是 <...> 引用格式: {val}")
                        else:
                            for ref in REF_RE.findall(val):
                                self.check_ref(ref, f"{source} › {path} › {rel}")
                # E05: 选择结构 (有 options) 的 type 必须是完整引用
                #      ontology 字段声明的 type (string/enum/<object>) 不检查
                tval = obj.get("type")
                if "options" in obj and isinstance(tval, str):
                    m = TYPE_RE.match(tval)
                    if not m:
                        self.err(f"{source} › {path} › type",
                                 f"E05 type 不是 <ontology::multiple_choice_enum.XXX> 完整引用: {tval}")
                # E06/W02: do_after 引用
                da = obj.get("do_after")
                if isinstance(da, str):
                    self.check_do_after(da, f"{source} › {path} › do_after", text)
                elif isinstance(da, dict):
                    for item in da.get("options", []):
                        if isinstance(item, str):
                            self.check_do_after(item, f"{source} › {path} › do_after", text)
                # E07: cost null (键存在且值为 null 才报; 键不存在 = 正常省略)
                for key in ("<ontology::cost>", "<ontology::instant_cost>",
                            "<ontology::continuous_cost>"):
                    if key in obj and obj[key] is None:
                        self.err(f"{source} › {path} › {key}",
                                 "E07 cost 写 null — 无需支付应省略字段")
                # E08: | null 后缀 (type 内)
                if isinstance(tval, str) and "| null" in tval:
                    self.err(f"{source} › {path} › type",
                             "E08 type 含 '| null' — 可空由 optional/default 表达")
                # E09: definition vs description (游戏层; instances.json 的实例定义用 definition 是惯例)
                if not is_ontology and "definition" in obj and not source.endswith("instances.json"):
                    self.err(f"{source} › {path} › definition",
                             "E09 游戏层应使用 description 键, 不用 definition")
                # W03: condition 形态 (description 包装 / 直接 zh-en / options+type 三种合法)
                for ckey in ("<ontology::condition>", "condition"):
                    cval = obj.get(ckey)
                    if isinstance(cval, dict):
                        keys = set(cval.keys())
                        if not (keys <= {"zh", "en", "description"} or {"options", "type"} <= keys):
                            self.warn(f"{source} › {path} › {ckey}",
                                      f"W03 condition 形态未知 (期望 {{zh,en}}, {{description}} 或 {{options,type}}): {sorted(keys)}")
                # W01: trigger/action 的 target 应是字符串;
                #      ontology 字段声明 ({"type": ...}) 不检查
                tgt = obj.get("target")
                if isinstance(tgt, dict) and "type" not in tgt:
                    self.warn(f"{source} › {path} › target",
                              "W01 target 是对象 — 约定纯字符串")
                for k, v in obj.items():
                    # key-as-type 键也是引用 (如 {"<event_card>": {...}})
                    for ref in REF_RE.findall(k):
                        self.check_ref(ref, f"{source} › {path} › {k}")
                    walk(v, f"{path}.{k}")
            elif isinstance(obj, list):
                for i, v in enumerate(obj):
                    walk(v, f"{path}[{i}]")
            elif isinstance(obj, str):
                # E02: 字符串中的概念引用
                for ref in REF_RE.findall(obj):
                    self.check_ref(ref, f"{source} › {path}")

        walk(data, "$")

    def check_do_after(self, ref: str, loc: str, text: str):
        if ref.startswith("<"):
            self.warn(loc, f"W02 do_after 引用了 <{ref}> — 应引用步骤 id 而非概念")
        elif ref not in self.node_ids:
            self.err(loc, f"E06 do_after 引用不存在的步骤 id: {ref}")

    # ── constraints 顶层字段校验 (ontology 概念, 含继承链) ─
    def check_constraints(self, data: list[dict], text: str):
        def all_fields(cid: str, memo: dict) -> set[str]:
            """本概念 + extends/specifies 链上所有祖先的顶层字段"""
            if cid in memo:
                return memo[cid]
            fields = set(self.concept_fields.get(cid, set()))
            parent = self.concept_parent.get(cid)
            if parent:
                fields |= all_fields(parent, memo)
            memo[cid] = fields
            return fields

        memo: dict = {}
        for c in data:
            cid = c.get("id")
            if not cid:
                continue
            for slot in ("required", "optional"):
                for item in c.get("constraints", {}).get(slot, []):
                    fid = item if isinstance(item, str) else item.get("id")
                    if (isinstance(fid, str) and not fid.startswith("<")
                            and fid not in all_fields(cid, memo)):
                        self.warn(f"ontology › {cid} › constraints.{slot}",
                                  f"W04 字段 {fid} 既不在本概念也不在任何父概念顶层声明")

    # ── E11: 父类 required 必须每条继承链都闭合 ───────────
    #      每条 extends/specifies/instance_of 链上至少一个节点实现该字段;
    #      实现节点覆盖其下所有后代链 (继承语义)
    # ── W05: 孤立概念 (有定义无引用, 非触发型) ────────────
    def check_e11_w05(self, ontology_concepts: list[dict]):
        def subtree_closed(cid: str, field: str) -> bool:
            """该节点子树是否闭合: 自身实现 field 或某后代实现"""
            if field in self.concept_fields.get(cid, set()):
                return True
            return any(subtree_closed(c, field)
                       for c, p in self.concept_parent.items() if p == cid)

        def uncovered_chains(parent: str, field: str) -> list[str]:
            """返回完全未闭合的直接子链 (子节点自身未实现且其子树也无实现)"""
            return [c for c, p in self.concept_parent.items()
                    if p == parent and not subtree_closed(c, field)]

        for c in ontology_concepts:
            cid = c.get("id")
            if not cid:
                continue
            if not any(p == cid for p in self.concept_parent.values()):
                continue  # 无子类不适用
            for item in c.get("constraints", {}).get("required", []):
                fid = item if isinstance(item, str) else item.get("id")
                if not isinstance(fid, str):
                    continue
                key = self.norm_field(fid)
                if key in ("id", "<field_id>"):
                    continue  # id 隐式拥有; <field_id> 是 Object 的占位符
                bad = uncovered_chains(cid, key)
                if bad:
                    self.err(f"ontology › {cid} › constraints.required",
                             f"E11 字段 {fid} 的继承链未闭合: {', '.join(bad)} (这些链上无节点实现该字段)")

        # W05: 触发型 = 沿父链可达 trigger (action/effect/activation 等由点燃机制引用)
        def is_trigger_type(cid: str) -> bool:
            cur, seen = cid, set()
            while cur and cur not in seen:
                if cur == "trigger":
                    return True
                seen.add(cur)
                cur = self.concept_parent.get(cur)
            return False

        for cid in sorted(self.definition_ids):
            if cid in self.referenced or is_trigger_type(cid):
                continue
            self.warn("", f"W05 孤立概念 <{cid}> — 有定义但无任何引用")

    # ── 总入口 ────────────────────────────────────────────
    def run(self) -> int:
        targets: list[tuple[Path, str, bool]] = []
        if self.include_ontology:
            for f in ("concepts.json", "flow.json"):
                p = ONTOLOGY_DIR / f
                if p.exists():
                    targets.append((p, f"ontology/{f}", True))
        if self.game:
            gd = GAMES_DIR / self.game
            if not gd.exists():
                print(f"错误: 游戏目录 {gd} 不存在")
                return 2
            for f in ("concepts.json", "flow.json", "instances.json"):
                p = gd / f
                if p.exists():
                    targets.append((p, f"games/{self.game}/{f}", False))
        else:
            for gd in sorted(GAMES_DIR.iterdir()):
                if gd.is_dir() and (gd / "concepts.json").exists():
                    for f in ("concepts.json", "flow.json", "instances.json"):
                        p = gd / f
                        if p.exists():
                            targets.append((p, f"games/{gd.name}/{f}", False))

        # 阶段一: 收集全部定义 (跨文件全局命名空间)
        for p, src, is_onto in targets:
            try:
                data = json.loads(p.read_text(encoding="utf-8"))
            except Exception:
                continue
            self.collect_ids(data, src, is_onto)

        # 阶段二: 逐文件检查
        for p, src, is_onto in targets:
            self.check_file(p, src, is_onto)

        # ontology constraints 顶层字段引用 + E11/W05
        if self.include_ontology:
            p = ONTOLOGY_DIR / "concepts.json"
            try:
                text = p.read_text(encoding="utf-8")
                data = json.loads(text)
                self.check_constraints(data.get("concepts", []), text)
                self.check_e11_w05(data.get("concepts", []))
            except Exception:
                pass
        return 0


def main():
    # Windows 控制台默认 GBK, 强制 UTF-8 输出 (终端不支持的字符降级为 ?)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="Board AI 规则文件语法校验")
    ap.add_argument("--game", default=None, help="只校验指定游戏 (目录名)")
    ap.add_argument("--no-ontology", action="store_true", help="不校验 ontology")
    args = ap.parse_args()

    v = Validator(args.game, not args.no_ontology)
    rc = v.run()

    if not v.issues:
        print("全部通过")
        return 0
    errors = [i for i in v.issues if i[0] == "ERROR"]
    warns = [i for i in v.issues if i[0] == "WARN"]
    for level, loc, msg in v.issues:
        print(f"[{level}] {loc}\n    {msg}")
    print(f"\n共 {len(errors)} 个错误, {len(warns)} 个警告")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
