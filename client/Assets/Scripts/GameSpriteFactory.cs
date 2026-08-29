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

    /// <summary>发展卡：贴合 Splendor 真实版面（竖版）。level 决定等级色并与宝石主色绑定。</summary>
    public static Sprite Card(int level)
    {
        // 竖版：宽170 高245，比例≈0.69 贴近扫描件
        return MakeSprite(170, 245, (x, y) => CardPixel(x, y, level));
    }

    /// <summary>按宝石类型 + 费用 + 声望画一张具体的一级卡（更贴近真实卡面）。</summary>
    public static Sprite CardGem(Color gemColor, Color[] costGems, int prestige)
    {
        // 竖版
        return MakeSprite(170, 245, (x, y) => CardGemPixel(x, y, gemColor, costGems, prestige));
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
        // 按等级绑定一个主色调（一级绿/二级蓝/三级红，Splendor 等级色）
        Color main = level == 1 ? new Color(0.42f, 0.62f, 0.44f)
                   : level == 2 ? new Color(0.44f, 0.52f, 0.76f)
                   : new Color(0.62f, 0.36f, 0.32f);
        // 费用于此用默认宝石色（简化）；完整费用用 CardGem
        Color[] costs = { new Color(0.26f, 0.52f, 0.96f), new Color(0.20f, 0.66f, 0.33f) };
        return CardGemPixel(x, y, main, costs, level == 3 ? 4 : 0);
    }

    /// <summary>按 Splendor 版面画一张竖卡：左上绶带、右上宝石icon、左中主图渐变、左下费用圆列。</summary>
    static Color CardGemPixel(int x, int y, Color gem, Color[] costGems, int prestige)
    {
        int W = 170, H = 245;
        // 卡底（浅色）
        Color c = new Color(0.97f, 0.96f, 0.92f);
        if (x < 4 || x >= W - 4 || y < 4 || y >= H - 4) return new Color(0f, 0f, 0f, 0f);
        // 边框
        if (x < 6 || x >= W - 6 || y < 6 || y >= H - 6) c = new Color(0.45f, 0.40f, 0.34f);

        // ---- 卡面主图：宝石色渐变(上方 60% 高度) ----
        int artTop = H - 4, artBottom = (int)(H * 0.38f);
        if (y <= artTop && y >= artBottom)
        {
            float t = 1f - (y - artBottom) / (float)(artTop - artBottom); // 0底-1顶
            c = Color.Lerp(Color.Lerp(gem, Color.white, 0.25f), gem, t);
            // 主图底部一条"地面"暗色
            if (y < artBottom + 30) c = Color.Lerp(gem, new Color(0.2f, 0.2f, 0.2f), 0.35f);
        }

        // ---- 左上绶带纹章(等级色, 竖条+顶部横条) ----
        // 顶部横条(左半部, 宽130 高26)
        if (y >= H - 34 && y <= H - 8 && x >= 6 && x <= 136)
            c = Color.Lerp(gem, Color.black, 0.08f);
        // 竖绶带(左边缘, 宽34, 从横条往下)
        if (y >= H - 34 && y <= artBottom + 30 && x >= 6 && x <= 40)
            c = Color.Lerp(gem, Color.black, 0.20f);

        // ---- 右上宝石 icon(圆形) ----
        int icx = 132, icy = H - 46, ico = 26;
        int dix = x - icx, diy = y - icy;
        if (dix * dix + diy * diy <= ico * ico)
        {
            c = Color.Lerp(gem, Color.white, 0.5f);   // 宝石圆
            if (dix * dix + diy * diy >= (ico - 4) * (ico - 4)) c = new Color(0.3f, 0.22f, 0.12f); // 深描边
            if (dix * dix + diy * diy <= 6 * 6) c = Color.Lerp(gem, Color.white, 0.8f); // 高光
        }

        // ---- 左下费用圆列(数量+宝石图标) ----
        int cx0 = 22, cy0 = (int)(H * 0.30f);
        int r = 22;
        for (int i = 0; i < costGems.Length; i++)
        {
            int ccy = cy0 - i * (r * 2 + 4);
            int dx = x - cx0, dy = y - ccy;
            if (dx * dx + dy * dy <= r * r)
            {
                c = Color.Lerp(costGems[i], Color.black, 0.05f);  // 费用圆底=宝石色
                if (dx * dx + dy * dy >= (r - 3) * (r - 3)) c = Color.Lerp(costGems[i], Color.black, 0.4f); // 描边
            }
        }

        // ---- 声望点(左下大圆, 若有) ----
        if (prestige > 0)
        {
            int px = 22, py = H - 40;
            int dx = x - px, dy = y - py;
            if (dx * dx + dy * dy <= 26 * 26)
            {
                c = new Color(0.25f, 0.25f, 0.25f);
                if (dx * dx + dy * dy >= 23 * 23) c = new Color(0.1f, 0.1f, 0.1f);
            }
        }
        return c;
    }

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
