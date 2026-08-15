#!/usr/bin/env python3
"""
token 俯拍照 → 剪影挤出原料（视觉车道③：激光切割扁平 token）。

输入：一张正上方俯拍的照片（token 平放在对比色背景上）
输出（games/{game}/media/capture/{name}/）：
    contour.json       归一化轮廓多边形（包围盒 [0,1] 空间）+ 真实尺寸
    texture_front.png  抠掉背景的正面贴图（RGBA，背景透明）
    mask.png           二值掩码（备用）
    debug.jpg          轮廓叠在原图上的核验图（人工看提取质量）

原理：这类 token 的 3D 模型 = 2D 轮廓挤出厚度。轮廓由照片分割得到，
照片本身即正面贴图；背面默认镜像，侧面由 Unity 用纯色生成。

Usage:
    D:/Python/Python312/python.exe scripts/extract_token_silhouette.py 照片.jpg --name tribe --width-mm 20
"""

import argparse
import json
import cv2
import numpy as np
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ── 参数 ──────────────────────────────────────────
MAX_DIM = 2000            # 长边像素上限（够用且快）
PADDING = 12              # 贴图裁切边距（像素）
EPSILON_RATIO = 0.003     # approxPolyDP 容差（占轮廓周长比），越大轮廓越简
CHAIKIN_PASSES = 1        # 圆角平滑迭代次数（木质 token 的激光切割圆角感）
SECOND_BLOB_RATIO = 0.25  # 第二大目标超过最大目标的此比例 → 提示画面里还有别的东西
BORDER_MARGIN = 3         # 判「轮廓贴边」的像素余量


def imread_safe(path):
    """cv2.imread 处理不了中文路径，用 imdecode 兜底。"""
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_safe(path, img):
    ext = Path(path).suffix
    ok, buf = cv2.imencode(ext, img)
    if ok:
        buf.tofile(str(path))
    return ok


def chaikin(points, passes=CHAIKIN_PASSES):
    """Chaikin 角切法：多边形拐角削成圆角，逼近木质件的激光切割轮廓。"""
    pts = np.asarray(points, dtype=np.float64)
    for _ in range(passes):
        n = len(pts)
        q = np.zeros((n * 2, 2))
        for i in range(n):
            p0, p1 = pts[i], pts[(i + 1) % n]
            q[2 * i] = p0 * 0.75 + p1 * 0.25
            q[2 * i + 1] = p0 * 0.25 + p1 * 0.75
        pts = q
    return pts


def segment_mask(gray, background):
    """
    Otsu 阈值分割出 token 掩码。
    auto 时用边框像素判断哪一侧是背景（背景贴图边缘，token 在中间）：
    边框均值高于阈值 → 背景在亮侧，token 是暗侧。
    """
    ret, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    border_mean = (gray[0, :].mean() + gray[-1, :].mean() + gray[:, 0].mean() + gray[:, -1].mean()) / 4
    token_is_dark = border_mean > ret
    if background == "light":
        token_is_dark = True
    elif background == "dark":
        token_is_dark = False
    if not token_is_dark:
        binary = cv2.bitwise_not(binary)
    return binary


def clean_mask(binary):
    """
    形态学去噪 → 取最大连通域为 token → 填洞。
    返回 (mask, 警告列表)。
    """
    kernel = np.ones((5, 5), np.uint8)
    mask = cv2.morphologyEx(binary, cv2.MORPH_OPEN, kernel, iterations=2)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel, iterations=3)

    n, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    if n <= 1:
        raise RuntimeError(
            "画面里没找到 token——背景不够干净或对比太弱。"
            "试试换对比更强的背景，或用 --background light/dark 指定背景明暗。"
        )
    areas = stats[1:, cv2.CC_STAT_AREA]
    order = np.argsort(-areas)
    biggest = order[0] + 1
    warnings = []
    if len(order) > 1 and areas[order[1]] > areas[order[0]] * SECOND_BLOB_RATIO:
        warnings.append(
            "画面里还有另一个较大的东西（第二大目标面积占 {:.0%}），请一次只拍一个 token".format(
                areas[order[1]] / areas[order[0]]
            )
        )
    token = np.zeros_like(mask)
    token[labels == biggest] = 255

    # 填洞：token 是实心件，掩码内部不应有洞（印刷图案造成的暗斑不算洞）
    inv = cv2.bitwise_not(token)
    flooded = inv.copy()
    cv2.floodFill(flooded, None, (0, 0), 0)
    token = cv2.bitwise_or(token, flooded)  # flooded 里残留的 255 即洞
    return token, warnings


def extract_contour(mask):
    """取外轮廓 → 多边形近似 → Chaikin 圆角平滑，返回 Nx2 float 点集。"""
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        raise RuntimeError("掩码里没有轮廓")
    contour = max(contours, key=cv2.contourArea)
    peri = cv2.arcLength(contour, True)
    poly = cv2.approxPolyDP(contour, EPSILON_RATIO * peri, True)
    return chaikin(poly[:, 0, :])


def main():
    parser = argparse.ArgumentParser(description="token 俯拍照 → 剪影轮廓 + 贴图")
    parser.add_argument("photo", help="俯拍照片路径")
    parser.add_argument("--name", default=None, help="token 名（默认取文件名）")
    parser.add_argument("--game", default="civolution", help="游戏目录名")
    parser.add_argument("--width-mm", type=float, default=None,
                        help="token 真实宽度（毫米，用尺子量最长边）")
    parser.add_argument("--background", choices=["auto", "light", "dark"], default="auto",
                        help="背景明暗（auto=用边框像素自动判断）")
    args = parser.parse_args()

    src = Path(args.photo)
    name = args.name or src.stem
    out_dir = ROOT / "games" / args.game / "media" / "capture" / name
    out_dir.mkdir(parents=True, exist_ok=True)

    img = imread_safe(src)
    if img is None:
        raise RuntimeError("读图失败：{}".format(src))
    # 长边压到 MAX_DIM 以内
    h, w = img.shape[:2]
    scale = min(1.0, MAX_DIM / max(h, w))
    if scale < 1.0:
        img = cv2.resize(img, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA)
    h, w = img.shape[:2]

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    mask, warnings = clean_mask(segment_mask(gray, args.background))

    # ── 轮廓 ──
    pts = extract_contour(mask)
    x, y = pts[:, 0].min(), pts[:, 1].min()
    bw, bh = pts[:, 0].max() - x, pts[:, 1].max() - y
    if x < BORDER_MARGIN or y < BORDER_MARGIN or x + bw > w - BORDER_MARGIN or y + bh > h - BORDER_MARGIN:
        warnings.append("轮廓贴到画面边缘，可能被裁切——重拍一张 token 居中的")

    norm_pts = (pts - [x, y]) / [bw, bh]  # 归一化到包围盒 [0,1]
    norm_pts = np.round(norm_pts, 5)
    aspect = bw / bh

    # ── 贴图：裁包围盒 + 掩码做 alpha ──
    pad = PADDING
    x0, y0 = max(0, int(x) - pad), max(0, int(y) - pad)
    x1, y1 = min(w, int(x + bw) + pad), min(h, int(y + bh) + pad)
    crop = img[y0:y1, x0:x1]
    alpha = mask[y0:y1, x0:x1]
    rgba = cv2.cvtColor(crop, cv2.COLOR_BGR2BGRA)
    rgba[:, :, 3] = alpha

    # ── 核验图：轮廓画回原图 ──
    debug = img.copy()
    pts_i = np.round(pts).astype(np.int32)
    cv2.drawContours(debug, [pts_i], -1, (0, 0, 255), max(2, int(min(h, w) / 400)))
    debug = cv2.resize(debug, (min(w, 1000), int(min(w, 1000) * h / w))) if w > 1000 else debug

    # ── 写盘 ──
    contour_json = {
        "name": name,
        "convention": (
            "points 为包围盒 [0,1] 空间，原点左上、y 向下。"
            "Unity 换算：x_m=(x-0.5)*width_mm/1000，y_m=(0.5-y)*height_mm/1000"
        ),
        "size_mm": {
            "width": args.width_mm,
            "height": round(args.width_mm / aspect, 2) if args.width_mm else None,
        },
        "aspect": round(aspect, 4),
        "bbox_px": [round(x, 1), round(y, 1), round(bw, 1), round(bh, 1)],
        "points": norm_pts.tolist(),
    }
    (out_dir / "contour.json").write_text(
        json.dumps(contour_json, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    imwrite_safe(out_dir / "texture_front.png", rgba)
    imwrite_safe(out_dir / "mask.png", mask)
    imwrite_safe(out_dir / "debug.jpg", debug)

    print("✓ {}：轮廓 {} 点，包围盒 {:.0f}×{:.0f} px（宽高比 {:.2f}）".format(name, len(pts), bw, bh, aspect))
    if args.width_mm:
        print("  真实尺寸：宽 {} mm × 高 {} mm".format(args.width_mm, contour_json["size_mm"]["height"]))
    else:
        warnings.append("未提供 --width-mm，Unity 端需手动指定真实尺寸")
    print("  输出目录：{}".format(out_dir))
    for wmsg in warnings:
        print("⚠ {}".format(wmsg))
    print("  核验图：{}（红色轮廓应贴合 token 边缘）".format(out_dir / "debug.jpg"))


if __name__ == "__main__":
    main()
