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
                            .GetMethod("RebuildTableBefore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            .Invoke(cuePlayer, new object[] { anim2, idx });

                        int market = 0;
                        foreach (var it in anim2.Store.Items)
                            if (it.Id.StartsWith("market_card_")) market++;
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
        public static void SelfTestDealSync()
        {
            int failures = 0;
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");

            var go = new GameObject("DealHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.logMoves = true;
            anim.logTweens = true;

            if (!anim.LoadCue(gameRoot, "full", "setup.cards.002.1", false))
            {
                Debug.Log("[DealTest] FAIL LoadCue 返回 false");
                EditorApplication.Exit(1);
                return;
            }

            string probeId = "market_card_1_emerald#1";
            Vector3 startPos = Vector3.zero;
            foreach (var it in anim.Store.Items)
                if (it.Id == probeId && it.Actor != null) startPos = it.Actor.Go.transform.localPosition;

            var deckPos = anim.Store.ZoneCenter("deck_level_1");
            var slotPos = anim.Store.ZonePosition("card_market", 0);
            Debug.Log($"[DealTest] 起始=({startPos.x:0.00},{startPos.z:0.00}) " +
                      $"牌堆=({deckPos.x:0.00},{deckPos.z:0.00}) 市场首格=({slotPos.x:0.00},{slotPos.z:0.00})");

            bool onDeck = Mathf.Abs(startPos.x - deckPos.x) < 0.35f && Mathf.Abs(startPos.z - deckPos.z) < 0.35f;
            Debug.Log($"[DealTest] {(onDeck ? "PASS" : "FAIL")} 发牌前停在对应牌堆位置");
            if (!onDeck) failures++;

            // 手动驱动协程：编辑器里设固定帧长，Time.deltaTime 才会推进补间
            // （-executeMethod 没有帧循环，deltaTime 恒为 0，直接 Seek 测不出补间）。
            // 补间由 StartCoroutine 驱动，而批处理没有帧循环时协程不会推进 —— 端到端
            // 断言在这里测不了。改为断言「补间参数正确」+「原语本身能走到终点」：
            // 参数对 + 原语对 ⇒ 编辑器里必然飞到位。
            var expectedTo = anim.Store.ZonePosition("card_market", 0);
            Vector3 actualFrom = Vector3.zero, actualTo = Vector3.zero;
            var plan = anim.PlanMoveForTest("market_card_1_emerald#1");
            if (plan != null)
            {
                actualFrom = anim.Store.CurrentPosition(plan.Item);
                actualTo = plan.InPlace ? anim.Store.ZonePosition("card_market", plan.Order)
                                        : anim.Store.CurrentPosition(plan.Item);
            }
            bool planOk = plan != null && plan.InPlace
                && Mathf.Abs(actualTo.x - expectedTo.x) < 0.01f
                && Mathf.Abs(actualTo.z - expectedTo.z) < 0.01f
                && Mathf.Abs(actualFrom.x - deckPos.x) < 0.01f;
            Debug.Log($"[DealTest] {(planOk ? "PASS" : "FAIL")} 发牌补间参数：" +
                      $"起=({actualFrom.x:0.00},{actualFrom.z:0.00}) 止=({actualTo.x:0.00},{actualTo.z:0.00}) " +
                      $"期望止=({expectedTo.x:0.00},{expectedTo.z:0.00})");
            if (!planOk) failures++;

            {
                var probe = new GameObject("primProbe");
                TutorialPrimitives.ManualDelta = 1f / 60f;
                TutorialPrimitives.Paused = false;
                var e0 = TutorialPrimitives.TweenPosition(probe.transform, actualFrom, actualTo, 0.45f, "easeInOutCubic");
                int n = 0;
                while (e0.MoveNext()) n++;
                float dd = Vector3.Distance(probe.transform.position, actualTo);
                bool primOk = dd < 0.01f && n > 1;
                Debug.Log($"[DealTest] {(primOk ? "PASS" : "FAIL")} 补间原语走到终点：{n} 步, 偏差 {dd:0.0000}");
                if (!primOk) failures++;
                Object.DestroyImmediate(probe);
                TutorialPrimitives.ManualDelta = 0f;
            }

            Debug.Log($"[DealTest] {(failures == 0 ? "全部通过" : failures + " 项失败")}");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// 逐帧出图：把一条 cue 的时间轴按固定步长走一遍，每步存一张 PNG。
        /// 因为画面已经是「时间的函数」（Seek 采样片段），离屏也能看到完整动画，
        /// 不需要 Unity 帧循环、也不需要人肉截图。
        /// 用法：-executeMethod ...CaptureTimeline -captureCue &lt;id&gt; -captureFrom 0 -captureTo 8 -captureStep 0.15
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
            if (!anim.LoadCue(gameRoot, "full", cueId, false))
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
            Debug.Log($"[Timeline] 已出 {n} 帧 → {outDir}");
        }

        private static string ArgValue(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
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
