# -*- coding: utf-8 -*-
"""生成 client 项目的第一批占位 sprite（棋盘、彩色圆片、影子）。

输出到 client/Assets/Resources/Sprites/，文件名以 .png.bytes 结尾——
Unity 将其作为原始字节导入（TextAsset），运行时由 TutorialPlayer 用
Sprite.Create 构造，完全绕开纹理导入设置。真实照片素材到位后本脚本可废弃。

坐标约定：棋盘 1000x700 像素 = 世界 10x7 单位（100 PPU），
世界 (x, z) -> 像素 (500 + 100*x, 350 - 100*z)。
"""
import os
import cv2
import numpy as np

OUT = r"D:\workspace\board\client\Assets\Resources\Sprites"
os.makedirs(OUT, exist_ok=True)

# 与 client/Assets/Scripts/TutorialPlayer.cs 的槽位坐标对齐
MARKET_SLOTS = [(2.40, -1.60), (2.40, 0.10), (2.40, 1.80)]
PLAYER_SLOTS = [(-3.30, 1.60), (-3.30, 0.60), (-3.30, -0.40)]


def save(name, img):
    ok, buf = cv2.imencode(".png", img)
    assert ok, name
    buf.tofile(os.path.join(OUT, name + ".png.bytes"))
    print("saved", name, img.shape)


def overlay_rect(img, x, y, w, h, color, alpha):
    ov = img.copy()
    cv2.rectangle(ov, (x, y), (x + w, y + h), color, -1)
    return cv2.addWeighted(ov, alpha, img, 1 - alpha, 0)


def make_board():
    W, H = 1000, 700
    board = np.full((H, W, 3), (161, 119, 76), np.uint8)  # 木色底
    cv2.rectangle(board, (8, 8), (W - 9, H - 9), (92, 64, 38), 16)  # 深色边框
    board = overlay_rect(board, 560, 130, 360, 420, (120, 150, 200), 0.28)  # 市场区
    board = overlay_rect(board, 30, 130, 320, 420, (110, 170, 110), 0.28)    # 玩家区
    for x, z in MARKET_SLOTS + PLAYER_SLOTS:
        cx, cy = int(500 + 100 * x), int(350 - 100 * z)
        cv2.circle(board, (cx, cy), 30, (255, 255, 255), 3)
        cv2.circle(board, (cx, cy), 24, (255, 255, 255), 1)
    save("board", board)


def make_disc(color):
    S = 256
    img = np.zeros((S, S, 4), np.uint8)
    cv2.circle(img, (128, 128), 120, (*color, 255), -1)      # 主体
    cv2.circle(img, (128, 128), 120, (255, 255, 255, 255), 8)  # 白边
    cv2.circle(img, (128, 128), 94, (255, 255, 255, 60), 3)   # 内环
    hl = np.zeros((S, S, 4), np.uint8)                        # 左上高光
    cv2.circle(hl, (96, 96), 34, (255, 255, 255, 70), -1)
    return cv2.add(img, hl)


def make_shadow():
    img = np.zeros((90, 160, 4), np.uint8)
    cv2.ellipse(img, (80, 45), (70, 36), 0, 0, 360, (0, 0, 0, 130), -1)
    img[:, :, 3] = cv2.GaussianBlur(img[:, :, 3], (31, 31), 0)
    save("shadow", img)


if __name__ == "__main__":
    make_board()
    make_shadow()
    for name, c in [
        ("disc_blue", (66, 133, 244)),
        ("disc_red", (234, 67, 53)),
        ("disc_green", (52, 168, 83)),
        ("disc_white", (245, 245, 245)),
        ("disc_gold", (251, 188, 4)),
    ]:
        save(name, make_disc(c))
    print("done ->", OUT)
