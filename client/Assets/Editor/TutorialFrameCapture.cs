// BoardGameTutorial — cue 组件状态查询
//
// 只做一件事：把某条 cue 的组件状态打印出来（数据，不是像素）。
//
//   DumpState        采样一条 cue 的终态 → JSON
//   TraceState       逐帧打印状态轨迹（0.1s 一步）
//   ListZone         某个 zone 每一件的 id/order/坐标/显示哪面
//   DumpAdvancePath  按编辑器真实路径（解入口状态 + LoadCue）逐条推进并报告
//
// 用法（Windows 命令行，可从 WSL 调 Unity.exe）：
//   Unity.exe -batchmode -projectPath D:\workspace\board\client \
//     -executeMethod BoardGameTutorial.Editor.TutorialFrameCapture.TraceState \
//     -traceCue setup.cards.002.1 -logFile ...\trace.log -quit
//
// **这里没有任何 SelfTest**：自己出题自己阅卷，通过只说明"实现了我以为的东西"，
// 不说明"实现了要的东西"。判定交给用户。
//
// 出图（离屏渲染）已删除 —— 它两次把错画面当成证据。
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








        private static string ArgValue(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
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
        /// 按用户的真实路径采样：**播完 cue12，再跳/进到 cue13**，看市场牌显示哪一面。
        ///
        /// 为什么需要单独一条：`-dumpReplay` 走的是"沿 entry 链重放"，
        /// 而用户是"顺序播到 cue13"（或跳转）。两条路径不同，前者查不出后者的问题。
        /// </summary>
        public static void DumpAdvancePath()
        {

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
            if (!player.LoadRuntime()) { Debug.Log("[Adv] LoadRuntime 失败"); EditorApplication.Exit(1); return; }

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
            }
            Debug.Log("[Adv] 逐条推进完毕（上面每行的「显示卡背」计数即事实，判定由人来做）");
            EditorApplication.Exit(0);
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




        /// <summary>
        /// 发牌时**每一张被取走的牌**的 order 与它当时的坐标 —— 直接回答
        /// "发的是牌堆顶还是牌堆底"（不判定，只列事实）。
        /// </summary>
        public static void TraceDealOrder()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-dealCue", "setup.cards.002.1");
            string zone = ArgValue("-dealZone", "deck_level_1");

            var go = new GameObject("DealTraceHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", cueId, false);

            for (float tt = 0f; tt <= 2.6f; tt += 0.05f) anim.Seek(tt);

            // 位置表：order → 坐标，标出"最右上"与"最左下"两端
            var snap = anim.Store.Items.Where(x => x.ZoneId == zone)
                            .OrderBy(x => x.Order)
                            .Select(x => (x.Order, x.Id, x.Template.id, x.LivePosition))
                            .ToList();
            if (snap.Count == 0) { Debug.Log($"[Deal] {zone} 空"); EditorApplication.Exit(0); return; }
            var topRight = snap.OrderByDescending(s2 => s2.LivePosition.x).First();
            var botLeft  = snap.OrderBy(s2 => s2.LivePosition.x).First();
            Debug.Log($"[Deal] {zone} 共 {snap.Count} 张（按 order 列出）：");
            // 屏幕 y ∝ -z（z 越大越靠下）；屏幕 x ∝ +x
            foreach (var s2 in snap.OrderBy(s2 => -s2.LivePosition.z).ThenBy(s2 => s2.LivePosition.x).Take(12))
                Debug.Log($"[Deal]   order={s2.Order,3} {s2.Item3,-24} " +
                          $"({s2.LivePosition.x:0.000},{s2.LivePosition.z:0.000})");
            Debug.Log($"[Deal] 画面最上那张: order={snap.OrderBy(s2 => s2.LivePosition.z).First().Order}");
            Debug.Log($"[Deal] 画面最下那张: order={snap.OrderByDescending(s2 => s2.LivePosition.z).First().Order}");

            // 谁被发走了：zone 成员集合的差集
            var known = new HashSet<string>(snap.Select(s2 => s2.Id));
            var inZone = new Dictionary<string, (int Order, string Tpl)>();
            foreach (var s2 in snap) inZone[s2.Id] = (s2.Order, s2.Item3);

            for (float tt = 2.6f; tt <= anim.TotalDuration + 0.1f; tt += 0.02f)
            {
                anim.Seek(tt);
                foreach (var id in known.ToList())
                {
                    ZoneItem it = null;
                    foreach (var cand in anim.Store.Items) if (cand.Id == id) { it = cand; break; }
                    if (it != null && it.ZoneId == zone) continue;
                    string dest = it == null ? "(不存在)" : it.ZoneId;
                    Debug.Log($"[Deal] t={tt:0.00} 发走 order={inZone[id].Order,3} " +
                              $"{inZone[id].Tpl,-24} → {dest}");
                    known.Remove(id);
                }
            }
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 高亮脉冲的**时间采样自检**：给定时序 Seek 到某时刻，打印目标缩放；
        /// 然后**不再 Seek**（模拟暂停）再打印一次 —— 两次必须相同。
        /// 协程版本在第二次会继续变化（它靠 deltaTime 自己跑）。
        /// </summary>
        public static void TraceHighlightScale()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-hlCue", "setup.cards.001.3");
            string target = ArgValue("-hlTarget", "sample_back_2#1");

            var go = new GameObject("HlHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", cueId, false);

            CueAnimActor Find()
            {
                foreach (var it in anim.Store.Items)
                    if (it.Id == target) return it.Actor;
                return null;
            }
            float Scale() { var a = Find(); return a?.Go?.transform.localScale.x ?? -1f; }

            // 高亮 at=0.1 dur=1.3 → 峰值约在 0.75s
            foreach (var t2 in new[] { 0.30f, 0.75f, 1.10f, 1.35f, 1.50f })
            {
                for (float s = 0f; s <= t2; s += 0.02f) anim.Seek(s);
                float a = Scale();
                // 关键：不再 Seek（= 暂停），再读一次
                float b = Scale();
                Debug.Log($"[Hl] t={t2:0.00} 采样后={a:0.0000} 暂停后再读={b:0.0000} " +
                          $"{(Mathf.Abs(a - b) < 1e-6f ? "一致" : "**仍在变化**")}");
            }
            EditorApplication.Exit(0);
        }

        public static void ListZone()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-listCue", "setup.cards.002.1");
            string zone = ArgValue("-listZone", "deck_level_2");

            var go = new GameObject("ListHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.LoadCue(gameRoot, "full", cueId, false);
            anim.Seek(float.Parse(ArgValue("-listAt", "2.6")));

            var items = anim.Store.Items.Where(x => x.ZoneId == zone).OrderBy(x => x.Order).ToList();
            Debug.Log($"[List] {cueId} 的 {zone}：共 {items.Count} 件");
            EditorApplication.Exit(0);
        }

        public static void TraceState()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-traceCue", "setup.cards.002.1");
            string[] zones = ArgValue("-traceZones",
                "deck_level_1,deck_level_2,deck_level_3,card_market,showcase_1,showcase_2,showcase_3")
                .Split(',');

            var go = new GameObject("TraceHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            if (!anim.LoadCue(gameRoot, "full", cueId, false))
            {
                Debug.LogError($"[Trace] LoadCue 失败: {cueId}");
                EditorApplication.Exit(1);
                return;
            }

            var header = new System.Text.StringBuilder("[Trace] t      ");
            foreach (var z in zones) header.Append($"{z,-16}");
            Debug.Log(header.ToString());

            for (float tt = 0f; tt <= anim.TotalDuration + 0.1f; tt += 0.1f)
            {
                anim.Seek(tt);
                var row = new System.Text.StringBuilder($"[Trace] {tt,5:0.0}  ");
                foreach (var z in zones)
                {
                    int n = 0, face = 0, back = 0;
                    var xs = new System.Collections.Generic.List<float>();
                    foreach (var it in anim.Store.Items)
                    {
                        if (it.ZoneId != z) continue;
                        n++;
                        if (it.Showing == "face") face++; else if (it.Showing == "back") back++;
                        xs.Add(Mathf.Round(it.LivePosition.x * 10000f) / 10000f);
                    }
                    int steps = xs.Count == 0 ? 0 : xs.Distinct().Count();
                    row.Append($"{(n == 0 ? "-" : $"{n}张 正{face}背{back} 台{steps}"),-16}");
                }
                Debug.Log(row.ToString());
            }
            EditorApplication.Exit(0);
        }




        /// <summary>
        /// 按真实顺序重放目标 cue 之前所有存在动画数据的 cue，
        /// 让牌桌状态等于「顺序播到这里」时的样子。
        /// </summary>


    }
}
#endif
