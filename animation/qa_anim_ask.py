#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把**手写**的合法性问句问规则引擎，并落日志。

问题默认放在 `content/games/{game}/tutorial/anim/_qa/questions.json` 里手写
（写新动画时连脚本一起写）；也可以用 `--in` 指定 track 文件（`*.anim.json`）
或其它 questions.json。本脚本只负责"问 + 记"。

    python3 animation/qa_anim_ask.py --game splendor
    python3 animation/qa_anim_ask.py --game splendor --track full --only cue1,cue2
    python3 animation/qa_anim_ask.py --in content/games/splendor/tutorial/anim/v2/full.anim.json --list
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

if (sys.stdout.encoding or "").lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
GAMES_ROOT = ROOT / "content" / "games"


def _game_from_path(path: Path) -> str | None:
    """Infer ``{game}`` from a path below content/games/."""
    candidates = [path] if path.is_absolute() else [Path.cwd() / path, ROOT / path]
    games_root = GAMES_ROOT.resolve()
    for candidate in candidates:
        try:
            rel = candidate.resolve().relative_to(games_root)
        except (OSError, ValueError):
            continue
        if rel.parts:
            return rel.parts[0]
    return None


def _games_with_qa() -> list[str]:
    games = set()
    for path in GAMES_ROOT.glob("*/tutorial/anim/_qa/questions.json"):
        try:
            games.add(path.relative_to(GAMES_ROOT).parts[0])
        except (OSError, ValueError, IndexError):
            continue
    return sorted(games)


def resolve_game(game_arg: str | None, input_path: Path | None) -> str:
    """Use --game when present, otherwise infer from --in or the unique game QA dir."""
    inferred = _game_from_path(input_path) if input_path is not None else None
    if game_arg:
        if inferred and inferred != game_arg:
            raise SystemExit(
                f"--game={game_arg} 与 --in 路径推导出的游戏 {inferred} 不一致")
        return game_arg
    if inferred:
        return inferred
    candidates = _games_with_qa()
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        raise SystemExit("找不到任何游戏 QA 目录；请用 --game 指定游戏")
    raise SystemExit(
        f"检测到多个候选游戏 {', '.join(candidates)}；请用 --game 或 --in 指定")


def qa_dir_for(game: str) -> Path:
    return GAMES_ROOT / game / "tutorial" / "anim" / "_qa"


def qa_input_for(game: str, track: str | None, input_arg: str | None) -> Path:
    if input_arg:
        return Path(input_arg)
    qa_dir = qa_dir_for(game)
    primary = qa_dir / "questions.json"
    if primary.exists():
        return primary
    if track:
        per_track = qa_dir / f"questions.{track}.json"
        if per_track.exists():
            return per_track
    return primary


def load_qa_profile(game: str) -> dict:
    """Load game/profile info used in reports (not hardcoded in this tool)."""
    path = GAMES_ROOT / game / "tutorial" / "animation" / "qa-profile.json"
    if not path.exists():
        return {}
    doc = json.loads(path.read_text(encoding="utf-8"))
    return doc if isinstance(doc, dict) else {}


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


def _verdict_pattern(expected: str | None = None) -> str:
    """Use the hand-written exact-vocabulary family when it is known."""
    if expected in ("允许", "不允许"):
        return r"(不允许|允许)"
    if expected in ("合法", "有问题"):
        return r"(不合法|合法|有问题)"
    return r"(不合法|合法|有问题|不允许|允许)"


def verdict_of(reply, expected: str | None = None):
    # Take the *last standalone* verdict token.  Some LLM answers start with a
    # wrong "不允许/有问题" and then self-correct to "允许/合法" in the same
    # reply; the final sentence is the operative answer.  Standalone means the
    # token is followed by punctuation/end, which prevents explanatory phrases
    # such as "被玩家B合法拿走" from overriding an opening "有问题。".
    # When the hand-written ask declares its expected vocabulary, only parse
    # that family ("允许/不允许" vs "合法/有问题") so a later explanatory
    # "动作合法" does not override the opening "允许。".
    # Normalize negated problem phrases first: "没有问题" contains "有问题"
    # but is a positive verdict.
    reply = reply.replace("没有问题", "合法").replace("没有不合法", "合法")
    family = _verdict_pattern(expected)
    standalone = rf"{family}(?=$|[\s。！？，、；：,.!?…）】」』\"'])"
    matches = list(re.finditer(standalone, reply))
    if not matches:
        matches = list(re.finditer(family, reply))
    if not matches and expected is not None:
        matches = list(re.finditer(r"不合法|合法|有问题|不允许|允许", reply))
    return matches[-1].group(0) if matches else "?"


def load_asks(path: Path):
    """Load either a questions.json spec or cue.qa fields from a track file.

    Questions stay hand-written.  They can live next to the cue as:
        cue = { "id": ..., "qa": ["question 1", "question 2"] }
    and this loader simply extracts them for the API sender.
    """
    spec = json.loads(path.read_text(encoding="utf-8"))
    if isinstance(spec, dict) and "cues" in spec and "asks" not in spec:
        asks = []
        for cue in spec.get("cues") or []:
            cid = cue.get("id")
            raw_qa = cue.get("qa")
            if raw_qa is None:
                continue
            items = raw_qa if isinstance(raw_qa, list) else [raw_qa]
            for item in items:
                if isinstance(item, str) and item.strip():
                    asks.append({"cue": cid, "q": item.strip()})
                elif isinstance(item, dict) and str(item.get("q") or "").strip():
                    ask = {"cue": cid, "q": str(item["q"]).strip()}
                    if item.get("expect") is not None:
                        ask["expect"] = str(item["expect"])
                    asks.append(ask)
        return {"source": str(path), "asks": asks}
    return spec


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None, help="游戏 id；未指定时从 --in 路径推导")
    ap.add_argument("--track", default="full", help="轨道 id；用于 questions.<track>.json 回退")
    ap.add_argument("--in", dest="inp", default=None, help="questions.json 或 *.anim.json")
    ap.add_argument("--only", default="")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--tag", default="", help="日志后缀，便于对比不同版本（默认 latest）")
    ap.add_argument("--jobs", type=int, default=4, help="并发问数（默认 4）")
    ap.add_argument("--strict", action="store_true", help="有可疑答案时返回非零")
    a = ap.parse_args(argv)

    input_arg = Path(a.inp) if a.inp else None
    game = resolve_game(a.game, input_arg)
    inp = qa_input_for(game, a.track, a.inp)
    try:
        spec = load_asks(inp)
    except FileNotFoundError:
        print(f"[qa] missing questions file: {inp}", file=sys.stderr)
        return 2
    items = spec["asks"]
    only = [x.strip() for x in a.only.split(",") if x.strip()]
    if only:
        items = [x for x in items if x["cue"] in only]
    if a.list:
        for x in items:
            print(f"[{x['cue']}]\n  {x['q']}\n")
        return 0

    profile = load_qa_profile(game)
    game_id = str(profile.get("game_id") or game)
    demo_note = str(profile.get("demo_note") or "").strip()

    qa_dir = qa_dir_for(game)
    qa_dir.mkdir(parents=True, exist_ok=True)
    tagsuf = a.tag or "latest"
    jl = qa_dir / f"ask_log_{tagsuf}.jsonl"

    def ask_one(idx_item):
        i, it = idx_item
        body = json.dumps({"game_id": game_id,
                           "messages": [{"role": "user", "content": it["q"]}]},
                          ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(api_url(), data=body,
                                     headers={"Content-Type": "application/json"})
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=150) as r:
                reply = json.loads(r.read().decode("utf-8")).get("reply", "")
        except Exception as e:                           # noqa: BLE001
            reply = f"<失败：{e}>"
        row = dict(it, reply=reply, seconds=round(time.time() - t0, 1),
                   verdict=verdict_of(reply, it.get("expect")))
        return i, row

    rows_by_idx = {}
    jobs = max(1, int(a.jobs or 1))
    with ThreadPoolExecutor(max_workers=jobs) as pool:
        futs = [pool.submit(ask_one, (i, it)) for i, it in enumerate(items, 1)]
        done = 0
        for fut in as_completed(futs):
            i, row = fut.result()
            rows_by_idx[i] = row
            done += 1
            print(f"{done}/{len(items)} [{row['verdict']}] {row['cue']} ({row['seconds']}s)")
    rows = [rows_by_idx[i] for i in sorted(rows_by_idx)]
    with jl.open("w", encoding="utf-8") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

    def ok(row):
        expect = row.get("expect")
        if expect:
            return row["verdict"] == expect
        return row["verdict"] in ("允许", "合法")

    bad = [r for r in rows if not ok(r)]
    md = ["# 动画合法性问答（手写问题，规则引擎当裁判）", "",
          f"- 问题：`{Path(a.inp).name if a.inp else inp.name}`（**手写**，写动画时连脚本一起写；只用状态与动作，"
          f"不用教程自造词，规则自动发生的事就说成自动）",
          f"- 结果：共 {len(rows)} 问，通过 {len(rows)-len(bad)}，可疑 {len(bad)}"]
    if demo_note:
        md.append(f"- 演示局：{demo_note}")
    md.append("")
    if bad:
        md += ["## 需要人工看", ""]
        for r in bad:
            md += [f"### {r['cue']} — {r['verdict']}", "", "问：", "",
                   "> " + r["q"], "", "答：", "", "> " + r["reply"], ""]
    md += ["## 全部问答", ""]
    for r in rows:
        md += [f"### {r['cue']} — {r['verdict']}", "", "问：", "", "> " + r["q"], "",
               "答：", "", "> " + r["reply"], ""]
    (qa_dir / f"ask_log_{tagsuf}.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"\n通过 {len(rows)-len(bad)} / 可疑 {len(bad)}；日志 → {qa_dir}/ask_log_{tagsuf}.md")
    return 1 if (a.strict and bad) else 0


if __name__ == "__main__":
    sys.exit(main())
