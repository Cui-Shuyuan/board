# -*- coding: utf-8 -*-
"""Brass: Birmingham FAQ 批量测试：解析 doc/faq/brass-birmingham/faq.md 的结构化条目
（1-7 节，31 题）+ 手工整理的第 8 节一行式条目（21 题），逐条 POST /api/chat
（game_id=brass-birmingham），边跑边落盘到 scripts/_qa_brass_results.jsonl。
"""
import io
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

# Windows GBK 控制台 → utf-8，避免打印中文报 UnicodeEncodeError
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

API = "http://localhost:5000/api/chat"
FAQ = Path(r"D:\workspace\board\doc\brass-birmingham\faq.md")
OUT = Path(r"D:\workspace\board\scripts\_qa_brass_results.jsonl")

CAT_RE = re.compile(r"^### (\d+)\. (.+)$")
ORIG_RE = re.compile(r"^- \*\*原文\*\*：(.*)$")
ZH_RE = re.compile(r"^\s*\*\*中文\*\*：(.*)$")
ANS_RE = re.compile(r"^\s*\*\*答案摘要\*\*：(.*)$")

# 第 8 节「其他长尾 / 常见困惑（精选）」一行式 Q→A，手工整理为中文提问
TAIL_ITEMS = [
    ("资源立方体（煤、铁、啤酒）的数量有限吗？不够用时怎么办？", "无限，用替代物。"),
    ("我可以把链接穿过有商人板块的地点（如 Gloucester）继续延伸吗？", "可以，商人地点可作为中间点继续建 link。"),
    ("铁路时代的双链接行动能用商人啤酒吗？每条铁路需要煤吗？", "啤酒必须来自酒厂（不能是商人啤酒）；每条铁路放置后需要 1 块煤。"),
    ("煤矿或铁厂建好后，之后还能再把上面的立方体卖到市场吗？", "不能，只有建造动作瞬间可以。"),
    ("我在板上没有任何建筑或链接时，可以建乡村酒厂吗？", "可以，用产业卡或万能产业卡（无存在时的特殊规则）。"),
    ("商人啤酒的加成什么时候获得？", "只有 Sell 动作使用该商人啤酒时。"),
    ("发展（Develop）动作需要什么资源？", "铁（可从任意铁厂/网络内/市场取）。"),
    ("侦察（Scout）会给几张万能卡？", "1 张万能产业卡 + 1 张万能地点卡。"),
    ("回合结束时商人啤酒会补充吗？回合顺序怎么确定？", "补满商人啤酒；按花钱最少者在前（并列保持相对次序）。"),
    ("2/3 人局移除的地点卡对应的地点还能建造吗？", "能，仍可通过网络或万能卡建造。"),
    ("1 级陶器在铁路时代还能建吗？", "是唯一在铁路时代可建的 1 级板块（前提是未被移除）。"),
    ("跳过（pass）动作也要弃一张卡吗？", "要。"),
    ("回合顺序并列时怎么排？", "保持相对次序。"),
    ("终局时负收入还要付钱吗？", "若适用通常仍要付；终局计分优先看 VP。"),
    ("侦察拿到的万能卡当回合能用吗？", "通常下回合（侦察后补手牌）。"),
    ("覆盖建造（Overbuild）需要满足什么条件？", "与正常建造相同的卡牌要求和连接规则。"),
    ("建铁路需要的煤按什么顺序取？", "先最近的已连接来源，再市场。"),
    ("售卖（Sell）时啤酒有哪些来源？", "自己的酒厂（任意）、对手酒厂（需连接）、商人啤酒。"),
    ("板块上的蓝色/白色等级标记（Canal-only）意味着什么？", "只能在运河时代建造，不是时代结束时的移除标准。"),
    ("初始回合顺序怎么确定？", "角色板块洗混随机。"),
    ("玩家花的钱放在哪里用于回合顺序计算？", "角色板块上。"),
]


def parse_faq(text: str):
    items = []
    cat = ""
    i = 0
    lines = text.splitlines()
    while i < len(lines):
        s = lines[i].strip()
        m = CAT_RE.match(s)
        if m:
            cat = f"{m.group(1)}. {m.group(2)}"
            i += 1
            continue
        m = ORIG_RE.match(s)
        if m:
            orig = m.group(1).strip()
            zh, ans = "", ""
            j = i + 1
            while j < len(lines):
                s2 = lines[j].strip()
                if ORIG_RE.match(s2) or CAT_RE.match(s2):
                    break
                mz = ZH_RE.match(s2)
                ma = ANS_RE.match(s2)
                if mz:
                    zh = mz.group(1).strip()
                elif ma:
                    ans = ma.group(1).strip()
                j += 1
            items.append({"category": cat, "original": orig, "zh": zh, "faq_answer": ans})
            i = j
            continue
        i += 1
    return items


def ask(game_id, q, timeout=180):
    body = json.dumps({
        "game_id": game_id,
        "messages": [{"role": "user", "content": q}],
    }, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        dt = time.time() - t0
        return data.get("reply", ""), dt, None
    except Exception as e:
        return "", time.time() - t0, str(e)


def main():
    text = FAQ.read_text(encoding="utf-8")
    items = parse_faq(text)
    for q, a in TAIL_ITEMS:
        items.append({"category": "8. 其他长尾 / 常见困惑（精选）",
                      "original": "", "zh": q, "faq_answer": a})
    print(f"解析到 {len(items)} 道题")
    with OUT.open("w", encoding="utf-8") as f:
        for i, it in enumerate(items, 1):
            reply, dt, err = ask("brass-birmingham", it["zh"])
            rec = {
                "num": i,
                "category": it["category"],
                "question": it["zh"],
                "faq_answer": it["faq_answer"],
                "reply": reply,
                "elapsed": round(dt, 1),
                "error": err,
            }
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            f.flush()
            status = "ERR" if err else f"{dt:.0f}s"
            print(f"[{i:>2}] {status} {it['zh'][:38]}", flush=True)
    print("done")


if __name__ == "__main__":
    main()
