#!/usr/bin/env python3
"""
从 PDF 组件页检测组件，生成可人工审核的 manifest.json。
自动标记截断/可疑项，检测遗漏区域。

Usage:
    D:/Python/Python312/python.exe scripts/generate_manifest.py
    # 审核后裁切:
    D:/Python/Python312/python.exe scripts/crop_from_manifest.py --game civolution --page 4
"""
import fitz
import cv2
import json
import numpy as np
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME_DIR = ROOT / "games" / "civolution"
ASSETS_DIR = GAME_DIR / "assets" / "components"
DPI = 600

# ── 检测参数 ──────────────────────────────────────
GAP_PERCENTILE = 20
MIN_ROW_HEIGHT = 50
MIN_COMP_WIDTH = 60
MAX_COMP_WIDTH_RATIO = 0.85
ROW_SMOOTH_KERNEL = 20
COL_SMOOTH_KERNEL = 20
COL_DARK_PERCENTILE = 45

# ── 后过滤参数 ─────────────────────────────────────
MIN_COMP_HEIGHT = 130
MIN_COMP_WIDTH_POST = 130
MIN_COMP_AREA = 20000
MAX_ASPECT = 4.0
PADDING = 30  # 最终裁切边距


def find_component_boxes(gray_img):
    """投影分析找组件包围框"""
    h, w = gray_img.shape
    inv = 255 - gray_img

    row_means = inv.mean(axis=1)
    row_smooth = np.convolve(row_means, np.ones(ROW_SMOOTH_KERNEL) / ROW_SMOOTH_KERNEL, mode='same')
    gap_thresh = np.percentile(row_smooth, GAP_PERCENTILE)
    is_gap = row_smooth < gap_thresh

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

    all_boxes = []
    for i in range(len(gaps) - 1):
        row_top = gaps[i][1]
        row_bot = gaps[i + 1][0]
        row_height = row_bot - row_top
        if row_height < MIN_ROW_HEIGHT:
            continue

        row_region = inv[row_top:row_bot, :]
        col_means = row_region.mean(axis=0)
        col_smooth = np.convolve(col_means, np.ones(COL_SMOOTH_KERNEL) / COL_SMOOTH_KERNEL, mode='same')
        col_thresh = np.percentile(col_smooth, COL_DARK_PERCENTILE)
        is_dark = col_smooth > col_thresh

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
        if in_comp and len(is_dark) - comp_start >= MIN_COMP_WIDTH:
            comps.append((comp_start, w - 1))

        for cx1, cx2 in comps:
            all_boxes.append((cx1, row_top, cx2 - cx1, row_height))

    # 合并垂直重叠框
    if not all_boxes:
        return []
    all_boxes.sort(key=lambda b: (b[1], b[0]))
    merged = []
    for box in all_boxes:
        x, y, bw, bh = box
        merged_flag = False
        for j, (mx, my, mbw, mbh) in enumerate(merged):
            overlap_y = min(y + bh, my + mbh) - max(y, my)
            if overlap_y > 0:
                overlap_x = min(x + bw, mx + mbw) - max(x, mx)
                min_w = min(bw, mbw)
                if overlap_x > min_w * 0.5:
                    nx = min(x, mx)
                    ny = min(y, my)
                    nw = max(x + bw, mx + mbw) - nx
                    nh = max(y + bh, my + mbh) - ny
                    merged[j] = (nx, ny, nw, nh)
                    merged_flag = True
                    break
        if not merged_flag:
            merged.append(box)
    merged.sort(key=lambda b: (b[1], b[0]))
    return merged


def filter_boxes(boxes, img_w, img_h):
    """过滤噪声"""
    return [
        (x, y, bw, bh) for (x, y, bw, bh) in boxes
        if bh >= MIN_COMP_HEIGHT
        and bw >= MIN_COMP_WIDTH_POST
        and bw * bh >= MIN_COMP_AREA
        and max(bw / bh, bh / bw) <= MAX_ASPECT
    ]


def check_truncation(gray_img, x, y, w, h, margin=8):
    """检查包围框内的内容是否触及边缘（截断标志）。
    只标记严重截断：内容占边缘 >60% 或有明显裁切痕迹。"""
    x1, y1 = max(0, x), max(0, y)
    x2, y2 = min(gray_img.shape[1], x + w), min(gray_img.shape[0], y + h)
    if x2 - x1 < 20 or y2 - y1 < 20:
        return []

    roi = gray_img[y1:y2, x1:x2]
    rh, rw = roi.shape
    dark_thresh = 180

    touches = []
    # 检查每条边：边缘有 >50% 像素是暗色（内容），且边缘外有亮色（说明内容被截断）
    m = min(margin, max(rh // 6, 3))
    if m > 2:
        top_ratio = (roi[0:m, :] < dark_thresh).sum() / max(roi[0:m, :].size, 1)
        if top_ratio > 0.5:
            touches.append('top')
        bot_ratio = (roi[rh - m:rh, :] < dark_thresh).sum() / max(roi[rh - m:rh, :].size, 1)
        if bot_ratio > 0.5:
            touches.append('bottom')
    m = min(margin, max(rw // 6, 3))
    if m > 2:
        left_ratio = (roi[:, 0:m] < dark_thresh).sum() / max(roi[:, 0:m].size, 1)
        if left_ratio > 0.5:
            touches.append('left')
        right_ratio = (roi[:, rw - m:rw] < dark_thresh).sum() / max(roi[:, rw - m:rw].size, 1)
        if right_ratio > 0.5:
            touches.append('right')

    return touches


def expand_box(x, y, w, h, img_w, img_h, padding):
    """扩大包围框"""
    x1 = max(0, x - padding)
    y1 = max(0, y - padding)
    x2 = min(img_w, x + w + padding)
    y2 = min(img_h, y + h + padding)
    return x1, y1, x2 - x1, y2 - y1


def find_uncovered_regions(boxes, gray_img):
    """找出页面上未被任何包围框覆盖、可能遗漏组件的区域"""
    h, w = gray_img.shape
    covered = np.zeros((h, w), dtype=np.uint8)
    for x, y, bw, bh in boxes:
        x1, y1 = max(0, x - 5), max(0, y - 5)
        x2, y2 = min(w, x + bw + 5), min(h, y + bh + 5)
        covered[y1:y2, x1:x2] = 255

    # 找覆盖图上的大空洞
    inv_covered = 255 - covered
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 20))
    dilated = cv2.dilate(inv_covered, kernel, iterations=1)
    contours, _ = cv2.findContours(dilated, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    uncovered = []
    for cnt in contours:
        area = cv2.contourArea(cnt)
        if area > 30000:  # 至少 30000 px²
            bx, by, bbw, bbh = cv2.boundingRect(cnt)
            # 检查这个区域是否有内容
            region = gray_img[by:by + bbh, bx:bx + bbw]
            if region.std() > 30:  # 有足够变化=可能遗漏了组件
                uncovered.append((bx, by, bbw, bbh))

    return uncovered


def get_page_text(page, img_w, img_h):
    """提取页面文字块位置（用于组件命名）"""
    blocks = page.get_text('blocks')
    page_rect = page.rect
    labels = []
    for b in blocks:
        x0, y0, x1, y1, text, block_type, block_no = b
        t = text.strip()
        if not t or len(t) < 3:
            continue
        if t.startswith('.') and len(t.replace('.', '').strip()) < 5:
            continue
        px0 = int(x0 / page_rect.width * img_w)
        py0 = int(y0 / page_rect.height * img_h)
        px1 = int(x1 / page_rect.width * img_w)
        labels.append({'text': t, 'x': px0, 'y': py0, 'w': px1 - px0})
    return labels


def assign_label(box, labels, used_labels):
    """为包围框找最近的文字标签"""
    x, y, w, h = box
    box_cy = y + h // 2
    best = None
    best_dist = float('inf')

    # 优先找组件下方的标签
    for lbl in labels:
        lid = (lbl['text'], lbl['y'])
        if lid in used_labels:
            continue
        if lbl['y'] >= y + h and lbl['y'] < y + h + 400:
            dist = lbl['y'] - (y + h)
            if dist < best_dist:
                best_dist = dist
                best = lbl
    if best and best_dist < 200:
        used_labels.add((best['text'], best['y']))
        return best['text'][:80]

    # 其次找最近的任意标签
    for lbl in labels:
        lid = (lbl['text'], lbl['y'])
        if lid in used_labels:
            continue
        dist = abs(lbl['y'] - box_cy)
        if dist < best_dist:
            best_dist = dist
            best = lbl
    if best and best_dist < 500:
        used_labels.add((best['text'], best['y']))
        return best['text'][:80]
    return ''


def generate_page_manifest(doc, pdf_page_idx, page_num):
    """为一个页面生成完整 manifest"""
    page = doc[pdf_page_idx]
    page_rect = page.rect

    pix = page.get_pixmap(dpi=DPI)
    img_w, img_h = pix.width, pix.height

    img = np.frombuffer(pix.samples, dtype=np.uint8).reshape(img_h, img_w, pix.n)
    if pix.n == 4:
        img = cv2.cvtColor(img, cv2.COLOR_RGBA2BGR)
    elif pix.n == 3:
        img = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

    print(f"Page {page_num}: {img_w}x{img_h}")

    # 检测 → 过滤
    raw_boxes = find_component_boxes(gray)
    print(f"  Raw: {len(raw_boxes)}")
    boxes = filter_boxes(raw_boxes, img_w, img_h)
    print(f"  Filtered: {len(boxes)}")

    # 文字标签
    labels = get_page_text(page, img_w, img_h)
    used_labels = set()

    # 生成 items
    items = []
    issues = []
    for i, (x, y, w, h) in enumerate(boxes):
        label_text = assign_label((x, y, w, h), labels, used_labels)

        # 检查截断
        trunc = check_truncation(gray, x, y, w, h, margin=12)

        # 提高截断置信度的加大 padding
        status = 'ok'
        if trunc:
            status = 'truncated'
            issues.append({'index': i, 'label': label_text, 'bbox': [x, y, w, h], 'truncation': trunc})

        # 归一化坐标（0-1）
        norm_bbox = [
            round(x / img_w, 4),
            round(y / img_h, 4),
            round(w / img_w, 4),
            round(h / img_h, 4),
        ]

        # 文件名
        if label_text:
            clean = label_text.replace('\n', ' ').replace('|', '-').replace('/', '-')
            clean = ' '.join(clean.split())[:50]
            clean = ''.join(c for c in clean if c.isalnum() or c in ' _-')
        else:
            clean = 'component'
        file_name = f"p{page_num:02d}_{i:03d}_{clean}.jpg"

        items.append({
            'index': i,
            'concept_id': None,
            'label_en': label_text,
            'label_zh': '',
            'bbox_norm': norm_bbox,
            'bbox_px': [x, y, x + w, y + h],
            'file': file_name,
            'description': '',
            'review_status': status,
        })

    # 检查遗漏区域
    uncovered = find_uncovered_regions(boxes, gray)
    suggestions = []
    for j, (ux, uy, uw, uh) in enumerate(uncovered):
        norm_bbox = [
            round(ux / img_w, 4),
            round(uy / img_h, 4),
            round(uw / img_w, 4),
            round(uh / img_h, 4),
        ]
        suggestions.append({
            'index': len(items) + j,
            'concept_id': None,
            'label_en': f'[MISSING] uncovered region {j}',
            'label_zh': '',
            'bbox_norm': norm_bbox,
            'bbox_px': [ux, uy, ux + uw, uy + uh],
            'file': f'p{page_num:02d}_missing_{j:02d}.jpg',
            'description': 'Auto-detected uncovered region - may contain missed component',
            'review_status': 'suggested',
        })

    manifest = {
        'source': f'Civolution_Rules_US_web_v1_0.pdf (page {page_num})',
        'page': page_num,
        'image_size': {'width': img_w, 'height': img_h},
        'items': items + suggestions,
        'issues': {
            'truncated': len(issues),
            'suggested': len(suggestions),
            'details': issues,
        },
    }

    return manifest, img


def main():
    doc = fitz.open(str(GAME_DIR / "Civolution_Rules_US_web_v1_0.pdf"))

    for pdf_idx, page_num in [(3, 4), (4, 5)]:
        manifest, img = generate_page_manifest(doc, pdf_idx, page_num)

        out_dir = ASSETS_DIR / f"page-{page_num:02d}"
        out_dir.mkdir(parents=True, exist_ok=True)
        manifest_path = out_dir / "manifest.json"

        with open(manifest_path, 'w', encoding='utf-8') as f:
            json.dump(manifest, f, ensure_ascii=False, indent=2)

        # 输出摘要
        ok = sum(1 for i in manifest['items'] if i['review_status'] == 'ok')
        truncated = sum(1 for i in manifest['items'] if i['review_status'] == 'truncated')
        suggested = sum(1 for i in manifest['items'] if i['review_status'] == 'suggested')

        print(f"  -> {manifest_path}")
        print(f"     {ok} ok, {truncated} truncated, {suggested} suggested (missing)")
        print(f"     Issues: {manifest['issues']['details']}")

        # 同时渲染带框的标注图
        annotated = img.copy()
        colors = {'ok': (0, 255, 0), 'truncated': (0, 165, 255), 'suggested': (0, 0, 255)}
        for item in manifest['items']:
            x1, y1, x2, y2 = item['bbox_px']
            color = colors.get(item['review_status'], (0, 255, 0))
            thickness = 2 if item['review_status'] == 'ok' else 3
            cv2.rectangle(annotated, (x1, y1), (x2, y2), color, thickness)
            cv2.putText(annotated, str(item['index']), (x1 + 3, y1 + 25),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, color, 1)
            if item['review_status'] != 'ok':
                cv2.putText(annotated, item['review_status'],
                            (x1 + 3, y2 - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)

        review_path = out_dir / f"review_page{page_num:02d}.jpg"
        cv2.imwrite(str(review_path), annotated)
        print(f"     Review image: {review_path}")
        print(f"     Green=ok, Orange=truncated, Red=suggested-missing")

    doc.close()
    print("\nDone! Next steps:")
    print("  1. Review the annotated images in assets/components/page-*/review_page*.jpg")
    print("  2. Edit manifest.json to adjust bboxes, add missing items, update labels")
    print("  3. Run: python scripts/crop_from_manifest.py --game civolution --page 4")
    print("  4. Run: python scripts/crop_from_manifest.py --game civolution --page 5")


if __name__ == "__main__":
    main()
