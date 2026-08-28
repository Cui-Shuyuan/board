# -*- coding: utf-8 -*-
"""教学动画配件：照片 → 透明贴图 sprite（批量）。

输入：games/{game}/media/raw/{name}.jpg  —— 每个配件一张正上方俯拍，放对比色背景
输出：client/Assets/Resources/teaching/{game}/sprites/{name}.png（透明背景贴图）
      并把 sprites.json 的 slot->png 指向它。

与 scripts/extract_token_silhouette.py（token→剪影挤出）同源思路，这里简化为：
俯拍照片 → 分割出配件 → 抠透明背景 → 裁切归一 → 存 PNG（去掉了 3D 轮廓挤出部分）。

用法:
    D:/Python/Python312/python.exe scripts/extract_teach_sprites.py splendor

依赖: opencv-python, numpy（项目既有脚本已用）
"""
import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "games"
OUT_RES = ROOT / "client" / "Assets" / "Resources" / "teaching"

MAX_DIM = 2000
PADDING = 12


def imread_safe(path):
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_safe(path, img):
    ext = Path(path).suffix
    ok, buf = cv2.imencode(ext, img)
    if ok:
        buf.tofile(str(path))
    return ok


def extract_sprite(img):
    """抠出图中最大配件，返回 RGBA 透明贴图（已裁切+白底已去）。"""
    # 转灰度，Otsu 二值化
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    _, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    # 找外轮廓
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None
    # 取最大轮廓（配件）
    c = max(contours, key=cv2.contourArea)
    x, y, w, h = cv2.boundingRect(c)
    # 裁切并加边距
    x0 = max(0, x - PADDING); y0 = max(0, y - PADDING)
    x1 = min(img.shape[1], x + w + PADDING); y1 = min(img.shape[0], y + h + PADDING)
    crop = img[y0:y1, x0:x1]

    # 构造 alpha：轮廓内不透明，外透明
    mask = np.zeros(binary.shape, dtype=np.uint8)
    cv2.drawContours(mask, [c], -1, 255, -1)
    alpha = mask[y0:y1, x0:x1]

    # 转 RGBA
    bgr = cv2.cvtColor(crop, cv2.COLOR_BGR2RGBA)
    rgba = np.dstack((bgr[:, :, :3], alpha))
    # 平滑 alpha 边缘（抗锯齿）
    rgba[:, :, 3] = cv2.GaussianBlur(rgba[:, :, 3], (3, 3), 0)
    return rgba


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("game", help="游戏 id，如 splendor")
    ap.add_argument("--raw", help="原始照片目录(默认 games/{game}/media/raw)")
    args = ap.parse_args()

    game = args.game
    raw_dir = Path(args.raw) if args.raw else GAMES / game / "media" / "raw"
    out_dir = OUT_RES / game / "sprites"
    out_dir.mkdir(parents=True, exist_ok=True)

    # 读 sprites.json（slot->png）以便回写
    meta_path = OUT_RES / game / "sprites.json"
    meta = {"game": game, "sprites": []}
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))

    count = 0
    for photo in sorted(raw_dir.glob("*.jpg")):
        name = photo.stem  # 文件名 = slot/概念 id
        img = imread_safe(photo)
        if img is None:
            print("跳过(读不了):", photo)
            continue
        sprite = extract_sprite(img)
        if sprite is None:
            print("跳过(没抠到):", photo)
            continue
        out_png = out_dir / (name + ".png")
        imwrite_safe(out_png, sprite)

        # 更新 sprites.json：slot -> png(相对 Resources, 去扩展名)
        entry = {"slot": name, "png": f"{game}/sprites/{name}"}
        # 按 slot 去重替换
        meta["sprites"] = [e for e in meta["sprites"] if e.get("slot") != name]
        meta["sprites"].append(entry)

        print("处理:", photo.name, "->", out_png.relative_to(OUT_RES.parent.parent), f"({sprite.shape[1]}x{sprite.shape[0]})")
        count += 1

    meta_path.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"完成: {count} 张。sprites.json 已更新。")
    print("产物目录:", out_dir)


if __name__ == "__main__":
    main()
