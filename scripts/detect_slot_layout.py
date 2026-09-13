#!/usr/bin/env python3
"""
从版图扫描/俯拍照片里自动检测矩形槽位，生成 slot_layouts.json。

不做「点一下」这种人工操作：程序找矩形，输出带编号的核验图，你只需要看
debug 图确认编号顺序对不对；不对就调 --rows/--cols/--min-area 重跑。

用法：
    python scripts/detect_slot_layout.py --game splendor --image board_scan.jpg
    python scripts/detect_slot_layout.py --game splendor --image board_scan.jpg --prefix market --rows 3 --cols 4

输出：
    games/{game}/slot_layouts.json    （自动生成，可再手工微调参数）
    games/{game}/slot_layouts.debug.jpg
"""

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent.parent

# ── 检测参数 ──────────────────────────────────────────
MAX_DIM = 1600
MIN_AREA_RATIO = 0.0002      # 槽位最小面积占图比例
MAX_AREA_RATIO = 0.15        # 槽位最大面积占图比例
RECT_RATIO = 0.75            # 轮廓面积 / 最小外接矩形面积，越大越像矩形
ROW_TOL = 0.6                # 行聚类容差（占中位高度比例）
COL_TOL = 0.6                # 列聚类容差（占中位宽度比例）
MERGE_EPS = 0.02             # 去重重叠中心距离（归一化）


def imread_safe(path):
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_safe(path, img):
    ext = Path(path).suffix
    ok, buf = cv2.imencode(ext, img)
    if ok:
        buf.tofile(str(path))
    return ok


def order_points(pts):
    pts = np.array(pts, dtype=np.float32)
    rect = np.zeros((4, 2), dtype=np.float32)
    s = pts.sum(axis=1)
    diff = np.diff(pts, axis=1)
    rect[0] = pts[np.argmin(s)]
    rect[2] = pts[np.argmax(s)]
    rect[1] = pts[np.argmin(diff)]
    rect[3] = pts[np.argmax(diff)]
    return rect


def detect_rectangles(gray):
    """返回 [(cx_norm, cy_norm, w_norm, h_norm, contour, bbox), ...]"""
    h, w = gray.shape
    # 温和模糊 + 自适应阈值，比 Canny 对印刷线框更稳
    blur = cv2.GaussianBlur(gray, (5, 5), 0)
    binary = cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 25, 10)
    # 闭运算把虚线/断裂边连上
    kernel = np.ones((3, 3), np.uint8)
    binary = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel, iterations=2)

    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    min_area = w * h * MIN_AREA_RATIO
    max_area = w * h * MAX_AREA_RATIO

    rects = []
    for cnt in contours:
        area = cv2.contourArea(cnt)
        if area < min_area or area > max_area:
            continue
        peri = cv2.arcLength(cnt, True)
        approx = cv2.approxPolyDP(cnt, 0.03 * peri, True)
        if len(approx) != 4:
            continue
        pts = order_points(approx.reshape(4, 2))
        (tl, tr, br, bl) = pts
        width = max(np.linalg.norm(tr - tl), np.linalg.norm(br - bl))
        height = max(np.linalg.norm(tl - bl), np.linalg.norm(tr - br))
        if width < 8 or height < 8:
            continue
        rect_area = width * height
        if rect_area <= 0:
            continue
        if area / rect_area < RECT_RATIO:
            continue
        cx = float((tl[0] + br[0]) / 2.0 / w)
        cy = float((tl[1] + br[1]) / 2.0 / h)
        rw = float(width / w)
        rh = float(height / h)
        rects.append((cx, cy, rw, rh, approx, (int(tl[0]), int(tl[1]), int(br[0]), int(br[1]))))
    return rects


def merge_rects(rects):
    """中心/尺寸相近的重复检测合并。"""
    if not rects:
        return []
    rects = sorted(rects, key=lambda r: (r[1], r[0]))
    merged = []
    used = [False] * len(rects)
    for i, r in enumerate(rects):
        if used[i]:
            continue
        group = [r]
        for j in range(i + 1, len(rects)):
            if used[j]:
                continue
            if abs(rects[j][0] - r[0]) < MERGE_EPS and abs(rects[j][1] - r[1]) < MERGE_EPS:
                group.append(rects[j])
                used[j] = True
        cx = float(np.mean([g[0] for g in group]))
        cy = float(np.mean([g[1] for g in group]))
        rw = float(np.mean([g[2] for g in group]))
        rh = float(np.mean([g[3] for g in group]))
        merged.append((cx, cy, rw, rh, group[0][4], group[0][5]))
        used[i] = True
    return sorted(merged, key=lambda r: (round(r[1], 2), r[0]))


def cluster_rows(rects):
    """按 y 中心聚类成行，返回按 y 排序的行列表。"""
    if not rects:
        return []
    heights = [r[3] for r in rects]
    med_h = float(np.median(heights)) if heights else 0.05
    ys = sorted([r[1] for r in rects])
    rows = []
    cur = [ys[0]]
    for y in ys[1:]:
        if y - cur[-1] <= med_h * ROW_TOL:
            cur.append(y)
        else:
            rows.append(float(np.mean(cur)))
            cur = [y]
    rows.append(float(np.mean(cur)))
    rows.sort()
    return rows


def assign_rows_cols(rects, rows):
    """按行中心把矩形分到行，并在每行内按 x 排序分列。返回 row->cols 结构。"""
    rects = sorted(rects, key=lambda r: (r[1], r[0]))
    row_buckets = {i: [] for i in range(len(rows))}
    for r in rects:
        y = r[1]
        best = min(range(len(rows)), key=lambda i: abs(rows[i] - y))
        row_buckets[best].append(r)
    grid = []
    for i in sorted(row_buckets):
        row = sorted(row_buckets[i], key=lambda r: r[0])
        if row:
            grid.append(row)
    return grid


def median_cols(grid):
    lens = [len(row) for row in grid]
    return int(np.median(lens)) if lens else 0


def detect_grid(rects, hint_rows, hint_cols):
    """返回 grid layout dict 或 None。"""
    rects = merge_rects(rects)
    if len(rects) < 2:
        return None

    rows = cluster_rows(rects)
    if hint_rows:
        # 用户给了行数：重新按分位数生成行中心
        ys = sorted([r[1] for r in rects])
        rows = [float(np.percentile(ys, (i + 0.5) * 100.0 / hint_rows)) for i in range(hint_rows)]

    grid = assign_rows_cols(rects, rows)
    n_rows = len([g for g in grid if g])
    n_cols = median_cols(grid)
    if hint_cols:
        n_cols = hint_cols
    if n_rows < 1 or n_cols < 1:
        return None

    # 取最整齐的 n_cols 个槽位（每行按 x 顺序保留前 n_cols 或均匀抽样）
    cells = []
    for row in grid:
        row = row[:n_cols]
        if len(row) == n_cols:
            cells.append(row)
    if not cells:
        return None
    n_rows = len(cells)

    # 原点 = 第一行第一列槽位中心
    origin_x = cells[0][0][0]
    origin_y = cells[0][0][1]
    # 列间距：所有相邻列中心差的中位数
    col_pitches = []
    for row in cells:
        for c in range(1, n_cols):
            col_pitches.append(row[c][0] - row[c - 1][0])
    step_x = float(np.median(col_pitches)) if col_pitches else 0.08
    row_pitches = []
    for r in range(1, n_rows):
        row_pitches.append(cells[r][0][1] - cells[r - 1][0][1])
    step_y = float(np.median(row_pitches)) if row_pitches else 0.08

    return {
        "type": "grid",
        "id_prefix": "slot",
        "origin": {"x": round(origin_x, 4), "y": round(origin_y, 4)},
        "step_x": round(step_x, 4),
        "step_y": round(step_y, 4),
        "rows": n_rows,
        "cols": n_cols,
        "start_index": 0,
        "order": "row_major",
        "label": {"zh": "自动检测槽位"},
    }


def draw_debug(img, layout, rects):
    h, w = img.shape[:2]
    prefix = layout.get("id_prefix", "slot")
    rows = layout.get("rows", 0)
    cols = layout.get("cols", 0)
    step_x = layout.get("step_x", 0.08)
    step_y = layout.get("step_y", 0.08)
    ox = layout["origin"]["x"]
    oy = layout["origin"]["y"]
    for r in range(rows):
        for c in range(cols):
            idx = r * cols + c
            cx = int((ox + c * step_x) * w)
            cy = int((oy + r * step_y) * h)
            cv2.circle(img, (cx, cy), 8, (0, 0, 255), 2)
            cv2.putText(img, f"{prefix}_{idx}", (cx + 10, cy - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 2)
    # 原检测矩形画蓝色
    for (cx, cy, rw, rh, approx, bbox) in rects[:100]:
        cv2.rectangle(img, (bbox[0], bbox[1]), (bbox[2], bbox[3]), (255, 0, 0), 2)
    return img


def main():
    parser = argparse.ArgumentParser(description="Detect rectangular board slots and emit slot_layouts.json")
    parser.add_argument("--game", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--prefix", default="slot")
    parser.add_argument("--rows", type=int, help="期望行数（可选，帮助聚类）")
    parser.add_argument("--cols", type=int, help="期望列数（可选）")
    args = parser.parse_args()

    image_path = Path(args.image)
    if not image_path.exists():
        print(f"image not found: {image_path}", file=sys.stderr)
        sys.exit(2)

    img = imread_safe(image_path)
    if img is None:
        print(f"failed to read image: {image_path}", file=sys.stderr)
        sys.exit(2)

    # 限制尺寸，加快检测
    h, w = img.shape[:2]
    scale = 1.0
    if max(h, w) > MAX_DIM:
        scale = MAX_DIM / float(max(h, w))
        img = cv2.resize(img, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA)
        h, w = img.shape[:2]

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    rects = detect_rectangles(gray)
    print(f"detected {len(rects)} candidate rectangles")

    layout = detect_grid(rects, args.rows, args.cols)
    if layout is None:
        print("could not detect a regular grid; try cropping the board region or providing --rows/--cols", file=sys.stderr)
        sys.exit(3)

    layout["id_prefix"] = args.prefix
    if args.rows:
        layout["rows"] = args.rows
    if args.cols:
        layout["cols"] = args.cols

    game_dir = ROOT / "games" / args.game
    game_dir.mkdir(parents=True, exist_ok=True)

    layout_doc = {
        "game_id": args.game,
        "comment": "由 detect_slot_layout.py 自动检测生成，请核对 debug 图后微调参数。",
        "layouts": [layout],
        "slots": [],
    }
    out_path = game_dir / "slot_layouts.json"
    out_path.write_text(json.dumps(layout_doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out_path}")

    debug = draw_debug(img.copy(), layout, rects)
    debug_path = game_dir / "slot_layouts.debug.jpg"
    imwrite_safe(debug_path, debug)
    print(f"wrote {debug_path}")


if __name__ == "__main__":
    main()
