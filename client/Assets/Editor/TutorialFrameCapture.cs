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
        ///   -dumpCue &lt;cueId&gt;  -dumpReplay 0|1  -dumpOut &lt;path&gt;        采一条
        ///   -dumpCues "a,b,c"  -dumpOut &lt;path&gt;                      一次采一整条轨道（一个文件）
        ///   -dumpItems 0|1（默认 1）每件组件的状态；0 = 只要 zone 聚合
        ///
        /// 采一整条轨道时不需要 -dumpReplay：它本身就是从头顺次播到尾。
        /// </summary>
        public static void DumpState()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string gameRoot = Path.Combine(repoRoot, "games/splendor");
            string cueId = ArgValue("-dumpCue", "setup.cards.002.1");
            bool replay = ArgValue("-dumpReplay", "0") == "1";
            string outPath = ArgValue("-dumpOut", Path.Combine(Application.dataPath, "..", "CaptureOut", "state.json"));
            // 每件组件的状态（一行一件）。默认开：排查"到底哪一件不对"只能靠它。
            // 整条轨道（100+ 条 cue）会让文件到 MB 级，嫌大就 -dumpItems 0（聚合仍在）。
            bool withItems = ArgValue("-dumpItems", "1") == "1";
            // 把引擎**解析到**的事件打一行（-dumpEvents 1）。
            // 为什么要这个：契约是脚本文件里写的，引擎读到的是 JsonUtility 解析后的对象，
            // 两者不一致时（字段没映射上、默认值、老数据…）表现是"画面什么都不做"，
            // 只看脚本文件永远查不出来 —— 必须看解析结果（2026-09 的 create/what 就是这么查的）。
            bool withEvents = ArgValue("-dumpEvents", "0") == "1";
            // 把"每个模板最终用的是哪张图"打出来（-dumpImages 1）。
            // 换素材/加处理（例如 <原名>_cutout.png）之后，第一个要确认的就是它 ——
            // 否则"改了素材但画面没变"要靠猜。
            bool withImages = ArgValue("-dumpImages", "0") == "1";

            var go = new GameObject("DumpHost");
            var anim = go.AddComponent<TutorialCueAnimPlayer>();
            anim.animationEnabled = true;
            anim.logImages = withImages;

            // ── 一次采一整条轨道（-dumpCues "a,b,c"）─────────────────────────
            // 以前每条 cue 都要启动一次 Unity（batchmode 启动几十秒）+ 每条都从头重放一遍
            // entry 链，一整条轨道 109 条就是 109 次启动。合成一次以后：Unity 只启动一次，
            // 从头顺次播到尾，每条播到终态就记一笔 —— 这也正是播放器的真实路径（顺序播放），
            // 比一条条重放更接近用户实际看到的画面。
            var cuesArg = ArgValue("-dumpCues", null);
            if (!string.IsNullOrEmpty(cuesArg))
            {
                var ids = new List<string>();
                foreach (var raw in cuesArg.Split(','))
                {
                    var id = raw.Trim();
                    if (id.Length > 0) ids.Add(id);
                }

                // 像真播放器那样把**整条轨道**的顺序交过去：入口链重放要按它找"本条之前"。
                // 采样器少了这一步，第一条 cue 的入口状态就会解错（实测会一路重放到全片终态，
                // 于是整条轨道的状态被叠在一起：市场 12→24 张）。**采样必须走和播放器同一条路。**
                anim.SetCueOrder(ids);

                // ── 把引擎**自己说的话**收进采样 ────────────────────────────
                // 为什么：像"highlight 没选中任何件"这种事，引擎只在日志里说一句，
                // 画面表现是"什么都没发生"，而对账（比状态）**看不见** —— 状态确实没变，
                // 因为那一动压根没生效。这类"脚本要求的事没发生"只有引擎知道，
                // 所以让它随采样一起交出来，由 check_cue_script 报给人看。
                var problems = new List<string>();
                Application.LogCallback logCatcher = (condition, stackTrace, type) =>
                {
                    if (type != LogType.Warning && type != LogType.Error && type != LogType.Exception)
                        return;   // Log（例如"本条 cue 没有动画数据"）是正常的，不收
                    string first = (condition ?? "").Split('\n')[0].Trim();
                    if (first.Length == 0 || problems.Contains(first)) return;
                    if (problems.Count < 20) problems.Add(first);
                };
                Application.logMessageReceived += logCatcher;

                var many = new System.Text.StringBuilder();
                many.Append("{\n");
                many.Append("  \"schema_version\": 1,\n");
                many.Append("  \"game_id\": \"splendor\",\n");
                many.Append("  \"track\": \"full\",\n");
                many.Append("  \"kind\": \"exit_states\",\n");
                many.Append("  \"cues\": {\n");
                for (int k = 0; k < ids.Count; k++)
                {
                    // 第一条从牌桌初始状态起，之后承接上一条的终态。
                    // 没有动画数据的 cue（例如 setup.cards.002.2）LoadCue 返回 false，
                    // 但它**保留牌桌** —— 那正是要采样的状态。
                    problems.Clear();   // 每条 cue 单独收，问题归属才清楚
                    bool ok = anim.LoadCue(gameRoot, "full", ids[k], k > 0);
                    if (!ok && anim.ActorCount == 0)
                        Debug.LogWarning($"[Dump] {ids[k]} 没有动画数据、场景也是空的（采到的是空状态）");
                    if (withEvents)
                        foreach (var e in anim.EventsForTest)
                            Debug.Log($"[Dump] {ids[k]} ev@{e.at} {e.action}" +
                                      $" what={(e.what == null ? "<null>" : $"'{e.what.concept}'")}" +
                                      $" target='{e.target}' zone='{e.zone}'" +
                                      $" src={(e.source == null || e.source.Count == 0 ? "-" : string.Join("/", e.source))}" +
                                      $" qty={e.quantity} dest='{e.destination}'");
                    for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f) anim.Seek(tt);

                    var zs = CollectZones(anim);
                    // 引擎在**这一条 cue 里**报出的警告/错误（去重、限量）
                    var cueProblems = new List<string>(problems);
                    // 整幅图（盒面等）也是状态 —— 不导出它，"背景多出一张盒面"这类问题
                    // 在采样里根本看不见（用户报的 cue 10 就是这么漏的）。
                    var pic = anim.BoxPictureForTest;
                    many.Append($"    \"{ids[k]}\": {{\n      \"picture\": " +
                                (string.IsNullOrEmpty(pic) ? "null" : $"\"{pic}\"") + "," +
                                $"\n      \"zones\": {{\n");
                    AppendZoneLines(many, zs, "        ");
                    many.Append("      }");
                    if (withItems)
                    {
                        many.Append(",\n      \"items\": [\n");
                        AppendItemLines(many, anim, "        ");
                        many.Append("      ]");
                    }
                    if (cueProblems.Count > 0)
                    {
                        many.Append(",\n      \"problems\": [");
                        for (int pi = 0; pi < cueProblems.Count; pi++)
                        {
                            many.Append("\n        " + JsonEscape(cueProblems[pi]));
                            if (pi < cueProblems.Count - 1) many.Append(",");
                        }
                        many.Append("\n      ]");
                    }
                    many.Append("\n    }");
                    if (k < ids.Count - 1) many.Append(",");
                    many.Append("\n");
                }
                many.Append("  }\n}\n");
                Application.logMessageReceived -= logCatcher;

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllText(outPath, many.ToString());
                Debug.Log($"[Dump] 一次采样 {ids.Count} 条 cue 的终态 → {outPath}");
                EditorApplication.Exit(0);
                return;
            }

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

            var zones = CollectZones(anim);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"cue\": \"{cueId}\",\n");
            var onePic = anim.BoxPictureForTest;
            sb.Append("  \"picture\": " + (string.IsNullOrEmpty(onePic) ? "null" : $"\"{onePic}\"") + ",\n");
            sb.Append("  \"zones\": {\n");
            AppendZoneLines(sb, zones, "    ");
            sb.Append("  }");
            if (withItems)
            {
                sb.Append(",\n  \"items\": [\n");
                AppendItemLines(sb, anim, "    ");
                sb.Append("  ]");
            }
            sb.Append("\n}\n");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[Dump] {cueId} 终态已写入 {outPath}（{anim.ActorCount} 件，{zones.Count} 个 zone）");
            EditorApplication.Exit(0);
        }

        /// <summary>把当前牌桌按 zone 聚合成**语义状态**（件数 / 身份 / 画面上实际显示哪一面）。</summary>
        private static SortedDictionary<string, ZoneAgg> CollectZones(TutorialCueAnimPlayer anim)
        {
            var zones = new SortedDictionary<string, ZoneAgg>();
            // **先把全部 zone 摆上（哪怕是空的）**：zone 是"世界里的位置"，一开始就建好了；
            // 只列"有件的 zone"会让人以为区域是随件出现的，也分不清"没有这个区域"和"区域是空的"。
            // （用户 2026-09：这些 zone 应该在动画一开始就创建好。）
            foreach (var z in anim.Store.Zones)
                if (z != null && !string.IsNullOrEmpty(z.id) && !zones.ContainsKey(z.id))
                    zones[z.id] = new ZoneAgg();
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
                // face_up/face_down 只统计**真的有正反面**的件（有背面贴图才有"面"可谈）。
                // 不这么收的话，单面件会按 `Flipped` 的默认值被记成"3 件朝下"，
                // 而画面上明明显示的是正面 —— 契约一断言就报"期望朝上、实际朝下"，
                // 让人去追一个**根本不存在**的状态（贵族板块就是这种：只有一面）。
                // 判据与逐件输出（AppendItemLines）用同一个 helper，两处不许各写一套。
                if (HasTwoSides(it)) { if (it.Flipped) agg.FaceUp++; else agg.FaceDown++; }
                if (!agg.Kinds.TryGetValue(it.KindKey, out var ka))
                {
                    ka = new KindAgg();
                    agg.Kinds[it.KindKey] = ka;
                }
                ka.Count++;
                if (HasTwoSides(it)) { if (it.Flipped) ka.FaceUp++; else ka.FaceDown++; }
                switch (it.Showing)
                {
                    case "face": ka.ShowsFace++; break;
                    case "back": ka.ShowsBack++; break;
                    default: ka.Hidden++; break;
                }
            }
            return zones;
        }

        /// <summary>
        /// 把 zone 状态写进 JSON（不含外层大括号），每行前置 indent。
        ///
        /// 两种粒度都写：
        ///   zones[k].count / face_up / face_down / shows_* —— 这个区域整体
        ///   zones[k].kinds[身份] —— **按身份分的状态**（几张、几张朝上、实际显示哪一面）
        ///
        /// 为什么要做到 kinds 这一层：曾经"契约全 PASS 而画面是错的"——契约只统计
        /// "朝上几张、朝下几张"，看不出"**哪一张**朝上"。牌堆里 4 张真牌 + 32 张垫牌，
        /// 最上面那张真牌正面朝上时，zone 级统计和契约对得上，只有按身份看才露馅。
        /// </summary>
        private static void AppendZoneLines(System.Text.StringBuilder sb,
            SortedDictionary<string, ZoneAgg> zones, string indent)
        {
            int zi = 0;
            foreach (var kv in zones)
            {
                zi++;
                var agg = kv.Value;
                sb.Append($"{indent}\"{kv.Key}\": {{ \"count\": {agg.Count}, " +
                          $"\"face_up\": {agg.FaceUp}, \"face_down\": {agg.FaceDown}, " +
                          $"\"shows_face\": {agg.ShowsFace}, \"shows_back\": {agg.ShowsBack}, " +
                          $"\"hidden\": {agg.Hidden}, \"kinds\": {{");
                int ki = 0;
                foreach (var k in agg.Kinds)
                {
                    ki++;
                    var ka = k.Value;
                    sb.Append($"\"{k.Key}\": {{ \"count\": {ka.Count}");
                    if (ka.FaceUp > 0) sb.Append($", \"face_up\": {ka.FaceUp}");
                    if (ka.FaceDown > 0) sb.Append($", \"face_down\": {ka.FaceDown}");
                    if (ka.ShowsFace > 0) sb.Append($", \"shows_face\": {ka.ShowsFace}");
                    if (ka.ShowsBack > 0) sb.Append($", \"shows_back\": {ka.ShowsBack}");
                    if (ka.Hidden > 0) sb.Append($", \"hidden\": {ka.Hidden}");
                    sb.Append(" }");
                    if (ki < agg.Kinds.Count) sb.Append(", ");
                }
                sb.Append("} }");
                if (zi < zones.Count) sb.Append(",");
                sb.Append("\n");
            }
        }

        /// <summary>每个身份的合计（件数 + 面 + 实际显示）。</summary>
        private class KindAgg
        {
            public int Count, FaceUp, FaceDown;
            public int ShowsFace, ShowsBack, Hidden;
        }

        private class ZoneAgg
        {
            public int Count, FaceUp, FaceDown;
            public int ShowsFace, ShowsBack, Hidden;   // 画面上实际显示哪一面
            public SortedDictionary<string, KindAgg> Kinds = new SortedDictionary<string, KindAgg>();
        }

        /// <summary>
        /// 每件组件的状态，**一行一件**（要排查具体是哪一件出问题时看这个）。
        ///
        /// 字段就是"组件状态"那句话的展开：它在哪个 zone、第几位、哪一面、画面上显示哪一面，
        /// 以及它**是本体里的什么**（concept —— 由模板的 concept/concept_by_palette 决定，
        /// 纯视觉件没有）。
        ///
        /// face 只对**真的有正反面**的件输出：判据是它有没有背面贴图（有 back_image 才有）。
        /// 宝石没有背面，硬写个 face 只会让人以为它也能翻面。
        /// </summary>
        /// <summary>
        /// 这件**真的有正反面**吗 —— 判据是它有没有背面贴图。
        ///
        /// 没有背面的件（宝石、只有一面的贵族板块）不该出现在 `face_up/face_down` 的统计里：
        /// 那个数一出来，人就会以为它翻了面，而去追一个不存在的状态。
        /// 逐件输出与区域聚合必须用**同一个判据**，否则同一份采样里两个数字互相矛盾。
        /// </summary>
        /// <summary>写进 JSON 的字符串转义（日志内容不可控，必须转义）。</summary>
        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        private static bool HasTwoSides(ZoneItem it) =>
            it?.Actor != null && it.Actor.BackSprite != null;

        private static void AppendItemLines(System.Text.StringBuilder sb, TutorialCueAnimPlayer anim, string indent)
        {
            // 先取成列表再按下标走：Store.Items 是 IEnumerable（每次枚举都是新的），
            // 既避免在循环里反复枚举，也让 .Count 是 List 的属性而不是 LINQ 方法组。
            var all = new List<ZoneItem>(anim.Store.Items);
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                var actor = it.Actor;
                string concept = ConceptOf(it);
                string face = HasTwoSides(it) ? (it.Flipped ? "up" : "down") : null;
                sb.Append($"{indent}{{ \"id\": \"{it.Id}\", \"kind\": \"{it.KindKey}\"");
                if (!string.IsNullOrEmpty(concept)) sb.Append($", \"concept\": \"{concept}\"");
                sb.Append($", \"zone\": \"{it.ZoneId}\", \"order\": {it.Order}");
                if (face != null) sb.Append($", \"face\": \"{face}\"");
                sb.Append($", \"shows\": \"{it.Showing}\" }}");
                sb.Append(i < all.Count - 1 ? ",\n" : "\n");
            }
        }

        /// <summary>这件组件实例化的是哪个概念：先按色板查，再退回模板上的 concept。</summary>
        private static string ConceptOf(ZoneItem it)
        {
            var tpl = it?.Template;
            if (tpl == null) return null;
            if (tpl.concept_by_palette != null)
                foreach (var e in tpl.concept_by_palette)
                    if (e != null && e.palette == it.PaletteName && !string.IsNullOrEmpty(e.concept))
                        return e.concept;
            return tpl.concept;
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

            // 像真播放器那样把**整条轨道**的顺序交过去（含没有动画的 cue）。
            // 少了这一步，入口链重放找不到"没有动画的目标 cue"就会一路重放到全片终态 ——
            // 复现工具本身就会失真（实测会得到"全片终态"：宝石堆 17 件 = 20−3）。
            var runtimePath = Path.Combine(gameRoot, "tutorial", "full.runtime.json");
            if (File.Exists(runtimePath))
            {
                var rt = JsonUtility.FromJson<TutorialCueDoc>(File.ReadAllText(runtimePath));
                if (rt?.cues != null)
                {
                    var ids = new List<string>(rt.cues.Count);
                    foreach (var c in rt.cues) if (c != null) ids.Add(c.id);
                    anim.SetCueOrder(ids);
                }
            }

            // 播放器真实用的路径现在**只有一条**：LoadCue 自己负责入口状态
            // （不接续时从根重放到本条之前，接续时沿用上一条终态）。
            // 以前这里还要复现 PlayCue → ApplyEntryState + LoadCue 两步（用反射调私有方法），
            // 而那套重复机制正是"顺序播放也把前一条 create 的东西清掉"的根源 —— 已经删掉。
            string[] path = { "setup.cards.002.1", "setup.cards.002.2",
                              "setup.gems.001.1", "setup.gems.001.2" };
            // -advanceSkip 0.5：复现用户的动作 —— **看到一半就按 →**。
            // 只对第一条生效：先 Seek 到 50%，再调 Next() 会调的 Complete()，然后照常进入下一条。
            float skipFraction = 0f;
            float.TryParse(ArgValue("-advanceSkip", "0"), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out skipFraction);

            // -advanceBack "A,B"：复现用户的**按 ←** —— 在 A 播完之后跳回 B。
            // B 常常是"没有动画数据"的 cue（例如 setup.cards.002.2 纯口播），
            // 那条路径以前不重建入口状态，于是 A 的世界和镜头都被留在原地。
            string backArg = ArgValue("-advanceBack", null);
            if (!string.IsNullOrEmpty(backArg))
            {
                var pair = backArg.Split(',');
                anim.LoadCue(gameRoot, "full", pair[0], false);
                for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f) anim.Seek(tt);
                Report(anim, pair[0] + " 播完");
                anim.LoadCue(gameRoot, "full", pair[1], false);      // ← 按 ← 走的就是这条
                Report(anim, "按 ← 回到 " + pair[1]);
                Debug.Log("[Adv] 按 ← 复现完毕（判定由人来做：市场张数/展示位件数/相机大小）");
                EditorApplication.Exit(0);
                return;
            }

            for (int i = 0; i < path.Length; i++)
            {
                // 顺序进入：i>0 表示接着上一条的终态
                anim.LoadCue(gameRoot, "full", path[i], i > 0);
                Report(anim, path[i] + " [LoadCue 后]");
                if (i == 0 && skipFraction > 0f)
                {
                    float cut = anim.TotalDuration * Mathf.Clamp01(skipFraction);
                    for (float tt = 0f; tt <= cut; tt += 0.05f) anim.Seek(tt);
                    Report(anim, $"{path[i]} 播到 {skipFraction:P0}（此刻按 →）");
                    anim.Complete();          // ← Next() 里干的就是这一下
                    Report(anim, path[i] + " Complete() 之后");
                }
                else
                {
                    for (float tt = 0f; tt <= anim.TotalDuration + 1f; tt += 0.05f) anim.Seek(tt);
                    Report(anim, path[i] + " 播完");
                }
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
            // 「没落位」：件的画面位置与它的格位坐标不一致 —— 快进打断时被卡在半空的那张。
            int offSlot = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.Actor == null) continue;
                var want = anim.Store.CurrentPosition(it);
                if ((want - it.LivePosition).sqrMagnitude > 1e-4f) offSlot++;
            }
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
            int gemPiles = 0, displays = 0;
            foreach (var it in anim.Store.Items)
            {
                if (it.ZoneId != null && it.ZoneId.StartsWith("gem_supply")) gemPiles++;
                if (it.ZoneId == "gem_display" || it.ZoneId == "gold_display") displays++;
            }
            Debug.Log($"[Adv] {tag}: 市场 {mk} 张 → 显示卡面 {mkFace} / 显示卡背 {mkBack} / 隐藏 {mkHidden}" +
                      $"（Flipped=true 有 {flippedTrue} 张；没落位 {offSlot} 件）" +
                      $"；宝石堆 {gemPiles} / 展示位 {displays}；相机 orthoSize={anim.CameraOrthoSize:0.00}");
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
            Debug.Log($"[List] {cueId} @{ArgValue("-listAt", "2.6")}s 的 {zone}：共 {items.Count} 件");
            // 以前这里只打了"共 N 件"，清单算出来没用 —— "到底哪一件不对"于是只能靠猜。
            // 一行一件，字段与 DumpState 的 items 一致。
            foreach (var it in items)
            {
                string concept = ConceptOf(it) ?? "-";
                string face = (it.Actor != null && it.Actor.BackSprite != null)
                    ? (it.Flipped ? "up" : "down") : "-";
                Debug.Log($"[List]   order {it.Order,3}  {it.Id,-28} {it.KindKey,-34} " +
                          $"concept={concept,-28} face={face,-4} shows={it.Showing}");
            }
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
