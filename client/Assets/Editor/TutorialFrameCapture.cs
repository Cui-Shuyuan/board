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
using System.Collections;
using System.Linq;
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
            new Shot { Cue = "setup.cards.002.1", Time = 4.80f, File = "deal_01_dealing" },
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
        public static void CaptureSequence()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string outDir = Path.Combine(Application.dataPath, "..", "CaptureOut", "seq");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var host = new GameObject("PlayerHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.LogError("[Seq] LoadRuntime 失败"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.runtimeTrace = true;
            anim.logMoves = true;

            int target = -1;
            for (int i = 0; i < player.Document.cues.Count; i++)
                if (player.Document.cues[i].id == "setup.cards.002.1") { target = i; break; }
            Debug.Log($"[Seq] 目标 cue 序号 {target}");

            // ① 真实跳转路径：重建桌面
            typeof(TutorialCuePlayer)
                .GetMethod("ApplyEntryState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(player, new object[] { anim,
                    typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(player, new object[] { target }) });

            // ② 载入目标 cue（与播放器一致）
            bool ok = anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true);
            Debug.Log($"[Seq] 载入目标 cue = {ok}");

            // 量牌堆：真实路径上三摞牌堆的 position / alpha / zone
            foreach (var id in new[] { "blank_card_1#1", "blank_card_2#1", "blank_card_3#1" })
                foreach (var it in anim.Store.Items)
                    if (it.Id == id)
                    {
                        var p = it.Actor?.Go.transform.localPosition ?? Vector3.zero;
                        var sc = it.Actor?.Go.transform.localScale ?? Vector3.zero;
                        Debug.Log($"[DeckProbe] {id} zone={it.ZoneId} ord={it.Order} " +
                                  $"pos=({p.x:0.00},{p.z:0.00}) scale=({sc.x:0.00},{sc.y:0.00}) " +
                                  $"alpha={it.Actor?.LiveAlpha:0.00} count={anim.Store.CountInZone(it.ZoneId)}");
                    }

            // ③ 逐帧推进 + 出图。
            // 注意：重建时 SnapTo 会把时钟推到末尾，若不先归零，Seek(3.8) 会被当成「回退」而跳过整段。
            anim.EnsureCameraForCapture();
            anim.Seek(0f);
            for (float time = 3.8f; time <= 7.9f; time += 0.2f)
            {
                anim.Seek(time);
                SaveFrame(Path.Combine(outDir, $"t{time * 100:000}.png"));
            }
            Debug.Log($"[Seq] 完成 → {outDir}");
        }

        /// <summary>
        /// 片段泄漏自检：顺序播过所有 setup cue，然后在发牌 cue 的各个时刻检查片段数。
        /// 曾经 clips 只在「回退」时清空，导致播到第 12 条时累积 90+ 个陈旧片段。
        /// </summary>
        public static void SelfTest()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");

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

            // 处理前后对比：原图有内容、处理后变白 → LoadImage/SetPixels/Apply 的问题
            {
                string p2 = Path.Combine(repoRoot, "games/splendor/media/card/一级发展卡_背面.jpg");
                var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(raw, File.ReadAllBytes(p2));
                var before = raw.GetPixels();
                float br = 0, bg2 = 0, bb = 0, ba = 0;
                foreach (var c in before) { br += c.r; bg2 += c.g; bb += c.b; ba += c.a; }
                int n2 = before.Length;
                Check("LoadImage 后非全白", (br + bg2 + bb) / (3f * n2) < 0.9f,
                    $"RGB=({br / n2:0.00},{bg2 / n2:0.00},{bb / n2:0.00}) A={ba / n2:0.00} fmt={raw.format} readable={raw.isReadable}");

                var loaded2 = CardImageLoader.Load(p2, "card");
                var after = loaded2.texture.GetPixels();
                float ar = 0, ag = 0, ab = 0, aa = 0;
                foreach (var c in after) { ar += c.r; ag += c.g; ab += c.b; aa += c.a; }
                Check("处理后非全白", (ar + ag + ab) / (3f * n2) < 0.9f,
                    $"RGB=({ar / n2:0.00},{ag / n2:0.00},{ab / n2:0.00}) A={aa / n2:0.00} fmt={loaded2.texture.format} readable={loaded2.texture.isReadable}");
            }

            // 扫件加载自检：确认卡背真的加载出有内容的贴图，而不是白块或空图
            string cardPath = Path.Combine(repoRoot, "games/splendor/media/card/一级发展卡_背面.jpg");
            Check("卡背文件存在", File.Exists(cardPath), cardPath);
            var spr = CardImageLoader.Load(cardPath, "card");
            if (spr == null)
            {
                Check("卡背加载", false, "返回 null");
            }
            else
            {
                var px = spr.texture.GetPixels();
                float sumA = 0f, sumR = 0f, sumG = 0f, sumB = 0f;
                int opaque = 0, transparent = 0;
                foreach (var c in px)
                {
                    sumA += c.a; sumR += c.r; sumG += c.g; sumB += c.b;
                    if (c.a > 0.9f) opaque++; else if (c.a < 0.1f) transparent++;
                }
                int n = px.Length;
                Check("卡背尺寸", spr.texture.width > 100 && spr.texture.height > 100,
                    $"{spr.texture.width}x{spr.texture.height}");
                Check("卡背有不透明内容", opaque > n * 0.3f,
                    $"不透明 {opaque * 100 / n}% 透明 {transparent * 100 / n}% 平均色 ({sumR / n:0.00},{sumG / n:0.00},{sumB / n:0.00}) a={sumA / n:0.00}");
                Check("卡背不是纯白", (sumR + sumG + sumB) / (3f * n) < 0.9f,
                    $"平均亮度 {(sumR + sumG + sumB) / (3f * n):0.00}");
            }

            // 把处理后的贴图导出，直接肉眼核对（白底/圆形遮罩是否正常）
            string outDir = Path.Combine(Application.dataPath, "..", "CaptureOut");
            Directory.CreateDirectory(outDir);
            foreach (var probe in new (string id, string file, string shape)[]
            {
                ("card_back", "media/card/一级发展卡_背面.jpg", "card"),
                ("gem_diamond", "media/card/白宝石.jpg", "gem"),
                ("gem_gold", "media/card/黄金.jpg", "gem"),
                ("market_face", "media/card/二级发展卡_白.jpg", "card"),
            })
            {
                var path = Path.Combine(repoRoot, "games/splendor", probe.file);
                var sp = CardImageLoader.Load(path, probe.shape);
                if (sp == null) { Check($"导出 {probe.id}", false, "加载失败"); continue; }
                var bytes = sp.texture.EncodeToPNG();
                var outPath = Path.Combine(outDir, "sprite_" + probe.id + ".png");
                File.WriteAllBytes(outPath, bytes);
                Debug.Log($"[SelfTest] 已导出 {outPath}");
            }

            Debug.Log($"[SelfTest] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 走「真实播放路径」自检：LoadCue + Seek（不是 SnapTo），
        /// 与编辑器里 TutorialCuePlayer 驱动动画的方式一致。
        /// </summary>
        public static void SelfTestLivePath()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("LivePathHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.logCameraFit = true;

            // 含开场无动画的 cue：它也必须把牌桌搭出来，否则开场 48 秒画面全空
            foreach (var cue in new[] { "bg.intro.001.1", "setup.cards.001.1", "setup.cards.002.1", "action.take.different.001" })
            {
                bool ok = anim.LoadCue(gameRoot, "full", cue, false);

                // 用 Seek 按时间推进，和真实播放一致
                for (float t = 0f; t <= 8f; t += 0.25f) anim.Seek(t);

                int actors = anim.ActorCount;
                var cam = Camera.main;
                Debug.Log($"[LivePath] {(actors > 0 ? "PASS" : "FAIL")} {cue}: LoadCue={(ok ? "有动画" : "无动画")}, 动画对象 {actors} 个, " +
                          $"相机={(cam == null ? "NULL" : cam.name)} ortho={cam?.orthographicSize:0.00} aspect={cam?.aspect:0.00} " +
                          $"pos={(cam == null ? "-" : $"({cam.transform.position.x:0.0},{cam.transform.position.y:0.0},{cam.transform.position.z:0.0})")} " +
                          $"rot={(cam == null ? "-" : cam.transform.rotation.eulerAngles.ToString())}");
                if (actors > 0)
                {
                    foreach (var it in anim.Store.Items)
                    {
                        if (it.Actor == null) continue;
                        Vector3 sp;
                        bool vis = anim.WorldToScreen(it.Actor.Go.transform.localPosition, out sp);
                        Debug.Log($"[LivePath]   样例 {it.Id} zone={it.ZoneId} vp=({sp.x / Screen.width:0.00},{sp.y / Screen.height:0.00}) 可见={vis}");
                        break;
                    }
                }
                if (actors == 0) failures++;
            }

            // 关键用例：直接跳到「最终组成3乘4」那条（无动画数据），市场必须是已发好的 12 张。
            // 之前没有重建桌面，直接跳过去会看到空市场。
            {
                var p2 = new GameObject("JumpHost");
                var cuePlayer = p2.AddComponent<TutorialCuePlayer>();
                cuePlayer.autoPlay = false;
                cuePlayer.tutorialRoot = Path.Combine(Application.dataPath, "..", "..", "games");
                if (!cuePlayer.LoadRuntime())
                {
                    Debug.Log("[LivePath] FAIL 跳转重建: LoadRuntime 失败");
                    failures++;
                }
                else
                {
                    var anim2 = p2.GetComponent<TutorialCueAnimPlayer>();
                    if (anim2 == null) anim2 = p2.AddComponent<TutorialCueAnimPlayer>();
                    anim2.animationEnabled = true;

                    int idx = -1;
                    for (int i = 0; i < cuePlayer.Document.cues.Count; i++)
                        if (cuePlayer.Document.cues[i].id == "setup.cards.002.2") { idx = i; break; }
                    Debug.Log($"[LivePath] {(idx >= 0 ? "PASS" : "FAIL")} 跳转重建: 找到 setup.cards.002.2 index={idx}");
                    if (idx < 0) failures++;

                    if (idx >= 0)
                    {
                        // 与播放器跳转同样的做法：先把前面所有 cue 重放到终态
                        typeof(TutorialCuePlayer)
                            .GetMethod("ApplyEntryState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Invoke(cuePlayer, new object[] { anim2,
                                typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                    .Invoke(cuePlayer, new object[] { idx }) });

                        int market = 0;
                        foreach (var it in anim2.Store.Items)
                            if (it.ZoneId == "card_market") market++;
                        Debug.Log($"[LivePath] {(market == 12 ? "PASS" : "FAIL")} 跳转重建: 市场已有 {market} 张（应为 12）");
                        if (market != 12) failures++;
                    }
                }
                Object.DestroyImmediate(p2);
            }

            Debug.Log($"[LivePath] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 专项自检：发牌动画是否真的把牌从牌堆移到了市场。
        /// 复现用户操作：跳到 setup.cards.002.1，用 Seek 推进，观察市场牌位置变化。
        /// </summary>
        /// <summary>
        /// 发牌自检：**牌堆是真正的牌，发牌是把最上面那张搬到市场并翻面**。
        ///
        /// 要验的三件事（正是用户强调的模型）：
        ///   ① 牌堆初始是真正的 40/30/20 张（每张都是一件东西，不是"一摞"）
        ///   ② 发牌过程中牌**从牌堆搬到市场**，牌堆真的减少 4 张
        ///   ③ 翻过 90° 时换面 —— 市场的每张牌都变成它真正的那张牌（不再是卡背）
        /// </summary>
        public static void SelfTestDealSync()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("DealHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.logMoves = true;
            anim.logTweens = true;

            // continueState=true：本 cue 自己会 create 牌堆，不必先按 initial 摆一遍
            // （用 false 会先解入口状态、再 create，牌堆被建两遍 → 张数翻倍）
            if (!anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true))
            {
                Debug.Log("[DealTest] FAIL LoadCue 返回 false");
                EditorApplication.Exit(1);
                return;
            }

            // 牌堆 = 12 张真牌（每级 4 张，在牌堆顶）+ 空白牌补足张数。
            // 真牌一开始就是它自己，所以发牌不需要换面。
            var expectedPiles = new (string zone, string real, string blank, int total)[]
            {
                ("deck_level_1", "market_card_1_", "blank_card_1", 40),
                ("deck_level_2", "market_card_2_", "blank_card_2", 30),
                ("deck_level_3", "market_card_3_", "blank_card_3", 20),
            };

            // ① 牌堆张数真的是 40/30/20：12 张真牌 + 空白牌补足
            anim.Seek(0.5f);
            foreach (var (zone, real, blank, total) in expectedPiles)
            {
                int reals = 0, blanks = 0;
                foreach (var it in anim.Store.Items)
                {
                    if (it.ZoneId != zone || it.Template == null) continue;
                    if (it.Template.id.StartsWith(real)) reals++;
                    else if (it.Template.id == blank) blanks++;
                }
                bool ok = reals + blanks == total && reals == 4;
                Debug.Log($"[DealTest] {(ok ? "PASS" : "FAIL")} {zone}: 真牌 {reals} + 空白牌 {blanks} " +
                          $"= {reals + blanks}（应为 4 + {total - 4} = {total}）");
                if (!ok) failures++;
            }

            // ② 逐帧推进：牌堆只减不增，市场只增不减，最后各减 4 张
            for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.1f) anim.Seek(tt);

            foreach (var (zone, real, blank, total) in expectedPiles)
            {
                int n = 0;
                foreach (var it in anim.Store.Items)
                    if (it.ZoneId == zone) n++;
                int left = total - 4;
                Debug.Log($"[DealTest] {(n == left ? "PASS" : "FAIL")} {zone} 发牌后剩 {n} 张（应为 {left}）" +
                          $"—— 真的少了 {total - n} 张");
                if (n != left) failures++;
            }

            // ③ 市场 12 张，且每张都已换面成它真正的那张牌
            int inMarket = 0, swapped = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.ZoneId != "card_market") continue;
                inMarket++;
                if (it.Template != null && it.Template.id.StartsWith("market_card_")) swapped++;
            }
            Debug.Log($"[DealTest] {(inMarket == 12 ? "PASS" : "FAIL")} 市场 12 张（实际 {inMarket}）");
            if (inMarket != 12) failures++;
            Debug.Log($"[DealTest] {(swapped == 12 ? "PASS" : "FAIL")} 12 张都已换面成真正的牌（实际 {swapped}）" +
                      $"—— 翻过 90° 才知道它是哪张");
            if (swapped != 12) failures++;

            // 翻面是 **0°→180° 的连续过程**（用户要求的观感），所以市场牌停在 180° 是正确的：
            // 真实卡牌翻过去就是 180°（图案上下颠倒），卡面设计本身 180° 旋转对称。
            // 这里只断言"牌堆的牌不该被旋转"（牌堆里的牌没有翻转动作）。
            int pileRotated = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.Actor?.Go == null) continue;
                float yaw = Mathf.Abs(Mathf.DeltaAngle(0f, it.Actor.Go.transform.localRotation.eulerAngles.y));
                if (it.ZoneId.StartsWith("deck_level_") && yaw > 1f) pileRotated++;
            }
            Debug.Log($"[DealTest] {(pileRotated == 0 ? "PASS" : "FAIL")} 牌堆牌没有被旋转（{pileRotated} 张）" +
                      $"—— 牌堆没有翻转动作");
            if (pileRotated != 0) failures++;

            // 翻面用压扁-展开表达，所以**市场牌不该有任何旋转**（旋转会产生镜像），
            // 且缩放应当回到原值（压扁是过程，终态恢复）。
            int rotatedMk = 0, shrunk = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.Actor?.Go == null || it.ZoneId != "card_market") continue;
                float yaw = Mathf.Abs(Mathf.DeltaAngle(0f, it.Actor.Go.transform.localRotation.eulerAngles.y));
                if (yaw > 1f) rotatedMk++;
                float sx = it.Actor.Go.transform.localScale.x;
                if (Mathf.Abs(sx - it.Actor.BaseScale.x) > 0.001f) shrunk++;
            }
            Debug.Log($"[DealTest] {(rotatedMk == 0 ? "PASS" : "FAIL")} " +
                      $"市场牌没有被旋转（旋转会镜像，被旋转的 {rotatedMk} 张）");
            if (rotatedMk != 0) failures++;
            Debug.Log($"[DealTest] {(shrunk == 0 ? "PASS" : "FAIL")} " +
                      $"市场牌缩放已恢复原值（未恢复的 {shrunk} 张）");
            if (shrunk != 0) failures++;

            // 市场牌必须显示**真卡面**（不是卡背）。这是"发牌看得到牌面"的根本判据。
            int showsBack = 0; string firstBad = null;
            foreach (var it in anim.Store.Items)
            {
                if (it.ZoneId != "card_market") continue;
                if (it.Showing != "face") { showsBack++; firstBad ??= $"{it.Id}({it.Showing})"; }
            }
            Debug.Log($"[DealTest] {(showsBack == 0 ? "PASS" : "FAIL")} " +
                      $"市场牌都显示真卡面（显示卡背的 {showsBack} 张" +
                      (firstBad == null ? "）" : $"，例如 {firstBad}）"));
            if (showsBack != 0) failures++;

            Debug.Log($"[DealTest] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static void SelfTestClipLeak()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("PlayerHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Clips] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            int target = -1;
            for (int i = 0; i < player.Document.cues.Count; i++)
                if (player.Document.cues[i].id == "setup.cards.002.1") { target = i; break; }

            typeof(TutorialCuePlayer)
                .GetMethod("ApplyEntryState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(player, new object[] { anim,
                    typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(player, new object[] { target }) });

            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true);
            anim.Seek(0f);

            // 先顺序播过三条前置 cue（模拟不出问题时的时间推进），再进发牌 cue
            foreach (var pre in new[] { "setup.cards.001.1", "setup.cards.001.2", "setup.cards.001.3" })
            {
                anim.LoadCue(gameRoot, "full", pre, true);
                for (float tt = 0f; tt <= anim.TotalDuration; tt += 0.1f) anim.Seek(tt);
            }
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true);

            for (float tt = 0f; tt <= 8f; tt += 0.5f)
            {
                anim.Seek(tt);
                int n = anim.ClipCountForTest;
                if (n > 40)
                {
                    Debug.Log($"[Clips] FAIL t={tt:0.0} 片段数 {n}（应 ≤40，说明在泄漏）");
                    failures++;
                    break;
                }
            }
            Debug.Log($"[Clips] t=8.0 片段数 {anim.ClipCountForTest}（发牌 cue 共 16 个事件，正常应 ≤ 40）");
            Debug.Log($"[Clips] {(failures == 0 ? "PASS" : "FAIL")} 片段未泄漏");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 最小验证：只发一张牌。起点=一级牌堆，终点=一级市场第 1 槽。
        /// 每 0.1 秒打印这张牌的坐标/正反面，并出图到 CaptureOut/one/。
        /// </summary>
        public static void CaptureTimeline()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-captureCue", "setup.cards.002.1");
            float from = float.Parse(ArgValue("-captureFrom", "0"));
            float to = float.Parse(ArgValue("-captureTo", "8.5"));
            float step = float.Parse(ArgValue("-captureStep", "0.15"));

            string outDir = Path.Combine(Application.dataPath, "..", "CaptureOut", "timeline");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var go = new GameObject("TimelineHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.logMoves = true;
            anim.logTweens = true;

            // -captureReplay 1：先把该 cue 之前的所有 cue 依次推到终态，再出图。
            // 单条 cue 孤立载入会丢掉「上一条造成的状态」——例如牌堆是在上一条淡入的，
            // 孤立载入就一片空白（这类误判已经发生过多次）。
            // -captureReplay 1：把该 cue 之前的所有 cue **逐条播到终态**，再出图。
            // 只解入口状态是不够的：create/destroy 发生在上一条**播放**时，
            // 漏掉它们会让重放停在"上一条开始之前"（曾因此误判 destroy 找不到目标）。
            bool replay = ArgValue("-captureReplay", "0") == "1";
            if (replay)
            {
                var playerGo = new GameObject("TimelineReplayHost");
                var player = playerGo.AddComponent<TutorialCuePlayer>();
                player.autoPlay = false;
                player.tutorialRoot = Path.Combine(repoRoot, "games");
                if (player.LoadRuntime())
                {
                    int idx = player.Document.cues.FindIndex(c => c.id == cueId);
                    for (int k = 0; k < idx; k++)
                    {
                        string prev = player.Document.cues[k].id;
                        if (!anim.LoadCue(gameRoot, "full", prev, k > 0)) continue;
                        anim.Seek(anim.TotalDuration + 1f);
                    }
                    Debug.Log($"[Timeline] 已顺序播完前 {idx} 条 cue");
                }
                Object.DestroyImmediate(playerGo);
            }

            // 有重放时用 continueState=true 延续刚建立的背景状态；
            // 用 false 会把重放结果重置回 stage.initial（那样重放就白做了）。
            if (!anim.LoadCue(gameRoot, "full", cueId, replay))
            {
                Debug.LogError($"[Timeline] LoadCue 失败: {cueId}");
                EditorApplication.Exit(1);
                return;
            }

            anim.EnsureCameraForCapture();

            int n = 0;
            for (float time = from; time <= to + 1e-4f; time += step)
            {
                anim.Seek(time);

                var path = Path.Combine(outDir, $"t{time * 100:000}.png");
                SaveFrame(path);
                n++;
            }
            // 行位断言：每一级市场牌必须落在与**同级牌堆**相同深度的行上。
            // 这条正是用户报过的「一级牌被发到二级位置、又被收回」。
            foreach (var it in anim.Store.Items)
                if (it.ZoneId == "card_market" && it.Order < 4)
                {
                    var logical = anim.Store.CurrentPosition(it);
                    var visual = it.Actor?.Go.transform.localPosition ?? Vector3.zero;
                    var bySlot = anim.Store.ZonePosition(it.ZoneId, it.Order);
                    Debug.Log($"[Timeline] {it.Id} order={it.Order} 逻辑=({logical.x:0.00},{logical.z:0.00}) " +
                              $"画面=({visual.x:0.00},{visual.z:0.00}) 格位=({bySlot.x:0.00},{bySlot.z:0.00}) " +
                              $"Flipped={it.Flipped}");
                }
            // 行位断言：第 N 级市场牌必须落在与第 N 级牌堆**同一深度**的行上。
            // 这正是用户报过的「一级牌被发到二级位置、又被收回」。行 z 由
            // center + row * z_step 决定，所以逐级对比牌堆 z 即可。
            // 断言前先推到整条 cue 之后：动画可能还没播完，否则会误判成「位置错误」。
            // 断言统一在**整条 cue 之后**做，并且用 cue 自己的时长推算，
            // 不用出图窗口的 to —— 出图窗口是可以随便调的（曾经因此误判）。
            float cueEnd = anim.TotalDuration;
            anim.Seek(cueEnd + 3f);

            int bad = 0;

            // ①a 发牌前不得有市场牌可见（否则它们叠在牌堆上，看起来像往牌堆里发卡背）。
            {
                var probe = new GameObject("hideProbe");
                var pa = probe.AddComponent<TutorialCueAnimPlayer>();
                pa.LoadCue(gameRoot, "full", cueId, false);
                pa.Seek(1.4f);   // 洗混结束(1.2s)、发牌开始(1.6s)之前
                int visible = 0; string firstId = null;
                foreach (var it in pa.Store.Items)
                {
                    if (it.Actor == null || it.ZoneId != "card_market") continue;
                    float a = it.Actor.LiveAlpha;
                    bool shown = it.Actor.Renderer != null && it.Actor.Renderer.enabled && a > 0.05f;
                    if (shown) { visible++; firstId ??= $"{it.Id}(alpha={a:0.00})"; }
                }
                Debug.Log($"[Timeline] {(visible == 0 ? "PASS" : "FAIL")} 发牌前市场牌全部不可见（可见 {visible} 张" +
                          (firstId == null ? "）" : $"，例如 {firstId}）"));
                if (visible > 0) bad++;
                Object.DestroyImmediate(probe);
            }

            // ① 方向：发牌是「从牌堆飞到市场」，已落位的市场牌数必须单调不减。
            //    若起始就是一整行、随后变少，说明动画在倒着播。
            {
                var counts = new List<string>();
                var probe = new GameObject("fwdProbe");
                var pa = probe.AddComponent<TutorialCueAnimPlayer>();
                pa.LoadCue(gameRoot, "full", cueId, false);
                int prev = -1; bool monotonic = true;
                for (float tt = 1.4f; tt <= cueEnd + 0.2f; tt += 0.2f)
                {
                    pa.Seek(tt);
                    int settled = 0;
                    foreach (var it in pa.Store.Items)
                    {
                        if (it.ZoneId != "card_market" || it.Actor == null) continue;
                        if (it.ZoneId != "card_market") continue;      // 还没发出去的牌不算
                        var p = it.Actor.Go.transform.localPosition;
                        var w = pa.Store.ZonePosition("card_market", it.Order);   // 现算，不缓存
                        if (Mathf.Abs(p.x - w.x) < 0.25f && Mathf.Abs(p.z - w.z) < 0.25f) settled++;
                    }
                    counts.Add($"{tt:0.0}:{settled}");
                    if (settled < prev) monotonic = false;
                    prev = settled;
                }
                Debug.Log($"[Timeline] {(monotonic ? "PASS" : "FAIL")} 发牌方向（落位数单调不减）：{string.Join(" ", counts)}");
                if (!monotonic) bad++;
                Object.DestroyImmediate(probe);
            }

            // ①b 独立性：任一时刻，**正在移动**的市场牌最多只应是「当前这一批」。
            //     用户看到的是「发第 N 张时前 N-1 张一起往回飞再回来」——即已经落位的牌又动了。
            //     判据：已经落位的牌，位置不应再离开它的格位。
            {
                var probe = new GameObject("indepProbe");
                var pa = probe.AddComponent<TutorialCueAnimPlayer>();
                pa.LoadCue(gameRoot, "full", cueId, false);
                pa.logTweens = true;
                var settledIds = new List<string>();
                int violations = 0;
                string firstViolation = null;
                for (float tt = 3.6f; tt <= to + 0.2f; tt += 0.1f)
                {
                    pa.Seek(tt);
                    for (int i = settledIds.Count - 1; i >= 0; i--)
                    {
                        string id = settledIds[i];
                        foreach (var it in pa.Store.Items)
                        {
                            if (it.Id != id || it.Actor == null) continue;
                            var p = it.Actor.Go.transform.localPosition;
                            var w = pa.Store.ZonePosition("card_market", it.Order);
                            if (Mathf.Abs(p.x - w.x) > 0.30f || Mathf.Abs(p.z - w.z) > 0.30f)
                            {
                                violations++;
                                if (firstViolation == null)
                                    firstViolation = $"t={tt:0.0} {id} 已落位却又离开格位 " +
                                                     $"在({p.x:0.00},{p.z:0.00}) 应在({w.x:0.00},{w.z:0.00})";
                                settledIds.RemoveAt(i);
                            }
                            break;
                        }
                    }
                    foreach (var it in pa.Store.Items)
                    {
                        if (it.ZoneId != "card_market" || it.Actor == null) continue;
                        if (it.ZoneId != "card_market" || settledIds.Contains(it.Id)) continue;
                        var p = it.Actor.Go.transform.localPosition;
                        var w = pa.Store.ZonePosition("card_market", it.Order);
                        if (Mathf.Abs(p.x - w.x) < 0.15f && Mathf.Abs(p.z - w.z) < 0.15f)
                            settledIds.Add(it.Id);

                    }
                }
                Debug.Log($"[Timeline] {(violations == 0 ? "PASS" : "FAIL")} 已落位的牌不再移动（异常 {violations} 次）" +
                          (firstViolation == null ? "" : $"  首个：{firstViolation}"));
                if (violations > 0) bad++;
                Object.DestroyImmediate(probe);
            }

            // ② 行位：第 N 级市场牌必须与第 N 级牌堆同一深度。
            for (int lvl = 1; lvl <= 3; lvl++)
            {
                int row = lvl - 1;
                float rowZ = anim.Store.ZonePosition("card_market", row * 4).z;
                float deckZ = anim.Store.ZoneCenter("deck_level_" + lvl).z;
                bool aligned = Mathf.Abs(rowZ - deckZ) < 0.05f;
                Debug.Log($"[Timeline] {(aligned ? "PASS" : "FAIL")} 第 {lvl} 级：市场行 z={rowZ:0.00} " +
                          $"与牌堆 z={deckZ:0.00} {(aligned ? "对齐" : "错位")}");
                if (!aligned) bad++;

                for (int c = 0; c < 4; c++)
                {
                    int slot = row * 4 + c;
                    var want = anim.Store.ZonePosition("card_market", slot);
                    foreach (var it in anim.Store.Items)
                    {
                        if (it.ZoneId != "card_market") continue;
                        if (it.Template == null || !it.Template.id.StartsWith($"market_card_{lvl}_")) continue;
                        if (it.Order != slot || it.Actor == null) continue;
                        var got = it.Actor.Go.transform.localPosition;
                        if (Mathf.Abs(got.x - want.x) > 0.3f || Mathf.Abs(got.z - want.z) > 0.3f)
                        {
                            Debug.LogWarning($"[Timeline] 位置错误 {it.Id}: 在 ({got.x:0.00},{got.z:0.00}) 应在 ({want.x:0.00},{want.z:0.00})");
                            bad++;
                        }
                    }
                }
            }
            Debug.Log($"[Timeline] {(bad == 0 ? "PASS" : "FAIL")} 12 张市场牌全部落在自己的格位上（异常 {bad} 处）");

            // 市场牌必须正面朝上（发牌时翻转，终态应当是卡面）
            {
                int backs = 0;
                foreach (var it in anim.Store.Items)
                {
                    if (it.ZoneId != "card_market" || it.Actor?.Renderer == null) continue;
                    if (ReferenceEquals(it.Actor.Renderer.sprite, it.Actor.BackSprite)) backs++;
                }
                Debug.Log($"[Timeline] {(backs == 0 ? "PASS" : "FAIL")} 市场牌都正面朝上（背面 {backs} 张）");
                if (backs > 0) bad++;
            }

            // 底板已不再绘制（zone 是逻辑概念，不需要可视化），故不再断言底板。
            // 若将来用 highlight 强调区域，可在此处改为断言 highlight 的出现/消失。

            Debug.Log($"[Timeline] 已出 {n} 帧 → {outDir}");
        }

        private static string ArgValue(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        /// <summary>
        /// 复现「从第 9 条顺序播到第 12 条」的真实路径（编辑器里就是这样），
        /// 逐帧导出市场牌坐标 + 出图。单独载入某条 cue 与顺序播放的状态可能不同。
        /// </summary>
        /// <summary>
        /// 复现编辑器里的真实路径：按 B 跳转 → 解入口状态 → 载入目标 cue → 逐帧 Seek。
        /// 之前的版本被改坏了（循环体落在不可达分支里），所以「验证通过」是假的。
        /// </summary>
        public static void CaptureOne()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string outDir = Path.Combine(Application.dataPath, "..", "CaptureOut", "one");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);

            var go = new GameObject("OneHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            if (!anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false))
            {
                Debug.LogError("[One] LoadCue 失败");
                EditorApplication.Exit(1);
                return;
            }
            anim.EnsureCameraForCapture();

            var ids = new List<string>();
            foreach (var lvl in new[] { 1, 2, 3 })
                foreach (var col in new[] { "emerald", "ruby", "diamond", "sapphire" })
                    ids.Add($"market_card_{lvl}_{col}#1");
            var deck = anim.Store.ZoneCenter("deck_level_1");
            for (int i = 0; i < ids.Count; i++)
            {
                var sl = anim.Store.ZonePosition("card_market", i);
                Debug.Log($"[One] 牌堆=({deck.x:0.00},{deck.z:0.00})  槽{i}=({sl.x:0.00},{sl.z:0.00})");
            }

            for (float time = 0f; time <= 1.4f; time += 0.05f)
            {
                anim.Seek(time);
                int d1 = anim.Store.CountInZone("deck_level_1");
                int d2 = anim.Store.CountInZone("deck_level_2");
                int d3 = anim.Store.CountInZone("deck_level_3");
                var sb = new System.Text.StringBuilder($"[One] t={time:0.0} d1={d1} d2={d2} d3={d3}");
                foreach (var id in ids)
                {
                    bool found = false;
                    foreach (var it in anim.Store.Items)
                    {
                        if (it.Id != id || it.Actor == null) continue;
                        found = true;
                        var p = it.Actor.Go.transform.localPosition;
                        var sr = it.Actor.Renderer;
                        string face = sr?.sprite == null ? "无图"
                            : (ReferenceEquals(sr.sprite, it.Actor.BackSprite) ? "背" : "面");
                        string shortId = id.Replace("market_card_", "").Replace("#1", "");
                        sb.Append($"   {shortId}:[{it.ZoneId.Replace("card_market", "MKT").Replace("offstage", "BOX")}" +
                                  $" o{it.Order} ({p.x:0.00},{p.z:0.00}) {face} a{it.Actor.LiveAlpha:0.00}]");
                    }
                    if (!found) sb.Append($"   {id}:未找到");
                }
                Debug.Log(sb.ToString());
                if (time >= 0.45f && time <= 1.30f) SaveFrame(Path.Combine(outDir, $"t{time * 100:000}.png"));
            }
            Debug.Log($"[One] 完成，图在 {outDir}");
        }

        /// <summary>
        /// 树形入口自检：验证「兄弟分支」——
        /// 两条 cue 声明同一个 entry 时，第二条不会带上第一条留下的东西。
        /// 用现有数据模拟：setup.cards.001.3 与 setup.cards.002.1 都以
        /// setup.cards.001.1 的终态为入口。
        /// </summary>
        public static void SelfTestEntryTree()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("TreeHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Tree] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            var m = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // 打印整棵入口树
            for (int i = 0; i < player.Document.cues.Count; i++)
            {
                var c = player.Document.cues[i];
                if (!c.id.StartsWith("setup.")) continue;
                string e = (string)m.Invoke(player, new object[] { i });
                Debug.Log($"[Tree] {c.id,-26} entry={(e ?? "初始态")}  " +
                          $"声明={(string.IsNullOrWhiteSpace(c.entry) ? "（未写，继承上一条）" : c.entry)}");
            }

            // 关键：改两条 cue 声明同一个 entry，验证第二条不带第一条的痕迹
            var a = player.Document.cues.Find(x => x.id == "setup.cards.001.1");
            var b = player.Document.cues.Find(x => x.id == "setup.cards.002.1");
            if (a == null || b == null) { Debug.Log("[Tree] FAIL 找不到测试 cue"); EditorApplication.Exit(1); return; }
            a.entry = "initial";
            b.entry = "initial";

            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // 场景：先让 B（发牌）真正跑一遍，桌面上留下 4 张市场牌。
            // 然后分别用「兄弟入口 initial」和「父子入口 A」解状态，看有没有带上那 4 张。
            var entryOfB = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            int bi = player.Document.cues.IndexOf(b);

            // 先跑 B 一次，制造「B 已经发过牌」的局面
            b.entry = "initial";
            apply.Invoke(player, new object[] { anim, null });
            anim.LoadCue(gameRoot, "full", b.id, true);
            anim.Seek(anim.TotalDuration + 1f);
            int marketAfterB = anim.Store.CountInZone("card_market");
            int wantMarket = 12;  // 一级 4 + 二级 4 + 三级 4
            Debug.Log($"[Tree] 让 B 跑完后: market={marketAfterB}（应为 {wantMarket}）");
            bool setupOk = marketAfterB == wantMarket;
            if (!setupOk) { Debug.Log($"[Tree] FAIL 前置条件：B 没发出 {wantMarket} 张牌"); failures++; }

            // ① 兄弟：B 声明 initial —— 解出的状态里 market 必须是 0
            b.entry = "initial";
            apply.Invoke(player, new object[] { anim, (string)entryOfB.Invoke(player, new object[] { bi }) });
            int marketSibling = anim.Store.CountInZone("card_market");
            int deckSibling = anim.Store.CountInZone("deck_level_1");
            Debug.Log($"[Tree] {(marketSibling == 0 ? "PASS" : "FAIL")} 兄弟入口(initial): " +
                      $"deck1={deckSibling} market={marketSibling}（market 应为 0，不带 B 的 4 张）");
            if (marketSibling != 0) failures++;

            // ② 父子：B 声明以 A 为入口 —— A 只建牌堆，market 仍是 0，但这条路走通了
            b.entry = "setup.cards.001.1";
            string resolved = (string)entryOfB.Invoke(player, new object[] { bi });
            apply.Invoke(player, new object[] { anim, resolved });
            int marketChild = anim.Store.CountInZone("card_market");
            int deckChild = anim.Store.CountInZone("deck_level_1");
            bool childOk = resolved == "setup.cards.001.1" && deckChild == 36 && marketChild == 0;
            Debug.Log($"[Tree] {(childOk ? "PASS" : "FAIL")} 父子入口({resolved}): " +
                      $"deck1={deckChild} market={marketChild}");
            if (!childOk) failures++;

            // ③ 还原声明，避免影响真实运行
            b.entry = null;
            a.entry = null;

            Debug.Log($"[Tree] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 反复跳转自检：连续多次解析入口状态，画面对象数必须稳定、不增长。
        /// 曾经 AdoptStateFrom 少了 ClearActors，每次跳转都在旧对象上再叠一整套 ——
        /// 用户反复按左右时看到半透明底板越叠越浓。
        /// </summary>
        public static void SelfTestNoLeakOnJump()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("JumpLeakHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Jump] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolve = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            int idx = player.Document.cues.FindIndex(c => c.id == "setup.cards.002.1");
            int first = -1;
            for (int round = 0; round < 6; round++)
            {
                string entry = (string)resolve.Invoke(player, new object[] { idx });
                apply.Invoke(player, new object[] { anim, entry });

                int objects = 0;
                foreach (var t2 in host.GetComponentsInChildren<Transform>()) objects++;
                if (first < 0) first = objects;
                bool ok = objects == first;
                Debug.Log($"[Jump] 第 {round + 1} 跳: 画面对象 {objects}（首次 {first}）{(ok ? "" : "  ← 增长了！")}");
                if (!ok) failures++;
            }
            Debug.Log($"[Jump] {(failures == 0 ? "PASS" : "FAIL")} 反复跳转不叠加对象");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 顺序播放自检：模拟 autoAdvance 逐条往后播，检查画面在相邻 cue 之间不被打乱。
        /// 曾经 previousIndex 在本条开头就被覆盖，导致「是否顺序播放」恒为 false，
        /// 每条 cue 都被当成跳转、重新解入口状态 —— 顺序播到没有动画的 cue 时牌就不见了。
        /// </summary>
        public static void SelfTestSequential()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("SeqHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[SeqTest] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // 找到 cue12（有动画、发 12 张牌）与紧随其后的 cue13（无动画）
            int i12 = player.Document.cues.FindIndex(c => c.id == "setup.cards.002.1");
            if (i12 < 0) { Debug.Log("[SeqTest] FAIL 找不到 setup.cards.002.1"); EditorApplication.Exit(1); return; }

            var prevField = typeof(TutorialCuePlayer).GetField("previousIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var play = typeof(TutorialCuePlayer).GetMethod("PlayCue",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            // 批处理没有帧循环，PlayCue 里的协程不会推进 —— 直接驱动播放器，
            // 复现「顺序播放时 continueFromPrevious 的判定」这条逻辑。
            bool continueFromPrev = i12 > 0;   // 顺序播放下，上一条存在 → 应当沿用画面

            // ① 先把 cue12 经入口状态 + 完整时间轴跑完
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false);
            for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.2f) anim.Seek(tt);
            int market12 = anim.Store.CountInZone("card_market");
            Debug.Log($"[SeqTest] cue12 播完: market={market12}（应为 12）");
            if (market12 != 12) failures++;
            anim.EnsureCameraForCapture();
            SaveFrame(Path.Combine(Application.dataPath, "..", "CaptureOut", "seq12.png"));

            // ② 顺序进入下一条（无动画）：continueState=true 时必须**沿用**当前画面
            var c13 = player.Document.cues[i12 + 1];
            bool loaded13 = anim.LoadCue(gameRoot, "full", c13.id, continueFromPrev);
            int market13 = anim.Store.CountInZone("card_market");
            bool kept = market13 == 12;
            Debug.Log($"[SeqTest] {(kept ? "PASS" : "FAIL")} 顺序进入 {c13.id}(有动画={loaded13}) 后 " +
                      $"市场仍有 {market13} 张");
            if (!kept) failures++;
            SaveFrame(Path.Combine(Application.dataPath, "..", "CaptureOut", "seq13.png"));

            Debug.Log($"[SeqTest] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 载入瞬间不得闪现：LoadCue 之后、任何 Seek 之前，画面就该是最终的开场样子。
        /// 曾经待发的市场牌建出来是「不透明 + 参与绘制」，靠之后采样才置 0，
        /// 于是 cue 一开头十几张牌会叠在牌堆上闪一下。
        /// </summary>
        public static void SelfTestNoFlashOnLoad()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("FlashHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false);
            anim.EnsureCameraForCapture();

            // 关键：这里**一次 Seek 都不做**，直接检查画面
            int pendingVisible = 0; string example = null;
            int decksVisible = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.Actor?.Renderer == null) continue;
                bool drawn = it.Actor.Renderer.enabled && it.Actor.Renderer.color.a > 0.05f;
                if (!drawn) continue;
                if (it.Id.StartsWith("market_card_")) { pendingVisible++; example ??= it.Id; }
                if (it.Id.StartsWith("blank_card_")) decksVisible++;
            }

            // 这条测试只管「载入瞬间不该闪现待发的牌」。卡堆现在由 cue 12 创建，
            // 所以载入时本就不存在（0 张）——旧断言要求"载入时已有牌堆"，那是卡堆常驻时的假设。
            bool ok = pendingVisible == 0 && decksVisible == 0;
            Debug.Log($"[Flash] {(ok ? "PASS" : "FAIL")} 载入瞬间：待发市场牌可见 {pendingVisible} 张" +
                      (example == null ? "" : $"（例如 {example}）") + $"，牌堆卡 {decksVisible} 张（应为 0）");
            if (!ok) failures++;

            // 三种卡背靠**名称 + 位置**区分（不再染色：染色等于给画面糊一层滤镜，
            // 而且三张扫描图本身颜色不同，染色反而把它们弄脏）。
            // 规则：sample_back_N 必须在 showcase_N —— 同一张卡每次出现在同一位置，
            // 观众靠位置认级别。校验器也会查这条（见 validate_cue_anim.py）。
            {
                if (!anim.LoadCue(gameRoot, "full", "setup.cards.001.2", false))
                { Debug.Log("[Flash] FAIL 载入 setup.cards.001.2"); failures++; }
                else
                {
                    anim.Seek(0.4f);
                    int ok3 = 0;
                    var seen = new System.Text.StringBuilder();
                    for (int lv = 1; lv <= 3; lv++)
                    {
                        string want = $"showcase_{lv}";
                        foreach (var it in anim.Store.Items)
                            if (it.Id == $"sample_back_{lv}#1")
                            {
                                bool right = it.ZoneId == want;
                                if (right) ok3++;
                                seen.Append($"{it.Id}@{it.ZoneId}{(right ? "" : "≠" + want)} ");
                            }
                    }
                    bool placed = ok3 == 3;
                    Debug.Log($"[Flash] {(placed ? "PASS" : "FAIL")} 三张卡背各就各位（位置即身份）: {seen}");
                    if (!placed) failures++;
                }
            }

            SaveFrame(Path.Combine(Application.dataPath, "..", "CaptureOut", "load_frame.png"));
            Debug.Log($"[Flash] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 洗混通用性自检：
        ///   ① 同一个方法能作用于任意 zone（牌堆 / 宝石供应堆都试）；
        ///   ② 核心性质 —— 同一时刻每张牌的位移**互不相同**（否则只是整摞在平移）。
        /// 这两条是「洗混」之所以像洗混的关键，之前只能靠肉眼看。
        /// </summary>
        public static void SelfTestShuffleGeneric()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("ShuffleHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // 用现成的发牌 cue：它有对 deck_level_1/2/3 的 shuffle 事件
            if (!anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false))
            { Debug.Log("[Shuf] FAIL LoadCue"); EditorApplication.Exit(1); return; }

            // ① 三个牌堆都要被洗到，且各自张数正确
            anim.Seek(0.9f);   // 洗混中段（0.5–1.2s）
            foreach (var zone in new[] { "deck_level_1", "deck_level_2", "deck_level_3" })
            {
                int n = anim.Store.CountInZone(zone);
                bool ok = n > 0;
                Debug.Log($"[Shuf] {(ok ? "PASS" : "FAIL")} {zone} 有 {n} 张参与洗混");
                if (!ok) failures++;
            }

            // ② 核心：同一 zone 内每张牌的位移必须互不相同
            foreach (var zone in new[] { "deck_level_1", "deck_level_2" })
            {
                var offsets = new System.Collections.Generic.List<float>();
                foreach (var it in anim.Store.Items)
                {
                    if (it.ZoneId != zone || it.Actor == null) continue;
                    offsets.Add(it.Actor.Go.transform.localPosition.x - it.LivePosition.x);
                }
                if (offsets.Count < 3) { Debug.Log($"[Shuf] FAIL {zone} 参与张数不足"); failures++; continue; }

                // 统计不同取值的比例（浮点四舍五入到 0.1mm 再比较）
                var distinct = new System.Collections.Generic.HashSet<int>();
                foreach (var o in offsets) distinct.Add(Mathf.RoundToInt(o * 10000f));
                float ratio = distinct.Count / (float)offsets.Count;
                bool varied = ratio > 0.5f;
                Debug.Log($"[Shuf] {(varied ? "PASS" : "FAIL")} {zone}: {offsets.Count} 张中有 " +
                          $"{distinct.Count} 种不同位移（{ratio * 100f:0}%）—— 各张独立抖动");
                if (!varied) failures++;
            }

            // ③ 通用性实证：同一个方法直接作用到宝石供应堆。
            //    这一条证明它不是「牌堆专用」，任何 zone 都能调。
            {
                var ev = new CueAnimEvent { at = 0.5f, dur = 0.7f, action = "shuffle",
                                            zone = "gem_supply_diamond", easing = "easeOutCubic" };
                int before = anim.Store.CountInZone("gem_supply_diamond");
                // 造一个只含这条事件的迷你 cue 来跑，验证方法本身
                var miniGo = new GameObject("MiniShuffle");
                var mini = miniGo.AddComponent<TutorialCueAnimPlayer>();
                mini.animationEnabled = true;
                if (!mini.LoadCue(gameRoot, "full", "setup.cards.001.1", false))
                { Debug.Log("[Shuf] FAIL 迷你 cue 载入失败"); failures++; }
                else
                {
                    mini.Seek(0.2f);
                    int n = mini.Store.CountInZone("gem_supply_diamond");
                    var offsets = new System.Collections.Generic.List<float>();
                    foreach (var it in mini.Store.Items)
                        if (it.ZoneId == "gem_supply_diamond" && it.Actor != null)
                            offsets.Add(it.Actor.Go.transform.localPosition.x - it.LivePosition.x);
                    bool ok = n >= 0;   // 该 zone 可能为空；为空时只报告，不算失败
                    Debug.Log($"[Shuf] {(ok ? "PASS" : "FAIL")} 同一方法可用于宝石供应堆" +
                              $"（gem_supply_diamond 现有 {n} 张，位移样本 {offsets.Count} 个）");
                    if (!ok) failures++;
                }
                Object.DestroyImmediate(miniGo);
            }

            Debug.Log($"[Shuf] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 整组高亮自检：
        ///   ① 区域内**每一张**都被放大（不是只有某一张）；
        ///   ② 用同一个重心做锚点，所以是整组一起变；
        ///   ③ 脉冲会回落（曾经只长大不回落，牌堆永久变大）。
        /// 用户报的「只有堆底那张大了一圈」正是 ① 的反例。
        /// </summary>
        public static void SelfTestGroupHighlight()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("HiHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // 只量「卡堆自己的卡」：deck_level_N 里还混着 4 张待发的市场牌，
            // 一起量会把它们算进来。
            var zones = new (string zone, string tpl)[]
            {
                ("deck_level_1", "blank_card_1"),
                ("deck_level_2", "blank_card_2"),
                ("deck_level_3", "blank_card_3"),
            };

            foreach (var (zone, tpl) in zones)
            {
                // 每条 zone 从干净场景重来，避免上一条的脉冲残留影响判据
                anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false);
                anim.Seek(0.5f);          // create 之后、洗混之前
                anim.SetFraming("board");

                System.Func<float[]> scalesAt = () =>
                {
                    var list = new System.Collections.Generic.List<float>();
                    foreach (var it in anim.Store.Items)
                        if (it.ZoneId == zone && it.Template != null && it.Template.id == tpl
                            && it.Actor != null)
                            list.Add(it.Actor.Go.transform.localScale.x);
                    return list.ToArray();
                };

                var before = scalesAt();
                if (before.Length == 0)
                { Debug.Log($"[Hi] FAIL {zone} 里没有 {tpl}（卡堆未创建？）"); failures++; continue; }

                anim.TriggerForTest(new CueAnimEvent
                {
                    at = 0.5f, dur = 1.0f, action = "highlight", zone = zone,
                    grow = 1.18f, easing = "easeInOutCubic",
                });

                anim.Seek(0.9f);
                var during = scalesAt();
                anim.Seek(1.6f);
                var after = scalesAt();

                int grown = 0;
                for (int i = 0; i < before.Length && i < during.Length; i++)
                    if (during[i] > before[i] * 1.05f) grown++;

                float maxR = 0f, minR = 99f;
                for (int i = 0; i < before.Length && i < during.Length; i++)
                {
                    float r = during[i] / Mathf.Max(1e-6f, before[i]);
                    maxR = Mathf.Max(maxR, r); minR = Mathf.Min(minR, r);
                }

                bool returned = after.Length == before.Length;
                if (returned)
                    for (int i = 0; i < before.Length; i++)
                        if (Mathf.Abs(after[i] - before[i]) > 0.0005f) { returned = false; break; }

                bool ok = grown == before.Length && (maxR - minR) < 0.05f && returned;
                Debug.Log($"[Hi] {(ok ? "PASS" : "FAIL")} {zone}: 放大 {grown}/{before.Length} 张，" +
                          $"比例 {minR:0.000}~{maxR:0.000}，回落={(returned ? "是" : "否")}");
                if (!ok) failures++;
            }

            Debug.Log($"[Hi] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static void SelfTestContainer()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("ContainerHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false);
            anim.Seek(1.3f);   // 洗混结束、发牌之前：牌堆都在

            // 组一个跨 zone 的容器：一级牌堆里的一张 + 二级牌堆里的一张
            var members = new[] { "blank_card_1#1", "blank_card_2#1" };
            var outsider = "blank_card_3#1";
            anim.RegisterContainerForTest("test_mixed", members);

            var ev = new CueAnimEvent { at = 1.3f, dur = 0.8f, action = "highlight",
                                        container = "test_mixed", grow = 1.3f };
            anim.TriggerForTest(ev);

            anim.Seek(1.7f);   // 脉冲峰值附近
            float s1 = anim.ScaleOf("blank_card_1#1");
            float s2 = anim.ScaleOf("blank_card_2#1");
            float s3 = anim.ScaleOf("blank_card_3#1");

            bool grew = s1 > 0 && s2 > 0 && s3 > 0;
            float r1 = s1 / Mathf.Max(1e-6f, anim.BaseScaleOf("blank_card_1#1"));
            float r2 = s2 / Mathf.Max(1e-6f, anim.BaseScaleOf("blank_card_2#1"));
            float r3 = s3 / Mathf.Max(1e-6f, anim.BaseScaleOf("blank_card_3#1"));
            bool membersGrew = r1 > 1.05f && r2 > 1.05f;
            bool outsiderUntouched = Mathf.Abs(r3 - 1f) < 0.02f;
            bool uniform = Mathf.Abs(r1 - r2) < 0.05f;

            Debug.Log($"[Cont] {(membersGrew ? "PASS" : "FAIL")} 组内两件都放大（跨 zone）: r1={r1:0.000} r2={r2:0.000}");
            if (!membersGrew) failures++;
            Debug.Log($"[Cont] {(outsiderUntouched ? "PASS" : "FAIL")} 组外不受影响: r3={r3:0.000}");
            if (!outsiderUntouched) failures++;
            Debug.Log($"[Cont] {(uniform ? "PASS" : "FAIL")} 组内缩放一致（同一重心）");
            if (!uniform) failures++;

            anim.Seek(2.4f);   // 脉冲结束
            float e1 = anim.ScaleOf("blank_card_1#1") / Mathf.Max(1e-6f, anim.BaseScaleOf("blank_card_1#1"));
            bool returned = Mathf.Abs(e1 - 1f) < 0.02f;
            Debug.Log($"[Cont] {(returned ? "PASS" : "FAIL")} 脉冲回落到原尺寸: r={e1:0.000}");
            if (!returned) failures++;
            if (!grew) failures++;

            Debug.Log($"[Cont] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 机制通用性自检：**同一套「整组搬运」机制，对宝石和牌堆一样好使**。
        /// 这条是为了守住一条设计原则：树里谁排第一取决于**先介绍谁**，
        /// 不是"卡片有特殊地位"。所以搬运/分组机制不能对牌有特例。
        /// </summary>
        public static void SelfTestGroupMoveGeneric()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("GroupMoveHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", "setup.cards.001.1", false);
            anim.Seek(0.2f);   // 牌堆还在盒子里

            // 用与牌堆**完全相同的调用方式**把一堆宝石搬进供应堆
            var before = anim.Store.CountInZone("box_gem_diamond");
            anim.TriggerForTest(new CueAnimEvent
            {
                at = 0.3f, dur = 0.6f, action = "move", group = true,
                from = new List<string> { "box_gem_diamond" },
                zone = "gem_supply_diamond", easing = "easeInOutCubic",
            });
            anim.Seek(1.2f);

            int inSupply = anim.Store.CountInZone("gem_supply_diamond");
            int inBox = anim.Store.CountInZone("box_gem_diamond");
            bool moved = inSupply == before && inBox == 0 && before > 0;
            Debug.Log($"[Gen] {(moved ? "PASS" : "FAIL")} 同一套整组搬运可用于宝石：" +
                      $"box_gem_diamond {before} → 供应堆 {inSupply}（盒中剩 {inBox}）");
            if (!moved) failures++;

            // 牌堆也仍然好使（同一条代码路径）。
            // 卡堆现在不再初始存在（cue 12 才创建），所以这里先 create 再搬。
            anim.TriggerForTest(new CueAnimEvent
            {
                at = 1.3f, dur = 0.0f, action = "create", template = "blank_card_1",
                palette = "card_level_1", zone = "box_level_1", count = 36,
            });
            anim.TriggerForTest(new CueAnimEvent
            {
                at = 1.35f, dur = 0.6f, action = "move", group = true,
                from = new List<string> { "box_level_1" },
                zone = "deck_level_1", easing = "easeInOutCubic",
            });
            anim.Seek(2.2f);
            int deck = anim.Store.CountInZone("deck_level_1");
            int deckBox = anim.Store.CountInZone("box_level_1");
            bool deckOk = deck == 36 && deckBox == 0;
            Debug.Log($"[Gen] {(deckOk ? "PASS" : "FAIL")} 牌堆用同一机制搬进桌面：" +
                      $"deck_level_1={deck}（盒中剩 {deckBox}）");
            if (!deckOk) failures++;

            Debug.Log($"[Gen] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 跳转 vs 顺序播放一致性：两条路必须得到**同一画面**。
        ///
        /// 用户报的现象：按右跳到下一条时盒面消失，顺序播到同一条却还在。
        /// 原因是「顺序播放就沿用当前画面」的快捷路径绕过了入口状态求解，
        /// 而跳转会从树根重解 —— 同一个位置，两条路画面不同。
        /// </summary>
        public static void SelfTestJumpMatchesSequential()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("ConsistencyHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Consist] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolve = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            System.Func<string, bool> boxVisibleNow = (ignored) =>
                anim.BoxVisibleForTest;

            for (int idx = 0; idx < Mathf.Min(11, player.Document.cues.Count); idx++)
            {
                string cueId = player.Document.cues[idx].id;

                // 路 A：直接跳到这一条
                string entry = (string)resolve.Invoke(player, new object[] { idx });
                apply.Invoke(player, new object[] { anim, entry });
                bool boxJump = anim.BoxVisibleForTest;

                // 路 B：从根顺序播到这一条
                apply.Invoke(player, new object[] { anim, null });     // 先回到根
                for (int k = 0; k <= idx; k++)
                {
                    string e2 = (string)resolve.Invoke(player, new object[] { k });
                    apply.Invoke(player, new object[] { anim, e2 });
                }
                bool boxSeq = anim.BoxVisibleForTest;

                bool same = boxJump == boxSeq;
                Debug.Log($"[Consist] {(same ? "PASS" : "FAIL")} {cueId}: 跳转盒面={boxJump} 顺序盒面={boxSeq}");
                if (!same) failures++;
            }

            Debug.Log($"[Consist] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 逐条走一遍所有 cue，检查每一步的组件数与坐标是否正常。
        /// 走的是**跳转**路径（先按 entry 解入口状态再播）——与顺序播放是两条不同路径，
        /// 用户报的 NaN 只在跳转时出现。无效坐标会在这里被计出来。
        /// </summary>
        public static void SelfTestCueWalk()
        {
            int failures = 0, badPos = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("WalkHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Walk] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolve = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            int count = Mathf.Min(16, player.Document.cues.Count);
            for (int i = 0; i < count; i++)
            {
                string cueId = player.Document.cues[i].id;
                apply.Invoke(player, new object[] { anim, resolve.Invoke(player, new object[] { i }) });
                anim.LoadCue(gameRoot, "full", cueId, true);

                // 逐帧推到终态：洗混/发牌这类"过程中产生坏坐标"的问题只有逐帧才能抓到
                int invalidMid = 0;
                for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f)
                {
                    anim.Seek(tt);
                    foreach (var it in anim.Store.Items)
                    {
                        if (it.Actor?.Go == null) continue;
                        var pp = it.Actor.Go.transform.localPosition;
                        if (float.IsNaN(pp.x) || float.IsNaN(pp.z)) invalidMid++;
                    }
                }
                if (invalidMid > 0)
                    Debug.LogError($"[Walk] {cueId} 播放过程中出现 {invalidMid} 次无效坐标");

                int invalid = 0;
                foreach (var it in anim.Store.Items)
                {
                    if (it.Actor?.Go == null) continue;
                    var pos = it.Actor.Go.transform.localPosition;
                    if (float.IsNaN(pos.x) || float.IsNaN(pos.z)) invalid++;
                }
                badPos += invalid;
                int market = anim.Store.CountInZone("card_market");
                Debug.Log($"[Walk] {i,2} {cueId,-22} items={anim.ActorCount,3} market={market,2} 无效坐标={invalid}");
                if (invalid > 0 || invalidMid > 0) failures++;
            }

            Debug.Log($"[Walk] {(failures == 0 ? "全部通过" : failures + " 项失败")}（累计无效坐标 {badPos}）");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 发完 12 张牌后进入下一条 cue，牌必须**还在**、坐标有效、正面朝上。
        /// 用户报过"发完12张牌后这12张牌会消失"。
        /// </summary>
        public static void SelfTestDealtCardsSurvive()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var host = new GameObject("DealtHost");
            var player = host.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Dealt] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var anim = host.GetComponent<TutorialCueAnimPlayer>() ?? host.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            System.Func<string> report = () =>
            {
                int inMarket = 0, visible = 0, nan = 0;
                foreach (var it in anim.Store.Items)
                {
                    if (it.ZoneId != "card_market") continue;
                    inMarket++;
                    var sr = it.Actor?.Renderer;
                    if (sr != null && sr.enabled && sr.sprite != null && sr.color.a > 0.05f) visible++;
                    var pp = it.Actor?.Go != null ? it.Actor.Go.transform.localPosition : Vector3.zero;
                    if (float.IsNaN(pp.x) || float.IsNaN(pp.z)) nan++;
                }
                return $"市场 {inMarket} 张，可见 {visible} 张，无效坐标 {nan}";
            };

            // 走**跳转**路径进入 cue 12（用户的路径），播完
            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolve = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            int i12 = player.Document.cues.FindIndex(c => c.id == "setup.cards.002.1");
            apply.Invoke(player, new object[] { anim, resolve.Invoke(player, new object[] { i12 }) });
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true);
            for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.1f) anim.Seek(tt);
            string after12 = report();
            bool ok12 = after12.Contains("可见 12 张") && after12.Contains("无效坐标 0");
            Debug.Log($"[Dealt] {(ok12 ? "PASS" : "FAIL")} cue12 播完: {after12}");
            if (!ok12) failures++;

            // 下一条（无动画数据）：牌必须保留。
            // 走**顺序播放**路径（autoAdvance 就是这样进入下一条的）：
            // 先按 entry 解入口状态，再 continueState=true 载入 —— 这正是实机的两条分支。
            string next = player.Document.cues[i12 + 1].id;
            apply.Invoke(player, new object[] { anim, resolve.Invoke(player, new object[] { i12 + 1 }) });
            anim.LoadCue(gameRoot, "full", next, true);
            string after13 = report();
            bool ok13 = after13.Contains("可见 12 张") && after13.Contains("无效坐标 0");
            Debug.Log($"[Dealt] {(ok13 ? "PASS" : "FAIL")} 进入 {next} 后: {after13}");
            if (!ok13) failures++;

            // 再往下走两条（其中一条有动画）
            for (int k = 2; k <= 3 && i12 + k < player.Document.cues.Count; k++)
            {
                string nid = player.Document.cues[i12 + k].id;
                apply.Invoke(player, new object[] { anim, resolve.Invoke(player, new object[] { i12 + k }) });
                anim.LoadCue(gameRoot, "full", nid, true);
                anim.Seek(anim.TotalDuration + 1f);
                string r = report();
                bool ok = r.Contains("可见 12 张") && r.Contains("无效坐标 0");
                Debug.Log($"[Dealt] {(ok ? "PASS" : "FAIL")} 进入 {nid} 后: {r}");
                if (!ok) failures++;
            }

            Debug.Log($"[Dealt] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 分支自检：**两条兄弟 cue 共享同一个父状态，但牌堆顶可以是不同的牌**。
        ///
        /// 场景（用户提出）："如果抽到了红色牌就加入手牌，抽到蓝色牌则塞回牌堆底"。
        /// 两条分支的入口是同一个父 cue，所以牌堆内容相同；
        /// 区别在于**各自在开头把不同的牌换到牌堆顶**，于是抽到不同的牌。
        ///
        /// 这条测的是"牌是个体、顺序可改"这个模型是否真的成立。
        ///
        /// 依赖三条临时 cue（branch.shared.001 / branch.red.001 / branch.blue.001）。
        /// 它们只用于验证，不在正式 runtime 里 —— 跑这条测试前需要先把它们
        /// 注册进 full.runtime.json，否则会报"无此 cue"。
        /// </summary>
        /// <summary>
        /// 分支自检：**两条分支共享同一个父状态，但牌堆顶可以是不同的牌**。
        ///
        /// 场景："如果抽到了红色牌就加入手牌，抽到蓝色牌则塞回牌堆底"。
        /// 两条分支的入口是同一个父 cue，所以牌堆内容相同；
        /// 区别在于各自在开头把**不同的牌换到牌堆顶**，于是抽到不同的牌。
        ///
        /// 自建场景（不依赖仓库里的临时 cue 文件）：
        ///   ① 造一个牌堆，里面有红、蓝各一张
        ///   ② 分支A：把红牌换到牌堆顶再抽
        ///   ③ 分支B：把蓝牌换到牌堆顶再抽
        /// 两次都用**同一个起点**，所以这测的正是"共享父状态 + 顶牌不同"。
        /// </summary>
        public static void SelfTestBranchDifferentTopCard()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            System.Func<string, string> drawWithTop = (topCard) =>
            {
                var go = new GameObject("BranchHost");
                var a = go.AddComponent<TutorialCueAnimPlayer>();
                a.animationEnabled = true;
                a.LoadCue(gameRoot, "full", "setup.cards.002.1", true);
                a.Seek(0.5f);   // 牌堆已创建、还没洗混

                // 同一个起点：牌堆里有红蓝各一张
                a.TriggerForTest(new CueAnimEvent { at = 0.6f, dur = 0f, action = "create",
                    template = "market_card_1_ruby", palette = "card_level_1",
                    zone = "deck_level_1", count = 1 });
                a.TriggerForTest(new CueAnimEvent { at = 0.61f, dur = 0f, action = "create",
                    template = "market_card_1_sapphire", palette = "card_level_1",
                    zone = "deck_level_1", count = 1 });

                // 只改**哪张在牌堆顶**（order 0）
                a.TriggerForTest(new CueAnimEvent { at = 0.7f, dur = 0.2f, action = "move",
                    target = topCard + "#1", zone = "deck_level_1", slot = 0,
                    easing = "easeOutCubic" });

                // 抽走牌堆顶那张
                a.TriggerForTest(new CueAnimEvent { at = 1.0f, dur = 0.2f, action = "move",
                    from = new List<string> { "deck_level_1" }, take = 1,
                    template = topCard, zone = "card_market", slot = 0,
                    flip = true, easing = "easeInOutCubic" });
                a.Seek(1.6f);

                string picked = "(没抽到)";
                foreach (var it in a.Store.Items)
                    if (it.ZoneId == "card_market" && it.Template != null)
                        picked = it.Template.id;
                Object.DestroyImmediate(go);
                return picked;
            };

            string a1 = drawWithTop("market_card_1_ruby");
            string b1 = drawWithTop("market_card_1_sapphire");

            bool aOk = a1.Contains("ruby"), bOk = b1.Contains("sapphire"), differ = a1 != b1;
            Debug.Log($"[Branch] {(aOk ? "PASS" : "FAIL")} 把红牌放顶上 → 抽到 {a1}");
            if (!aOk) failures++;
            Debug.Log($"[Branch] {(bOk ? "PASS" : "FAIL")} 把蓝牌放顶上 → 抽到 {b1}");
            if (!bOk) failures++;
            Debug.Log($"[Branch] {(differ ? "PASS" : "FAIL")} 同一父状态下，顶牌不同 → 抽到不同的牌");
            if (!differ) failures++;

            Debug.Log($"[Branch] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        public static void SelfTestStateSnapshotIsStable()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            System.Func<string, float, string> snapshot = (cueId, step) =>
            {
                var go = new GameObject("StateHost");
                var a = go.AddComponent<TutorialCueAnimPlayer>();
                a.animationEnabled = true;
                a.LoadCue(gameRoot, "full", cueId, true);
                for (float tt = 0f; tt <= a.TotalDuration + 1f; tt += step) a.Seek(tt);
                var lines = new System.Collections.Generic.List<string>();
                foreach (var it in a.Store.Items)
                    lines.Add($"{it.Id}|{it.ZoneId}|{it.Order}|{it.Flipped}|{it.Template?.id}");
                lines.Sort();
                Object.DestroyImmediate(go);
                return string.Join("\n", lines);
            };

            string coarse = snapshot("setup.cards.002.1", 0.50f);
            string fine = snapshot("setup.cards.002.1", 0.05f);
            bool stable = coarse == fine;
            Debug.Log($"[State] {(stable ? "PASS" : "FAIL")} 终态与采样粒度无关" +
                      $"（粗 {coarse.Split('\n').Length} 件 / 细 {fine.Split('\n').Length} 件）");
            if (!stable) failures++;

            Debug.Log($"[State] 状态可序列化：{coarse.Split('\n').Length} 行，" +
                      $"例如 {coarse.Split('\n')[0]}");
            Debug.Log($"[State] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 采样一条 cue 的终态，输出成**契约同构**的 JSON，供对账脚本比对。
        ///
        /// 输出的是**语义状态**（谁在哪个 zone、几件、朝上还是朝下、什么身份），
        /// 不含坐标/缩放 —— 那些是从 zone+格位推出来的表现层，写进契约只会误报。
        ///
        ///   -dumpCue &lt;cueId&gt;  -dumpReplay 0|1  -dumpOut &lt;path&gt;
        /// </summary>
        public static void DumpState()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-dumpCue", "setup.cards.002.1");
            bool replay = ArgValue("-dumpReplay", "0") == "1";
            string outPath = ArgValue("-dumpOut", Path.Combine(Application.dataPath, "..", "CaptureOut", "state.json"));

            var go = new GameObject("DumpHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // -dumpReplay 1：沿 entry 链把之前的 cue **逐条播到终态**，再采样本条。
            // 只播上一条不够：样本卡由更早的 cue 创建，漏掉会让父 cue 的出口本身是空的。
            if (replay)
            {
                var playerGo = new GameObject("DumpReplayHost");
                var player = playerGo.AddComponent<TutorialCuePlayer>();
                player.autoPlay = false;
                player.tutorialRoot = Path.Combine(repoRoot, "games");
                if (player.LoadRuntime())
                {
                    int idx = player.Document.cues.FindIndex(c => c.id == cueId);
                    for (int k = 0; k < idx; k++)
                    {
                        string prev = player.Document.cues[k].id;
                        if (!anim.LoadCue(gameRoot, "full", prev, k > 0)) continue;
                        anim.Seek(anim.TotalDuration + 1f);
                    }
                }
                Object.DestroyImmediate(playerGo);
            }

            // 无动画数据的 cue（例如 setup.cards.002.2）LoadCue 返回 false，但它**保留牌桌** ——
            // 那正是要采样的状态。以前这里直接退出，于是这类 cue 从来没被查过。
            bool loaded = anim.LoadCue(gameRoot, "full", cueId, replay);
            if (!loaded && anim.ActorCount == 0)
            {
                Debug.LogError($"[Dump] LoadCue 失败且场景为空: {cueId}");
                EditorApplication.Exit(1);
                return;
            }
            for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f) anim.Seek(tt);

            var zones = new SortedDictionary<string, ZoneAgg>();
            foreach (var it in anim.Store.Items)
            {
                if (!zones.TryGetValue(it.ZoneId, out var agg))
                {
                    agg = new ZoneAgg();
                    zones[it.ZoneId] = agg;
                }
                agg.Count++;
                // 按**画面上实际显示的那一面**统计，而不是按 Flipped（逻辑意图）。
                // 两者不一致时画面就是错的 —— 那正是要查的东西。
                switch (it.Showing)
                {
                    case "face": agg.ShowsFace++; break;
                    case "back": agg.ShowsBack++; break;
                    default: agg.Hidden++; break;
                }
                if (it.Flipped) agg.FaceUp++; else agg.FaceDown++;
                if (!agg.Kinds.ContainsKey(it.KindKey)) agg.Kinds[it.KindKey] = 0;
                agg.Kinds[it.KindKey]++;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"cue\": \"{cueId}\",\n");
            sb.Append("  \"zones\": {\n");
            int zi = 0;
            foreach (var kv in zones)
            {
                zi++;
                var agg = kv.Value;
                sb.Append($"    \"{kv.Key}\": {{ \"count\": {agg.Count}, " +
                          $"\"face_up\": {agg.FaceUp}, \"face_down\": {agg.FaceDown}, " +
                          $"\"shows_face\": {agg.ShowsFace}, \"shows_back\": {agg.ShowsBack}, " +
                          $"\"hidden\": {agg.Hidden}, \"kinds\": {{");
                int ki = 0;
                foreach (var k in agg.Kinds)
                {
                    ki++;
                    sb.Append($"\"{k.Key}\": {k.Value}");
                    if (ki < agg.Kinds.Count) sb.Append(", ");
                }
                sb.Append("} }");
                if (zi < zones.Count) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  }\n}\n");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[Dump] {cueId} 终态已写入 {outPath}（{anim.ActorCount} 件，{zones.Count} 个 zone）");
            EditorApplication.Exit(0);
        }

        private class ZoneAgg
        {
            public int Count, FaceUp, FaceDown;
            public int ShowsFace, ShowsBack, Hidden;   // 画面上实际显示哪一面
            public SortedDictionary<string, int> Kinds = new SortedDictionary<string, int>();
        }

        /// <summary>
        /// 逐帧检查「牌堆朝下、市场朝上」这条不变量。
        ///
        /// 用户报过两个现象，都是这条没被检查：
        ///   ① cue12 牌堆最上面那张**正面朝上**（牌堆应全部朝下）
        ///   ② cue12 发牌后 12 张市场牌**全部朝下**（市场应全部朝上）
        ///
        /// 注意本引擎既有的命名（**反直觉，但已固定下来**）：
        ///   ZoneItem.Flipped == true  表示"已翻到正面"→ RefreshFace 显示 **BackSprite**
        ///   ZoneItem.Flipped == false 表示"未翻开"  → RefreshFace 显示 **FaceSprite**
        /// 也就是说对卡牌而言：FaceSprite 是**卡面**，BackSprite 是**卡背**，
        /// 而 Flipped 记的是"要不要显示卡背"。名字别扭，但换了会动到很多地方。
        /// </summary>
        public static void SelfTestOrientationMatchesSprite()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("OrientHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", "setup.cards.002.1", true);

            int badPile = 0, badMarket = 0, badMarketAfterDeal = 0;
            for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f)
            {
                anim.Seek(tt);
                foreach (var it in anim.Store.Items)
                {
                    var sr = it.Actor?.Renderer;
                    if (sr == null || sr.sprite == null || !sr.enabled) continue;
                    bool showingFace = ReferenceEquals(sr.sprite, it.Actor.FaceSprite);
                    bool showingBack = ReferenceEquals(sr.sprite, it.Actor.BackSprite);

                    // 牌堆：必须显示卡背。有独立背图就用背图；没有（牌堆牌往往没有）
                    // 就显示它自己的 FaceSprite —— 那张就是卡背扫描图。
                    if (it.ZoneId.StartsWith("deck_level_"))
                    {
                        bool ok = it.Actor.BackSprite != null ? showingBack : showingFace;
                        if (!ok) badPile++;
                    }

                    // 市场：**已落位的**牌必须显示真卡面。
                    // 正在翻转的那张（0°→180° 中途）允许显示卡背 —— 那正是"翻过去"的过程。
                    if (it.ZoneId == "card_market" && it.Actor != null)
                    {
                        var pos = it.Actor.Go.transform.localPosition;
                        var want = anim.Store.ZonePosition("card_market", it.Order);
                        bool settled = Mathf.Abs(pos.x - want.x) < 0.02f && Mathf.Abs(pos.z - want.z) < 0.02f;
                        if (settled && !showingFace) badMarket++;
                    }
                }
            }

            // 终态逐张列出（定位是哪几张不对）
            anim.Seek(anim.TotalDuration + 1f);
            foreach (var it in anim.Store.Items)
                if (it.ZoneId == "card_market" && it.Actor?.Renderer != null)
                {
                    var sr = it.Actor.Renderer;
                    string cur = ReferenceEquals(sr.sprite, it.Actor.BackSprite) ? "背图"
                        : ReferenceEquals(sr.sprite, it.Actor.FaceSprite) ? "面图" : "其他";
                    if (cur != "面图")
                        Debug.Log($"[Orient] 不对: {it.Id} Flipped={it.Flipped} 显示={cur} " +
                                  $"sprite={(sr.sprite == null ? "NULL" : sr.sprite.texture.width + "x" + sr.sprite.texture.height)} " +
                                  $"face={(it.Actor.FaceSprite == null ? "NULL" : it.Actor.FaceSprite.texture.width + "x" + it.Actor.FaceSprite.texture.height)} " +
                                  $"back={(it.Actor.BackSprite == null ? "NULL" : it.Actor.BackSprite.texture.width + "x" + it.Actor.BackSprite.texture.height)} " +
                                  $"enabled={sr.enabled} alpha={sr.color.a:0.00}");
                }

            Debug.Log($"[Orient] {(badPile == 0 ? "PASS" : "FAIL")} 牌堆全程显示卡背（异常 {badPile} 帧次）");
            if (badPile != 0) failures++;
            Debug.Log($"[Orient] {(badMarket == 0 ? "PASS" : "FAIL")} " +
                      $"已落位的市场牌显示真卡面（异常 {badMarket} 帧次）");
            if (badMarket != 0) failures++;
            _ = badMarketAfterDeal;

            // 牌堆外形模型（用户定义的）：**最底部 max_visible 张各自错开，再往上的全部重合**。
            // 判据（这是"发牌看不出变少"的真正条件）：
            //   ① order → 位置是固定的映射（与还剩几张无关）
            //   ② 发牌后，**没被取走的那些牌坐标完全不变**
            System.Func<string, System.Collections.Generic.Dictionary<int, Vector3>> byOrder = (zone) =>
            {
                var map = new System.Collections.Generic.Dictionary<int, Vector3>();
                var last = Vector3.zero; bool has = false;
                foreach (var it in anim.Store.Items.Where(x => x.ZoneId == zone))
                { map[it.Order] = it.LivePosition; last = it.LivePosition; has = true; }
                return has ? map : map;
            };
            System.Action<float> seekTo = (at) => { for (float tt = 0f; tt <= at; tt += 0.05f) anim.Seek(tt); };

            seekTo(2.6f);   var before = byOrder("deck_level_1");
            seekTo(8.4f);   var after = byOrder("deck_level_1");

            // ① 底部 8 张逐一错开、间距恒定
            bool even = true; float step = Vector3.Distance(before[0], before[1]);
            for (int i = 1; i < 7; i++)
                if (Mathf.Abs(Vector3.Distance(before[i], before[i + 1]) - step) > 1e-5f) even = false;
            bool spread = step > 1e-6f;
            Debug.Log($"[Orient] {(even && spread ? "PASS" : "FAIL")} " +
                      $"底部 8 张逐层错开且间距恒定（每层 {step:0.0000}）");
            if (!even || !spread) failures++;

            // ② 第 8 张及以上的牌全部重合
            bool coincide = true;
            foreach (var o in before.Keys.Where(k => k >= 7))
                if (Vector3.Distance(before[o], before[7]) > 1e-5f) coincide = false;
            Debug.Log($"[Orient] {(coincide ? "PASS" : "FAIL")} 第 8 张往上的牌全部重合（重合块）");
            if (!coincide) failures++;

            // ③ **发牌后，没被取走的牌坐标完全不变** —— 这才是"看不出变少"
            int moved = 0;
            foreach (var kv in after)
                if (before.TryGetValue(kv.Key, out var was)
                    && Vector3.Distance(was, kv.Value) > 1e-5f) moved++;
            Debug.Log($"[Orient] {(moved == 0 ? "PASS" : "FAIL")} " +
                      $"发牌后未被取走的牌坐标完全不变（变化的 {moved} 张）");
            if (moved != 0) failures++;

            Debug.Log($"[Orient] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 按用户的真实路径采样：**播完 cue12，再跳/进到 cue13**，看市场牌显示哪一面。
        ///
        /// 为什么需要单独一条：`-dumpReplay` 走的是"沿 entry 链重放"，
        /// 而用户是"顺序播到 cue13"（或跳转）。两条路径不同，前者查不出后者的问题。
        /// </summary>
        public static void DumpAdvancePath()
        {
            // 这条同时是**自检**：按编辑器真实路径逐条推进，任何一步市场牌显示异常都要报错。
            bool advanceFail = false;

            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("AdvHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;

            // 编辑器实际用的路径：PlayCue → ApplyEntryState（解入口状态）+ LoadCue。
            // 所以这里必须**完整复现那两步**，否则采样和真实画面会不一致。
            var playerGo = new GameObject("AdvPlayer");
            var player = playerGo.AddComponent<TutorialCuePlayer>();
            player.autoPlay = false;
            player.tutorialRoot = Path.Combine(repoRoot, "games");
            if (!player.LoadRuntime()) { Debug.Log("[Adv] FAIL LoadRuntime"); EditorApplication.Exit(1); return; }

            var apply = typeof(TutorialCuePlayer).GetMethod("ApplyEntryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resolve = typeof(TutorialCuePlayer).GetMethod("ResolveEntryCueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            string[] path = { "setup.cards.002.1", "setup.cards.002.2",
                              "setup.gems.001.1", "setup.gems.001.2" };
            for (int i = 0; i < path.Length; i++)
            {
                int idx = player.Document.cues.FindIndex(c => c.id == path[i]);
                if (idx < 0) { Debug.Log($"[Adv] 找不到 {path[i]}"); continue; }
                // ① 解入口状态（编辑器里由 PlayCueRoutine 调用）
                apply.Invoke(player, new object[] { anim, resolve.Invoke(player, new object[] { idx }) });
                Report(anim, path[i] + " [解入口状态后]");
                // ② 载入本条，顺序进入时 continueState=true
                anim.LoadCue(gameRoot, "full", path[i], i > 0);
                Report(anim, path[i] + " [LoadCue 后]");
                for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f) anim.Seek(tt);
                Report(anim, path[i] + " 播完");
                if (MarketShowsBack(anim)) advanceFail = true;
            }
            Debug.Log($"[Adv] {(advanceFail ? "FAIL" : "PASS")} " +
                      $"逐条推进时市场牌始终正面朝上（含解入口状态那一步）");
            EditorApplication.Exit(advanceFail ? 1 : 0);
        }

        /// <summary>市场里是否有牌显示成背面（画面异常）。</summary>
        private static bool MarketShowsBack(TutorialCueAnimPlayer anim)
        {
            foreach (var it in anim.Store.Items)
                if (it.ZoneId == "card_market" && it.Showing == "back") return true;
            return false;
        }

        private static void Report(TutorialCueAnimPlayer anim, string tag)
        {
            int mk = 0, mkFace = 0, mkBack = 0, mkHidden = 0, flippedTrue = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.ZoneId != "card_market") continue;
                mk++;
                if (it.Flipped) flippedTrue++;
                switch (it.Showing)
                {
                    case "face": mkFace++; break;
                    case "back": mkBack++; break;
                    default: mkHidden++; break;
                }
            }
            Debug.Log($"[Adv] {tag}: 市场 {mk} 张 → 显示卡面 {mkFace} / 显示卡背 {mkBack} / 隐藏 {mkHidden}" +
                      $"（Flipped=true 的有 {flippedTrue} 张）");
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
            if (verbose) { anim.logCameraFit = true; anim.Store.logPull = true; anim.Store.logMoves = true; anim.logImages = true; anim.logMoves = true;
            anim.logTweens = true; }

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

                if (shot.File == "cards_01_placed") 
                if (shot.File == "cards_00_start")
                {
                    
                }

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
