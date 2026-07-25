#!/usr/bin/env python3
"""
用投影分析法从规则书组件页自动检测并裁切每个独立组件。
- 水平投影找组件行（rows）
- 垂直投影找行内单个组件（columns）
- 过滤噪声，保存裁切结果

Usage:
    D:/Python/Python312/python.exe scripts/extract_components_cv.py
"""
import fitz
import cv2
import numpy as np
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME_DIR = ROOT / "games" / "civolution"
OUT_DIR = GAME_DIR / "media"
DPI = 600
OUT_DIR.mkdir(parents=True, exist_ok=True)

# ── 参数 ──────────────────────────────────────────
GAP_PERCENTILE = 20       # 投影值低于此百分位视为行间隙
MIN_ROW_HEIGHT = 50       # 最小组件行高度
MIN_COMP_WIDTH = 60       # 最小组件宽度
MAX_COMP_WIDTH_RATIO = 0.85  # 最大组件宽度（占页面比）
PADDING = 20              # 裁切边距（像素）
ROW_SMOOTH_KERNEL = 20    # 行投影平滑核
COL_SMOOTH_KERNEL = 20    # 列投影平滑核
COL_DARK_PERCENTILE = 45  # 列投影暗区阈值百分位

# ── 后过滤参数 ─────────────────────────────────────
MIN_COMP_HEIGHT = 130     # 最小组件高度（排除纯文字标签条）
MIN_COMP_WIDTH_POST = 130 # 最小组件宽度（排除细长文字栏）
MIN_COMP_AREA = 20000     # 最小组件面积（像素²）
MAX_ASPECT_RATIO = 4.0    # 最大宽高比（排除跨页标签条）


def find_component_boxes(gray_img):
    """
    用投影分析在灰度图上找所有组件包围框。
    返回 [(x, y, w, h), ...]
    """
    h, w = gray_img.shape
    inv = 255 - gray_img  # 反转：暗区=高值

    # ── 水平投影 → 找组件行 ──
    row_means = inv.mean(axis=1)
    row_smooth = np.convolve(row_means, np.ones(ROW_SMOOTH_KERNEL) / ROW_SMOOTH_KERNEL, mode='same')

    gap_thresh = np.percentile(row_smooth, GAP_PERCENTILE)
    is_gap = row_smooth < gap_thresh

    # 找间隙区间
    gaps = []
    in_gap = False
    gap_start = 0
    for y in range(len(is_gap)):
        if is_gap[y] and not in_gap:
            gap_start = y
            in_gap = True
        elif not is_gap[y] and in_gap:
            gaps.append((gap_start, y))
            in_gap = False
    if in_gap:
        gaps.append((gap_start, len(is_gap) - 1))

    gaps = [(s, e) for s, e in gaps if e - s > 3]

    # ── 在每对相邻间隙之间找组件 ──
    all_boxes = []
    for i in range(len(gaps) - 1):
        row_top = gaps[i][1]
        row_bot = gaps[i + 1][0]
        row_height = row_bot - row_top

        if row_height < MIN_ROW_HEIGHT:
            continue

        # 行内垂直投影
        row_region = inv[row_top:row_bot, :]
        col_means = row_region.mean(axis=0)
        col_smooth = np.convolve(col_means, np.ones(COL_SMOOTH_KERNEL) / COL_SMOOTH_KERNEL, mode='same')

        col_thresh = np.percentile(col_smooth, COL_DARK_PERCENTILE)
        is_dark = col_smooth > col_thresh

        # 找暗区（组件列）
        comps = []
        in_comp = False
        comp_start = 0
        for x in range(len(is_dark)):
            if is_dark[x] and not in_comp:
                comp_start = x
                in_comp = True
            elif not is_dark[x] and in_comp:
                cw = x - comp_start
                if cw >= MIN_COMP_WIDTH and cw < w * MAX_COMP_WIDTH_RATIO:
                    comps.append((comp_start, x))
                in_comp = False
        if in_comp:
            cw = len(is_dark) - comp_start
            if cw >= MIN_COMP_WIDTH and cw < w * MAX_COMP_WIDTH_RATIO:
                comps.append((comp_start, len(is_dark) - 1))

        for cx1, cx2 in comps:
            all_boxes.append((cx1, row_top, cx2 - cx1, row_height))

    # ── 合并在垂直方向重叠的框（同一组件可能跨多个检测行）──
    if not all_boxes:
        return []

    # 按 Y 排序
    all_boxes.sort(key=lambda b: (b[1], b[0]))

    merged = []
    for box in all_boxes:
        x, y, bw, bh = box
        merged_flag = False
        for j, (mx, my, mbw, mbh) in enumerate(merged):
            # 检查垂直重叠
            overlap_y = min(y + bh, my + mbh) - max(y, my)
            if overlap_y > 0:
                # 检查水平重叠 > 50%
                overlap_x = min(x + bw, mx + mbw) - max(x, mx)
                min_w = min(bw, mbw)
                if overlap_x > min_w * 0.5:
                    # 合并
                    nx = min(x, mx)
                    ny = min(y, my)
                    nw = max(x + bw, mx + mbw) - nx
                    nh = max(y + bh, my + mbh) - ny
                    merged[j] = (nx, ny, nw, nh)
                    merged_flag = True
                    break
        if not merged_flag:
            merged.append(box)

    # 按位置排序
    merged.sort(key=lambda b: (b[1], b[0]))
    return merged


def process_page(doc, pdf_page_idx, page_num):
    """处理一个组件页面"""
    page = doc[pdf_page_idx]
    pix = page.get_pixmap(dpi=DPI)
    img_w, img_h = pix.width, pix.height

    img = np.frombuffer(pix.samples, dtype=np.uint8).reshape(img_h, img_w, pix.n)
    if pix.n == 4:
        img = cv2.cvtColor(img, cv2.COLOR_RGBA2BGR)
    elif pix.n == 3:
        img = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

    print(f"\n{'=' * 60}")
    print(f"PDF Page {page_num}: {img_w}x{img_h} @ {DPI}dpi")
    print(f"{'=' * 60}")

    boxes = find_component_boxes(gray)
    print(f"Detected {len(boxes)} raw regions")

    # 后过滤：排除文字标签条和噪声
    boxes = [
        (x, y, bw, bh) for (x, y, bw, bh) in boxes
        if bh >= MIN_COMP_HEIGHT
        and bw >= MIN_COMP_WIDTH_POST
        and bw * bh >= MIN_COMP_AREA
        and max(bw / bh, bh / bw) <= MAX_ASPECT_RATIO
    ]
    print(f"After filtering: {len(boxes)} component regions")

    # 获取文字块位置用于命名
    blocks = page.get_text('blocks')
    page_rect = page.rect

    def pdf_pt_to_px(pt_x, pt_y):
        return int(pt_x / page_rect.width * img_w), int(pt_y / page_rect.height * img_h)

    # 为每个裁切找最近文字标签
    label_positions = []
    for b in blocks:
        x0, y0, x1, y1, text, block_type, block_no = b
        t = text.strip()
        if not t or len(t) < 3:
            continue
        if t.startswith('.') and len(t.replace('.', '').strip()) < 5:
            continue
        px0, py0 = pdf_pt_to_px(x0, y0)
        label_positions.append({'text': t, 'x': px0, 'y': py0, 'w': pdf_pt_to_px(x1, y1)[0] - px0})

    annotated = img.copy()
    saved = 0
    used_labels = set()

    for i, (x, y, bw, bh) in enumerate(boxes):
        # 添加 padding
        x1 = max(0, x - PADDING)
        y1 = max(0, y - PADDING)
        x2 = min(img_w, x + bw + PADDING)
        y2 = min(img_h, y + bh + PADDING)

        if x2 - x1 < 40 or y2 - y1 < 40:
            continue

        # 查找最近的文字标签
        box_cy = y + bh // 2
        label_text = ''
        best_label = None
        best_dist = float('inf')
        for lbl in label_positions:
            lid = (lbl['text'], lbl['y'])
            if lid in used_labels:
                continue
            # 标签应该在组件下方不远处
            if lbl['y'] >= y + bh and lbl['y'] < y + bh + 300:
                dist = lbl['y'] - (y + bh)
                if dist < best_dist:
                    best_dist = dist
                    best_label = lbl

        if best_label and best_dist < 150:
            label_text = best_label['text'][:60]
            used_labels.add((best_label['text'], best_label['y']))
        else:
            # 找最近的任何标签
            for lbl in label_positions:
                lid = (lbl['text'], lbl['y'])
                if lid in used_labels:
                    continue
                dist = abs(lbl['y'] - box_cy)
                if dist < best_dist:
                    best_dist = dist
                    best_label = lbl
            if best_label:
                label_text = best_label['text'][:60]
                used_labels.add((best_label['text'], best_label['y']))

        # 生成文件名
        if label_text:
            label_clean = label_text.replace('\n', ' ').replace('|', '-').replace('/', '-')
            label_clean = ' '.join(label_clean.split())[:50]
            label_clean = ''.join(c for c in label_clean if c.isalnum() or c in ' _-')
        else:
            label_clean = 'component'

        out_name = f"p{page_num:02d}_{i:03d}_{label_clean}.jpg"
        out_path = OUT_DIR / out_name

        crop = img[y1:y2, x1:x2]
        cv2.imwrite(str(out_path), crop)
        saved += 1

        # 标注
        cv2.rectangle(annotated, (x, y), (x + bw, y + bh), (0, 255, 0), 3)
        cv2.putText(annotated, f"{i}", (x + 5, min(y + 30, img_h - 5)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)

        print(f"  [{i:03d}] {bw:4d}x{bh:4d} @({x:4d},{y:4d}) -> {out_name}")
        if label_text:
            print(f"         label: {label_text[:80]}")

    # 保存标注图
    anno_path = GAME_DIR / f"page-{page_num:02d}_600dpi_annotated.jpg"
    cv2.imwrite(str(anno_path), annotated)
    print(f"  -> {saved} crops. Annotated: {anno_path.name}")
    return saved


def main():
    doc = fitz.open(str(GAME_DIR / "Civolution_Rules_US_web_v1_0.pdf"))

    total = 0
    for pdf_idx, page_num in [(3, 4), (4, 5)]:
        n = process_page(doc, pdf_idx, page_num)
        total += n

    doc.close()

    # 列出输出
    files = sorted(OUT_DIR.glob("*.jpg"))
    print(f"\n{'=' * 60}")
    print(f"Total: {total} component crops saved to {OUT_DIR}")
    print(f"Files: {len(files)}")
    for f in files:
        size_kb = f.stat().st_size / 1024
        print(f"  {f.name:60s} {size_kb:8.1f} KB")


if __name__ == "__main__":
    main()
