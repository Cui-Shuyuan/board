#!/usr/bin/env python3
"""
从 Civolution 规则书组件页按黑色矩形边框裁切组件。
每个组件照片都框在黑色矩形边框内（米黄色底、白色页背景）。

核心思路：黑色细线边框 = 有洞的矩形轮廓。
用 RETR_CCOMP 获取二级轮廓层级：外轮廓 + 内孔洞 → 这就是边框。

Usage:
    D:/Python/Python312/python.exe scripts/extract_components_by_border.py
"""
import fitz
import cv2
import numpy as np
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME_DIR = ROOT / "games" / "civolution"
OUT_DIR = GAME_DIR / "media" / "by_border"
DPI = 600

# ── 检测参数 ──────────────────────────────────────
DARK_THRESHOLD = 55        # 灰度 < 此值视为"黑色"（边框是纯黑线，文字/照片内容一般 > 此值）
CLOSE_KERNEL = 3           # 闭运算核大小（只修补 1-2px 断点，不合并相邻边框）
CLOSE_ITER = 1             # 闭运算迭代次数
MIN_COMP_AREA = 15000      # 最小面积（px² @600dpi）—— 大约 122x122
MAX_COMP_AREA = 4000000    # 最大面积
MAX_ASPECT = 5.0           # 最大宽高比
MIN_VERTICES = 4           # 多边形近似最少顶点数
MAX_VERTICES = 12          # 最多顶点数（允许圆角矩形）
PADDING = 8                # 裁切时额外边距

# ── 合并去重参数 ──────────────────────────────────
OVERLAP_THRESH = 0.5       # 重叠面积 > 此比例视为同一组件


def find_border_rects(gray_img):
    """
    找出所有黑色矩形边框。
    策略：用轮廓层级（CCOMP）检测"有洞的矩形"——这是边框的独特特征。
    黑色细线边框 → 暗像素形成环形 → 外轮廓含内孔洞。
    纯文字/照片暗块 → 实心无洞 → 排除。

    返回 [(x, y, w, h), ...]
    """
    h, w = gray_img.shape

    # Step 1: 阈值 — 只取非常暗的像素（边框是纯黑线）
    _, dark = cv2.threshold(gray_img, DARK_THRESHOLD, 255, cv2.THRESH_BINARY_INV)

    # Step 2: 轻量闭运算 — 修补边框上的微小断点（不合并相邻边框）
    if CLOSE_KERNEL > 0:
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (CLOSE_KERNEL, CLOSE_KERNEL))
        dark = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, kernel, iterations=CLOSE_ITER)

    # Step 3: 找轮廓 + 层级（CCOMP：二级树，区分外轮廓和孔洞）
    contours, hierarchy = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)

    if hierarchy is None or len(contours) == 0:
        return []

    hierarchy = hierarchy[0]  # shape: (N, 4) = [next, prev, child, parent]

    # Step 4: 筛选"有洞"的轮廓（边框特征）
    candidates = []
    holeless_candidates = []  # fallback: 没洞但很矩形的

    for i, cnt in enumerate(contours):
        _next, _prev, child, parent = hierarchy[i]

        # 只处理外轮廓（parent == -1）
        if parent != -1:
            continue

        x, y, bw, bh = cv2.boundingRect(cnt)
        area = bw * bh

        # 尺寸过滤
        if area < MIN_COMP_AREA or area > MAX_COMP_AREA:
            continue
        aspect = max(bw / bh, bh / bw) if bh > 0 else 99
        if aspect > MAX_ASPECT:
            continue
        # 贴边的是页面边框
        if x <= 2 or y <= 2 or x + bw >= w - 2 or y + bh >= h - 2:
            continue

        # 多边形近似 → 判断矩形度
        peri = cv2.arcLength(cnt, True)
        approx = cv2.approxPolyDP(cnt, 0.02 * peri, True)
        n_verts = len(approx)

        if n_verts < MIN_VERTICES or n_verts > MAX_VERTICES:
            continue

        # 计算轮廓面积占包围盒比例
        cnt_area = cv2.contourArea(cnt)
        extent = cnt_area / area

        entry = {
            'bbox': (x, y, bw, bh),
            'area': area,
            'vertices': n_verts,
            'extent': extent,
            'has_hole': child != -1,
        }

        if child != -1:
            # 有洞 → 高置信边框
            candidates.append(entry)
        else:
            # 没洞但矩形 → 低置信备用（可能是闭运算填满的小边框）
            holeless_candidates.append(entry)

    # Step 5: 合并去重
    all_candidates = candidates + holeless_candidates
    if not all_candidates:
        return []

    # 按置信度排序（有洞的优先）、面积降序
    all_candidates.sort(key=lambda c: (not c['has_hole'], -c['area']))

    merged = []
    for c in all_candidates:
        x, y, bw, bh = c['bbox']
        dup = False
        for m in merged:
            mx, my, mbw, mbh = m['bbox']
            ox = max(0, min(x + bw, mx + mbw) - max(x, mx))
            oy = max(0, min(y + bh, my + mbh) - max(y, my))
            overlap = ox * oy
            if overlap > OVERLAP_THRESH * min(bw * bh, mbw * mbh):
                dup = True
                break
        if not dup:
            merged.append(c)

    # Step 6: 按页面位置排序
    merged.sort(key=lambda c: (c['bbox'][1], c['bbox'][0]))
    return [m['bbox'] for m in merged]


def get_page_text_blocks(page, img_w, img_h):
    """提取页面文字块坐标，用于组件命名。"""
    blocks = page.get_text('blocks')
    page_rect = page.rect
    labels = []
    for b in blocks:
        x0, y0, x1, y1, text, block_type, block_no = b
        t = text.strip()
        if not t or len(t) < 2:
            continue
        px0 = int(x0 / page_rect.width * img_w)
        py0 = int(y0 / page_rect.height * img_h)
        px1 = int(x1 / page_rect.width * img_w)
        py1 = int(y1 / page_rect.height * img_h)
        labels.append({
            'text': t, 'x': px0, 'y': py0,
            'w': px1 - px0, 'h': py1 - py0,
        })
    return labels


def assign_label(box, labels, used):
    """为包围框匹配最近的文字标签（优先找正下方的）。"""
    x, y, bw, bh = box

    best = None
    best_score = float('inf')

    for lbl in labels:
        lid = (lbl['text'], lbl['y'], lbl['x'])
        if lid in used:
            continue
        # 标签在组件下方不远处
        if lbl['y'] >= y + bh - 50 and lbl['y'] < y + bh + 500:
            h_overlap = max(0, min(x + bw, lbl['x'] + lbl['w']) - max(x, lbl['x']))
            if h_overlap > 0:
                dist = lbl['y'] - (y + bh)
                score = dist - h_overlap * 0.1
                if score < best_score:
                    best_score = score
                    best = lbl

    if best and best_score < 300:
        used.add((best['text'], best['y'], best['x']))
        return best['text'][:80]

    # fallback: 最近标签
    for lbl in labels:
        lid = (lbl['text'], lbl['y'], lbl['x'])
        if lid in used:
            continue
        dist = abs(lbl['y'] + lbl['h'] // 2 - (y + bh // 2))
        if dist < best_score:
            best_score = dist
            best = lbl
    if best and best_score < 800:
        used.add((best['text'], best['y'], best['x']))
        return best['text'][:80]
    return ''


def process_page(doc, pdf_page_idx, page_num):
    """处理一个组件页面。"""
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
    print(f"Page {page_num}: {img_w}x{img_h} @ {DPI}dpi")

    rects = find_border_rects(gray)
    print(f"Detected {len(rects)} border rectangles")

    labels = get_page_text_blocks(page, img_w, img_h)
    used_labels = set()

    out_dir = OUT_DIR / f"page-{page_num:02d}"
    out_dir.mkdir(parents=True, exist_ok=True)

    # 清空旧结果
    for old in out_dir.glob("*.jpg"):
        old.unlink()

    annotated = img.copy()
    saved = 0

    for i, (x, y, bw, bh) in enumerate(rects):
        x1 = max(0, x - PADDING)
        y1 = max(0, y - PADDING)
        x2 = min(img_w, x + bw + PADDING)
        y2 = min(img_h, y + bh + PADDING)

        label_text = assign_label((x, y, bw, bh), labels, used_labels)

        if label_text:
            clean = label_text.replace('\n', ' ').replace('|', '-').replace('/', '-')
            clean = ' '.join(clean.split())[:50]
            clean = ''.join(c for c in clean if c.isalnum() or c in ' _-')
        else:
            clean = 'component'
        out_name = f"p{page_num:02d}_{i:03d}_{clean}.jpg"
        out_path = out_dir / out_name

        crop = img[y1:y2, x1:x2]
        cv2.imwrite(str(out_path), crop)
        saved += 1

        color = (0, 255, 0)
        cv2.rectangle(annotated, (x1, y1), (x2, y2), color, 3)
        cv2.putText(annotated, f"{i}", (x1 + 5, min(y1 + 30, img_h - 5)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)

        print(f"  [{i:03d}] {bw:5d}x{bh:5d} @({x:4d},{y:4d}) -> {out_name}")
        if label_text:
            print(f"         label: {label_text[:80]}")

    anno_path = out_dir / f"review_page{page_num:02d}.jpg"
    cv2.imwrite(str(anno_path), annotated)
    print(f"  -> {saved} crops saved. Review: {anno_path}")
    return saved, annotated


def main():
    pdf_path = GAME_DIR / "Civolution_Rules_US_web_v1_0.pdf"
    doc = fitz.open(str(pdf_path))

    total = 0
    for pdf_idx, page_num in [(3, 4), (4, 5)]:
        n, _ = process_page(doc, pdf_idx, page_num)
        total += n

    doc.close()

    print(f"\n{'=' * 60}")
    print(f"Total: {total} component crops saved to {OUT_DIR}")
    print(f"Review annotated images to verify quality.")


if __name__ == "__main__":
    main()
