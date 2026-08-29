using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 去掉起始玩家标记成图右下角的"豆包AI生成"水印。
/// 水印在右下角(约右下 22% 区域)，将该区域像素置为透明(背景本身是透明/网格，无实体)。
/// 用法：Unity -batchmode -executeMethod RemoveWatermark.Run
/// 输入：games/splendor/media/marker/起始玩家标记_成图.png
/// 输出：games/splendor/media/marker/起始玩家标记_成图_nwm.png
/// </summary>
public static class RemoveWatermark
{
    static string Repo = "D:/workspace/board";

    public static void Run()
    {
        string src = Repo + "/games/splendor/media/marker/起始玩家标记_成图.png";
        string dst = Repo + "/games/splendor/media/marker/起始玩家标记_成图_nwm.png";
        if (!File.Exists(src)) { Debug.LogError("[Watermark] 缺失: " + src); EditorApplication.Exit(1); return; }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(src))) { Debug.LogError("[Watermark] 解码失败"); EditorApplication.Exit(1); return; }
        Debug.Log($"[Watermark] 图 {tex.width}x{tex.height}");

        var pixels = tex.GetPixels();

        // 检测右下角水印区域：约右下 22% 宽 x 25% 高。将该区域所有接近"浅灰/白"的像素置透明。
        // 水印是半透明白字，背景是透明网格；把右下角整体 alpha 拉低即可，如宝石不在该区域则安全。
        float x0 = tex.width * 0.78f;
        float y0 = tex.height * 0.72f; // 从顶部算(Unity y=0 是底部，需反转)；这里用"从底部"坐标
        // Unity 像素数组第0行是底部。我们想让"图像右下角(视觉上右下)"透明。
        // 视觉右下 = x 靠右(高x)，y 靠下(视觉上)。在像素数组中 y小=图像底部。所以视觉右下的 y 值小。
        float bottomFrac = 0.28f; // 视觉上从底往上 28% 高度
        float rightFrac = 0.24f;  // 视觉上从右往左 24% 宽度
        int xStart = (int)(tex.width * (1f - rightFrac)); // 靠右开始
        int yEnd = (int)(tex.height * bottomFrac);         // 数组 y < 该值 = 图像底部区域

        int cleared = 0;
        for (int y = 0; y < yEnd; y++)
            for (int x = xStart; x < tex.width; x++)
            {
                var c = pixels[y * tex.width + x];
                // 只透明掉接近灰白的水印/背景色
                float lum = (c.r + c.g + c.b) / 3f;
                float sat = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
                if (lum > 0.55f && sat < 0.30f)
                {
                    pixels[y * tex.width + x] = new Color(0, 0, 0, 0);
                    cleared++;
                }
            }
        tex.SetPixels(pixels); tex.Apply();
        Debug.Log($"[Watermark] 已透明化 {cleared} 像素");

        File.WriteAllBytes(dst, tex.EncodeToPNG());
        Debug.Log("[Watermark] 已输出: " + dst);
        EditorApplication.Exit(0);
    }
}
