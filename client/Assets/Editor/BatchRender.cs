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

    /// <summary>对每个 chunk 拍动画中间帧（t=0.3/0.6/0.85/1.0），用于评估动画动态质量。</summary>
    public static void RenderMotion()
    {
        int n = TotalChunks();
        float[] ts = { 0.3f, 0.6f, 0.85f, 1f };
        for (int i = 0; i < n; i++)
            foreach (var t in ts)
                DoRender(i, t, false);
        EditorApplication.Exit(0);
    }

    public static void RenderFrame()
    {
        DoRender(0, exit: true);
    }

    /// <summary>诊断宝石透明度：渲染 chunk4(发宝石,进度1=播完)并打印每个 gem 的 alpha。</summary>
    public static void RenderGemDiag()
    {
        EnableDiag = true;
        DoRender(4, 1.0f, exit: true);
    }

    public static bool EnableDiag = false;

    static void Diag(TeachingPlayer player)
    {
        if (!EnableDiag) return;
        string[] names = { "gem_diamond", "gem_sapphire", "gem_emerald", "gem_ruby", "gem_onyx", "gem_gold" };
        // 遍历全部 Transform 找名字（gem 是教学播放器的子对象，GameObject.Find 找不到）
        var all = Object.FindObjectsOfType<Transform>(true);
        foreach (var n in names)
        {
            Transform t = null;
            foreach (var tr in all) if (tr.name == n) { t = tr; break; }
            if (t == null) { Debug.Log("[GemDiag] 找不到 " + n); continue; }
            var sr = t.GetComponent<SpriteRenderer>();
            Debug.Log("[GemDiag] " + n + " alpha=" + (sr ? sr.color.a.ToString("F2") : "无SR") + " scale=" + t.localScale.ToString("F2") + " pos=" + t.position);
        }
    }

    // 进度版：确定 chunk + 进度 t
    static void DoRender(int chunkIndex, float t, bool exit)
    {
        EnsureCamera();

        // 清掉上一个 chunk 残留对象
        foreach (var go in Object.FindObjectsOfType<TeachingPlayer>())
            Object.DestroyImmediate(go.gameObject);

        var host = new GameObject("TeachingHost");
        var player = host.AddComponent<TeachingPlayer>();
        player.ApplyChunkProgressSynced(chunkIndex, t);

        var bytes = CaptureFrame(Camera.main);
        Diag(player);
        var dir = System.IO.Path.Combine(Application.dataPath, "../Logs", OUT_DIR);
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"frame_chunk{chunkIndex}_t{t:0.00}.png");
        System.IO.File.WriteAllBytes(path, bytes);
        Debug.Log("[BatchRender] 已输出: " + path);

        if (exit) EditorApplication.Exit(0);
    }

    static void DoRender(int chunkIndex, bool exit)
    {
        EnsureCamera();
        foreach (var go in Object.FindObjectsOfType<TeachingPlayer>())
            Object.DestroyImmediate(go.gameObject);

        var host = new GameObject("TeachingHost");
        var player = host.AddComponent<TeachingPlayer>();
        player.ApplyChunkFinalStateSynced(chunkIndex);

        var bytes = CaptureFrame(Camera.main);
        var dir = System.IO.Path.Combine(Application.dataPath, "../Logs", OUT_DIR);
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "frame_chunk" + chunkIndex + ".png");
        System.IO.File.WriteAllBytes(path, bytes);
        Debug.Log("[BatchRender] 已输出: " + path);

        if (exit) EditorApplication.Exit(0);
    }

    static byte[] CaptureFrame(Camera cam)
    {
        if (cam == null) { Debug.LogError("[BatchRender] 无相机"); return new byte[0]; }
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

    // 允许从命令行传入块索引的办法：Unity -executeMethod 只接受无参方法；
    // 这里用环境变量 / 静态字段让外部脚本可改……为简单起见固定 0 基块，控制台版可后续扩。

    static int TotalChunks()
    {
        var asset = Resources.Load<TextAsset>(TeachingPlayer.ResourceName);
        if (asset == null) return 6;
        var data = JsonUtility.FromJson<TeachingData>(asset.text);
        return data != null && data.chunks != null ? data.chunks.Count : 6;
    }

    /// <summary>真实运行协程验证：驱动 TeachingPlayer 的协程到指定 chunk 播完后渲帧。</summary>
    public static void RenderRuntime()
    {
        EnsureCamera();
        foreach (var go in Object.FindObjectsOfType<TeachingPlayer>())
            Object.DestroyImmediate(go.gameObject);
        var host = new GameObject("TeachingHost");
        var player = host.AddComponent<TeachingPlayer>();
        player.InitAndGo(0); // 启动协程自动连播

        // 用 EditorApplication.update 驱动协程跑到第 5 块(chunk5)后渲帧
        int targetChunk = 4;
        float tStart = Time.realtimeSinceStartup;
        EditorApplication.update += () =>
        {
            if (player.CurrentChunkIndex() >= targetChunk && player.IsPlaying() == false)
            {
                var bytes = CaptureFrame(Camera.main);
                var dir = System.IO.Path.Combine(Application.dataPath, "../Logs", OUT_DIR);
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "frame_runtime_chunk4.png");
                System.IO.File.WriteAllBytes(path, bytes);
                Debug.Log("[BatchRender] runtime 已输出: " + path);
                EditorApplication.update = null;
                EditorApplication.Exit(0);
            }
            if (Time.realtimeSinceStartup - tStart > 40f)
            {
                Debug.Log("[BatchRender] runtime 超时");
                EditorApplication.update = null;
                EditorApplication.Exit(1);
            }
        };
    }

    static bool _captured = false;
    static void MaybeCapture(TeachingPlayer p) { }

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
