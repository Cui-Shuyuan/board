#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把**手写**的合法性问句问规则引擎，并落日志（不再有生成器）。

问题在 `games/splendor/tutorial/anim/_qa/questions.json` 里手写（写新动画时连脚本一起写）：
只看这一条的状态与动作，用玩家口吻问一句话。本脚本只负责"问 + 记"。

    python3 scripts/qa_anim_ask.py                # 全问
    python3 scripts/qa_anim_ask.py --only cue1,cue2
    python3 scripts/qa_anim_ask.py --list          # 只看问题
"""
from __future__ import annotations

import argparse
import io
import json
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

if (sys.stdout.encoding or "").lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
QA = ROOT / "games/splendor/tutorial/anim/_qa"


def api_url():
    if os.environ.get("BOARDAI_API"):
        return os.environ["BOARDAI_API"]
    try:
        host = subprocess.run(["ip", "route", "show", "default"], capture_output=True,
                              text=True, timeout=3).stdout.split()[2]
        if host:
            return f"http://{host}:5000/api/chat"
    except Exception:                                    # noqa: BLE001
        pass
    return "http://localhost:5000/api/chat"


def verdict_of(reply):
    i_yes, i_no = reply.find("允许"), reply.find("不允许")
    j_ok, j_bad = reply.find("合法"), reply.find("有问题")
    if i_no >= 0 and (i_yes < 0 or i_no < i_yes):
        return "不允许"
    if j_bad >= 0 and (j_ok < 0 or j_bad < j_ok):
        return "有问题"
    if i_yes >= 0:
        return "允许"
    if j_ok >= 0:
        return "合法"
    return "?"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=str(QA / "questions.json"))
    ap.add_argument("--only", default="")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--tag", default="", help="日志后缀，便于对比不同版本（默认 latest）")
    a = ap.parse_args()
    spec = json.loads(Path(a.inp).read_text(encoding="utf-8"))
    items = spec["asks"]
    only = [x.strip() for x in a.only.split(",") if x.strip()]
    if only:
        items = [x for x in items if x["cue"] in only]
    if a.list:
        for x in items:
            print(f"[{x['cue']}]\n  {x['q']}\n")
        return 0

    QA.mkdir(parents=True, exist_ok=True)
    tagsuf = a.tag or "latest"
    jl = QA / f"ask_log_{tagsuf}.jsonl"
    rows = []
    with jl.open("w", encoding="utf-8") as f:
        for i, it in enumerate(items, 1):
            body = json.dumps({"game_id": "splendor",
                               "messages": [{"role": "user", "content": it["q"]}]},
                              ensure_ascii=False).encode("utf-8")
            req = urllib.request.Request(api_url(), data=body,
                                         headers={"Content-Type": "application/json"})
            t0 = time.time()
            try:
                with urllib.request.urlopen(req, timeout=150) as r:
                    reply = json.loads(r.read().decode("utf-8")).get("reply", "")
            except Exception as e:                       # noqa: BLE001
                reply = f"<失败：{e}>"
            row = dict(it, reply=reply, seconds=round(time.time() - t0, 1),
                       verdict=verdict_of(reply))
            rows.append(row)
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
            f.flush()
            print(f"{i}/{len(items)} [{row['verdict']}] {it['cue']} ({row['seconds']}s)")
    bad = [r for r in rows if r["verdict"] not in ("允许", "合法")]
    md = ["# 动画合法性问答（手写问题，规则引擎当裁判）", "",
          f"- 问题：`{Path(a.inp).name}`（**手写**，写动画时连脚本一起写；只用状态与动作，"
          f"不用教程自造词，规则自动发生的事就说成自动）",
          f"- 结果：共 {len(rows)} 问，通过 {len(rows)-len(bad)}，可疑 {len(bad)}",
          f"- 演示局：两人局（每色在场 4 颗、黄金 5、手上限 10、保留上限 3）", ""]
    if bad:
        md += ["## 需要人工看", ""]
        for r in bad:
            md += [f"### {r['cue']} — {r['verdict']}", "", "问：", "",
                   "> " + r["q"], "", "答：", "", "> " + r["reply"], ""]
    md += ["## 全部问答", ""]
    for r in rows:
        md += [f"### {r['cue']} — {r['verdict']}", "", "问：", "", "> " + r["q"], "",
               "答：", "", "> " + r["reply"], ""]
    (QA / f"ask_log_{tagsuf}.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"\n通过 {len(rows)-len(bad)} / 可疑 {len(bad)}；日志 → {QA}/ask_log_{tagsuf}.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
