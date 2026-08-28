using UnityEngine;
using UnityEditor;

/// <summary>
/// 命令行出图自检入口：在 batchmode 下创建教学场景、搭景、播到指定块后渲一帧 PNG，
/// 供多模态视觉调参。用法（WSL 侧通过 cmd.exe 调起）：
///   Unity.exe -batchmode -quit -projectPath D:\workspace\board\client \
///            -executeMethod BatchRender.Render -logFile ...\Logs\_render.log
/// 可选参数（第二个）传块索引（0 基）：-executeMethod BatchRender.Render  （无参数穿 0）
/// 产物：D:\workspace\board\client\Logs\render_frames\frame_0.png 等
/// </summary>
public static class BatchRender
{
    const string OUT_DIR = "render_frames";

    public static void Render()
    {
        // 遍历全部 chunk，各出一帧，便于一次审查完整设置流程
        int n = TotalChunks();
        for (int i = 0; i < n; i++)
            DoRender(i, exit: false);
        EditorApplication.Exit(0);
    }

    public static void RenderFrame()
    {
        DoRender(0, exit: true);
    }

    static int TotalChunks()
    {
        var asset = Resources.Load<TextAsset>(TeachingPlayer.ResourceName);
        if (asset == null) return 6;
        var data = JsonUtility.FromJson<TeachingData>(asset.text);
        return data != null && data.chunks != null ? data.chunks.Count : 6;
    }

    // 允许从命令行传入块索引的办法：Unity -executeMethod 只接受无参方法；
    // 这里用环境变量 / 静态字段让外部脚本可改……为简单起见固定 0 基块，控制台版可后续扩。

    static void DoRender(int chunkIndex, bool exit)
    {
        EnsureCamera();

        // 清掉上一个 chunk 残留对象
        foreach (var go in Object.FindObjectsOfType<TeachingPlayer>())
            Object.DestroyImmediate(go.gameObject);

        // 创建教学播放器并同步把场景置为该 chunk 播完后的状态（确定性，不依赖协程）
        var host = new GameObject("TeachingHost");
        var player = host.AddComponent<TeachingPlayer>();
        player.ApplyChunkFinalStateSynced(chunkIndex);

        // 渲染相机一帧
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[BatchRender] 无相机"); return; }
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

        var dir = System.IO.Path.Combine(Application.dataPath, "../Logs", OUT_DIR);
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "frame_chunk" + chunkIndex + ".png");
        System.IO.File.WriteAllBytes(path, bytes);
        Debug.Log("[BatchRender] 已输出: " + path);

        if (exit) EditorApplication.Exit(0);
    }

    static void EnsureCamera()
    {
        if (Camera.main != null) return;
        var go = new GameObject("Main Camera");
        go.tag = "MainCamera";
        var cam = go.AddComponent<Camera>();
        go.AddComponent<AudioListener>();
        cam.orthographic = true;
        cam.orthographicSize = 3.6f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.11f, 0.12f, 0.16f);
        cam.transform.SetPositionAndRotation(
            new Vector3(0f, 7.66f, -6.43f),
            Quaternion.Euler(50f, 0f, 0f));
    }
}
