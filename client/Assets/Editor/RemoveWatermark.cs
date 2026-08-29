using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 把起始玩家标记成图的白/浅灰背景抠成透明（保留蓝白宝石），并去掉右下角水印。
/// 用 flood-fill（从四边向内，接近背景色且连通的区域 -> 透明），避免误伤宝石。
/// 用法：Unity -batchmode -executeMethod RemoveWatermark.Run
/// </summary>
public static class RemoveWatermark
{
    static string Repo = "D:/workspace/board";

    public static void Run()
    {
        string src = Repo + "/games/splendor/media/marker/起始玩家标记_成图.png";
        string dst = Repo + "/games/splendor/media/marker/起始玩家标记_clean.png";
        if (!File.Exists(src)) { Debug.LogError("[Clean] 缺失 " + src); EditorApplication.Exit(1); return; }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(src))) { Debug.LogError("[Clean] 解码失败"); EditorApplication.Exit(1); return; }
        int W = tex.width, H = tex.height;
        var px = tex.GetPixels();

        Color bg = SampleCorner(px, W, H);
        Debug.Log($"[Clean] 背景参考 = {bg.r:F2},{bg.g:F2},{bg.b:F2}");

        bool[] vis = new bool[W * H];
        var queue = new int[W * H];
        int head = 0, tail = 0;
        System.Action<int> seed = (idx) => { if (!vis[idx]) { vis[idx] = true; queue[tail++] = idx; } };
        for (int x = 0; x < W; x++) { seed(idx(x, 0, W)); seed(idx(x, H - 1, W)); }
        for (int y = 0; y < H; y++) { seed(idx(0, y, W)); seed(idx(W - 1, y, W)); }

        int cleared = 0;
        while (head < tail)
        {
            int cur = queue[head++];
            int cx = cur % W, cy = cur / W;
            if (IsBg(px[cur], bg))
            {
                px[cur] = new Color(0, 0, 0, 0);
                cleared++;
                if (cx > 0 && !vis[cur - 1]) { vis[cur - 1] = true; queue[tail++] = cur - 1; }
                if (cx < W - 1 && !vis[cur + 1]) { vis[cur + 1] = true; queue[tail++] = cur + 1; }
                if (cy > 0 && !vis[cur - W]) { vis[cur - W] = true; queue[tail++] = cur - W; }
                if (cy < H - 1 && !vis[cur + W]) { vis[cur + W] = true; queue[tail++] = cur + W; }
            }
        }
        tex.SetPixels(px); tex.Apply();
        Debug.Log($"[Clean] 透明化 {cleared} 像素");

        File.WriteAllBytes(dst, tex.EncodeToPNG());
        Debug.Log("[Clean] 已输出 " + dst);
        EditorApplication.Exit(0);
    }

    static int idx(int x, int y, int W) { return y * W + x; }

    static Color SampleCorner(Color[] px, int W, int H)
    {
        Color sum = Color.clear; int n = 0;
        for (int x = 0; x < 10; x++) for (int y = 0; y < 10; y++) { sum += px[y * W + x]; n++; }
        for (int x = W - 10; x < W; x++) for (int y = 0; y < 10; y++) { sum += px[y * W + x]; n++; }
        for (int x = 0; x < 10; x++) for (int y = H - 10; y < H; y++) { sum += px[y * W + x]; n++; }
        for (int x = W - 10; x < W; x++) for (int y = H - 10; y < H; y++) { sum += px[y * W + x]; n++; }
        return sum / n;
    }

    static bool IsBg(Color c, Color bg)
    {
        float lum = (c.r + c.g + c.b) / 3f;
        float sat = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
        return lum > 0.82f && sat < 0.10f;
    }
}
