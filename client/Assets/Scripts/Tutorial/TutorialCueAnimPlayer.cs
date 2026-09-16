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
                if (ev.action != "move" || ev.target != targetId) continue;
                var actor = FindActor(ev.target);
                if (actor?.Item == null) continue;
                int ord = ev.order == -2 ? ev.slot : ev.order;
                var from = actor.LivePosition;
                var to = ev.zone != null ? Store.ZonePosition(ev.zone, ord) : Store.CurrentPosition(actor.Item);
                return new MovePlan
                {
                    Item = actor.Item, Destination = ev.zone, Order = ord, InPlace = false,
                    From = from, To = to, Flip = ev.flip,
                };
            }
            return null;
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

            string path = Path.Combine(gameRoot, "tutorial", "anim", track, cueId + ".json");
            if (!File.Exists(path))
            {
                // 这条 cue 还没有动画数据。
                // 关键：即使没有动画，也要把**牌桌**搭出来并保留住 —— 否则开场那几条
                // （背景介绍等）会让画面完全空白，看起来像整个模块坏了。
                // 注意 Store 是同一个实例，所以后续 cue 会接着这张桌子继续。
                Debug.Log($"[TutorialCueAnim] 本条 cue 无动画数据，只保留牌桌: {cueId}");
                CueId = cueId;
                clock = -1f;
                nextIndex = 0;

                // 判据是「画面上有没有对象」，不是「store 里有没有数据」。
                // 这两者会不一致：store 有 12 张、animRoot 却被清空过。
                bool hasScene = animRoot != null && animRoot.transform.childCount > 0;
                if (!hasScene)
                {
                    // 牌桌还没搭过：先载入 stage（否则模板为空，什么都生成不出来），
                    // 再按 initial 摆好、建对象、取景。
                    ClearActors();
                    LoadStage(gameRoot, null);
                    Store.Reset();
                    Store.ApplyInitial();
                    BuildActorObjects();
                    SyncActorsToStore();
                    EnsureCamera();
                    SetBackground();
                    FitCamera();
                }
                return false;
            }

            ClearActors();   // 确定要重建画面了，才销毁旧对象
            cueDoc = JsonUtility.FromJson<CueAnimDoc>(File.ReadAllText(path));
            if (cueDoc == null || cueDoc.events == null)
            {
                Debug.LogError($"[TutorialCueAnim] 解析失败: {path}");
                cueDoc = null;
                return false;
            }
            Note = cueDoc.note;

            LoadStage(gameRoot, cueDoc.stage);

            // 续接（顺序播放）：接着上一条的终态。
            // 不续接（跳转 / 重播 / 按 B 预览）：退回牌桌初始态，再按本条 cue 的 start 布置。
            // 必须 Reset+ApplyInitial，否则跳到后面的 cue 会带着上一轮留下的组件。
            if (!continueState)
            {
                Store.Reset();
                Store.ApplyInitial();
            }
            ApplyCueStart();

            // 市场牌在发牌前停在对应牌堆的位置当「牌背」；它们必须显示背面，
            // 否则会把整摞牌堆的卡背盖住，看起来像牌堆正面朝上。
            foreach (var item in Store.Items)
                if (item.Id.StartsWith("market_card_")) item.Flipped = true;

            BuildActorObjects();
            SyncActorsToStore();

            EnsureCamera();
            SetBackground();
            FitCamera();

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
                    int want = seed.expand_to > 0 ? seed.expand_to : Mathf.Max(1, seed.count);
                    int have = Store.CountIn(seed.zone, seed.palette, seed.template);
                    int need = Mathf.Max(0, want - have);
                    if (need == 0) continue;

                    // 必须「先搬后生」：stage.initial 常把这一份放在 offstage（表示还在盒子里），
                    // 直接 Spawn 会多造一份，出现在画面外的重复件。
                    // 每一次都重新数一遍现有数量，因此重播/重复载入也不会翻倍。
                    int guard = 0;
                    while (need > 0 && guard++ < 64)
                    {
                        int moved = Store.PullFrom(OffstageZoneId, seed.template, seed.palette, seed.zone, need);
                        need -= moved;
                        if (moved == 0) break;
                    }
                    if (need > 0) Store.Spawn(seed.template, seed.palette, seed.zone, need);
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
                var itemTpl = EffectiveTemplate(item.Template, item.PaletteName);
                var go = CreateSpriteObject("item:" + item.Id, itemTpl, item.BaseColor);
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
            }
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
            };
            return clone;
        }

        private GameObject CreateSpriteObject(string name, StageTemplate tpl, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(animRoot.transform, false);

            var sr = go.AddComponent<SpriteRenderer>();
            var sprite = ResolveSprite(tpl);
            sr.sprite = sprite;
            sr.sortingOrder = tpl.sorting_order;

            // 「动画前不可见」的组件（例如在牌堆上待发的市场牌）必须**建出来就是透明的**。
            // 曾经这里一律用模板 alpha（默认 1），靠之后采样才置 0 —— 于是
            // LoadCue 到第一次 Seek 之间有一段空档，十几张待发牌会以不透明状态
            // 叠在牌堆上闪一下（用户看到「牌堆闪了一下」）。
            bool hidden = tpl.hide_until_animated;
            color.a = hidden ? 0f : Mathf.Clamp01(tpl.alpha);
            sr.color = color;
            if (hidden) sr.enabled = false;   // 双保险：连绘制都不参与

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
            if (!string.IsNullOrEmpty(tpl.face_image)) yield return tpl.face_image;

            // 按色板名找实物图（宝石六色、黄金）
            if (!string.IsNullOrEmpty(tpl.palette) && PaletteImages.TryGetValue(tpl.palette, out var image))
            {
                yield return "media/card/" + image;
                yield return "media/card/" + Path.GetFileNameWithoutExtension(image) + ".png";
            }

            // 贵族板块
            if (tpl.palette == "noble")
            {
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


        /// <summary>按「是否已翻开」刷新贴图。有背面贴图且未翻开时显示背面。</summary>
        private static void RefreshFace(CueAnimActor actor)
        {
            if (actor?.Renderer == null || actor.BackSprite == null) return;
            actor.Renderer.sprite = actor.Item != null && actor.Item.Flipped
                ? actor.BackSprite
                : actor.FaceSprite;
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
                item.Actor.LiveColor = item.BaseColor;
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, item.Template.rotation);
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
        /// <summary>
        /// 采纳另一个播放器（重建实例）算出的牌桌状态：把归属/格位搬过来并按新状态摆好。
        /// 用于「重建用独立实例、正式播放用主实例」，两者状态对接。
        /// </summary>
        public void AdoptStateFrom(TutorialCueAnimPlayer other, string gameRoot)
        {
            if (other?.Store == null) return;

            // 必须先把**组件本身**登记进来：主播放器的 store 可能是空的，
            // 只复制 zone/order 是没有对象的。曾经漏了这一步，于是主播放器是一张空桌子，
            // 牌堆不存在、发牌只能从孤立位置搬来搬去（表现为方向反过来）。
            Store.AdoptItemsFrom(other.Store);

            // 再对齐归属、格位、正反面
            foreach (var src in other.Store.Items)
            {
                if (!Store.TryGetItem(src.Id, out var dst)) continue;
                dst.ZoneId = src.ZoneId;
                dst.Order = src.Order;
                dst.Flipped = src.Flipped;
                dst.EntryAnchor = src.EntryAnchor;
                dst.EntryFrom = src.EntryFrom;
                Store.SetActiveItem(dst);
            }

            // 按新状态重建画面对象。
            // 必须先销毁旧对象：BuildActorObjects 只新建、不替换。
            // 漏了这一步时每次跳转都在旧对象之上再叠一整套，半透明底板会越叠越浓
            // （用户反复按左右时看到「框越来越明显」）。
            ClearActors();
            LoadStage(gameRoot, null);
            BuildActorObjects();
            SyncActorsToStore();
        }

        /// <summary>
        /// 只把牌桌摆成 stage.initial 的样子（不载入任何 cue 的动画）。
        /// 用于「入口状态 = 牌桌初始态」和自检。
        /// </summary>
        public bool LoadInitialOnly(string gameRoot)
        {
            StopAnimations();
            ClearActors();
            clips.Clear();
            cueDoc = null;
            CueId = null;
            clock = -1f;
            nextIndex = 0;

            LoadStage(gameRoot, null);
            Store.Reset();
            Store.ApplyInitial();
            BuildActorObjects();
            SyncActorsToStore();
            EnsureCamera();
            SetBackground();
            FitCamera();
            return true;
        }

        /// <summary>自检/出图用：确保场景里有可用的相机。</summary>
        public void EnsureCameraForCapture() => EnsureCamera();

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
                    actor.LiveAlpha = (item.Template != null && item.Template.hide_until_animated) ? 0f : actor.BaseAlpha;
                    ApplyAlpha(actor);
                    continue;
                }
                actor.LivePosition = Store.CurrentPosition(item);
                actor.Go.transform.localPosition = actor.LivePosition;

                actor.LiveRotation = item.Template != null ? item.Template.rotation : 0f;
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, actor.LiveRotation);
                actor.LiveScale = actor.BaseScale;
                actor.Go.transform.localScale = actor.LiveScale;
                // 默认状态：要求「动画前不可见」的组件一律透明。
                // 这是**默认**而非补丁——之前只在「有片段但未开始」时隐藏，
                // 漏掉了「还没有任何片段」的那些（二级/三级市场牌），
                // 它们就按默认透明度显示、叠在各自牌堆上，看起来像往牌堆里发卡背。
                bool hideNow = item.Template != null && item.Template.hide_until_animated;
                actor.LiveAlpha = hideNow ? 0f : actor.BaseAlpha;
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
                    float yaw = Mathf.LerpUnclamped(clip.FlipFromYaw, clip.FlipFromYaw + 180f, k);
                    clip.Actor.Go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                    bool showingBack = Mathf.Cos(yaw * Mathf.Deg2Rad) < 0f;
                    if (clip.Actor.BackSprite != null)
                        clip.Actor.Renderer.sprite = showingBack ? clip.Actor.BackSprite : clip.Actor.FaceSprite;
                }

                if (clip.HasScale)
                {
                    clip.Actor.LiveScale = Vector3.LerpUnclamped(clip.ScaleFrom, clip.ScaleTo, k);
                    clip.Actor.Go.transform.localScale = clip.Actor.LiveScale;
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
                    clip.Actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
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
                    float envelope = Mathf.Sin(Mathf.Clamp01(k) * Mathf.PI);
                    envelope = Mathf.Pow(envelope, 0.45f);
                    float w = elapsed * clip.ShuffleFreq * Mathf.PI * 2f + clip.ShufflePhase;
                    float dx = Mathf.Sin(w) * clip.ShuffleAmp * envelope;
                    float dz = Mathf.Sin(w * 0.73f + 1.1f) * clip.ShuffleZ * envelope;
                    clip.Actor.Go.transform.localPosition =
                        clip.ShuffleFrom + new Vector3(dx, 0f, dz);
                }
            }
        }

        /// <summary>把这条 cue 直接推到结束（顺序播放进入下一条之前用）。</summary>
        public void Complete()
        {
            if (cueDoc?.events == null) return;
            while (nextIndex < cueDoc.events.Count)
            {
                var ev = cueDoc.events[nextIndex];
                nextIndex++;
                TriggerFinal(ev);
            }
            clock = Mathf.Max(clock, TotalDuration);
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
                item.Actor.LiveColor = item.BaseColor;
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localScale = item.Actor.BaseScale;
                item.Actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, item.Template.rotation);
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
            if (animRoot != null)
            {
                Object.DestroyImmediate(animRoot);
                animRoot = null;
            }
        }

        // ── 原语 ──────────────────────────────────────────────────────────

        private void Trigger(CueAnimEvent ev)
        {
            if (ev == null) return;
            // 记下这条事件在时间轴上的位置：片段起点要用**事件时间**，
            // 而不是「触发到它的那一刻」——后者随帧率/seek 粒度变化，
            // 会让动画起点漂移、按 elapsed 计算的效果（如洗混）不一致。
            currentEventAt = ev.at;

            switch (ev.action)
            {
                case "wait": return;
                case "move": TriggerMove(ev); return;
                case "rotate":
                case "flip": TriggerRotate(ev); return;
                case "scale": TriggerScale(ev); return;
                case "fade": TriggerFade(ev); return;
                case "highlight": TriggerHighlight(ev); return;
                case "shuffle": TriggerShuffle(ev); return;
                default:
                    Debug.LogWarning($"[TutorialCueAnim] unknown action '{ev.action}' in cue {CueId}");
                    return;
            }
        }

        /// <summary>把事件推到终态且不播协程，用于顺序播放时推进状态。</summary>
        private void TriggerFinal(CueAnimEvent ev)
        {
            if (ev == null || ev.action == "wait") return;

            if (ev.action == "move")
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
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, actor.LiveRotation);
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
            public bool Flip;      // 是否同时翻面
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

            public bool HasScale;
            public Vector3 ScaleFrom, ScaleTo;

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
                        Destination = ev.zone,
                        Order = ord,
                        InPlace = false,
                    });
                }
                return plan;
            }

            if (string.IsNullOrEmpty(ev.zone) || Store.GetZone(ev.zone) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no resolvable destination ('{ev.zone}')");
                return plan;
            }

            var sources = ev.from;
            if (sources == null || sources.Count == 0)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no from zone");
                return plan;
            }

            int take = ev.take > 0 ? ev.take : 1;
            var picked = new List<ZoneItem>();

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
                    var item = PickFront(source, picked, ev.template);
                    if (item == null) break;
                    picked.Add(item);
                    plan.Add(new MovePlan { Item = item, Destination = ev.zone, Order = ev.order });
                }
            }


            return plan;
        }

        /// <summary>取 zone 里最靠前、且不在 excluded 中的组件。</summary>
        private ZoneItem PickFront(string zoneId, List<ZoneItem> excluded, string template = null)
        {
            ZoneItem best = null;
            foreach (var item in Store.Items)
            {
                if (item.ZoneId != zoneId) continue;
                if (excluded != null && excluded.Contains(item)) continue;
                if (!string.IsNullOrEmpty(template) && item.Template?.id != template) continue;
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

                // order=-2 的语义是「从别处出场、落在目标 zone 的第 Order 格」——
                // 逻辑归属也必须落到那一格。之前只动了画面、不记格位，导致：
                //   ① 后续事件按旧归属算位置时，把这些牌拉回起点（用户看到的「被收回去」）
                //   ② 新发的牌按当前格位算，落到了别的行
                // 现在统一按正常落位处理，动画起点仍由 from_zone 提供，所以看起来依旧「从牌堆飞出」。
                // 统一走 MoveToSlot(item, zone, slot)：坐标由格位表给出，
                // 「落到第几格」不再散落成算术。slot < 0 表示追加到末尾。
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
                if (actor == null) continue;

                step.From = from;
                step.To = to;
                step.Flip = ev.flip;

                if (logTweens)
                    Debug.Log($"[Tween] {step.Item.Id} inPlace={step.InPlace} order={step.Order} " +
                              $"from=({from.x:0.00},{from.z:0.00}) to=({to.x:0.00},{to.z:0.00}) dur={ev.dur}");

                var clip = ClipAt(step.Item, actor, ev);
                clip.HasMove = true;
                clip.From = from;
                clip.To = to;

                // 淡入：发牌前市场牌是隐藏的（避免它们叠在牌堆上，看起来像多出几层卡背）
                if (ev.fade_in >= 0f)
                {
                    clip.HasAlpha = true;
                    clip.AlphaFrom = Mathf.Clamp01(ev.fade_in);
                    clip.AlphaTo = 1f;
                }

                // 边移动边翻转：到终点恰好转到另一面。
                if (ev.flip && actor.BackSprite != null)
                {
                    clip.HasFlip = true;
                    clip.FlipFromYaw = step.Item.Flipped ? 180f : 0f;
                    step.Item.Flipped = !step.Item.Flipped;   // 逻辑状态立即到终态
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

        private void TriggerFade(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                float to = ev.to_alpha >= 0f
                    ? Mathf.Clamp01(ev.to_alpha)
                    : (actor.LiveAlpha > 0.5f ? 0f : 1f);
                float from = actor.LiveAlpha;
                actor.LiveAlpha = to;
                RunTween(TweenAlpha(actor, from, to, ev));
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
        /// 洗混：牌堆里每张牌原地左右高频颤抖。
        ///
        /// 关键是**每张牌都不一样**：用牌号做种子决定幅度、频率、初相和纵向分量，
        /// 同一瞬间有的向左有的向右、抖得也不一样快。若所有牌同相摆动，
        /// 看起来只是整摞在晃，不像洗牌。
        /// 颤抖幅度很小（毫米级），所以叠在一起的牌只会让牌堆边缘发毛 —— 正是要的效果。
        /// </summary>
        private void TriggerShuffle(CueAnimEvent ev)
        {
            int i = 0;
            int count = 0;
            foreach (var actor in Resolve(ev)) count++;

            foreach (var actor in Resolve(ev))
            {
                if (actor?.Go == null || actor.Item == null) continue;

                // 由牌号散列出的确定性伪随机（不用 Random，保证每次播放一致）
                int h = StableHash(actor.Item.Id) + i * 7919;
                float r1 = Hash01(h, 1);
                float r2 = Hash01(h, 2);
                float r3 = Hash01(h, 3);
                float r4 = Hash01(h, 4);

                var clip = ClipAt(actor.Item, actor, ev);
                clip.HasShuffle = true;
                clip.ShuffleFrom = actor.LivePosition;

                // 幅度 2.0~3.6mm（0.020~0.036 世界单位）：够看出「发毛」，又不会散开
                clip.ShuffleAmp = Mathf.Lerp(0.040f, 0.062f, r1);
                // 频率 8~14Hz：人手搓牌的频率区间
                clip.ShuffleFreq = Mathf.Lerp(8f, 14f, r2);
                clip.ShufflePhase = r3 * Mathf.PI * 2f;   // 初相不同 → 瞬时方向不同
                clip.ShuffleZ = clip.ShuffleAmp * Mathf.Lerp(0.25f, 0.6f, r4);
                clip.ShuffleDecay = 2.6f;                  // 约 1.5s 内收住
                i++;
            }
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

        // ── 协程 ──────────────────────────────────────────────────────────

        private IEnumerator TweenPosition(CueAnimActor actor, ZoneItem item, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            yield return TutorialPrimitives.TweenPosition(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LivePosition = to;
            item.LivePosition = to;
        }

        /// <summary>
        /// 边移动边翻转：与位移并行，到终点恰好转到另一面。
        ///
        /// 轴向取 actor 的**本地 Y 轴**——actor 已绕 X 转 90° 平躺，本地 Y 正是屏幕竖直方向，
        /// 绕它转 180° 就是牌面水平翻过去（途中 90° 时收成一条线）。
        /// 用 Mathf.Cos 判断当前朝哪边，决定显示正面还是背面贴图。
        /// </summary>
        private IEnumerator TweenFlip(CueAnimActor actor, ZoneItem item, CueAnimEvent ev)
        {
            if (actor?.Go == null || actor.BackSprite == null) yield break;
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);

            float dur = Mathf.Max(ev.dur, 0.01f);
            float fromYaw = item.Flipped ? 180f : 0f;
            float toYaw = fromYaw + 180f;

            float t = 0f;
            while (t < dur)
            {
                t = Mathf.Min(t + TutorialPrimitives.Delta, dur);
                float k = Easing.Evaluate(EasingOr(ev), t / dur);
                float yaw = Mathf.LerpUnclamped(fromYaw, toYaw, k);
                actor.Go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

                // 以 90°/270° 为界切贴图：前半看到原面，后半看到另一面。
                bool showingBack = Mathf.Cos(yaw * Mathf.Deg2Rad) < 0f;
                actor.Renderer.sprite = showingBack ? actor.BackSprite : actor.FaceSprite;
                yield return null;
            }

            actor.Go.transform.localRotation = Quaternion.identity;
            item.Flipped = !item.Flipped;
            actor.Renderer.sprite = item.Flipped ? actor.BackSprite : actor.FaceSprite;
        }

        private IEnumerator TweenRotation(CueAnimActor actor, float from, float to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            float dur = Mathf.Max(ev.dur, 0f);
            if (dur <= 0f)
            {
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, to);
            }
            else
            {
                float t = 0f;
                while (t < dur)
                {
                    t = Mathf.Min(t + TutorialPrimitives.Delta, dur);
                    float k = Easing.Evaluate(EasingOr(ev), t / dur);
                    actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(from, to, k));
                    yield return null;
                }
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, to);
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

        private List<CueAnimActor> Resolve(CueAnimEvent ev)
        {
            var result = new List<CueAnimActor>();
            if (ev == null) return result;

            if (!string.IsNullOrEmpty(ev.zone))
            {
                foreach (var item in Store.Items)
                    if (item.ZoneId == ev.zone && item.Actor != null) result.Add(item.Actor);
                return result;
            }

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor != null) result.Add(actor);
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

        /// <summary>按牌桌的 zone 范围取景，50° 固定俯角。</summary>
        private void FitCamera()
        {
            float pitch = stage?.board != null && stage.board.camera_pitch > 0f ? stage.board.camera_pitch : 50f;
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
            animCamera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
            animCamera.orthographicSize = orthoSize;

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
