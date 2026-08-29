using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>诊断成图的透明度情况：统计 alpha 分布，判断背景是否真透明。</summary>
public static class DiagAlpha
{
    static string Repo = "D:/workspace/board";

    public static void Run()
    {
        string src = Repo + "/games/splendor/media/marker/起始玩家标记_成图.png";
        if (!File.Exists(src)) { Debug.LogError("[Diag] 缺失: " + src); EditorApplication.Exit(1); return; }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(src))) { Debug.LogError("[Diag] 解码失败"); EditorApplication.Exit(1); return; }
        int W = tex.width, H = tex.height;
        var px = tex.GetPixels();

        int alpha0 = 0, alpha255 = 0, alphaMid = 0;
        int nearWhite = 0; // 接近白色(RGB都>0.9)
        for (int i = 0; i < px.Length; i++)
        {
            float a = px[i].a;
            if (a <= 0.02f) alpha0++;
            else if (a >= 0.98f) alpha255++;
            else alphaMid++;
            float lum = (px[i].r + px[i].g + px[i].b) / 3f;
            if (lum > 0.9f) nearWhite++;
        }
        long total = px.Length;
        Debug.Log($"[Diag] 尺寸 {W}x{H} 总像素 {total}");
        Debug.Log($"[Diag] alpha=0: {alpha0} ({alpha0*100f/total:F1}%)  alpha=1: {alpha255} ({alpha255*100f/total:F1}%)  中间: {alphaMid} ({alphaMid*100f/total:F1}%)");
        Debug.Log($"[Diag] 近白色像素: {nearWhite} ({nearWhite*100f/total:F1}%)");
        EditorApplication.Exit(0);
    }
}
