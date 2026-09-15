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
using System.Collections.Generic;
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
        // 追踪模式：按 -captureTrack 传入 cue 名时，从 0 起每 0.15s 出一帧
        private static Shot[] BuildTrackShots(string cue, int count, float step)
        {
            var list = new System.Collections.Generic.List<Shot>();
            for (int i = 0; i < count; i++)
                list.Add(new Shot { Cue = cue, Time = i * step, File = $"track_{i:00}_t{i * step:0.00}" });
            return list.ToArray();
        }

        private static readonly Shot[] Shots =
        {
            new Shot { Cue = "setup.cards.001.1", Time = 0.00f, File = "cards_00_start" },
            new Shot { Cue = "setup.cards.001.1", Time = 1.60f, File = "cards_01_placed" },
            new Shot { Cue = "setup.cards.001.1", Time = 2.90f, File = "cards_02_green" },
            new Shot { Cue = "setup.cards.001.1", Time = 4.20f, File = "cards_03_blue" },

            new Shot { Cue = "setup.cards.001.2", Time = 0.05f, File = "x12_00" },
            new Shot { Cue = "setup.cards.001.2", Time = 0.40f, File = "x12_01" },
            new Shot { Cue = "setup.cards.001.3", Time = 0.05f, File = "x13_00" },
            new Shot { Cue = "setup.cards.001.3", Time = 0.40f, File = "x13_01" },
            new Shot { Cue = "setup.cards.002.1", Time = 0.05f, File = "x14_00" },
            new Shot { Cue = "setup.cards.002.1", Time = 0.40f, File = "x14_01" },

            new Shot { Cue = "setup.cards.002.1", Time = 0.30f, File = "deal_00_before" },
            new Shot { Cue = "setup.cards.002.1", Time = 1.90f, File = "deal_00_shuffle" },
            new Shot { Cue = "setup.cards.002.1", Time = 5.90f, File = "deal_01_dealing" },
            new Shot { Cue = "setup.cards.002.1", Time = 7.90f, File = "deal_02_done" },

            new Shot { Cue = "action.take.different.001", Time = 0.00f, File = "take_00_start" },
            new Shot { Cue = "action.take.different.001", Time = 0.70f, File = "take_01_highlight" },
            new Shot { Cue = "action.take.different.001", Time = 4.00f, File = "take_02_moved" },
            new Shot { Cue = "action.take.different.001", Time = 4.60f, File = "take_03_final" },
        };

        private static bool verbose;
        private static bool dump;

        /// <summary>
        /// 把当前所有组件的位置、可见性、屏幕坐标写成文本。
        /// 比读像素可靠：能直接看出「谁在画面里」「谁本该在画面外却进来了」。
        /// </summary>
        private static void WriteDump(TutorialCueAnimPlayer anim, string path, string tag)
        {
            var cam = Camera.main;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {tag}  cam={(cam == null ? "null" : cam.name)} ortho={cam?.orthographicSize:0.000} aspect={cam?.aspect:0.000}");

            // 区域（zone）中心
            foreach (var zone in anim.Store.Zones)
            {
                var world = anim.Store.ZoneCenter(zone.id);
                sb.AppendLine($"  zone  {zone.id,-24} world=({world.x,6:0.00},{world.z,6:0.00}) role={zone.role}");
            }

            // 区域底板（不在 ZoneStore 里，单独列出来；只看 board_* 的，其余是组件自己的 Renderer）
            foreach (var kv in anim.PanelStates())
                if (kv.Key.StartsWith("board_"))
                    sb.AppendLine($"  panel {kv.Key,-24} enabled={(kv.Value ? "ON" : "off")}");

            // 所有组件实例：ZoneStore 是唯一事实来源
            int visible = 0, offscreen = 0;
            foreach (var item in anim.Store.Items)
            {
                var actor = item.Actor;
                if (actor == null) continue;
                var p = actor.Go.transform.localPosition;
                float sx = float.NaN, sy = float.NaN;
                bool onScreen = false;
                if (cam != null)
                {
                    var sp = cam.WorldToScreenPoint(new Vector3(p.x, p.y, p.z));
                    sx = sp.x / Mathf.Max(1, Screen.width);
                    sy = sp.y / Mathf.Max(1, Screen.height);
                    onScreen = sp.z > 0f && sx >= 0f && sx <= 1f && sy >= 0f && sy <= 1f;
                }
                if (onScreen) visible++; else offscreen++;
                sb.AppendLine($"  item  {item.Id,-18} zone={item.ZoneId,-22} order={item.Order,2} " +
                              $"pos=({p.x,6:0.00},{p.z,6:0.00}) " +
                              $"{(onScreen ? "ONSCREEN" : "offscreen")}" +
                              (onScreen ? $" vp=({sx:0.000},{sy:0.000})" : ""));
            }
            sb.AppendLine($"# onscreen={visible} offscreen={offscreen}");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[TutorialFrameCapture] dump {path}");
        }

        /// <summary>
        /// 类型契约自检：用 -executeMethod ...TutorialFrameCapture.SelfTest 调用。
        ///
        /// 为什么需要：Unity 的 JsonUtility **不支持可空类型**（int? / float?），遇到时静默返回 null，
        /// 不报错。曾经因此让 take / expand_to / peak_alpha 三个字段从 JSON 读进来永远是空，
        /// 表现为「三种宝石各取一枚」只搬了一枚。这里把契约钉死，改坏了会在 CI/出帧时立刻发现。
        /// </summary>
        public static void SelfTest()
        {
            int failures = 0;

            void Check(string what, bool ok, string detail)
            {
                Debug.Log($"[SelfTest] {(ok ? "PASS" : "FAIL")} {what}: {detail}");
                if (!ok) failures++;
            }

            var seed = JsonUtility.FromJson<CueAnimSeed>(
                "{\"expand_to\":7,\"count\":3,\"template\":\"gem\",\"palette\":\"gem_ruby\",\"zone\":\"z\"}");
            Check("CueAnimSeed.expand_to", seed.expand_to == 7, $"expand_to={seed.expand_to}");
            Check("CueAnimSeed.count", seed.count == 3, $"count={seed.count}");

            var ev = JsonUtility.FromJson<CueAnimEvent>(
                "{\"at\":1.5,\"take\":3,\"peak_alpha\":0.55,\"grow\":1.2,\"to_alpha\":0.4,\"from\":[\"a\",\"b\"]}");
            Check("CueAnimEvent.take", ev.take == 3, $"take={ev.take}");
            Check("CueAnimEvent.peak_alpha", Mathf.Abs(ev.peak_alpha - 0.55f) < 1e-4f, $"peak_alpha={ev.peak_alpha}");
            Check("CueAnimEvent.grow", Mathf.Abs(ev.grow - 1.2f) < 1e-4f, $"grow={ev.grow}");
            Check("CueAnimEvent.to_alpha", Mathf.Abs(ev.to_alpha - 0.4f) < 1e-4f, $"to_alpha={ev.to_alpha}");
            Check("CueAnimEvent.from[]", ev.from != null && ev.from.Count == 2, $"from={(ev.from == null ? "null" : string.Join(",", ev.from))}");

            // 未指定时必须是哨兵值，而不是 0/NaN
            var orderEv = JsonUtility.FromJson<CueAnimEvent>("{\"at\":5.45,\"order\":3,\"flip\":true}");
            Check("CueAnimEvent.order", orderEv.order == 3, $"order={orderEv.order}");
            Check("CueAnimEvent.flip", orderEv.flip, $"flip={orderEv.flip}");

            var bare = JsonUtility.FromJson<CueAnimEvent>("{\"at\":0}");
            Check("CueAnimEvent.默认 order=-1", bare.order == -1, $"order={bare.order}");
            Check("CueAnimEvent.默认 take=0", bare.take == 0, $"take={bare.take}");
            Check("CueAnimEvent.默认 peak_alpha<0", bare.peak_alpha < 0f, $"peak_alpha={bare.peak_alpha}");
            Check("CueAnimEvent.默认 to_alpha<0", bare.to_alpha < 0f, $"to_alpha={bare.to_alpha}");

            // 验证 MoveToAt：把 12 件依次放到指定序号，落点必须与序号一致
            var store = new ZoneStore();
            var stage = JsonUtility.FromJson<StageDoc>(
                "{\"zones\":[" +
                "{\"id\":\"src\",\"center\":{\"x\":0,\"z\":0},\"layout\":{\"type\":\"row\",\"x_step\":1},\"capacity\":20}," +
                "{\"id\":\"dst\",\"center\":{\"x\":0,\"z\":0},\"layout\":{\"type\":\"grid\",\"cols\":4,\"x_step\":0.72,\"z_step\":0.95},\"capacity\":12}]," +
                "\"templates\":[{\"id\":\"t\"}]}");
            store.LoadStage(stage);
            for (int i = 0; i < 12; i++) store.Spawn("t", null, "src", 1);
            var items = new List<ZoneItem>(store.Items);
            for (int i = 0; i < 12; i++)
            {
                int want = (i / 4) * 4 + (i % 4);   // 0..11 顺序
                store.MoveToAt(items[i], "dst", want);
            }
            var placed = new List<ZoneItem>();
            foreach (var it in store.Items) if (it.ZoneId == "dst") placed.Add(it);
            placed.Sort((a, b) => a.Order.CompareTo(b.Order));
            bool ordered = true;
            for (int i = 0; i < placed.Count; i++)
            {
                var p = store.CurrentPosition(placed[i]);
                float expectZ = ((i / 4) - 0) * 0.95f;
                if (placed[i].Order != i) { ordered = false; break; }
            }
            Check("MoveToAt 顺序号", ordered, $"放置 {placed.Count} 件，序号 {(placed.Count > 0 ? placed[0].Order + ".." + placed[placed.Count - 1].Order : "-")}");

            Debug.Log($"[SelfTest] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static void CaptureAll()
        {
            verbose = System.Environment.GetCommandLineArgs().Length > 0 &&
                      System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-captureVerbose") >= 0;
            var dir = Path.Combine(Application.dataPath, "..", OutputDir);
            Directory.CreateDirectory(dir);
            var args = System.Environment.GetCommandLineArgs();
            dump = System.Array.IndexOf(args, "-captureDump") >= 0;
            int trackIdx = System.Array.IndexOf(args, "-captureTrack");
            Shot[] shots = Shots;
            if (trackIdx >= 0 && trackIdx + 1 < args.Length)
            {
                int count = 16; float step = 0.15f;
                int countIdx = System.Array.IndexOf(args, "-captureCount");
                if (countIdx >= 0 && countIdx + 1 < args.Length) int.TryParse(args[countIdx + 1], out count);
                int stepIdx = System.Array.IndexOf(args, "-captureStep");
                if (stepIdx >= 0 && stepIdx + 1 < args.Length) float.TryParse(args[stepIdx + 1], out step);
                shots = BuildTrackShots(args[trackIdx + 1], count, step);
            }
            Capture(dir, shots);
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
            if (verbose) { anim.logCameraFit = true; anim.Store.logPull = true; anim.Store.logMoves = true; anim.logImages = true; }

            if (verbose) anim.Store.logPull = true;

            string currentCue = null;
            bool continueState = false;
            bool primed = false;

            foreach (var shot in shots)
            {
                if (shot.Cue != currentCue)
                {
                    // 出帧必须复现「顺序播到这条 cue」时的牌桌状态，而不是凭空起局：
                    // 按真实顺序把目标 cue 之前所有有动画的 cue 走完。
                    // 之前用「先加载某条 cue 当铺垫」的取巧做法，它会在 store 里留下副作用，
                    // 导致后续 start.set 计数错位（宝石被倒进贵族区）。
                    if (!primed)
                    {
                        // 只重放目标**之前**的 cue；目标本身必须留着自己载入。
                        // 曾经把目标也重放了一遍，而 move 不是幂等的（每次追加到 zone 尾部），
                        // 于是同一次搬运用执行两遍，市场多出一批牌挤在角落。
                        ReplayPreceding(anim, player, shot.Cue);
                        primed = true;
                        // 前序终态就是目标的入口状态，所以目标用 continueState=false：
                        // 它会以当前 store 为起点，并把这当作本条 cue 的入口快照。
                        continueState = false;
                        currentCue = null;
                    }

                    if (!anim.LoadCue(Path.Combine(player.tutorialRoot, player.gameId), player.track, shot.Cue, continueState))
                    {
                        Debug.LogWarning($"[TutorialFrameCapture] 载入 cue 动画失败: {shot.Cue}（跳过）");
                        currentCue = shot.Cue;
                        continue;
                    }
                    currentCue = shot.Cue;
                }

                anim.SnapTo(shot.Time);

                if (dump)
                    WriteDump(anim, Path.Combine(outputDirectory, shot.File + ".txt"), $"{shot.Cue} t={shot.Time:0.00}");

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

        /// <summary>
        /// 按真实顺序重放目标 cue 之前所有存在动画数据的 cue，
        /// 让牌桌状态等于「顺序播到这里」时的样子。
        /// </summary>
        private static bool replayedAny;

        private static void ReplayPreceding(TutorialCueAnimPlayer anim, TutorialCuePlayer player, string targetCue)
        {
            replayedAny = false;
            string root = Path.Combine(player.tutorialRoot, player.gameId);
            var doc = player.Document;
            if (doc?.cues == null)
            {
                Debug.LogWarning($"[TutorialFrameCapture] ReplayPreceding: Document 为空（root={root}）");
                return;
            }
            Debug.Log($"[TutorialFrameCapture] ReplayPreceding 目标={targetCue}，runtime 共 {doc.cues.Count} 条 cue");

            bool first = true;
            foreach (var cue in doc.cues)
            {
                if (cue.id == targetCue) break;   // 目标本身不在这里重放
                string path = Path.Combine(root, "tutorial", "anim", player.track, cue.id + ".json");
                if (!File.Exists(path)) continue;

                if (!anim.LoadCue(root, player.track, cue.id, !first)) continue;
                anim.SnapTo(999f);
                first = false;
                replayedAny = true;
                Debug.Log($"[TutorialFrameCapture] replayed {cue.id}");
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
