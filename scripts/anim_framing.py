#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""取景 pass：**逐 cue 决定"哪些 zone 必须入镜"**，并把它写进动画数据（脚本层，可重生成）。

用户 2026-09-20 定的口径：
  · 写脚本时就说清"这一条要哪几个 zone 入镜"（`camera: "a,b"`）；
  · 机位由引擎的函数算（`camera_fill` = 这几个 zone 占画面中央的比例）；
  · 跨度超过整桌长/宽 50% → 引擎自动退回全局镜头（所以这里可以放心给，别怕切碎）；
  · 特写默认 fill = 0.72（比老的 0.8 近约 1.1 倍），需要更宽时显式给 0.8。

规则（机械，可复核；例外写在 KEEP_BOARD / EXTRA 里）：
  1. 这条 cue 的事件碰到的 zone（source/destination/zone 解析后）→ 一起入镜；
  2. 讲"玩家手上的东西/买下的牌"的 cue，把对应的玩家区一起框进来（EXTRA 表）；
  3. 什么都不碰（纯讲解）→ 保持全局（KEEP_BOARD 里也列了必须留全局的时刻）；
  4. 已经在用特写 token（supply/cards）的 cue：保留 token，只补 fill。

配套：把"入镜但契约没声明"的 zone 补进契约（见 --contracts，读采样状态取件数）。
    python3 scripts/anim_framing.py --write [--contracts]
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ANIM = ROOT / "games/splendor/tutorial/anim/full.json"
STAGE = ROOT / "games/splendor/tutorial/anim/_stage/splendor.table.json"
SAMPLE = ROOT / "games/splendor/tutorial/anim/full.exitstate.json"
FILL = 0.72
FILL_WIDE = 0.8
# 必须留全局镜头的时刻（讲"整桌/收尾/多人"这类，本来就该看全局）
KEEP_BOARD = {
    "bg.intro.001.1", "bg.intro.001.2", "bg.intro.002.1", "bg.intro.002.2",
    "bg.intro.002.3", "bg.intro.003.1", "bg.intro.003.2", "bg.intro.003.3",
    "setup.end.001.1", "setup.end.001.2", "setup.end.001.3", "setup.end.002",
    "action.turn.001", "action.turn.002", "action.summary.002.2",
    "setup.cards.002.2",            # 3 乘 4 供应堆：安静看全局
}
# 讲"玩家自己的东西"时，把这些区一起框进来
EXTRA = {
    "hold": ["player_holding"],
    "dev": ["player_development"],
    "res": ["player_reserved"],
    "nob": ["player_nobles"],
}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--contracts", action="store_true", help="把入镜但契约没声明的 zone 补进契约")
    a = ap.parse_args()
    doc = json.loads(ANIM.read_text(encoding="utf-8"))
    stage = json.loads(STAGE.read_text(encoding="utf-8"))
    ids = {z["id"] for z in stage["zones"]}
    zone_of_concept = {}
    for z in stage["zones"]:
        if z.get("concept"):
            zone_of_concept.setdefault(z["concept"], []).append(z["id"])

    def rid(ref):
        if not ref or not isinstance(ref, str):
            return None
        if ref in ids:
            return ref
        m = re.match(r"<([^|>]+)(\|(.+))?>$", ref)
        if not m:
            return None
        concept, parts = m.group(1), m.group(3)
        want = {}
        if parts:
            for kv in parts.split("|"):
                if "=" in kv:
                    k, v = kv.split("=", 1)
                    want[k.strip()] = v.strip().strip("<>")
        cands = zone_of_concept.get(concept, [])
        if not want and len(cands) == 1:
            return cands[0]
        for z in stage["zones"]:
            if z.get("concept") != concept:
                continue
            got = {p["key"]: str(p["value"]).strip("<>") for p in (z.get("parts") or [])}
            if all(got.get(k) == v for k, v in want.items()):
                return z["id"]
        return None

    changed = kept = 0
    for cue in doc["cues"]:
        evs = cue.get("events") or []
        cid = cue["cue"]
        cams = [e for e in evs if e.get("camera")]
        if not cams:
            continue
        first = cams[0]
        if first["camera"] not in ("board",) and "," in str(first["camera"]):
            continue                                   # 已经是 zone 列表的（上一批手写的）不动
        if first["camera"] == "board":
            if cid in KEEP_BOARD:
                kept += 1
                continue
            touched = []
            for e in evs:
                # **只被 destroy 的 zone 不进取景**：那条 cue 结束就空了，镜头不该往那儿凑
                # （否则"切镜头在第一帧、清场在 2.20s"会被取景链检查当场抓住 ✗）
                if e.get("action") == "destroy":
                    continue
                for key, many in (("source", True), ("destination", False), ("zone", False)):
                    v = e.get(key)
                    if v is None:
                        continue
                    for ref in (v if many else [v]):
                        z = rid(ref)
                        if z and z not in touched and z not in ("offstage",):
                            touched.append(z)
            if not touched:
                kept += 1
                continue
            # 讲玩家自己东西的 cue → 把玩家区带进来
            txt = (cue.get("story", "") + cue.get("note", ""))
            for tag, zs in EXTRA.items():
                if tag == "hold" and ("手上" in txt or "持有" in txt):
                    touched += [z for z in zs if z not in touched]
                if tag == "dev" and ("发展区" in txt or "已购买" in txt):
                    touched += [z for z in zs if z not in touched]
                if tag == "res" and "保留" in txt:
                    touched += [z for z in zs if z not in touched]
                if tag == "nob" and "贵族" in txt:
                    touched += [z for z in zs if z not in touched]
            first["camera"] = ",".join(touched)
            first["camera_fill"] = FILL if len(touched) <= 2 else FILL_WIDE
            changed += 1
        elif first["camera"] in ("supply", "cards"):
            first.setdefault("camera_fill", FILL_WIDE)
            changed += 1
    print(f"取景：改 {changed} 条，留全局 {kept} 条")

    if a.contracts:
        # 把"入镜了但契约没声明"的 zone 补进契约（件数取采样里的真实值）
        sample = json.loads(SAMPLE.read_text(encoding="utf-8"))["cues"]
        stage_zones = {z["id"]: z for z in stage["zones"]}
        added = 0
        for cue in doc["cues"]:
            cid = cue["cue"]
            st = sample.get(cid, {}).get("zones") or {}
            for side in ("enter", "exit"):
                part = cue.get(side) or {}
                zs = part.get("zones") or {}
                frame = set()
                for e in (cue.get("events") or []):
                    cam = e.get("camera")
                    if cam and cam not in ("board", "supply", "cards"):
                        frame |= {x.strip() for x in str(cam).split(",") if x.strip()}
                for zid in sorted(frame):
                    if zid in zs or zid not in st:
                        continue
                    rec = st[zid]
                    entry = {"count": rec.get("count", 0)}
                    if rec.get("kinds"):
                        entry["kinds"] = {k: {"count": v.get("count", 0)} for k, v in rec["kinds"].items()}
                    zs[zid] = entry
                    added += 1
                part["zones"] = zs
        print(f"契约：补声明 {added} 处入镜 zone")

    if a.write:
        ANIM.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("已写回", ANIM)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
