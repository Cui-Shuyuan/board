#!/usr/bin/env python3
"""用本地 ComfyUI 生成/重绘素材（FLUX.1-dev）。

为什么走 img2img 而不是 txt2img：我们要的是**"这件东西"看起来干净**，不是"随便生成一枚宝石"。
所以把扫描件当参考图，denoise 控制"改多少"：
    低（0.3–0.45）→ 只去噪点/阴影，形状与颜色基本不动（信息型的件，如卡牌，必须用这一档）
    中（0.5–0.65）→ 材质/光照重画，轮廓大体保留（圆片、板块这类"样子简单"的件）
    高（0.8+）    → 等于重画，只适合"扫描太差，干脆要一张好看的"

⚠️ **卡牌不要用中高 denoise**：卡面上的费用/声望/图标是**信息**，重画会把数字画错。

背景一律要求"纯白、无阴影"，这样 alpha 可以**确定性地**键出来（生成式模型不输出透明通道，
"抠图"这一步仍然由代码做，见 mate_clean/matte_eval）。

用法：
    python3 scripts/gen_comfy.py --images 红宝石.jpg 贵族_0001.jpg --denoise 0.45
    python3 scripts/gen_comfy.py --images 一级发展卡_绿.jpg --denoise 0.3 --suffix _clean
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
COMFY = Path("/mnt/d/ai/ComfyUI_windows_portable_nvidia/ComfyUI_windows_portable/ComfyUI")
SERVER = "http://172.17.208.1:8188"      # WSL → Windows 主机；ComfyUI --listen 0.0.0.0

POSITIVE = ("professional product photograph of a single {what}, centered, "
            "even soft studio lighting, no shadow, pure flat white background, "
            "crisp clean edges, high detail, no text, no watermark")
NEGATIVE = ("shadow, drop shadow, cast shadow, reflection, table, background clutter, "
            "multiple objects, blurry, noise, jpeg artifacts, text, watermark, hands")


def post(path: str, payload: dict | None = None) -> dict:
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(SERVER + path, data=data,
                                headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode())


def workflow(image_name: str, prompt: str, denoise: float, steps: int, seed: int, prefix: str) -> dict:
    """FLUX img2img。节点接线与用户机器上现成工作流一致（unet / ae / clip_l+t5xxl_fp8）。"""
    return {
        "1": {"class_type": "UNETLoader",
              "inputs": {"unet_name": "flux1-dev-fp8.safetensors", "weight_dtype": "default"}},
        "2": {"class_type": "DualCLIPLoader",
              "inputs": {"clip_name1": "t5xxl_fp8_e4m3fn.safetensors", "clip_name2": "clip_l.safetensors",
                         "type": "flux"}},
        "3": {"class_type": "VAELoader", "inputs": {"vae_name": "ae.safetensors"}},
        "4": {"class_type": "LoadImage", "inputs": {"image": image_name, "upload": "image"}},
        "5": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["2", 0]}},
        "6": {"class_type": "CLIPTextEncode", "inputs": {"text": NEGATIVE, "clip": ["2", 0]}},
        "7": {"class_type": "VAEEncode", "inputs": {"pixels": ["4", 0], "vae": ["3", 0]}},
        "8": {"class_type": "FluxGuidance", "inputs": {"conditioning": ["5", 0], "guidance": 3.5}},
        "9": {"class_type": "KSampler",
              "inputs": {"model": ["1", 0], "positive": ["8", 0], "negative": ["6", 0],
                         "latent_image": ["7", 0], "seed": seed, "steps": steps, "cfg": 1.0,
                         "sampler_name": "euler", "scheduler": "normal", "denoise": denoise}},
        "10": {"class_type": "VAEDecode", "inputs": {"samples": ["9", 0], "vae": ["3", 0]}},
        "11": {"class_type": "SaveImage", "inputs": {"images": ["10", 0], "filename_prefix": prefix}},
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--images", nargs="+", required=True, help="扫描件文件名（在 media/card 下）")
    ap.add_argument("--game", default="splendor")
    ap.add_argument("--denoise", type=float, default=0.45)
    ap.add_argument("--steps", type=int, default=20)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--what", default="polished game token, round cabochon",
                    help="提示词里的主体描述（英文更稳），如 'polished red glass gemstone token'")
    ap.add_argument("--suffix", default="_gen", help="输出文件名后缀")
    ap.add_argument("--out", default="/tmp/matte_gen")
    ap.add_argument("--src-dir", default=None, help="扫描件目录（默认自动找本仓库或 Windows 工作区）")
    args = ap.parse_args()

    # 扫描件存在**哪个工作区**：media/ 不进 git，所以 WSL 侧常常没有这些图；
    # 按顺序找：本仓库 → 挂载的 Windows 工作区（Unity 读的那份）。
    candidates = [ROOT / "games" / args.game / "media" / "card",
                  Path("/mnt/d/workspace/board/games") / args.game / "media" / "card"]
    src_dir = next((c for c in candidates if c.exists()), candidates[0])
    in_dir = COMFY / "input"
    out_dir = COMFY / "output"
    dst_dir = Path(args.out)
    dst_dir.mkdir(parents=True, exist_ok=True)
    in_dir.mkdir(parents=True, exist_ok=True)

    try:
        post("/system_stats")
    except Exception as e:
        print(f"连不上 ComfyUI（{SERVER}）：{e}\n先起 ComfyUI："
              f"cd {COMFY} && ../python_embeded/python.exe main.py --listen 0.0.0.0 --port 8188",
              file=sys.stderr)
        return 2

    if args.src_dir:
        src_dir = Path(args.src_dir)
    print(f"扫描件目录：{src_dir}")
    for name in args.images:
        src = src_dir / name
        if not src.exists():
            print(f"找不到 {src}", file=sys.stderr)
            return 2
        shutil.copy(src, in_dir / name)      # ComfyUI 只能读它 input 目录里的图
        prefix = f"gen_{Path(name).stem}{args.suffix}"
        prompt = POSITIVE.format(what=args.what)
        wf = workflow(name, prompt, args.denoise, args.steps, args.seed, prefix)
        t0 = time.time()
        res = post("/prompt", {"prompt": wf})
        pid = res.get("prompt_id")
        print(f"[{name}] 提交 {pid}（denoise={args.denoise}）", flush=True)

        hist = None
        while time.time() - t0 < 1800:
            time.sleep(3)
            try:
                h = post(f"/history/{pid}")
            except Exception:
                continue
            if pid in h and h[pid].get("outputs"):
                hist = h[pid]
                break
        if not hist:
            print(f"[{name}] 超时没结果", file=sys.stderr)
            return 1
        for node_out in hist["outputs"].values():
            for im in node_out.get("images", []):
                src_png = out_dir / im.get("subfolder", "") / im["filename"]
                dst = dst_dir / f"{Path(name).stem}{args.suffix}.png"
                shutil.copy(src_png, dst)
                print(f"[{name}] {time.time() - t0:.0f}s → {dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
