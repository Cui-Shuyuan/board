#!/usr/bin/env python3
"""素材流水线：把**扫描件**变成动画能用的**成品 PNG**（抠图 + 统一尺度 + 可选生成式质感）。

分工（2026-09 实测定下来的）：
  · **剪影**靠几何/强边缘，不靠模型 —— 模型的 matte 会被阴影、铺满画面打败；
  · **质感**可以交给生成式（本地 FLUX）—— 但它会"重画"，所以生成之后**必须上锁**：
      剪影锁：alpha 用①算出来的（生成图只提供颜色，不提供形状）
      颜色锁：把生成图的颜色统计对齐扫描件（"红"不会变成"金"）
  · **背景**用边界泛洪键控（白色的件内部不会被掏空）。

圆片（gem/gold）走：强边缘找轮廓 → 最小二乘拟合圆（残差就是质量分）→ 圆形 alpha
                  → 统一到"实物 mm × 统一 px/mm" → 生成式质感 → 上锁。
矩形件（card/noble）本轮未实现：要走 mm 长宽比几何裁切，**且不重画**（卡面数字是信息）。

用法：
    python3 scripts/matte_pipeline.py --class gem --game splendor --out <dir>
    python3 scripts/matte_pipeline.py --class gem --no-generate     # 只要剪影+统一尺度
    python3 scripts/matte_pipeline.py --class gem --denoise 0.5 --report /tmp/report.json
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SCAN_CANDIDATES = [ROOT / "games/splendor/media/card",
                   Path("/mnt/d/workspace/board/games/splendor/media/card")]

# 色板 → 扫描件（与引擎 PaletteImages 一致；引擎那边现在优先取 <base>_cutout.png）
GEMS = {
    "gem_diamond": "白宝石.jpg", "gem_sapphire": "蓝宝石.jpg", "gem_ruby": "红宝石.jpg",
    "gem_emerald": "绿宝石.jpg", "gem_onyx": "黑宝石.jpg", "gem_gold": "黄金.jpg",
}


def luminance(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def fit_circle_by_profile(rgb: np.ndarray, bg: np.ndarray, cx0: float, cy0: float,
                          r0: float) -> tuple[float, float, float, float, float]:
    """沿射线找**最外一次"从背景跳进件"**的位置，再最小二乘拟合圆。

    为什么不是最强梯度：宝石上**印的图案**内部也有强边缘，梯度法会被图案带偏
    （实测残差 4–13%、px/mm 差 27%，八件实物明明一样大）。
    为什么不是"与背景色的差值"：**阴影**也是"非背景"，会把半径撑大（红宝石 510 vs 真值 266）。

    判据：D = |Δ亮度| + 2·|Δ色度| —— 阴影几乎**没有色度**，件（哪怕是黑/白件）的
    亮度差又远大于阴影；再从外向内取**第一次**越过"该射线最大值 35%"的位置，
    就是件的外轮廓。返回 (cx, cy, r, 残差比, 半径一致性)。
    """
    lum = luminance(rgb)
    chroma = rgb.max(axis=2).astype(np.float32) - rgb.min(axis=2).astype(np.float32)
    bg_l = float(luminance(bg.reshape(1, 1, 3))[0, 0])
    bg_c = float(bg.max() - bg.min())
    h, w = lum.shape

    r_out = int(min(min(w, h) * 0.49, r0 * 1.35))
    r_in = max(3, int(r0 * 0.55))
    angles = np.linspace(0, 2 * np.pi, 720, endpoint=False)
    radii = np.arange(r_in, r_out + 1, 1.0)
    cos_t, sin_t = np.cos(angles), np.sin(angles)
    xs = np.clip(np.round(cx0 + np.outer(cos_t, radii)).astype(int), 0, w - 1)
    ys = np.clip(np.round(cy0 + np.outer(sin_t, radii)).astype(int), 0, h - 1)
    dl = np.abs(lum[ys, xs] - bg_l)
    dc = np.abs(chroma[ys, xs] - bg_c)
    d = dl + 2.0 * dc

    pts, radii_hit = [], []
    for i in range(len(angles)):
        row = d[i]
        if row.size == 0:
            continue
        thr = 0.35 * row.max()
        idx = np.nonzero(row >= thr)[0]
        if idx.size == 0:
            continue
        # 从外向内：取最后一个（最外侧）越过的采样点
        j = idx[-1]
        pts.append((cx0 + cos_t[i] * radii[j], cy0 + sin_t[i] * radii[j]))
        radii_hit.append(radii[j])
    if len(pts) < 60:
        return cx0, cy0, r0, 1.0, 1.0

    P = np.array(pts, dtype=np.float64)
    A = np.column_stack([P[:, 0], P[:, 1], np.ones(len(P))])
    b = -(P[:, 0] ** 2 + P[:, 1] ** 2)
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    fx, fy = -sol[0] / 2, -sol[1] / 2
    fr = math.sqrt(max(0.0, fx * fx + fy * fy - sol[2]))
    dist = np.hypot(P[:, 0] - fx, P[:, 1] - fy)
    residual = float(np.std(dist) / max(1e-6, fr))
    consistency = float(np.std(radii_hit) / max(1e-6, np.mean(radii_hit)))
    return fx, fy, fr, residual, consistency


def border_white_mask(rgb: np.ndarray, tol: float = 0.07, max_frac: float = 0.25) -> np.ndarray:
    """从图像边界泛洪出"近白且与边界连通"的像素（返回 True=背景）。

    **泛洪**是关键：贵族板块的插画里也有大片白色（"3"的旗子），
    用亮度阈值一把切会把它们掏空；只有和边界连通的才是台面。
    """
    h, w, _ = rgb.shape
    # 台面颜色**因扫描件而异**（实测贵族 0001 是 (230,230,228)、0003 是 (245,248,243)）——
    # 所以不能写死"近白"阈值，要用**边界中位色**当参考。再按距离泛洪。
    border = np.concatenate([rgb[0], rgb[-1], rgb[:, 0], rgb[:, -1]])
    bed = np.median(border, axis=0)
    near = np.linalg.norm(rgb - bed, axis=2) <= tol * 255
    bg = np.zeros((h, w), dtype=bool)
    stack = []
    for x in range(w):
        for y in (0, h - 1):
            if near[y, x] and not bg[y, x]:
                bg[y, x] = True; stack.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if near[y, x] and not bg[y, x]:
                bg[y, x] = True; stack.append((x, y))
    while stack:
        x, y = stack.pop()
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= nx < w and 0 <= ny < h and near[ny, nx] and not bg[ny, nx]:
                bg[ny, nx] = True; stack.append((nx, ny))
    # **兜底**：泛洪吃掉太多（> max_frac）说明它吃进件本身了（例：卡面边缘颜色均匀、
    # 与边界同色）—— 那就当作"没有台面"，宁可不动也不能把件切掉。
    if bg.mean() > max_frac:
        return np.zeros((h, w), dtype=bool)
    return bg


def process_rect(src: Path, dst: Path, w_mm: float, h_mm: float, px_per_mm: float,
                 key_border_white: bool, inset: int = 1) -> dict:
    """矩形件（发展卡/贵族板块）：裁到实物 → 按 mm 统一尺寸 → （贵族）键掉圆角处的台面。

    扫描件的长宽比常常偏（实测卡面 0.681 vs 实物 63:88=0.716，差 5%）——
    引擎把整张图铺进 width×height 的矩形，所以**重采样到 mm 比例**正好把这点偏差纠回来。
    """
    rgb = np.asarray(Image.open(src).convert("RGB"))
    h, w, _ = rgb.shape
    bg_mask = border_white_mask(rgb)
    frac = bg_mask.mean()
    note = "铺满整幅"
    if frac > 0.02:                       # 有明显台面 → 裁到件的外接框
        ys, xs = np.nonzero(~bg_mask)
        x0, y0, x1, y1 = xs.min() + inset, ys.min() + inset, xs.max() - inset, ys.max() - inset
        rgb = rgb[y0:y1 + 1, x0:x1 + 1]
        note = f"裁掉台面 {w}x{h}→{rgb.shape[1]}x{rgb.shape[0]}"
    W = int(round(w_mm * px_per_mm))
    H = int(round(h_mm * px_per_mm))
    im = Image.fromarray(rgb).resize((W, H), Image.LANCZOS)
    arr = np.asarray(im)
    alpha = np.full((H, W), 255, dtype=np.uint8)
    if key_border_white:                  # 贵族：圆角处露出的台面要透明
        bm = border_white_mask(arr)
        if bm.any():
            # **只保留最大的一块不透明区域**（和宝石那条路一样）：泛洪后常留下孤立小碎块
            # （实测贵族 0005：最大连通域只占 98.1%，其余是碎屑 → 验收会报"不是一个整体"）。
            try:
                import importlib.util
                spec = importlib.util.spec_from_file_location("matte_clean", ROOT / "scripts/matte_clean.py")
                mc = importlib.util.module_from_spec(spec); spec.loader.exec_module(mc)
                keep = mc.largest_component([not v for v in bm.reshape(-1)], W, H)
                bm = np.array([not v for v in keep], dtype=bool).reshape(H, W)
            except Exception as e:
                print(f"  （连通域清理跳过：{e}）")
            # 1px 过渡，避免硬边
            soft = np.clip(bm.astype(np.float32) * 2.0, 0, 1)[..., None]
            a = (1.0 - soft) * 255.0
            alpha = a[..., 0].astype(np.uint8)
    out = Image.fromarray(arr).convert("RGBA")
    out.putalpha(Image.fromarray(alpha))
    out.save(dst)
    return {"out": dst.name, "size": f"{W}x{H}", "note": note,
            "aspect": round(W / H, 4), "mm": f"{w_mm}x{h_mm}",
            "border_white": round(float(bm.mean()) if key_border_white else 0.0, 4)}


def circle_alpha(size: int, r: float) -> np.ndarray:
    """正方画布内的抗锯齿圆 alpha（半径 r，圆心画布中心）。"""
    yy, xx = np.mgrid[0:size, 0:size]
    c = (size - 1) / 2.0
    d = np.hypot(xx - c, yy - c)
    return np.clip(r + 0.5 - d, 0.0, 1.0)


def color_lock(gen: np.ndarray, scan: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """把生成图的颜色统计对齐扫描件（逐通道均值/标准差匹配，只在件内统计）。"""
    out = gen.astype(np.float32).copy()
    for ch in range(3):
        g = gen[..., ch][mask]
        s = scan[..., ch][mask]
        if g.size == 0 or s.size == 0:
            continue
        gm, gs = float(g.mean()), float(g.std()) + 1e-6
        sm, ss = float(s.mean()), float(s.std())
        out[..., ch] = (out[..., ch] - gm) * (ss / gs) + sm
    return np.clip(out, 0, 255).astype(np.uint8)


RECTS = {
    # 类 → (文件名通配, 实物 mm)。发展卡 63x88、贵族 60x60（components.json）。
    "noble": (["贵族_0001.jpg", "贵族_0002.jpg", "贵族_0003.jpg", "贵族_0004.jpg", "贵族_0005.jpg"], 60.0, 60.0, True),
    "card": (["一级发展卡_白.jpg", "一级发展卡_蓝.jpg", "一级发展卡_绿.jpg", "一级发展卡_红.jpg", "一级发展卡_黑.jpg",
              "一级发展卡_背面.jpg", "二级发展卡_白.jpg", "二级发展卡_蓝.jpg", "二级发展卡_绿.jpg", "二级发展卡_红.jpg",
              "二级发展卡_黑.jpg", "二级发展卡_背面.jpg", "三级发展卡_白.jpg", "三级发展卡_蓝.jpg", "三级发展卡_绿.jpg",
              "三级发展卡_红.jpg", "三级发展卡_黑.jpg", "三级发展卡_背面.jpg"], 63.0, 88.0, False),
}


def run_rect(args) -> int:
    names, w_mm, h_mm, key_white = RECTS[args.cls]
    scan_dir = next((c for c in SCAN_CANDIDATES if c.exists()), None)
    if scan_dir is None:
        print("找不到扫描件目录", file=sys.stderr)
        return 2
    out_dir = Path(args.out) if args.out else scan_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    # 统一 px/mm：用宝石那批量到的中位尺度（同一台扫描机、同一档 DPI）。
    # 这样"卡 63mm / 宝石 43mm"的相对大小在动画里是对的。
    px_per_mm = args.px_per_mm
    rows = []
    print(f"{args.cls}：{len(names)} 个，实物 {w_mm}x{h_mm}mm，统一 {px_per_mm:.2f} px/mm "
          f"→ {int(round(w_mm * px_per_mm))}x{int(round(h_mm * px_per_mm))}px")
    for name in names:
        src = scan_dir / name
        if not src.exists():
            print(f"  跳过（没有 {name}）")
            continue
        dst = out_dir / (Path(name).stem + "_cutout.png")
        info = process_rect(src, dst, w_mm, h_mm, px_per_mm, key_white)
        info["scan"] = name
        rows.append(info)
        print(f"  {name:22} → {info['out']:26} {info['size']:>9} 长宽比={info['aspect']:.3f} {info['note']}")
    if args.report:
        Path(args.report).write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n输出目录：{out_dir}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--class", dest="cls", default="gem",
                    choices=["gem", "gold", "noble", "card"])
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--out", default=None, help="输出目录（默认写回扫描件同目录，文件名 <原名>_cutout.png）")
    ap.add_argument("--mm", type=float, default=43.0, help="实物直径（mm）——来自 components.json")
    ap.add_argument("--canonical", type=int, default=512, help="统一输出的像素直径")
    ap.add_argument("--no-generate", action="store_true", help="只做剪影+统一尺度，不跑生成式")
    ap.add_argument("--denoise", type=float, default=0.5)
    ap.add_argument("--report", default=None)
    ap.add_argument("--px-per-mm", type=float, default=11.88,
                    help="统一尺度（宝石那批量到的中位值：11.88 px/mm）")
    args = ap.parse_args()

    if args.cls in ("noble", "card"):
        return run_rect(args)

    scan_dir = next((c for c in SCAN_CANDIDATES if c.exists()), None)
    if scan_dir is None:
        print("找不到扫描件目录", file=sys.stderr)
        return 2
    out_dir = Path(args.out) if args.out else scan_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    # 生成式（可选）：一次会话跑完所有件
    session = None
    if not args.no_generate:
        try:
            import importlib.util
            sys.path.insert(0, str(ROOT / "scripts"))
            spec = importlib.util.spec_from_file_location("gen_comfy", ROOT / "scripts/gen_comfy.py")
            gen = importlib.util.module_from_spec(spec); spec.loader.exec_module(gen)   # noqa
            gen.post("/system_stats")
            gen_session = gen
        except Exception as e:
            print(f"生成式不可用（{e}），退化为只做剪影+统一尺度", file=sys.stderr)
            gen_session = None
    else:
        gen_session = None

    # 先量一遍：像素半径 + 置信度（残差/一致性）。用于"物理先验兜底"：
    # 六枚宝石实物同尺寸（components.json: 43mm），所以**测不准的那件**可以用
    # 其余件的中位尺度（用户原话："这几个圆的大小扫出来其实不一样，但实物是一样的"）。
    measured = {}
    for palette, fname in GEMS.items():
        src = scan_dir / fname
        if not src.exists():
            continue
        rgb = np.asarray(Image.open(src).convert("RGB"))
        bg = np.median(np.concatenate([rgb[:8, :8].reshape(-1, 3), rgb[:8, -8:].reshape(-1, 3),
                                       rgb[-8:, :8].reshape(-1, 3), rgb[-8:, -8:].reshape(-1, 3)]), axis=0)
        dist = np.linalg.norm(rgb.astype(np.float32) - bg, axis=2)
        mask0 = dist > 0.12 * 255
        ys, xs = np.nonzero(mask0)
        if len(xs) < 100:
            continue
        cx0, cy0 = xs.mean(), ys.mean()
        r0 = (xs.max() - xs.min() + ys.max() - ys.min()) / 4.0
        measured[palette] = (rgb, bg, cx0, cy0, r0)
    confident = []
    for palette, (rgb, bg, cx0, cy0, r0) in measured.items():
        _, _, r, resid, cons = fit_circle_by_profile(rgb, bg, cx0, cy0, r0)
        if resid <= 0.02 and cons <= 0.05:
            confident.append(r)
    median_r_per_mm = (2 * float(np.median(confident)) / 43.0) if confident else None
    if median_r_per_mm:
        print(f"（量到的可信尺度：{{}} 件，中位 {{:.2f}} px/mm；测不准的件按它兜底）"
              .format(len(confident), median_r_per_mm))

    rows = []
    for palette, fname in GEMS.items():
        src = scan_dir / fname
        if not src.exists():
            print(f"跳过（没有 {fname}）")
            continue
        rgb = np.asarray(Image.open(src).convert("RGB"))
        h, w, _ = rgb.shape

        # ① 剪影：先用一次粗估计（非背景像素的外接框）定中心与初始半径，再用强边缘拟合圆
        bg = np.median(np.concatenate([rgb[:8, :8].reshape(-1, 3), rgb[:8, -8:].reshape(-1, 3),
                                       rgb[-8:, :8].reshape(-1, 3), rgb[-8:, -8:].reshape(-1, 3)]), axis=0)
        dist = np.linalg.norm(rgb.astype(np.float32) - bg, axis=2)
        mask0 = dist > 0.12 * 255
        ys, xs = np.nonzero(mask0)
        if len(xs) < 100:
            print(f"{fname}: 找不到前景，跳过")
            continue
        cx0, cy0 = xs.mean(), ys.mean()
        r0 = (xs.max() - xs.min() + ys.max() - ys.min()) / 4.0
        cx, cy, r, residual, consistency = fit_circle_by_profile(rgb, bg, cx0, cy0, r0)
        fell_back = False
        if median_r_per_mm and (residual > 0.02 or consistency > 0.05):
            # 测不准（白件白底那种）：半径改用中位尺度 —— 实物尺寸是已知的，
            # 与其信一次坏拟合，不如信"六枚一样大"这条物理事实。圆心仍用自己量到的。
            r = median_r_per_mm * args.mm / 2.0
            fell_back = True

        # 裁到圆的外接正方形 → 缩放到统一像素直径 → 圆形 alpha
        side = int(round(2 * r))
        x0, y0 = int(round(cx - side / 2)), int(round(cy - side / 2))
        pad = max(0, -min(x0, y0))
        x0, y0 = max(0, x0), max(0, y0)
        x1, y1 = min(w, x0 + side), min(h, y0 + side)
        crop = rgb[y0:y1, x0:x1]
        size = args.canonical
        # 缩放前先把"件之外"用**件自身的平均色**填掉：否则 LANCZOS 会把白色台面混进边缘，
        # 在深色桌面上看就是一圈浅色描边（alpha 边缘 1px，混色却会糊一整圈）。
        yy, xx = np.mgrid[y0:y1, x0:x1]
        local = np.clip(r + 0.5 - np.hypot(xx - cx, yy - cy), 0, 1)
        mean_col = (crop[local > 0.5].mean(axis=0) if (local > 0.5).any() else crop.reshape(-1, 3).mean(axis=0))
        crop_im = Image.fromarray(crop).resize((size, size), Image.LANCZOS)
        alpha = circle_alpha(size, size / 2.0 - 1.0)
        scan_piece = np.asarray(crop_im)

        # ② 质感（可选）：生成 → 剪影锁 + 颜色锁
        appearance = scan_piece
        if gen_session is not None:
            what = {"gem_diamond": "polished white glass gemstone token, round cabochon",
                    "gem_sapphire": "polished blue glass gemstone token, round cabochon",
                    "gem_ruby": "polished red glass gemstone token, round cabochon",
                    "gem_emerald": "polished green glass gemstone token, round cabochon",
                    "gem_onyx": "polished black glass gemstone token, round cabochon",
                    "gem_gold": "polished gold coin token, round"}[palette]
            try:
                gen_session.SERVER = gen_session.SERVER
                generated = gen_session.run_one(str(src), what, args.denoise)   # 见 gen_comfy.run_one
            except AttributeError:
                print("gen_comfy.py 缺少 run_one（跳过生成式）", file=sys.stderr)
                generated = None
            except Exception as e:
                print(f"{fname}: 生成失败（{e}）—— 用扫描件本身", file=sys.stderr)
                generated = None
            if generated is not None:
                gim = Image.open(generated).convert("RGB")
                # 生成图 → 同一裁剪框（按比例换算）→ 同一尺寸
                scale_x, scale_y = gim.size[0] / w, gim.size[1] / h
                gbox = (int(x0 * scale_x), int(y0 * scale_y), int(x1 * scale_x), int(y1 * scale_y))
                gim = gim.crop(gbox).resize((size, size), Image.LANCZOS)
                gen_raw = np.asarray(gim)
                m = alpha > 0.5
                d_before = float(np.linalg.norm(gen_raw[m].mean(axis=0) - scan_piece[m].mean(axis=0)))
                appearance = color_lock(gen_raw, scan_piece, m)
                d_after = float(np.linalg.norm(appearance[m].mean(axis=0) - scan_piece[m].mean(axis=0)))
                print(f"      颜色锁：平均色差 {d_before:6.1f} → {d_after:5.1f}（0-441）"
                      f"{'  ⚠ 仍偏大' if d_after > 25 else '  ✓'}")

        out = Image.fromarray(appearance).convert("RGBA")
        out.putalpha(Image.fromarray((alpha * 255).astype(np.uint8)))
        dst = out_dir / (Path(fname).stem + "_cutout.png")
        out.save(dst)

        rows.append({"scan": fname, "out": dst.name, "size": f"{size}x{size}",
                     "r_src": round(r, 1), "residual": round(residual, 4),
                     "consistency": round(consistency, 4),
                     "px_per_mm": round(2 * r / args.mm, 3),
                     "generated": gen_session is not None and appearance is not scan_piece,
                     "fell_back": fell_back,
                     "mm": args.mm })
        print(f"{fname:14} → {dst.name}  r={r:6.1f}px 残差={residual:.3f} 半径一致={consistency:.3f} "
              f"（{2 * r / args.mm:.2f} px/mm）"
              f"{'  [尺度兜底]' if fell_back else ''}"
              f"{'  [生成+锁定]' if rows[-1]['generated'] else ''}")

    if args.report:
        Path(args.report).write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"报告 → {args.report}")
    print(f"\n输出目录：{out_dir}")
    print("统一尺度：所有圆片都渲染成 %d×%d（= %.0fmm 实物），引擎 world_size 0.43 直接对上。"
          % (args.canonical, args.canonical, args.mm))
    return 0


if __name__ == "__main__":
    sys.exit(main())
