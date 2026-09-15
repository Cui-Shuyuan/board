// BoardGameTutorial — 编辑器离线出帧
//
// 目的：让 AI 自己看到画面，而不是让用户当截图工。
//
// 用法（Windows 命令行，可从 WSL 调 Unity.exe）：
//   Unity.exe -batchmode -projectPath D:\workspace\board\client \
//     -executeMethod BoardGameTutorial.Editor.TutorialFrameCapture.CaptureAll \
//     -logFile D:\workspace\board\client\Logs\capture.log -quit
//
// 行为：完全不播音频、不等协程。直接用 SnapTo 把每条 cue 推到指定时刻的终态，
// 再把主相机渲染成 PNG，输出到 client/CaptureOut/。
// 这样 batchmode 没有音频设备也能跑，而且画面是确定性的、可重复的。
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BoardGameTutorial.Editor
{
    public static class TutorialFrameCapture
    {
        private const string OutputDir = "CaptureOut";
        private const int Width = 1280;
        private const int Height = 720;

        private struct Shot
        {
            public string Cue;
            public float Time;
            public string File;
        }

        // 要在哪个 cue 的哪一秒出帧。要加内容改这里。
        private static readonly Shot[] Shots =
        {
            new Shot { Cue = "setup.cards.001.1", Time = 0.00f, File = "cards_00_start" },
            new Shot { Cue = "setup.cards.001.1", Time = 1.60f, File = "cards_01_placed" },
            new Shot { Cue = "setup.cards.001.1", Time = 2.90f, File = "cards_02_green" },
            new Shot { Cue = "setup.cards.001.1", Time = 4.20f, File = "cards_03_blue" },

            new Shot { Cue = "action.take.different.001", Time = 0.00f, File = "take_00_start" },
            new Shot { Cue = "action.take.different.001", Time = 0.70f, File = "take_01_highlight" },
            new Shot { Cue = "action.take.different.001", Time = 4.00f, File = "take_02_moved" },
            new Shot { Cue = "action.take.different.001", Time = 4.60f, File = "take_03_final" },
        };

        private static bool verbose;

        public static void CaptureAll()
        {
            verbose = System.Environment.GetCommandLineArgs().Length > 0 &&
                      System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-captureVerbose") >= 0;
            var dir = Path.Combine(Application.dataPath, "..", OutputDir);
            Directory.CreateDirectory(dir);
            Capture(dir, Shots);
            EditorApplication.Exit(0);
        }

        /// <summary>同步出帧：搭景 → 逐条 cue 落位 → 渲染。</summary>
        private static void Capture(string outputDirectory, Shot[] shots)
        {
            // 清掉可能存在的旧播放器与它的场景对象。
            foreach (var old in Object.FindObjectsByType<TutorialCuePlayer>(FindObjectsSortMode.None))
                Object.DestroyImmediate(old.gameObject);
            foreach (var old in Object.FindObjectsByType<TutorialCueAnimPlayer>(FindObjectsSortMode.None))
                Object.DestroyImmediate(old.gameObject);

            var host = new GameObject("TutorialCaptureHost");
            var player = host.AddComponent<TutorialCuePlayer>();

            // 关键：关掉自动播放，避免走异步音频加载路径。
            player.autoPlay = false;
            player.showDebugUI = false;
            player.tutorialRoot = Path.Combine(Application.dataPath, "..", "..", "games");

            if (!player.LoadRuntime())
            {
                Debug.LogError("[TutorialFrameCapture] LoadRuntime 失败");
                return;
            }

            // batchmode 下 AddComponent 不会触发 Awake，所以不能依赖 TutorialCuePlayer.Awake
            // 自动挂载；这里显式创建动画面。
            var anim = host.GetComponent<TutorialCueAnimPlayer>();
            if (anim == null) anim = host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // Unity 每帧会把 Camera.aspect 重置回 Screen 的宽高比；batchmode 下 Screen 固定 640x480，
            // 与渲染目标不一致。取景时用 cameraAspectOverride，出帧前再显式覆盖一次。
            anim.cameraAspectOverride = (float)Width / Height;
            if (verbose) anim.logCameraFit = true;

            string currentCue = null;
            bool continueState = false;

            foreach (var shot in shots)
            {
                if (shot.Cue != currentCue)
                {
                    if (!anim.LoadCue(Path.Combine(player.tutorialRoot, player.gameId), player.track, shot.Cue, continueState))
                    {
                        Debug.LogWarning($"[TutorialFrameCapture] 载入 cue 动画失败: {shot.Cue}（跳过）");
                        currentCue = shot.Cue;
                        continue;
                    }
                    currentCue = shot.Cue;
                    // 顺序出帧：同一条 cue 的多个时刻共享入口状态，跨 cue 才沿用终态。
                    continueState = false;
                }

                anim.SnapTo(shot.Time);

                string path = Path.Combine(outputDirectory, shot.File + ".png");
                SaveFrame(path);
                Debug.Log($"[TutorialFrameCapture] saved {path}");
            }

            Debug.Log("[TutorialFrameCapture] done");
        }

        /// <summary>把关键点的屏幕坐标打出来，用于诊断取景。</summary>
        private static void DumpProjection(TutorialCueAnimPlayer anim, string tag)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Debug.Log($"[Capture.Dump {tag}] cam.orthoSize={cam.orthographicSize:0.000} aspect={cam.aspect:0.000} " +
                      $"screen={Screen.width}x{Screen.height} pos=({cam.transform.position.x:0.00},{cam.transform.position.y:0.00},{cam.transform.position.z:0.00})");

            var probes = new (string name, Vector3 world)[]
            {
                ("world origin", Vector3.zero),
                ("deck col", new Vector3(-2.30f, 0f, 0.10f)),
                ("market left", new Vector3(-2.05f, 0f, 0.10f)),
                ("market right", new Vector3(0.35f, 0f, 0.10f)),
                ("noble mid", new Vector3(-0.85f, 0f, 1.85f)),
                ("holding mid", new Vector3(-0.85f, 0f, -2.15f)),
                ("gems mid", new Vector3(0f, 0f, -1.13f)),
            };
            foreach (var probe in probes)
            {
                Vector3 screen;
                bool ok = anim.WorldToScreen(probe.world, out screen);
                Debug.Log($"[Capture.Dump {tag}] {probe.name,-14} screen=({screen.x:0},{screen.y:0}) visible={ok}");
            }
        }

        private static void SaveFrame(string path)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[TutorialFrameCapture] 没有 Camera.main");
                return;
            }

            // Unity 每帧会把 Camera.aspect 重置回 Screen 的宽高比；batchmode 下 Screen 固定 640x480，
            // 与我们要渲染的 16:9 不一致，会导致取景按 4:3 计算、画面被裁切。这里显式覆盖。
            cam.aspect = (float)Width / Height;

            var rt = RenderTexture.GetTemporary(Width, Height, 24);
            var previous = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previous;

            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
#endif
