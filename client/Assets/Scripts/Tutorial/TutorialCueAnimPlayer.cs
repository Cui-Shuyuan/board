// BoardGameTutorial
// cue 内动画播放器（状态版）。
//
// 模型：动画 = 维护一组组件的状态。组件状态 = 它在哪个 zone、以什么姿态；
// 位置由 zone 的布局规则推导。因此本播放器做两件事：
//   1. 渲染层：把 ZoneStore 的每个组件实例成 sprite，位置由 (zone, slot) 算出；
//   2. 时间轴层：按相对秒数触发 cue 数据里的原语，move 的语义就是 zone → zone。
//
// 时钟由 TutorialCuePlayer 喂入（audioSource.time）：动画天然对齐口播。
// 重播当前 cue 恢复到这条 cue 的入口状态；顺序播放接着上一条的终态继续。
// 将来编译器可以离线复算每个 cue 的入口状态写进 runtime，用于任意跳转。
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public partial class TutorialCueAnimPlayer : MonoBehaviour
    {
        private const float GemPpu = 100f;

        private static Sprite solidSprite;

        [Header("Cue animation")]
        [Tooltip("整条 cue 动画总开关；关掉后退回纯音频（便于 A/B 对比）。")]
        public bool animationEnabled = true;

        [Tooltip("动画整体速度倍率，只用于调试。")]
        public float timeScale = 1f;

        public string CueId { get; private set; }
        public string Note { get; private set; }
        public bool IsLoaded => cueDoc != null;

        /// <summary>
        /// 测试用：把时间轴推进到 target 秒，并返回一个「把当前所有补间跑完」的迭代器。
        /// 批处理没有帧循环，调用方需自行 MoveNext 驱动（配合 Time.captureDeltaTime）。
        /// </summary>
        public IEnumerator DriveForTest(float target)
        {
            const float step = 1f / 60f;
            for (float t = 0f; t <= target; t += step)
            {
                Seek(t);
                yield return null;
            }
            Seek(target);
            // 让已经启动的补间继续跑完
            for (int i = 0; i < 240 && running.Count > 0; i++) yield return null;
        }

        /// <summary>自检用：按目标 id 求出本条 cue 的搬运计划。</summary>
        public MovePlan PlanMoveForTest(string targetId)
        {
            if (cueDoc?.events == null) return null;
            foreach (var ev in cueDoc.events)
            {
                if (ev.action != "transfer" || ev.target != targetId) continue;
                var actor = FindActor(ev.target);
                if (actor?.Item == null) continue;
                int ord = ev.order == -2 ? ev.slot : ev.order;
                var from = actor.LivePosition;
                var to = ev.destination != null ? Store.ZonePosition(ev.destination, ord) : Store.CurrentPosition(actor.Item);
                return new MovePlan
                {
                    Item = actor.Item, Destination = ev.destination, Order = ord, InPlace = false,
                    From = from, To = to, ToFace = ev.to,
                };
            }
            return null;
        }

        /// <summary>
        /// 设置取景目标。zoneId 为空或 "board" 表示整桌取景；
        /// "cards" / "supply" 是**组取景 token**（分别框住展示位三张卡背、整排供应区）；
        /// 其余按 zone id 特写该区域。
        /// </summary>
        public void SetFraming(string zoneId, float padding = 0f)
        {
            frameZoneId = string.IsNullOrEmpty(zoneId) || zoneId == "board" ? null
                : (zoneId == "cards" ? FrameCardsToken
                : (zoneId == "supply" ? FrameSupplyToken : zoneId));
            framePadding = padding;
            FitCamera();
        }

        /// <summary>自检用：本条 cue 实际载入的事件（用于核对 JsonUtility 有没有丢字段）。</summary>
        public IEnumerable<CueAnimEvent> EventsForTest =>
            cueDoc != null && cueDoc.events != null ? cueDoc.events : new List<CueAnimEvent>();

        /// <summary>自检用：当前显示的图片名（没有则空）。</summary>
        public string BoxPictureForTest => currentPicture;

        /// <summary>
        /// 自检用：组件的**画面姿态**汇总 —— 面片是否正对相机、缩放是否等比。
        ///
        /// 状态采样（zone/件数/身份/哪一面）看不见姿态，而"组件被压矮了"正是姿态问题：
        /// 面片若立在世界的 XY 平面、相机又是斜视的，高度会被 cos(pitch) 投影压扁。
        /// 这里断言每个 actor 的朝向都等于 `SpriteRotation(自己的 roll)`（即面片平面平行于
        /// 屏幕），且缩放等比（xyz 里 xy 相等）—— 全部为 0 才说明组件是按原始尺寸画的。
        ///
        /// 注意：`flip` 动画中途会故意把 scale.x 压到 0（翻面），所以这条只该在**终态**查。
        /// </summary>
        public string ShapeReportForTest()
        {
            int n = 0, badRot = 0, badScale = 0;
            float worst = 0f;
            foreach (var it in Store.Items)
            {
                var go = it?.Actor?.Go;
                if (go == null) continue;
                n++;
                float d = Quaternion.Angle(go.transform.localRotation, SpriteRotation(it.Actor.LiveRotation));
                if (d > 0.5f) { badRot++; if (d > worst) worst = d; }
                var s = go.transform.localScale;
                if (Mathf.Abs(s.x - s.y) > 0.001f * Mathf.Max(1f, Mathf.Abs(s.y))) badScale++;
            }
            return $"件={n} 姿态不符={badRot}（最大角差 {worst:0.0}°）非等比缩放={badScale}" +
                   $" 相机俯角={CameraPitch:0}° orthoSize={CameraOrthoSize:0.00}";
        }

        /// <summary>自检用：临时注册一个容器。</summary>
        public void RegisterContainerForTest(string id, string[] itemIds)
        {
            if (stage == null) return;
            if (stage.containers == null) stage.containers = new List<StageContainer>();
            stage.containers.RemoveAll(c => c != null && c.id == id);
            stage.containers.Add(new StageContainer { id = id, items = new List<string>(itemIds) });
        }

        /// <summary>自检用：直接触发一条事件。</summary>
        public void TriggerForTest(CueAnimEvent ev)
        {
            currentEventAt = ev.at;
            Trigger(ev);
        }

        /// <summary>自检用：取某组件的当前缩放。</summary>
        public float ScaleOf(string itemId)
        {
            var a = FindActor(itemId);
            return a?.Go != null ? a.Go.transform.localScale.x : 0f;
        }

        /// <summary>自检用：取某组件的基准缩放。</summary>
        public float BaseScaleOf(string itemId)
        {
            var a = FindActor(itemId);
            return a != null ? a.BaseScale.x : 0f;
        }

        /// <summary>当前登记的片段数（自检用：应随 cue 长度有界，不应累积）。</summary>
        public int ClipCountForTest => clips.Count;

        /// <summary>当前牌桌上已有的组件数（自检/调试用）。</summary>
        public int ActorCount
        {
            get
            {
                int n = 0;
                foreach (var it in Store.Items) if (it.Actor != null) n++;
                return n;
            }
        }
        public bool HasEvents => cueDoc != null && cueDoc.events != null && cueDoc.events.Count > 0;
        public float CameraGroundHalfWidth { get; private set; }
        public float CameraOrthoSize { get; private set; }
        public ZoneStore Store { get; private set; } = new ZoneStore();

        private StageDoc stage;
        private CueAnimDoc cueDoc;
        private string gameRootPath;
        private GameObject animRoot;
        private Camera animCamera;
        private const string FrameCardsToken = "__cards__";

        /// <summary>
        /// 取景 "supply"：把整排供应区一起框住 —— 用 <see cref="SupplyPalette"/> 色板的那些 zone
        /// （璀璨宝石里就是宝石 5 色 + 黄金共 6 堆）。
        ///
        /// 为什么需要它：整桌取景（board）按 extent 框，而牌桌「宽 4.5 × 深 6.6」、屏幕是 16:9 ——
        /// 按深度取景会在左右留一大堆空，宝石只占屏宽 4% 左右，「每种 4/5/7 枚」根本数不清。
        /// 这与 "cards"（showcase 三张卡背并排）是同一类需求，所以同样做成取景 token；
        /// 但成员不写死在代码里，而是按**色板**判定，换游戏不用改代码。
        /// </summary>
        private const string FrameSupplyToken = "__supply__";
        private const string SupplyPalette = "panel_supply";

        private string frameZoneId;      // 非空 = 特写取景到该 zone
        private float framePadding;
        private SpriteRenderer boxSprite;
        private string currentPicture;

        private readonly Dictionary<string, CueAnimActor> actors = new Dictionary<string, CueAnimActor>();
        /// <summary>区域底板：zone id → 代表它的装饰件 id（静态底板，不带高亮）。</summary>

        /// <summary>底板 id → 它代表的 zone 列表；这些 zone 里没有可见组件时底板不显示。</summary>

        /// <summary>zone id → 该 zone 专属的高亮底板（每个 zone 一块，绝不共用）。</summary>
        private readonly Dictionary<string, CueAnimActor> zoneGlow = new Dictionary<string, CueAnimActor>();

        private struct PendingScale
        {
            public float At;
            public CueAnimActor Actor;
            public float Factor;
            public CueAnimEvent Event;
        }

        /// <summary>错峰触发的缩放（scale + stagger）。</summary>
        private readonly List<PendingScale> pendingScales = new List<PendingScale>();
        private readonly List<Coroutine> running = new List<Coroutine>();

        private float clock = -1f;
        private float currentEventAt;
        private int nextIndex;
        private ZoneSnapshot entrySnapshot;

        /// <summary>
        /// 只推状态、不碰渲染对象。用于"从根重放到入口"（跳转/重播/上一条）——
        /// 重放时不需要也没法动画，只要把 Store 推到最后；渲染对象由本条 cue 的
        /// BuildActorObjects() 统一建。
        /// </summary>
        private bool stateOnly;

        // 自检：这条 track 的脚本读过没有、里面有多少段（一次就够，别每条 cue 刷屏）
        private static readonly HashSet<string> preflightDone = new HashSet<string>();
        private static int scriptMissing;

        /// <summary>
        /// 第一次读某条 track 的脚本时，把"读到了什么"打出来。
        ///
        /// 为什么需要：数据版本不对时（最典型是工作区没同步），引擎的表现是**静默**的 ——
        /// 每条 cue 都走"没有动画数据，只保留牌桌"，画面就像卡住一样，没有任何报错。
        /// 2026-09-18 用户就是这样：Windows 工作区落后 124 个提交，宝石那一节 9 条动画
        /// 根本不在那个脚本里，于是"口播在说宝石、画面停在 cue13"。
        /// 一行日志换一个小时的排查。
        /// </summary>
        private static void PreflightScript(string path)
        {
            if (!preflightDone.Add(path)) return;
            if (!File.Exists(path))
            {
                Debug.LogError($"[TutorialCueAnim] 找不到动画脚本 {path} —— " +
                               $"所有 cue 都会退化成「只保留牌桌」（画面不动）。工作区同步了吗？");
                return;
            }
            TrackAnimDoc doc = null;
            try { doc = JsonUtility.FromJson<TrackAnimDoc>(File.ReadAllText(path)); }
            catch (System.Exception e) { Debug.LogError($"[TutorialCueAnim] 脚本解析异常 {path}: {e.Message}"); }
            int total = doc?.cues?.Count ?? 0;
            int withEvents = 0;
            if (doc?.cues != null)
                foreach (var c in doc.cues)
                    if (c?.events != null && c.events.Count > 0) withEvents++;
            Debug.Log($"[TutorialCueAnim] 动画脚本 {path}：{total} 段 cue，其中 {withEvents} 段有事件");
        }

        // ── 加载 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 载入牌桌与这一条 cue，并把画面摆到「本条 cue 开始播放时」的状态。
        /// continueState=true 接着上一条的终态；false 从牌桌 initial + 本条 start 起。
        /// </summary>
        public bool LoadCue(string gameRoot, string track, string cueId, bool continueState)
        {
            CueId = cueId;
            Note = null;
            cueDoc = null;
            StopAnimations();
            // 注意：**这里不销毁画面对象**。
            // 没有动画数据的 cue 要「保留牌桌」，若在开头就 ClearActors，
            // store 里虽有数据但渲染对象已没了 —— 表现为「牌不见了」。
            // 只在确定要重建画面（有动画 / 需要搭初始牌桌）时才清理。

            // 关键：换 cue 必须清空片段列表。
            // 曾经只在「回退」时清理，于是每条 cue 都往里加十几个片段、从不释放——
            // 播到第 12 条时已累积 90+ 个陈旧片段，它们会被重新采样套用，
            // 表现为「动作做了但没实际发生」「整行先出现」。
            clips.Clear();
            lastTraceTime = -99f;

            if (!animationEnabled || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(cueId))
            {
                Debug.LogWarning($"[TutorialCueAnim] 跳过动画: enabled={animationEnabled} " +
                                 $"gameRoot='{gameRoot}' cue='{cueId}'");
                ClearScene();
                return false;
            }

            // 一个动画一个文件：`anim/{track}.json`，里面按轨道顺序放着每条 cue。
            string path = Path.Combine(gameRoot, "tutorial", "anim", track + ".json");
            PreflightScript(path);

            TrackAnimDoc trackDoc = null;
            CueAnimDoc found = null;
            if (File.Exists(path))
            {
                trackDoc = JsonUtility.FromJson<TrackAnimDoc>(File.ReadAllText(path));
                if (trackDoc == null || trackDoc.cues == null)
                {
                    Debug.LogError($"[TutorialCueAnim] 解析失败（cues 读不到）: {path}");
                    cueDoc = null;
                    return false;
                }
                foreach (var c in trackDoc.cues)
                    if (c != null && c.cue == cueId) { found = c; break; }

                if (found == null)
                {
                    // 脚本里根本没有这条 cue。这与"契约写了、动画还没写"（有段但 events 为空）
                    // 是两回事：**脚本里没有这一段**，多半是数据版本不对 ——
                    // 最典型的就是工作区没同步（引擎读到的是几天前的脚本）。
                    // 所以第一次遇到就报 error，并把"脚本里到底有多少段"一并说出来，
                    // 否则这个失败完全是静默的：画面只会"什么都没发生"。
                    scriptMissing++;
                    if (scriptMissing <= 3)
                        Debug.LogError($"[TutorialCueAnim] 动画脚本里没有这条 cue: {cueId}（{path}）。" +
                                       $"脚本共 {trackDoc.cues.Count} 段；若刚加过动画，多半是工作区没同步 —— " +
                                       $"检查 {path} 是不是最新版。");
                }
                else if (found.events == null || found.events.Count == 0)
                {
                    // 契约写了、动画还没写（例如 setup.nobles.001.1）：本条无动画，保留牌桌
                    Debug.Log($"[TutorialCueAnim] 本条 cue 还没有动画数据（events 为空），只保留牌桌: {cueId}");
                    found = null;
                }
            }

            if (found == null)
            {
                // 没有动画数据（文件不存在 / 没有这条 cue / events 为空）。
                // 关键：即使没有动画，也要把**牌桌**搭出来并保留住 —— 否则开场那几条
                // （背景介绍等）会让画面完全空白，看起来像整个模块坏了。
                // 注意 Store 是同一个实例，所以后续 cue 会接着这张桌子继续。
                Debug.Log($"[TutorialCueAnim] 本条 cue 无动画数据，只保留牌桌: {cueId}");
                CueId = cueId;
                clock = -1f;
                nextIndex = 0;

                // 判据是「**牌桌建过没有**」= stage 载入过。**不要**用"画面上有没有对象"：
                // 介绍那几条 cue 桌上本来就是空的（一件都没有）—— 那时 animRoot 都不存在，
                // 用画面判会把"空桌"误判成"还没建桌"，于是顺序播放时也去重建入口状态
                // （实测：整条轨道的状态被重放一遍，市场 12 张变 24 张）。
                bool hasScene = stage != null;
                // **牌桌还没搭过**（!hasScene）**或者本条是跳转/重播/上一条进来的**（!continueState）：
                // 都要把状态重建到本条的入口。
                //
                // 少了后半句就是用户 2026-09 报的那个现象：从宝石介绍按 ← 退回卡牌那条
                // （`setup.cards.002.2`，它**没有动画数据**），画面还停在宝石上 ——
                // 状态没重建（展示位那 5 枚样本还在），镜头也没重建（市场其实在，只是被留在镜头外）。
                if (!hasScene || !continueState)
                {
                    ClearActors();
                    LoadStage(gameRoot, trackDoc != null ? trackDoc.stage : null);
                    // ReplayEntryChain 自己会 Reset + ApplyInitial，再按顺序把本条之前的事件推到终态
                    ReplayEntryChain(trackDoc, cueId);
                    BuildActorObjects();
                    SyncActorsToStore();
                    EnsureCamera();
                    SetBackground();
                    FitCamera();
                    // 整幅图也要回到**入口**状态（与正常载入那条路一致）：
                    // 判据是"脚本里没写图就没有图"，不能沿用上一条留下的盒面。
                    string entryPic = EntryPictureFor(trackDoc, cueId);
                    TriggerShowBox(new CueAnimEvent
                    {
                        action = "showbox",
                        picture = entryPic,
                        on = string.IsNullOrEmpty(entryPic) ? 0f : 1f,
                    });
                }
                return false;
            }

            ClearActors();   // 确定要重建画面了，才销毁旧对象
            cueDoc = found;
            Note = cueDoc.note;

            LoadStage(gameRoot, trackDoc.stage);
            // 入口的整幅图状态（要用刚载入的 stage 的 default_picture 当树根，所以放在这之后）
            string entryPicture = EntryPictureFor(trackDoc, cueId);

            // 续接（顺序播放）：接着上一条的终态。
            // 不续接（跳转 / 重播 / 按 B 预览）：退回牌桌初始态，再按本条 cue 的 start 布置。
            // 必须 Reset+ApplyInitial，否则跳到后面的 cue 会带着上一轮留下的组件。
            if (!continueState)
            {
                // 跳转 / 重播 / 上一条：**从根重放到本条之前**，得到真正的入口状态。
                //
                // 以前这里是 Reset + ApplyInitial —— 那等于"回到开局"：前序 cue 里 create 出来的
                // 东西全没了（用户报的"跳进 cue 11 什么都看不到"：那三张卡背是 cue 10 create 的）。
                // 顺序播放（continueState=true）接着上一条的终态，不需要重放。
                //
                // 这样"跳转"和"顺序播放"走的是**同一条状态路径**，两种走法不可能再不一致；
                // 内存里记的"编译器离线复算入口状态"，就是这件重放的结果预先算好而已。
                ReplayEntryChain(trackDoc, cueId);
                cueDoc = found;          // 重放会把 cueDoc 换成前序 cue，这里换回来
                CueId = cueId;
                Note = found.note;
            }
            ApplyCueStart();

            // 这里曾经有一段「把所有 market_card_* 设成未翻开」的代码 —— 那是旧模型的遗留：
            // 当年市场牌预先创建、停在牌堆位置当牌背，所以要藏起来。
            // 现在市场牌是**发牌时从牌堆搬过来的真牌**，落位后本就该显示真卡面；
            // 这一段会把它们全部设回"未翻开"，表现就是**发完牌后 12 张市场牌全部朝下**
            // （用户报的第二个现象）。牌堆的"When 朝下"由 create 的 face_down 显式声明。

            BuildActorObjects();
            SyncActorsToStore();

            EnsureCamera();
            SetBackground();
            FitCamera();

            // 应用入口的整幅图状态：有图就显示，没有就**清掉**（这一步同样重要 ——
            // 否则从"有图"的 cue 跳进"没图"的 cue 时，旧图会一直留在画面上）。
            TriggerShowBox(new CueAnimEvent
            {
                action = "showbox",
                picture = entryPicture,
                on = string.IsNullOrEmpty(entryPicture) ? 0f : 1f,
            });

            CaptureEntry();
            clock = 0f;
            nextIndex = 0;
            return true;
        }

        private string OffstageZoneId
        {
            get
            {
                if (offstageZoneId == null)
                {
                    offstageZoneId = "";
                    foreach (var zone in Store.Zones)
                        if (zone.role == "offstage") { offstageZoneId = zone.id; break; }
                }
                return offstageZoneId;
            }
        }

        private string offstageZoneId;

        private void LoadStage(string gameRoot, string stageRel)
        {
            string rel = string.IsNullOrEmpty(stageRel) ? "_stage/splendor.table" : stageRel;
            string path = Path.Combine(gameRoot, "tutorial", "anim", rel + ".json");
            if (!File.Exists(path))
            {
                Debug.LogError($"[TutorialCueAnim] missing stage file: {path}");
                stage = null;
                Store.LoadStage(null);
                return;
            }

            stage = JsonUtility.FromJson<StageDoc>(File.ReadAllText(path));
            Store.LoadStage(stage);
            offstageZoneId = null;
            gameRootPath = gameRoot;
            Debug.Log($"[TutorialCueAnim] 牌桌载入: stage={(stage == null ? "NULL" : stage.game_id)} " +
                      $"cue={CueId} events={(cueDoc?.events == null ? 0 : cueDoc.events.Count)}");
        }

        /// <summary>本条 cue 播放前对状态做的准备（清空 / 预置 / 临时组件）。</summary>
        private void ApplyCueStart()
        {
            var start = cueDoc.start;
            if (start == null) return;

            if (start.clear) Store.Reset();

            if (start.set != null)
            {
                foreach (var seed in start.set)
                {
                    string seedZone = Store.ResolveZoneRef(seed.zone);
                    string seedTemplate = seed.template, seedPalette = seed.palette;
                    if (seed.what != null && !string.IsNullOrEmpty(seed.what.concept))
                    {
                        // 预置是"凭空保证这里有 N 件真件"，所以用**真件候选**：
                        // 介绍用的样本（`sample:true`）绑同一套概念，但它不可能在真件区里，
                        // 让它参与竞争只会把"盒里的黄金"判成说不清（2026-09 踩过）。
                        var cands = Store.ConceptCandidatesReal(seed.what.concept, seed.what.parts);
                        if (cands.Count != 1)
                        {
                            Debug.LogError($"[TutorialCueAnim] start.set 的 what='{seed.what.concept}' " +
                                           $"有 {cands.Count} 个候选，预置必须唯一（cue {CueId}）");
                            continue;
                        }
                        seedTemplate = cands[0].TemplateId;
                        if (!string.IsNullOrEmpty(cands[0].Palette)) seedPalette = cands[0].Palette;
                    }
                    int want = seed.expand_to > 0 ? seed.expand_to : Mathf.Max(1, seed.count);
                    int have = Store.CountIn(seedZone, seedPalette, seedTemplate);
                    int need = Mathf.Max(0, want - have);
                    if (need == 0) continue;

                    // 直接补差额就够：**游戏盒是抽象概念、没有实体**（用户 2026-09），
                    // 所以不存在"这一份先放在 offstage 表示还在盒里"这回事了 ——
                    // 缺几件就 create 几件（"从盒子里拿出来" = create）。
                    // 每次重新数一遍现有数量，所以重播/重复载入也不会翻倍。
                    if (need > 0) Store.Spawn(seedTemplate, seedPalette, seedZone, need);
                }
            }

        }

        // ── 渲染层 ────────────────────────────────────────────────────────

        private void BuildActorObjects()
        {
            var rootGo = new GameObject("CueAnimRoot");
            rootGo.transform.SetParent(transform, false);
            animRoot = rootGo;

            // 注意：**不画 zone 底板**。
            // 曾经每个锚点都生成一块半透明色块（市场/牌堆/供应区…），但 zone 是逻辑概念，
            // 不需要可视化 —— 它们只是在画面上留下脏色块。
            // 需要强调某个区域时用 highlight 原语（临时出现、会消失）。
            // 锚点数据仍然保留：自检要用它的 zones 列表算格位范围。

            foreach (var item in Store.Items)
            {
                if (actors.ContainsKey(item.Id)) continue;
                BuildActorObject(item);
            }
        }

        /// <summary>为单个组件建立可视对象（开局批量创建与新 create 事件共用）。</summary>
        private void BuildActorObject(ZoneItem item)
        {
            if (stateOnly) return;   // 重放入口链时只推状态
            if (item == null || actors.ContainsKey(item.Id)) return;
            var itemTpl = EffectiveTemplate(item.Template, item.PaletteName);

            // 扫描件是**实物照片**，不能再乘色板色 —— 那等于给照片套一层滤镜
            // （用户报的"宝石怎么都加上了滤镜"）。色板色只该给程序化占位图着色。
            // 判定用"解析得到扫描图路径"，与 ResolveSprite 的选择标准一致。
            if (ResolveImagePath(itemTpl) != null) item.BaseColor = Color.white;

            var go = CreateSpriteObject("item:" + item.Id, itemTpl, item.BaseColor);
            if (animRoot != null) go.transform.SetParent(animRoot.transform, true);
            var sr = go.GetComponent<SpriteRenderer>();
            var actor = new CueAnimActor(item, sr.sprite, go, sr, go.transform.localScale);

            // 正反两面都准备好：翻面时只换贴图，不重建对象。
            actor.FaceSprite = sr.sprite;
            actor.EffectiveTemplate = itemTpl;

            var backPath = ResolveBackImagePath(itemTpl);
            if (backPath != null)
            {
                var back = CardImageLoader.Load(backPath, itemTpl.shape);
                if (back != null) actor.BackSprite = back;
            }

            item.Actor = actor;
            actors[item.Id] = actor;

            // 落在它的逻辑格位上，并按模板默认透明度显示
            var pos = Store.CurrentPosition(item);
            actor.LivePosition = pos;
            go.transform.localPosition = pos;
            actor.LiveAlpha = itemTpl.alpha;
            ApplyAlpha(actor);
        }

        /// <summary>
        /// 生成 sprite 时用的模板。实例的色板（如 gem_diamond）可能只写在 stage.initial / start.set 上，
        /// 模板里没有 —— 必须并进来，否则找不到该色板的扫描图，会退化成白色方块。
        /// </summary>
        private static StageTemplate EffectiveTemplate(StageTemplate tpl, string paletteName)
        {
            if (tpl == null || string.IsNullOrEmpty(paletteName) || tpl.palette == paletteName) return tpl;

            var clone = new StageTemplate
            {
                id = tpl.id, shape = tpl.shape, palette = paletteName,
                sprite = tpl.sprite, face_image = tpl.face_image, back_image = tpl.back_image,
                world_size = tpl.world_size, width = tpl.width, height = tpl.height,
                alpha = tpl.alpha, rotation = tpl.rotation,
                sorting_order = tpl.sorting_order, highlight = tpl.highlight,
                // 注意：新增模板字段必须在这里也复制一份，否则实例色板与模板不同时会静默丢失。
                // 已经漏过 from_zone / tint（表现各不相同、都很难查）。
                from_zone = tpl.from_zone,
            };
            return clone;
        }

        /// <summary>取景相机的俯仰角（度）：90 = 正俯视，越小越斜。</summary>
        private float CameraPitch
        {
            get
            {
                return stage?.board != null && stage.board.camera_pitch > 0f
                    ? stage.board.camera_pitch
                    : 90f;
            }
        }

        /// <summary>
        /// 组件面片的基准朝向：**与相机同朝向**，即面片平面平行于屏幕。
        ///
        /// 相机是斜视的（`stage.board.camera_pitch`）。面片如果固定立在世界的 XY 平面里
        /// （rotation = 0），斜看过去高度就被 cos(pitch) 压扁 —— 50° 时只剩 64%，
        /// 用户看到的就是"摄像机明明是 50°，组件却像被整体压矮了一截"。
        /// 让面片跟着相机转，组件在任何 pitch 下都按**原始尺寸、原始宽高比**显示。
        ///
        /// `roll` 是组件自己的平面内旋转（洗牌/散开的轻微歪斜），叠加在面片平面里，
        /// 所以它仍然是绕"贴图中心"转，语义不变。
        /// </summary>
        private Quaternion SpriteRotation(float roll)
        {
            return Quaternion.Euler(CameraPitch, 0f, 0f) * Quaternion.Euler(0f, 0f, roll);
        }

        private GameObject CreateSpriteObject(string name, StageTemplate tpl, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(animRoot.transform, false);
            // 建出来就正对相机：否则第一帧还没走到复位分支时会以"立着的"姿态闪一下。
            go.transform.localRotation = SpriteRotation(tpl.rotation);

            var sr = go.AddComponent<SpriteRenderer>();
            var sprite = ResolveSprite(tpl);
            sr.sprite = sprite;
            sr.sortingOrder = tpl.sorting_order;

            // 「动画前不可见」的组件（例如在牌堆上待发的市场牌）必须**建出来就是透明的**。
            // 曾经这里一律用模板 alpha（默认 1），靠之后采样才置 0 —— 于是
            // LoadCue 到第一次 Seek 之间有一段空档，十几张待发牌会以不透明状态
            // 叠在牌堆上闪一下（用户看到「牌堆闪了一下」）。
            color.a = Mathf.Clamp01(tpl.alpha);
            sr.color = color;

            var scale = LocalScaleFor(tpl, sprite);
            if (tpl.highlight)
            {
                sr.enabled = false;
                go.transform.localScale = Vector3.zero;
            }
            else
            {
                go.transform.localScale = scale;
            }
            return go;
        }

        private Sprite ResolveSprite(StageTemplate tpl)
        {
            var image = ResolveImagePath(tpl);
            if (image != null)
            {
                var loaded = CardImageLoader.Load(image, tpl.shape);
                if (loaded != null)
                {
                    if (logImages) Debug.Log($"[TutorialCueAnim] 扫描图: {tpl.id} ({tpl.palette}) ← {Path.GetFileName(image)}");
                    return loaded;
                }
                Debug.LogWarning($"[TutorialCueAnim] 扫描图加载失败: {image}");
            }

            if (!string.IsNullOrEmpty(tpl.sprite))
            {
                var loaded = Resources.Load<Sprite>(tpl.sprite);
                if (loaded != null) return loaded;
                var tex = Resources.Load<Texture2D>(tpl.sprite);
                if (tex != null)
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), GemPpu);
                Debug.LogWarning($"[TutorialCueAnim] sprite not found: {tpl.sprite}");
            }

            if (tpl.shape == "panel" || tpl.shape == "dot" || tpl.shape == "shadow")
                return SharedSolidSprite();

            var color = Palette.Resolve(tpl.palette);
            if (tpl.shape == "card")
            {
                // 贵族面向下用 noble 色板的方块；发展卡用等级色卡背。
                return tpl.palette == "noble" ? GameSpriteFactory.NobleTile(color) : GameSpriteFactory.CardBack(color);
            }

            return GameSpriteFactory.Gem(color);
        }

        /// <summary>
        /// 色板名 → 扫描图文件名。宝石六色共用一个 gem 模板，靠 palette 区分实物图。
        /// 键是 stage 里用的 palette 名；值是 media/card 下的文件名。
        /// </summary>
        private static readonly Dictionary<string, string> PaletteImages = new Dictionary<string, string>
        {
            { "gem_diamond",  "白宝石.jpg" },
            { "gem_sapphire", "蓝宝石.jpg" },
            { "gem_emerald",  "绿宝石.jpg" },
            { "gem_ruby",     "红宝石.jpg" },
            { "gem_onyx",     "黑宝石.jpg" },
            { "gem_gold",     "黄金.jpg" },
        };

        /// <summary>
        /// 找到模板对应的扫描图。优先 face_image；其次按色板名的实物图；最后按等级卡背约定。
        /// </summary>
        /// <summary>翻面用的另一面。模板写了 back_image 才有；否则不翻转。</summary>
        private string ResolveBackImagePath(StageTemplate tpl)
        {
            if (tpl == null || string.IsNullOrEmpty(tpl.back_image) || string.IsNullOrEmpty(gameRootPath))
                return null;
            var path = Path.Combine(gameRootPath, tpl.back_image);
            if (File.Exists(path)) return path;
            Debug.LogWarning($"[TutorialCueAnim] back_image 不存在: {path}");
            return null;
        }

        private string ResolveImagePath(StageTemplate tpl)
        {
            if (string.IsNullOrEmpty(gameRootPath)) return null;

            foreach (var relative in ImageCandidates(tpl))
            {
                var candidate = Path.Combine(gameRootPath, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>按顺序尝试的扫描图路径：face_image 优先，其次 media/card/ 的命名约定。</summary>
        private static IEnumerable<string> ImageCandidates(StageTemplate tpl)
        {
            if (!string.IsNullOrEmpty(tpl.face_image))
            {
                // 处理过的图优先：卡面/卡背来自 stage 的 face_image，同样走 `<原名>_cutout.png` 约定
                // （scripts/matte_pipeline.py 的产物：裁到实物、按 mm 统一尺寸、alpha 已修好）。
                string faceDir = Path.GetDirectoryName(tpl.face_image);
                string faceBase = Path.GetFileNameWithoutExtension(tpl.face_image);
                string dirPrefix = string.IsNullOrEmpty(faceDir)
                    ? "" : faceDir.Replace('\\', '/') + "/";
                yield return dirPrefix + faceBase + "_cutout.png";
                yield return tpl.face_image;
            }

            // 按色板名找实物图（宝石六色、黄金）
            if (!string.IsNullOrEmpty(tpl.palette) && PaletteImages.TryGetValue(tpl.palette, out var image))
            {
                // **处理过的图优先**：`<原名>_cutout.png` 是 scripts/matte_pipeline.py 的产物
                // （抠好的 alpha / 统一尺寸 / 可选的生成式质感），原始扫描件一个字节不动。
                // 顺序 = "越处理过的越优先"，于是"换素材处理方式"不用改引擎。
                string baseName = Path.GetFileNameWithoutExtension(image);
                yield return "media/card/" + baseName + "_cutout.png";
                yield return "media/card/" + image;
                yield return "media/card/" + baseName + ".png";
            }

            // 贵族板块
            if (tpl.palette == "noble")
            {
                yield return "media/card/贵族_0001_cutout.png";
                yield return "media/card/贵族_0001.jpg";
                yield return "media/card/贵族_0001.png";
            }

            if (tpl.shape != "card" || string.IsNullOrEmpty(tpl.palette)) yield break;
            if (!tpl.palette.StartsWith("card_level_")) yield break;

            string level = "level_" + tpl.palette.Substring("card_level_".Length);
            yield return "media/card/" + level + "_back.jpg";
            yield return "media/card/" + level + "_back.png";
        }

        /// <summary>
        /// 计算 SpriteRenderer 需要的 localScale。
        ///
        /// 注意：sprite 的原生尺寸 = 像素 / pixelsPerUnit。扫描图是 712x1012 @100PPU，
        /// 原生就已经是 7.12x10.12 世界单位，所以绝不能把「目标世界尺寸」直接当缩放用，
        /// 否则卡牌会放大到占满屏幕。缩放 = 目标世界尺寸 / 原生世界尺寸。
        /// </summary>
        private static Vector3 LocalScaleFor(StageTemplate tpl, Sprite sprite)
        {
            float nativeW = 1f, nativeH = 1f;
            if (sprite != null && sprite.rect.width > 0f && sprite.rect.height > 0f)
            {
                float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : GemPpu;
                nativeW = sprite.rect.width / ppu;
                nativeH = sprite.rect.height / ppu;
            }

            if (tpl.width > 0f || tpl.height > 0f)
            {
                float wantW = tpl.width > 0f ? tpl.width : nativeW;
                float wantH = tpl.height > 0f ? tpl.height : nativeH;

                // 底板是纯色板，宽高必须各自贴合它负责的格位范围（不能等比）：
                // 等比会让高度被宽度限制，底板就盖不住整列。
                if (tpl.shape == "panel")
                    return new Vector3(wantW / nativeW, wantH / nativeH, 1f);

                // 卡牌/板块等有图案的：等比缩放，保证不拉伸变形。
                float scale = Mathf.Min(wantW / nativeW, wantH / nativeH);
                return new Vector3(scale, scale, 1f);
            }

            float target = tpl.world_size <= 0f ? 0.10f : tpl.world_size;
            float uniform = target / Mathf.Max(nativeW, nativeH);
            return new Vector3(uniform, uniform, 1f);
        }

        private static Sprite SharedSolidSprite()
        {
            if (solidSprite == null)
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var pixels = new Color[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                tex.SetPixels(pixels);
                tex.Apply();
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                solidSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            }
            return solidSprite;
        }

        /// <summary>某个 zone 里是否有「在场」的组件（offstage 的不算）。</summary>
        private bool ZoneHasContent(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return false;
            foreach (var item in Store.Items)
            {
                if (item.ZoneId != zoneId) continue;
                var zone = Store.GetZone(item.ZoneId);
                if (zone != null && zone.role == "offstage") continue;
                if (item.Actor != null && item.Actor.Go != null && item.Actor.Go.activeSelf) return true;
            }
            return false;
        }


        /// <summary>
        /// 按组件的朝向决定显示哪一面 —— **全项目只此一处定义**，flip 片段也必须遵守同一套。
        ///
        /// 约定：`ZoneItem.Flipped` = 「这张牌现在是不是正面朝上」。
        ///   ZoneItem.Flipped == true  → 正面朝上 → 显示 **FaceSprite**（真卡面）
        ///   ZoneItem.Flipped == false → 背面朝上 → 显示 **BackSprite**（没有独立背图就用 FaceSprite）
        ///
        /// 曾经有两套**相反**的规则，谁最后执行谁赢 —— 表现为"播完动画是对的、
        /// 换 cue 重建后又翻回背面"。所以这里只留一套。
        /// </summary>
        private static void RefreshFace(CueAnimActor actor)
        {
            if (actor?.Renderer == null) return;
            bool faceUp = actor.Item != null && actor.Item.Flipped;
            if (faceUp)
                actor.Renderer.sprite = actor.FaceSprite;                 // 正面 = 真卡面
            else
                actor.Renderer.sprite = actor.BackSprite != null ? actor.BackSprite : actor.FaceSprite;
                // 没有独立背图（牌堆里的牌）时，它的"那一面"本身就是卡背扫描图
        }

        /// <summary>把每个组件瞬间摆到它当前 (zone, slot) 的位置 —— 这是状态的可视化。</summary>
        private void SyncActorsToStore()
        {
            foreach (var item in Store.Items)
            {
                if (item.Actor == null) continue;
                var pos = Store.CurrentPosition(item);
                item.LivePosition = pos;
                item.Actor.LivePosition = pos;
                item.Actor.Go.transform.localPosition = pos;

                item.Actor.LiveScale = item.Actor.BaseScale;
                item.Actor.LiveAlpha = item.Template.alpha;
                item.Actor.LiveColor = item.BaseColor;   // 染色必须一起带上，否则复位时会丢
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localRotation = SpriteRotation(item.Template.rotation);
                item.Actor.ApplyColor();
                RefreshFace(item.Actor);
            }
        }

        // ── 时间轴 ────────────────────────────────────────────────────────

        public float TotalDuration
        {
            get
            {
                if (cueDoc?.events == null) return 0f;
                float end = 0f;
                foreach (var ev in cueDoc.events)
                {
                    if (ev == null || ev.action == "wait") continue;
                    end = Mathf.Max(end, ev.at + ev.lead + Mathf.Max(0f, ev.dur));
                }
                return end;
            }
        }

        /// <summary>
        /// 外部直接给定动画时间（离线出帧 / 帧截图用，不依赖音频）。
        /// 与 Seek 的区别只是语义：Seek 由音频时钟驱动，这里由调用方驱动。
        /// </summary>
        public void DriveAnimation(float time)
        {
            Seek(time);
        }

        /// <summary>
        /// 把这条 cue 推进到指定时刻的**终态**，不播协程、不等音频。
        ///
        /// 两个用途：
        ///   1. 离线出帧（batchmode 里没有音频设备，不能靠音频时钟）；
        ///   2. 将来编译器离线计算「某个时刻的完整画面」。
        /// 与 Seek 的区别：Seek 触发协程做插值（表现为动画），这里直接落位（表现为状态）。
        /// </summary>
        public void SnapTo(float time)
        {
            if (cueDoc?.events == null) return;

            var target = new List<CueAnimEvent>();
            foreach (var ev in cueDoc.events)
                if (ev != null && ev.at <= time + 1e-4f) target.Add(ev);

            StopAnimations();
            RestoreSnapshot(entrySnapshot);

            // 需要按原始顺序逐条落位，因为 move 依赖前一条 move 之后的顺序（take 的取件顺序）。
            foreach (var ev in target) TriggerFinal(ev);

            clock = time;
            nextIndex = cueDoc.events.Count;
            pendingScales.Clear();
        }

        public void Seek(float time)
        {
            if (cueDoc == null) return;

            if (clock < 0f || time + 0.25f < clock)
            {
                ResetToStart();
                clips.Clear();          // 回退：片段全部失效，由当前时刻重新触发
            }

            clock = time;
            float scaled = time * Mathf.Max(0.01f, timeScale);


            // 触发所有已到点的事件。触发时就把「逻辑状态」推到终态（Store 是时间的阶跃函数），
            // 视觉上的过渡交给片段采样 —— 这样跳转/暂停/倒放都不需要特殊处理。
            while (nextIndex < cueDoc.events.Count && cueDoc.events[nextIndex].at <= scaled + 1e-4f)
            {
                var ev = cueDoc.events[nextIndex];
                nextIndex++;
                Trigger(ev);
            }

            for (int i = pendingScales.Count - 1; i >= 0; i--)
            {
                if (pendingScales[i].At > scaled + 1e-4f) continue;
                var entry = pendingScales[i];
                pendingScales.RemoveAt(i);
                ApplyScale(entry.Actor, entry.Factor, entry.Event);
            }

            SampleClips(scaled);
            TracePositions(scaled);
            SyncPointers();          // 指示物是时钟的纯函数：到哪里才亮

        }

        /// <summary>
        /// 按当前时刻采样所有片段，直接算出每个组件的画面状态。
        /// 这是「动画 = 时间的函数」的落点：不累积、不依赖帧，因此完全可复现，
        /// 离屏出图只要依次 Seek 即可看到完整动画。
        /// </summary>
        /// <summary>
        /// 一组 zone 的全部格位包围盒（世界坐标）。用于让底板自动贴合内容。
        /// </summary>
        private bool TryZoneBounds(List<string> zoneIds, out float x0, out float x1, out float z0, out float z1)
        {
            x0 = z0 = float.MaxValue; x1 = z1 = float.MinValue;
            bool any = false;
            foreach (var zoneId in zoneIds)
            {
                if (string.IsNullOrEmpty(zoneId)) continue;
                var zone = Store.GetZone(zoneId);
                if (zone == null || zone.role == "offstage") continue;
                int count = Mathf.Max(1, Store.CountInZone(zoneId));
                // 至少覆盖该 zone 的标称容量，避免空 zone 时底板缩成一点
                int capacity = zone.capacity > 0 ? Mathf.Max(zone.capacity, count) : count;
                float halfW = (zone.size?.w ?? 0.14f) * 0.5f;
                float halfH = (zone.size?.h ?? 0.14f) * 0.5f;
                for (int i = 0; i < capacity; i++)
                {
                    var p = Store.ZonePosition(zoneId, i);
                    x0 = Mathf.Min(x0, p.x - halfW); x1 = Mathf.Max(x1, p.x + halfW);
                    z0 = Mathf.Min(z0, p.z - halfH); z1 = Mathf.Max(z1, p.z + halfH);
                }
                any = true;
            }
            return any;
        }

        /// <summary>取（或新建）该组件在本时刻的片段，供各触发函数登记视觉变化。</summary>
        private Clip ClipAt(ZoneItem item, CueAnimActor actor, CueAnimEvent ev, float extraLead = 0f)
        {
            var clip = new Clip
            {
                Item = item,
                Actor = actor,
                Start = currentEventAt + Mathf.Max(0f, ev.lead) + Mathf.Max(0f, extraLead),
                Dur = Mathf.Max(0.01f, ev.dur),
                Easing = EasingOr(ev),
            };
            clips.Add(clip);
            return clip;
        }

        /// <summary>自检用：取某块底板的相机平面包围盒（x/z 范围）。</summary>
        /// <summary>自检/出图用：确保场景里有可用的相机。</summary>
        public void EnsureCameraForCapture() => EnsureCamera();

        /// <summary>
        /// 坐标守卫：无效坐标一旦写进 Transform，Unity 会每帧报同样的错，
        /// 但不会告诉你是哪个组件、哪一步产出的。这里一次说清。
        /// </summary>
        private void WarnIfInvalid(ZoneItem item, Vector3 pos, string stage)
        {
            if (!float.IsNaN(pos.x) && !float.IsNaN(pos.z)
                && !float.IsInfinity(pos.x) && !float.IsInfinity(pos.z)) return;
            Debug.LogError($"[CueAnim] 坐标无效（{stage}）{item?.Id} zone={item?.ZoneId} " +
                           $"order={item?.Order} pos=({pos.x},{pos.z}) cue={CueId}");
        }

        /// <summary>把组件的实时透明度写到渲染器上。</summary>
        private static void ApplyAlpha(CueAnimActor actor)
        {
            if (actor?.Renderer == null) return;
            float a = Mathf.Clamp01(actor.LiveAlpha);
            var c = actor.LiveColor;
            c.a = a;
            actor.Renderer.color = c;

            // 透明度与「是否绘制」联动：全透明就不画（省开销，也避免半透明底板那种脏像），
            // 一旦淡入开始就重新打开。若只改 alpha 不改 enabled，
            // 建对象时为隐藏而关掉的 renderer 会永远关着，牌再也出不来。
            if (Mathf.Approximately(a, 0f)) actor.Renderer.enabled = false;
            else if (!actor.Renderer.enabled) actor.Renderer.enabled = true;
        }

        /// <summary>
        /// 运行时诊断：把「每张牌在什么时刻、由哪个片段决定、在什么位置」写进日志。
        /// 打开后（按 T 或运行时设 runtimeTrace=true），编辑器里跑一遍就能和离屏出图逐帧对照，
        /// 差异会直接暴露在哪一帧、哪张牌、哪个片段上。
        /// </summary>
        public bool runtimeTrace = true;   // 默认打开：出问题时日志里有可对照的坐标轨迹
        private float lastTraceTime = -99f;

        private void TracePositions(float scaled)
        {
            if (!runtimeTrace) return;
            // 按时间节流，不用 Time.frameCount（批处理下它恒为 0，会把日志全挡掉）。
            if (scaled - lastTraceTime < 0.3f) return;
            lastTraceTime = scaled;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[Trace] t={scaled:0.00} clips={clips.Count}");
            foreach (var it in Store.Items)
            {
                if (it.Actor == null || !it.Id.StartsWith("market_card_")) continue;
                var p = it.Actor.Go.transform.localPosition;
                string by = "-";
                foreach (var c in clips)
                    if (c.Item != null && c.Item.Id == it.Id && c.HasMove)
                        by = $"{c.Start:0.0}+{c.Dur:0.0}";
                var sc = it.Actor.Go.transform.localScale;
                var sr = it.Actor.Renderer;
                string face = sr == null || sr.sprite == null ? "无图"
                    : (ReferenceEquals(sr.sprite, it.Actor.BackSprite) ? "背" : "面");
                sb.Append($"  {it.Id.Replace("market_card_", "")}=({p.x:0.0},{p.z:0.0})" +
                          $"s{sc.x:0.00} a{it.Actor.LiveAlpha:0.0} {face} 显{(sr != null && sr.enabled ? 1 : 0)}[{by}]");
            }
            Debug.Log(sb.ToString());
        }

        private void SampleClips(float scaled)
        {
            // 每个组件取「最近登记的那个片段」，用于决定本时刻它该在哪：
            //   片段还没开始 → 停在片段起点（例如牌堆），这样「排队等发牌」的牌不会提前出现在市场
            //   片段进行中   → 按插值位置
            //   没有片段     → 按逻辑位置（格位表）
            var latest = new Dictionary<string, Clip>();
            foreach (var clip in clips)
            {
                if (clip?.Item == null) continue;
                if (!latest.TryGetValue(clip.Item.Id, out var prev) || clip.Start >= prev.Start)
                    latest[clip.Item.Id] = clip;
            }

            // 先复位到逻辑状态。**已有生效片段的组件跳过**：
            // 它的位置由片段决定（可能是从牌堆飞到市场的中途）。
            // 曾经无条件按逻辑位置复位，导致「还没轮到的牌先出现在市场」，
            // 随后补间从牌堆飞过来 —— 用户看到的就是「先出一整行，再一张张往回飞」。
            foreach (var item in Store.Items)
            {
                var actor = item.Actor;
                if (actor?.Go == null) continue;

                // 牌堆层次：**按 order 递增**，order 越小越盖在上面。
                // 必须每次采样都算 —— 建对象时整摞还没建完（张数不全→层次算错），
                // 而牌堆成员每发一张就变。同一模板 sortingOrder 相同时谁盖住谁是任意的，
                // 表现就是"最顶上那张被压在底下"。
                var stackZone = Store.GetZone(item.ZoneId);
                if (stackZone?.display != null && stackZone.display.mode == "stack")
                {
                    int n = Mathf.Max(1, Store.HighestOrderPlusOne(item.ZoneId));
                    actor.Renderer.sortingOrder = item.Template.sorting_order + (n - item.Order);
                }
                else if (actor.Renderer.sortingOrder != item.Template.sorting_order)
                {
                    actor.Renderer.sortingOrder = item.Template.sorting_order;
                }

                if (latest.TryGetValue(item.Id, out var pending))
                {
                    // 片段已登记：位置由片段决定（未开始就停在起点，进行中由下面的采样覆盖）。
                    if (scaled < pending.Start)
                    {
                        actor.LivePosition = pending.From;
                        actor.Go.transform.localPosition = pending.From;
                    }

                    // 透明度必须在这里也复位：要求隐藏的组件在片段生效前一律透明。
                    // 之前这个分支直接 continue，跳过了这段 —— 二级/三级市场牌就带着
                    // 默认 alpha=1 显示在自己的牌堆位置上，看起来像往牌堆里发卡背。
                    actor.LiveAlpha = actor.BaseAlpha;
                    actor.LiveColor = item.BaseColor;
                    ApplyAlpha(actor);
                    continue;
                }
                actor.LivePosition = Store.CurrentPosition(item);
                WarnIfInvalid(item, actor.LivePosition, "复位");
                actor.Go.transform.localPosition = actor.LivePosition;

                actor.LiveRotation = item.Template != null ? item.Template.rotation : 0f;
                actor.Go.transform.localRotation = SpriteRotation(actor.LiveRotation);
                actor.LiveScale = actor.BaseScale;
                actor.Go.transform.localScale = actor.LiveScale;
                // 默认状态：按组件自己的基准透明度。
                // 「还没出场的东西不可见」不再靠模板标记，而是**对象根本还没被创建**
                // （见 create 原语）——所以这里不需要额外判断。
                actor.LiveAlpha = actor.BaseAlpha;
                ApplyAlpha(actor);
                RefreshFace(actor);
            }

            foreach (var clip in latest.Values)
            {
                if (clip?.Actor?.Go == null) continue;
                if (scaled < clip.Start) continue;
                float k = clip.K(scaled);

                if (clip.HasMove)
                {
                    var p = Vector3.LerpUnclamped(clip.From, clip.To, k);
                    clip.Item.LivePosition = p;
                    clip.Actor.LivePosition = p;
                    clip.Actor.Go.transform.localPosition = p;
                }

                if (clip.HasFlip)
                {
                    // 翻面用**压扁-展开**表达，而不是绕 Y 轴旋转。
                    //
                    // 为什么不用旋转：把一张平面贴图绕 Y 轴转到 180°，它在屏幕上就是
                    // **左右镜像**的（用户报的"翻出来的正面卡牌都是镜像的"）。
                    // 真实的牌翻过去之所以不镜像，是因为实体牌的两面是两个不同的面；
                    // 我们只有一张平面贴图，旋转必然镜像。
                    //
                    // 压扁-展开是 2D 里表达翻牌的标准手法：
                    //   0 → 0.5：横向收缩到 0（牌"立起来"侧对镜头），显示背面
                    //   0.5 → 1：横向展开回原宽，显示正面
                    // 全程不旋转，所以正面始终是正的。
                    float squash = Mathf.Abs(1f - 2f * k);      // 1 → 0 → 1
                    // 最小不要压到 0：完全扁平的贴图会让"当前贴图身份"这类判断失去意义，
                    // 也让复位逻辑更难判断。留一点点宽度即可（视觉上仍是"侧对镜头"）。
                    squash = Mathf.Max(squash, 0.02f);
                    var scl = clip.Actor.BaseScale;
                    scl.x *= squash;
                    clip.Actor.Go.transform.localScale = scl;
                    clip.Actor.Go.transform.localRotation = SpriteRotation(0f);
                    // 同步 LiveScale，否则复位时会把终态缩放当基准、越缩越小
                    clip.Actor.LiveScale = scl;

                    bool faceUpNow = k >= 0.5f;
                    clip.Actor.Renderer.sprite = faceUpNow
                        ? clip.Actor.FaceSprite
                        : (clip.Actor.BackSprite != null ? clip.Actor.BackSprite : clip.Actor.FaceSprite);
                }

                if (clip.HasScale)
                {
                    clip.Actor.LiveScale = Vector3.LerpUnclamped(clip.ScaleFrom, clip.ScaleTo, k);
                    clip.Actor.Go.transform.localScale = clip.Actor.LiveScale;
                }

                if (clip.HasGroupMove)
                {
                    var p2 = Vector3.LerpUnclamped(clip.GroupMoveFrom, clip.GroupMoveTo, k);
                    WarnIfInvalid(clip.Item, p2, "groupMove");
                    clip.Actor.Go.transform.localPosition = p2;
                }

                if (clip.HasGroupShift)
                {
                    var d = Vector3.LerpUnclamped(clip.GroupShiftFrom, clip.GroupShiftTo, k);
                    clip.Actor.Go.transform.localPosition = clip.Actor.LivePosition + d;
                }

                if (clip.HasGroupScale)
                {
                    // 整组一起放大：自身缩放 × 位置相对重心外扩，等效于「以重心为锚点整体缩放」。
                    // 这样一整摞牌看起来是一个对象在变大，而不是某一张单独变大。
                    // 脉冲 = 去程 + 回程：前半程放大到 grow，后半程回到原样。
                    // 之前直接把 k 映到 [1, grow]，于是只长大不回落，牌堆永久变大了。
                    float half = k < 0.5f ? k * 2f : (1f - k) * 2f;
                    float g = Mathf.LerpUnclamped(1f, clip.GroupGrow, half);
                    var basePos = clip.Actor.LivePosition;
                    var gp = clip.GroupCenter + (basePos - clip.GroupCenter) * g;
                    WarnIfInvalid(clip.Item, gp, "groupScale");
                    clip.Actor.Go.transform.localPosition = gp;
                    clip.Actor.Go.transform.localScale = clip.Actor.BaseScale * g;
                }

                if (clip.HasAlpha)
                {
                    clip.Actor.LiveAlpha = Mathf.LerpUnclamped(clip.AlphaFrom, clip.AlphaTo, k);
                    ApplyAlpha(clip.Actor);
                }

                if (clip.HasRotate && !clip.HasFlip)
                {
                    float rot = Mathf.LerpUnclamped(clip.RotFrom, clip.RotTo, k);
                    clip.Actor.LiveRotation = rot;
                    clip.Actor.Go.transform.localRotation = SpriteRotation(rot);
                }

                if (clip.HasShuffle)
                {
                    // 洗牌 = 每张牌各自**左右高频颤抖**：
                    //   频率、幅度、相位、纵向分量都按牌号取不同的值，
                    //   所以同一瞬间有的向左有的向右、抖得也不一样快 —— 才像在搓牌。
                    //   外面再乘一个衰减包络：开头最猛，末尾收住，整体不跑位。
                    float elapsed = Mathf.Max(0f, scaled - clip.Start);
                    // 包络：前 1/4 起振、中段保持满幅、末 1/4 收住。
                    // 用 (1-k) 线性衰减会让抖动过早变弱（实测 0.9s 后就几乎不动了）。
                    // 注意：sin(kπ) 在 k=1 处会因浮点误差得到 **极小的负数**（~-1e-8），
                    // 而负数的小数次幂（Pow(x, 0.45)）在数学上无定义 → 返回 NaN。
                    // NaN 一旦写进 Transform 就每帧报错，并且会让这一摞牌"消失"
                    // （位置变 NaN 后不再被渲染，后续 cue 也修不回来）。
                    // 所以底数必须先夹到非负。
                    float baseWave = Mathf.Max(0f, Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI));
                    float envelope = Mathf.Pow(baseWave, Shuffle.EnvelopePower);
                    float w = elapsed * clip.ShuffleFreq * Mathf.PI * 2f + clip.ShufflePhase;
                    float dx = Mathf.Sin(w) * clip.ShuffleAmp * envelope;
                    float dz = Mathf.Sin(w * 0.73f + 1.1f) * clip.ShuffleZ * envelope;
                    var sp2 = clip.ShuffleFrom + new Vector3(dx, 0f, dz);
                    WarnIfInvalid(clip.Item, sp2, "shuffle 采样");
                    clip.Actor.Go.transform.localPosition = sp2;
                }
            }
        }

        /// <summary>把这条 cue 直接推到结束（顺序播放进入下一条之前用）。</summary>
        /// <summary>
        /// 把本条 cue **推到终态**（离开这条 cue 之前必须调，例如用户看到一半按 →）。
        ///
        /// ⚠️ 这里**不能用"只改外观"的 TriggerFinal** —— 它原先只处理 transfer 的位移，
        /// 既不套用 `to`（终态朝向），也不处理 create / destroy / stack / showbox。
        /// 后果（用户 2026-09 报的、已用 -advanceSkip 复现）：
        /// 发牌看到一半按 →，12 张市场牌里 **11 张停在背面**，还有 1 张卡在半空没落位，
        /// 而且这个坏状态会被后面每一条 cue 继承（"正常播完就没事、快进就坏"）。
        ///
        /// 现在走**和正常播放同一条路**（Trigger），状态不可能不一致；排出来的补间
        /// 随后全部作废，画面直接摆到终态 —— 要的是终态，不是"把动画播完"。
        /// </summary>
        public void Complete()
        {
            if (cueDoc?.events == null) return;
            while (nextIndex < cueDoc.events.Count)
            {
                var ev = cueDoc.events[nextIndex];
                nextIndex++;
                Trigger(ev);
            }
            clock = Mathf.Max(clock, TotalDuration);
            clips.Clear();          // 补间作废：不再有"卡在半空"的件
            StopAnimations();
            SyncActorsToStore();    // 画面按 Store 摆到终态
        }

        /// <summary>恢复到本条 cue 的入口状态（重播 / 向后拖动时用）。</summary>
        public void ResetToStart()
        {
            StopAnimations();
            pendingScales.Clear();
            RestoreSnapshot(entrySnapshot);
            clock = 0f;
            nextIndex = 0;
        }

        private void CaptureEntry()
        {
            entrySnapshot = ZoneSnapshot.Capture(Store);
        }

        private void RestoreSnapshot(ZoneSnapshot snapshot)
        {
            if (snapshot == null) return;

            foreach (var item in Store.Items)
            {
                if (item.Actor == null) continue;
                if (!snapshot.TryGet(item.Id, out string zoneId, out int order)) continue;
                item.ZoneId = zoneId;
                item.Order = order;

                var pos = Store.CurrentPosition(item);
                item.LivePosition = pos;
                item.Actor.LivePosition = pos;
                item.Actor.Go.transform.localPosition = pos;

                item.Actor.LiveScale = item.Actor.BaseScale;
                item.Actor.LiveAlpha = item.Template.alpha;
                item.Actor.LiveColor = item.BaseColor;   // 染色必须一起带上，否则复位时会丢
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localScale = item.Actor.BaseScale;
                item.Actor.Go.transform.localRotation = SpriteRotation(item.Template.rotation);
                item.Actor.ApplyColor();
                RefreshFace(item.Actor);
            }
        }

        public void ClearScene()
        {
            StopAnimations();
            ClearActors();
            cueDoc = null;
            clock = -1f;
            nextIndex = 0;
            pendingScales.Clear();
            Store.Reset();
        }

        private void StopAnimations()
        {
            for (int i = 0; i < running.Count; i++)
                if (running[i] != null) StopCoroutine(running[i]);
            running.Clear();
        }

        private void ClearActors()
        {
            actors.Clear();
            zoneGlow.Clear();
            ClearPointers();
            if (animRoot != null)
            {
                Object.DestroyImmediate(animRoot);
                animRoot = null;
            }

            // 盒面等整幅图也一并销毁：换节（continueState=false）时自动消失，
            // 不需要在数据里额外写一条「隐藏」事件。
            if (boxSprite != null)
            {
                Object.DestroyImmediate(boxSprite.gameObject);
                boxSprite = null;
            }
        }

        // ── 原语 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 把事件里的 zone 引用解析成**具体 zone id**（就地归一化，幂等）。
        ///
        /// 脚本可以写"本体身份"而不是本作专用的 id：
        ///   `source: ["<gem_supply|color=<diamond>>"]`、`destination: "<card_market>"`
        /// 解析在**事件入口**做一次，下游（PlanMove / create / destroy / 高亮 / 取景）
        /// 全都继续只认 id —— 于是"可复用"这件事只有一个地方需要懂。
        /// 解析不出来时保持原样并报错（下游的"未知 zone"会再报一次，不会静默）。
        /// </summary>
        private void ResolveZoneRefs(CueAnimEvent ev)
        {
            if (ev == null) return;
            ev.zone = Store.ResolveZoneRef(ev.zone);
            ev.destination = Store.ResolveZoneRef(ev.destination);
            if (ev.source != null)
                for (int i = 0; i < ev.source.Count; i++)
                    ev.source[i] = Store.ResolveZoneRef(ev.source[i]);
        }

        private void Trigger(CueAnimEvent ev)
        {
            ResolveZoneRefs(ev);
            if (ev == null) return;
            // 记下这条事件在时间轴上的位置：片段起点要用**事件时间**，
            // 而不是「触发到它的那一刻」——后者随帧率/seek 粒度变化，
            // 会让动画起点漂移、按 elapsed 计算的效果（如洗混）不一致。
            currentEventAt = ev.at;

            // 取景：事件若指定了 camera，先把镜头切过去再执行动作，
            // 否则动作会发生在错误的取景下（例如特写时物体仍很小）。
            if (!string.IsNullOrEmpty(ev.camera))
            {
                // 取景也允许写 zone 引用；多 zone 同框（"a,b"）逐段解析
                var parts = ev.camera.Split(',');
                for (int i = 0; i < parts.Length; i++) parts[i] = Store.ResolveZoneRef(parts[i].Trim());
                SetFraming(string.Join(",", parts), ev.camera_padding);
            }

            switch (ev.action)
            {
                case "wait": return;
                case "transfer": TriggerMove(ev); return;
                case "rotate":
                case "flip": TriggerRotate(ev); return;
                case "scale": TriggerScale(ev); return;
                case "fade": TriggerFade(ev); return;
                case "highlight": TriggerHighlight(ev); return;
                case "point": TriggerPoint(ev); return;
                case "shuffle": TriggerShuffle(ev); return;
                case "showbox": TriggerShowBox(ev); return;
                case "zone": TriggerZone(ev); return;
                case "create": TriggerCreate(ev); return;
                case "stack": TriggerStack(ev); return;
                case "destroy": TriggerDestroy(ev); return;
                default:
                    Debug.LogWarning($"[TutorialCueAnim] unknown action '{ev.action}' in cue {CueId}");
                    return;
            }
        }

        /// <summary>把事件推到终态且不播协程，用于顺序播放时推进状态。</summary>
        private void TriggerFinal(CueAnimEvent ev)
        {
            if (ev == null || ev.action == "wait") return;
            ResolveZoneRefs(ev);

            if (ev.action == "transfer")
            {
                foreach (var step in PlanMove(ev))
                {
                    // 与 TriggerMove 一致：统一走 MoveToSlot，再按格位表落位。
                    if (step.Destination != null)
                        Store.MoveToSlot(step.Item, step.Destination, step.Order);
                    if (step.Item.Actor != null) ApplyCurrentPlacement(step.Item, step.Item.Actor);
                }
                return;
            }

            // 其余原语只影响外观：进入下一条前复原到基准。
            foreach (var actor in Resolve(ev))
            {
                if (actor?.Item == null) continue;
                actor.LiveScale = actor.BaseScale;
                actor.LiveAlpha = actor.BaseAlpha;
                actor.LiveColor = actor.BaseColor;
                actor.LiveRotation = actor.Item.Template.rotation;
                actor.Go.transform.localScale = actor.BaseScale;
                actor.Go.transform.localRotation = SpriteRotation(actor.LiveRotation);
                actor.Go.transform.localPosition = Store.CurrentPosition(actor.Item);
                actor.ApplyColor();
            }
        }

        private void ApplyCurrentPlacement(ZoneItem item, CueAnimActor actor)
        {
            var pos = Store.CurrentPosition(item);
            item.LivePosition = pos;
            actor.LivePosition = pos;
            actor.Go.transform.localPosition = pos;
            actor.LiveScale = actor.BaseScale;
            actor.Go.transform.localScale = actor.BaseScale;
        }

        public class MovePlan
        {
            public ZoneItem Item;
            public string Destination;
            public int Order;      // -1 = 追加到末尾
            public bool InPlace;   // true = 只动画位置，不改占用
            public Vector3 From;   // 补间起点（触发时快照）
            public Vector3 To;     // 补间终点（触发时快照）
            public string ToFace;  // 到终点时的朝向（"face_up"/"face_down"，空 = 不翻）
        }

        /// <summary>
        /// 时间轴片段：一段只在 [Start, Start+Dur] 内生效的视觉变化。
        /// 关键设计 —— 画面是**时间的纯函数**：Seek(t) 直接采样所有片段算出位置，
        /// 不靠「每帧推进一点」。因此暂停、跳转、倒放天然正确，
        /// 而且离屏出图时只要依次 Seek 就能看到完整动画（不依赖 Unity 帧循环）。
        /// </summary>
        private class Clip
        {
            public ZoneItem Item;
            public CueAnimActor Actor;
            public float Start;
            public float Dur;

            public bool HasMove;
            public Vector3 From, To;

            public bool HasFlip;
            public float FlipFromYaw; // 起始偏航角（0 或 180）

            public bool FlipHalfTurn; // true = 翻半圈回到原角度（新建组件的翻转）

            public bool HasScale;
            public Vector3 ScaleFrom, ScaleTo;

            /// <summary>
            /// 整组缩放：把一组组件（例如一整摞牌堆）当作**一个对象**，
            /// 以这组的重心为中心整体放大/缩小。
            /// 单张缩放会让牌堆只有某一张变大（用户看到的「只有堆底那张大了一圈」）。
            /// </summary>
            public bool HasGroupScale;
            public Vector3 GroupCenter;   // 触发时快照的组锚点
            public float GroupGrow;       // 目标倍率

            /// <summary>
            /// 整组平移：组内每件位移相同的量，**相对位置不变**。
            /// 用于「把这一组东西挪到别处」（例如展示玩家的保留区）。
            /// </summary>
            public bool HasGroupShift;
            public Vector3 GroupShiftFrom, GroupShiftTo;

            /// <summary>整组搬运：每件各自算位移（到自己的目标格位），但同一时刻、同一时长。</summary>
            public bool HasGroupMove;
            public Vector3 GroupMoveFrom, GroupMoveTo;

            public bool HasAlpha;
            public float AlphaFrom, AlphaTo;

            public bool HasRotate;
            public float RotFrom, RotTo;

            public bool HasShuffle;
            public Vector3 ShuffleFrom;     // 抖动基准位置（触发时快照，别人挪它不影响）
            public float ShuffleAmp;        // 水平幅度（每张不同）
            public float ShuffleFreq;       // 抖动频率（每张不同）
            public float ShufflePhase;      // 初相（每张不同）
            public float ShuffleZ;          // 纵向幅度（比水平小，方向也各自不同）
            public float ShuffleDecay;      // 衰减速度

            public string Easing;

            public float K(float time)
            {
                if (Dur <= 0f) return 1f;
                return Mathf.Clamp01(BoardGameTutorial.Easing.Evaluate(Easing, (time - Start) / Dur));
            }
        }

        private readonly List<Clip> clips = new List<Clip>();

        /// <summary>
        /// move 的语义：把组件从 source zone 搬到 destination zone。
        /// 数据里只有两端 zone（+ 可选 take），落点位置由 zone 推导。
        /// 注意：这里只算「搬哪几件、搬到哪」，不改账本；改账本由调用方决定
        /// （播放时立即落账，位置动画负责补上视觉；推进终态时瞬间落账）。
        /// </summary>
        private List<MovePlan> PlanMove(CueAnimEvent ev)
        {
            var plan = new List<MovePlan>();

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor?.Item != null)
                {
                    // order=-2：落到 ev.slot 指定的格位，**归属也真的迁过去**。
                    // 视觉上仍从 from_zone（对应牌堆）飞出，所以看起来是「从牌堆翻出来」；
                    // 但逻辑上必须落到市场，否则后续事件会把牌拉回起点、后面的牌也会占错格。
                    int ord = ev.order == -2 ? ev.slot : ev.order;
                    plan.Add(new MovePlan
                    {
                        Item = actor.Item,
                        Destination = ev.destination,
                        Order = ord,
                        InPlace = false,
                    });
                }
                return plan;
            }

            if (string.IsNullOrEmpty(ev.destination) || Store.GetZone(ev.destination) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no resolvable destination ('{ev.destination}')");
                return plan;
            }

            var sources = ev.source;
            if (sources == null || sources.Count == 0)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no from zone");
                return plan;
            }

            // 整组搬运（ev.group）时取该 zone 的**全部**：
            // 缺省的 take=1 只搬一件，一摞牌会只剩一张动（用户会看到"牌堆没出来"）。
            int take = ev.quantity > 0 ? ev.quantity : (ev.group ? int.MaxValue : 1);
            var picked = new List<ZoneItem>();

            // 本体语言的引用 → 具体素材。解析不出来就**不动**并报错，不猜。
            string pickTemplate = ev.template;
            string pickPalette = ev.palette;
            List<ZoneStore.TemplateChoice> pickCandidates = null;
            if (ev.HasWhat)   // 不是 `ev.what != null`：JsonUtility 会给它造空实例（见 HasWhat）
            {
                // 搬的是**真件**：样本不参与（它只活在介绍用的展示位里）。
                pickCandidates = Store.ConceptCandidatesReal(ev.what.concept, ev.what.parts);
                if (pickCandidates.Count == 0)
                {
                    Debug.LogError($"[TutorialCueAnim] transfer 的 what 一个候选都没有（cue {CueId}）：" +
                                   $"concept='{ev.what.concept}' —— 概念名或属性写错了？");
                    return plan;
                }
                pickTemplate = null;   // 改由候选集判定
                pickPalette = null;
            }

            // from 写多个 zone = 每个 zone 各取 take 件（三种宝石各一枚）。
            foreach (var source in sources)
            {
                if (Store.GetZone(source) == null)
                {
                    Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId}: 未知 from zone '{source}'");
                    continue;
                }
                for (int i = 0; i < take; i++)
                {
                    var item = PickFront(source, picked, pickTemplate, pickPalette, pickCandidates);
                    if (item == null) break;
                    picked.Add(item);
                    plan.Add(new MovePlan { Item = item, Destination = ev.destination, Order = ev.order });
                }
            }


            return plan;
        }

        /// <summary>取 zone 里最靠前、且不在 excluded 中的组件。</summary>
        /// <summary>
        /// 取牌堆"最上面"那张 = **`order` 最小**的那张（用户定义：**先发 order 0**）。
        ///
        /// 叠放位置把 order 0..容量-9 放在**重合块**里（全部盖在 order 32 上），
        /// 所以从 order 0 一路发到 order 31，外形**天然不变**；再发才开始变小。
        /// </summary>
        private ZoneItem PickFront(string zoneId, List<ZoneItem> excluded, string template = null,
            string palette = null, List<ZoneStore.TemplateChoice> candidates = null)
        {
            ZoneItem best = null;
            foreach (var item in Store.Items)
            {
                if (item.ZoneId != zoneId) continue;
                if (excluded != null && excluded.Contains(item)) continue;
                if (!string.IsNullOrEmpty(template) && item.Template?.id != template) continue;
                if (!string.IsNullOrEmpty(palette) && item.PaletteName != palette) continue;
                if (candidates != null && !Store.MatchesConcept(item, candidates)) continue;
                if (best == null || item.Order < best.Order) best = item;
            }
            return best;
        }

        private void TriggerMove(CueAnimEvent ev)
        {
            var moved = new List<ZoneItem>();
            foreach (var step in PlanMove(ev))
            {
                var actor = step.Item.Actor;
                Vector3 from = actor != null ? actor.LivePosition : step.Item.LivePosition;
                WarnIfInvalid(step.Item, from, "move 起点");

                // order=-2 的语义是「从别处出场、落在目标 zone 的第 Order 格」——
                // 逻辑归属也必须落到那一格。之前只动了画面、不记格位，导致：
                //   ① 后续事件按旧归属算位置时，把这些牌拉回起点（用户看到的「被收回去」）
                //   ② 新发的牌按当前格位算，落到了别的行
                // 现在统一按正常落位处理，动画起点仍由 from_zone 提供，所以看起来依旧「从牌堆飞出」。
                // 统一走 MoveToSlot(item, zone, slot)：坐标由格位表给出，
                // 「落到第几格」不再散落成算术。slot < 0 表示追加到末尾。
                step.Item.Shown = true;   // 被动画带出来了，此后不再因静态标记而隐藏
                if (step.Destination != null)
                    Store.MoveToSlot(step.Item, step.Destination, step.Order);
                moved.Add(step.Item);
                if (logImages && step.Order >= 0)
                    Debug.Log($"[Move] {step.Item.Id} → {step.Destination} order={step.Item.Order} " +
                              $"pos=({Store.CurrentPosition(step.Item).x:0.00},{Store.CurrentPosition(step.Item).z:0.00}) " +
                              $"zoneCount={Store.CountInZone(step.Destination)}");

                // 终点：原位动画落在「目标 zone 的第 Order 格」，普通搬运落在新归属的格位。
                // 落位后取终点：直接问格位表，和底板贴合用的是同一张表。
                Vector3 to = step.Destination != null
                    ? Store.ZonePosition(step.Destination, step.Order)
                    : Store.CurrentPosition(step.Item);

                step.Item.LivePosition = to;
                // **状态先落，再管画面**：`to`（终态朝向）是状态，不能和"有没有 actor"绑在一起。
                // 以前这行在 `if (actor == null) continue;` 之后，于是**入口链重放**（stateOnly、
                // 没有 actor）时市场牌全部落在背面 —— "跳到发牌之后的任意一条，市场全是卡背"。
                if (!string.IsNullOrEmpty(ev.to)) step.Item.Flipped = ev.to == "face_up";
                if (actor == null) continue;

                step.From = from;
                step.To = to;
                step.ToFace = ev.to;

                if (logTweens)
                    Debug.Log($"[Tween] {step.Item.Id} inPlace={step.InPlace} order={step.Order} " +
                              $"from=({from.x:0.00},{from.z:0.00}) to=({to.x:0.00},{to.z:0.00}) dur={ev.dur}");

                WarnIfInvalid(step.Item, to, "move 终点");
                var clip = ClipAt(step.Item, actor, ev);
                if (ev.group)
                {
                    // 整组搬运：同一时刻、同一时长，但每件各自飞向自己的目标格位。
                    // 逐件错开会让一整摞牌散成扇形；同步则保持"这一摞"的整体感。
                    clip.HasGroupMove = true;
                    clip.GroupMoveFrom = from;
                    clip.GroupMoveTo = to;
                }
                else
                {
                    clip.HasMove = true;
                    clip.From = from;
                    clip.To = to;
                }

                // 淡入：发牌前市场牌是隐藏的（避免它们叠在牌堆上，看起来像多出几层卡背）
                if (ev.fade_in >= 0f)
                {
                    clip.HasAlpha = true;
                    clip.AlphaFrom = Mathf.Clamp01(ev.fade_in);
                    clip.AlphaTo = 1f;
                }

                // 边移动边翻转：到终点恰好转到另一面。
                // 翻转不依赖 back_image：牌堆的牌**正面就是卡背图**（它还没有"另一面"）。
                // 曾经用 `ev.flip && actor.BackSprite != null` 作为条件，牌堆的牌
                // 没有 back_image，于是整条翻转+换面都没发生（发到市场的牌一直是卡背）。
                if (!string.IsNullOrEmpty(ev.to))
                {
                    // 状态上面已经落了；这里只负责"翻给他看"
                    clip.HasFlip = true;
                }
            }

            CloseGaps(moved, ev);
        }

        /// <summary>
        /// 一件组件被拿走之后，同一 zone 里排在它后面的组件顺位前移。
        /// 这样「从供应堆拿走宝石」会看到堆真的少了一枚，而不是留一个空位。
        /// </summary>
        private void CloseGaps(List<ZoneItem> moved, CueAnimEvent ev)
        {
            if (moved == null || moved.Count == 0) return;

            var touched = new HashSet<string>();
            foreach (var item in moved)
            {
                // 源 zone 需要重排；目标 zone 不需要（新来的排在最后）。
                touched.Add(item.Id);
            }

            foreach (var item in Store.Items)
            {
                if (touched.Contains(item.Id) || item.Actor == null) continue;

                var target = Store.CurrentPosition(item);
                if ((target - item.Actor.LivePosition).sqrMagnitude < 1e-6f) continue;

                Vector3 from = item.Actor.LivePosition;
                item.LivePosition = target;
                RunTween(TweenPosition(item.Actor, item, from, target, ev));
            }
        }

        private void TriggerRotate(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                float to = ev.action == "flip" ? actor.LiveRotation + 180f : ev.angle;
                RunTween(TweenRotation(actor, actor.LiveRotation, to, ev));
                actor.LiveRotation = to;
            }
        }

        private void TriggerScale(CueAnimEvent ev)
        {
            float factor = ev.scale <= 0f ? 1.25f : ev.scale;
            float step = ev.stagger > 0f ? ev.stagger : 0f;
            float scaledAt = clock * Mathf.Max(0.01f, timeScale);
            int scheduled = 0;

            foreach (var actor in Resolve(ev))
            {
                if (step <= 0f)
                {
                    ApplyScale(actor, factor, ev);
                }
                else
                {
                    // stagger：同一组组件错开缩放，视觉上是「一枚一枚被强调」。
                    pendingScales.Add(new PendingScale
                    {
                        At = scaledAt + scheduled * step,
                        Actor = actor,
                        Factor = factor,
                        Event = ev,
                    });
                }
                scheduled++;
            }
        }

        private void ApplyScale(CueAnimActor actor, float factor, CueAnimEvent ev)
        {
            Vector3 from = actor.LiveScale;
            Vector3 to = ev.scale_mode == "to" ? actor.BaseScale * factor : from * factor;
            actor.LiveScale = to;
            RunTween(TweenScale(actor, from, to, ev));
        }

        /// <summary>
        /// 淡入/淡出。走**片段**而不是协程：与其它原语统一，因此
        /// ① 批处理/离线出图能看到；② 跳转与顺序播放一致；③ 暂停会跟着停。
        /// 之前用协程，导致它在离屏渲染里根本不发生（测不出来）。
        /// </summary>
        private void TriggerFade(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                if (actor?.Item == null) continue;
                actor.Item.Shown = true;   // 被淡入/淡出带出来了
                float from = actor.LiveAlpha;
                float to = ev.to_alpha >= 0f
                    ? Mathf.Clamp01(ev.to_alpha)
                    : (from > 0.5f ? 0f : 1f);

                var clip = ClipAt(actor.Item, actor, ev);
                clip.HasAlpha = true;
                clip.AlphaFrom = from;
                clip.AlphaTo = to;
            }
        }

        /// <summary>
        /// highlight：目标可以是某件组件，也可以是整个 zone。
        /// zone 的情况只脉冲该区域的装饰底板（panel/dot），不脉冲里面每一枚宝石，
        /// 否则「高亮供应区」会变成整堆宝石一起闪。
        /// </summary>
        /// <summary>
        /// shuffle：洗混是**原地**表现，不改变任何组件的位置。
        ///
        /// 以前这里把每张牌搬到新位置（「交叉换位」），作用于整摞 40 张时会把牌堆
        /// 摊成一条横跨画面的长龙 —— 洗牌不该把牌洗到桌面上。所以改成原地抖动：
        /// 轻微摇晃 + 微小缩放起伏，位置始终不变。
        /// </summary>
        /// <summary>
        /// 洗混参数：**所有牌堆共用这一处**。
        /// 想调整洗牌手感（幅度/频率/纵向分量/收尾速度）只改这里，
        /// 不必去动每个 cue，也不必复制逻辑。
        /// </summary>
        private static class Shuffle
        {
            /// <summary>水平幅度区间（世界单位，1 单位 = 100mm）。4~6.2mm：看得出「发毛」又不散开。</summary>
            public const float AmpMin = 0.040f, AmpMax = 0.062f;
            /// <summary>抖动频率区间（Hz）。8~14Hz 接近人手搓牌。</summary>
            public const float FreqMin = 8f, FreqMax = 14f;
            /// <summary>纵向幅度占水平的比例区间。让抖动不是一个方向的平移。</summary>
            public const float DepthMin = 0.25f, DepthMax = 0.60f;
            /// <summary>包络形状：前 1/4 起振、中段保持、末 1/4 收住。</summary>
            public const float EnvelopePower = 0.45f;

            /// <summary>确定性伪随机 → [0,1)，不用 Random，保证每次播放一致。</summary>
            public static float Rand(int h, int salt) => Hash01(h, salt);
        }

        /// <summary>
        /// 整组高亮/选中：以这一组的**重心**为锚点整体放大再回落。
        ///
        /// 为什么要按组：牌堆由几十张叠成，单张缩放只会让其中一张（还往往是堆底那张）
        /// 变大，看起来是「一张牌出错」而不是「这一堆被选中」。整组缩放才对。
        /// 用重心而不是某张牌的位置，是为了对任意形状的组都成立（牌堆、宝石堆、玩家持有区）。
        /// </summary>
        /// <summary>
        /// 对一组组件做整组缩放。锚点由调用方给出（通常是组重心，也可由容器自带）。
        ///
        /// 与逐个缩放的区别：位置也相对锚点一起外扩，等效于「把这一组当一个对象缩放」。
        /// 逐个缩放只会让每张牌各自变大、整堆并不看起来变大。
        /// </summary>
        private void GroupScaleActors(List<CueAnimActor> actors, Vector3 center,
                                      float grow, float dur, float lead, string easing)
        {
            if (actors == null || actors.Count == 0) return;
            var synthetic = new CueAnimEvent { at = currentEventAt, dur = dur, lead = lead, easing = easing };
            foreach (var actor in actors)
            {
                if (actor?.Item == null) continue;
                var clip = ClipAt(actor.Item, actor, synthetic);
                clip.HasGroupScale = true;
                clip.GroupCenter = center;
                clip.GroupGrow = grow;
            }
        }

        /// <summary>整组平移：组内每件位移相同，相对位置保持不变。</summary>
        private void GroupShiftActors(List<CueAnimActor> actors, Vector3 delta,
                                      float dur, float lead, string easing)
        {
            if (actors == null || actors.Count == 0) return;
            var synthetic = new CueAnimEvent { at = currentEventAt, dur = dur, lead = lead, easing = easing };
            foreach (var actor in actors)
            {
                if (actor?.Item == null) continue;
                var clip = ClipAt(actor.Item, actor, synthetic);
                clip.HasGroupShift = true;
                clip.GroupShiftFrom = Vector3.zero;
                clip.GroupShiftTo = delta;
            }
        }

        private void GroupScaleZone(string zoneId, float grow, float dur, float lead, string easing)
        {
            var actors = new List<CueAnimActor>();
            foreach (var item in Store.Items)
                if (item.ZoneId == zoneId && item.Actor != null) actors.Add(item.Actor);
            if (actors.Count == 0) return;

            var center = Vector3.zero;
            foreach (var a in actors) center += a.LivePosition;
            center /= actors.Count;

            GroupScaleActors(actors, center, grow, dur, lead, easing);
        }

        /// <summary>
        /// 创建组件：让对象**真的出现**在桌上（而不只是把已存在的对象显形）。
        ///
        /// 与「初始就在桌上、只是透明」的区别：
        ///   - 语义清楚：这一幕之前，这个对象**不存在**（符合"这时才是卡堆登场"）
        ///   - 不必为每个"以后可能出现"的东西写占位数据，也不用管它的默认透明度
        /// 入口状态重放时会一并重放 create，所以跳转与顺序播放一致。
        /// </summary>
        /// <summary>
        /// 建一整摞牌（公共原语）。
        ///
        /// 一摞牌 = 顶面若干张"真实存在的牌" + 底下垫满的空白牌。
        /// 用户对牌堆的定义（也是"发牌天然不变形"的原因）：
        ///   · `order` 越小越靠**牌堆顶**，第一个被发走；
        ///   · 只有离顶最远的 `max_visible` 张形成台阶，其余全部重合；
        ///   · 顶面那一叠（`order 0..capacity-max_visible-1`）是重合块，
        ///     发它们不改变外形。
        ///
        /// 关键：**真牌必须占最小的 order**。所以这里**倒着建** ——
        /// 先建垫牌（拿小 order）、再按 real_templates 的**倒序**建真牌（拿大 order 之上的），
        /// 这样 real_templates 的第一个正好落在"顶面 + 重合块"里，成为第一个被发走的。
        ///
        /// 用 `NextFreeSlot` 的旧写法（谁先建谁 order 小）规则隐晦、方向易反；
        /// 这里把意图一步说清：给牌面顺序和垫牌模板，其余交给引擎。
        /// </summary>
        private void TriggerStack(CueAnimEvent ev)
        {
            if (string.IsNullOrEmpty(ev.destination)) { Debug.LogWarning("[TutorialCueAnim] stack 缺少 zone"); return; }

            var realIds = (ev.real_templates ?? "")
                .Split(new[] { ',', '|', ' ' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (realIds.Count == 0) { Debug.LogWarning($"[TutorialCueAnim] stack 缺少 real_templates（cue {CueId}）"); return; }

            int capacity = ev.capacity > 0 ? ev.capacity : realIds.Count;
            if (capacity < realIds.Count) capacity = realIds.Count;
            int pad = capacity - realIds.Count;

            // ① **真牌先建** → 拿到**最小**的 order = 牌堆顶面（`order 0` 第一个被发走）。
            //    垫牌后建 → 拿较大的 order，落在牌堆更里面。
            //    （反过来就会让真牌沉到底下 —— 表现是"从牌堆底发牌"。）
            int realMade = 0;
            foreach (var tid in realIds)
            {
                if (Store.GetTemplate(tid) == null)
                {
                    Debug.LogWarning($"[TutorialCueAnim] stack 未知模板 '{tid}'（cue {CueId}）");
                    continue;
                }
                if (Store.CountInZone(ev.destination, tid) > 0) continue;   // 幂等
                var made = Store.Spawn(tid, ev.plain ? null : ev.palette, ev.destination, 1);
                foreach (var it in made)
                {
                    if (it == null) continue;
                    it.Shown = true;
                    it.Flipped = ev.to == "face_up";   // 默认背面朝上（牌堆就是这样）
                    BuildActorObject(it);
                    realMade++;
                }
            }

            // ② 垫牌后建 → 拿到较大的 order（牌堆更里面）
            int padMade = 0;
            if (pad > 0 && !string.IsNullOrEmpty(ev.pad_template))
            {
                int have = Store.CountInZone(ev.destination, ev.pad_template);
                int need = pad - have;
                if (need > 0)
                {
                    var made = Store.Spawn(ev.pad_template, ev.plain ? null : ev.palette, ev.destination, need);
                    foreach (var it in made)
                    {
                        if (it == null) continue;
                        it.Shown = true;
                        it.Flipped = ev.to == "face_up";   // 垫牌默认背面朝上
                        BuildActorObject(it);
                        padMade++;
                    }
                }
            }

            Debug.Log($"[TutorialCueAnim] stack {ev.destination}: 垫牌 +{padMade} 真牌 +{realMade} " +
                      $"→ 共 {Store.CountInZone(ev.destination)} 张（capacity {capacity}，顶面 {realIds[0]}）");
        }

        private void TriggerCreate(CueAnimEvent ev)
        {
            if (string.IsNullOrEmpty(ev.template))
            {
                Debug.LogWarning($"[TutorialCueAnim] create 缺少 template（cue {CueId}）");
                return;
            }
            if (Store.GetTemplate(ev.template) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] create 未知模板 '{ev.template}'（cue {CueId}）");
                return;
            }
            string zone = string.IsNullOrEmpty(ev.destination) ? "offstage" : ev.destination;
            int n = ev.count > 0 ? ev.count : 1;

            // 幂等：只补齐差额。
            // 「创建 N 件」如果被触发两次（重复 Seek、先解入口状态再载入等），
            // 会把牌堆建两遍（40 张变 80 张）。按已有数量补齐比"每次全建"稳妥，
            // 也让 create 可以安全地写在多条 cue 里（例如"确保牌堆已就位"）。
            // 幂等的判据必须是**这个事件自己指定的东西**：同模板不同色板是不同件
            // （五枚颜色各一的 `gem_sample`），只数模板会让第二条起全被跳过 ——
            // 表现是"写了 5 条 create，画面上只出现 1 枚"（2026-09 宝石介绍踩过）。
            int have = Store.CountInZone(zone, ev.template, ev.plain ? null : ev.palette);
            n -= have;
            if (n <= 0) return;

            // plain = 不指定色板（整摞牌在发出去之前不区分颜色）
            var created = Store.Spawn(ev.template, ev.plain ? null : ev.palette, zone, n);
            foreach (var item in created)
            {
                if (item == null) continue;
                // 指定格位：发牌要发到"第 n 格"，否则只会追加到末尾、摆不成 4 列网格
                if (ev.slot >= 0 && ev.slot != item.Order) Store.MoveToSlot(item, zone, ev.slot);
                // 创建出来的组件默认"已出场"：它不曾处于"等待出场"的状态
                item.Shown = true;
                // 背面朝上：牌堆里的牌就是这样（是哪张已定，但还没翻开）
                // 语义：Flipped = 是否正面朝上，所以这里是 false
                // 朝向：face_up 显式正面；否则默认背面（face_down 或都没写）。
                // 默认背面是有意的 —— 牌堆/待发牌本来就该先看到卡背，
                // 要正面就别省 face_up（别再靠"create + flip"两个事件凑）。
                item.Flipped = ev.to == "face_up";
                BuildActorObject(item);

                // create 带 flip = 出场过程中翻到正面（发牌时"翻开四张"）。
                // 与 move 的翻转走同一套：FlipFromYaw 起翻、逻辑状态立即到终态
                // （漏了 FlipFromYaw 会一直停在背面 —— 因为它默认 0，翻完恰好是 180°）。
                if (!string.IsNullOrEmpty(ev.to) && item.Actor != null && item.Actor.BackSprite != null)
                {
                    var clip = ClipAt(item, item.Actor, ev);
                    clip.HasFlip = true;
                    // 新建的组件没有历史偏航角：从 0° 起翻、走完半圈回到 0°，
                    // 由 sprite 决定看到的是哪一面。若沿用 move 的 (from, from+180)
                    // 会停在 180°，把正面贴图也镜像掉 —— 终态看起来仍是背面。
                    clip.FlipFromYaw = 0f;
                    clip.FlipHalfTurn = true;
                    item.Flipped = true;
                }
            }
        }

        /// <summary>
        /// 运行时开/关一个 zone（世界会长大：后续游戏可能随着卡牌/板块进场新增区域）。
        ///
        /// 定义来自 `stage.zone_defs` —— **脚本里不写坐标**（坐标只属于 stage 这一层）：
        ///   {"action": "zone", "op": "add", "zone": "workshop"}        ← 开出来
        ///   {"action": "zone", "op": "add", "zone": "market_row", "index": 2}  ← 第 3 个实例（id = market_row#2）
        ///   {"action": "zone", "op": "remove", "zone": "workshop"}     ← 关掉（要求它已经空了）
        ///
        /// 幂等：已经存在的 add 不报错（重复 Seek / 回退重放时会走到这里）。
        /// </summary>
        private void TriggerZone(CueAnimEvent ev)
        {
            string id = !string.IsNullOrEmpty(ev.zone) ? ev.zone : ev.target;
            string op = string.IsNullOrEmpty(ev.op) ? "add" : ev.op;
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning($"[TutorialCueAnim] zone 事件没写 zone（cue {CueId}）");
                return;
            }

            if (op == "remove")
            {
                Store.RemoveZone(id);
                return;
            }

            var def = stage?.zone_defs?.Find(d => d != null && d.id == id);
            if (def == null)
            {
                Debug.LogError($"[TutorialCueAnim] zone add: stage.zone_defs 里没有 '{id}'（cue {CueId}）—— " +
                               $"新增的 zone 也必须有定义，坐标只写在 stage 里");
                return;
            }

            var inst = new StageZone
            {
                id = def.id,
                label = def.label,
                role = string.IsNullOrEmpty(def.role) ? "zone" : def.role,
                center = new StagePoint
                {
                    x = def.center != null ? def.center.x : 0f,
                    z = def.center != null ? def.center.z : 0f,
                },
                layout = def.layout,
                capacity = def.capacity,
                palette = def.palette,
                size = def.size,
                concept = def.concept,
                contains = def.contains,
                margin = def.margin,
                repeat_x = def.repeat_x,
                repeat_z = def.repeat_z,
            };
            if (ev.index > 0)
            {
                inst.id = $"{def.id}#{ev.index}";
                inst.center.x += def.repeat_x * ev.index;
                inst.center.z += def.repeat_z * ev.index;
            }

            if (Store.AddZone(inst) && logMoves)
                Debug.Log($"[Zone] 开出 zone {inst.id}（center=({inst.center.x:0.00},{inst.center.z:0.00})，cue {CueId}）");
        }

        /// <summary>
        /// 销毁组件：对象**真的消失**（从数据里移除），不是调成透明。
        /// 用于「这三张卡背删掉不要了」这类：之前的对象不该继续存在于桌上。
        /// </summary>
        private void TriggerDestroy(CueAnimEvent ev)
        {
            var doomed = new List<ZoneItem>();

            if (!string.IsNullOrEmpty(ev.target))
            {
                if (Store.TryGetItem(ev.target, out var one) && one != null) doomed.Add(one);
                else Debug.LogWarning($"[TutorialCueAnim] destroy target '{ev.target}' 不存在（cue {CueId}）");
            }
            else
            {
                List<ZoneStore.TemplateChoice> cands = null;
                if (ev.HasWhat)
                {
                    cands = Store.ConceptCandidates(ev.what.concept, ev.what.parts);
                    if (cands.Count == 0)
                        Debug.LogError($"[TutorialCueAnim] destroy 的 what 一个候选都没有（cue {CueId}）：" +
                                       $"concept='{ev.what.concept}'");
                }
                foreach (var item in Store.Items)
                {
                    if (!string.IsNullOrEmpty(ev.zone) && item.ZoneId != ev.zone) continue;
                    if (cands != null)
                    {
                        if (Store.MatchesConcept(item, cands)) doomed.Add(item);
                        continue;
                    }
                    if (!string.IsNullOrEmpty(ev.template)
                        && (item.Template == null || item.Template.id != ev.template)) continue;
                    if (!string.IsNullOrEmpty(ev.palette) && item.PaletteName != ev.palette) continue;
                    doomed.Add(item);
                }
                if (doomed.Count == 0)
                    Debug.LogWarning($"[TutorialCueAnim] destroy 没匹配到任何组件" +
                                     $"（zone='{ev.zone}' template='{ev.template}'，cue {CueId}）");
                // 数量上限：`count` / `quantity` 说了几枚就只销毁几枚。
                // 为什么需要：「把每色多出来的 3 枚放回盒子」是 destroy 3 枚 ——
                // 不写上限就会把整堆 7 枚全销毁（盒子没有实体之后，destroy 就是"放回去"，
                // 而"放回几枚"是脚本里说清的数字，不能由引擎猜）。
                int limit = ev.count > 0 ? ev.count : ev.quantity;
                if (limit > 0 && doomed.Count > limit)
                {
                    doomed.Sort((a, b) => a.Order.CompareTo(b.Order));   // 从堆顶开始拿（order 小 = 在上）
                    doomed.RemoveRange(limit, doomed.Count - limit);
                }
            }

            foreach (var item in doomed)
            {
                if (item.Actor?.Go != null) Object.DestroyImmediate(item.Actor.Go);
                actors.Remove(item.Id);
                Store.RemoveItem(item.Id);
            }
        }

        /// <summary>
        /// 显示/隐藏一张整幅图片（例如游戏盒封面）。
        ///
        /// 用途：纯讲述性的小节（背景介绍）没有牌桌动作，但需要一张画面撑住。
        /// 放在很低的 sortingOrder（比任何卡牌都低），并按视口等比缩放 —— 只缩放不做裁剪，
        /// 保证整张图完整可见。
        ///
        /// 注意：它挂在 animRoot 下，所以换 cue（continueState=false）重建画面时会自动清掉，
        /// 进入下一节不需要额外写"隐藏"事件。
        /// </summary>
        /// <summary>
        /// 应用「树根画面」：stage.board.default_picture。
        /// 入口状态从根开始解，所以每次解入口都会先摆成根的样子，再由 cue 的事件改变。
        /// 这样「按右跳转」和「顺序播到同一条」得到完全相同的画面。
        /// </summary>
        /// <summary>
        /// 从树根（牌桌 initial）出发，把本条 cue **之前**每一条的事件都推到终态，
        /// 于是 Store 正好落在这条 cue 的入口状态。
        ///
        /// 做法与 `TutorialFrameCapture` 的 `-dumpReplay` 一致：逐条设成当前 cue、Seek 到末尾。
        /// 期间 `stateOnly = true`：只改状态，不建渲染对象（渲染对象由本条 cue 的
        /// BuildActorObjects() 一次性建出来）。
        /// </summary>
        /// <summary>
        /// 整条轨道的 cue 顺序（**含没有动画的那些**）。由播放器在载入运行时数据后交过来。
        ///
        /// 为什么必须有它：入口链重放要"重放到**本条之前**就停"。而动画脚本里只有 17 条
        /// 有动画的 cue，另外 92 条纯口播 cue **不在里面** —— 按动画脚本的顺序找目标，
        /// 找不到就会一路重放到整条轨道结束（跳到一条纯口播 cue 会得到"全片终态"）。
        /// </summary>
        private List<string> cueOrder;

        public void SetCueOrder(List<string> ids)
        {
            cueOrder = ids != null ? new List<string>(ids) : null;
        }

        private void ReplayEntryChain(TrackAnimDoc trackDoc, string targetCueId)
        {
            Store.Reset();
            Store.ApplyInitial();
            if (trackDoc?.cues == null) return;

            // 按**整条轨道**的顺序走，遇到目标就停；动画脚本里没有的 cue 跳过（它没有事件）。
            var byId = new Dictionary<string, CueAnimDoc>();
            foreach (var cc in trackDoc.cues)
                if (cc != null && !string.IsNullOrEmpty(cc.cue)) byId[cc.cue] = cc;
            var order = cueOrder ?? trackDoc.cues.ConvertAll(c => c != null ? c.cue : null);

            bool prevStateOnly = stateOnly;
            stateOnly = true;
            try
            {
                foreach (var id in order)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id == targetCueId) break;             // 只重放本条之前（在**整轨顺序**里找目标）
                    if (!byId.TryGetValue(id, out var c)) continue;   // 这条没有动画，跳过
                    if (c.events == null || c.events.Count == 0) continue;

                    cueDoc = c;
                    CueId = c.cue;
                    clips.Clear();
                    clock = 0f;
                    nextIndex = 0;
                    for (float t = 0f; t <= TotalDuration + 1f; t += 0.05f) Seek(t);
                }
            }
            finally
            {
                stateOnly = prevStateOnly;
                clips.Clear();
                clock = 0f;
                nextIndex = 0;
            }

            // 把入口状态的事实打出来。上一次这个 bug 就是靠这类"事实行"定位的：
            // 它显示 `入口=初始态 market=0 deck1=0` —— 一眼看出入口根本没被解出来。
            int total = 0;
            foreach (var it in Store.Items) total++;
            Debug.Log($"[TutorialCueAnim] 入口状态（从根重放到 {targetCueId}）：" +
                      $"market={Store.CountInZone("card_market")} " +
                      $"deck1={Store.CountInZone("deck_level_1")} " +
                      $"deck2={Store.CountInZone("deck_level_2")} " +
                      $"deck3={Store.CountInZone("deck_level_3")} " +
                      $"总={total} 整幅图={(string.IsNullOrEmpty(currentPicture) ? "无" : currentPicture)}");
        }

        /// <summary>
        /// 这条 cue **入口**时该显示哪张整幅图（null = 不显示）。
        ///
        /// 从树根画面（`stage.board.default_picture`）出发，把这条 cue **之前**所有 cue 里的
        /// `showbox` 事件按时间顺序走一遍，最后一个说了算。
        ///
        /// 为什么需要它：整幅图是播放器级状态，但它**不属于组件** —— `ZoneSnapshot` 只记
        /// 「件 → (zone, order)」，所以跳跃时这一维无从恢复。以前的做法是"沿用当前那张图"，
        /// 于是从有图的 cue 跳进没图的 cue 会把旧图带过去（用户报的"cue 10 背景出现盒面"）。
        /// 组件那一维靠入口快照，这一维就靠这段复算 —— 与"跳转由编译期复算入口状态"同一思路，
        /// 只是它便宜到可以在载入时算。
        /// </summary>
        private string EntryPictureFor(TrackAnimDoc trackDoc, string cueId)
        {
            string pic = stage?.board != null ? stage.board.default_picture : null;
            if (trackDoc?.cues == null) return pic;
            var events = new List<CueAnimEvent>();
            foreach (var c in trackDoc.cues)
            {
                if (c == null) continue;
                if (c.cue == cueId) break;           // 只算这条 cue **之前**的
                if (c.events == null) continue;
                events.Clear();
                events.AddRange(c.events);
                events.Sort((a, b) => (a?.at ?? 0f).CompareTo(b?.at ?? 0f));  // 同一 cue 内按时间
                foreach (var ev in events)
                    if (ev != null && ev.action == "showbox")
                        pic = (ev.on >= 0.5f && !string.IsNullOrEmpty(ev.picture)) ? ev.picture : null;
            }
            return pic;
        }

        private void ApplyRootPicture()
        {
            string pic = stage?.board != null ? stage.board.default_picture : null;
            TriggerShowBox(new CueAnimEvent
            {
                action = "showbox",
                picture = pic,
                on = string.IsNullOrEmpty(pic) ? 0f : 1f,
            });
        }

        private void TriggerShowBox(CueAnimEvent ev)
        {
            bool show = ev.on >= 0.5f;
            if (!show || string.IsNullOrEmpty(ev.picture))
            {
                if (boxSprite != null) { Object.DestroyImmediate(boxSprite.gameObject); boxSprite = null; }
                currentPicture = null;
                return;
            }

            if (string.IsNullOrEmpty(gameRootPath)) return;
            var path = Path.Combine(gameRootPath, ev.picture);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[TutorialCueAnim] showbox 图片不存在: {path}");
                return;
            }

            // 用 gem 形状（圆遮罩对这张图不适用）→ 走 card 分支只做白底抠除；
            // 这张图没有白底，抠除不影响画面。
            var sprite = CardImageLoader.Load(path, "card");
            if (sprite == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] showbox 加载失败: {path}");
                return;
            }

            if (boxSprite == null)
            {
                var go = new GameObject("BoxArt");
                // 挂在播放器自己下面，**不是** animRoot：
                // animRoot 是牌桌根节点，显示盒面时会被整体隐藏，盒面不能被一起藏掉。
                go.transform.SetParent(transform, false);
                boxSprite = go.AddComponent<SpriteRenderer>();
                boxSprite.sortingOrder = -900;   // 比卡牌低，但在纯色背景之上
            }
            boxSprite.sprite = sprite;
            boxSprite.enabled = true;
            currentPicture = ev.picture;   // 显式记住路径（Sprite.Create 不给 sprite 命名）

            // 等比缩放到视口的 ~92% 高度，居中放在取景中心
            float aspect = animCamera != null && animCamera.aspect > 0.01f ? animCamera.aspect : 1.7778f;
            float ortho = animCamera != null ? animCamera.orthographicSize : 2.8f;
            float viewH = 2f * ortho;
            float viewW = viewH * aspect;

            float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : 100f;
            float nativeW = sprite.rect.width / ppu;
            float nativeH = sprite.rect.height / ppu;

            float k = Mathf.Min(viewH * 0.92f / nativeH, viewW * 0.92f / nativeW);
            boxSprite.transform.localScale = new Vector3(k, k, 1f);

            // 让它**正对相机**并居中（与组件面片同一套朝向，见 SpriteRotation）：
            // 盒面是一张竖图，斜视相机下若平躺就会被压扁并跑到画面底部。
            var cam = animCamera != null ? animCamera : Camera.main;
            if (cam != null)
            {
                var tr = boxSprite.transform;
                tr.rotation = cam.transform.rotation;                     // 与相机同朝向（= SpriteRotation(0)）
                tr.position = cam.transform.position + cam.transform.forward * ortho;
            }

            // 不动牌桌：第一节的牌桌是空的（牌堆还在盒子里），第二节的牌堆从盒面**后面**
            // 飞出来，正是要的效果。曾经这里把 animRoot 整体关掉却忘了打开，
            // 结果盒面一消失画面就全空了。
        }

        /// <summary>字符串的稳定散列（与平台/运行次数无关）。</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 17;
                if (s != null) foreach (char c in s) h = h * 31 + c;
                return h;
            }
        }

        /// <summary>把散列值映射到 [0,1)。</summary>
        private static float Hash01(int h, int salt)
        {
            unchecked
            {
                int x = h ^ (salt * 2654435761u.GetHashCode());
                x = (x ^ (x >> 15)) * 0x2c1b3c6d;
                x = (x ^ (x >> 12)) * 0x297a2d39;
                x ^= x >> 15;
                return (x & 0x7fffffff) / (float)0x80000000;
            }
        }

        /// <summary>
        /// 洗混：指定 zone 内的每张牌**原地左右高频颤抖**。
        ///
        /// 通用方法：任何 zone 都能用（牌堆、供应堆、玩家持有区…），
        /// 只要给一个 shuffle 事件指向它即可，参数由 Shuffle 统一给出。
        ///
        /// 关键是**每张牌都不一样**：用组件 id 散列出幅度、频率、初相和纵向分量，
        /// 同一瞬间有的向左有的向右、抖得也不一样快。若所有牌同相摆动，
        /// 看起来只是整摞在晃，不像洗牌。
        /// 幅度很小（毫米级），叠在一起的牌只会让牌堆边缘发毛 —— 正是要的效果。
        /// </summary>
        private void TriggerShuffle(CueAnimEvent ev)
        {
            // 强度倍率：0 表示用默认 1.0。小棋子（宝石）或大牌堆可以各自调。
            float scale = ev.amount > 0f ? ev.amount : 1f;

            int i = 0;
            foreach (var actor in Resolve(ev))
            {
                if (actor?.Go == null || actor.Item == null) continue;

                // 由组件 id + 序号散列出确定性伪随机（不用 Random，保证可复现）
                int h = StableHash(actor.Item.Id) + i * 7919;
                float r1 = Shuffle.Rand(h, 1);
                float r2 = Shuffle.Rand(h, 2);
                float r3 = Shuffle.Rand(h, 3);
                float r4 = Shuffle.Rand(h, 4);

                var clip = ClipAt(actor.Item, actor, ev);
                clip.HasShuffle = true;
                WarnIfInvalid(actor.Item, actor.LivePosition, "shuffle 基准");
                // 基准若已失效就用格位表的位置，绝不让坏坐标进入插值
                var shuffleBase = actor.LivePosition;
                if (float.IsNaN(shuffleBase.x) || float.IsNaN(shuffleBase.z))
                    shuffleBase = Store.CurrentPosition(actor.Item);
                clip.ShuffleFrom = shuffleBase;
                clip.ShuffleAmp = Mathf.Lerp(Shuffle.AmpMin, Shuffle.AmpMax, r1) * scale;
                clip.ShuffleFreq = Mathf.Lerp(Shuffle.FreqMin, Shuffle.FreqMax, r2);
                clip.ShufflePhase = r3 * Mathf.PI * 2f;   // 初相不同 → 瞬时方向不同
                clip.ShuffleZ = clip.ShuffleAmp * Mathf.Lerp(Shuffle.DepthMin, Shuffle.DepthMax, r4);
                i++;
            }
        }

        // ── 协程 ──────────────────────────────────────────────────────────

        private IEnumerator TweenPosition(CueAnimActor actor, ZoneItem item, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            yield return TutorialPrimitives.TweenPosition(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LivePosition = to;
            item.LivePosition = to;
        }

        private IEnumerator TweenRotation(CueAnimActor actor, float from, float to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            float dur = Mathf.Max(ev.dur, 0f);
            if (dur <= 0f)
            {
                actor.Go.transform.localRotation = SpriteRotation(to);
            }
            else
            {
                float t = 0f;
                while (t < dur)
                {
                    t = Mathf.Min(t + TutorialPrimitives.Delta, dur);
                    float k = Easing.Evaluate(EasingOr(ev), t / dur);
                    actor.Go.transform.localRotation = SpriteRotation(Mathf.LerpUnclamped(from, to, k));
                    yield return null;
                }
                actor.Go.transform.localRotation = SpriteRotation(to);
            }
            actor.LiveRotation = to;
        }

        private IEnumerator TweenScale(CueAnimActor actor, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            actor.Go.transform.localScale = from;
            yield return TutorialPrimitives.TweenScale(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LiveScale = to;
        }

        private IEnumerator TweenAlpha(CueAnimActor actor, float from, float to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            if (actor.Renderer != null)
            {
                var c = actor.LiveColor;
                c.a = from;
                actor.Renderer.color = c;
                yield return TutorialPrimitives.TweenAlpha(actor.Renderer, from, to, ev.dur, EasingOr(ev));
            }
            actor.LiveAlpha = to;
        }

        private static IEnumerator WaitScaled(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += TutorialPrimitives.Delta;
                yield return null;
            }
        }

        /// <summary>装饰件（区域底板/高亮层）不参与原地缩放脉冲。</summary>
        private static bool IsDecoration(StageTemplate tpl)
        {
            return tpl != null && (tpl.shape == "panel" || tpl.shape == "dot");
        }

        private static string EasingOr(CueAnimEvent ev)
        {
            return string.IsNullOrEmpty(ev.easing) ? Easing.Default : ev.easing;
        }

        private void RunTween(IEnumerator routine)
        {
            if (routine == null) return;
            running.Add(StartCoroutine(routine));
        }

        // ── 解析 ──────────────────────────────────────────────────────────

        private CueAnimActor FindActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return actors.TryGetValue(id, out var actor) ? actor : null;
        }

        private enum Selector { None, Target, Container, What, Zone }

        /// <summary>判定事件用的是哪种选择器（优先级 target &gt; container &gt; zone）。</summary>
        private Selector PickSelector(CueAnimEvent ev)
        {
            if (ev == null) return Selector.None;
            bool hasTarget = !string.IsNullOrEmpty(ev.target);
            bool hasContainer = !string.IsNullOrEmpty(ev.container);
            bool hasWhat = ev.HasWhat;
            bool hasZone = !string.IsNullOrEmpty(ev.zone);

            // `what` + `zone` **不是二选一**：what 说"哪一种"，zone 说"在哪找"。
            // 只有 target / container / what 三者互相冲突。
            int n = (hasTarget ? 1 : 0) + (hasContainer ? 1 : 0) + (hasWhat ? 1 : 0);
            if (n > 1)
                Debug.LogWarning($"[TutorialCueAnim] 事件同时指定了多个选择器" +
                                 $"（target='{ev.target}' container='{ev.container}' " +
                                 $"what='{ev.what?.concept}'，cue {CueId}）：" +
                                 $"按 target > container > what > zone 取优先级最高的");

            if (hasTarget) return Selector.Target;
            if (hasContainer) return Selector.Container;
            if (hasWhat) return Selector.What;
            if (hasZone) return Selector.Zone;
            return Selector.None;
        }

        /// <summary>
        /// 解析容器：返回它点名的组件。
        /// 容器只是「一组 id」，组内每个组件仍是独立组件 —— 所以既能整组操作，
        /// 也能单独操作其中一件。找不到的 id 会警告（数据写错时能立刻发现）。
        /// </summary>
        private List<ZoneItem> ResolveContainer(string containerId)
        {
            var found = new List<ZoneItem>();
            if (string.IsNullOrEmpty(containerId)) return found;

            StageContainer def = null;
            if (stage?.containers != null)
                foreach (var c in stage.containers)
                    if (c != null && c.id == containerId) { def = c; break; }

            if (def?.items == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] 容器 '{containerId}' 未定义（cue {CueId}）");
                return found;
            }

            foreach (var id in def.items)
            {
                ZoneItem item = null;
                if (string.IsNullOrEmpty(id) || !Store.TryGetItem(id, out item) || item == null)
                {
                    Debug.LogWarning($"[TutorialCueAnim] 容器 '{containerId}' 里的 '{id}' 不存在（cue {CueId}）");
                    continue;
                }
                found.Add(item);
            }
            return found;
        }

        /// <summary>容器的锚点：定义了中心就用它，否则用组内重心。</summary>
        private Vector3 ContainerCenter(string containerId, List<ZoneItem> items)
        {
            if (stage?.containers != null)
                foreach (var c in stage.containers)
                    if (c != null && c.id == containerId && c.has_center)
                        return new Vector3(c.x, 0f, c.z);

            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var it in items) { if (it?.Actor == null) continue; sum += it.Actor.LivePosition; n++; }
            return n > 0 ? sum / n : Vector3.zero;
        }

        private List<CueAnimActor> Resolve(CueAnimEvent ev)
        {
            var result = new List<CueAnimActor>();
            if (ev == null) return result;

            // 选择器规则（全操作统一），优先级 target > container > zone：
            //   target    = 单件
            //   container = 任意一组（点名的一组，不要求同 zone）
            //   zone      = 该区域全部
            // 同时写多个是数据错误：报警告，按优先级取最高的那个。
            // 曾经这里的顺序是 zone 优先，而 highlight 是 target 优先，
            // 同一个事件在两类操作里会选中不同的东西。
            var picked = PickSelector(ev);
            if (picked == Selector.Target)
            {
                var actor = FindActor(ev.target);
                if (actor != null) result.Add(actor);
                return result;
            }
            if (picked == Selector.Container)
            {
                foreach (var item in ResolveContainer(ev.container))
                    if (item?.Actor != null) result.Add(item.Actor);
                return result;
            }
            if (picked == Selector.What)
            {
                // 按**概念**选件：本体 <transfer> 的 <object> 字段说明里写着它
                // "同时承担 <event> 中 target 的语义"，所以这就是"点名一件"的正规写法。
                // zone 限定范围，order 指定第几位（-1 = 不限）。
                var cands = Store.ConceptCandidates(ev.what.concept, ev.what.parts);
                if (cands.Count == 0)
                {
                    Debug.LogError($"[TutorialCueAnim] what 一个候选都没有（cue {CueId}）：" +
                                   $"concept='{ev.what.concept}'");
                    return result;
                }
                foreach (var item in Store.Items)
                {
                    if (item?.Actor == null) continue;
                    if (!string.IsNullOrEmpty(ev.zone) && item.ZoneId != ev.zone) continue;
                    if (ev.order >= 0 && item.Order != ev.order) continue;
                    if (!Store.MatchesConcept(item, cands)) continue;
                    result.Add(item.Actor);
                }
                if (result.Count == 0)
                    Debug.LogWarning($"[TutorialCueAnim] what='{ev.what.concept}' " +
                                     $"{(string.IsNullOrEmpty(ev.zone) ? "" : $"在 {ev.zone} ")}" +
                                     $"没选中任何件（cue {CueId}）");
                else if (result.Count > 1 && ev.order < 0)
                    // 静态检查判断不了"在这个 zone 里唯一不唯一"（那要真实状态），
                    // 所以由运行期把事实报出来：选中了不止一件。
                    Debug.LogWarning($"[TutorialCueAnim] what='{ev.what.concept}' " +
                                     $"{(string.IsNullOrEmpty(ev.zone) ? "" : $"在 {ev.zone} ")}" +
                                     $"选中了 {result.Count} 件（没给 order，cue {CueId}）");
                return result;
            }
            if (picked == Selector.Zone)
            {
                foreach (var item in Store.Items)
                    if (item.ZoneId == ev.zone && item.Actor != null) result.Add(item.Actor);
                return result;
            }

            foreach (var item in Store.Items)
                if (item.Actor != null) result.Add(item.Actor);
            return result;
        }

        // ── 相机与背景 ────────────────────────────────────────────────────

        private void EnsureCamera()
        {
            if (animCamera == null) animCamera = Camera.main;
            if (animCamera == null)
            {
                var camGo = new GameObject("TutorialCueCamera");
                camGo.tag = "MainCamera";
                animCamera = camGo.AddComponent<Camera>();
            }
            animCamera.orthographic = true;
            animCamera.clearFlags = CameraClearFlags.SolidColor;
        }

        private void SetBackground()
        {
            if (animCamera == null) return;
            var bg = new Color(0.12f, 0.13f, 0.16f, 1f);
            if (stage?.board != null && !string.IsNullOrEmpty(stage.board.background))
            {
                Color parsed;
                if (Palette.TryResolveRgb(stage.board.background, out parsed)) bg = parsed;
            }
            animCamera.backgroundColor = bg;
        }

        /// <summary>按牌桌的 zone 范围取景；俯角由 stage.board.camera_pitch 决定（90 = 正俯视）。</summary>
        private void FitCamera()
        {
            float pitch = CameraPitch;
            float orthoScale = stage?.board != null && stage.board.ortho_scale > 0f ? stage.board.ortho_scale : 1.18f;

            float minX = -1.5f, maxX = 1.5f, minZ = -1f, maxZ = 1.4f;
            bool any = false;

            // extent 是取景的唯一依据；没有时才退回按 zone 布局推算（容易因 cols 写错而失准）。
            var extent = stage?.board?.extent;
            if (extent != null && extent.max_x > extent.min_x && extent.max_z > extent.min_z)
            {
                minX = extent.min_x; maxX = extent.max_x;
                minZ = extent.min_z; maxZ = extent.max_z;
                any = true;
            }

            foreach (var zone in Store.Zones)
            {
                if (any || zone.role == "offstage") continue;
                var layout = zone.layout ?? new StageLayout();
                int capacity = zone.capacity > 0 ? zone.capacity : 1;
                int cols = Mathf.Max(1, layout.cols);
                int rows = Mathf.Max(1, Mathf.CeilToInt(capacity / (float)cols));
                float halfW = Mathf.Max(0.05f, (cols - 1) * 0.5f * layout.x_step);
                float halfH = Mathf.Max(0.05f, (rows - 1) * 0.5f * layout.z_step);
                const float pad = 0.26f;

                float x0 = zone.center.x - halfW - pad, x1 = zone.center.x + halfW + pad;
                float z0 = zone.center.z - halfH - pad, z1 = zone.center.z + halfH + pad;
                if (!any)
                {
                    minX = x0; maxX = x1; minZ = z0; maxZ = z1; any = true;
                }
                else
                {
                    minX = Mathf.Min(minX, x0); maxX = Mathf.Max(maxX, x1);
                    minZ = Mathf.Min(minZ, z0); maxZ = Mathf.Max(maxZ, z1);
                }
            }

            // 取景写成多个 zone id（逗号分隔）= 把它们**一起**框住。
            // 用途：「宝石展示位 + 黄金展示位」这种"两块一起看"的镜头。
            if (!string.IsNullOrEmpty(frameZoneId) && frameZoneId.IndexOf(',') >= 0)
            {
                minX = float.MaxValue; maxX = float.MinValue;
                minZ = float.MaxValue; maxZ = float.MinValue;
                foreach (var zid in frameZoneId.Split(','))
                {
                    var z = Store.GetZone(zid.Trim());
                    if (z == null || z.role == "offstage") continue;
                    int c = Mathf.Max(1, z.capacity > 0 ? z.capacity : 1);
                    float hw = (z.size?.w ?? 0.2f) * 0.5f;
                    float hh = (z.size?.h ?? 0.2f) * 0.5f;
                    for (int i = 0; i < c; i++)
                    {
                        var q = Store.ZonePosition(z.id, i);
                        minX = Mathf.Min(minX, q.x - hw); maxX = Mathf.Max(maxX, q.x + hw);
                        minZ = Mathf.Min(minZ, q.z - hh); maxZ = Mathf.Max(maxZ, q.z + hh);
                    }
                }
                orthoScale = framePadding > 0f ? framePadding : 1.25f;
            }
            // 取景 "supply"：把整排供应堆（凡用 panel_supply 色板的 zone）一起框住。
            // 与 "cards" 同类，但成员按色板判定，不写死 zone 名。
            if (frameZoneId == FrameSupplyToken)
            {
                minX = float.MaxValue; maxX = float.MinValue;
                minZ = float.MaxValue; maxZ = float.MinValue;
                int members = 0;
                foreach (var zone in Store.Zones)
                {
                    if (zone.role == "offstage" || zone.palette != SupplyPalette) continue;
                    int cnt = Mathf.Max(1, zone.capacity > 0 ? zone.capacity : 1);
                    float hw = (zone.size?.w ?? 0.2f) * 0.5f;
                    float hh = (zone.size?.h ?? 0.2f) * 0.5f;
                    for (int i = 0; i < cnt; i++)
                    {
                        var q = Store.ZonePosition(zone.id, i);
                        minX = Mathf.Min(minX, q.x - hw); maxX = Mathf.Max(maxX, q.x + hw);
                        minZ = Mathf.Min(minZ, q.z - hh); maxZ = Mathf.Max(maxZ, q.z + hh);
                    }
                    members++;
                }
                if (members == 0)
                {
                    // 不静默失败：取景目标找不到成员时，必须留下痕迹（否则画面悄悄退回上一次取景）
                    Debug.LogWarning($"[CueAnim.FitCamera] 取景 'supply' 没找到任何 palette='{SupplyPalette}' 的 zone，" +
                                     $"沿用上一次取景（cue {CueId}）");
                    return;
                }
                orthoScale = framePadding > 0f ? framePadding : 1.25f;
            }
            // 取景 "cards"：把 showcase（1,2,3）三张并排的卡背一起框住
            else if (frameZoneId == FrameCardsToken)
            {
                float hw = 0.315f, hh = 0.44f;
                minX = float.MaxValue; maxX = float.MinValue;
                minZ = float.MaxValue; maxZ = float.MinValue;
                foreach (var zid in new[] { "showcase_1", "showcase_2", "showcase_3" })
                {
                    var q = Store.ZonePosition(zid, 0);
                    minX = Mathf.Min(minX, q.x - hw); maxX = Mathf.Max(maxX, q.x + hw);
                    minZ = Mathf.Min(minZ, q.z - hh); maxZ = Mathf.Max(maxZ, q.z + hh);
                }
                orthoScale = framePadding > 0f ? framePadding : 1.5f;
            }
            // 特写：把取景范围换成指定 zone 的格位包围盒
            else if (!string.IsNullOrEmpty(frameZoneId))
            {
                var fz = Store.GetZone(frameZoneId);
                if (fz != null && fz.role != "offstage")
                {
                    int cnt = Mathf.Max(1, fz.capacity > 0 ? fz.capacity : Store.CountInZone(frameZoneId));
                    float hw = (fz.size?.w ?? 0.2f) * 0.5f;
                    float hh = (fz.size?.h ?? 0.2f) * 0.5f;
                    minX = float.MaxValue; maxX = float.MinValue;
                    minZ = float.MaxValue; maxZ = float.MinValue;
                    for (int i = 0; i < cnt; i++)
                    {
                        var q = Store.ZonePosition(frameZoneId, i);
                        minX = Mathf.Min(minX, q.x - hw); maxX = Mathf.Max(maxX, q.x + hw);
                        minZ = Mathf.Min(minZ, q.z - hh); maxZ = Mathf.Max(maxZ, q.z + hh);
                    }
                    orthoScale = framePadding > 0f ? framePadding : 2.2f;
                }
            }

            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            float halfW2 = Mathf.Max(0.5f, (maxX - minX) * 0.5f * orthoScale);
            float halfH2 = Mathf.Max(0.5f, (maxZ - minZ) * 0.5f * orthoScale);

            float pitchRad = pitch * Mathf.Deg2Rad;
            float sinP = Mathf.Max(0.15f, Mathf.Sin(pitchRad));
            float aspect = cameraAspectOverride > 0f
                ? cameraAspectOverride
                : (stage?.board != null && stage.board.aspect > 0f
                    ? stage.board.aspect
                    : Mathf.Max(0.5f, (float)Screen.width / Mathf.Max(1, Screen.height)));

            float orthoSize = Mathf.Max(halfH2 * sinP, halfW2 / aspect);
            orthoSize = Mathf.Max(orthoSize, 0.6f);

            // 横向上界兜底：相机实际 aspect 若比取景时窄，内容会从左右溢出。
            float safeAspect = Mathf.Max(0.5f, aspect);
            if (orthoSize * safeAspect < halfW2) orthoSize = halfW2 / safeAspect;

            float distance = orthoSize * 3.2f;
            var focus = new Vector3(cx, 0f, cz);
            var eye = focus + new Vector3(0f, Mathf.Sin(pitchRad), -Mathf.Cos(pitchRad)) * distance;
            // **入口链重放（stateOnly）时相机还没建**：那时我们只关心状态，取景只要记下来
            // （`frameZoneId` 已经在 SetFraming 里设好了），等 LoadCue 建出相机后再摆一次
            // —— 它在重放之后一定会调一次 FitCamera（LoadCue 末尾）。
            // 少了这个判空就是 NullReferenceException：表现是"冷启动跳到某条 cue 直接崩"，
            // 而顺序播放不会（相机早就有了），所以这条路径特别容易漏。
            if (animCamera != null)
            {
                animCamera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
                animCamera.orthographicSize = orthoSize;
            }

            CameraOrthoSize = orthoSize;
            CameraGroundHalfWidth = orthoSize * aspect;

            if (logCameraFit)
            {
                Debug.Log($"[CueAnim.FitCamera] bounds x[{minX:0.00},{maxX:0.00}] z[{minZ:0.00},{maxZ:0.00}] " +
                          $"halfW={halfW2:0.00} halfH={halfH2:0.00} orthoSize={orthoSize:0.00} " +
                          $"aspect={aspect:0.00} pitch={pitch:0} eye=({eye.x:0.00},{eye.y:0.00},{eye.z:0.00})");
            }
        }

        /// <summary>调试开关：打印取景计算过程（离线出帧诊断用）。</summary>
        public bool logCameraFit;

        /// <summary>调试：打印扫锚图解析结果。</summary>
        public bool logImages;

        /// <summary>调试：打印 move 的取件过程。</summary>
        public bool logMoves;

        /// <summary>调试：打印补间的起点终点。</summary>
        public bool logTweens;

        /// <summary>
        /// 取景使用的宽高比。&lt;=0 表示用当前屏幕（正常运行）。
        /// 离线出帧必须显式指定，否则 batchmode 的 4:3 GameView 会和 16:9 渲染目标不一致，
        /// 导致画面被裁掉右边和下边。
        /// </summary>
        public float cameraAspectOverride;

        /// <summary>调试：各区域底板当前是否可见。</summary>
        public List<KeyValuePair<string, bool>> PanelStates()
        {
            var list = new List<KeyValuePair<string, bool>>();
            foreach (var kv in actors)
                if (kv.Value?.Renderer != null)
                    list.Add(new KeyValuePair<string, bool>(kv.Key, kv.Value.Renderer.enabled));
            return list;
        }

        /// <summary>
        /// <summary>调试：打印某个 zone 中心附近所有可见对象（含贴图与层序），用于定位「谁画在了谁上面」。</summary>
        public void ProbeZoneOccupants(string zoneId)
        {
            var center = Store.ZoneCenter(zoneId);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ProbeZone] zone={zoneId} center=({center.x:0.00},{center.z:0.00}) 附近可见对象：");
            foreach (var item in Store.Items)
            {
                if (item.Actor?.Renderer == null) continue;
                var p = item.Actor.Go.transform.localPosition;
                float d = Mathf.Sqrt((p.x - center.x) * (p.x - center.x) + (p.z - center.z) * (p.z - center.z));
                if (d > 0.6f) continue;
                var sprite = item.Actor.Renderer.sprite;
                sb.AppendLine($"  {item.Id,-26} zone={item.ZoneId,-18} order={item.Order,3} " +
                              $"pos=({p.x:0.00},{p.z:0.00}) " +
                              $"sprite={(sprite == null ? "NULL" : sprite.rect.width + "x" + sprite.rect.height)} " +
                              $"tex={(sprite?.texture == null ? "NULL" : sprite.texture.width + "x" + sprite.texture.height)} " +
                              $"sort={item.Actor.Renderer.sortingOrder} enabled={item.Actor.Renderer.enabled}");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>调试：渲染前检查卡牌贴图内容并导出，用于确认渲染采样到的是哪张图。</summary>
        public void ProbeCardTexture(string outDir)
        {
            foreach (var item in Store.Items)
            {
                if (item.Actor?.Renderer?.sprite == null) continue;
                if (!item.Id.StartsWith("card_back_1#")) continue;

                var sp = item.Actor.Renderer.sprite;
                var tex = sp.texture;
                if (tex == null) { Debug.Log("[ProbeCard] sprite.texture == null"); return; }

                var px = tex.GetPixels();
                float r = 0, g = 0, b = 0, a = 0;
                foreach (var c in px) { r += c.r; g += c.g; b += c.b; a += c.a; }
                int n = px.Length;
                Debug.Log($"[ProbeCard] {item.Id} sprite={sp.rect.width}x{sp.rect.height} tex={tex.width}x{tex.height} " +
                          $"fmt={tex.format} readable={tex.isReadable} " +
                          $"avgRGB=({r / n:0.00},{g / n:0.00},{b / n:0.00}) A={a / n:0.00}");

                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllBytes(Path.Combine(outDir, "probe_card_runtime.png"), tex.EncodeToPNG());
                }
                return;
            }
            Debug.Log("[ProbeCard] 没找到 card_back_1 的 actor");
        }
        /// <summary>调试：把卡牌染成红色，用于判断白色来自贴图还是另有覆盖。</summary>
        public void TintCardRed()
        {
            foreach (var item in Store.Items)
            {
                if (item.Actor?.Renderer == null) continue;
                if (!item.Id.StartsWith("card_back_1#")) continue;
                item.Actor.Renderer.color = Color.red;
            }
            Debug.Log("[TintCardRed] 已把卡牌染红");
        }
        /// 调试：为所有组件重新解析并重新赋值贴图。
        /// 用来区分「贴图/上传有问题」和「别处覆盖了 sprite」——替换后画面变化说明是前者。
        /// </summary>
        public void ForceReloadSprites()
        {
            foreach (var item in Store.Items)
            {
                if (item.Actor?.Renderer == null) continue;
                var tpl = EffectiveTemplate(item.Template, item.PaletteName);
                var fresh = ResolveSprite(tpl);
                if (fresh != null) item.Actor.Renderer.sprite = fresh;
                item.Actor.FaceSprite = fresh;
            }
            Debug.Log("[ForceReloadSprites] 已重新赋值");
        }

        /// <summary>调试：把画面里每个 sprite 的真实渲染状态打出来。</summary>
        public void DumpRenderState()
        {
            if (animRoot == null) return;
            var renderers = animRoot.GetComponentsInChildren<SpriteRenderer>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Render] 共 {renderers.Length} 个 SpriteRenderer");
            int shown = 0;
            foreach (var sr in renderers)
            {
                if (!sr.enabled) continue;
                if (shown++ > 6) break;
                var tex = sr.sprite != null ? sr.sprite.texture : null;
                sb.AppendLine($"[Render] {sr.gameObject.name} sprite={(sr.sprite == null ? "NULL" : sr.sprite.rect.width + "x" + sr.sprite.rect.height)} " +
                              $"tex={(tex == null ? "NULL" : tex.width + "x" + tex.height)} color=({sr.color.r:0.00},{sr.color.g:0.00},{sr.color.b:0.00},{sr.color.a:0.00}) " +
                              $"scale=({sr.transform.localScale.x:0.000},{sr.transform.localScale.y:0.000}) order={sr.sortingOrder} " +
                              $"mat={(sr.sharedMaterial == null ? "NULL" : sr.sharedMaterial.name)}");
            }
            Debug.Log(sb.ToString());
        }

        public bool WorldToScreen(Vector3 world, out Vector3 screen)
        {
            screen = Vector3.zero;
            if (animCamera == null) return false;
            screen = animCamera.WorldToScreenPoint(world);
            screen.y = Screen.height - screen.y;
            return screen.z > 0f;
        }
    }

    /// <summary>cue 入口状态的轻量快照，用于重播时恢复（不重建对象）。</summary>
    public class ZoneSnapshot
    {
        private struct State
        {
            public string ZoneId;
            public int Order;
        }

        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public static ZoneSnapshot Capture(ZoneStore store)
        {
            var snapshot = new ZoneSnapshot();
            foreach (var item in store.Items)
            {
                snapshot.states[item.Id] = new State
                {
                    ZoneId = item.ZoneId,
                    Order = item.Order,
                };
            }
            return snapshot;
        }

        public bool TryGet(string id, out string zoneId, out int order)
        {
            zoneId = null;
            order = -1;
            if (!states.TryGetValue(id, out var state)) return false;
            zoneId = state.ZoneId;
            order = state.Order;
            return true;
        }
    }
}
