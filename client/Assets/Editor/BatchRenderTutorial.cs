using System.IO;
using UnityEngine;
using UnityEditor;
using BoardGameTutorial;

/// <summary>
/// 8 原语数据驱动播放器（TutorialDirector + games/{game}/tutorial.json）的出图自检入口。
/// 与 BatchRender.cs（TeachingPlayer 旧版）互不干扰。
/// 产物：Logs/tutorial_frames/tutorial_ch{i}.png 和 tutorial_ch{i}_t{t}.png
/// </summary>
public static class BatchRenderTutorial
{
    const string OUT_DIR = "tutorial_frames";

    public static void RenderAllChapters()
    {
        for (int i = 0; i < 6; i++)
            DoRenderChapter(i, -1f, false);
        EditorApplication.Exit(0);
    }

    public static void RenderMotion()
    {
        float[] ts = { 0.3f, 0.6f, 0.85f, 1f };
        for (int i = 0; i < 6; i++)
            foreach (var t in ts)
                DoRenderChapter(i, t, false);
        EditorApplication.Exit(0);
    }

    public static void RenderChapter0() => DoRenderChapter(0, -1f, true);
    public static void RenderChapter1() => DoRenderChapter(1, -1f, true);
    public static void RenderChapter2() => DoRenderChapter(2, -1f, true);
    public static void RenderChapter3() => DoRenderChapter(3, -1f, true);
    public static void RenderChapter4() => DoRenderChapter(4, -1f, true);
    public static void RenderChapter5() => DoRenderChapter(5, -1f, true);

    static void DoRenderChapter(int index, float t, bool exit)
    {
        // 清理上一个 TutorialDirector（TutorialRoot 挂在它下面，一并销毁）
        foreach (var p in Object.FindObjectsOfType<TutorialDirector>())
            Object.DestroyImmediate(p.gameObject);

        var host = new GameObject("TutorialHost");
        var player = host.AddComponent<TutorialDirector>();
        player.autoPlay = false;
        // games/{game}/tutorial.json 与 media 都在仓库 games 目录下
        player.tutorialRoot = Path.Combine(Application.dataPath, "..", "..", "games");

        if (!player.LoadTutorial("splendor"))
        {
            Debug.LogError("[BatchRenderTutorial] 加载 tutorial.json 失败");
            if (exit) EditorApplication.Exit(1);
            return;
        }

        if (t < 0f)
            player.ApplyChapterStateSynced(index);
        else
            player.ApplyChapterProgressSynced(index, t);

        var bytes = CaptureFrame(Camera.main);
        var dir = Path.Combine(Application.dataPath, "../Logs", OUT_DIR);
        Directory.CreateDirectory(dir);
        string name = t < 0f
            ? $"tutorial_ch{index}.png"
            : $"tutorial_ch{index}_t{t:0.00}.png";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        Debug.Log("[BatchRenderTutorial] 已输出: " + path);

        if (exit) EditorApplication.Exit(0);
    }

    static byte[] CaptureFrame(Camera cam)
    {
        if (cam == null) { Debug.LogError("[BatchRenderTutorial] 无相机"); return new byte[0]; }
        int w = 1280, h = 720;
        var rt = RenderTexture.GetTemporary(w, h, 24);
        var prevRT = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = prevRT;
        RenderTexture.ReleaseTemporary(rt);

        var bytes = tex.EncodeToPNG();
        Object.Destroy(tex);
        return bytes;
    }
}
