using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 把起始玩家标记的两个零件图（零件1水平 + 零件2旋转90°）交叉叠放，合成一张十字标记 PNG。
/// 作用：直接拼出真实结构（不依赖出图模型的瞎想），存成素材供动画使用。
/// 用法（命令行）：Unity -batchmode -executeMethod MakeMarkerSprite.Run
/// 输入：games/splendor/media/marker/起始玩家标记_零件1.jpg、零件2.jpg
/// 输出：games/splendor/media/marker/marker_cross.png
/// </summary>
public static class MakeMarkerSprite
{
    static string Repo = "D:/workspace/board";

    public static void Run()
    {
        var p1 = LoadTex(Repo + "/games/splendor/media/marker/起始玩家标记_零件1.jpg");
        var p2 = LoadTex(Repo + "/games/splendor/media/marker/起始玩家标记_零件2.jpg");
        if (p1 == null || p2 == null) { Debug.LogError("[Marker] 零件图加载失败"); EditorApplication.Exit(1); return; }
        Debug.Log($"[Marker] 零件1={p1.width}x{p1.height} 零件2={p2.width}x{p2.height}");

        // 抠背景（接近白/灰 -> 透明）
        var a1 = MakeTransparent(p1);
        var a2 = MakeTransparent(p2);

        // 零件2 旋转90°
        var a2rot = Rotate90(a2);

        // 合成为十字：零件1 在底层（水平横板），零件2rot 叠放在上（竖板）
        int W = Mathf.Max(a1.width, a2rot.width) + 40;
        int H = Mathf.Max(a1.height, a2rot.height) + 40;
        var canvas = new Texture2D(W, H, TextureFormat.RGBA32, false);
        var clear = new Color(0, 0, 0, 0);
        var pixels = new Color[W * H];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;
        // 叠放：把两图都居中画到画布
        Blend(canvas, a1, (W - a1.width) / 2, (H - a1.height) / 2);
        Blend(canvas, a2rot, (W - a2rot.width) / 2, (H - a2rot.height) / 2);
        canvas.Apply();

        var outPath = Repo + "/games/splendor/media/marker/marker_cross.png";
        File.WriteAllBytes(outPath, canvas.EncodeToPNG());
        Debug.Log("[Marker] 已输出: " + outPath);

        Object.Destroy(p1); Object.Destroy(p2);
        EditorApplication.Exit(0);
    }

    static Texture2D LoadTex(string p)
    {
        if (!File.Exists(p)) { Debug.LogWarning("[Marker] 不存在: " + p); return null; }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(p))) { Debug.LogWarning("[Marker] 解码失败: " + p); return null; }
        return tex;
    }

    static Texture2D MakeTransparent(Texture2D t)
    {
        var src = t.GetPixels();
        var dst = new Color[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var c = src[i];
            // 接近白/灰背景 -> 透明（判断浅色 + 低饱和度）
            float lum = (c.r + c.g + c.b) / 3f;
            float sat = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
            if (lum > 0.72f && sat < 0.18f) dst[i] = new Color(0, 0, 0, 0);
            else dst[i] = c;
        }
        var outTex = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
        outTex.SetPixels(dst); outTex.Apply();
        return outTex;
    }

    static Texture2D Rotate90(Texture2D src)
    {
        int w = src.height, h = src.width;
        var rot = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var pixels = src.GetPixels();
        var np = new Color[w * h];
        for (int ny = 0; ny < h; ny++)
            for (int nx = 0; nx < w; nx++)
            {
                // 顺时针 90°：目标(nx,ny) 来自源(sx,sy)=(ny, src.height-1-nx)
                int sx = ny, sy = src.height - 1 - nx;
                np[ny * w + nx] = pixels[sy * src.width + sx];
            }
        rot.SetPixels(np); rot.Apply();
        return rot;
    }

    static void Blend(Texture2D canvas, Texture2D img, int ox, int oy)
    {
        var cp = canvas.GetPixels();
        var ip = img.GetPixels();
        for (int y = 0; y < img.height; y++)
            for (int x = 0; x < img.width; x++)
            {
                int cx = ox + x, cy = oy + y;
                if (cx < 0 || cy < 0 || cx >= canvas.width || cy >= canvas.height) continue;
                var src = ip[y * img.width + x];
                if (src.a <= 0.01f) continue;
                var dst = cp[cy * canvas.width + cx];
                float a = src.a;
                // 简单 over 合成
                cp[cy * canvas.width + cx] = new Color(
                    src.r * a + dst.r * (1 - a),
                    src.g * a + dst.g * (1 - a),
                    src.b * a + dst.b * (1 - a),
                    Mathf.Max(a, dst.a));
            }
        canvas.SetPixels(cp); canvas.Apply();
    }
}
