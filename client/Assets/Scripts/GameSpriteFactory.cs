using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 运行时程序化生成占位精灵（版图/发展卡/贵族/宝石圆片/起始玩家标记）。
/// 不依赖外部图片文件——全部用 Texture2D.SetPixels 逐像素绘制，纯程序确定性。
/// 真实照片素材到位后，本类可整体替换为 Resources 加载，接口保持不变。
/// </summary>
public static class GameSpriteFactory
{
    const float PPU = 100f;

    // ---------------- 公开入口 ----------------

    /// <summary>版图：贴合 Splendor 设置布局的俯视桌面。</summary>
    public static Sprite Board()
    {
        // 1000x700 像素 = 世界 10x7 单位
        return MakeSprite(1000, 700, (x, y) => BoardPixel(x, y));
    }

    /// <summary>发展卡，level 决定顶部色条与声望点。</summary>
    public static Sprite Card(int level)
    {
        return MakeSprite(150, 190, (x, y) => CardPixel(x, y, level));
    }

    /// <summary>贵族板块。</summary>
    public static Sprite Noble()
    {
        return MakeSprite(150, 150, (x, y) => NoblePixel(x, y));
    }

    /// <summary>宝石圆片，color 为 RGB。</summary>
    public static Sprite Gem(Color color)
    {
        return MakeSprite(256, 256, (x, y) => GemPixel(x, y, color));
    }

    /// <summary>起始玩家标记（指向上方的箭头圆片）。</summary>
    public static Sprite StartMarker()
    {
        return MakeSprite(128, 128, (x, y) => StartMarkerPixel(x, y));
    }

    /// <summary>阴影椭圆。</summary>
    public static Sprite Shadow()
    {
        return MakeSprite(160, 90, (x, y) => ShadowPixel(x, y));
    }

    // ---------------- 像素绘制 ---------------

    static Sprite MakeSprite(int w, int h, System.Func<int, int, Color> pixel)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = pixel(x, y);
        tex.SetPixels(pixels);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), PPU);
    }

    static Color BoardPixel(int x, int y)
    {
        // 桌面底（浅木色）
        Color c = new Color(0.70f, 0.52f, 0.34f);
        // 木纹：每条 90px 画一条浅浅的暗纹
        if ((x + y * 0.3f) % 90 < 2f) c *= 0.94f;
        // 外框向内 10px
        if (x < 10 || x >= 990 || y < 10 || y >= 690) c = new Color(0.48f, 0.33f, 0.20f);
        // 顶：贵族区（y: 40..130 即从上数，注意 y 是高度坐标）
        int rowFromTop = 700 - y;
        if (rowFromTop >= 40 && rowFromTop <= 130 && x >= 60 && x <= 940)
            c = new Color(0.60f, 0.56f, 0.50f);
        // 中：市场区（行 160..470）
        if (rowFromTop >= 160 && rowFromTop <= 470 && x >= 30 && x <= 940)
            c = new Color(0.82f, 0.76f, 0.68f);
        // 下左：宝石供应区（行 500..660, x 30..700）
        if (rowFromTop >= 500 && rowFromTop <= 660 && x >= 30 && x <= 700)
            c = new Color(0.90f, 0.86f, 0.80f);
        // 下右：黄金区（行 500..660, x 720..940）
        if (rowFromTop >= 500 && rowFromTop <= 660 && x >= 720 && x <= 940)
            c = new Color(0.94f, 0.86f, 0.58f);
        // 深色分区边框
        if (rowFromTop == 40 || rowFromTop == 130 || rowFromTop == 160 || rowFromTop == 470 || rowFromTop == 500 || rowFromTop == 660)
            c = new Color(0.45f, 0.34f, 0.22f);
        return c;
    }

    static Color CardPixel(int x, int y, int level)
    {
        // 卡底（米白）
        Color c = new Color(0.97f, 0.97f, 0.94f);
        if (x < 4 || x >= 146 || y < 4 || y >= 186)
            return new Color(0f, 0f, 0f, 0f); // 透明边缘，天然圆角感
        // 边框
        if (x < 8 || x >= 142 || y < 8 || y >= 182) c = new Color(0.55f, 0.50f, 0.42f);
        // 顶部等级色条（y 从 0 是卡底，这里卡高 190，顶条在顶部 y∈[190-34,190-4]）
        Color lvl = level == 1 ? new Color(0.40f, 0.63f, 0.42f)
                   : level == 2 ? new Color(0.42f, 0.52f, 0.78f)
                   : new Color(0.60f, 0.40f, 0.36f);
        int top = 190;
        int lvlTop = top - y; // y=0 底部，y=top 顶部
        if (lvlTop >= 4 && lvlTop <= 34) c = lvl;
        // 声望点圆圈（左上）
        int px = x - 26, py = y - (top - 56);
        if (px * px + py * py <= 14 * 14) c = new Color(0.35f, 0.35f, 0.35f);
        // 底部费用小圆点（5 列 x 2 行，示意）
        int row = rowOfY_Card(y);
        int col = colOfX_Card(x);
        if (row >= 0 && col >= 0)
        {
            int cx = 18 + col * 24, cy = (top - 130) + row * 30;
            int dx = x - cx, dy = y - cy;
            if (dx * dx + dy * dy <= 10 * 10)
            {
                Color[] gemCols = { new Color(0.26f, 0.52f, 0.96f), new Color(0.92f, 0.26f, 0.21f),
                                    new Color(0.20f, 0.66f, 0.33f), new Color(0.98f, 0.74f, 0.02f),
                                    new Color(0.60f, 0.40f, 0.24f) };
                c = gemCols[col];
                if (dx * dx + dy * dy >= 8 * 8) c = new Color(0.4f, 0.4f, 0.4f);
            }
        }
        return c;
    }

    static int rowOfY_Card(int y) { int ry = y - (190 - 160); return (ry >= -10 && ry <= 10) ? 1 : ((ry >= -40 && ry <= -20) ? 0 : -1); }
    static int colOfX_Card(int x) { for (int c = 0; c < 5; c++) { int cx = 18 + c * 24; if (Mathf.Abs(x - cx) <= 12) return c; } return -1; }

    static Color NoblePixel(int x, int y)
    {
        // 卡其底
        Color c = new Color(0.74f, 0.67f, 0.55f);
        if (x < 4 || x >= 146 || y < 4 || y >= 146) return new Color(0f, 0f, 0f, 0f);
        if (x < 8 || x >= 142 || y < 8 || y >= 142) c = new Color(0.48f, 0.40f, 0.28f);
        // 脸
        int fx = x - 75, fy = y - 75;
        int faceTop = 150; // 简化：把脸放在中上部
        int fy2 = fy; // 直接以中心为脸心
        if (fx * fx + fy2 * fy2 <= 34 * 34) c = new Color(0.92f, 0.86f, 0.77f); // 脸
        // 帽
        if (y >= 116 && y <= 136 && x >= 45 && x <= 105) c = new Color(0.60f, 0.24f, 0.24f);
        // 眼睛
        if (((x - 62) * (x - 62) + (y - 78) * (y - 78) <= 25) || ((x - 88) * (x - 88) + (y - 78) * (y - 78) <= 25))
            c = new Color(0.2f, 0.2f, 0.2f);
        return c;
    }

    static Color GemPixel(int x, int y, Color color)
    {
        int dx = x - 128, dy = y - 128;
        float d = Mathf.Sqrt(dx * dx + dy * dy);
        if (d > 120) return new Color(0f, 0f, 0f, 0f);
        // 边缘内的高光与内环
        Color c = color;
        if (d > 114) c = new Color(1f, 1f, 1f, 1f);           // 白边
        else if (d > 94) c = Color.Lerp(color, Color.white, 0.15f); // 内环微亮
        // 左上高光
        int hx = x - 96, hy = y - 96;
        if (hx * hx + hy * hy <= 34 * 34) c = Color.Lerp(c, Color.white, 0.5f);
        return c;
    }

    static Color StartMarkerPixel(int x, int y)
    {
        int dx = x - 64, dy = y - 64;
        float d = Mathf.Sqrt(dx * dx + dy * dy);
        if (d > 62) return new Color(0f, 0f, 0f, 0f);
        // 指向上的箭头多边形（简化三角形）
        bool inside = y >= 10 && y <= 118 && x >= 18 && x <= 110 &&
                      (y < 64 ? Mathf.Abs(x - 64) <= (y - 10) * 0.85f : Mathf.Abs(x - 64) <= (118 - y) * 0.9f + 10);
        Color c = inside ? new Color(0.78f, 0.24f, 0.24f) : new Color(0f, 0f, 0f, 0f);
        if (inside && d > 58) c = new Color(1f, 1f, 1f, 1f); // 白边
        return c;
    }

    static Color ShadowPixel(int x, int y)
    {
        float nx = (x - 80) / 70f, ny = (y - 45) / 36f; // 椭圆归一化
        float v = nx * nx + ny * ny;
        float a = Mathf.Clamp01(1f - v) * 0.5f; // 越靠中心越深
        return new Color(0f, 0f, 0f, a);
    }
}
