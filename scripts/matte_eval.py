#!/usr/bin/env python3
"""抠图验收：任何来源的 alpha 图都按**同一套判据**过一遍。

为什么要有它：抠图可以来自几何拟合、开源模型（BiRefNet/RMBG…）、在线 API、手工修图 ——
**来源可以换，验收不能换**。判据全部对着"实物是什么"来，而不是"看起来像不像"：

  1. **单连通**：不透明像素里最大连通域的占比。灰尘、扫描件边框、模型漏出来的碎块都会让它掉下来。
  2. **边缘宽度**：半透明像素数 / 周长。≈1–2px 是"1px 抗锯齿"；明显更大说明糊边，
     或者是模型把**阴影**当成半透明主体带进来了（阴影正是我们最想去掉的东西）。
  3. **长宽比**：matte 包围盒的 w/h 对上 `games/{game}/components.json` 里的**实物尺寸**比例
     （宝石 1.0、贵族 1.0、发展卡 63/88≈0.716）。这一条能抓出"切歪了/切到一半"。
  4. **实心度**：matte 面积 / 包围盒面积，对上该形状的理论值（圆 π/4≈0.785、圆角矩形≈0.95+）。
     吃掉一块或多留一块都会偏离。
  5. **软边质量占比**：0<α<0.5 的像素占不透明面积的比例。阴影/反光残留会把这一项顶高。
  6. **尺寸一致性**（可选，给同一类实物）：用 `components.json` 的毫米尺寸反推像素/毫米，
     同一类件的比例应当一致 —— 扫描时大小不一，是**这里**把它归一化的。

用法：
    python3 scripts/matte_eval.py --dir /tmp/matte_out --game splendor
    python3 scripts/matte_eval.py --dir out --preview     # 额外写棋盘格预览图，便于肉眼验收

输出：一行一个文件的测量值 + PASS/WARN/FAIL；不做修改、不写回素材。
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 形状先验（来自实物：见 games/{game}/components.json 的 mm 尺寸）
SHAPE_PRIOR = {
    "gem": {"ratio": 1.0, "solidity": 0.785, "hint": "圆形 token"},
    "gold": {"ratio": 1.0, "solidity": 0.785, "hint": "圆形 token"},
    "noble": {"ratio": 1.0, "solidity": 0.98, "hint": "方形板块"},
    "development_card": {"ratio": 63 / 88, "solidity": 0.97, "hint": "矩形卡牌"},
}


def components_mm(game: str) -> dict:
    p = ROOT / "games" / game / "components.json"
    if not p.exists():
        return {}
    doc = json.loads(p.read_text(encoding="utf-8"))
    out = {}
    for c in doc.get("components", []):
        if "w" in c and "h" in c:
            out[c["id"]] = (c["w"], c["h"])
        elif "diameter" in c:
            out[c["id"]] = (c["diameter"], c["diameter"])
    return out


def classify(name: str, mm: dict) -> str | None:
    """从文件名猜它是什么件（本项目的命名约定：宝石/黄金/贵族_/发展卡）。"""
    if "宝石" in name or "黄金" in name:
        return "gem" if "黄金" not in name else "gold"
    if "贵族" in name:
        return "noble"
    if "发展卡" in name:
        return "development_card"
    return None


def largest_component_ratio(mask: list[bool], w: int, h: int) -> float:
    """最大连通域 / 全部不透明像素。"""
    total = sum(mask)
    if total == 0:
        return 0.0
    seen = [False] * (w * h)
    best = 0
    for start in range(w * h):
        if not mask[start] or seen[start]:
            continue
        size = 0
        q = deque([start])
        seen[start] = True
        while q:
            i = q.popleft()
            size += 1
            x, y = i % w, i // w
            if x > 0 and mask[i - 1] and not seen[i - 1]:
                seen[i - 1] = True; q.append(i - 1)
            if x < w - 1 and mask[i + 1] and not seen[i + 1]:
                seen[i + 1] = True; q.append(i + 1)
            if y > 0 and mask[i - w] and not seen[i - w]:
                seen[i - w] = True; q.append(i - w)
            if y < h - 1 and mask[i + w] and not seen[i + w]:
                seen[i + w] = True; q.append(i + w)
        best = max(best, size)
    return best / total


def evaluate(path: Path, game: str, mm: dict) -> dict:
    from PIL import Image

    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = list(im.getdata())
    alpha = [p[3] / 255.0 for p in px]

    opaque = [a >= 0.5 for a in alpha]
    n_opaque = sum(opaque)
    if n_opaque < w * h * 0.001:
        # 退化的 matte（几乎全透明，或只剩几个像素）：直接说清，别让后面的比例数变成乱码
        return {"file": path.name, "verdict": "FAIL", "shape": classify(path.name, mm),
                "why": [f"几乎全是透明的（不透明像素只有 {n_opaque} 个）—— 抠图退化/空"],
                "size": f"{w}x{h}", "bbox": "-", "ratio": 0.0, "solid": 0.0, "solidity": 0.0,
                "edge_px": 0.0, "soft_ratio": 0.0, "corner_max_alpha": 0.0, "px_per_mm": None}

    # 包围盒
    minx, miny, maxx, maxy = w, h, -1, -1
    for i, o in enumerate(opaque):
        if not o:
            continue
        x, y = i % w, i // w
        minx, maxx = min(minx, x), max(maxx, x)
        miny, maxy = min(miny, y), max(maxy, y)
    bw, bh = maxx - minx + 1, maxy - miny + 1
    ratio = bw / bh

    solid = largest_component_ratio(opaque, w, h)
    soft = sum(1 for a in alpha if 0.0 < a < 0.5) / n_opaque
    edge = sum(1 for a in alpha if 0.02 < a < 0.98)
    perimeter = 2 * (bw + bh)
    edge_w = edge / max(1, perimeter)
    solidity = n_opaque / (bw * bh)

    # 四角必须全透明（token / 卡牌都一样：实物不该占满整张方图）
    corners = [alpha[0], alpha[w - 1], alpha[(h - 1) * w], alpha[-1]]
    corner_max = max(corners)

    shape = classify(path.name, mm)
    prior = SHAPE_PRIOR.get(shape) if shape else None

    why = []
    verdict = "PASS"
    if solid < 0.995:
        why.append(f"不透明像素不是一个整体（最大连通域 {solid:.3f}）—— 有碎块/边框残留")
        verdict = "FAIL"
    if corner_max > 0.01:
        why.append(f"四角不透明（max α={corner_max:.2f}）—— 背景没切干净")
        verdict = "FAIL"
    if edge_w > 3.0:
        why.append(f"边缘太宽（{edge_w:.1f}px/周长的半透明带）—— 糊边，或把阴影带进来了")
        verdict = "FAIL" if edge_w > 5 else "WARN"
    if soft > 0.15:
        why.append(f"软边像素占比 {soft:.2f} 偏高（阴影/反光残留）")
        if verdict == "PASS":
            verdict = "WARN"
    if prior:
        if abs(ratio - prior["ratio"]) > 0.06:
            why.append(f"长宽比 {ratio:.3f} 与实物 {prior['ratio']:.3f}（{prior['hint']}）不符 —— 切歪/切缺")
            if verdict == "PASS":
                verdict = "WARN"
        if abs(solidity - prior["solidity"]) > 0.08:
            why.append(f"实心度 {solidity:.3f} 与 {prior['hint']} 的 {prior['solidity']:.3f} 不符")
            if verdict == "PASS":
                verdict = "WARN"

    mm_size = mm.get(shape) if shape else None
    px_per_mm = (bw / mm_size[0]) if mm_size and mm_size[0] else None

    return {
        "file": path.name,
        "verdict": verdict,
        "why": why,
        "size": f"{w}x{h}",
        "bbox": f"{bw}x{bh}",
        "ratio": round(ratio, 3),
        "solid": round(solid, 4),
        "solidity": round(solidity, 3),
        "edge_px": round(edge_w, 2),
        "soft_ratio": round(soft, 3),
        "corner_max_alpha": round(corner_max, 3),
        "px_per_mm": round(px_per_mm, 2) if px_per_mm else None,
        "shape": shape,
    }


def write_preview(path: Path, out_dir: Path, tile: int = 16) -> Path:
    """棋盘格预览：白边/糊边/阴影残留一眼可见（肉眼验收用）。"""
    from PIL import Image

    im = Image.open(path).convert("RGBA")
    w, h = im.size
    bg = Image.new("RGBA", (w, h))
    pxa = bg.load()
    for y in range(h):
        for x in range(w):
            pxa[x, y] = (60, 60, 64, 255) if ((x // tile) + (y // tile)) % 2 else (210, 210, 214, 255)
    out = Image.alpha_composite(bg, im).convert("RGB")
    dst = out_dir / (path.stem + ".preview.png")
    out.save(dst)
    return dst


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True, help="要验收的抠图目录（PNG，带 alpha）")
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--preview", action="store_true", help="额外写棋盘格预览图")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    args = ap.parse_args()

    d = Path(args.dir)
    files = sorted(p for p in d.glob("*.png") if not p.name.endswith(".preview.png"))
    if not files:
        print(f"没找到 PNG：{d}", file=sys.stderr)
        return 2

    mm = components_mm(args.game)
    results = []
    for f in files:
        r = evaluate(f, args.game, mm)
        results.append(r)
        if args.preview:
            r["preview"] = str(write_preview(f, d))

    if args.json:
        print(json.dumps(results, ensure_ascii=False, indent=2))
        return 0

    print(f"验收 {len(results)} 个抠图（{d}）")
    print("-" * 100)
    bad = 0
    for r in results:
        mark = {"PASS": "OK  ", "WARN": "WARN", "FAIL": "FAIL"}[r["verdict"]]
        if r["verdict"] == "FAIL":
            bad += 1
        extra = f"  px/mm≈{r['px_per_mm']}" if r.get("px_per_mm") else ""
        print(f"{mark} {r['file']:28} {r['size']:>10} bbox={r['bbox']:>10} 长宽比={r['ratio']:.3f} "
              f"连通={r['solid']:.3f} 实心={r['solidity']:.3f} 边宽={r['edge_px']:.2f}px "
              f"软边={r['soft_ratio']:.3f} 四角α={r['corner_max_alpha']:.2f}{extra}")
        for wlines in r["why"]:
            print(f"       - {wlines}")
    print("-" * 100)
    print(f"{len(results) - bad} 通过 / {bad} 失败")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
